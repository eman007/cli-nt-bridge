"""Peek: read the SA tab's latest completed result + a read-back of the strategy template's
current NinjaScriptProperty inputs, WITHOUT firing a new Run. Read-only.

Use it to (a) capture a result the backtest watcher missed, and (b) verify that param
injection actually took (a wrong-params run otherwise looks like a valid result).
"""
from __future__ import annotations

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


def run_peek(timeout: float = 30.0) -> dict:
    """Drop a peek request for the in-NT8 AddOn and wait for its result payload.

    Returns the raw response: {id, status, metrics|null, params, ...}. Raises
    TimeoutError if the AddOn does not respond (NT8 down or AddOn not loaded).
    """
    trigger, result = ntio.ensure_bridge_dirs()
    rid = new_request_id()
    ntio.atomic_write_json(trigger / f"peek_{rid}.json", {"id": rid, "kind": "peek"})
    return ntio.poll_for_json(result / f"peek_{rid}.json", timeout=timeout)
