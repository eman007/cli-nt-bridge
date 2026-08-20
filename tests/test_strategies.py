"""strategies — reading and changing the Control Center's enabled state.

The distinctions under test are the ones that cost real time on the original connection-recovery
spike: a grid we could not read is not an empty grid, and a checkbox that ticked is not a strategy
that is running.
"""
import json

from nt8bridge import cli
from nt8bridge import strategies as ntstrategies


# ---- request shape ----

def test_read_only_request_carries_no_actions():
    req = ntstrategies.build_strategies_request("id1")
    assert req["kind"] == "strategies"
    assert "enable" not in req and "disable" not in req
    assert "dryRun" not in req and "force" not in req


def test_names_are_joined_and_flags_are_bare_booleans():
    """`dryRun`/`force` must be real JSON booleans: the AddOn's reader special-cases bare `true` for
    those keys, and would not see them as quoted strings."""
    req = ntstrategies.build_strategies_request(
        "id2", enable=["A", "B"], disable=["C"], dry_run=True, force=True, settle_ms=1500)
    assert req["enable"] == "A,B"
    assert req["disable"] == "C"
    assert req["dryRun"] is True
    assert req["force"] is True
    # settleMs is the opposite case — quoted, because ExtractJsonString only reads quoted values.
    assert req["settleMs"] == "1500"


# ---- parsing ----

def _st(**over):
    payload = {
        "status": "ok",
        "gridResolved": True,
        # Two rows on purpose: one whose display name differs from its type (the common case — a
        # strategy renamed on the grid), and one where they match. `find` has to handle both.
        "strategies": [
            {"name": "Morning Breakout", "type": "SampleBreakoutStrategy", "enabled": True,
             "state": "Realtime", "account": "Sim101", "instrument": "MNQ 09-26"},
            {"name": "SampleTrendStrategy", "type": "SampleTrendStrategy", "enabled": False,
             "state": "Terminated", "account": "Sim101", "instrument": "MES 09-26"},
        ],
        "changed": [], "skipped": [], "notes": [], "errors": [],
    }
    payload.update(over)
    return ntstrategies.parse_strategies_response(payload)


def test_enabled_filters_on_the_grid_bool():
    assert [s["name"] for s in _st().enabled()] == ["Morning Breakout"]


def test_find_matches_name_or_type_case_insensitively():
    assert len(_st().find("sampletrend")) == 1
    assert len(_st().find("samplebreakout")) == 1   # matched on type, whose name differs
    assert _st().find("nothing-like-this") == []


def test_unreadable_grid_is_not_an_empty_grid():
    """`null` and `[]` are different claims. Collapsing them reports "no strategies are running" for
    "we never managed to look", which is the failure mode this whole command exists to remove."""
    st = ntstrategies.parse_strategies_response(
        {"status": "error", "gridResolved": False, "strategies": None,
         "errors": [{"code": "BRIDGE", "message": "could not reach the Control Center StrategiesGrid"}]})
    assert st.strategies is None
    assert st.enabled() == []      # convenience view is still safe to call
    assert st.grid_resolved is False
    assert st.ok is False


# ---- the enabled/state distinction ----

def test_clicked_but_not_yet_realtime_is_unverified():
    """The checkbox ticking proves the click landed, not that the strategy is live. A strategy still
    walking Configure -> DataLoaded when the settle expired must not be reported as running."""
    st = _st(changed=[{"name": "SampleTrendStrategy", "from": False, "to": True,
                       "clicked": True, "enabled": True, "state": "DataLoaded"}])
    assert [c["name"] for c in st.unverified()] == ["SampleTrendStrategy"]


def test_realtime_is_verified():
    st = _st(changed=[{"name": "SampleTrendStrategy", "from": False, "to": True,
                       "clicked": True, "enabled": True, "state": "Realtime"}])
    assert st.unverified() == []


def test_a_disable_is_never_unverified_by_a_missing_realtime():
    """`unverified` asks "did the enable take". Terminated is the SUCCESS state for a disable, so
    reusing the live-state check for it would report every successful disable as a failure."""
    st = _st(changed=[{"name": "Morning Breakout", "from": True, "to": False,
                       "clicked": True, "enabled": False, "state": "Terminated"}])
    assert st.unverified() == []


def test_dry_run_changes_are_not_unverified():
    """Nothing was clicked, so there is nothing to verify — a dry run must not look like a failure."""
    st = _st(dryRun=True, changed=[{"name": "SampleTrendStrategy", "from": False, "to": True,
                                    "clicked": False, "enabled": False, "state": None}])
    assert st.unverified() == []
    assert st.dry_run is True


# ---- the exposure guard, as it surfaces to a caller ----

def test_refused_disable_arrives_as_a_skip_with_a_reason():
    st = _st(skipped=[{"name": "Morning Breakout", "code": "exposure",
                       "reason": "open position on Sim101 (Long 2 MNQ 09-26) — pass force to disable anyway"}])
    assert len(st.skipped) == 1
    assert "force" in st.skipped[0]["reason"]
    assert st.unsatisfied_skips() == st.skipped
    assert st.ok is True    # the command worked; it declined the action, which is not an error


# ---- idempotence: a no-op request is not a failure ----

def test_enabling_something_already_running_is_satisfied():
    """"Make sure X is on" is the normal shape of an unattended caller. If its no-op reported the
    same as a refusal, every retry would look like a failure and nothing could safely loop."""
    st = _st(skipped=[{"name": "Morning Breakout", "code": "alreadyEnabled",
                       "reason": "already enabled (state=Realtime)"}])
    assert st.unsatisfied_skips() == []


def test_enabled_but_terminated_is_not_satisfied():
    """The checkbox says enabled and the strategy says Terminated. Believing the checkbox here is the
    exact box-looks-healthy failure the command exists to catch, so the disagreement must surface."""
    st = _st(strategies=[{"name": "Zombie", "type": "Zombie", "enabled": True,
                          "state": "Terminated", "account": "Sim101", "instrument": "MNQ 09-26"}],
             skipped=[{"name": "Zombie", "code": "alreadyEnabled", "reason": "already enabled (state=Terminated)"}])
    assert [s["name"] for s in st.unsatisfied_skips()] == ["Zombie"]


def test_already_disabled_is_satisfied():
    st = _st(skipped=[{"name": "SampleTrendStrategy", "code": "alreadyDisabled", "reason": "already disabled"}])
    assert st.unsatisfied_skips() == []


def test_a_skip_without_a_code_is_not_assumed_benign():
    """An AddOn old enough not to send `code` cannot be read as reporting success."""
    st = _st(skipped=[{"name": "SampleTrendStrategy", "reason": "already enabled"}])
    assert len(st.unsatisfied_skips()) == 1


# ---- CLI exit codes ----
#
# The exit code IS the answer for an unattended caller; these pin the distinction between "running"
# and "clicked, but cannot show it running", which is the one worth getting wrong-proof.

def _run(monkeypatch, capsys, payload, argv):
    monkeypatch.setattr(ntstrategies, "run_strategies", lambda **kw: payload)
    rc = cli.main(argv)
    return rc, json.loads(capsys.readouterr().out)


def test_cli_clean_read_exits_zero(monkeypatch, capsys):
    rc, out = _run(monkeypatch, capsys,
                   {"status": "ok", "gridResolved": True, "strategies": [], "changed": [], "skipped": []},
                   ["strategies"])
    assert rc == 0
    assert out["command"] == "strategies"


def test_cli_unreachable_grid_exits_one(monkeypatch, capsys):
    rc, _ = _run(monkeypatch, capsys,
                 {"status": "error", "gridResolved": False, "strategies": None,
                  "errors": [{"code": "BRIDGE", "message": "could not reach the grid"}]},
                 ["strategies"])
    assert rc == 1


def test_cli_skipped_action_exits_two(monkeypatch, capsys):
    """A refused disable is not a crash and not a success. Reporting it as 0 is how a caller
    concludes the strategy is off when it is still running."""
    rc, out = _run(monkeypatch, capsys,
                   {"status": "ok", "gridResolved": True, "strategies": [], "changed": [],
                    "skipped": [{"name": "X", "code": "exposure",
                                 "reason": "open position on Sim101 — pass force"}]},
                   ["strategies", "--disable", "X"])
    assert rc == 2
    assert out["unsatisfied"][0]["name"] == "X"


def test_cli_enabling_an_already_running_strategy_exits_zero(monkeypatch, capsys):
    rc, out = _run(monkeypatch, capsys,
                   {"status": "ok", "gridResolved": True,
                    "strategies": [{"name": "X", "enabled": True, "state": "Realtime"}],
                    "changed": [],
                    "skipped": [{"name": "X", "code": "alreadyEnabled",
                                 "reason": "already enabled (state=Realtime)"}]},
                   ["strategies", "--enable", "X"])
    assert rc == 0
    assert "unsatisfied" not in out


def test_cli_enable_without_realtime_exits_two_and_says_to_re_read(monkeypatch, capsys):
    rc, out = _run(monkeypatch, capsys,
                   {"status": "ok", "gridResolved": True,
                    "strategies": [{"name": "X", "enabled": True, "state": "DataLoaded"}],
                    "changed": [{"name": "X", "from": False, "to": True, "clicked": True,
                                 "enabled": True, "state": "DataLoaded"}],
                    "skipped": []},
                   ["strategies", "--enable", "X"])
    assert rc == 2
    assert out["unverified"][0]["name"] == "X"
    assert "re-read" in out["unverifiedHint"]


def test_cli_enable_reaching_realtime_exits_zero(monkeypatch, capsys):
    rc, out = _run(monkeypatch, capsys,
                   {"status": "ok", "gridResolved": True,
                    "strategies": [{"name": "X", "enabled": True, "state": "Realtime"}],
                    "changed": [{"name": "X", "from": False, "to": True, "clicked": True,
                                 "enabled": True, "state": "Realtime"}],
                    "skipped": []},
                   ["strategies", "--enable", "X"])
    assert rc == 0
    assert "unverified" not in out


def test_cli_strategy_assertion_fails_on_enabled_but_not_realtime(monkeypatch, capsys):
    """`--strategy` asserts on `state`, not on the checkbox. A row that is `enabled` but sitting in
    Terminated is exactly the box-looks-healthy case this command exists to catch."""
    rc, out = _run(monkeypatch, capsys,
                   {"status": "ok", "gridResolved": True,
                    "strategies": [{"name": "SampleTrendStrategy", "type": "SampleTrendStrategy",
                                    "enabled": True, "state": "Terminated"}],
                    "changed": [], "skipped": []},
                   ["strategies", "--strategy", "sampletrend"])
    assert rc == 2
    assert out["strategyRunning"] is False
