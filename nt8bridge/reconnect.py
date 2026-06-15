"""Reconnect half of the IPC contract (request builder + runner).

Reconnects a configured NinjaTrader connection by name via the in-NT8 AddOn
(`Connection.Connect(savedOptions)` marshalled to the UI dispatcher). This is an
UNCONDITIONAL operator override — it reconnects whatever you name, even something
you'd parked. The inadvertent-only policy (only auto-reconnect connections that
dropped on their own) lives in the `connwatch` guardian, not here.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "reconnect", "connection": str}
  response: {"id": str, "status": "ok"|"error", "ts": str, "name": str,
             "wasConnected": bool, "connectAttempted": bool, "connectThrew": bool,
             "connectError": str, "statusAfter": str, "errors": [...]}
"""
from __future__ import annotations

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


def build_reconnect_request(request_id: str, connection: str) -> dict:
    return {"id": request_id, "kind": "reconnect", "connection": connection}


def run_reconnect(connection: str, timeout: float = 30.0) -> dict:
    """Reconnect `connection` by name. Requires a name (refuses an empty target).

    Returns the raw response payload. Raises TimeoutError if the AddOn does not
    respond (NT8 down or AddOn not loaded).
    """
    if not connection:
        raise ValueError("reconnect requires a connection name")
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"reconnect_{request_id}.json",
        build_reconnect_request(request_id, connection),
    )
    return ntio.poll_for_json(result / f"reconnect_{request_id}.json", timeout=timeout)
