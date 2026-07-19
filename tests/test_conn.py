import json

import pytest

from nt8bridge import cli
from nt8bridge import connections as ntconnections
from nt8bridge import connwatch
from nt8bridge import reconnect as ntreconnect


# ---- connections read ----

def test_connections_build_request():
    req = ntconnections.build_connections_request("id1")
    assert req["id"] == "id1" and req["kind"] == "connections"


def test_parse_and_inadvertently_dropped_filters_user_parked():
    state = ntconnections.parse_connections_response({
        "status": "ok",
        "connections": [
            {"name": "Tradovate-Demo", "status": "ConnectionLost", "connected": False, "inadvertentlyDropped": True},
            {"name": "Tradovate-Live", "status": "Disconnected", "connected": False, "inadvertentlyDropped": False},
            {"name": "Kinetick", "status": "Connected", "connected": True, "inadvertentlyDropped": False},
        ],
    })
    assert state.ok is True
    # only the inadvertent drop; the user-parked + the connected one are excluded
    assert {c["name"] for c in state.inadvertently_dropped()} == {"Tradovate-Demo"}
    assert state.connection("Kinetick")["connected"] is True
    assert state.connection("nope") is None


def test_inadvertently_dropped_respects_allow_list():
    state = ntconnections.parse_connections_response({
        "status": "ok",
        "connections": [
            {"name": "A", "inadvertentlyDropped": True},
            {"name": "B", "inadvertentlyDropped": True},
        ],
    })
    assert {c["name"] for c in state.inadvertently_dropped(["A"])} == {"A"}


def test_run_connections_writes_trigger_and_reads_result(monkeypatch, tmp_path):
    from nt8bridge import ntio
    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    monkeypatch.setattr(ntconnections, "new_request_id", lambda: "cn1")
    trigger, result = ntio.ensure_bridge_dirs()
    ntio.atomic_write_json(result / "connections_cn1.json",
                           {"id": "cn1", "status": "ok", "connections": []})
    payload = ntconnections.run_connections(timeout=1.0)
    assert payload["status"] == "ok"
    assert (trigger / "connections_cn1.json").exists()


# ---- reconnect action ----

def test_reconnect_build_request():
    req = ntreconnect.build_reconnect_request("id1", "Tradovate-Demo")
    assert req["kind"] == "reconnect" and req["connection"] == "Tradovate-Demo"


def test_reconnect_requires_name():
    with pytest.raises(ValueError):
        ntreconnect.run_reconnect("")


def test_run_reconnect_writes_trigger_and_reads_result(monkeypatch, tmp_path):
    from nt8bridge import ntio
    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    monkeypatch.setattr(ntreconnect, "new_request_id", lambda: "rc1")
    trigger, result = ntio.ensure_bridge_dirs()
    ntio.atomic_write_json(result / "reconnect_rc1.json",
                           {"id": "rc1", "status": "ok", "name": "Tradovate-Demo", "statusAfter": "Connected"})
    payload = ntreconnect.run_reconnect("Tradovate-Demo", timeout=1.0)
    assert payload["status"] == "ok" and payload["statusAfter"] == "Connected"


# ---- connwatch guardian ----

def test_connwatch_requires_connections():
    with pytest.raises(ValueError):
        connwatch.watch([], max_iterations=1)


def test_user_parked_connection_never_reconnected():
    """THE constraint: a connection the user disconnected (not inadvertent) is never touched."""
    state = ntconnections.parse_connections_response({
        "status": "ok",
        "connections": [
            {"name": "Tradovate-Demo", "status": "Disconnected", "connected": False, "inadvertentlyDropped": False},
        ],
    })
    calls = []
    events = connwatch.watch(
        ["Tradovate-Demo"], grace_seconds=0.0, max_iterations=3,
        _scan=lambda allow: state.inadvertently_dropped(allow),
        _reconnect=lambda name: calls.append(name) or {"statusAfter": "Connected"},
        _sleep=lambda s: None, _now=lambda: 0.0, _log=lambda e: None,
    )
    assert calls == [] and events == []


def test_inadvertent_within_grace_then_reconnected_after_grace():
    flagged = [{"name": "Tradovate-Demo", "inadvertentlyDropped": True}]
    calls = []
    # iter1 -> now 0 (sets timer, within grace, no action); iter2 -> now 25 (past 20s grace -> reconnect)
    times = [0.0, 25.0]
    events = connwatch.watch(
        ["Tradovate-Demo"], grace_seconds=20.0, max_iterations=2,
        _scan=lambda allow: flagged,
        _reconnect=lambda name: (calls.append(name) or {"status": "ok", "statusAfter": "Connected"}),
        _sleep=lambda s: None, _now=lambda: (times.pop(0) if times else 25.0), _log=lambda e: None,
    )
    assert calls == ["Tradovate-Demo"]
    assert len(events) == 1 and events[0]["went_green"] is True
    assert events[0]["reason"] == "inadvertent drop past grace period"


def test_connwatch_cools_down_then_retries_after_max_attempts():
    """MED-1b: after max_attempts failures it COOLS DOWN (not a permanent give-up), then
    resets and retries once the cooldown elapses — so a session-critical feed keeps being
    healed (e.g. after an expired token is refreshed)."""
    flagged = [{"name": "X", "inadvertentlyDropped": True}]
    # iter1 t=0 attempt1; iter2 t=20 attempt2 -> cooldown(1000) until 1020;
    # iter3 t=200 still cooling; iter4 t=5000 cooldown elapsed -> reset -> attempt3.
    times = [0.0, 20.0, 200.0, 5000.0]
    events = connwatch.watch(
        ["X"], grace_seconds=0.0, max_attempts=2, max_iterations=4, cooldown_seconds=1000.0,
        _scan=lambda allow: flagged,
        _reconnect=lambda name: {"status": "ok", "statusAfter": "ConnectionLost"},  # never goes green
        _sleep=lambda s: None, _now=lambda: (times.pop(0) if times else 99999.0),
        _log=lambda e: None, _rng=lambda: 0.5,
    )
    attempts = [e for e in events if e.get("attempt")]
    assert len(attempts) == 3  # 2 before cooldown + 1 after -> NOT a permanent give-up
    assert any(e.get("cooling_down") for e in events)
    assert not any(e.get("giving_up") for e in events)  # permanent give-up replaced by cooldown


def test_connecting_status_does_not_burn_the_attempt_budget():
    """MED-1a: a slow reconnect that reads 'Connecting' is in-progress, not a failed attempt,
    so the guardian never falsely gives up on a connection that is actually coming back."""
    flagged = [{"name": "X", "inadvertentlyDropped": True}]
    times = iter([0.0, 20.0, 40.0, 60.0, 80.0, 100.0])
    events = connwatch.watch(
        ["X"], grace_seconds=0.0, max_attempts=2, max_iterations=5,
        connecting_recheck=5.0, max_connecting_rechecks=10,
        _scan=lambda allow: flagged,
        _reconnect=lambda name: {"status": "ok", "statusAfter": "Connecting"},
        _sleep=lambda s: None, _now=lambda: next(times), _log=lambda e: None, _rng=lambda: 0.5,
    )
    assert events and all(e.get("in_progress") for e in events)   # every result was an in-progress recheck
    assert not any(e.get("attempt") for e in events)              # zero failed attempts counted
    assert not any(e.get("cooling_down") or e.get("giving_up") for e in events)


def test_persistent_connecting_eventually_escalates_to_a_real_attempt():
    """MED-1a bound: if 'Connecting' never settles, after max_connecting_rechecks it is
    escalated to a real failed attempt, so a permanently-stuck connect still surfaces."""
    flagged = [{"name": "X", "inadvertentlyDropped": True}]
    t = [0.0]
    def now():
        v = t[0]; t[0] += 50.0; return v
    events = connwatch.watch(
        ["X"], grace_seconds=0.0, max_attempts=5, max_iterations=8,
        connecting_recheck=5.0, max_connecting_rechecks=2,
        _scan=lambda allow: flagged,
        _reconnect=lambda name: {"status": "ok", "statusAfter": "Connecting"},
        _sleep=lambda s: None, _now=now, _log=lambda e: None, _rng=lambda: 0.5,
    )
    assert any(e.get("in_progress") for e in events)   # rechecked while connecting
    assert any(e.get("attempt") for e in events)        # then escalated to a real attempt


def test_backoff_grows_and_caps():
    assert connwatch._backoff_seconds(1) == 15.0
    assert connwatch._backoff_seconds(2) == 30.0
    assert connwatch._backoff_seconds(3) == 60.0
    assert connwatch._backoff_seconds(99) == 300.0  # capped


def test_backoff_jitter_is_deterministic_with_injected_rng():
    """LOW-3: optional +/- jitter so connections that drop together don't retry in lockstep.
    Off by default (jitter_frac=0.0); rng()=0.5 is the no-change midpoint."""
    assert connwatch._backoff_seconds(2, jitter_frac=0.0) == 30.0
    assert connwatch._backoff_seconds(2, jitter_frac=0.1, rng=lambda: 0.5) == 30.0
    assert connwatch._backoff_seconds(2, jitter_frac=0.1, rng=lambda: 1.0) == 30.0 * 1.1
    assert connwatch._backoff_seconds(2, jitter_frac=0.1, rng=lambda: 0.0) == 30.0 * 0.9


# ---- CLI wiring ----

def test_cli_connections_timeout(monkeypatch, capsys):
    def boom(timeout=15.0):
        raise TimeoutError("no result from AddOn")
    monkeypatch.setattr(cli.ntconnections, "run_connections", boom)
    rc = cli.main(["connections"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 1 and payload["status"] == "timeout"


def test_cli_reconnect_requires_name():
    with pytest.raises(SystemExit):
        cli.main(["reconnect"])  # argparse rejects the missing --name


def test_cli_connwatch_once(monkeypatch, capsys):
    monkeypatch.setattr(cli.ntconnwatch, "watch",
                        lambda names, **kw: [{"connection": names[0], "went_green": True}])
    rc = cli.main(["connwatch", "--name", "Tradovate-Demo", "--once"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 0 and payload["command"] == "connwatch"
    assert payload["connections"] == ["Tradovate-Demo"]
