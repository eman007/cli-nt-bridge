"""Order half of the IPC contract (request builder + runner).

⚠ THE HIGHEST-RISK VERB FAMILY IN THIS BRIDGE. Everything else here reads state or
drives a chart; this one can put real orders on a real account from a headless shell
with nobody at the keyboard. It is built refusal-first, and this cut is deliberately
READ-ONLY: `api` and `list` only.

Why read-only first — the mutating path needs three facts nobody has yet confirmed
against a live NT: the real `CreateOrder` overload, the real property that marks an
account as SIMULATED, and the real settled-state values. Guessing any of them yields
code that compiles, runs, reports success, and does something other than what was
asked — and here "something else" is an order. `order --api` exists to answer all
three from the running platform rather than from memory.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "order", "action": "api"|"list",
             "account": str, "instrument": str, "working": "true"|"false"}
  response: {"id": str, "status": "ok"|"error", "ts": str, "action": str,
             "orders": [...], "errors": [...]}
"""
from __future__ import annotations

from nt8bridge import ntio
from nt8bridge.compile import new_request_id

READ_ONLY_ACTIONS = ("api", "list", "status")
MUTATING_ACTIONS = ("place", "cancel", "change")
ACTIONS = READ_ONLY_ACTIONS + MUTATING_ACTIONS

# Discovered from a live NT via `order --api` (2026-08-10) rather than copied from
# documentation. Kept here so the client can reject a bad side/type before a request is
# ever written — a refusal at the CLI is cheaper to read than one that comes back from
# inside NinjaTrader, and it means a typo never reaches the order path at all.
SIDES = ("Buy", "Sell", "BuyToCover", "SellShort")
TYPES = ("Market", "Limit", "StopMarket", "StopLimit", "MIT")
TIFS = ("Day", "Gtc", "Ioc", "Opg", "Gtd")
NEEDS_LIMIT = ("Limit", "StopLimit")
NEEDS_STOP = ("StopMarket", "StopLimit", "MIT")


def build_order_request(request_id: str, action: str, **kw) -> dict:
    req = {"id": request_id, "kind": "order", "action": action}
    for k in ("account", "instrument", "side", "type", "tif", "orderId", "name", "oco"):
        req[k] = str(kw.get(k) or "")
    for k in ("quantity", "limitPrice", "stopPrice", "settle"):
        v = kw.get(k)
        req[k] = "" if v in (None, "") else str(v)
    req["working"] = "true" if kw.get("working", True) else "false"
    req["all"] = "true" if kw.get("all") else "false"
    req["confirm"] = "true" if kw.get("confirm") else "false"
    return req


def run_order(action: str = "list", timeout: float = 20.0, **kw) -> dict:
    """Send one order request and return the raw response payload.

    Client-side refusals mirror the AddOn's, deliberately. Two independent gates on the
    order path is not redundancy — it means neither side is the only thing standing
    between a typo and a live order, and the CLI one fires without touching NT at all.
    """
    if action not in ACTIONS:
        raise ValueError(f"unknown order action {action!r}; known: {', '.join(ACTIONS)}")

    if action in MUTATING_ACTIONS:
        if not kw.get("confirm"):
            raise ValueError(f"'{action}' mutates orders and requires --confirm")
        if not kw.get("account"):
            raise ValueError(f"'{action}' requires --name <account>; there is no default account")

    if action == "place":
        if not kw.get("instrument"):
            raise ValueError("place requires --instrument")
        if kw.get("side") not in SIDES:
            raise ValueError(f"place requires --side, one of: {', '.join(SIDES)}")
        if kw.get("type") not in TYPES:
            raise ValueError(f"place requires --type, one of: {', '.join(TYPES)}")
        qty = kw.get("quantity")
        if not isinstance(qty, int) or qty <= 0:
            raise ValueError("place requires --quantity as a positive integer")
        if kw.get("tif") and kw["tif"] not in TIFS:
            raise ValueError(f"--tif must be one of: {', '.join(TIFS)}")
        # A required price left at 0 turns a Limit into a market order in all but name.
        if kw["type"] in NEEDS_LIMIT and not kw.get("limitPrice"):
            raise ValueError(f"{kw['type']} requires --limit-price")
        if kw["type"] in NEEDS_STOP and not kw.get("stopPrice"):
            raise ValueError(f"{kw['type']} requires --stop-price")

    if action == "cancel" and not (kw.get("orderId") or kw.get("all")):
        raise ValueError("cancel requires --order-id, or --all to cancel every working order")
    if action == "change" and not kw.get("orderId"):
        raise ValueError("change requires --order-id")
    if action == "status" and not kw.get("orderId"):
        raise ValueError("status requires --order-id")

    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"order_{request_id}.json",
        build_order_request(request_id, action, **kw),
    )
    return ntio.poll_for_json(result / f"order_{request_id}.json", timeout=timeout)
