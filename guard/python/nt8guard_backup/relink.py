"""Keep strategies on their charts when they come back under a different DB Id (NinjaTrader CLOSED).

The rules (design spec §6):
 1 a chart already lists the strategy's live Id, and no other backed-up strategy ever held that Id
   -> nothing to do. (NT8 reuses Ids; an entry another strategy also held is reported, not trusted.)
 2 a chart lists a DEAD Id from the strategy's own Id history while it is live under a new Id
   (a remapped restore, or re-added by hand) -> point that chart entry at the live Id
 3 the strategy was restored in THIS pass and its recorded chart tab no longer lists it -> add it
 4 a live strategy on no chart that was not restored now -> left alone (it may be Control-Center-only)
 5 a chart entry holding another live strategy's Id -> never touched

Usage: chart_relink.py [--nt8-dir D] [--guard-home H] [--apply] [--json]
Exit 0 = nothing to do / all applied; 2 = report mode with something to do; 1 = refused or failed.
"""
from __future__ import annotations

import argparse
import json
import sqlite3
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

from . import nt8db, paths, registry, workspaces
from .workspaces import StrategyRef


@dataclass(frozen=True)
class Action:
    kind: str
    name: str
    workspace: Path
    chart: str
    tab: str
    tab_name: str
    slot: str | None
    bars_index: int
    old_id: int | None
    new_id: int

    def describe(self) -> str:
        where = f"chart tab '{self.tab_name or self.tab}' ({self.workspace.stem})"
        if self.kind == "repoint":
            return f"{self.name}: {where} points at id {self.old_id}, now {self.new_id}"
        return f"{self.name}: put back on {where} as id {self.new_id}"


def scan(nt8: Path):
    refs: dict[Path, list[StrategyRef]] = {}
    tabs: dict[Path, set[tuple[str, str]]] = {}
    notes: list[str] = []
    for p in workspaces.workspace_files(nt8):
        try:
            refs[p] = workspaces.read_refs(p)
            tabs[p] = {(c, t) for c, t, _ in workspaces.read_tabs(p)}
        except (ET.ParseError, OSError) as ex:
            notes.append(f"{p.name} could not be read ({ex}); its charts were left alone")
    return refs, tabs, notes


def plan(doc: dict, live_ids: dict[str, int], refs_by_ws: dict, tabs_by_ws: dict,
         restored: set[str]) -> tuple[list[Action], list[str]]:
    actions: list[Action] = []
    notes: list[str] = []
    every_ref = [r for rs in refs_by_ws.values() for r in rs]
    path_of = {p.stem: p for p in tabs_by_ws}
    alive = set(live_ids.values())
    claims: dict[int, set[str]] = {}            # id -> every backup that has held it, now or before
    for k, r in doc["rows"].items():
        for i in [r["strategies"]["Id"], *r.get("idHistory", [])]:
            claims.setdefault(int(i), set()).add(k)
    for key, live_id in sorted(live_ids.items()):
        row = doc["rows"].get(key)
        if row is None:
            continue
        name = row["strategies"]["Name"]
        mine = [r for r in every_ref if r.strategy_id == live_id]
        others = claims.get(live_id, set()) - {key}
        if mine and not others:                                                      # rule 1
            continue
        for r in mine:                    # NT8 reuses Ids: an entry another backup also held proves nothing
            before = ", ".join(sorted(doc["rows"][o]["strategies"]["Name"] for o in others))
            notes.append(f"chart tab '{r.tab_name or r.tab}' ({r.workspace}) lists id {live_id}, which is now "
                         f"{name} but belonged to {before} before - left alone; check that chart")
        dead = {i for i in row.get("idHistory", []) if i != live_id and i not in alive}
        stale = [r for r in every_ref if r.strategy_id in dead]
        if stale:                                                                    # rule 2
            for r in stale:
                actions.append(Action("repoint", name, path_of[r.workspace], r.chart, r.tab, r.tab_name,
                                      r.slot, r.bars_index, r.strategy_id, live_id))
            continue
        if key in restored:                                                          # rule 3
            for c in row.get("charts", []):
                p = path_of.get(c["workspace"])
                if p is None or (c["chart"], c["tab"]) not in tabs_by_ws.get(p, set()):
                    notes.append(f"{name}: its chart tab '{c.get('tabName') or c['tab']}' in "
                                 f"{c['workspace']} no longer exists")
                    continue
                actions.append(Action("insert", name, p, c["chart"], c["tab"], c.get("tabName", ""),
                                      None, int(c.get("barsIndex", 0)), None, live_id))
    return actions, notes                                                           # rules 4, 5: no action


def apply(actions: list[Action], backup_root: Path, stamp: str) -> tuple[list[str], list[str]]:
    done: list[str] = []
    failed: list[str] = []
    backed: set[Path] = set()
    for a in actions:
        try:
            if a.workspace not in backed:
                workspaces.backup(a.workspace, backup_root, stamp)
                backed.add(a.workspace)
            if a.kind == "repoint":
                workspaces.repoint(a.workspace, a.chart, a.tab, a.slot, a.old_id, a.new_id)
            else:
                workspaces.insert(a.workspace, a.chart, a.tab, a.bars_index, a.new_id)
            done.append(a.describe())
        except (workspaces.WorkspaceEditError, OSError) as ex:
            failed.append(f"{a.describe()} - FAILED: {ex}")
    workspaces.prune_backups(backup_root)
    return done, failed


def stamp_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")


def main(argv: list[str] | None = None, *, nt8_running=paths.nt8_running, db_in_use=paths.file_in_use) -> int:
    ap = argparse.ArgumentParser(description="Put strategies back on their charts (NinjaTrader must be closed).")
    ap.add_argument("--nt8-dir")
    ap.add_argument("--guard-home")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args(argv)
    nt8 = paths.nt8_data_dir(a.nt8_dir)
    home = paths.guard_home(a.guard_home)
    # NT8 holds its database open while it runs: an open database means its NT8 is up, however the
    # folder is spelled, and that NT8 will rewrite these workspace files from memory when it closes.
    if (paths.is_live_nt8_dir(nt8) and nt8_running()) or db_in_use(nt8 / "db" / "NinjaTrader.sqlite"):
        print("REFUSED: NinjaTrader is running (or something has its database open). It rewrites its "
              "workspace files from memory when it closes, so an edit now would be lost. Close NinjaTrader "
              "first.", file=sys.stderr)
        return 1
    try:
        doc = registry.load(paths.backups_dir(home) / "rows.json")
    except registry.RegistryError as ex:
        print(f"ERROR: {ex}", file=sys.stderr)
        return 1
    db = nt8 / "db" / "NinjaTrader.sqlite"
    con = sqlite3.connect(f"file:{db.as_posix()}?mode=ro", uri=True)
    try:
        live_ids = nt8db.live_strategy_ids(con.cursor())
    finally:
        con.close()
    refs, tabs, notes = scan(nt8)
    actions, more = plan(doc, live_ids, refs, tabs, set())
    notes += more
    report = {"planned": [x.describe() for x in actions], "done": [], "failed": [], "notes": notes}
    if a.apply and actions:
        report["done"], report["failed"] = apply(actions, paths.backups_dir(home) / "workspaces", stamp_now())
    if a.json:
        print(json.dumps(report, indent=1))
    else:
        for n in notes:
            print(f"  note    {n}")
        for line in report["done"] if a.apply else report["planned"]:
            print(f"  {'done  ' if a.apply else 'plan  '}  {line}")
        for line in report["failed"]:
            print(f"  FAIL    {line}")
        if not actions:
            print("every strategy is on the chart it belongs to")
    if report["failed"]:
        return 1
    return 0 if (a.apply or not actions) else 2
