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
import random
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


def _backoff_seconds(
    attempt: int,
    base: float = 15.0,
    cap: float = 300.0,
    jitter_frac: float = 0.0,
    rng=random.random,
) -> float:
    """Exponential backoff between reconnect attempts: base * 2^(attempt-1), capped.

    With jitter_frac > 0 the value is spread by +/- jitter_frac (rng() in [0,1); 0.5 = no
    change) so several connections that drop together don't retry in lockstep. Off by
    default so a bare call returns the deterministic schedule.
    """
    base_val = base if attempt <= 1 else min(cap, base * (2 ** (attempt - 1)))
    if jitter_frac:
        base_val = base_val * (1.0 + jitter_frac * (2.0 * rng() - 1.0))
    return base_val


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
    cooldown_seconds: float = 1800.0,
    connecting_recheck: float = 15.0,
    max_connecting_rechecks: int = 5,
    jitter_frac: float = 0.1,
    *,
    _scan=None,
    _reconnect=None,
    _sleep=time.sleep,
    _now=time.monotonic,
    _log=_log_event,
    _rng=random.random,
) -> list[dict]:
    """Run the connection guardian. Returns the list of reconnect events (also for tests).

    Recovery state machine, per watched connection:
    - "Connected"  -> green: reset state.
    - "Connecting" -> a connect is UNDERWAY (the connect routinely needs >1.5s to settle);
      it is NOT counted toward max_attempts. Re-check up to `max_connecting_rechecks` times
      (every `connecting_recheck` s); the scan detects the transition to Connected on its
      own. A "Connecting" that never settles is escalated to a real failed attempt so a
      stuck connect still surfaces.
    - anything else -> a failed attempt: backoff (with +/- `jitter_frac` spread) up to
      `max_attempts`, then COOL DOWN for `cooldown_seconds` and retry (a session-critical
      feed must keep being healed, e.g. once an expired token is refreshed) — rather than
      giving up permanently.

    Known boundaries (safe-by-design, documented so they're not mistaken for bugs):
    - The AddOn only classifies a drop as inadvertent if it saw the Connected->ConnectionLost
      transition live. A connection that was already down when the AddOn loaded (NT8 restart /
      AddOn reload) reads `inadvertentlyDropped:false` and is NOT auto-healed — this guardian
      recovers IN-SESSION drops, not startup-failed connections.
    - A drop that a provider surfaces as Disconnected+NoError is classified user-parked (not
      reconnected). This errs on the safe side (never reconnect something the user parked).

    Underscore params are injection seams for tests; in production they default to the
    real scan/reconnect/sleep/clock/rng.
    """
    if not connections:
        raise ValueError("connwatch requires at least one connection name")
    allow = list(connections)
    scan_fn = _scan or scan_once
    reconnect_fn = _reconnect or (lambda name: ntreconnect.run_reconnect(name))

    dropped_since: dict[str, float] = {}
    attempts: dict[str, int] = {}
    next_at: dict[str, float] = {}
    connecting: dict[str, int] = {}      # consecutive "Connecting" rechecks (a connect underway)
    cooldown_until: dict[str, float] = {}  # monotonic time a post-max-attempts cooldown ends
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
                    connecting.pop(name, None)
                    cooldown_until.pop(name, None)
                    continue
                first = dropped_since.setdefault(name, now)
                down_for = now - first
                if down_for < grace_seconds:
                    continue
                # Cooldown: after max_attempts failures, wait out the cooldown then reset and
                # retry (never a permanent give-up for a still-flagged session-critical feed).
                cu = cooldown_until.get(name)
                if cu is not None:
                    if now < cu:
                        continue
                    attempts[name] = 0
                    connecting[name] = 0
                    cooldown_until.pop(name, None)
                    next_at.pop(name, None)
                if now < next_at.get(name, 0.0):
                    continue  # backing off, or waiting for a connect to settle
                try:
                    result = reconnect_fn(name)
                except Exception as e:
                    result = {"status": "error", "message": str(e)}
                status_after = (result or {}).get("statusAfter")

                if status_after == "Connected":
                    event = {
                        "connection": name,
                        "attempt": attempts.get(name, 0) + 1,
                        "down_for_s": round(down_for, 1),
                        "went_green": True,
                        "reason": "inadvertent drop past grace period",
                        "reconnect_result": result,
                    }
                    events.append(event)
                    _log(event)
                    dropped_since.pop(name, None)
                    attempts.pop(name, None)
                    next_at.pop(name, None)
                    connecting.pop(name, None)
                    cooldown_until.pop(name, None)
                    continue

                if status_after == "Connecting" and connecting.get(name, 0) < max_connecting_rechecks:
                    connecting[name] = connecting.get(name, 0) + 1
                    next_at[name] = now + connecting_recheck
                    event = {
                        "connection": name,
                        "in_progress": True,
                        "connecting_recheck": connecting[name],
                        "down_for_s": round(down_for, 1),
                        "went_green": False,
                        "reason": "reconnect in progress (Connecting)",
                        "reconnect_result": result,
                    }
                    events.append(event)
                    _log(event)
                    continue

                # failed attempt (or a persistent "Connecting" that never settled)
                connecting[name] = 0
                n = attempts.get(name, 0)
                attempts[name] = n + 1
                back = _backoff_seconds(n + 1, jitter_frac=jitter_frac, rng=_rng)
                next_at[name] = now + back
                event = {
                    "connection": name,
                    "attempt": n + 1,
                    "down_for_s": round(down_for, 1),
                    "went_green": False,
                    "backoff_s": back,
                    "reason": "inadvertent drop past grace period",
                    "reconnect_result": result,
                }
                if attempts[name] >= max_attempts:
                    cooldown_until[name] = now + cooldown_seconds
                    event["cooling_down"] = True
                    event["cooldown_s"] = cooldown_seconds
                    event["note"] = (
                        "max reconnect attempts reached - cooling down "
                        + str(int(cooldown_seconds))
                        + "s before retrying (e.g. expired credentials needing a manual refresh)"
                    )
                events.append(event)
                _log(event)

        if max_iterations is None or it < max_iterations:
            if interval > 0:
                _sleep(interval)
    return events
