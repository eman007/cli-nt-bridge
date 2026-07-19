import pytest

from nt8bridge import account as ntaccount
from nt8bridge import flatten, watch


# ---- flatten primitive ----

def test_flatten_build_request():
    req = flatten.build_flatten_request("id1", "Sim101", "MNQ 06-26")
    assert req["kind"] == "flatten"
    assert req["account"] == "Sim101"
    assert req["instrument"] == "MNQ 06-26"


def test_flatten_requires_account_name():
    with pytest.raises(ValueError):
        flatten.run_flatten("")


def test_run_flatten_writes_trigger_and_reads_result(monkeypatch, tmp_path):
    from nt8bridge import ntio
    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    monkeypatch.setattr(flatten, "new_request_id", lambda: "fl1")
    trigger, result = ntio.ensure_bridge_dirs()
    ntio.atomic_write_json(result / "flatten_fl1.json",
                           {"id": "fl1", "status": "ok", "flattenCalled": True, "ordersCancelled": 0})
    payload = flatten.run_flatten("Sim101", "MNQ 06-26", timeout=1.0)
    assert payload["status"] == "ok" and payload["flattenCalled"] is True


# ---- watchdog: protection detection ----

def test_has_protective_stop_true_for_stop_order():
    blk = {"workingOrders": [{"instrument": "MNQ 06-26", "type": "StopMarket"}]}
    assert watch.has_protective_stop(blk, "MNQ 06-26") is True


def test_has_protective_stop_false_for_target_only():
    # a lone profit-target limit is NOT protection
    blk = {"workingOrders": [{"instrument": "MNQ 06-26", "type": "Limit"}]}
    assert watch.has_protective_stop(blk, "MNQ 06-26") is False


def test_has_protective_stop_false_when_no_orders():
    assert watch.has_protective_stop({"workingOrders": []}, "MNQ 06-26") is False


# ---- watchdog: scoping ----

def test_scan_once_scopes_to_watched_accounts():
    payload = {"status": "ok", "accounts": [
        {"name": "Sim101",
         "positions": [{"instrument": "MNQ 06-26", "marketPosition": "Short", "quantity": 1}],
         "workingOrders": []},  # naked, watched
        {"name": "SimOther_Loose",
         "positions": [{"instrument": "ES 06-26", "marketPosition": "Long", "quantity": 2}],
         "workingOrders": []},  # naked, but NOT watched -> must be ignored
    ]}
    state = ntaccount.parse_account_response(payload)
    findings = watch.scan_once(["Sim101"], _state_fn=lambda: state)
    assert len(findings) == 1
    assert findings[0]["account"] == "Sim101"
    assert findings[0]["protected"] is False


# ---- watchdog: grace period + kill ----

def test_protected_position_never_killed():
    findings = [{"account": "Sim101", "instrument": "MNQ 06-26",
                 "marketPosition": "Long", "quantity": 1, "protected": True}]
    killed = watch.watch(["Sim101"], grace_seconds=0.0, max_iterations=3,
                         _scan=lambda a: findings, _flatten=lambda ac, i: {"status": "ok"},
                         _sleep=lambda s: None, _log=lambda e: None)
    assert killed == []


def test_naked_within_grace_not_killed_then_killed_after_grace():
    findings = [{"account": "Sim101", "instrument": "MNQ 06-26",
                 "marketPosition": "Short", "quantity": 1, "protected": False}]
    flat_calls = []
    # _now() is called twice per naked finding per iteration:
    # iter1 -> 0,0 (sets timer, naked_for 0 < grace, no kill)
    # iter2 -> 25,25 (naked_for 25 >= 20 grace -> KILL)
    times = [0.0, 0.0, 25.0, 25.0]
    killed = watch.watch(
        ["Sim101"], grace_seconds=20.0, max_iterations=2,
        _scan=lambda a: findings,
        _flatten=lambda ac, i: (flat_calls.append((ac, i)) or {"status": "ok"}),
        _sleep=lambda s: None,
        _now=lambda: (times.pop(0) if times else 25.0),
        _log=lambda e: None,
    )
    assert flat_calls == [("Sim101", "MNQ 06-26")]
    assert len(killed) == 1
    assert killed[0]["instrument"] == "MNQ 06-26"
    assert killed[0]["reason"] == "no protective stop past grace period"


def test_watch_requires_accounts():
    with pytest.raises(ValueError):
        watch.watch([], max_iterations=1)
