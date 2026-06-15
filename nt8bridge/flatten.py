"""Force-close half of the IPC contract (request builder + runner).

Flattens an account's position(s) and cancels its working orders via the in-NT8
AddOn. An independent kill switch for a stranded/naked position a strategy has
lost track of — a separate transport from whatever status feed the strategy uses.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "flatten", "account": str, "instrument": str|""}
  response: {"id": str, "status": "ok"|"error", "ts": str, "account": str,
             "flattenCalled": bool, "ordersCancelled": int, "flattened": [str], "errors": [...]}
"""
from __future__ import annotations

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


def build_flatten_request(request_id: str, account: str, instrument: str = "") -> dict:
    return {"id": request_id, "kind": "flatten", "account": account, "instrument": instrument or ""}


def run_flatten(account: str, instrument: str = "", timeout: float = 20.0) -> dict:
    """Force-close `account` (optionally a single `instrument`). Returns the raw
    response payload. Requires an account name — refuses to flatten everything.
    """
    if not account:
        raise ValueError("flatten requires an account name")
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"flatten_{request_id}.json",
        build_flatten_request(request_id, account, instrument),
    )
    return ntio.poll_for_json(result / f"flatten_{request_id}.json", timeout=timeout)
