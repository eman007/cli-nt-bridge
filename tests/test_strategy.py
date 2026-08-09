"""strategy — list / enable / disable / add on a chart.

The load-bearing cases are the refusals and the outcome check. A tool that arms an order source on
an unattended box is only safe if it would rather return an error than act on a guess, and only
useful if a call that resolved while changing nothing reads as failure.
"""
import pytest

from nt8bridge import strategy as ntstrategy


# ---- request shape ----

def test_list_request_defaults():
    req = ntstrategy.build_strategy_request("id1")
    assert req["kind"] == "strategy"
    assert req["action"] == "list"
    assert req["confirm"] == "false"
    assert req["params"] == {}


def test_mutation_request_carries_confirm_and_params():
    req = ntstrategy.build_strategy_request("id2", "add", chart="NQ", type_name="SentinelKeel_v0_1_0",
                                            params={"Fast": 14, "UseStop": True}, confirm=True)
    assert req["action"] == "add"
    assert req["chart"] == "NQ"
    assert req["type"] == "SentinelKeel_v0_1_0"
    assert req["confirm"] == "true"
    # Everything crosses the wire as a string; the AddOn coerces to the property's real type.
    assert req["params"] == {"Fast": "14", "UseStop": "True"}


# ---- param parsing ----

def test_parse_params_splits_on_first_equals_only():
    assert ntstrategy.parse_params(["Fast=14", "Label=a=b"]) == {"Fast": "14", "Label": "a=b"}


def test_parse_params_rejects_a_token_without_equals():
    """A dropped parameter is worse than an error: the strategy runs, produces a perfectly
    plausible result, and it is the wrong result."""
    with pytest.raises(ValueError):
        ntstrategy.parse_params(["Fast14"])


def test_parse_params_rejects_an_empty_key():
    with pytest.raises(ValueError):
        ntstrategy.parse_params(["=14"])


def test_parse_params_of_nothing_is_empty():
    assert ntstrategy.parse_params(None) == {}


# ---- list ----

def _listing(**over):
    p = {
        "status": "ok", "action": "list", "changed": False, "count": 3,
        "strategies": [
            {"chart": "Chart - NQ 09-26", "name": "Sentinel Keel",
             "type": "SentinelKeel_v0_1_0", "state": "Realtime", "enabled": True},
            {"chart": "Chart - NQ 09-26", "name": "RangeFilterATRStrategy",
             "type": "RangeFilterATRStrategy", "state": "Finalized", "enabled": False},
            {"chart": "Chart - ES 09-26", "name": "SentinelBridge_v0_2_0",
             "type": "SentinelBridge_v0_2_0", "state": "Finalized", "enabled": False},
        ],
        "notes": [], "errors": [],
    }
    p.update(over)
    return ntstrategy.parse_strategy_response(p)


def test_enabled_and_disabled_are_separated():
    """The dangerous state is ATTACHED BUT NOT RUNNING: it shows up in the workspace, the replay
    advances, the panel looks healthy, and the corpus comes back empty an hour later."""
    st = _listing()
    assert [s["name"] for s in st.enabled] == ["Sentinel Keel"]
    assert len(st.disabled) == 2


def test_describe_names_chart_and_state():
    assert "Sentinel Keel on Chart - NQ 09-26 = Realtime" in _listing().describe()


def test_empty_listing_says_so():
    assert "no strategies" in _listing(strategies=[]).describe()


def test_notes_are_carried_not_swallowed():
    """A chart whose UI thread did not answer is a FACT. Without the note, 'no strategies' and
    'one window was busy' render identically."""
    st = _listing(notes=["chart window did not answer within 5s: Chart"])
    assert st.notes and "did not answer" in st.notes[0]


# ---- mutations: changed vs succeeded ----

def test_a_real_change_is_both_changed_and_succeeded():
    st = ntstrategy.parse_strategy_response({
        "status": "ok", "action": "disable", "succeeded": True, "changed": True,
        "stateBefore": "Realtime", "stateAfter": "Finalized",
        "verdict": "disabled — state moved Realtime -> Finalized", "errors": [],
    })
    assert st.succeeded and st.changed


def test_already_in_the_target_state_succeeds_without_changing():
    """Found by running this for real: a chart whose three strategies were all already Finalized.
    Reporting that as `changed` would have claimed an action that never happened."""
    st = ntstrategy.parse_strategy_response({
        "status": "ok", "action": "disable", "succeeded": True, "changed": False,
        "stateBefore": "Finalized", "stateAfter": "Finalized",
        "verdict": "already disabled (Finalized) — nothing to do", "errors": [],
    })
    assert st.succeeded is True
    assert st.changed is False
    assert "nothing to do" in st.describe()


def test_a_call_that_resolved_but_moved_nothing_is_a_failure():
    """⭐ THE RULE. SetState resolving proves only that a method ran. The 33-minute wasted cell and
    both silent strategy-disables were green results over a machine doing nothing."""
    st = ntstrategy.parse_strategy_response({
        "status": "ok", "action": "enable", "succeeded": False, "changed": False,
        "stateBefore": "Finalized", "stateAfter": "Finalized",
        "verdict": "THE CALL RESOLVED BUT THE STATE IS Finalized — treat this as NOT enabled",
        "errors": [],
    })
    assert st.ok is True            # the command itself ran
    assert st.succeeded is False    # ...and did not do the thing, which is what matters
    assert "NOT enabled" in st.verdict


# ---- the refusals ----

def test_ambiguous_match_is_refused():
    st = ntstrategy.parse_strategy_response({
        "status": "error", "action": "disable", "succeeded": False, "changed": False,
        "strategies": [], "errors": [{"code": "REFUSED",
                                      "message": "AMBIGUOUS: 2 strategies match — Nothing was changed."}],
    })
    assert st.ok is False
    assert "Nothing was changed" in st.errors[0]["message"]


def test_enable_without_confirm_is_refused():
    """Arming an automated order source on a substring match is not something to do unattended."""
    st = ntstrategy.parse_strategy_response({
        "status": "error", "action": "enable", "succeeded": False, "changed": False,
        "strategies": [], "errors": [{"code": "REFUSED",
                                      "message": "enable starts an ORDER SOURCE and requires --confirm."}],
    })
    assert st.ok is False
    assert "ORDER SOURCE" in st.errors[0]["message"]


def test_add_of_an_unknown_type_names_the_likely_mistake():
    st = ntstrategy.parse_strategy_response({
        "status": "error", "action": "add", "succeeded": False, "changed": False,
        "strategies": [], "errors": [{"code": "NOTYPEFOUND",
                                      "message": "no StrategyBase subclass named 'Keel' is loaded — "
                                                 "check the class name (not the display Name)"}],
    })
    assert st.ok is False
    assert "not the display Name" in st.errors[0]["message"]


def test_attached_but_not_running_is_not_a_successful_add():
    st = ntstrategy.parse_strategy_response({
        "status": "ok", "action": "add", "succeeded": False, "changed": False,
        "stateAfter": "Configure",
        "verdict": "ATTACHED BUT NOT RUNNING — state is Configure", "errors": [],
    })
    assert st.succeeded is False
    assert "NOT RUNNING" in st.verdict


# ---- the stale-reference false green (found on a live chart, after three failed "fixes") ----

def test_a_reverted_state_is_a_failure_however_long_it_held():
    """⭐⭐ THE SUBTLEST FALSE GREEN OF THE LOT. ChartControl.StrategyEnable does not flip a flag on
    the object you hand it — it TERMINATES that instance and the chart re-applies a NEW one. A
    verifier holding the original reference therefore watches a corpse: it reads Finalized and holds
    there forever, while the chart's actual strategy is a different object at Realtime. A
    45-SECOND hold "confirmed" a disable that never happened, and only an independent listing
    disagreed. Identity is the (type, chart) pair, never the pointer."""
    st = ntstrategy.parse_strategy_response({
        "status": "ok", "action": "disable", "succeeded": False, "changed": False,
        "reverted": True, "waitedMs": 30000,
        "stateBefore": "Realtime", "stateAfter": "Realtime",
        "via": "ChartControl.StrategyEnable(false) then SetState(Terminated)",
        "verdict": "IT DID NOT HOLD — the state reached the target and then went back to Realtime",
    })
    assert st.succeeded is False
    assert st.payload_reverted() is True


def test_absent_is_distinct_from_terminated():
    """A strategy no longer on the chart and a strategy sitting Finalized are different facts. The
    fresh lookup returns 'Absent' for the first so they cannot be confused."""
    st = ntstrategy.parse_strategy_response({
        "status": "ok", "action": "disable", "succeeded": True, "changed": True,
        "stateBefore": "Realtime", "stateAfter": "Absent", "reverted": False,
    })
    assert st.state_after == "Absent"


# ---- mechanism ladder / index / force (all earned on a live chart) ----

def test_mechanism_defaults_to_auto_and_accepts_named_rungs():
    assert ntstrategy.build_strategy_request("i")["mechanism"] == "auto"
    for rung in ("flag", "flag-refresh", "enable-call", "setstate", "remove"):
        assert ntstrategy.build_strategy_request("i", mechanism=rung)["mechanism"] == rung


def test_hold_ms_is_sent_because_a_short_hold_is_a_slow_false_green():
    assert ntstrategy.build_strategy_request("i")["holdMs"] == "15000"
    assert ntstrategy.build_strategy_request("i", hold_ms=45000)["holdMs"] == "45000"


def test_index_is_omitted_unless_asked_for():
    """Ambiguity stays REFUSED by default. --index makes the choice explicit rather than
    reintroducing 'just take the first'."""
    assert ntstrategy.build_strategy_request("i")["index"] == ""
    assert ntstrategy.build_strategy_request("i", index=1)["index"] == "1"
    assert ntstrategy.build_strategy_request("i", index=0)["index"] == "0"   # 0 is a real index


def test_add_is_refused_by_default():
    """⛔ MEASURED: an Activator-created strategy never binds to a ChartBars. It sits inert, cannot
    be started, survives RemoveStrategyForChartBars, and makes NT raise an Error dialog on an
    unattended box. Attaching is a UI operation on this build, so the tool refuses rather than
    leaving that trap armed."""
    assert ntstrategy.build_strategy_request("i", "add")["force"] == "false"
    st = ntstrategy.parse_strategy_response({
        "status": "error", "action": "add", "succeeded": False,
        "errors": [{"code": "UNSAFE",
                    "message": "programmatic attach does not work on this build: the instance never "
                               "binds to a ChartBars, stays inert, and NT raises an Error dialog."}],
    })
    assert st.ok is False
    assert st.errors[0]["code"] == "UNSAFE"


def test_removal_is_proved_by_count_not_by_state():
    """Two instances left at Configure were never bound to the ChartBars, so
    RemoveStrategyForChartBars had nothing to remove — and the state check read 'not live' and
    called it success while both were still on the chart."""
    st = ntstrategy.parse_strategy_response({
        "status": "ok", "action": "disable", "mechanism": "remove", "succeeded": False,
        "remaining": 1, "stateAfter": "Configure",
        "notes": ["RemoveStrategyForChartBars ran but 1 strategies of that name are still on the chart"],
    })
    assert st.succeeded is False
    assert any("still on the chart" in n for n in st.notes)
