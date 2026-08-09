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


def build_connect_request(request_id: str, *, action: str, name: str,
                          confirm: bool, wait_ms: int) -> dict:
    """CONNECT or DISCONNECT a configured connection.

    `confirm` is required by the AddOn for `connect` and NOT for `disconnect`: raising a
    connection can arm an order-capable surface, while dropping one cannot, and the safe
    direction must never be the harder one to reach.

    The AddOn's ExtractJsonString reads only QUOTED values, so waitMs goes over as a string.
    """
    if action not in ("connect", "disconnect"):
        raise ValueError("action must be connect or disconnect")
    if not name:
        raise ValueError(f"{action} requires a connection name")
    return {"id": request_id, "kind": "connections", "action": action,
            "name": name, "confirm": bool(confirm), "waitMs": str(int(wait_ms))}


def run_connect(*, action: str, name: str, confirm: bool = False,
                wait_ms: int = 30000, timeout: float | None = None) -> dict:
    """Raise or drop a connection, and judge it by the STATUS THAT SETTLES, not by the call.

    The AddOn polls `Connection.Status` to a settled value before answering, so the client
    wait must outlast that poll window or a success reads as a timeout.
    """
    if timeout is None:
        timeout = wait_ms / 1000.0 + 20.0
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"connections_{request_id}.json",
        build_connect_request(request_id, action=action, name=name,
                              confirm=confirm, wait_ms=wait_ms),
    )
    return ntio.poll_for_json(result / f"connections_{request_id}.json", timeout=timeout)


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
