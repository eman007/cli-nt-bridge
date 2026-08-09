"""chart — list, attach/remove an indicator, close.

The reason this exists: `strategy add` alone does not reproduce a cell. A chart-derived sensor
computes from its OWN chart's bars, so the indicator set is part of the cell, not decoration.
"""
from nt8bridge import chart as ntchart


# ---- request shape ----

def test_list_request():
    req = ntchart.build_chart_request("id1")
    assert req["kind"] == "chart"
    assert req["action"] == "list"
    assert req["confirm"] == "false"


def test_add_indicator_request_carries_params():
    req = ntchart.build_chart_request("id2", "addIndicator", chart="NQ",
                                      type_name="SentinelTrend_v1_0_0", params={"Period": 14})
    assert req["action"] == "addIndicator"
    assert req["type"] == "SentinelTrend_v1_0_0"
    assert req["params"] == {"Period": "14"}


def test_close_needs_confirm_in_the_request():
    assert ntchart.build_chart_request("id3", "close")["confirm"] == "false"
    assert ntchart.build_chart_request("id4", "close", confirm=True)["confirm"] == "true"


def test_the_kind_value_does_not_collide_with_the_chart_field():
    """⭐ REGRESSION, found by driving it. The AddOn's key lookup took the first `"chart"` ANYWHERE
    and then hunted for the next ':' — so it matched the VALUE in `"kind":"chart"` and returned the
    following field. The chart filter silently became "list" and a box with three charts answered
    "no matching charts": confident, well-formed, and completely wrong.

    The wire shape that triggered it must stay covered here even though the fix is server-side."""
    req = ntchart.build_chart_request("id5", "list")
    assert req["kind"] == "chart"
    assert req["chart"] == ""
    assert list(req).index("kind") < list(req).index("chart")


# ---- listing ----

def _listing(**over):
    p = {
        "status": "ok", "action": "list", "succeeded": True, "count": 2,
        "charts": [
            {"title": "Chart - NQ 09-26", "instrument": "NQ 09-26 Globex", "barsPeriod": "1 Minute",
             "indicators": [{"name": "SentinelTrend", "state": "Realtime"}]},
            {"title": "Chart - ES 09-26", "instrument": "ES 09-26 Globex", "barsPeriod": "SaberRenko 5/5",
             "indicators": None},
        ],
        "notes": [], "errors": [],
    }
    p.update(over)
    return ntchart.parse_chart_response(p)


def test_describe_reports_instrument_and_indicator_count():
    assert "Chart - NQ 09-26 [NQ 09-26 Globex] 1 indicators" in _listing().describe()


def test_unreadable_indicators_are_not_reported_as_zero():
    """`null` is not `[]`. A chart we could not read and a chart with no indicators are different
    claims, and collapsing them is how 'the run produced nothing' gets excused as normal."""
    assert "unreadable" in _listing().describe()


def test_empty_listing_says_so():
    assert _listing(charts=[]).describe() == "no matching charts"


# ---- mutations verified by count ----

def test_add_that_raised_the_count_succeeded():
    st = ntchart.parse_chart_response({
        "status": "ok", "action": "addIndicator", "succeeded": True,
        "indicatorsBefore": 11, "indicatorsAfter": 12,
        "verdict": "addIndicator took — indicators 11 -> 12", "errors": [],
    })
    assert st.succeeded is True
    assert (st.before, st.after) == (11, 12)


def test_add_whose_count_did_not_move_is_a_failure():
    """⭐ Judged by the chart's own indicator count, not by the call returning. A reflection call
    that resolved and changed nothing is the exact failure this tool family exists to expose."""
    st = ntchart.parse_chart_response({
        "status": "ok", "action": "addIndicator", "succeeded": False,
        "indicatorsBefore": 11, "indicatorsAfter": 11,
        "verdict": "THE CALL RESOLVED BUT THE INDICATOR COUNT WENT 11 -> 11 — treat this as NOT applied",
    })
    assert st.succeeded is False
    assert "NOT applied" in st.verdict


def test_remove_lowers_the_count():
    st = ntchart.parse_chart_response({
        "status": "ok", "action": "removeIndicator", "succeeded": True,
        "indicatorsBefore": 12, "indicatorsAfter": 11, "verdict": "removeIndicator took", "errors": [],
    })
    assert st.succeeded and st.before > st.after


def test_ambiguous_chart_is_refused():
    st = ntchart.parse_chart_response({
        "status": "error", "action": "addIndicator", "succeeded": False, "charts": [],
        "errors": [{"code": "REFUSED",
                    "message": "AMBIGUOUS: 3 charts match — narrow --chart. Nothing was changed."}],
    })
    assert st.ok is False
    assert "Nothing was changed" in st.errors[0]["message"]


def test_unknown_indicator_type_points_at_the_class_name_trap():
    """A Sentinel tool blanks its own Name at DataLoaded — the on-chart label IS the Name — so the
    display name is usually the wrong thing to pass, and the error has to say so."""
    st = ntchart.parse_chart_response({
        "status": "error", "action": "addIndicator", "succeeded": False, "charts": [],
        "errors": [{"code": "NOTYPEFOUND",
                    "message": "no IndicatorBase subclass named 'Trend' is loaded — use the CLASS "
                               "name, not the display Name (a Sentinel tool blanks its Name)"}],
    })
    assert "CLASS name" in st.errors[0]["message"]


def test_close_that_left_the_window_registered_is_not_success():
    st = ntchart.parse_chart_response({
        "status": "ok", "action": "close", "succeeded": False,
        "verdict": "Close() RESOLVED BUT THE WINDOW IS STILL REGISTERED — treat as not closed",
    })
    assert st.succeeded is False


def test_notes_survive_into_the_result():
    st = _listing(notes=["chart did not answer within 5s"])
    assert st.notes == ["chart did not answer within 5s"]
