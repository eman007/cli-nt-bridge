"""Move the replay transport: seek, speed, and the connection range.

⭐⭐ WHY THE SEEK POLLS
    A full day was lost to a seek that "did not work". Every failing attempt reported back in 57 ms;
    every succeeding one took 5-7 SECONDS. `Reset` is ASYNCHRONOUS and walks the clock toward the
    target progressively — so reading the position immediately, declaring OFF-TARGET and aborting is
    what froze the seek mid-flight. The clock's own history was the proof and it was in the log the
    whole time, each attempt advancing and stopping:

        04-20 00:00:00 -> 04-21 03:06:14 -> 04-21 03:51:01

    always toward the target, never reaching it. So this command waits for the clock to SETTLE before
    rendering any verdict, and returns the whole trajectory it observed.

    The fleet currently runs a WORKAROUND for that failure (`seekTolMin = 0` plus `seekPauseFirst`),
    and TWO settings were changed at once, so the cause was never established. The trajectory is what
    establishes it: a seek that walks toward the target and stops short looks IDENTICAL to one that
    never moved if you only report the final position.

⛔ A SEEK IS RANGE-CHECKED, AND THAT CHECK WAS EARNED THE SAME DAY
    Writing the clock validates NOTHING. Driven against a real transport with 04-19..04-24 loaded, a
    seek to 2026-05-01 answered `succeeded: true, offset 0` — and it was telling the truth. The clock
    really did go there. There is simply no data there, so a bake started from that position produces
    nothing while every check reads green.

    That is a success-shaped nothing, the same family as a transport parked at 2099-12-01 passing a
    "ready" probe. So an out-of-range target now FAILS CLOSED and `force=True` is the only way past
    it, and every seek result carries the loaded range — "landed on target" means nothing without it.

⚠ THE RANGE IS THE FRAGILE PART
    ConnectOptions carries Start/End under obfuscated names, which is why programmatic connect was
    rejected once before. They are located BY TYPE rather than by name, every write is read back, and
    a write that cannot be verified reports failure rather than assuming it worked. Setting the range
    does NOT apply it — Playback must be reconnected, and the command says so.

    The range also does not survive an NT restart, and ConnectOnStartup re-connects with the stale
    one before a human can intervene. That is the tax this removes.

JSON contract (the in-NT8 AddOn honors):
  request : {"id","kind":"playbackctl","action":"api"|"seek"|"speed"|"range",
             "to","speed","start","end","settleMs","timeoutMs","confirm"}
  response: {"id","status","action","succeeded","verdict",...,"errors":[...]}
    seek adds: target, clockBefore, landedAt, offsetSec, via, settledAfterMs, timedOut, trajectory[]
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class PlaybackCtl:
    ok: bool
    action: str = "api"
    succeeded: bool = False
    verdict: str = ""
    trajectory: list[str] = field(default_factory=list)
    landed_at: str | None = None
    in_range: bool | None = None
    offset_sec: float | None = None
    timed_out: bool = False
    payload: dict = field(default_factory=dict)
    errors: list[dict] = field(default_factory=list)

    def moved(self) -> bool:
        """Did the clock go anywhere at all? Distinguishes a seek that walked and stopped short from
        one that never started — the two failures need opposite fixes."""
        return len(self.trajectory) > 1

    def describe(self) -> str:
        if self.action != "seek":
            return self.verdict or ("ok" if self.succeeded else "failed")
        if self.succeeded:
            return self.verdict
        if self.moved():
            return f"{self.verdict} (clock DID move — {len(self.trajectory)} positions seen)"
        return f"{self.verdict} (clock never moved — the seek call did nothing)"


def build_request(request_id: str, action: str, to: str | None = None, speed: int | None = None,
                  start: str | None = None, end: str | None = None, settle_ms: int = 1500,
                  timeout_ms: int = 60000, confirm: bool = False, force: bool = False) -> dict:
    return {
        "id": request_id,
        "kind": "playbackctl",
        "action": action,
        "to": to or "",
        # `is not None`, not truthiness: 0 is a real speed (a parked transport reads 0), and a
        # falsy-zero test silently sent an empty string, so the one value needed to PARK a
        # transport was the one value that could not be sent.
        "speed": str(speed) if speed is not None else "",
        "start": start or "",
        "end": end or "",
        "settleMs": str(settle_ms),
        "timeoutMs": str(timeout_ms),
        "confirm": "true" if confirm else "false",
        # Seeking outside the loaded range is refused by default: the clock really does move there,
        # reports success, and finds no data. --force is the deliberate way past that.
        "force": "true" if force else "false",
    }


def parse_response(payload: dict) -> PlaybackCtl:
    return PlaybackCtl(
        ok=payload.get("status") == "ok",
        action=payload.get("action", "api"),
        succeeded=bool(payload.get("succeeded")),
        verdict=payload.get("verdict", "") or "",
        trajectory=payload.get("trajectory") or [],
        landed_at=payload.get("landedAt"),
        in_range=payload.get("inRange"),
        offset_sec=payload.get("offsetSec"),
        timed_out=bool(payload.get("timedOut")),
        payload=payload,
        errors=payload.get("errors") or [],
    )


def run_playbackctl(action: str, to: str | None = None, speed: int | None = None,
                    start: str | None = None, end: str | None = None, settle_ms: int = 1500,
                    timeout_ms: int = 60000, confirm: bool = False, force: bool = False,
                    timeout: float | None = None) -> dict:
    """The client timeout must outlast the AddOn's own polling window, or a seek that is working
    correctly gets reported as a dead AddOn — the exact misreading this command exists to end."""
    if timeout is None:
        timeout = (timeout_ms / 1000.0) + 30.0 if action == "seek" else 30.0
    trigger, result = ntio.ensure_bridge_dirs()
    rid = new_request_id()
    ntio.atomic_write_json(
        trigger / f"playbackctl_{rid}.json",
        build_request(rid, action, to, speed, start, end, settle_ms, timeout_ms, confirm, force),
    )
    return ntio.poll_for_json(result / f"playbackctl_{rid}.json", timeout=timeout)
