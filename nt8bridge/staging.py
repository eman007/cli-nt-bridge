"""staging — decide WHICH DAYS a replay covers, because the seek cannot.

⛔ THE MEASUREMENT THIS IS BUILT ON (sentry-2, 2026-08-13, three consecutive drives)
    1. `playbackctl --seek` moves a DISPLAYED clock and never the tape. Verified against the bar
       transcript: seek to `12-30T23:00`, and the bars that arrived were stamped `12-30T04:59:50Z`
       — the start of the loaded data, **23 hours away**. Repeated after rewriting the range:
       identical bar, identical verdict.
    2. `--set-start/--set-end` NO-OP while Playback is CONNECTED (that is the `2099-12-01`
       read-back), and STICK while it is disconnected.
    3. Even a correct range did not bound the feed: with two day files staged the tape fed from
       the earlier one regardless. Removing `20251230.nrd` from `db\\replay\\GC 02-26\\` collapsed
       coverage to `12-30T23:00 → 12-31T16:00` and the run finally began where it was asked to.

    ⇒ **THE DAY FILE IS THE CONTROL SURFACE. The range is a filter on top of it. The seek is
      decoration.** A replay plays the staged data from its beginning; you choose the window by
      choosing what is staged.

WHY A VERB AND NOT A NOTE IN A RUNBOOK
    `runrange` was written to step over gaps BY SEEKING, so it counted `steps`, watched the clock
    move, and reported success while the tape never moved. Every multi-day bake this fleet has run
    was positioned by a mechanism that does nothing. A procedure nobody can execute identically
    twice is how that survived unnoticed; this makes it one call, with the before/after coverage
    printed as evidence.

⚠ IT MOVES FILES, SO IT IS WRITTEN TO BE REVERSIBLE. Parked days go to `db/_replay_parked/<inst>/`
    — deliberately OUTSIDE the replay tree, because a parking spot the thing you are hiding files
    from can still scan is not a parking spot — never a delete, and `--restore` puts every one of
    them back. `--list` is read-only and is the right first call on a box you did not set up.
"""
from __future__ import annotations

import json
import os
import posixpath
import re
import subprocess

DAY = re.compile(r"^(\d{8})\.nrd$", re.I)


def _run(host: str | None, ps: str, timeout: float = 120.0) -> tuple[int, str]:
    """Run a PowerShell one-liner on the node (or here). Single-quoted paths only — backslash
    escaping across bash → ssh → cmd → PowerShell is a documented time sink in this project."""
    cmd = 'powershell -NoProfile -Command "%s"' % ps.replace('"', '\\"')
    if host:
        from .fleet import ssh
        return ssh(host, cmd, timeout)
    r = subprocess.run(cmd, shell=True, capture_output=True, text=True, timeout=timeout)
    return r.returncode, (r.stdout or "").strip()


NT8 = "C:/Users/Administrator/Documents/NinjaTrader 8"


def _replay_dir(instrument: str) -> str:
    return "%s/db/replay/%s" % (NT8, instrument)


def _park_dir(instrument: str) -> str:
    """⛔ OUTSIDE db\replay ON PURPOSE. A `_parked` folder inside the replay tree is something NT
    may scan — as an instrument folder, or as data — and a parking spot that the thing you are
    hiding files from can still see is not a parking spot. Kept under db/ so it stays on the same
    volume and a move is a rename, not a copy."""
    return "%s/db/_replay_parked/%s" % (NT8, instrument)


def survey(instrument: str, host: str | None = None) -> dict:
    """What is staged, and what is parked. Read-only — safe as a first call anywhere."""
    live_dir = _replay_dir(instrument)
    park_dir = _park_dir(instrument)
    out = {"instrument": instrument, "host": host or "local",
           "replayDir": live_dir, "parkedDir": park_dir}
    for label, d in (("staged", live_dir), ("parked", park_dir)):
        rc, txt = _run(host, "if (Test-Path '%s') { Get-ChildItem '%s' -Filter *.nrd | "
                             "Select-Object -ExpandProperty Name }" % (d, d))
        names = sorted(n.strip() for n in (txt or "").splitlines()
                       if DAY.match(n.strip()))
        out[label] = names
    return out


def stage(instrument: str, days: list[str], host: str | None = None) -> dict:
    """Leave exactly `days` staged; park everything else. Returns before/after, always.

    An empty `days` is REFUSED rather than treated as "park everything": a typo that silently
    unstages a box's whole corpus is the kind of quiet destruction this tool must not offer.
    """
    days = [d.strip() for d in days if d and d.strip()]
    before = survey(instrument, host)
    if not days:
        return dict(before, refused=True,
                    why="REFUSED — no days given. Staging nothing would empty the replay folder; "
                        "if that is what you want, say it with --restore then park explicitly.")

    want = {d if d.lower().endswith(".nrd") else d + ".nrd" for d in days}
    missing = sorted(w for w in want if w not in set(before["staged"]) | set(before["parked"]))
    if missing:
        return dict(before, refused=True, missing=missing,
                    why="REFUSED — %d requested day(s) exist on neither side: %s. A bake positioned "
                        "on data that is not there produces an empty run that reads as a short one."
                        % (len(missing), ", ".join(missing)))

    live_dir = _replay_dir(instrument)
    park_dir = _park_dir(instrument)
    _run(host, "New-Item -ItemType Directory -Force '%s' | Out-Null" % park_dir)

    moved_out = [n for n in before["staged"] if n not in want]
    moved_in = [n for n in before["parked"] if n in want]
    for n in moved_out:
        _run(host, "Move-Item '%s/%s' '%s/' -Force" % (live_dir, n, park_dir))
    for n in moved_in:
        _run(host, "Move-Item '%s/%s' '%s/' -Force" % (park_dir, n, live_dir))

    after = survey(instrument, host)
    ok = set(after["staged"]) == want
    return {"instrument": instrument, "host": host or "local", "refused": False,
            "requested": sorted(want), "parkedOut": moved_out, "restoredIn": moved_in,
            "stagedBefore": before["staged"], "stagedAfter": after["staged"],
            "succeeded": ok,
            "verdict": ("staged exactly the %d requested day(s)" % len(want)) if ok else
                       "⛔ THE MOVES RESOLVED BUT THE FOLDER DOES NOT MATCH THE REQUEST — treat as "
                       "NOT staged and look before running anything",
            "note": "⚠ Playback must be RECONNECTED for a staging change to take effect: "
                    "connections --disconnect Playback, then --connect Playback --confirm."}


def restore(instrument: str, host: str | None = None) -> dict:
    """Put every parked day back. The undo, and it is deliberately unconditional."""
    before = survey(instrument, host)
    live_dir = _replay_dir(instrument)
    park_dir = _park_dir(instrument)
    for n in before["parked"]:
        _run(host, "Move-Item '%s/%s' '%s/' -Force" % (park_dir, n, live_dir))
    after = survey(instrument, host)
    return {"instrument": instrument, "host": host or "local",
            "restored": before["parked"], "stagedAfter": after["staged"],
            "parkedAfter": after["parked"],
            "succeeded": not after["parked"],
            "verdict": "restored %d day(s); nothing left parked" % len(before["parked"])
                       if not after["parked"] else
                       "⛔ %d day(s) COULD NOT BE RESTORED and are still parked" % len(after["parked"])}
