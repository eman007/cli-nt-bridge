"""Front month for a CME quarterly future, resolved from the clock.

WHY THIS EXISTS
    A hardcoded contract month is a time bomb with a three-month fuse, and it has now gone off
    twice on this desk:

      * 2026-09-18  AutoReconnect's watchdog was armed on a literal `MNQ 09-26`. The contract
        expired that morning, the feed for it stopped, and the AddOn read that as a dead feed -
        it disconnected and reconnected every two minutes all session and dropped five strategy
        rows. Fixed in the AddOn by resolving `MNQ ##-##` at runtime.
      * 2026-09-25  `feedwatch_detect_loop.py` still had `MNQ 09-26` as its default instrument,
        under a comment reading "UPDATE on the quarterly roll". Nobody did. From the moment that
        contract expired it reported the feed FROZEN every scan: ~700 false alerts a day for a
        week, burying the real outages in the same log.

    The rule is the CME one and matches ResolveFrontMonth in AutoReconnect.cs exactly, so the
    two agree on which contract is live: quarterly months (Mar/Jun/Sep/Dec), expiry on the third
    Friday, and roll `roll_days_before` days ahead of it.

    `MNQ ##-##` (or a bare `MNQ`) resolves; a concrete `MNQ 12-26` passes through untouched, so
    an operator can always pin a contract deliberately.
"""
from __future__ import annotations

import datetime as _dt
import re

_PLACEHOLDER = re.compile(r"^(?P<sym>[A-Za-z0-9]+)(\s+##-##)?$")
_CONCRETE = re.compile(r"^(?P<sym>[A-Za-z0-9]+)\s+(?P<mm>\d{2})-(?P<yy>\d{2})$")

QUARTERLY = (3, 6, 9, 12)
DEFAULT_ROLL_DAYS_BEFORE = 8


def third_friday(year: int, month: int) -> _dt.date:
    """The CME expiry date for a quarterly equity-index future."""
    first = _dt.date(year, month, 1)
    return first + _dt.timedelta(days=((4 - first.weekday()) % 7) + 14)


def resolve(configured: str, today: _dt.date | None = None,
            roll_days_before: int = DEFAULT_ROLL_DAYS_BEFORE) -> str:
    """`MNQ ##-##` / `MNQ` -> `MNQ 12-26`. A concrete month is returned unchanged.

    Raises ValueError on something that is neither, rather than guessing - a typo that silently
    became a real-looking contract would be worse than a crash at startup.
    """
    s = (configured or "").strip()
    if _CONCRETE.match(s):
        return s
    m = _PLACEHOLDER.match(s)
    if not m:
        raise ValueError(f"not an instrument name: {configured!r}")
    sym = m.group("sym").upper()
    day = today or _dt.date.today()

    year, month = day.year, day.month
    for _ in range(8):                      # at most two years ahead; the loop always terminates
        if month in QUARTERLY:
            expiry = third_friday(year, month)
            if day < expiry - _dt.timedelta(days=roll_days_before):
                return f"{sym} {month:02d}-{year % 100:02d}"
        month += 1
        if month > 12:
            month, year = 1, year + 1
    raise RuntimeError("no quarterly month found within two years")   # unreachable


def resolve_all(configured, today: _dt.date | None = None,
                roll_days_before: int = DEFAULT_ROLL_DAYS_BEFORE) -> list[str]:
    """Resolve a list (or a comma-separated string) of instrument names."""
    if isinstance(configured, str):
        configured = [p for p in configured.split(",") if p.strip()]
    return [resolve(c, today, roll_days_before) for c in configured]
