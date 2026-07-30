"""windows — inventory NinjaTrader's top-level windows.

Type, title, HWND, visibility, minimized/maximized, and screen geometry for every NT window.

WHY IT IS USEFUL
    • UI/AddOn work: know what is actually open without a screenshot.
    • Recovery: find windows dragged or thrown off-screen (a real hazard for any tool that moves
      windows) and see the coordinates that prove it.
    • Automation: assert "the Control Center is up and the chart is on the right monitor" before
      driving anything.

⚠ The NT-side handler is WIN32, not WPF, and must stay that way. NinjaTrader runs each window on its
own dispatcher thread, so reading `w.Left`/`.IsVisible` from the bridge's poller thread throws
`InvalidOperationException` on every window — a WPF implementation returns an empty list and looks
like "NinjaTrader has no windows".

CONTRACT
    request : {"id": str, "kind": "windows"}
    response: {"id","status","count","windows":[{hwnd,type,title,visible,minimized,maximized,
                                                 left,top,width,height}]}
"""
from __future__ import annotations

import uuid

from nt8bridge import ntio


def run_windows(timeout: float = 30.0) -> dict:
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = uuid.uuid4().hex
    ntio.atomic_write_json(
        trigger / f"windows_{request_id}.json",
        {"id": request_id, "kind": "windows"},
    )
    return ntio.poll_for_json(result / f"windows_{request_id}.json", timeout=timeout)


def offscreen(win: dict, vs_left=-32768, vs_top=-32768, vs_right=32767, vs_bottom=32767) -> bool:
    """Heuristic: is this window's title bar unreachable?

    Deliberately generous — the point is to catch the pathological cases (a window parked at
    short.MinValue by a runaway move, or pushed entirely past the desktop), not to police a window
    hanging slightly off an edge, which is normal and fine.
    """
    left, top = win.get("left", 0), win.get("top", 0)
    width = win.get("width", 0)
    if not win.get("visible", False):
        return False
    # ⚠ A MINIMIZED WINDOW IS NOT AN UNREACHABLE ONE. Windows parks minimized windows at
    # (-32000, -32000) by convention and they still report visible=true, so the coordinate test below
    # classifies every minimized window as lost. Measured against a live NinjaTrader: 1 of 67 windows,
    # a minimized chart — small here, but it scales with however many the user has minimized, and each
    # one is a false positive in the exact list whose only job is to be short enough to act on.
    # The taskbar is how you reach a minimized window; it is not unreachable.
    if win.get("minimized", False):
        return False
    if left <= -30000 or top <= -30000 or left >= 30000 or top >= 30000:
        return True
    return (left + width) < vs_left + 40 or left > vs_right - 40 or top > vs_bottom - 20
