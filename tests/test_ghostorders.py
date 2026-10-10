"""ghostorders — detection, and the guardrails around resetting an account.

The tests that matter here are the refusals. Clearing a stuck order on a sim account is routine;
wiping a funded account because a provider check was written as a denylist is not recoverable.
"""
import json

import pytest

from nt8bridge import cli
from nt8bridge import ghostorders as ntghost


# ---- request shape ----

def test_read_only_request_carries_no_action():
    req = ntghost.build_ghostorders_request("id1")
    assert req["kind"] == "ghostorders"
    assert "reset" not in req and "clearTradeData" not in req
    assert req["staleSeconds"] == "2"


def test_reset_flags_are_bare_booleans_but_staleseconds_is_quoted():
    """The AddOn's reader special-cases bare `true` for flags, and only extracts QUOTED values for
    everything else - so these two must serialise differently."""
    req = ntghost.build_ghostorders_request("id2", account="SimX", reset=True,
                                            clear_trade_data=True, stale_seconds=5)
    assert req["reset"] is True
    assert req["clearTradeData"] is True
    assert req["staleSeconds"] == "5"


def test_include_all_is_a_bare_flag_and_is_off_by_default():
    """The AddOn honored `includeAll` for a while with nothing on the Python side able to send it,
    so `allOrders` came back empty every time and the flag read as broken rather than unrequested."""
    assert "includeAll" not in ntghost.build_ghostorders_request("id3")
    assert ntghost.build_ghostorders_request("id3", include_all=True)["includeAll"] is True


def test_reset_without_an_account_is_refused_client_side():
    """Refused before it ever reaches NinjaTrader. 'clear the stuck order' and 'wipe every sim
    account on the box' must never be one keystroke apart."""
    with pytest.raises(ValueError, match="requires --account"):
        ntghost.run_ghostorders(reset=True)


# ---- classification ----

def _st(**over):
    payload = {
        "status": "ok", "staleSeconds": 2, "totalFrozen": 3,
        "accounts": [
            {"name": "SimScalp", "provider": "Simulator", "isSimulator": True,
             "connectionStatus": "Connected", "orders": 4, "frozenCount": 2,
             "frozen": [{"orderId": "a", "state": "CancelPending", "ageSeconds": 91},
                        {"orderId": "b", "state": "Initialized", "ageSeconds": 400}]},
            {"name": "FundedAcct", "provider": "Provider31", "isSimulator": False,
             "connectionStatus": "Connected", "orders": 2, "frozenCount": 1,
             "frozen": [{"orderId": "c", "state": "ChangePending", "ageSeconds": 55}]},
            {"name": "Quiet", "provider": "Simulator", "isSimulator": True,
             "connectionStatus": "Connected", "orders": 9, "frozenCount": 0, "frozen": []},
        ],
        "reset": None, "notes": [], "errors": [],
    }
    payload.update(over)
    return ntghost.parse_ghostorders_response(payload)


def test_a_negative_age_is_still_a_ghost():
    """NinjaTrader stamps Order.Time in ET and these boxes run PT, so a fresh order reports an age of
    about -3h. The staleness guard compared `age >= staleSeconds` and so rejected everything, and
    ghostorders called the box clean while a CancelPending Stop1 sat on the chart (2026-09-08,
    SimA NQ, ageSeconds -5566). The AddOn now qualifies those on state alone and marks
    clockSkew; this pins the parsed shape so a 'tidy-up' cannot quietly drop the skewed row."""
    st = _st(totalFrozen=1, accounts=[
        {"name": "SimA", "provider": "Simulator", "isSimulator": True,
         "connectionStatus": "Connected", "orders": 1, "frozenCount": 1,
         "frozen": [{"orderId": "621e", "state": "CancelPending",
                     "ageSeconds": -5566, "clockSkew": True}]}])
    assert [a["name"] for a in st.affected()] == ["SimA"]
    assert st.affected()[0]["frozen"][0]["clockSkew"] is True


def test_only_accounts_with_ghosts_are_affected():
    assert [a["name"] for a in _st().affected()] == ["SimScalp", "FundedAcct"]


def test_a_non_simulator_provider_is_NEVER_resettable():
    """`Provider31` is what a real funded account reports. It is not 'Live', so a denylist would
    have admitted it - which is exactly why the check is an allowlist on Simulator."""
    st = _st()
    assert [a["name"] for a in st.resettable()] == ["SimScalp"]
    assert [a["name"] for a in st.needs_a_human()] == ["FundedAcct"]


def test_an_unknown_provider_falls_on_the_safe_side():
    st = _st(accounts=[{"name": "Mystery", "provider": "SomethingNew", "isSimulator": False,
                        "connectionStatus": "Connected", "orders": 1, "frozenCount": 1, "frozen": []}])
    assert st.resettable() == []
    assert [a["name"] for a in st.needs_a_human()] == ["Mystery"]


# ---- CLI exit codes ----

def _run(monkeypatch, capsys, payload, argv):
    monkeypatch.setattr(ntghost, "run_ghostorders", lambda **kw: payload)
    rc = cli.main(argv)
    return rc, json.loads(capsys.readouterr().out)


def test_clean_box_exits_zero(monkeypatch, capsys):
    rc, _ = _run(monkeypatch, capsys,
                 {"status": "ok", "totalFrozen": 0, "accounts": [], "reset": None},
                 ["ghostorders"])
    assert rc == 0


def test_ghosts_found_but_not_reset_exits_two(monkeypatch, capsys):
    rc, out = _run(monkeypatch, capsys,
                   {"status": "ok", "totalFrozen": 2, "reset": None,
                    "accounts": [{"name": "SimScalp", "isSimulator": True, "frozenCount": 2, "frozen": []}]},
                   ["ghostorders"])
    assert rc == 2
    assert out["affectedAccounts"] == ["SimScalp"]


def test_a_refused_reset_exits_one_not_two(monkeypatch, capsys):
    """1 means 'you asked for something I would not do'; 2 means 'the box has ghosts'. Collapsing
    them would let a refusal read as a mere detection and get ignored."""
    rc, _ = _run(monkeypatch, capsys,
                 {"status": "ok", "totalFrozen": 1, "accounts": [],
                  "reset": {"account": "FundedAcct", "performed": False,
                            "reason": "provider 'Provider31', not Simulator - refusing"}},
                 ["ghostorders", "--account", "FundedAcct", "--reset"])
    assert rc == 1


def test_a_reset_that_performed_but_did_not_clear_exits_two(monkeypatch, capsys):
    """`performed` means the CALL RETURNED. It is not evidence the ghosts went away.

    This test previously asserted rc == 0 here, which encoded the defect: on 2026-09-21
    `--reset` returned performed:true on two accounts while leaving all four orders wedged in
    CancelPending, the command exited 0 ("clean"), and ~80 minutes went to chasing the wrong
    cause. ResetSimulationAccount simply does not clear that state. The AddOn now re-reads the
    account after acting and reports `cleared`; the exit code follows that, not `performed`.
    """
    rc, _ = _run(monkeypatch, capsys,
                 {"status": "ok", "totalFrozen": 2, "accounts": [],
                  "reset": {"account": "SimScalp", "performed": True,
                            "stillStalledAfter": 2, "cleared": False, "reason": None}},
                 ["ghostorders", "--account", "SimScalp", "--reset"])
    assert rc == 2


def test_a_reset_that_actually_cleared_exits_zero(monkeypatch, capsys):
    rc, _ = _run(monkeypatch, capsys,
                 {"status": "ok", "totalFrozen": 2, "accounts": [],
                  "reset": {"account": "SimScalp", "performed": True,
                            "stillStalledAfter": 0, "cleared": True, "reason": None}},
                 ["ghostorders", "--account", "SimScalp", "--reset"])
    assert rc == 0


def test_cancel_that_cleared_exits_zero(monkeypatch, capsys):
    """--cancel is the preferred remedy: it reaches CancelPending, which flatten filters out."""
    rc, out = _run(monkeypatch, capsys,
                   {"status": "ok", "totalFrozen": 4, "accounts": [],
                    "cancel": {"account": "SimB", "attempted": 2, "orderIds": ["a", "b"],
                               "stillStalledAfter": 0, "cleared": True, "reason": None}},
                   ["ghostorders", "--account", "SimB", "--cancel"])
    assert rc == 0
    assert out["cancel"]["attempted"] == 2


def test_cancel_that_left_ghosts_exits_two(monkeypatch, capsys):
    rc, _ = _run(monkeypatch, capsys,
                 {"status": "ok", "totalFrozen": 4, "accounts": [],
                  "cancel": {"account": "SimB", "attempted": 2, "orderIds": ["a", "b"],
                             "stillStalledAfter": 2, "cleared": False, "reason": None}},
                 ["ghostorders", "--account", "SimB", "--cancel"])
    assert rc == 2


def test_a_refused_cancel_exits_one(monkeypatch, capsys):
    rc, _ = _run(monkeypatch, capsys,
                 {"status": "ok", "totalFrozen": 1, "accounts": [],
                  "cancel": {"account": "Nope", "attempted": 0, "orderIds": [],
                             "stillStalledAfter": -1, "cleared": False,
                             "reason": "account not found: Nope"}},
                 ["ghostorders", "--account", "Nope", "--cancel"])
    assert rc == 1


def test_cancel_without_account_is_refused_locally(monkeypatch, capsys):
    """Never cancel across every account on one keystroke."""
    rc, out = _run(monkeypatch, capsys, {"status": "ok", "totalFrozen": 0, "accounts": []},
                   ["ghostorders", "--cancel"])
    assert rc == 1
    assert "requires --account" in out["message"]


def test_live_ghosts_surface_the_manual_procedure(monkeypatch, capsys):
    """No programmatic fix exists for these, so a bare count would strand the operator."""
    rc, out = _run(monkeypatch, capsys,
                   {"status": "ok", "totalFrozen": 1, "reset": None,
                    "accounts": [{"name": "FundedAcct", "isSimulator": False, "frozenCount": 1, "frozen": []}]},
                   ["ghostorders"])
    assert rc == 2
    assert out["needsAHuman"] == ["FundedAcct"]
    assert "Database Management" in out["needsAHumanHint"]
