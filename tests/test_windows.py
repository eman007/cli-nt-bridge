"""windows.offscreen — what counts as an unreachable window, and what does not.

`--offscreen` exists to surface the one window you cannot get to. Its whole value is being a SHORT
list, so a false positive is not a cosmetic problem: it buries the real answer among ordinary windows.
Both classes below were measured against a live NinjaTrader with 67 windows open.
"""
from __future__ import annotations

import pytest

from nt8bridge import windows


def _win(**kw):
    base = {"left": 100, "top": 100, "width": 800, "height": 600,
            "visible": True, "minimized": False}
    base.update(kw)
    return base


def test_onscreen_window_is_not_offscreen():
    assert windows.offscreen(_win()) is False


@pytest.mark.parametrize("geom", [
    {"left": -32768}, {"top": -32768}, {"left": 99999}, {"top": 99999},
])
def test_parked_at_a_pathological_coordinate(geom):
    """short.MinValue / far past the desktop — a runaway move, not a placement."""
    assert windows.offscreen(_win(**geom)) is True


@pytest.mark.parametrize("geom", [
    {"left": -32000, "top": -32000, "width": 94, "height": 28},   # exactly how Windows parks one
    {"left": -32000, "top": -32000},
])
def test_minimized_window_is_not_offscreen(geom):
    """⚠ FOUND BY DRIVING IT against a live NinjaTrader.

    Windows parks a minimized window at (-32000, -32000) and it still reports visible=true, so the
    pathological-coordinate test classifies every minimized window as lost. Measured: 1 of 67 windows
    — modest here, but it scales with however many the user has minimized, and each is a false
    positive in the one list whose value is being short enough to act on. The taskbar is how you
    reach a minimized window; it is not unreachable."""
    assert windows.offscreen(_win(minimized=True, **geom)) is False


def test_a_genuinely_lost_window_still_reports():
    """The fix must not blunt the instrument it is sharpening."""
    assert windows.offscreen(_win(left=-32000, top=-32000, minimized=False)) is True


@pytest.mark.parametrize("geom", [
    {"left": -1920},                    # a monitor to the LEFT of the primary
    {"top": -1080},                     # a monitor ABOVE the primary
    {"left": -1920, "top": -1080},
    {"left": -40},                      # hanging slightly off an edge — normal
])
def test_negative_coordinates_are_normal_and_not_flagged(geom):
    """⚠ WHY THE THRESHOLD IS GENEROUS. Monitors left of or above the primary have NEGATIVE
    virtual-desktop coordinates, so a window on one is perfectly reachable at left=-1920. A tighter
    bound would report a stranger's second monitor as unreachable — a false positive on the most
    common multi-monitor layout there is."""
    assert windows.offscreen(_win(**geom)) is False


def test_invisible_window_is_never_reported():
    """Not visible is a different condition from not reachable; `windows` reports it separately."""
    assert windows.offscreen(_win(left=-32768, visible=False)) is False


def test_size_is_not_part_of_the_heuristic():
    """A deliberate gap, recorded as a test so it is a decision rather than a surprise: a 0x0 visible
    window is degenerate but is not off-screen, and offscreen() judges position only."""
    assert windows.offscreen(_win(width=0, height=0)) is False
