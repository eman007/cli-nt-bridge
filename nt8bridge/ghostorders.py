"""ghostorders — find the stuck yellow order lines, and clear them on a simulation account.

WHAT A GHOST ORDER IS
    NOT a working order. It is an order wedged in a TRANSITIONAL state that should have resolved in
    milliseconds and never did:

        OrderState in (CancelPending, ChangePending, Initialized)  AND  older than stale_seconds

    Those three all mean "NinjaTrader has asked the broker something and is still waiting". A Working
    order sitting for hours is normal and is not a ghost; a CancelPending sitting for two seconds is
    already wrong. Hence the default threshold of 2 seconds rather than something conservative.

    The predicate is lifted from the aiDuplicateAccountActions indicator, which has been diagnosing
    this on this desk far longer than the bridge has.

⚠ THE PROVIDER CHECK IS AN ALLOWLIST AND MUST STAY ONE
    Reset happens only when `Provider == Simulator`. It is tempting to write "not Live" instead —
    do not. Real funded accounts report an unnamed provider (observed as Provider31), so a denylist
    silently admits them, and this call wipes the account. An allowlist fails closed on anything it
    does not recognise, which is the only safe default when the downside is a live account.

⚠ LIVE ACCOUNTS HAVE NO EQUIVALENT, deliberately
    There is no supported programmatic reset for a live account, and this does not invent one. They
    are reported so you know they are affected, and refused. The manual path is: cancel externally,
    disconnect and reconnect every connection, and if the ghosts survive that,
    Control Center → Tools → Database Management → Reset DB, then restart NinjaTrader.

⚠ clear_trade_data DEFAULTS TO FALSE
    The vendor always passes `true`, which wipes the account's trade history. This desk's journal
    ingests sim fills, so silently destroying that record while "clearing a stuck order" would be a
    bad trade. Opt in when you actually want a clean slate.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "ghostorders", "account": str, "reset": bool,
             "clearTradeData": bool, "staleSeconds": str}
  response: {"id","status","ts","staleSeconds","totalFrozen",
             "accounts":[{name, provider, isSimulator, connectionStatus, orders, frozenCount,
                          frozen:[{orderId,name,instrument,state,action,quantity,ageSeconds}]}],
             "reset": {account, performed, clearTradeData, reason} | None,
             "notes":[str], "errors":[...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id

# The three states that mean "waiting on the broker". Anything else is healthy or finished.
TRANSITIONAL_STATES = ("CancelPending", "ChangePending", "Initialized")


@dataclass
class GhostOrdersState:
    ok: bool
    stale_seconds: int = 2
    total_frozen: int = 0
    accounts: list[dict] = field(default_factory=list)
    reset: dict | None = None
    cancel: dict | None = None
    notes: list[str] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)

    def affected(self) -> list[dict]:
        """Accounts that actually have ghosts, worst first."""
        return sorted(
            [a for a in self.accounts if a.get("frozenCount")],
            key=lambda a: a.get("frozenCount", 0), reverse=True,
        )

    def resettable(self) -> list[dict]:
        """Affected accounts a reset can actually fix — simulation accounts only."""
        return [a for a in self.affected() if a.get("isSimulator")]

    def needs_a_human(self) -> list[dict]:
        """Affected accounts we refuse to touch. These are the ones worth telling someone about:
        no programmatic fix exists, so a ghost here sits until a person intervenes."""
        return [a for a in self.affected() if not a.get("isSimulator")]


def build_ghostorders_request(
    request_id: str,
    account: str | None = None,
    reset: bool = False,
    clear_trade_data: bool = False,
    stale_seconds: int = 2,
    include_all: bool = False,
    cancel: bool = False,
) -> dict:
    req: dict = {"id": request_id, "kind": "ghostorders"}
    if account:
        req["account"] = account
    if reset:
        req["reset"] = True
    if cancel:
        req["cancel"] = True
    if clear_trade_data:
        req["clearTradeData"] = True
    if include_all:
        req["includeAll"] = True
    # A string: the AddOn's dependency-free reader only extracts QUOTED values for anything that is
    # not one of the handful of bare-boolean flags it special-cases.
    req["staleSeconds"] = str(int(stale_seconds))
    return req


def parse_ghostorders_response(payload: dict) -> GhostOrdersState:
    return GhostOrdersState(
        ok=payload.get("status") == "ok",
        stale_seconds=payload.get("staleSeconds", 2),
        total_frozen=payload.get("totalFrozen", 0),
        accounts=payload.get("accounts") or [],
        reset=payload.get("reset"),
        cancel=payload.get("cancel"),
        notes=payload.get("notes") or [],
        errors=payload.get("errors") or [],
    )


def run_ghostorders(
    account: str | None = None,
    reset: bool = False,
    clear_trade_data: bool = False,
    stale_seconds: int = 2,
    timeout: float = 45.0,
    include_all: bool = False,
    cancel: bool = False,
) -> dict:
    """Read ghost orders, and optionally CANCEL or reset ONE account to clear them.

    Prefer `cancel` over `reset`. On 2026-09-21 `reset` reported performed:true while changing
    nothing, four times across two accounts, and cost ~80 minutes of a live session:
    ResetSimulationAccount() does not clear an order wedged in CancelPending. `cancel` calls
    Account.Cancel() on exactly the STALLED orders the predicates flag -- which `flatten` can never
    reach, because it filters on IsWorkingState and CancelPending is deliberately not one.

    Both actions now RE-READ the account afterwards and report `stillStalledAfter` / `cleared`.
    `performed` only ever meant "the call returned"; it was never evidence of an outcome.

    Reset requires an explicit account name — the AddOn refuses a blanket reset, because "clear the
    stuck order" and "wipe every simulation account on the box" should never be one keystroke apart.

    `include_all` dumps every order the account holds, not just the ones the predicates flag. Reach
    for it when a chart shows a line that `frozen` does not explain — the predicates encode what we
    currently believe a ghost looks like, and that belief has already been wrong once.
    """
    if reset and not account:
        raise ValueError("reset requires --account; it will never reset every account")
    if cancel and not account:
        raise ValueError("cancel requires --account; it will never cancel across every account")
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"ghostorders_{request_id}.json",
        build_ghostorders_request(request_id, account, reset, clear_trade_data,
                                  stale_seconds, include_all, cancel),
    )
    return ntio.poll_for_json(result / f"ghostorders_{request_id}.json", timeout=timeout)
