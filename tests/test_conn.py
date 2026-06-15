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


def test_connwatch_gives_up_and_surfaces_after_max_attempts():
    flagged = [{"name": "X", "inadvertentlyDropped": True}]
    times = [0.0, 20.0, 60.0]  # advance past each backoff so a 2nd attempt fires
    events = connwatch.watch(
        ["X"], grace_seconds=0.0, max_attempts=2, max_iterations=3,
        _scan=lambda allow: flagged,
        _reconnect=lambda name: {"status": "ok", "statusAfter": "ConnectionLost"},  # never goes green
        _sleep=lambda s: None, _now=lambda: (times.pop(0) if times else 100.0), _log=lambda e: None,
    )
    assert len(events) == 2  # capped at max_attempts
    assert events[-1].get("giving_up") is True


def test_backoff_grows_and_caps():
    assert connwatch._backoff_seconds(1) == 15.0
    assert connwatch._backoff_seconds(2) == 30.0
    assert connwatch._backoff_seconds(3) == 60.0
    assert connwatch._backoff_seconds(99) == 300.0  # capped


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
