"""Connection-state half of the IPC contract (request builder + response parser).

Reads every configured NinjaTrader connection's live status and whether it dropped
INADVERTENTLY (ConnectionLost / error-disconnect) vs was parked by the user (a clean
Disconnect / UserAbort). The in-NT8 AddOn classifies intent from NT8's own
ConnectionStatusUpdate event + ErrorCode; this is the read side.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "connections"}
  response: {"id": str, "status": "ok"|"error", "ts": str,
             "connections": [{"name": str, "status": str, "connected": bool,
                              "inadvertentlyDropped": bool}],
             "errors": [...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class ConnectionsState:
    ok: bool
    connections: list[dict] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)

    def connection(self, name: str) -> dict | None:
        """Return the named connection block, or None if not present."""
        for c in self.connections:
            if c.get("name") == name:
                return c
        return None

    def inadvertently_dropped(self, allow: list[str] | None = None) -> list[dict]:
        """Connections NT8 flagged as inadvertently dropped (ConnectionLost / error).

        A connection the user explicitly disconnected reads `inadvertentlyDropped:
        false` and is never returned here. Optionally limited to the `allow` list.
        """
        out = []
        for c in self.connections:
            if not c.get("inadvertentlyDropped"):
                continue
            if allow is not None and c.get("name") not in allow:
                continue
            out.append(c)
        return out


def build_connections_request(request_id: str) -> dict:
    return {"id": request_id, "kind": "connections"}


def parse_connections_response(payload: dict) -> ConnectionsState:
    return ConnectionsState(
        ok=payload.get("status") == "ok",
        connections=payload.get("connections", []),
        errors=payload.get("errors", []),
    )


def run_connections(timeout: float = 15.0) -> dict:
    """Read all configured connections + their live status from the in-NT8 AddOn.

    Returns the raw response payload. Raises TimeoutError if the AddOn does not
    respond — itself diagnostic: NT8 is down, or the NT8BridgeServer AddOn is not
    loaded/compiled.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"connections_{request_id}.json",
        build_connections_request(request_id),
    )
    return ntio.poll_for_json(result / f"connections_{request_id}.json", timeout=timeout)
