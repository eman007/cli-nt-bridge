from pathlib import Path

from nt8bridge import histdump


def test_build_dump_request_shape():
    r = histdump.build_dump_request("abc", "MNQ 03-25", "20241201", "C:/x.csv", "depth")
    assert r == {"id": "abc", "kind": "marketReplayDump", "instrument": "MNQ 03-25",
                 "date": "20241201", "outPath": "C:/x.csv", "mode": "depth"}


def test_discover_work_only_missing_dates(tmp_path):
    replay = tmp_path / "replay"
    (replay / "MNQ 03-25").mkdir(parents=True)
    for d in ("20241201", "20241202", "20241203"):
        (replay / "MNQ 03-25" / f"{d}.nrd").write_bytes(b"x")
    out = tmp_path / "out"
    (out / "MNQ 03-25").mkdir(parents=True)
    (out / "MNQ 03-25" / "20241201.csv").write_text("already")   # exported already
    work = histdump.discover_work(replay, "MNQ*", out, force=False)
    assert work == [("MNQ 03-25", "20241202"), ("MNQ 03-25", "20241203")]


def test_discover_work_force_includes_existing(tmp_path):
    replay = tmp_path / "replay"
    (replay / "MNQ 03-25").mkdir(parents=True)
    (replay / "MNQ 03-25" / "20241201.nrd").write_bytes(b"x")
    out = tmp_path / "out"
    (out / "MNQ 03-25").mkdir(parents=True)
    (out / "MNQ 03-25" / "20241201.csv").write_text("already")
    assert histdump.discover_work(replay, "MNQ*", out, force=True) == [("MNQ 03-25", "20241201")]


def _seed(tmp_path, existing_body="L2;1;x\n"):
    replay = tmp_path / "replay"; (replay / "MNQ 03-25").mkdir(parents=True)
    for d in ("20241201", "20241202"):
        (replay / "MNQ 03-25" / f"{d}.nrd").write_bytes(b"x")
    out = tmp_path / "out"; (out / "MNQ 03-25").mkdir(parents=True)
    (out / "MNQ 03-25" / "20241201.csv").write_text(existing_body)   # the gate's ground truth
    return replay, out


def test_run_histdump_gate_pass_and_export(tmp_path, monkeypatch):
    replay, out = _seed(tmp_path)
    # bridge stub: writes the SAME body as the existing gate CSV (byte-identical) to whatever outPath
    def fake_dump(instrument, date, out_path, mode="depth", timeout=300.0):
        Path(out_path).write_text("L2;1;x\n")
        return {"status": "ok", "rows": 1, "outPath": out_path}
    monkeypatch.setattr(histdump, "dump_one", fake_dump)
    res = histdump.run_histdump(instrument_glob="MNQ*", out_dir=out, replay_dir=replay, engine="nt8")
    assert res["status"] == "ok"
    assert res["gate"]["ok"] is True
    assert res["exported"] == ["MNQ 03-25/20241202"]          # only the missing date
    assert (out / "MNQ 03-25" / "20241202.csv").read_text() == "L2;1;x\n"
    assert not list((out / "MNQ 03-25").glob("*.tmp"))         # no temp left behind


def test_run_histdump_gate_fail_aborts(tmp_path, monkeypatch):
    replay, out = _seed(tmp_path, existing_body="L2;1;GROUND-TRUTH\n")
    def fake_dump(instrument, date, out_path, mode="depth", timeout=300.0):
        Path(out_path).write_text("L2;1;DIFFERENT\n")            # mismatch vs existing
        return {"status": "ok", "rows": 1, "outPath": out_path}
    monkeypatch.setattr(histdump, "dump_one", fake_dump)
    res = histdump.run_histdump(instrument_glob="MNQ*", out_dir=out, replay_dir=replay, engine="nt8")
    assert res["status"] == "gate_failed"
    assert res["gate"]["ok"] is False
    assert not (out / "MNQ 03-25" / "20241202.csv").exists()     # nothing written on abort


def test_run_histdump_records_failures(tmp_path, monkeypatch):
    replay, out = _seed(tmp_path)
    def fake_dump(instrument, date, out_path, mode="depth", timeout=300.0):
        if date == "20241201":                                   # gate date: match
            Path(out_path).write_text("L2;1;x\n"); return {"status": "ok", "rows": 1}
        return {"status": "error", "error": "no replay data"}    # the export date: fail
    monkeypatch.setattr(histdump, "dump_one", fake_dump)
    res = histdump.run_histdump(instrument_glob="MNQ*", out_dir=out, replay_dir=replay, engine="nt8")
    assert res["exported"] == []
    assert res["failed"] == [{"file": "MNQ 03-25/20241202", "error": "no replay data"}]


def test_depth_csv_to_parquet(tmp_path):
    import pyarrow.parquet as pq
    csv = tmp_path / "d.csv"
    # observed layout: recType;op;ts;f3;f4;level;;price;size
    csv.write_text("L2;1;20241201110023;7400000;0;0;;21220;3\n"
                   "L2;0;20241201110023;7400000;0;1;;21279.25;1\n")
    n = histdump.depth_csv_to_parquet(csv, tmp_path / "d.parquet")
    assert n == 2
    t = pq.read_table(tmp_path / "d.parquet")
    assert t.num_rows == 2
    cols = set(t.column_names)
    assert {"rec_type", "op", "ts", "level", "price", "size"} <= cols
    d = t.to_pydict()
    assert d["price"][0] == 21220.0 and d["size"][1] == 1 and d["level"][1] == 1


def test_run_histdump_gate_skipped_reports_distinctly(tmp_path, monkeypatch):
    # fresh instrument: a .nrd but NO existing CSV -> the gate cannot diff -> skipped
    replay = tmp_path / "replay"; (replay / "GC 02-26").mkdir(parents=True)
    (replay / "GC 02-26" / "20260101.nrd").write_bytes(b"x")
    out = tmp_path / "out"

    def fake_dump(instrument, date, out_path, mode="depth", timeout=300.0):
        Path(out_path).parent.mkdir(parents=True, exist_ok=True)
        Path(out_path).write_text("L2;1;x\n")
        return {"status": "ok", "rows": 1}
    monkeypatch.setattr(histdump, "dump_one", fake_dump)

    # --validate-only on a fresh dir must NOT claim "validated" (it validated nothing)
    v = histdump.run_histdump(instrument_glob="GC*", out_dir=out, replay_dir=replay, validate_only=True, engine="nt8")
    assert v["status"] == "gate_skipped" and v["gate"]["skipped"] is True
    # a real run proceeds but flags the skip loudly
    r = histdump.run_histdump(instrument_glob="GC*", out_dir=out, replay_dir=replay, engine="nt8")
    assert r["status"] == "ok" and r["exported"] == ["GC 02-26/20260101"]
    assert any("gate SKIPPED" in w for w in r.get("warnings", []))


def test_run_histdump_empty_dump_is_failure_not_silent_gap(tmp_path, monkeypatch):
    replay, out = _seed(tmp_path)

    def fake_dump(instrument, date, out_path, mode="depth", timeout=300.0):
        if date == "20241201":                                    # gate: byte-match the existing CSV
            Path(out_path).write_text("L2;1;x\n"); return {"status": "ok", "rows": 1}
        Path(out_path).write_text("")                             # export: empty file, no exception
        return {"status": "ok", "rows": 0}
    monkeypatch.setattr(histdump, "dump_one", fake_dump)
    res = histdump.run_histdump(instrument_glob="MNQ*", out_dir=out, replay_dir=replay, engine="nt8")
    assert res["exported"] == []
    assert res["failed"] == [{"file": "MNQ 03-25/20241202", "error": "empty output"}]
    assert not (out / "MNQ 03-25" / "20241202.csv").exists()      # NOT written -> re-attemptable


def test_run_histdump_timeout_is_recorded_not_fatal(tmp_path, monkeypatch):
    replay, out = _seed(tmp_path)

    def fake_dump(instrument, date, out_path, mode="depth", timeout=300.0):
        if date == "20241201":
            Path(out_path).write_text("L2;1;x\n"); return {"status": "ok", "rows": 1}
        raise TimeoutError("no result within 300s")
    monkeypatch.setattr(histdump, "dump_one", fake_dump)
    res = histdump.run_histdump(instrument_glob="MNQ*", out_dir=out, replay_dir=replay, engine="nt8")
    assert res["status"] == "ok" and res["exported"] == []       # batch survives one date's timeout
    assert res["failed"][0]["file"] == "MNQ 03-25/20241202"
    assert "timeout" in res["failed"][0]["error"]


def test_depth_csv_to_parquet_skips_malformed(tmp_path):
    import pyarrow.parquet as pq
    csv = tmp_path / "d.csv"
    csv.write_text("L2;1;20241201110023;7400000;0;0;;21220;3\n"
                   "L1;1;20241201110023\n"                         # short (non-L2) -> skipped, not crash
                   "L2;9;20241201110024;7400000;0;1;;notanum;2\n"  # bad price -> skipped, not crash
                   "L2;0;20241201110025;7400000;0;2;;21230;5\n")
    n = histdump.depth_csv_to_parquet(csv, tmp_path / "d.parquet")
    assert n == 2                                                 # only the 2 valid L2 rows
    d = pq.read_table(tmp_path / "d.parquet").to_pydict()
    assert d["price"] == [21220.0, 21230.0] and d["size"] == [3, 5]
