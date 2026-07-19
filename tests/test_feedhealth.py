"""Tests for feed-health (frozen-but-connected feed detection).

The gap these cover: NT8 reports a data feed `connected: true` even when it has gone
DARK (no ticks flowing). `connwatch` only heals feeds NT8 flags as *dropped*, so a
frozen chart slips through every net (a chart can freeze mid-session while the live
market keeps moving — the bridge still reports it connected the whole time).
Detection = a feed whose last tick is stale past a threshold is FROZEN, even
though it is nominally "connected".
"""
import json

import pytest

from nt8bridge import cli
from nt8bridge import feedhealth
from nt8bridge import feedwatch


# ---- request + parse ----

def test_feedhealth_build_request_joins_instruments():
    req = feedhealth.build_feedhealth_request("id1", ["MNQ 09-26", "NQ 09-26"])
    assert req["id"] == "id1" and req["kind"] == "feedhealth"
    # comma-joined because instrument names contain spaces, never commas
    assert req["instruments"] == "MNQ 09-26,NQ 09-26"


def test_parse_feedhealth_response_and_feed_lookup():
    state = feedhealth.parse_feedhealth_response({
        "status": "ok",
        "feeds": [
            {"instrument": "MNQ 09-26", "lastPrice": 30643.0, "lastTickTime": "2026-06-22T17:55:00Z", "ageMs": 800},
        ],
    })
    assert state.ok is True
    assert state.feed("MNQ 09-26")["lastPrice"] == 30643.0
    assert state.feed("nope") is None


# ---- the core: stale detection ----

def test_fresh_feed_is_not_stale():
    state = feedhealth.parse_feedhealth_response({
        "status": "ok",
        "feeds": [{"instrument": "MNQ 09-26", "lastPrice": 30643.0, "ageMs": 800}],
    })
    assert state.stale_feeds(max_age_seconds=10.0) == []


def test_connected_but_dark_feed_is_flagged_stale():
    """The frozen-feed bug: a feed with a (stale) last price but a tick age far past
    threshold is FROZEN, even though NT8 calls it connected."""
    state = feedhealth.parse_feedhealth_response({
        "status": "ok",
        "feeds": [{"instrument": "MNQ 09-26", "lastPrice": 30598.25, "ageMs": 1_800_000}],  # ~30 min stale
    })
    stale = state.stale_feeds(max_age_seconds=10.0)
    assert [f["instrument"] for f in stale] == ["MNQ 09-26"]


def test_feed_with_no_tick_data_is_treated_as_stale():
    """ageMs null = the feed has delivered no tick we can age -> not live -> stale
    (conservative: if we cannot confirm freshness, do not assume the feed is good)."""
    state = feedhealth.parse_feedhealth_response({
        "status": "ok",
        "feeds": [{"instrument": "MNQ 09-26", "lastPrice": None, "ageMs": None}],
    })
    assert [f["instrument"] for f in state.stale_feeds(max_age_seconds=10.0)] == ["MNQ 09-26"]


def test_stale_feeds_respects_allow_list():
    state = feedhealth.parse_feedhealth_response({
        "status": "ok",
        "feeds": [
            {"instrument": "MNQ 09-26", "ageMs": 1_800_000},
            {"instrument": "ES 09-26", "ageMs": 1_800_000},
        ],
    })
    assert [f["instrument"] for f in state.stale_feeds(10.0, allow=["MNQ 09-26"])] == ["MNQ 09-26"]


def test_age_boundary_is_strictly_greater_than_threshold():
    state = feedhealth.parse_feedhealth_response({
        "status": "ok",
        "feeds": [
            {"instrument": "AT", "ageMs": 10_000},   # exactly 10s -> NOT stale
            {"instrument": "OVER", "ageMs": 10_001},  # just over -> stale
        ],
    })
    assert [f["instrument"] for f in state.stale_feeds(max_age_seconds=10.0)] == ["OVER"]


# ---- run_feedhealth IPC ----

def test_feedhealth_requires_instruments():
    with pytest.raises(ValueError):
        feedhealth.run_feedhealth([])


def test_run_feedhealth_writes_trigger_and_reads_result(monkeypatch, tmp_path):
    from nt8bridge import ntio
    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    monkeypatch.setattr(feedhealth, "new_request_id", lambda: "fh1")
    trigger, result = ntio.ensure_bridge_dirs()
    ntio.atomic_write_json(result / "feedhealth_fh1.json",
                           {"id": "fh1", "status": "ok", "feeds": []})
    payload = feedhealth.run_feedhealth(["MNQ 09-26"], timeout=1.0)
    assert payload["status"] == "ok"
    assert (trigger / "feedhealth_fh1.json").exists()


# ---- feedwatch guardian (detect + alert) ----

def test_feedwatch_requires_instruments():
    with pytest.raises(ValueError):
        feedwatch.watch([], max_iterations=1)


def test_fresh_feed_never_alerts():
    fresh = []  # scan returns the stale feeds; a fresh feed yields none
    alerts = []
    events = feedwatch.watch(
        ["MNQ 09-26"], grace_seconds=0.0, max_iterations=3,
        _scan=lambda allow: fresh,
        _sleep=lambda s: None, _now=lambda: 0.0, _log=lambda e: alerts.append(e),
    )
    assert alerts == [] and events == []


def test_stale_within_grace_then_alerts_after_grace():
    stale = [{"instrument": "MNQ 09-26", "ageMs": 1_800_000}]
    alerts = []
    # iter1 -> now 0 (sets timer, within grace, no alert); iter2 -> now 25 (past 20s grace -> alert)
    times = [0.0, 25.0]
    events = feedwatch.watch(
        ["MNQ 09-26"], grace_seconds=20.0, max_iterations=2,
        _scan=lambda allow: stale,
        _sleep=lambda s: None, _now=lambda: (times.pop(0) if times else 25.0),
        _log=lambda e: alerts.append(e),
    )
    assert len(alerts) == 1
    assert alerts[0]["instrument"] == "MNQ 09-26"
    assert alerts[0]["reason"] == "feed connected but no tick past grace (frozen)"


def test_recovered_feed_resets_and_can_alert_again():
    """A feed that goes fresh clears its timer; if it freezes again it re-alerts (no
    permanent suppression)."""
    # iter1 stale@0 (timer set), iter2 fresh@1 (reset), iter3 stale@2 (timer reset), iter4 stale@30 (alert)
    rounds = [
        [{"instrument": "X", "ageMs": 999_999}],
        [],
        [{"instrument": "X", "ageMs": 999_999}],
        [{"instrument": "X", "ageMs": 999_999}],
    ]
    times = [0.0, 1.0, 2.0, 30.0]
    alerts = []
    events = feedwatch.watch(
        ["X"], grace_seconds=20.0, max_iterations=4,
        _scan=lambda allow: rounds.pop(0),
        _sleep=lambda s: None, _now=lambda: times.pop(0),
        _log=lambda e: alerts.append(e),
    )
    assert len(alerts) == 1 and alerts[0]["instrument"] == "X"


def test_feedwatch_survives_a_scan_failure():
    """NT8 down / AddOn absent -> run_feedhealth times out; the loop must NOT crash,
    just skip the round and keep watching (the feed may come back)."""
    calls = [0]
    def boom(allow):
        calls[0] += 1
        raise TimeoutError("no result from AddOn")
    alerts = []
    events = feedwatch.watch(
        ["MNQ 09-26"], grace_seconds=0.0, max_iterations=3,
        _scan=boom, _sleep=lambda s: None, _now=lambda: 0.0, _log=lambda e: alerts.append(e),
    )
    assert calls[0] == 3        # kept scanning despite every scan failing
    assert events == []         # no frozen-feed alert manufactured from a failed read


def test_realert_cadence_throttles_repeat_alerts():
    """While a feed stays frozen, re-shout only every realert_seconds (not every scan).
    grace=0 so the first alert fires immediately at t=0 (matches watchdog_detect_loop);
    re-alert at t=30; t=10 and t=31 are throttled -> 2 alerts across 4 frozen scans."""
    stale = [{"instrument": "X", "ageMs": 999_999}]
    times = [0.0, 10.0, 30.0, 31.0]
    alerts = []
    feedwatch.watch(
        ["X"], grace_seconds=0.0, realert_seconds=30.0, max_iterations=4,
        _scan=lambda allow: stale,
        _sleep=lambda s: None, _now=lambda: times.pop(0),
        _log=lambda e: alerts.append(e),
    )
    assert len(alerts) == 2


# ---- CLI wiring ----

def test_cli_feedhealth_timeout(monkeypatch, capsys):
    def boom(instruments, timeout=15.0):
        raise TimeoutError("no result from AddOn")
    monkeypatch.setattr(cli.ntfeedhealth, "run_feedhealth", boom)
    rc = cli.main(["feedhealth", "--instrument", "MNQ 09-26"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 1 and payload["status"] == "timeout"


def test_cli_feedhealth_reports_stale(monkeypatch, capsys):
    monkeypatch.setattr(cli.ntfeedhealth, "run_feedhealth",
                        lambda instruments, **kw: {"status": "ok",
                                                   "feeds": [{"instrument": "MNQ 09-26", "ageMs": 1_800_000}]})
    rc = cli.main(["feedhealth", "--instrument", "MNQ 09-26", "--max-age", "10"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 1  # stale feed -> non-zero exit (diagnostic)
    assert payload["staleFeeds"] == ["MNQ 09-26"]


def test_cli_feedwatch_once(monkeypatch, capsys):
    monkeypatch.setattr(cli.ntfeedwatch, "watch",
                        lambda names, **kw: [{"instrument": names[0], "reason": "frozen"}])
    rc = cli.main(["feedwatch", "--instrument", "MNQ 09-26", "--once"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 0 and payload["command"] == "feedwatch"
    assert payload["instruments"] == ["MNQ 09-26"]
