"""Capture what a NinjaTrader node's screen actually says.

WHY THIS EXISTS
    2026-08-02, and it is a process failure rather than a code one. A replay would not seek on one
    node. Every diagnosis was made by asking the operator what was on their screen and reasoning
    about the answer — and the reasoning was wrong, repeatedly, because a relayed screen is a lossy
    channel. The Playback window and the Conductor panel were showing two DIFFERENT clock values the
    entire time and nobody caught it, because nobody was looking at both at once.

    A fleet of six headless replay workers cannot be operated by a human describing a window. This
    is the read-only sibling of `windows`: that command reports a window EXISTS, this one reports
    what it SAYS.

WHY THE CAPTURE HAPPENS INSIDE NT
    SSH lands in session 0, which owns no desktop. A capture taken from there is black — and black
    is worse than an error, because it looks like an answer. The AddOn runs in the interactive
    session where the pixels are.
"""
from __future__ import annotations

from pathlib import Path

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


def build_screenshot_request(request_id: str, title: str | None = None,
                             hwnd: int | None = None, out: str | None = None) -> dict:
    """Target precedence matches the AddOn: hwnd, then title, else the whole virtual screen."""
    req = {"id": request_id, "kind": "screenshot"}
    if hwnd is not None:
        req["hwnd"] = str(hwnd)
    elif title:
        req["title"] = title
    if out:
        req["out"] = out
    return req


def run_screenshot(title: str | None = None, hwnd: int | None = None,
                   out: str | None = None, timeout: float = 30.0) -> dict:
    """Capture on the node and return the payload (including the PNG path ON THAT NODE).

    Pulling the file back to the caller is a separate step — see `cli._screenshot` — because on a
    remote node that is an scp, and this module has no business knowing the transport.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"screenshot_{request_id}.json",
        build_screenshot_request(request_id, title, hwnd, out),
    )
    return ntio.poll_for_json(result / f"screenshot_{request_id}.json", timeout=timeout)


def looks_blank(path: str | Path) -> bool:
    """Cheap sanity check: is this PNG suspiciously small for its dimensions?

    A session-0 capture, an occluded window, or a failed render all produce a valid PNG file — so
    'the command succeeded' is not evidence that anything was captured. PNG compresses a uniformly
    black frame to almost nothing, so a few-KB file for a full window is the tell. This is a HINT,
    never a verdict: the answer is to look at the image.
    """
    try:
        return Path(path).stat().st_size < 8192
    except OSError:
        return True
