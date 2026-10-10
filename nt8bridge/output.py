"""Read NT8's Output 1 / Output 2 (Print) tabs.

The AddOn tees `NinjaTrader.Code.Output.OutputEvent` to per-tab JSONL under
`<NT8>/NT8Bridge/output/`, one record per line:

    {"t":"2026-08-10T04:52:10.123Z","tab":1,"msg":"MyStrategy: gate=OPEN atr=14.25"}
    {"t":"2026-08-10T04:52:11.004Z","tab":1,"reset":true}       <- Output window cleared
    {"t":"2026-08-10T04:52:11.500Z","tab":1,"dropped":417}      <- queue overflow, N lost

JSONL rather than plain text because a single `Print()` may contain newlines; one record
per line survives that. This module never talks to NT8 -- the logs are plain files in the
bridge dir and `ntio.nt8_root()` honours NT8_DIR, so a remote box is just a path.

ALL TIMES ARE UTC. This box is PST and NT8's own data is ET, so every timestamp this
module emits carries an explicit `Z` and every timestamp it parses is read as UTC.
"""
from __future__ import annotations

import json
import re
import time
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path

from nt8bridge import ntio

TABS = (1, 2)
MARKER_NAME = "tee.on"
_REL = re.compile(r"^(\d+)([smh])$")
_UNIT = {"s": "seconds", "m": "minutes", "h": "hours"}


# ---- paths ----

def output_dir() -> Path:
    return ntio.bridge_dir() / "output"


def tab_path(tab: int, generation: int = 0) -> Path:
    """Live file for `tab` (generation 0), or a rotated generation (1 = previous)."""
    if tab not in TABS:
        raise ValueError(f"tab must be 1 or 2 (NT8 has two Output tabs), got {tab!r}")
    stem = f"tab{tab}" if generation == 0 else f"tab{tab}.{generation}"
    return output_dir() / f"{stem}.jsonl"


def marker_path() -> Path:
    """Presence of this file is what enables the tee. See enable()."""
    return output_dir() / MARKER_NAME


# ---- enable / disable (opt-in; the AddOn re-checks the marker on its poll tick) ----

def is_enabled() -> bool:
    return marker_path().exists()


def enable() -> Path:
    """Turn the tee on. Takes effect within one AddOn poll tick -- no NT8 reload."""
    output_dir().mkdir(parents=True, exist_ok=True)
    marker = marker_path()
    marker.write_text("on\n", encoding="utf-8")
    return marker


def disable() -> bool:
    """Turn the tee off. Returns whether it had been on. Existing logs are left alone."""
    marker = marker_path()
    if not marker.exists():
        return False
    marker.unlink()
    return True


# ---- --since parsing ----

def parse_since(spec: str, now: datetime | None = None) -> datetime:
    """Parse a --since value into an aware UTC datetime.

    Accepts a full ISO timestamp, a bare HH:MM (today, UTC -- NOT local: see module
    docstring), or a relative 30s / 10m / 2h.
    """
    now = now or datetime.now(timezone.utc)
    spec = (spec or "").strip()
    if not spec:
        raise ValueError("--since is empty")

    rel = _REL.match(spec)
    if rel:
        return now - timedelta(**{_UNIT[rel.group(2)]: int(rel.group(1))})

    if re.fullmatch(r"\d{1,2}:\d{2}(:\d{2})?", spec):
        parts = [int(p) for p in spec.split(":")]
        parts += [0] * (3 - len(parts))
        return now.replace(hour=parts[0], minute=parts[1], second=parts[2], microsecond=0)

    parsed = _parse_ts(spec)
    if parsed is None:
        raise ValueError(
            f"cannot read --since {spec!r}; use an ISO timestamp, HH:MM (UTC), or 30s/10m/2h"
        )
    return parsed


def _parse_ts(value: object) -> datetime | None:
    """ISO-8601 -> aware UTC datetime, or None. Naive input is assumed to be UTC."""
    if not isinstance(value, str) or not value:
        return None
    try:
        dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt.astimezone(timezone.utc)


# ---- reading ----

@dataclass
class ReadStats:
    """What the read saw, so a caller can report gaps instead of implying completeness."""
    missing: bool = False          # no log file at all for this tab
    malformed: int = 0             # lines that did not parse (e.g. a partial tail write)
    dropped: int = 0              # records the AddOn had to drop (queue overflow)
    resets: int = 0               # Output window clears inside the window read
    files: list[Path] = field(default_factory=list)


def _is_marker(rec: dict) -> bool:
    """Gap markers carry no message: a window clear, or a dropped batch."""
    return "msg" not in rec


def read_records(
    tab: int = 1,
    tail: int | None = None,
    since: datetime | None = None,
    grep: str | None = None,
) -> tuple[list[dict], ReadStats]:
    """Read a tab's records oldest-first, reading back across one rotation if needed."""
    stats = ReadStats()
    pattern = re.compile(grep) if grep else None

    records: list[dict] = []
    for generation in (1, 0):                       # rotated generation first: it is older
        path = tab_path(tab, generation)
        if not path.exists():
            continue
        stats.files.append(path)
        records.extend(_load(path, stats))

    if not stats.files:
        stats.missing = True
        return [], stats

    if since is not None:
        # A record whose timestamp will not parse is KEPT -- dropping it would hide data.
        records = [r for r in records if (_parse_ts(r.get("t")) or since) >= since]
    if pattern is not None:
        # Markers always survive a grep: a cleared window or a dropped batch explains
        # missing lines, and hiding them would make a filtered read look complete.
        records = [r for r in records if _is_marker(r) or pattern.search(r.get("msg", ""))]
    if tail is not None:
        records = records[-tail:] if tail > 0 else []

    for rec in records:
        if "dropped" in rec:
            stats.dropped += int(rec.get("dropped") or 0)
        if rec.get("reset"):
            stats.resets += 1
    return records, stats


def _load(path: Path, stats: ReadStats) -> list[dict]:
    out: list[dict] = []
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        stats.malformed += 1
        return out
    for line in text.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            rec = json.loads(line)
        except json.JSONDecodeError:
            stats.malformed += 1          # partial write at the tail, or a torn line
            continue
        if isinstance(rec, dict):
            out.append(rec)
        else:
            stats.malformed += 1
    return out


# ---- rendering ----

def render(records: list[dict]) -> list[str]:
    """One line per record. Times carry an explicit Z so a pasted line is unambiguous."""
    lines = []
    for rec in records:
        stamp = _hhmmss(rec.get("t"))
        if rec.get("reset"):
            lines.append(f"{stamp}  -- Output cleared --")
        elif "dropped" in rec:
            lines.append(f"{stamp}  -- {rec['dropped']} records dropped (queue overflow) --")
        else:
            # A multi-line Print stays ONE record: collapse its newlines to a visible
            # escape so line-oriented tools (and grep) still see one line per record.
            # Collapsed outside the f-string on purpose: a backslash inside an f-string EXPRESSION
            # is a SyntaxError before Python 3.12, and pyproject declares requires-python >=3.10.
            # The whole module failed to import on 3.11, which surfaces as `nt8bridge` looking
            # broken rather than as a version problem, because cli.py imports this at module level.
            msg = str(rec.get("msg", "")).replace("\r\n", "\n").replace("\r", "\n")
            msg = msg.replace("\n", "\\n")
            lines.append(f"{stamp}  {msg}")
    return lines


def _hhmmss(value: object) -> str:
    dt = _parse_ts(value)
    return dt.strftime("%H:%M:%SZ") if dt else "--:--:--Z"


# ---- status ----

def status() -> dict:
    """Tee state plus per-tab size/record/drop counts, read straight off the files."""
    out: dict = {"enabled": is_enabled(), "dir": str(output_dir())}
    for tab in TABS:
        live, rotated = tab_path(tab), tab_path(tab, 1)
        stats = ReadStats()
        records = _load(live, stats) if live.exists() else []
        out[f"tab{tab}"] = {
            "bytes": live.stat().st_size if live.exists() else 0,
            "records": len(records),
            "dropped": sum(int(r.get("dropped") or 0) for r in records if "dropped" in r),
            "malformed": stats.malformed,
            "rotated": rotated.exists(),
            "rotatedAt": _mtime_utc(rotated) if rotated.exists() else None,
        }
    return out


def _mtime_utc(path: Path) -> str:
    return datetime.fromtimestamp(path.stat().st_mtime, timezone.utc).strftime(
        "%Y-%m-%dT%H:%M:%SZ"
    )


# ---- live follow ----

def follow(
    tab: int = 1,
    grep: str | None = None,
    interval: float = 0.5,
    stop: object = None,
):
    """Yield records as they are appended, surviving rotation.

    Rotation renames the live file and starts a new one, so the byte offset we were
    reading from becomes meaningless -- detected as the file shrinking (or vanishing)
    and handled by starting over from its head.
    """
    pattern = re.compile(grep) if grep else None
    path = tab_path(tab)
    offset = path.stat().st_size if path.exists() else 0
    pending = ""

    while stop is None or not stop():
        try:
            size = path.stat().st_size if path.exists() else 0
        except OSError:
            size = 0
        if size < offset:                     # rotated (or truncated) -> restart at head
            offset, pending = 0, ""
        if size > offset:
            with path.open("r", encoding="utf-8", errors="replace") as fh:
                fh.seek(offset)
                chunk = fh.read()
                offset = fh.tell()
            pending += chunk
            *lines, pending = pending.split("\n")   # keep a partial last line for later
            for line in lines:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(rec, dict):
                    continue
                if pattern is not None and not _is_marker(rec) and not pattern.search(
                    rec.get("msg", "")
                ):
                    continue
                yield rec
        time.sleep(interval)
