"""Market Replay transport state (request builder + response parser).

WHY THIS EXISTS
    2026-08-02. A replay-equivalence run compared two boxes proven byte-identical in every input
    that had been made verifiable — code, replay `.nrd`, historical bars, and the serialised
    chart+strategy blob. It still diverged: one box's replay clock was parked at the loaded range
    start, the other's was 27 hours in and moving, so the same seek landed on one and silently
    no-opped on the other.

    Every input we could check was a FILE we could hash. The transport's own state lives only
    inside a running NinjaTrader — no file, no hash, no read-back — so it was the one input still
    set by hand, per box, at different moments. This command gives it a read-back.

WHAT `movingSec` IS FOR
    A single clock reading cannot tell a parked transport from a running one, and that distinction
    is precisely what broke the run. The AddOn samples NowEst twice a real gap apart and reports the
    delta, so the answer is measured rather than inferred.

WHAT `coverage` IS FOR
    `GetReplayMinMaxDates` per `.nrd`, straight from NT's own reader. This is NOT the Playback
    slider: the slider's bounds are the CONNECTION range you typed, not the indexed data, and
    reading it as proof that data was loaded cost hours on the same day.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "playback", "instrument": str|None}
  response: {"id": str, "status": "ok"|"error", "ts": str,
             "transportResolved": bool,
             "connection": {"status": str, "connected": bool},
             "clockEst": str|None, "clockEstFirst": str|None,
             "sampleMs": int, "movingSec": float, "moving": bool,
             "speed": int|None, "maxSpeedValue": int|None,
             "coverage": [{"instrument": str, "files": int, "readable": int, "unreadable": int,
                           "from": str|None, "to": str|None, "days": [...]}],
             "errors": [...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class PlaybackState:
    ok: bool
    connected: bool = False
    moving: bool = False
    clock_est: str | None = None
    speed: int | None = None
    coverage: list[dict] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)

    def span(self, instrument: str) -> tuple[str | None, str | None]:
        """(from, to) across every readable .nrd for `instrument`, or (None, None)."""
        for c in self.coverage:
            if c.get("instrument") == instrument:
                return c.get("from"), c.get("to")
        return None, None

    def clock_unset(self) -> bool:
        """Is the replay clock at NT's far-future 'nothing loaded' sentinel?

        A connected Playback with no replay data loaded reads back 2099-12-01 and speed 0 — which
        is *stationary*, and so passed the moving/not-moving test as READY on the first live run.
        A transport with no data is the emptiest possible kind of not-ready, and calling it ready
        is exactly the success-shaped nothing these commands exist to stop.
        """
        return bool(self.clock_est) and self.clock_est[:4] >= "2090"

    def ready_to_seek(self) -> tuple[bool, str]:
        """Is this transport in a state a seek can be trusted from?

        Measured, not assumed: a moving transport is the state in which Reset() has been observed
        to no-op. Returns (ok, why) so the caller can print the reason rather than a bare False.
        """
        if not self.ok:
            return False, "playback state unavailable"
        if not self.connected:
            return False, "playback connection is not connected"
        if self.clock_unset():
            return False, (f"replay clock reads {self.clock_est} — NT's 'no replay data loaded' "
                           "sentinel. Connected is not the same as loaded.")
        if self.moving:
            return False, f"replay clock is advancing (clock {self.clock_est}) — playback is running"
        if not any(c.get("readable", 0) for c in self.coverage):
            return False, "no readable .nrd files on disk — nothing to replay"
        return True, f"transport parked at {self.clock_est}"


def build_playback_request(request_id: str, instrument: str | None = None) -> dict:
    req = {"id": request_id, "kind": "playback"}
    if instrument:
        req["instrument"] = instrument
    return req


def parse_playback_response(payload: dict) -> PlaybackState:
    conn = payload.get("connection") or {}
    return PlaybackState(
        ok=payload.get("status") == "ok",
        connected=bool(conn.get("connected")),
        moving=bool(payload.get("moving")),
        clock_est=payload.get("clockEst"),
        speed=payload.get("speed"),
        coverage=payload.get("coverage", []),
        errors=payload.get("errors", []),
    )


def run_playback(instrument: str | None = None, timeout: float = 30.0) -> dict:
    """Read replay transport state from the in-NT8 AddOn.

    Returns the raw response payload. Raises TimeoutError if the AddOn does not respond — itself
    diagnostic: NT8 is down, or NT8BridgeServer is not loaded/compiled.

    Note the default timeout is higher than most read commands: the handler deliberately sleeps
    between its two clock samples, and coverage walks every .nrd on disk.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"playback_{request_id}.json",
        build_playback_request(request_id, instrument),
    )
    return ntio.poll_for_json(result / f"playback_{request_id}.json", timeout=timeout)
