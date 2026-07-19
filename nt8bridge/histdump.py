"""Automated NT8 MarketReplay export: discover un-exported replay dates and dump them
via the marketReplayDump bridge kind. See docs/superpowers/specs/2026-07-02-automated-histdump-design.md."""
from __future__ import annotations

import os
import uuid
from pathlib import Path

from nt8bridge import ntio


def build_dump_request(rid: str, instrument: str, date: str, out_path: str, mode: str) -> dict:
    return {"id": rid, "kind": "marketReplayDump", "instrument": instrument,
            "date": date, "outPath": out_path, "mode": mode}


def dump_one(instrument: str, date: str, out_path: str, mode: str = "depth",
             timeout: float = 300.0) -> dict:
    """Fire one marketReplayDump and return the bridge result dict."""
    rid = uuid.uuid4().hex[:12]
    trigger, result = ntio.ensure_bridge_dirs()
    ntio.atomic_write_json(trigger / f"marketReplayDump_{rid}.json",
                           build_dump_request(rid, instrument, date, out_path, mode))
    return ntio.poll_for_json(result / f"marketReplayDump_{rid}.json", timeout=timeout)


def discover_work(replay_dir, instrument_glob: str, out_dir,
                  force: bool = False) -> list[tuple[str, str]]:
    """(instrument, YYYYMMDD) pairs that have a .nrd but no <out_dir>/<instrument>/<date>.csv."""
    replay_dir, out_dir = Path(replay_dir), Path(out_dir)
    work: list[tuple[str, str]] = []
    for inst_dir in sorted(replay_dir.glob(instrument_glob)):
        if not inst_dir.is_dir():
            continue
        inst = inst_dir.name
        for nrd in sorted(inst_dir.glob("*.nrd")):
            date = nrd.stem
            if force or not (out_dir / inst / f"{date}.csv").exists():
                work.append((inst, date))
    return work


def _byte_equal(a: Path, b: Path) -> bool:
    try:
        return a.read_bytes() == b.read_bytes()
    except OSError:
        return False


def _unlink(p: Path) -> None:
    try:
        p.unlink()
    except OSError:
        pass


def _ok(res: dict) -> bool:
    return res.get("status") == "ok" or res.get("ok") is True


def _validation_gate(replay_dir, instrument_glob: str, out_dir, mode: str, timeout: float) -> dict:
    """Re-export the first date that already has a CSV and byte-diff it. Guarantees equivalence
    before any batch write. If nothing pre-existing to diff, skip with a warning."""
    for inst_dir in sorted(Path(replay_dir).glob(instrument_glob)):
        if not inst_dir.is_dir():
            continue
        inst = inst_dir.name
        for nrd in sorted(inst_dir.glob("*.nrd")):
            date = nrd.stem
            existing = Path(out_dir) / inst / f"{date}.csv"
            if existing.exists():
                tmp = existing.parent / f".gate_{date}.csv.tmp"
                res = dump_one(inst, date, str(tmp), mode, timeout)
                same = _ok(res) and _byte_equal(tmp, existing)
                _unlink(tmp)
                return {"ok": bool(same), "skipped": False, "gate": f"{inst}/{date}",
                        "detail": "byte-identical" if same else "MISMATCH vs existing CSV"}
    return {"ok": True, "skipped": True, "gate": None,
            "detail": "no existing CSV to diff — gate skipped (construction-equivalence only)"}


def run_histdump(*, instrument_glob: str, out_dir, replay_dir=None, mode: str = "depth",
                 force: bool = False, validate_only: bool = False, parquet: bool = False,
                 timeout: float = 300.0) -> dict:
    out_dir = Path(out_dir)
    replay_dir = Path(replay_dir) if replay_dir else (ntio.nt8_root() / "db" / "replay")

    gate = _validation_gate(replay_dir, instrument_glob, out_dir, mode, timeout)
    if not gate["ok"]:
        return {"status": "gate_failed", "gate": gate, "exported": [], "failed": [], "count": 0}
    if validate_only:
        # A skipped gate validated NOTHING — report it distinctly (non-"validated") so a caller
        # scripting on status/rc can't mistake "no ground truth to diff" for a real pass.
        status = "gate_skipped" if gate.get("skipped") else "validated"
        return {"status": status, "gate": gate, "exported": [], "failed": [], "count": 0}

    exported: list[str] = []
    failed: list[dict] = []
    warnings: list[str] = []
    if gate.get("skipped"):
        warnings.append("equivalence gate SKIPPED — no existing CSV to diff; "
                        "output trusted on construction-equivalence only")
    for inst, date in discover_work(replay_dir, instrument_glob, out_dir, force):
        final = out_dir / inst / f"{date}.csv"
        final.parent.mkdir(parents=True, exist_ok=True)
        tmp = final.parent / f".{date}.csv.tmp"
        try:
            res = dump_one(inst, date, str(tmp), mode, timeout)
        except TimeoutError as e:
            _unlink(tmp)
            failed.append({"file": f"{inst}/{date}", "error": f"timeout: {e}"})
            continue
        rows = res.get("rows")
        wrote = tmp.exists() and tmp.stat().st_size > 0
        # An empty / 0-row dump that didn't throw would otherwise be recorded as a "success" and then
        # skipped forever by discovery — a permanent silent gap. Require real content before committing.
        if _ok(res) and wrote and (rows is None or rows > 0):
            os.replace(tmp, final)   # atomic (temp is a sibling of final -> same volume)
            if parquet:
                try:
                    depth_csv_to_parquet(final, final.with_suffix(".parquet"))
                except Exception as e:   # parquet is a convenience view; never lose the CSV over it
                    warnings.append(f"{inst}/{date}: parquet failed: {e}")
            exported.append(f"{inst}/{date}")
        else:
            _unlink(tmp)
            reason = res.get("error") or res.get("message")
            if not reason:
                reason = "empty output" if not wrote else ("0 rows" if rows == 0 else "dump failed")
            failed.append({"file": f"{inst}/{date}", "error": reason})
    result = {"status": "ok", "gate": gate, "exported": exported, "failed": failed,
              "count": len(exported)}
    if warnings:
        result["warnings"] = warnings
    return result


def depth_csv_to_parquet(csv_path, parquet_path) -> int:
    """Parse the L2 depth rows of a full-depth CSV into a typed table and write parquet, returning
    the L2 row count. The CSV remains the complete source of truth; parquet is an opt-in L2
    convenience view, NOT a lossless mirror: non-conforming lines (L1 trade prints, or rows that
    don't parse) are skipped here. Expected L2 layout (semicolon-delimited):
    [0] rec_type | [1] op | [2] ts YYYYMMDDHHMMSS | [3],[4] two ints (raw) | [5] level | [6] empty | [7] price | [8] size.
    (Typed-datetime ts + L1 capture are follow-ons.)"""
    import pyarrow as pa
    import pyarrow.parquet as pq
    rec_type, op, ts, f3, f4, level, price, size = ([] for _ in range(8))
    with open(csv_path, "r", newline="") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            p = line.split(";")
            if len(p) < 9:
                continue
            try:
                o, a, b, lv, sz = int(p[1]), int(p[3]), int(p[4]), int(p[5]), int(p[8])
                pr = float(p[7])
            except ValueError:
                continue   # malformed / non-L2 row — the CSV still holds it verbatim
            rec_type.append(p[0]); op.append(o); ts.append(p[2])
            f3.append(a); f4.append(b); level.append(lv); price.append(pr); size.append(sz)
    # ts kept as the raw YYYYMMDDHHMMSS string in v1 (no tz assumption); typed-datetime cast is a follow-on.
    tbl = pa.table({"rec_type": rec_type, "op": op, "ts": ts,
                    "f3": f3, "f4": f4, "level": level, "price": price, "size": size})
    pq.write_table(tbl, str(parquet_path))
    return tbl.num_rows
