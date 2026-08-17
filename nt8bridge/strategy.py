"""Chart strategies: see them, arm them, stop them, attach them — without a GUI click.

WHY THIS EXISTS
    A NinjaTrader workspace does NOT contain its strategy. It stores an integer handle; the type and
    every parameter live in db\\NinjaTrader.sqlite, a file that also holds Accounts, Orders and
    Positions and so must never be copied between machines. Staging the same cell on a second box
    therefore ended with a human re-adding the strategy by hand. Six workers, six hands — and
    anything needing a GUI click per box cannot run a matrix, which is the entire reason the fleet
    exists.

    Twice in one day a Playback toggle silently DISABLED a chart strategy and a replay ran for half
    an hour producing nothing. `workspace` could already see that. It could not fix it.

⛔ IT WILL NOT GUESS
    An ambiguous match is refused, never resolved by taking the first. `enable` and `add` attach or
    arm an ORDER SOURCE and additionally require `--confirm`; `disable` does not, because the safe
    direction must never be the harder one to reach.

⭐ REFLECT, THEN VERIFY THE OUTCOME
    These are not published API members and they can move between NT builds. So the strategy's State
    is re-read from the object AFTER the call, and `changed` reports what actually happened. A
    SetState that resolved and changed nothing reads as FAILURE here — which is the only thing that
    makes this safe to run unattended. Every failure this project keeps re-paying for is a call that
    resolved while doing nothing.

JSON contract (the in-NT8 AddOn honors):
  request : {"id","kind":"strategy","action":"list"|"enable"|"disable"|"add",
             "chart","name","type","confirm","params":{...}}
  response: {"id","status","ts","action","changed","count",
             "strategies":[{"chart","name","type","state","enabled"}],
             "stateBefore","stateAfter","via","verdict","notes":[str],"errors":[...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id

LIVE_STATES = ("Active", "Realtime", "Historical")


@dataclass
class StrategyState:
    ok: bool
    action: str = "list"
    # `changed` and `succeeded` answer DIFFERENT questions and conflating them lies both ways:
    # disabling an already-stopped strategy moves nothing yet succeeds, while a SetState that
    # resolved and left the state untouched moves nothing and FAILED. Gate on `succeeded`.
    succeeded: bool = False
    changed: bool = False
    strategies: list[dict] = field(default_factory=list)
    state_before: str | None = None
    state_after: str | None = None
    verdict: str = ""
    via: str = ""
    notes: list[str] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)
    payload: dict = field(default_factory=dict)

    def payload_reverted(self) -> bool:
        """Did the state reach the target and then go BACK? A reverted change is not a change, no
        matter how long the intermediate state was observed."""
        return bool(self.payload.get("reverted"))

    @property
    def enabled(self) -> list[dict]:
        return [s for s in self.strategies if s.get("enabled")]

    @property
    def disabled(self) -> list[dict]:
        """The dangerous ones: attached, visible in the workspace, and producing nothing."""
        return [s for s in self.strategies if not s.get("enabled")]

    def describe(self) -> str:
        if self.action in ("enable", "disable", "add"):
            return self.verdict or ("succeeded" if self.succeeded else "did NOT take effect")
        if not self.strategies:
            return "no strategies on any matching chart"
        return "; ".join(
            "%s on %s = %s" % (s.get("name") or s.get("type"), s.get("chart"), s.get("state"))
            for s in self.strategies)


def build_strategy_request(request_id: str, action: str = "list", chart: str | None = None,
                           name: str | None = None, type_name: str | None = None,
                           params: dict | None = None, confirm: bool = False,
                           hold_ms: int = 15000, mechanism: str = "auto",
                           index: int | None = None, force: bool = False) -> dict:
    req = {
        "id": request_id,
        "kind": "strategy",
        "action": action,
        "chart": chart or "",
        "name": name or "",
        "type": type_name or "",
        "confirm": "true" if confirm else "false",
        # The state must HOLD, not merely be observed once: a chart re-applying a strategy produces
        # a transient target state that a single sample reports as a durable change.
        "holdMs": str(hold_ms),
        # Which lever to pull. `auto` climbs a ladder — flag, flag+refresh, state machine, remove —
        # and stops at the first rung that HOLDS, so the answer is measured on this machine rather
        # than assumed. Named rungs exist so the question stays answerable when a build moves it.
        "mechanism": mechanism,
        # Which of several same-type matches to act on. Ambiguity is still REFUSED by default; this
        # makes the choice explicit rather than letting the tool pick for you.
        "index": "" if index is None else str(index),
        # --add refuses by default: measured on this build, a programmatic attach never binds to a
        # ChartBars, stays inert, and makes NT raise an Error dialog on an unattended box.
        "force": "true" if force else "false",
    }
    # Sent as a nested object because the AddOn's params parser reads a `"params": {...}` slice —
    # the same wire shape `configure` and `backtest` already use.
    req["params"] = {k: str(v) for k, v in (params or {}).items()}
    return req


def parse_params(pairs: list[str] | None) -> dict:
    """`--param Fast=14 --param UseStop=true` -> {"Fast": "14", "UseStop": "true"}.

    A token without '=' is an error rather than a silently dropped setting: a strategy that runs
    with the wrong parameters produces a result that looks entirely valid.
    """
    out: dict[str, str] = {}
    for raw in pairs or []:
        if "=" not in raw:
            raise ValueError(f"--param must be KEY=VALUE, got {raw!r}")
        k, v = raw.split("=", 1)
        k = k.strip()
        if not k:
            raise ValueError(f"--param has an empty key: {raw!r}")
        out[k] = v.strip()
    return out


def parse_strategy_response(payload: dict) -> StrategyState:
    return StrategyState(
        ok=payload.get("status") == "ok",
        action=payload.get("action", "list"),
        succeeded=bool(payload.get("succeeded")),
        changed=bool(payload.get("changed")),
        strategies=payload.get("strategies") or [],
        state_before=payload.get("stateBefore"),
        state_after=payload.get("stateAfter"),
        verdict=payload.get("verdict", "") or "",
        via=payload.get("via", "") or "",
        notes=payload.get("notes") or [],
        errors=payload.get("errors") or [],
        payload=payload,
    )


def run_strategy(action: str = "list", chart: str | None = None, name: str | None = None,
                 type_name: str | None = None, params: dict | None = None,
                 confirm: bool = False, timeout: float = 60.0, hold_ms: int = 15000,
                 mechanism: str = "auto", index: int | None = None, force: bool = False) -> dict:
    """Timeout is generous: attaching a strategy makes the chart load bars and run the state
    machine, which is real work on a busy UI thread."""
    trigger, result = ntio.ensure_bridge_dirs()
    rid = new_request_id()
    ntio.atomic_write_json(
        trigger / f"strategy_{rid}.json",
        build_strategy_request(rid, action, chart, name, type_name, params, confirm, hold_ms,
                               mechanism, index, force),
    )
    return ntio.poll_for_json(result / f"strategy_{rid}.json", timeout=timeout)
