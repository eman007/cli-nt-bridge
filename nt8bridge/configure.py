"""Configure: write each key in the request's `params` map to whichever of
(tab, tab.TabStrategyProperties, strategyTemplate) has a matching writable
property. Type-aware: Instrument is created via Instrument.GetInstrument(name);
DateTime/TimeSpan/Enum parsed explicitly.

Closes the workflow gap where nt8bridge could inject strategy params + click Run
but required the user to manually configure the SA tab (instrument/dates/bar
type/fill resolution) before each (strategy, instrument) sweep.

Usage from CLI:
  python -m nt8bridge probe                  # discover writable property names first
  python -m nt8bridge configure --config X.json  # apply
  python -m nt8bridge backtest --config X.json   # then fire Run

Wire format mirrors backtest: send the whole config dict; AddOn reads `params`
and routes each key. Tab-level keys (Instrument, FromLocal, ToLocal, BarsPeriod, …)
land on the tab; per-strategy params land on the template. Same call works for both.

Response:
  {id, status, applied:[{key,target,status:set|skip|error,...}, ...]}
"""
from __future__ import annotations

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


def run_configure(config: dict, timeout: float = 30.0) -> dict:
    trigger, result = ntio.ensure_bridge_dirs()
    rid = new_request_id()
    ntio.atomic_write_json(
        trigger / f"configure_{rid}.json",
        {"id": rid, "kind": "configure", "config": config, "params": config.get("params", {})},
    )
    return ntio.poll_for_json(result / f"configure_{rid}.json", timeout=timeout)
