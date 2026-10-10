"""Front-month resolution — the same dates the C# ResolveFrontMonth test pins.

The two implementations must agree: AutoReconnect.cs arms the silent-stall watchdog on its answer
and feedwatch_detect_loop.py scans on this one. If they disagree, one of them is watching a
contract that is not trading and will report a dead feed forever - which is exactly what both of
them did, three months apart (2026-09-18 and 2026-09-25).
"""
import datetime as dt

import pytest

from nt8bridge.frontmonth import resolve, resolve_all, third_friday


def d(s: str) -> dt.date:
    return dt.date.fromisoformat(s)


# ---- the roll itself ----

@pytest.mark.parametrize("today,want", [
    ("2026-09-09", "MNQ 09-26"),   # day before the roll -> still September
    ("2026-09-10", "MNQ 12-26"),   # roll day (8 days before the 3rd Friday, Sep 18)
    ("2026-09-18", "MNQ 12-26"),   # expiry morning -> must already be December
    ("2026-09-25", "MNQ 12-26"),   # the day feedwatch was found still on 09-26
    ("2026-08-01", "MNQ 09-26"),   # mid-quarter
    ("2026-12-09", "MNQ 12-26"),
    ("2026-12-10", "MNQ 03-27"),   # rolls across the year boundary in place
    ("2026-12-31", "MNQ 03-27"),
    ("2027-01-05", "MNQ 03-27"),
])
def test_resolves_the_front_month(today, want):
    assert resolve("MNQ ##-##", d(today)) == want


def test_a_bare_symbol_resolves_too():
    assert resolve("MES", d("2026-09-25")) == "MES 12-26"


def test_a_concrete_month_is_left_alone():
    """Pinning a contract by hand must keep working - including an expired one, which is the
    operator explicitly asking for it rather than a stale default."""
    assert resolve("MNQ 12-26", d("2026-09-25")) == "MNQ 12-26"
    assert resolve("MNQ 09-26", d("2026-09-25")) == "MNQ 09-26"


def test_garbage_raises_rather_than_inventing_a_contract():
    with pytest.raises(ValueError):
        resolve("not an instrument!", d("2026-09-25"))


def test_resolve_all_takes_the_env_string_shape():
    assert resolve_all("MNQ ##-##, MES ##-##", d("2026-09-25")) == ["MNQ 12-26", "MES 12-26"]
    assert resolve_all(["YM ##-##"], d("2026-09-25")) == ["YM 12-26"]


# ---- the expiry calendar, matching the C# side ----

@pytest.mark.parametrize("year,month,want", [
    (2026, 9, "2026-09-18"),
    (2026, 12, "2026-12-18"),
    (2027, 3, "2027-03-19"),
    (2027, 6, "2027-06-18"),
])
def test_third_friday(year, month, want):
    got = third_friday(year, month)
    assert got.isoformat() == want
    assert got.weekday() == 4


def test_every_quarterly_roll_for_three_years_lands_on_a_quarterly_month():
    """Sweep the calendar: whatever day you ask on, the answer is a quarterly month that has not
    yet passed its roll date."""
    day = d("2026-01-01")
    while day < d("2029-01-01"):
        got = resolve("MNQ ##-##", day)
        mm, yy = int(got[4:6]), int(got[7:9])
        assert mm in (3, 6, 9, 12), (day, got)
        expiry = third_friday(2000 + yy, mm)
        assert day < expiry - dt.timedelta(days=8), (day, got)
        day += dt.timedelta(days=1)
