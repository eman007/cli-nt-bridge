"""What is actually on each chart — instrument, bar type, indicators, strategies and their State.

WHY THIS EXISTS
    2026-08-02. Two replay runs were lost to a strategy that was silently DISABLED. Toggling the
    Playback connection disables chart strategies, and a workspace restored on an unattended boot
    can come back with the strategy off. Neither case announces itself: the replay runs, the clock
    advances, the panel looks healthy, and the corpus comes back empty an hour later.

    "Is my strategy enabled, on both boxes?" should be one command, not an RDP session per box.

READING `state`
    NT's own State enum is passed through verbatim — Active / Realtime / Historical / Terminated —
    because the raw value is the evidence. `enabled` is a convenience derived from it, and where the
    two ever disagree, believe `state`.

`null` IS NOT `[]`
    When the AddOn cannot resolve a member it emits null, never an empty list. A chart we could not
    read and a chart with no strategies are different claims, and collapsing them is how "the run
    produced nothing" gets mistaken for "the run was fine".

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "workspace"}
  response: {"id": str, "status": "ok"|"error", "ts": str,
             "workspace": str|None, "chartCount": int,
             "charts": [{"title": str, "type": str, "chartControlResolved": bool,
                         "instrument": str|None, "barsPeriod": str|None,
                         "indicators": [{"name","type","state","enabled"}]|None,
                         "strategies": [{"name","type","state","enabled"}]|None}],
             "notes": [str], "errors": [...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class WorkspaceState:
    ok: bool
    workspace: str | None = None
    charts: list[dict] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)

    def enabled_strategies(self) -> list[dict]:
        """Every enabled strategy across every chart, each tagged with its chart title."""
        out = []
        for c in self.charts:
            for s in c.get("strategies") or []:
                if s.get("enabled"):
                    out.append({**s, "chart": c.get("title")})
        return out

    def strategy_running(self, name_fragment: str) -> tuple[bool, str]:
        """Is a strategy whose name contains `name_fragment` enabled on any chart?

        Matches on a FRAGMENT so a version bump cannot silently turn the check into a no-match —
        the same reasoning the suite uses when detecting sibling indicators by type-name prefix.
        Returns (ok, why) so a caller can report the reason rather than a bare False.
        """
        frag = name_fragment.lower()
        seen = []
        for c in self.charts:
            strategies = c.get("strategies")
            if strategies is None:
                continue
            for s in strategies:
                # Match name OR type. A Sentinel tool blanks its Name at DataLoaded to hide the
                # on-chart label, so `name` is frequently "" and only `type` carries the identity —
                # a name-only match silently found nothing the first time this ran for real.
                nm = s.get("name") or ""
                ty = s.get("type") or ""
                if frag not in nm.lower() and frag not in ty.lower():
                    continue
                seen.append(f"{nm or ty} on {c.get('title')} = {s.get('state')}")
                if s.get("enabled"):
                    return True, seen[-1]
        if seen:
            return False, "found but not enabled: " + "; ".join(seen)
        return False, f"no strategy matching '{name_fragment}' on any chart"


def build_workspace_request(request_id: str) -> dict:
    return {"id": request_id, "kind": "workspace"}


def parse_workspace_response(payload: dict) -> WorkspaceState:
    return WorkspaceState(
        ok=payload.get("status") == "ok",
        workspace=payload.get("workspace"),
        charts=payload.get("charts", []),
        notes=payload.get("notes", []),
        errors=payload.get("errors", []),
    )


def run_workspace(timeout: float = 30.0) -> dict:
    """Read chart/indicator/strategy inventory from the in-NT8 AddOn.

    Timeout is generous because the handler marshals to EVERY chart window's own dispatcher — NT is
    multi-UI-threaded, so a busy chart answers slowly rather than not at all.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"workspace_{request_id}.json",
        build_workspace_request(request_id),
    )
    return ntio.poll_for_json(result / f"workspace_{request_id}.json", timeout=timeout)
