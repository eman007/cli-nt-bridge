"""Backtest half of the IPC contract (request builder + response parser).

JSON contract (Part 2 AddOn must honor):
  request : {"id": str, "kind": "backtest", "config": {...}}
  response: {"id": str, "status": "ok"|"error",
             "metrics": {...}, "trades": [...], "equity": [...],
             "errors": [...], "ts": str}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class BacktestResult:
    ok: bool
    metrics: dict = field(default_factory=dict)
    trades: list[dict] = field(default_factory=list)
    equity: list[dict] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)


def build_backtest_request(request_id: str, config: dict) -> dict:
    return {"id": request_id, "kind": "backtest", "config": config}


def parse_backtest_response(payload: dict) -> BacktestResult:
    return BacktestResult(
        ok=payload.get("status") == "ok",
        metrics=payload.get("metrics", {}),
        trades=payload.get("trades", []),
        equity=payload.get("equity", []),
        errors=payload.get("errors", []),
    )


def run_backtest(config: dict, timeout: float = 120.0) -> dict:
    """Drop a backtest request for the in-NT8 AddOn and wait for its result.

    Returns the raw response payload. During Part-3 bring-up the AddOn replies
    with a probe payload (status="probe"); once real backtests run it carries
    metrics/trades/equity. Raises TimeoutError if the AddOn does not respond.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"backtest_{request_id}.json",
        build_backtest_request(request_id, config),
    )
    return ntio.poll_for_json(result / f"backtest_{request_id}.json", timeout=timeout)
