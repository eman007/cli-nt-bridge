"""Charts: list them, and attach or remove INDICATORS on them.

WHY
    `strategy add` alone does not reproduce a cell. A Sentinel strategy reads its context from sensor
    indicators that must be ON THE SAME CHART — a chart-derived sensor computes from its own chart's
    bars, so an off-chart one is voting on a chart it cannot see. Staging a cell on a second box
    therefore means placing indicators too, and that was the other half of the per-box hand-work that
    kept a six-worker fleet down to however many hands were available.

⛔ THIS DOES NOT CREATE CHART WINDOWS, ON PURPOSE
    Constructing a chart window means building a WPF Window on another UI thread and wiring it into
    the workspace, on a platform that is multi-UI-threaded and hosts live order routing. The risk is
    real and the value is not: `layout` already places windows across machines and a workspace file
    already carries the charts. What a workspace CANNOT carry is the strategy and the indicator set —
    which is exactly what this and `strategy add` supply. Closing IS offered behind --confirm,
    because it is bounded and undone by reopening.

⭐ VERIFIED BY COUNT
    An add or remove is judged by the chart's indicator count before and after, not by the call
    returning. A reflection call that resolved and changed nothing is the failure mode this whole
    tool family exists to make visible.

JSON contract (the in-NT8 AddOn honors):
  request : {"id","kind":"chart","action":"list"|"addIndicator"|"removeIndicator"|"close",
             "chart","type","name","confirm","params":{...}}
  response: {"id","status","action","succeeded","verdict","chart",
             "indicatorsBefore","indicatorsAfter","via","charts":[...],"notes":[str],"errors":[...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class ChartState:
    ok: bool
    action: str = "list"
    succeeded: bool = False
    verdict: str = ""
    charts: list[dict] = field(default_factory=list)
    before: int | None = None
    after: int | None = None
    notes: list[str] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)

    def describe(self) -> str:
        if self.action != "list":
            return self.verdict or ("ok" if self.succeeded else "did NOT take effect")
        if not self.charts:
            return "no matching charts"
        out = []
        for c in self.charts:
            inds = c.get("indicators")
            # `null` is not `[]` — a chart we could not read and a chart with no indicators are
            # different claims, and collapsing them is how "the run produced nothing" gets excused.
            n = "unreadable" if inds is None else str(len(inds))
            out.append("%s [%s] %s indicators" % (c.get("title"), c.get("instrument"), n))
        return "; ".join(out)


def build_chart_request(request_id: str, action: str = "list", chart: str | None = None,
                        type_name: str | None = None, name: str | None = None,
                        params: dict | None = None, confirm: bool = False,
                        template_path: str | None = None, window: dict | None = None) -> dict:
    req = {
        "id": request_id,
        "kind": "chart",
        "action": action,
        "chart": chart or "",
        "type": type_name or "",
        "name": name or "",
        "confirm": "true" if confirm else "false",
        "params": {k: str(v) for k, v in (params or {}).items()},
    }
    # `path` is resolved by the ADDON, i.e. on the NinjaTrader machine — not here. The bridge is a
    # file-drop IPC on the same host, so a client-side existence check would be right by accident
    # locally and wrong the moment anyone drives a remote box.
    if template_path:
        req["path"] = template_path
    # Only fields the caller actually supplied are sent. The AddOn sets nothing it was not asked
    # about, so a data-window call cannot quietly rewrite a field you did not mention. Values go as
    # STRINGS because the AddOn's ExtractJsonString reads only quoted values — a bare number is
    # dropped silently (live-proven once already, in chartseries).
    for k, v in (window or {}).items():
        if v is not None and v != "":
            req[k] = str(v)
    return req


def parse_chart_response(payload: dict) -> ChartState:
    return ChartState(
        ok=payload.get("status") == "ok",
        action=payload.get("action", "list"),
        succeeded=bool(payload.get("succeeded")),
        verdict=payload.get("verdict", "") or "",
        charts=payload.get("charts") or [],
        before=payload.get("indicatorsBefore"),
        after=payload.get("indicatorsAfter"),
        notes=payload.get("notes") or [],
        errors=payload.get("errors") or [],
    )


def run_chart(action: str = "list", chart: str | None = None, type_name: str | None = None,
              name: str | None = None, params: dict | None = None, confirm: bool = False,
              timeout: float = 60.0, template_path: str | None = None,
              window: dict | None = None) -> dict:
    trigger, result = ntio.ensure_bridge_dirs()
    rid = new_request_id()
    ntio.atomic_write_json(
        trigger / f"chart_{rid}.json",
        build_chart_request(rid, action, chart, type_name, name, params, confirm,
                            template_path, window),
    )
    return ntio.poll_for_json(result / f"chart_{rid}.json", timeout=timeout)
