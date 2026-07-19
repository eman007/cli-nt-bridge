"""Backtest sweep — run a strategy across an instrument x bar-type x param-set
matrix by interleaving the existing `configure` (set tab-level Instrument +
BarsPeriod) and `backtest` (inject strategy params + fire Run) kinds.

Pure Python over existing IPC; configure_fn/backtest_fn are injectable so the
cross-product, merge, labelling, and sequencing are unit-testable without NT8.
The SA tab must already have the strategy selected (same precondition as
`backtest`/`batch`); sweep varies instrument/bars/params, not strategy choice.
"""
from __future__ import annotations

from nt8bridge import backtest as _backtest
from nt8bridge import configure as _configure


def combos(instruments: list, bars: list, paramsets) -> list:
    sets = paramsets if paramsets else [{"label": "", "params": {}}]
    out = []
    for instr in instruments:
        for bp in bars:
            for ps in sets:
                label = f"{instr} {bp}"
                if ps.get("label"):
                    label += f" {ps['label']}"
                out.append({
                    "instrument": instr,
                    "bars": bp,
                    "params": dict(ps.get("params", {})),
                    "label": label,
                })
    return out


def _configure_applied(cfg_result, keys) -> bool:
    """True only if configure returned ok AND each required tab-level key was
    actually `set` (not skip/error). Guards against backtesting the PREVIOUS
    instrument when e.g. a typo'd instrument leaves the tab unchanged — which
    would otherwise produce a comparison row mislabeled with the wrong series."""
    if not isinstance(cfg_result, dict) or cfg_result.get("status") != "ok":
        return False
    applied = {a.get("key"): a.get("status")
               for a in cfg_result.get("applied", []) if isinstance(a, dict)}
    return all(applied.get(k) == "set" for k in keys)


def run_sweep(base: dict, instruments: list, bars: list, paramsets=None, *,
              timeout: float = 120.0, configure_fn=None, backtest_fn=None) -> list:
    configure_fn = configure_fn or _configure.run_configure
    backtest_fn = backtest_fn or _backtest.run_backtest
    base = base or {}
    base_params = dict(base.get("params", {}))
    results = []
    for combo in combos(instruments, bars, paramsets):
        bt_params = {**base_params, **combo["params"]}
        row = {
            "label": combo["label"],
            "instrument": combo["instrument"],
            "bars": combo["bars"],
            "params": bt_params,
        }

        cfg_configure = dict(base)
        cfg_configure["params"] = {**base_params, "Instrument": combo["instrument"], "BarsPeriod": combo["bars"]}
        # A configure failure must NOT silently run the backtest on the prior
        # instrument, and a configure timeout must not abort the whole sweep.
        try:
            cfg_result = configure_fn(cfg_configure, timeout=timeout)
        except TimeoutError as e:
            results.append({**row, "result": {"status": "timeout", "ok": False, "message": f"configure: {e}"}})
            continue
        if not _configure_applied(cfg_result, ("Instrument", "BarsPeriod")):
            results.append({**row, "result": {
                "status": "error", "ok": False,
                "message": "configure did not apply Instrument/BarsPeriod for this combo",
                "configure": cfg_result,
            }})
            continue

        cfg_bt = dict(base)
        cfg_bt["params"] = bt_params
        try:
            payload = backtest_fn(cfg_bt, timeout=timeout)
        except TimeoutError as e:
            payload = {"status": "timeout", "ok": False, "message": str(e)}
        results.append({**row, "result": payload})
    return results
