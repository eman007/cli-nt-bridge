"""Account trade-performance half of the IPC contract (request builder + parser + transport).

Pulls a trade-performance report for a live/sim account over a date range -- the same data
NT8's native Trade Performance window shows -- via the in-NT8 AddOn, which sources executions
from NT8's trade DB (Execution.DbGet), pairs them with SystemPerformance.Calculate, and
returns round-trip trades + headline metrics. The full scorecard (win rate, avg win/loss,
equity, drawdown) is derived client-side by nt8bridge.report.compute_stats from `trades`.

JSON contract:
  request : {"id": str, "kind": "performance", "account": str,
             "from": str|"", "to": str|"", "instrument": str|""}   # from/to = ET YYYY-MM-DD
  response: {"id": str, "status": "ok"|"error", "ts": str, "account": str,
             "source": "db"|"memory", "from": str, "to": str,
             "metrics": {"totalTrades","netProfit","grossProfit","grossLoss","profitFactor","maxDrawdown"},
             "trades": [{"pnl","marketPosition","entryTime","exitTime","entryPrice",
                         "exitPrice","exitName","quantity","commission"}],
             "warnings": [str], "errors": [...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class PerformanceReport:
    ok: bool
    account: str = ""
    source: str = ""
    metrics: dict = field(default_factory=dict)
    trades: list = field(default_factory=list)
    warnings: list = field(default_factory=list)
    errors: list = field(default_factory=list)


def build_performance_request(request_id: str, account: str,
                              from_: str = "", to: str = "", instrument: str = "") -> dict:
    return {
        "id": request_id, "kind": "performance", "account": account or "",
        "from": from_ or "", "to": to or "", "instrument": instrument or "",
    }


def parse_performance_response(payload: dict) -> PerformanceReport:
    return PerformanceReport(
        ok=payload.get("status") == "ok",
        account=payload.get("account", ""),
        source=payload.get("source", ""),
        metrics=payload.get("metrics", {}) or {},
        trades=payload.get("trades", []) or [],
        warnings=payload.get("warnings", []) or [],
        errors=payload.get("errors", []) or [],
    )


def run_performance(account: str, from_: str = "", to: str = "", instrument: str = "",
                    timeout: float = 20.0) -> dict:
    """Drop a performance request for the in-NT8 AddOn and wait for its result.

    Raises ValueError if `account` is empty (the AddOn requires it). Raises TimeoutError
    if the AddOn does not respond -- itself diagnostic: NT8 down, or NT8BridgeServer not loaded.
    """
    if not account:
        raise ValueError("performance requires an account name")
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"perf_{request_id}.json",
        build_performance_request(request_id, account, from_, to, instrument),
    )
    return ntio.poll_for_json(result / f"perf_{request_id}.json", timeout=timeout)
