"""Read the OPEN NT8 Trade Performance window's commission/fee totals.

The curve-ball this solves: for live/funded (e.g. prop-firm) accounts NT8 does NOT persist
per-fill commission -- Execution.DbGet returns 0 and there is no local Commission template, so
the headless `performance` verb can only report gross P&L. NT8's native Trade Performance
window, however, fetches the broker's cash history when it displays the account and holds it in
the tab's view model (TradePerformanceReportViewModel: public TotalFeesAll/Long/Short +
FeesByExecution). This verb reads that live in-memory copy -- exactly what the window shows --
so the user must have the Trade Performance window open on the account (fees calculated).

JSON contract:
  request : {"id": str, "kind": "perfwindow", "account": str}
  response: {"id": str, "status": "ok"|"error", "ts": str, "reportCount": int,
             "reports": [{"account","from","to","feesCalculated","totalFees",
                          "totalFeesLong","totalFeesShort","feeItemSum","executionsWithFees",
                          "feeItemCount","trades","feeCategories":[{"type","total","count"}]}],
             "errors": [...]}
"""
from __future__ import annotations

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


def build_perfwindow_request(request_id: str, account: str = "", generate: bool = False,
                             from_: str = "", to: str = "") -> dict:
    return {
        "id": request_id, "kind": "perfwindow", "account": account or "",
        # booleans/dates are passed as strings so the AddOn's quoted-string parser reads them.
        "generate": "true" if generate else "false",
        "from": from_ or "", "to": to or "",
    }


def run_perfwindow(account: str = "", generate: bool = False, from_: str = "", to: str = "",
                   timeout: float = 20.0) -> dict:
    """Drop a perfwindow request for the in-NT8 AddOn and wait for its result.

    generate=True drives the window's own Generate (sets account filter + date range, fires
    GenerateReport, waits) so the pull is hands-off. Raises TimeoutError if the AddOn does not
    respond -- itself diagnostic: NT8 down, or the NT8BridgeServer AddOn is not loaded.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"perfwindow_{request_id}.json",
        build_perfwindow_request(request_id, account, generate, from_, to),
    )
    return ntio.poll_for_json(result / f"perfwindow_{request_id}.json", timeout=timeout)
