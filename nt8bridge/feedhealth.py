"""Feed-health: detect a FROZEN-but-connected data feed (a 'dark' feed).

NT8 reports a connection `connected: true` even when its market data has gone dark
(no ticks flowing). connwatch only heals feeds NT8 flags as *dropped*, so a frozen
chart slips through every net (a chart can freeze mid-session while the live market keeps
moving, and the `connections` read still calls it connected the whole time). This asks the
in-NT8 AddOn for each watched instrument's last-tick AGE,
computed AddOn-side from a single clock (UtcNow - MarketData.Last.Time) so there is no
client/server skew or timezone ambiguity; a tick older than a threshold is a frozen feed
even though it is nominally connected.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "feedhealth", "instruments": "MNQ 09-26,NQ 09-26"}
  response: {"id": str, "status": "ok"|"error", "ts": str,
             "feeds": [{"instrument": str, "lastPrice": float|null,
                        "lastTickTime": str, "ageMs": int|null}],
             "errors": [...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class FeedHealthState:
    ok: bool
    feeds: list[dict] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)

    def feed(self, instrument: str) -> dict | None:
        """Return the named feed block, or None if not present."""
        for f in self.feeds:
            if f.get("instrument") == instrument:
                return f
        return None

    def stale_feeds(self, max_age_seconds: float, allow: list[str] | None = None) -> list[dict]:
        """Feeds whose last tick is older than max_age_seconds — a 'connected but dark'
        feed. A feed that has delivered no tick we can age (ageMs null) is treated as
        stale too: if we cannot confirm freshness, do not assume the feed is live.
        Optionally limited to the `allow` list.
        """
        out = []
        for f in self.feeds:
            name = f.get("instrument")
            if allow is not None and name not in allow:
                continue
            age_ms = f.get("ageMs")
            if age_ms is None:
                out.append(f)
            elif age_ms / 1000.0 > max_age_seconds:
                out.append(f)
        return out


def build_feedhealth_request(request_id: str, instruments: list[str]) -> dict:
    # comma-joined: instrument full names contain spaces (e.g. "MNQ 09-26") but never
    # commas, so the AddOn can split the single string value back into names.
    return {"id": request_id, "kind": "feedhealth", "instruments": ",".join(instruments)}


def parse_feedhealth_response(payload: dict) -> FeedHealthState:
    return FeedHealthState(
        ok=payload.get("status") == "ok",
        feeds=payload.get("feeds", []),
        errors=payload.get("errors", []),
    )


def run_feedhealth(instruments: list[str], timeout: float = 15.0) -> dict:
    """Read each instrument's last-tick age from the in-NT8 AddOn. Requires at least one
    instrument. Returns the raw response payload. Raises TimeoutError if the AddOn does
    not respond — itself diagnostic: NT8 is down, or the NT8BridgeServer AddOn is not
    loaded/compiled.
    """
    if not instruments:
        raise ValueError("feedhealth requires at least one instrument")
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"feedhealth_{request_id}.json",
        build_feedhealth_request(request_id, instruments),
    )
    return ntio.poll_for_json(result / f"feedhealth_{request_id}.json", timeout=timeout)
