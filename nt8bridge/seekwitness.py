"""seekwitness — check a replay seek against THE BARS, because the clock lies.

⛔⛔ THE DEFECT THIS EXISTS FOR, MEASURED 2026-08-13 ON sentry-2.
    `playbackctl --seek 2025-12-30T12:00 --speed 5` reported

        landed within a minute of target after 250ms · speed is now 5
        playback -> clock 2025-12-30T12:07:45  speed 5  moving True

    and the bar transcript wrote **zero new bars**: still 946, newest still
    `2025-12-31T21:59:03Z`. Earlier the same session the clock read `12-30T23:08` while every bar
    the replay produced was stamped `12-31T21:15Z`. ⇒ **`NowEst` is a DISPLAYED clock, not the data
    cursor.** The transport keeps feeding from wherever it already was, and the seek's settle poll
    happily confirms the value it just wrote to a property nothing reads back.

    What that invalidates is not small: `runrange` steps over gaps BY SEEKING, so it counts a
    `step`, sees the clock move, and reports success while the tape never moved. And no
    replay-built corpus row can assert its window from the seek that preceded it.

THE RULE HERE: a seek is verified by NEW BARS NEAR THE TARGET, or it is not verified at all.
    `SentinelBarDump` already writes every completed bar with its own time and an `rt` flag, so the
    witness costs nothing to collect and is the same artefact that caught the defect.

    REPOSITIONED       new bars arrived, and they are near the target
    DID NOT REPOSITION new bars arrived somewhere ELSE — the seek was cosmetic, and we say where
    UNVERIFIED        no new bars (a PARKED tape produces none, and neither does a data gap)

⚠ UNVERIFIED IS NOT PASSED. It is reported as its own outcome and never folded into success — that
    distinction is the most expensive recurring lesson in this project.
"""
from __future__ import annotations

import datetime as dt
import glob
import io
import json
import os
import time

from .ntio import nt8_root

TAIL_BYTES = 512 * 1024          # dumps run to tens of MB; only the end can be new


def newest_dump(bars_dir: str | None = None) -> str | None:
    """The BarDump transcript currently being written, or None if nothing is recording."""
    d = bars_dir or os.path.join(str(nt8_root()), "Sentinel", "Harness", "bars")
    files = glob.glob(os.path.join(d, "*.jsonl"))
    return max(files, key=os.path.getmtime) if files else None


def last_live_bar(path: str) -> tuple[str | None, int]:
    """(time of the LAST live row in FILE ORDER, current file size).

    ⚠ FILE ORDER, NOT THE MAXIMUM TIMESTAMP — and the difference is the whole check. The first
    version of this took `max(t)`, which is silently blind to the case it exists to catch: a seek
    BACKWARDS produces bars with EARLIER stamps, so the maximum never moves and a successful
    reposition reads as "no new bars". Its own control test caught it. What we want is "what was
    appended", and that is a position in the file, not a value.

    Tail-read on purpose: a full parse of a 65 MB transcript inside a seek would put minutes of
    disk in the path of a verb whose whole job is to be quick and honest.
    """
    if not path or not os.path.exists(path):
        return None, 0
    size = os.path.getsize(path)
    with io.open(path, "rb") as fh:
        if size > TAIL_BYTES:
            fh.seek(size - TAIL_BYTES)
            fh.readline()                        # discard the partial line we landed in
        blob = fh.read().decode("utf-8", "replace")
    last = None
    for line in blob.splitlines():
        line = line.strip()
        if not line or not line.startswith("{"):
            continue
        try:
            r = json.loads(line)
        except ValueError:
            continue                             # a torn final line is expected, not an error
        if r.get("rt") and r.get("t"):
            last = r["t"]
    return last, size


def first_bar_after(path: str, since_bytes: int) -> str | None:
    """The FIRST live bar written after byte `since_bytes` — i.e. the first bar of the new run.

    The first bar after the seek is the honest sample: later ones drift away as the replay walks,
    so judging on the newest bar would slowly turn a real reposition into a false failure.
    """
    if not path or not os.path.exists(path):
        return None
    with io.open(path, "rb") as fh:
        fh.seek(max(0, since_bytes))
        # ⛔ Do NOT readline() to "skip a partial line" here: the baseline is a byte count taken at
        # a line boundary, so the first line after it is the FIRST NEW BAR — the very row this
        # function exists to return. Discarding it made every real reposition read as no-witness.
        # A torn line is handled where it belongs: the JSON guard in the loop.
        for raw in fh:
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("{"):
                continue
            try:
                r = json.loads(line)
            except ValueError:
                continue
            if r.get("rt") and r.get("t"):
                return r["t"]
    return None


def _parse(stamp: str | None) -> dt.datetime | None:
    if not stamp:
        return None
    s = stamp.replace("Z", "").split(".")[0]
    try:
        return dt.datetime.fromisoformat(s)
    except ValueError:
        return None


def verify(target: str, before_size: int, path: str | None,
           wait_s: float = 25.0, tolerance_min: float = 90.0,
           utc_offset_h: float = 0.0) -> dict:
    """Poll the transcript for bars written AFTER the seek and judge WHERE they are.

    `tolerance_min` is wide by design: a seek lands on a timestamp, but the next COMPLETED bar can
    be a while later on a quiet tape. A wide window still separates "moved to the target" from
    "never left 22 hours away", which is the failure actually seen.

    `utc_offset_h` converts the transport's Est stamps to the bar stamps' UTC. It is a PARAMETER
    rather than a constant because `--api` shows `NowEst`/`NowLocal` differing by the box's own
    offset, and hard-coding somebody's timezone is how a check quietly becomes wrong elsewhere.
    """
    path = path or newest_dump()
    out = {"witness": os.path.basename(path) if path else None,
           "bytesBefore": before_size, "target": target, "waitedS": 0.0}
    if not path:
        out.update(verdict="UNVERIFIED", repositioned=None,
                   why="no BarDump transcript on this box — nothing is recording, so there is no "
                       "witness. Attach SentinelBarDump (chart --apply-template) to make seeks "
                       "checkable.")
        return out

    t0 = time.time()
    newest = None
    while time.time() - t0 < wait_s:
        time.sleep(2.0)
        if os.path.getsize(path) > before_size:
            newest = first_bar_after(path, before_size)
            if newest:
                break
    out["waitedS"] = round(time.time() - t0, 1)
    out["firstBarAfter"] = newest

    if not newest:
        out.update(verdict="UNVERIFIED", repositioned=None,
                   why="no new bars in %.0fs. A PARKED tape writes none (set a speed), and so does "
                       "a gap in the data. ⚠ UNVERIFIED IS NOT PASSED — the seek is unconfirmed."
                       % wait_s)
        return out

    tgt, got = _parse(target), _parse(newest)
    if not tgt or not got:
        out.update(verdict="UNVERIFIED", repositioned=None,
                   why="could not parse target %r or bar time %r" % (target, newest))
        return out

    drift_min = abs((got - dt.timedelta(hours=utc_offset_h) - tgt).total_seconds()) / 60.0
    out["driftMin"] = round(drift_min, 1)
    if drift_min <= tolerance_min:
        out.update(verdict="REPOSITIONED", repositioned=True,
                   why="bars are arriving %.1f min from the target — the tape moved." % drift_min)
    else:
        out.update(verdict="DID NOT REPOSITION", repositioned=False,
                   why="⛔ THE CLOCK MOVED AND THE TAPE DID NOT. Bars are arriving at %s, which is "
                       "%.0f min (%.1f h) from the requested %s. The seek was cosmetic — treat any "
                       "run started this way as covering the WRONG window."
                       % (newest, drift_min, drift_min / 60.0, target))
    return out
