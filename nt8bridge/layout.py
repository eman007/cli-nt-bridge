"""Where NinjaTrader's windows sit — capture it as a file, apply it headlessly.

WHY THIS EXISTS
    Every input to a replay-equivalence run that had been made verifiable was a FILE we could hash:
    the code, the replay `.nrd`, the historical bars, the serialised chart+strategy blob. Window
    layout was not one of them. It lived only inside a running NinjaTrader, set by hand, per box, at
    different moments — the same shape as the transport state that cost a day, and as the Playback
    range that "does not travel".

    A fleet of six replay workers cannot have an input that needs a GUI click per box. Anything that
    does is guaranteed to diverge across a matrix, and to diverge silently.

FRACTIONS, NOT PIXELS
    A placement is stored as a fraction of its monitor's work area, so one file describes the same
    arrangement on a 2560x1440 desktop and a 1920x1080 VM. Absolute pixels cannot cross machines:
    `-3874` is a real coordinate on a three-monitor desktop and nowhere at all on a sentry.

IDENTITY, NEVER HWND
    Handles are reassigned on every launch, so a layout keyed on one dies at the next restart — and
    surviving a restart is the whole point. Matching is (process-local class key + title key),
    scored, best-match-wins, and a tie that cannot be resolved is REPORTED rather than guessed.

    ⚠ The class key matters more than it looks. NinjaTrader's WPF windows are named
    `HwndWrapper[NinjaTrader.exe;UI thread 1;<GUID>]` and that GUID is regenerated every launch, so
    the raw class is useless as a key. Only the app segment is stable.

WHY THE LOGIC IS ALL HERE AND NOT IN THE ADDON
    The AddOn is the one component in this system that cannot be tested without a running
    NinjaTrader. So it enumerates, and it moves an HWND it is told to move — nothing else. Matching,
    fractions and monitor mapping are pure functions in this module, and they are unit-tested
    offline. The less judgement the untestable half holds, the better.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "layout", "place": "hwnd,x,y,w,h,state;..."|""}
  response: {"id": str, "status": "ok"|"error", "ts": str,
             "placed": int, "failed": [str],
             "monitors": [{"id": int, "primary": bool, "work": {"x","y","w","h"}}],
             "count": int,
             "windows": [{"hwnd": int, "title": str, "class": str,
                          "visible","minimized","maximized","cloaked","owned","toolWindow": bool,
                          "monitor": int,
                          "visual": {"x","y","w","h"}, "restored": {"x","y","w","h"}}]}

LAYOUT FILE SCHEMA — `sentinel.berth.layout/1`
    Deliberately the SAME schema SentinelBerth writes on the desktop, so an arrangement painted with
    a grid on one machine is the artifact a headless box applies. One schema, two producers. The
    schema is the shared thing; the code is not, because the two live in different languages and
    different repos.
"""
from __future__ import annotations

import datetime as _dt
import json
from dataclasses import dataclass, field
from pathlib import Path

from nt8bridge import ntio
from nt8bridge.compile import new_request_id

SCHEMA = "sentinel.berth.layout/1"

# A window whose visible area is smaller than this is not an arrangement, it is a stub — a splash,
# a tooltip host, or a window mid-teardown. Placing one is noise; recording one poisons the file.
MIN_USEFUL_PX = 40


@dataclass
class LayoutState:
    ok: bool
    monitors: list[dict] = field(default_factory=list)
    windows: list[dict] = field(default_factory=list)
    placed: int = 0
    failed: list[str] = field(default_factory=list)
    ts: str | None = None

    def placeable(self) -> list[dict]:
        """The windows an arrangement can meaningfully contain.

        Cloaked is the state that makes a naive enumeration lie: a suspended or virtual-desktop
        window reports visible=True and minimized=False while parked at ~(-32000, -32000) at about
        76x28. Measured on a normal desktop, 28 of 37 "windows" were these. Capture them and the
        file is mostly junk; apply them and you compute real coordinates from garbage.

        ⚠ `owned` is NOT a disqualifier, however tempting. Measured on a sentry: NinjaTrader's
        Playback window and its charts BOTH report owned=True, because NT parents its windows to a
        hidden owner. Excluding owned windows dropped 27 windows to 1 — the whole application —
        and, like every filter mistake in this family, it failed silently: "1 window" and "NT has
        one window open" are the same output. Tool windows and cloaked windows are the real
        exclusions; ownership says nothing about whether a human arranged it.
        """
        out = []
        for w in self.windows:
            if w.get("cloaked") or w.get("toolWindow"):
                continue
            if not w.get("visible"):
                continue
            if not (w.get("title") or "").strip():
                continue
            geom = geometry_of(w)
            if geom["w"] < MIN_USEFUL_PX or geom["h"] < MIN_USEFUL_PX:
                continue
            out.append(w)
        return out


def class_key(cls: str | None) -> str:
    """Strip WPF's per-launch GUID down to the stable app segment.

    `HwndWrapper[NinjaTrader.exe;UI thread 1;ea69f969-...]` -> `HwndWrapper[NinjaTrader.exe]`.
    The thread segment goes too: NinjaTrader is multi-UI-threaded and a window is not guaranteed the
    same thread ordinal twice.
    """
    if not cls:
        return ""
    if not cls.startswith("HwndWrapper["):
        return cls
    inner = cls[len("HwndWrapper["):].rstrip("]")
    app = inner.split(";", 1)[0]
    return f"HwndWrapper[{app}]"


_TITLE_SEPS = (" - ", " – ", " — ", " | ")


def title_key(title: str | None) -> str:
    """The stable head of a title.

    A title carries live content — a chart gains a bar count, an editor gains a tab name — so the
    whole string is a poor key. The first segment before a separator is what a human would call the
    window, and it is what survives.
    """
    if not title:
        return ""
    t = title.strip()
    cut = len(t)
    for s in _TITLE_SEPS:
        i = t.find(s)
        if 0 < i < cut:
            cut = i
    head = t[:cut].strip()
    return head or t


def geometry_of(w: dict) -> dict:
    """Where the window LIVES, which is not always where it is drawn.

    A minimized window's on-screen rect is the park position; its restored rect is the arrangement.
    Recording the former is how a layout learns to fling windows off-screen — and on a headless box
    there is no mouse to go and find them again.
    """
    if w.get("minimized") or w.get("maximized"):
        r = w.get("restored") or {}
        if r.get("w", 0) > 0 and r.get("h", 0) > 0:
            return r
    return w.get("visual") or {"x": 0, "y": 0, "w": 0, "h": 0}


def state_of(w: dict) -> str:
    if w.get("maximized"):
        return "maximized"
    if w.get("minimized"):
        return "minimized"
    return "normal"


def _monitor_for(state: LayoutState, w: dict) -> dict | None:
    """Which monitor a window BELONGS to — decided from the geometry we are about to record.

    ⚠ The AddOn's own `monitor` field is deliberately NOT trusted here. It is computed from the
    window's on-screen rect, which for a minimized window is the park coordinate (~-32000) and
    therefore attributes it to whichever monitor happens to be nearest that point. Dividing the
    RESTORED geometry by THAT monitor's work area produced fractions like x = -3.1.

    Found by cross-checking this module's capture against SentinelBerth's on the same desktop: two
    independent producers of one schema agreed on three windows and disagreed on the two minimized
    ones. Neither tool alone would have shown it — the numbers are wrong, not absent.
    """
    if not state.monitors:
        return None
    g = geometry_of(w)
    cx, cy = g["x"] + g["w"] // 2, g["y"] + g["h"] // 2
    best, best_d = None, None
    for m in state.monitors:
        k = m["work"]
        if k["x"] <= cx < k["x"] + k["w"] and k["y"] <= cy < k["y"] + k["h"]:
            return m
        mx, my = k["x"] + k["w"] // 2, k["y"] + k["h"] // 2
        d = (cx - mx) ** 2 + (cy - my) ** 2
        if best_d is None or d < best_d:
            best, best_d = m, d
    # A window straddling two screens, or parked off every one, still has to be attributed to one.
    return best


def capture(state: LayoutState, name: str, now: str | None = None) -> dict:
    """Build a layout document from a live reading. Pure — no I/O, no clock unless you pass one."""
    windows = []
    for w in state.placeable():
        mon = _monitor_for(state, w)
        if not mon:
            continue
        work = mon["work"]
        if work["w"] <= 0 or work["h"] <= 0:
            continue
        g = geometry_of(w)
        windows.append({
            "titleKey": title_key(w.get("title")),
            "title": w.get("title"),          # for a human reading the file; never matched on
            "class": w.get("class"),
            "classKey": class_key(w.get("class")),
            "monitor": mon["id"],
            "state": state_of(w),
            "frac": {
                "x": round((g["x"] - work["x"]) / work["w"], 4),
                "y": round((g["y"] - work["y"]) / work["h"], 4),
                "w": round(g["w"] / work["w"], 4),
                "h": round(g["h"] / work["h"], 4),
            },
        })
    return {
        "schema": SCHEMA,
        "name": name,
        "tool": "nt8bridge layout",
        "capturedUtc": now or _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "monitors": state.monitors,
        "windows": windows,
    }


def _score(w: dict, placement: dict) -> int:
    """How well a live window answers to a recorded placement.

    Class alone is deliberately NOT enough. Within one process every NinjaTrader chart shares a
    class key, so class-only matching cannot tell two charts apart — and berthing the wrong chart is
    worse than leaving it alone and saying so.
    """
    score = 0
    pk = placement.get("classKey") or class_key(placement.get("class"))
    if pk and class_key(w.get("class")) == pk:
        score += 3
    tk = placement.get("titleKey") or ""
    if tk:
        if title_key(w.get("title")).lower() == tk.lower():
            score += 4
        elif tk.lower() in (w.get("title") or "").lower():
            score += 2
    return score


MATCH_THRESHOLD = 4


def plan_apply(doc: dict, state: LayoutState) -> tuple[list[dict], list[dict]]:
    """Resolve a layout document against a live reading.

    Returns (plan, problems). Every placement either produces exactly one move or appears in
    `problems` with a reason — a layout that half-applied and said nothing is indistinguishable
    from one that worked.
    """
    if doc.get("schema") != SCHEMA:
        # Refuse rather than best-effort. Applying a document we do not understand moves real
        # windows to coordinates computed from garbage, and there is no undo.
        return [], [{"reason": f"unrecognised schema: {doc.get('schema')!r}", "placement": None}]

    live = list(state.placeable())
    plan: list[dict] = []
    problems: list[dict] = []

    for p in doc.get("windows", []):
        scored = sorted(((_score(w, p), w) for w in live), key=lambda t: -t[0])
        best = scored[0] if scored else (0, None)
        if best[0] < MATCH_THRESHOLD:
            problems.append({"reason": "no live window matched",
                             "placement": f"{p.get('titleKey')} [{p.get('classKey')}]"})
            continue
        # A TIE IS NOT A MATCH. Two windows answering equally well means the identity we recorded
        # cannot distinguish them, and picking the first is a coin flip with someone's screen.
        if len(scored) > 1 and scored[1][0] == best[0]:
            problems.append({
                "reason": f"ambiguous — {best[0]} points for both "
                          f"{title_key(best[1].get('title'))!r} and {title_key(scored[1][1].get('title'))!r}",
                "placement": f"{p.get('titleKey')} [{p.get('classKey')}]"})
            continue

        w = best[1]
        live.remove(w)          # one live window satisfies one placement, never two

        mon = None
        for m in state.monitors:
            if m.get("id") == p.get("monitor"):
                mon = m
                break
        if mon is None:
            # The layout names a monitor this box does not have. Fall back to the primary and SAY
            # SO — silently retargeting is how a two-monitor arrangement lands in a heap on one.
            mon = next((m for m in state.monitors if m.get("primary")), None) or (state.monitors[0] if state.monitors else None)
            if mon is None:
                problems.append({"reason": "no monitors reported", "placement": p.get("titleKey")})
                continue
            problems.append({"reason": f"monitor {p.get('monitor')} absent — placed on {mon['id']}",
                             "placement": p.get("titleKey"), "severity": "note"})

        work = mon["work"]
        f = p.get("frac") or {}
        plan.append({
            "hwnd": w["hwnd"],
            "title": w.get("title"),
            "x": round(work["x"] + f.get("x", 0) * work["w"]),
            "y": round(work["y"] + f.get("y", 0) * work["h"]),
            "w": max(MIN_USEFUL_PX, round(f.get("w", 0.5) * work["w"])),
            "h": max(MIN_USEFUL_PX, round(f.get("h", 0.5) * work["h"])),
            "state": p.get("state", "normal"),
        })
    return plan, problems


def format_place(plan: list[dict]) -> str:
    """The wire format the AddOn parses: `hwnd,x,y,w,h,state` joined by `;`.

    Deliberately not JSON: the AddOn's only JSON reader extracts flat string values, and giving it a
    nested parser to maintain would put more judgement in the half that cannot be tested offline.
    """
    return ";".join(
        f"{p['hwnd']},{p['x']},{p['y']},{p['w']},{p['h']},{p.get('state', 'normal')}"
        for p in plan
    )


def build_layout_request(request_id: str, place: str = "") -> dict:
    return {"id": request_id, "kind": "layout", "place": place}


def parse_layout_response(payload: dict) -> LayoutState:
    return LayoutState(
        ok=payload.get("status") == "ok",
        monitors=payload.get("monitors", []),
        windows=payload.get("windows", []),
        placed=payload.get("placed", 0),
        failed=payload.get("failed", []),
        ts=payload.get("ts"),
    )


def run_layout(place: str = "", timeout: float = 30.0) -> dict:
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"layout_{request_id}.json",
        build_layout_request(request_id, place),
    )
    return ntio.poll_for_json(result / f"layout_{request_id}.json", timeout=timeout)


def read_layout_file(path: str | Path) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def write_layout_file(path: str | Path, doc: dict) -> Path:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(doc, indent=2, ensure_ascii=False), encoding="utf-8")
    return p
