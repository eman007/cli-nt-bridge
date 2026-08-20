"""strategies — read, and change, the enabled state of the Control Center's strategies.

WHY THIS EXISTS
    An explicit `Connection.Disconnect()` disables every running strategy, and NinjaTrader restores
    none of them — not on reconnect, and not on an app restart either. So the question "are my
    strategies actually running on that box right now?" was answerable only by an RDP session, and the
    answer "no" was fixable only by hand, per box, with the market open.

    The reach that fixes it was worked out for a connection-recovery AddOn, which has to put the
    strategies back after its own reconnect — but there it lived behind drop-a-marker-file-then-tail-
    a-log. This is the same capability as one command with a JSON contract, which is what makes it
    callable by anything other than a human reading a log.

WHY IT IS NOT `workspace`
    `workspace` walks the CHART windows, so it sees strategies attached to a chart. This walks the
    Control Center's Strategies tab — the other population, and the one a connection cycle turns off.
    Neither is a superset of the other; use `workspace` for "what is on this chart", this for "what is
    the platform running".

⚠ `enabled` IS NOT PROOF THE STRATEGY IS RUNNING
    `enabled` is the grid's own checkbox state: it says the click landed. The evidence that a strategy
    is live is its own `state` reaching `Realtime`. Both are returned on every row, and an acting call
    waits `settleMs` before re-reading precisely so `state` has had time to become true rather than
    transitional. Where the two disagree, believe `state` — this is the same rule `workspace` follows,
    and it is why the AddOn does not collapse them into one field.

⚠ DISABLING DOES NOT FLATTEN
    Turning a strategy off stops it managing what it is holding; the position and any working orders
    stay. `--disable` therefore refuses when the strategy's account has exposure on its instrument,
    and `--force` is the deliberate override. The check is on the ACCOUNT's position, not the
    strategy's own, because the strategy-level view can read flat while the account still carries the
    fill.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "strategies", "enable": "A,B", "disable": "C",
             "dryRun": bool, "force": bool, "settleMs": str}
  response: {"id": str, "status": "ok"|"error", "ts": str, "gridResolved": bool, "dryRun": bool,
             "strategies": [{"name","type","enabled","state","account","instrument"}]|None,
             "changed": [{"name","from","to","clicked","enabled","state"}],
             "skipped": [{"name","reason"}],
             "notes": [str], "errors": [...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id

# States in which NinjaTrader is actually running the strategy's logic. `Realtime` is the one that
# matters after an enable; the others are legitimate mid-transition readings, not successes.
LIVE_STATES = ("Realtime",)
TRANSITIONAL_STATES = ("Configure", "DataLoaded", "Historical", "Transition", "Active")


@dataclass
class StrategiesState:
    ok: bool
    grid_resolved: bool = False
    dry_run: bool = False
    strategies: list[dict] | None = None
    changed: list[dict] = field(default_factory=list)
    skipped: list[dict] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)

    def enabled(self) -> list[dict]:
        return [s for s in (self.strategies or []) if s.get("enabled")]

    def find(self, name_fragment: str) -> list[dict]:
        """Rows whose name OR type contains the fragment, case-insensitively.

        Fragment rather than exact match, for the same reason `workspace` does it: a version bump in
        the strategy name would otherwise turn the query into a silent no-match.
        """
        frag = name_fragment.lower()
        return [
            s for s in (self.strategies or [])
            if frag in (s.get("name") or "").lower() or frag in (s.get("type") or "").lower()
        ]

    def unsatisfied_skips(self) -> list[dict]:
        """Skips that mean the request was NOT carried out.

        `alreadyDisabled` is excluded outright, and `alreadyEnabled` is excluded when the row's own
        `state` agrees the strategy is live: asking to enable something already running is the normal
        shape of an idempotent caller, and reporting that the same as "refused, it holds a position"
        makes every retry look like a failure.

        `alreadyEnabled` on a row whose `state` is NOT live stays unsatisfied. An enabled checkbox
        above a `Terminated` strategy is precisely the box-looks-healthy case this command exists to
        catch, and the checkbox is the less trustworthy of the two readings.

        A skip with no `code` is treated as unsatisfied — an AddOn old enough not to send one cannot
        be assumed benign.
        """
        out = []
        for s in self.skipped:
            code = s.get("code")
            if code == "alreadyDisabled":
                continue
            if code == "alreadyEnabled":
                rows = [r for r in (self.strategies or []) if r.get("name") == s.get("name")]
                if rows and rows[0].get("state") in LIVE_STATES:
                    continue
            out.append(s)
        return out

    def unverified(self) -> list[dict]:
        """Rows we enabled whose `state` did not reach a live state before the response was written.

        This is NOT the same as "the enable failed" — a strategy loading historical data can be
        mid-transition when the settle expires. It means the command cannot yet claim success, which
        is a different report from claiming failure, and the caller should re-read rather than retry
        the click.
        """
        return [
            c for c in self.changed
            if c.get("to") is True and c.get("clicked") and c.get("state") not in LIVE_STATES
        ]


def build_strategies_request(
    request_id: str,
    enable: list[str] | None = None,
    disable: list[str] | None = None,
    dry_run: bool = False,
    force: bool = False,
    settle_ms: int = 3000,
) -> dict:
    req: dict = {"id": request_id, "kind": "strategies"}
    if enable:
        req["enable"] = ",".join(enable)
    if disable:
        req["disable"] = ",".join(disable)
    if dry_run:
        req["dryRun"] = True
    if force:
        req["force"] = True
    # A string, because the AddOn's dependency-free JSON reader only extracts QUOTED values for
    # anything that is not one of the handful of bare-boolean flags it special-cases.
    req["settleMs"] = str(int(settle_ms))
    return req


def parse_strategies_response(payload: dict) -> StrategiesState:
    return StrategiesState(
        ok=payload.get("status") == "ok",
        grid_resolved=bool(payload.get("gridResolved")),
        dry_run=bool(payload.get("dryRun")),
        # `null` is not `[]`: a grid we could not read and a grid with no strategies are different
        # claims, and collapsing them is how "nothing is running" gets reported for "we never looked".
        strategies=payload.get("strategies"),
        changed=payload.get("changed") or [],
        skipped=payload.get("skipped") or [],
        notes=payload.get("notes") or [],
        errors=payload.get("errors") or [],
    )


def run_strategies(
    enable: list[str] | None = None,
    disable: list[str] | None = None,
    dry_run: bool = False,
    force: bool = False,
    settle_ms: int = 3000,
    timeout: float = 60.0,
) -> dict:
    """Read the strategies grid, optionally clicking rows on the way.

    The default timeout is generous because the handler marshals onto the Control Center's own UI
    thread (twice more when it acts) and then deliberately sleeps `settle_ms` before re-reading. A
    busy Control Center answers slowly rather than not at all.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"strategies_{request_id}.json",
        build_strategies_request(request_id, enable, disable, dry_run, force, settle_ms),
    )
    return ntio.poll_for_json(result / f"strategies_{request_id}.json", timeout=timeout)
