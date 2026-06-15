"""Connection guardian: auto-reconnect connections that dropped INADVERTENTLY.

Out-of-band self-healing loop (sibling of watch.py for naked positions). Reads NT8
connection truth via the bridge; for each WATCHED connection that NT8 flagged as an
inadvertent drop (ConnectionLost / error-disconnect — NOT a clean user Disconnect),
once it stays down past a grace period (giving NT8's own auto-reconnect a chance), it
reconnects via the bridge, with exponential backoff and a cap so it never spins.

A connection the user explicitly parked is never touched: the AddOn classifies intent
from NT8's ConnectionStatusUpdate + ErrorCode, so a clean Disconnect / UserAbort reads
`inadvertentlyDropped: false` and is filtered out before this loop ever sees it.

Safety guards:
- Scoped to an explicit `connections` allow-list — never reconnects one you didn't name.
- Only acts on `inadvertentlyDropped` — user-parked connections are ignored.
- Grace period before the first attempt — lets NT8's built-in reconnect work first.
- Backoff + max attempts — never connect-storms; logs and gives up (surfaces) if a
  connection won't come back (e.g. an expired OAuth token needing a manual refresh).
"""
from __future__ import annotations

import json
import time

from nt8bridge import connections as ntconnections
from nt8bridge import ntio
from nt8bridge import reconnect as ntreconnect


def scan_once(connections: list[str], _state_fn=None) -> list[dict]:
    """One read of NT8 truth -> the watched connections that dropped inadvertently."""
    state_fn = _state_fn or (
        lambda: ntconnections.parse_connections_response(
            ntconnections.run_connections(timeout=15.0)
        )
    )
    state = state_fn()
    return state.inadvertently_dropped(connections)


def _backoff_seconds(attempt: int, base: float = 15.0, cap: float = 300.0) -> float:
    """Exponential backoff between reconnect attempts: base * 2^(attempt-1), capped."""
    if attempt <= 1:
        return base
    return min(cap, base * (2 ** (attempt - 1)))


def _log_event(event: dict) -> None:
    """Durable record so the operator sees every reconnect attempt + outcome."""
    line = json.dumps(event)
    try:
        path = ntio.bridge_dir() / "connwatch_events.jsonl"
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(line + "\n")
    except OSError:
        pass
    print("[connwatch] " + line, flush=True)


def watch(
    connections: list[str],
    grace_seconds: float = 20.0,
    interval: float = 10.0,
    max_attempts: int = 5,
    max_iterations: int | None = None,
    *,
    _scan=None,
    _reconnect=None,
    _sleep=time.sleep,
    _now=time.monotonic,
    _log=_log_event,
) -> list[dict]:
    """Run the connection guardian. Returns the list of reconnect events (also for tests).

    Underscore params are injection seams for tests; in production they default to the
    real scan/reconnect/sleep/clock.
    """
    if not connections:
        raise ValueError("connwatch requires at least one connection name")
    allow = list(connections)
    scan_fn = _scan or scan_once
    reconnect_fn = _reconnect or (lambda name: ntreconnect.run_reconnect(name))

    dropped_since: dict[str, float] = {}
    attempts: dict[str, int] = {}
    next_at: dict[str, float] = {}
    events: list[dict] = []
    it = 0
    while max_iterations is None or it < max_iterations:
        it += 1
        try:
            flagged_list = scan_fn(allow)
        except Exception as e:  # NT8/AddOn unreachable -> skip this round, retry next
            _log({"connection": None, "error": "scan failed: " + str(e)})
            flagged_list = None

        if flagged_list is not None:
            now = _now()
            flagged = {f["name"] for f in flagged_list}
            for name in allow:
                if name not in flagged:
                    # connected, parked, or absent -> reset its recovery state
                    dropped_since.pop(name, None)
                    attempts.pop(name, None)
                    next_at.pop(name, None)
                    continue
                first = dropped_since.setdefault(name, now)
                down_for = now - first
                if down_for < grace_seconds:
                    continue
                n = attempts.get(name, 0)
                if n >= max_attempts:
                    continue  # gave up already (surfaced below)
                if now < next_at.get(name, 0.0):
                    continue  # backing off
                try:
                    result = reconnect_fn(name)
                except Exception as e:
                    result = {"status": "error", "message": str(e)}
                attempts[name] = n + 1
                back = _backoff_seconds(n + 1)
                next_at[name] = now + back
                went_green = (result or {}).get("statusAfter") == "Connected"
                event = {
                    "connection": name,
                    "attempt": n + 1,
                    "down_for_s": round(down_for, 1),
                    "went_green": went_green,
                    "backoff_s": back,
                    "reason": "inadvertent drop past grace period",
                    "reconnect_result": result,
                }
                if not went_green and n + 1 >= max_attempts:
                    event["giving_up"] = True
                    event["note"] = (
                        "reconnect did not restore the connection after max attempts "
                        "- manual check needed (e.g. expired credentials)"
                    )
                events.append(event)
                _log(event)
                if went_green:
                    dropped_since.pop(name, None)
                    attempts.pop(name, None)
                    next_at.pop(name, None)

        if max_iterations is None or it < max_iterations:
            if interval > 0:
                _sleep(interval)
    return events
