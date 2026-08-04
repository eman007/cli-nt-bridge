"""layout — identity matching, fraction math, and the states that make an enumeration lie.

All of this is pure, and deliberately so: the AddOn half cannot be tested without a running
NinjaTrader, so every judgement it would otherwise make lives here instead and is asserted offline.

The cases that matter most are the SILENT ones — a cloaked window that looks alive, a minimized
window reporting the park coordinate, a class key that changes every launch. Each of those, left
unhandled, produces a layout that is confidently wrong rather than obviously broken.
"""
from __future__ import annotations

import pytest

from nt8bridge import layout


def _mon(i=0, x=0, y=0, w=1920, h=1040, primary=True):
    return {"id": i, "primary": primary, "work": {"x": x, "y": y, "w": w, "h": h}}


def _win(hwnd=1, title="Chart - NQ 06-26", cls="HwndWrapper[NinjaTrader.exe;UI thread 1;abc-123]",
         x=0, y=0, w=960, h=520, monitor=0, **kw):
    d = {"hwnd": hwnd, "title": title, "class": cls, "monitor": monitor,
         "visible": True, "minimized": False, "maximized": False,
         "cloaked": False, "owned": False, "toolWindow": False,
         "visual": {"x": x, "y": y, "w": w, "h": h},
         "restored": {"x": x, "y": y, "w": w, "h": h}}
    d.update(kw)
    return d


def _state(windows, monitors=None):
    return layout.LayoutState(ok=True, monitors=monitors or [_mon()], windows=windows)


# ── class key: the per-launch GUID ──────────────────────────────────────────────────────────────

def test_wpf_class_guid_is_stripped():
    a = "HwndWrapper[NinjaTrader.exe;UI thread 1;ea69f969-c071-440d-9bdf-30a3ea922e1e]"
    b = "HwndWrapper[NinjaTrader.exe;UI thread 7;99999999-0000-1111-2222-333333333333]"
    # Same app, different launch. If these do not collapse to one key, a layout cannot survive the
    # restart it exists to survive.
    assert layout.class_key(a) == layout.class_key(b) == "HwndWrapper[NinjaTrader.exe]"


def test_non_wpf_class_is_left_alone():
    assert layout.class_key("Chrome_WidgetWin_1") == "Chrome_WidgetWin_1"
    assert layout.class_key("") == ""
    assert layout.class_key(None) == ""


# ── title key ───────────────────────────────────────────────────────────────────────────────────

@pytest.mark.parametrize("title,expected", [
    ("Chart - NQ 06-26", "Chart"),
    ("Sentinel Cockpit", "Sentinel Cockpit"),
    ("NinjaScript Editor - New tab", "NinjaScript Editor"),
    ("Now!!!!!! — Strategies", "Now!!!!!!"),
    ("", ""),
])
def test_title_key(title, expected):
    assert layout.title_key(title) == expected


def test_title_that_is_only_a_separator_keeps_something():
    # Degenerate input must not produce an empty key that then matches everything.
    assert layout.title_key(" - x") == " - x".strip()


# ── the states that make an enumeration lie ─────────────────────────────────────────────────────

def test_cloaked_window_is_not_placeable():
    """A suspended UWP window reports visible=True, minimized=False, parked at -32000.

    Measured on a real desktop: 28 of 37 enumerated "windows" were in this state. Only DWM
    distinguishes them, and without that check a captured layout is mostly junk.
    """
    st = _state([_win(hwnd=1, cloaked=True, x=-31990, y=-32000, w=76, h=28),
                 _win(hwnd=2)])
    assert [w["hwnd"] for w in st.placeable()] == [2]


def test_tool_and_owned_windows_are_not_placeable():
    st = _state([_win(hwnd=1, toolWindow=True), _win(hwnd=2, owned=True), _win(hwnd=3)])
    assert [w["hwnd"] for w in st.placeable()] == [3]


def test_stub_sized_window_is_not_placeable():
    st = _state([_win(hwnd=1, w=20, h=10), _win(hwnd=2)])
    assert [w["hwnd"] for w in st.placeable()] == [2]


def test_minimized_window_uses_its_restored_rect_not_the_park_coordinate():
    """The bug this exists to prevent: recording -32000 and later flinging the window off-screen."""
    w = _win(hwnd=1, minimized=True)
    w["visual"] = {"x": -31990, "y": -32000, "w": 76, "h": 28}
    w["restored"] = {"x": 100, "y": 50, "w": 800, "h": 600}
    assert layout.geometry_of(w) == {"x": 100, "y": 50, "w": 800, "h": 600}
    assert layout.state_of(w) == "minimized"


def test_minimized_window_with_no_restored_rect_falls_back_rather_than_crashing():
    w = _win(hwnd=1, minimized=True)
    w["restored"] = {"x": 0, "y": 0, "w": 0, "h": 0}
    assert layout.geometry_of(w) == w["visual"]


# ── capture ─────────────────────────────────────────────────────────────────────────────────────

def test_capture_records_fractions_not_pixels():
    st = _state([_win(hwnd=1, x=480, y=260, w=960, h=520)], [_mon(w=1920, h=1040)])
    doc = layout.capture(st, "cell", now="2026-08-04T00:00:00Z")
    assert doc["schema"] == layout.SCHEMA
    assert doc["windows"][0]["frac"] == {"x": 0.25, "y": 0.25, "w": 0.5, "h": 0.5}


def test_capture_handles_a_monitor_with_a_negative_origin():
    # Monitors left of the primary have negative coordinates; treating that as invalid would drop
    # every window on them.
    st = _state([_win(hwnd=1, x=-4096, y=0, w=2048, h=520, monitor=1)],
                [_mon(0), _mon(1, x=-4096, y=0, w=4096, h=1040, primary=False)])
    doc = layout.capture(st, "cell")
    assert doc["windows"][0]["frac"]["x"] == 0.0
    assert doc["windows"][0]["frac"]["w"] == 0.5


def test_capture_writes_the_normalised_class_key_into_the_file():
    # The other producer of this schema is a separate tool in another language. Writing the key
    # rather than the rule is what stops the two implementations drifting apart.
    st = _state([_win(hwnd=1)])
    doc = layout.capture(st, "cell")
    assert doc["windows"][0]["classKey"] == "HwndWrapper[NinjaTrader.exe]"


# ── apply ───────────────────────────────────────────────────────────────────────────────────────

def _doc(windows, monitors=None):
    return {"schema": layout.SCHEMA, "name": "t", "monitors": monitors or [_mon()], "windows": windows}


def test_apply_matches_across_a_restart_when_only_the_guid_changed():
    doc = _doc([{"titleKey": "Sentinel Cockpit", "classKey": "HwndWrapper[NinjaTrader.exe]",
                 "monitor": 0, "state": "normal",
                 "frac": {"x": 0.5, "y": 0.0, "w": 0.5, "h": 1.0}}])
    # Same window, next launch: new HWND, new GUID in the class.
    st = _state([_win(hwnd=987654, title="Sentinel Cockpit",
                      cls="HwndWrapper[NinjaTrader.exe;UI thread 9;ffffffff-0000-0000-0000-000000000000]")])
    plan, problems = layout.plan_apply(doc, st)
    assert problems == []
    assert plan == [{"hwnd": 987654, "title": "Sentinel Cockpit",
                     "x": 960, "y": 0, "w": 960, "h": 1040, "state": "normal"}]


def test_apply_refuses_an_unknown_schema():
    plan, problems = layout.plan_apply({"schema": "something/9", "windows": []}, _state([_win()]))
    assert plan == []
    assert "unrecognised schema" in problems[0]["reason"]


def test_an_ambiguous_match_is_reported_not_guessed():
    """Two charts share a class key and a title key. Picking one is a coin flip with a real screen."""
    doc = _doc([{"titleKey": "Chart", "classKey": "HwndWrapper[NinjaTrader.exe]",
                 "monitor": 0, "state": "normal", "frac": {"x": 0, "y": 0, "w": 0.5, "h": 0.5}}])
    st = _state([_win(hwnd=1, title="Chart - NQ 06-26"), _win(hwnd=2, title="Chart - GC 12-26")])
    plan, problems = layout.plan_apply(doc, st)
    assert plan == []
    assert "ambiguous" in problems[0]["reason"]


def test_class_alone_is_not_enough_to_match():
    doc = _doc([{"titleKey": "Control Center", "classKey": "HwndWrapper[NinjaTrader.exe]",
                 "monitor": 0, "state": "normal", "frac": {"x": 0, "y": 0, "w": 1, "h": 1}}])
    st = _state([_win(hwnd=1, title="Superdom")])   # right class, wrong window
    plan, problems = layout.plan_apply(doc, st)
    assert plan == []
    assert problems[0]["reason"] == "no live window matched"


def test_one_live_window_cannot_satisfy_two_placements():
    doc = _doc([
        {"titleKey": "Sentinel Cockpit", "classKey": "HwndWrapper[NinjaTrader.exe]",
         "monitor": 0, "state": "normal", "frac": {"x": 0, "y": 0, "w": 0.5, "h": 1}},
        {"titleKey": "Sentinel Cockpit", "classKey": "HwndWrapper[NinjaTrader.exe]",
         "monitor": 0, "state": "normal", "frac": {"x": 0.5, "y": 0, "w": 0.5, "h": 1}},
    ])
    st = _state([_win(hwnd=7, title="Sentinel Cockpit")])
    plan, problems = layout.plan_apply(doc, st)
    assert len(plan) == 1
    assert any(p["reason"] == "no live window matched" for p in problems)


def test_a_missing_monitor_falls_back_to_primary_and_says_so():
    """A three-monitor arrangement applied to a one-monitor VM must not silently heap up."""
    doc = _doc([{"titleKey": "Sentinel Cockpit", "classKey": "HwndWrapper[NinjaTrader.exe]",
                 "monitor": 2, "state": "normal", "frac": {"x": 0, "y": 0, "w": 1, "h": 1}}])
    st = _state([_win(hwnd=1, title="Sentinel Cockpit")], [_mon(0)])
    plan, problems = layout.plan_apply(doc, st)
    assert len(plan) == 1
    note = [p for p in problems if p.get("severity") == "note"]
    assert note and "monitor 2 absent" in note[0]["reason"]


def test_fractions_resolve_against_the_TARGET_box_not_the_source():
    """The whole point of fractions: same file, different screen, same arrangement."""
    doc = _doc([{"titleKey": "Sentinel Cockpit", "classKey": "HwndWrapper[NinjaTrader.exe]",
                 "monitor": 0, "state": "normal", "frac": {"x": 0.5, "y": 0.5, "w": 0.5, "h": 0.5}}],
               [_mon(w=2560, h=1440)])
    st = _state([_win(hwnd=1, title="Sentinel Cockpit")], [_mon(w=1920, h=1080)])
    plan, _ = layout.plan_apply(doc, st)
    assert (plan[0]["x"], plan[0]["y"], plan[0]["w"], plan[0]["h"]) == (960, 540, 960, 540)


def test_minimized_state_travels_so_apply_does_not_pop_windows_open():
    doc = _doc([{"titleKey": "Sentinel Cockpit", "classKey": "HwndWrapper[NinjaTrader.exe]",
                 "monitor": 0, "state": "minimized", "frac": {"x": 0, "y": 0, "w": 0.5, "h": 0.5}}])
    st = _state([_win(hwnd=1, title="Sentinel Cockpit")])
    plan, _ = layout.plan_apply(doc, st)
    assert plan[0]["state"] == "minimized"


# ── wire format ─────────────────────────────────────────────────────────────────────────────────

def test_format_place_is_what_the_addon_parses():
    plan = [{"hwnd": 123, "x": 1, "y": 2, "w": 3, "h": 4, "state": "normal"},
            {"hwnd": 456, "x": -5, "y": 6, "w": 7, "h": 8, "state": "maximized"}]
    assert layout.format_place(plan) == "123,1,2,3,4,normal;456,-5,6,7,8,maximized"


def test_format_place_of_an_empty_plan_is_empty_not_a_stray_separator():
    # A stray ";" would reach the AddOn as a malformed record and be reported as a failure.
    assert layout.format_place([]) == ""


def test_request_shape():
    assert layout.build_layout_request("abc") == {"id": "abc", "kind": "layout", "place": ""}
    assert layout.build_layout_request("abc", "1,2,3,4,5,normal")["place"] == "1,2,3,4,5,normal"


def test_parse_response_carries_the_failures():
    st = layout.parse_layout_response({"status": "ok", "placed": 2, "failed": ["9 (dead hwnd)"],
                                       "monitors": [_mon()], "windows": []})
    assert st.ok and st.placed == 2 and st.failed == ["9 (dead hwnd)"]
