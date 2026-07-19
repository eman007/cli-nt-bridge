"""Feed guardian: detect a FROZEN-but-connected data feed and ALERT (detect-only).

The third NT8 failure mode, sibling to watch.py (naked positions) and connwatch.py
(dropped connections): a feed NT8 still reports `connected` but whose ticks have stopped
flowing (a feed can freeze mid-session while the live market keeps moving; every
drop-based safety net misses it because NT8 never flags a drop). Reads each
watched instrument's last-tick age via the bridge; a feed stale past a grace period is
FROZEN -> alert (durable jsonl + stdout).

Detect-only, deliberately (like the naked-position watchdog before RunFlatten shipped):
thawing a frozen feed needs an operator to cycle NT8 and your strategy must stop trading into
it — and a reconnect of an already-"connected" feed does not reliably thaw it. The job
here is to SURFACE the freeze that nothing else can see, fast.

Safety guards:
- Scoped to an explicit instrument allow-list.
- Grace period before the first alert (a brief gap is not a freeze).
- Re-alert cadence so a persistent freeze does not spam.
"""
from __future__ import annotations

import json
import time

from nt8bridge import feedhealth as ntfeedhealth
from nt8bridge import ntio


def scan_once(instruments: list[str], max_age_seconds: float = 10.0, _state_fn=None) -> list[dict]:
    """One read of NT8 truth -> the watched instruments whose feed is stale (frozen)."""
    state_fn = _state_fn or (
        lambda: ntfeedhealth.parse_feedhealth_response(
            ntfeedhealth.run_feedhealth(instruments, timeout=15.0)
        )
    )
    state = state_fn()
    return state.stale_feeds(max_age_seconds, allow=instruments)


def _log_event(event: dict) -> None:
    """Durable record so the operator sees every frozen-feed alert."""
    line = json.dumps(event)
    try:
        path = ntio.bridge_dir() / "feedwatch_events.jsonl"
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(line + "\n")
    except OSError:
        pass
    print("[feedwatch] *** FROZEN FEED *** " + line, flush=True)


def watch(
    instruments: list[str],
    grace_seconds: float = 20.0,
    interval: float = 5.0,
    realert_seconds: float = 30.0,
    max_age_seconds: float = 10.0,
    max_iterations: int | None = None,
    *,
    _scan=None,
    _sleep=time.sleep,
    _now=time.monotonic,
    _log=_log_event,
) -> list[dict]:
    """Run the feed guardian. Returns the list of alert events (also for tests).

    A watched instrument whose feed reads stale (frozen) for at least `grace_seconds`
    alerts, then re-alerts at most every `realert_seconds` while it stays frozen. A feed
    that goes fresh again clears its timer (so a later freeze re-alerts cleanly).

    Underscore params are injection seams for tests; in production they default to the
    real scan/sleep/clock/log.
    """
    if not instruments:
        raise ValueError("feedwatch requires at least one instrument")
    scan_fn = _scan or (lambda allow: scan_once(allow, max_age_seconds=max_age_seconds))

    frozen_since: dict[str, float] = {}
    last_alert: dict[str, float] = {}
    events: list[dict] = []
    it = 0
    while max_iterations is None or it < max_iterations:
        it += 1
        try:
            findings = scan_fn(instruments)
        except Exception:  # NT8 down / AddOn absent / read timeout -> skip this round, retry next
            findings = None
        if findings is None:
            if max_iterations is None or it < max_iterations:
                if interval > 0:
                    _sleep(interval)
            continue
        seen = set()
        now = _now()
        for f in findings:
            key = f.get("instrument")
            seen.add(key)
            first = frozen_since.setdefault(key, now)
            frozen_for = now - first
            if frozen_for >= grace_seconds and (now - last_alert.get(key, -1e9)) >= realert_seconds:
                event = {
                    "instrument": key,
                    "ageMs": f.get("ageMs"),
                    "lastPrice": f.get("lastPrice"),
                    "frozen_for_s": round(frozen_for, 1),
                    "reason": "feed connected but no tick past grace (frozen)",
                    "action": "DETECT_ONLY_ALERT",
                    "note": "cycle NT8 to thaw the feed; do not trade into a frozen chart",
                }
                events.append(event)
                _log(event)
                last_alert[key] = now
        # forget instruments whose feed recovered (fresh) so a later freeze re-alerts
        for key in list(frozen_since.keys()):
            if key not in seen:
                frozen_since.pop(key, None)
                last_alert.pop(key, None)
        if max_iterations is None or it < max_iterations:
            if interval > 0:
                _sleep(interval)
    return events
