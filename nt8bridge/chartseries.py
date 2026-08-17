"""Change a live chart's full data series (instrument + bar type + period).

Drops a `chartseries` trigger for the in-NT8 AddOn (RunChartSeries) and polls
its result. Target the active chart (default) or a specific chart by identity
(--on-instrument / --on-title). The AddOn refuses (status "blocked") if the
target chart has an enabled strategy or an open position on the instrument,
unless force=True.
"""
from __future__ import annotations

from pathlib import Path

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


def build_chartseries_request(rid: str, target: dict, dataseries: dict, force: bool) -> dict:
    return {"id": rid, "kind": "chartseries", "target": target, "dataseries": dataseries, "force": force}


def run_chartseries(*, instrument=None, bars_type=None, bars_value=None, bars_value2=None,
                    bars_base_value=None, on_instrument=None, on_title=None, force=False,
                    timeout: float = 30.0) -> dict:
    if bars_type and bars_value is None:
        raise ValueError("--bars-type requires --bars-value")
    dataseries = {}
    if instrument:
        dataseries["instrument"] = instrument
    if bars_type:
        dataseries["barsPeriodType"] = str(bars_type)
    # ⭐ VALUES ARE SENT INDEPENDENTLY OF --bars-type (fixed 2026-08-09).
    #    They used to be nested under `if bars_type:`, so a call that named no type sent no values —
    #    and there was therefore NO way to set Value/Value2 on a bars type already selected. That
    #    matters because switching to a CUSTOM type makes NT apply that type's OWN defaults and
    #    discard the values on the incoming BarsPeriod: asking for SentinelTBars 6/24 produced
    #    `212201_0_2_...` twice in a row. Stock types (Renko) are unaffected, which is why it hid.
    #    ⇒ Two-pass: switch the type, then call again WITHOUT --bars-type to stamp the values on.
    # The AddOn's ExtractJsonString reads only QUOTED JSON values, so send them as strings
    # (live-proven: a bare numeric was silently dropped to the default).
    if bars_value is not None:
        dataseries["barsPeriodValue"] = str(int(bars_value))
    if bars_value2 is not None:
        dataseries["barsPeriodValue2"] = str(int(bars_value2))
    # third param (UniRenko Open Offset etc.) — only when provided.
    if bars_base_value is not None:
        dataseries["baseBarsPeriodValue"] = str(int(bars_base_value))
    if not dataseries:
        raise ValueError("nothing to change: pass --instrument and/or --bars-type/--bars-value")

    if on_instrument:
        target = {"mode": "instrument", "value": on_instrument}
    elif on_title:
        target = {"mode": "title", "value": on_title}
    else:
        target = {"mode": "active"}

    trigger, result = ntio.ensure_bridge_dirs()
    trigger, result = Path(trigger), Path(result)
    rid = new_request_id()
    ntio.atomic_write_json(trigger / f"chartseries_{rid}.json",
                           build_chartseries_request(rid, target, dataseries, force))
    return ntio.poll_for_json(result / f"chartseries_{rid}.json", timeout=timeout)
