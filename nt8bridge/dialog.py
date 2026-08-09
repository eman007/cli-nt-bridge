"""See — and answer — the modal dialog that is blocking a headless box.

WHY THIS EXISTS
    A modal dialog on an unattended machine stops everything and announces nothing. NinjaTrader's
    Auto Rollover prompt sat on one node for days, offering to roll the contract out from under a
    holdout window, and the only way to see it was to open an interactive session — which is itself
    barred during a bake, because an RDP teardown drives NT into a UCEERR render-death spiral.

    `windows` could already SEE such a window. It could not answer it. This closes that.

HOW A MODAL IS DETECTED
    Without touching WPF at all: Windows disables the owner while a modal child is up, so
    `owner != 0 and not IsWindowEnabled(owner)` is the signal. It is thread-agnostic, which matters
    because the dialog belongs to a UI thread that is not the one answering.

⛔ IT WILL NOT GUESS
    `dismiss` requires an explicit dialog AND an explicit button, and REFUSES when either match is
    ambiguous rather than taking the first. There is deliberately no "click the default" — on a
    rollover prompt the default is the answer that spends your holdout.

⭐ VERIFY THE OUTCOME, NEVER THE CALL
    A click is POSTED, which proves only that a message was queued. The AddOn re-probes the window
    afterwards and reports `dismissed` from whether it actually went away. A posted click that
    changed nothing must never read as success — that is the single failure this project keeps
    paying for.

JSON contract (the in-NT8 AddOn honors):
  request : {"id","kind":"dialog","action":"list"|"dismiss","title","button","hwnd","waitMs"}
  response: {"id","status","ts","action","count",
             "dialogs":[{"hwnd","title","class","modal","buttonSource","buttons":[str]}],
             "dismissed":bool, "clickedVia":str, "verdict":str, "errors":[...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class DialogState:
    ok: bool
    action: str = "list"
    dialogs: list[dict] = field(default_factory=list)
    dismissed: bool = False
    verdict: str = ""
    clicked_via: str = ""
    errors: list[dict] = field(default_factory=list)

    @property
    def modals(self) -> list[dict]:
        """The ones that are actually BLOCKING. A non-modal dialog is a window; a modal one is a
        stopped machine, and only the second is an incident."""
        return [d for d in self.dialogs if d.get("modal")]

    def describe(self) -> str:
        if not self.dialogs:
            return "no dialogs"
        out = []
        for d in self.dialogs:
            names = d.get("buttons") or []
            extra = d.get("unlabelledButtons") or 0
            # An unlabelled control is counted, never named. A type name from Content.ToString()
            # would read as a caption you could click, and a caller matching it clicks blind.
            label = ", ".join(names) if names else "no named buttons"
            if extra:
                label += f" (+{extra} unlabelled)"
            out.append("%s [%s]%s" % (d.get("title") or "(untitled)", label,
                                      " MODAL" if d.get("modal") else ""))
        return "; ".join(out)


def build_dialog_request(request_id: str, action: str = "list", title: str | None = None,
                         button: str | None = None, hwnd: int | None = None,
                         wait_ms: int = 5000, scope: str = "modal") -> dict:
    return {
        "id": request_id,
        "kind": "dialog",
        "action": action,
        "title": title or "",
        "button": button or "",
        "hwnd": str(hwnd) if hwnd else "",
        "waitMs": str(wait_ms),
        # "modal" is the safe default; "all" widens to every visible top-level window, because a
        # NON-modal Error box or rollover prompt still blocks a day and the modal-only scan
        # reported a clean bill of health over a machine with two of them on screen.
        "scope": scope,
    }


def parse_dialog_response(payload: dict) -> DialogState:
    return DialogState(
        ok=payload.get("status") == "ok",
        action=payload.get("action", "list"),
        dialogs=payload.get("dialogs") or [],
        dismissed=bool(payload.get("dismissed")),
        verdict=payload.get("verdict", "") or "",
        clicked_via=payload.get("clickedVia", "") or "",
        errors=payload.get("errors") or [],
    )


def run_dialog(action: str = "list", title: str | None = None, button: str | None = None,
               hwnd: int | None = None, wait_ms: int = 5000, timeout: float = 30.0,
               scope: str = "modal") -> dict:
    trigger, result = ntio.ensure_bridge_dirs()
    rid = new_request_id()
    ntio.atomic_write_json(
        trigger / f"dialog_{rid}.json",
        build_dialog_request(rid, action, title, button, hwnd, wait_ms, scope),
    )
    return ntio.poll_for_json(result / f"dialog_{rid}.json", timeout=timeout)
