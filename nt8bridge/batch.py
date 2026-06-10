"""Batch backtests — run N param-sets through the bridge and aggregate results.

A batch file is JSON with an optional shared `base` and a non-empty `runs` list:

    {
      "base": {"params": {"Contracts": 1}},
      "runs": [
        {"label": "tight", "params": {"StopTicks": 30, "TargetTicks": 75}},
        {"label": "wide",  "params": {"StopTicks": 50, "TargetTicks": 150}}
      ]
    }

Each run's params override base.params. Runs execute sequentially (the Strategy
Analyzer runs one backtest at a time). The SA tab must be configured for the
strategy; only params vary per run.
"""
from __future__ import annotations

import json
from pathlib import Path

from nt8bridge import backtest as _backtest


class BatchError(ValueError):
    """Raised when a batch file is missing or malformed."""


def load_batch(path) -> dict:
    path = Path(path)
    if not path.exists():
        raise BatchError(f"Batch file not found: {path}")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        raise BatchError(f"Batch file is not valid JSON: {e}") from e
    if not isinstance(data.get("runs"), list) or not data["runs"]:
        raise BatchError("Batch file must have a non-empty 'runs' list")
    return data


def merged_params(base: dict, run: dict) -> dict:
    params = dict((base or {}).get("params", {}))
    params.update(run.get("params", {}))
    return params


def run_batch(batch: dict, timeout: float = 120.0, runner=None) -> list:
    """Run each entry in batch['runs'] sequentially.

    `runner(config, timeout=...)` defaults to backtest.run_backtest; it is
    injectable so the loop/merge logic is unit-testable without NinjaTrader.
    Returns a list of {label, params, result} (result is the raw backtest payload).
    """
    runner = runner or _backtest.run_backtest
    base = batch.get("base", {}) or {}
    results = []
    for i, run in enumerate(batch["runs"]):
        label = run.get("label", f"run{i + 1}")
        params = merged_params(base, run)
        cfg = dict(base)
        cfg["params"] = params
        try:
            payload = runner(cfg, timeout=timeout)
        except TimeoutError as e:
            payload = {"status": "timeout", "ok": False, "message": str(e)}
        results.append({"label": label, "params": params, "result": payload})
    return results


def summary_table(results: list) -> str:
    rows = [("label", "status", "net", "PF", "trades")]
    for r in results:
        res = r.get("result") or {}
        m = res.get("metrics", {}) or {}
        rows.append(
            (
                str(r.get("label", "")),
                str(res.get("status", "")),
                str(m.get("netProfit", "")),
                str(m.get("profitFactor", "")),
                str(m.get("totalTrades", "")),
            )
        )
    widths = [max(len(row[c]) for row in rows) for c in range(len(rows[0]))]
    return "\n".join(
        "  ".join(cell.ljust(widths[c]) for c, cell in enumerate(row)) for row in rows
    )
