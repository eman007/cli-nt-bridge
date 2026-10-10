"""Save every strategy in NinjaTrader's Strategies list: a complete NT8 template for each, and the
verbatim row in the backup registry, plus which chart tab it sits on.

Reads a COPY of the database (NinjaTrader holds it open) and never deletes a template - a strategy
that has just disappeared is exactly the one whose backup must survive.

Usage: backup_export.py [--nt8-dir D] [--guard-home H] [--nt8-start-utc ISO|auto|none]
                        [--dry-run] [--forget NAME]... [--json]
Exit 0 = saved (even nothing); 1 = nothing written, or a --forget was refused.
"""
from __future__ import annotations

import argparse
import json
import os
import sqlite3
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

from . import nt8db, paths, registry, userdata, workspaces


def _args(argv):
    ap = argparse.ArgumentParser(description="Save every NinjaTrader strategy's settings (Strategy backups).")
    ap.add_argument("--nt8-dir")
    ap.add_argument("--guard-home")
    ap.add_argument("--nt8-start-utc", default="auto",
                    help="NinjaTrader's process start time (ISO UTC), 'auto' to look it up, 'none' if not running")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--forget", action="append", default=[], metavar="NAME")
    ap.add_argument("--json", action="store_true")
    return ap.parse_args(argv)


def main(argv: list[str] | None = None, *, start_probe=paths.nt8_start_utc) -> int:
    a = _args(argv)
    nt8 = paths.nt8_data_dir(a.nt8_dir)
    reg_path = paths.backups_dir(paths.guard_home(a.guard_home)) / "rows.json"
    db = nt8 / "db" / "NinjaTrader.sqlite"
    if not db.exists():
        print(f"ERROR: no NinjaTrader database at {db}", file=sys.stderr)
        return 1
    try:
        doc = registry.load(reg_path)
    except registry.RegistryError as ex:
        print(f"ERROR: {ex} - not overwriting it", file=sys.stderr)
        return 1
    start = {"auto": None, "none": None}.get(a.nt8_start_utc, a.nt8_start_utc)
    if a.nt8_start_utc == "auto":
        start = start_probe()
    now = paths.utc_now()

    snap = Path(tempfile.gettempdir()) / f"nt8guard-{os.getpid()}.sqlite"
    try:
        nt8db.snapshot(db, snap)
        con = sqlite3.connect(f"file:{snap.as_posix()}?mode=ro", uri=True)
        con.row_factory = sqlite3.Row
        try:
            problems = nt8db.layout_problems(con)
            if problems:
                for p in problems:
                    print(f"ERROR: {p} - this NinjaTrader version stores strategies differently; nothing written",
                          file=sys.stderr)
                return 1
            live = nt8db.read_live(con)
        finally:
            con.close()
    except sqlite3.DatabaseError as ex:
        print(f"ERROR: the database copy could not be read ({ex}); NinjaTrader may have been writing it. "
              f"Nothing written; the next save will try again.", file=sys.stderr)
        return 1
    finally:
        snap.unlink(missing_ok=True)

    report = {"utc": now, "nt8StartUtc": start, "dryRun": a.dry_run, "saved": [], "failed": [],
              "duplicates": [], "renamed": [], "transitions": [], "warnings": [], "forgotten": [], "kept": []}

    by_key: dict[str, list[dict]] = {}
    for rec in live:
        s = rec["strategies"]
        by_key.setdefault(registry.key_of(s["Name"], s["Classname"]), []).append(rec)
    unique = []
    for key, recs in by_key.items():
        recs.sort(key=lambda r: r["strategies"]["Id"])
        unique.append(recs[0])
        if len(recs) > 1:
            report["duplicates"].append({"name": recs[0]["strategies"]["Name"],
                                         "ids": [r["strategies"]["Id"] for r in recs]})

    # One file per strategy. Names that reduce to the same file - exact duplicates, "A/B" and "A:B"
    # (same safe name), or "Demo" and "demo" (Windows ignores case) - are told apart by Id, lowest Id
    # keeping the plain name; otherwise the later one silently overwrites the earlier one's backup.
    taken: dict[str, str] = {}
    for rec in sorted(live, key=lambda r: r["strategies"]["Id"]):
        s = rec["strategies"]
        out = userdata.template_path(nt8, s["Classname"], s["Name"])
        clash = taken.get(os.path.normcase(str(out)))
        if clash is not None:
            out = userdata.template_path(nt8, s["Classname"], s["Name"], f" (id {s['Id']})")
            if clash != s["Name"]:                       # exact duplicates are reported as DUPLICATE
                report["renamed"].append({"name": s["Name"], "clashesWith": clash, "template": str(out)})
        taken[os.path.normcase(str(out))] = s["Name"]
        try:
            strat = userdata.strategy_xml(rec["userdata"])
            if not a.dry_run:
                userdata.write_template(out, userdata.build_template(s["Classname"], strat, out.parent))
            report["saved"].append({"name": s["Name"], "template": str(out)})
        except (ValueError, ET.ParseError, OSError) as ex:
            report["failed"].append({"name": s["Name"], "error": f"{type(ex).__name__}: {ex}"})

    transitions = registry.update(doc, unique, now, start)
    report["transitions"] = [{"name": t.name, "old": t.old, "new": t.new} for t in transitions]

    refs = []
    for wf in workspaces.workspace_files(nt8):
        try:
            refs += workspaces.read_refs(wf)
        except (ET.ParseError, OSError) as ex:
            report["warnings"].append(f"{wf.name} could not be read ({ex}); its charts were not recorded")
    registry.record_charts(doc, refs)

    forgotten, kept = registry.forget(doc, a.forget)
    report["forgotten"] = [k.rsplit("@", 1)[0] for k in forgotten]     # key = name@class
    report["kept"] = [{"name": n, "why": w} for n, w in kept]

    if not a.dry_run:
        registry.save(reg_path, doc)

    if a.json:
        print(json.dumps(report, indent=1))
    else:
        _print_human(report)
    if kept:
        return 1
    return 0


def _print_human(r: dict) -> None:
    for s in r["saved"]:
        print(f"  saved   {s['name']:<32} -> {Path(s['template']).name}")
    for f in r["failed"]:
        print(f"  FAIL    {f['name']:<32} {f['error']}")
    for d in r["duplicates"]:
        print(f"  DUPLICATE {d['name']}: {len(d['ids'])} strategies share this name (ids {d['ids']}); "
              f"each has a template, but only id {d['ids'][0]} can be put back - rename the others in NinjaTrader")
    for n in r["renamed"]:
        print(f"  SAME FILE {n['name']} would share a template file with {n['clashesWith']}; "
              f"saved as {Path(n['template']).name}")
    for t in r["transitions"]:
        if t["new"] == "missing":
            print(f"  MISSING {t['name']:<32} dropped from NinjaTrader's list across a restart; "
                  f"put back automatically at the next restart")
        elif t["new"] == "removed":
            print(f"  removed {t['name']:<32} removed while NinjaTrader was running; kept in the backups")
        else:
            print(f"  back    {t['name']:<32} in NinjaTrader's list again")
    for w in r["warnings"]:
        print(f"  warning {w}")
    for name in r["forgotten"]:
        print(f"  {'would forget' if r['dryRun'] else 'forgot'} {name}")
    for k in r["kept"]:
        print(f"  KEPT    {k['name']:<32} {k['why']}")
    missing = sum(1 for t in r["transitions"] if t["new"] == "missing")
    removed = sum(1 for t in r["transitions"] if t["new"] == "removed")
    print(f"{'dry run: ' if r['dryRun'] else ''}{len(r['saved'])} saved, {len(r['failed'])} failed, "
          f"{missing} missing, {removed} removed")
