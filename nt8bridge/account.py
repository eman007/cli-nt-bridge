"""Account-state half of the IPC contract (request builder + response parser).

An independent, out-of-band read of NinjaTrader's own Account objects: open
positions, working orders, realized/unrealized P&L, and recent completed trades.

This is a SEPARATE transport from whatever automation feeds your strategy. When an
upstream status/position substream stalls (e.g. NT8 stops pushing position updates and
a winning trade gets recorded as a $0 placeholder), this reader still answers the
load-bearing questions directly from NT8's truth: is a position actually open, and what
was a trade's real fill?

JSON contract (the in-NT8 AddOn must honor):
  request : {"id": str, "kind": "account", "account": str|""}   # ""=all accounts
  response: {"id": str, "status": "ok"|"error", "ts": str,
             "accounts": [{"name": str,
                           "realizedPnl": float|None, "unrealizedPnl": float|None,
                           "positions":       [{"instrument","marketPosition","quantity","avgPrice","unrealizedPnl"}],
                           "workingOrders":   [{"instrument","action","type","quantity","limitPrice","stopPrice","name","state"}],
                           "recentExecutions":[{"instrument","marketPosition","quantity","price","time","commission","orderName"}]}],
             "errors": [...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class AccountState:
    ok: bool
    accounts: list[dict] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)

    def account(self, name: str) -> dict | None:
        """Return the named account block, or None if not present."""
        for a in self.accounts:
            if a.get("name") == name:
                return a
        return None


def build_account_request(request_id: str, account: str = "") -> dict:
    return {"id": request_id, "kind": "account", "account": account or ""}


def parse_account_response(payload: dict) -> AccountState:
    return AccountState(
        ok=payload.get("status") == "ok",
        accounts=payload.get("accounts", []),
        errors=payload.get("errors", []),
    )


def run_account_state(account: str = "", timeout: float = 15.0) -> dict:
    """Drop an account-state request for the in-NT8 AddOn and wait for its result.

    `account` filters to one account by name (e.g. "Sim101"); empty = all
    accounts. Returns the raw response payload. Raises TimeoutError if the AddOn
    does not respond — which itself is diagnostic: NT8 is down, or the
    NT8BridgeServer AddOn is not loaded/compiled.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"account_{request_id}.json",
        build_account_request(request_id, account),
    )
    return ntio.poll_for_json(result / f"account_{request_id}.json", timeout=timeout)
