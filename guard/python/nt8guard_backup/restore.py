"""Put strategies NinjaTrader dropped from its list back - then back on their charts.

NinjaTrader must be CLOSED: a row written under a running NT8 is overwritten from memory when it
exits, and so are the workspace files. Report only, unless --apply. The database is backed up first
(last 3 kept). One transaction per strategy, with foreign_key_check and integrity_check compared to a
baseline from the untouched database. The original Id is reused when free; otherwise a fresh Id is
taken AT INSERT TIME (two remaps in one pass must not collide) and the chart follows via relink.
A strategy whose account or instrument no longer exists is refused, never half-restored.

Which strategies: by default those absent from the database whose backup state is `live` (present at
the last save, dropped at shutdown) or `missing` (dropped across a restart). `removed` ones only with
--name or --all.

Usage: backup_restore.py [--nt8-dir D] [--guard-home H] [--name N]... [--all] [--apply] [--no-relink] [--json]
Exit 0 = nothing to do / all done; 2 = report mode with something to do; 1 = refused or failed.
"""
from __future__ import annotations

import argparse
import base64
import json
import shutil
import sqlite3
import sys
from pathlib import Path

from . import nt8db, paths, registry, relink

DB_BACKUPS_KEPT = 3
COLS = ("Id", "Category", "Classname", "IsReplay", "IsResetOnNewTradingDay", "IsTerminal",
        "Name", "ServerId", "Template", "Userdata", "Workspace")


def choose(doc: dict, present: set[str], names: list[str], everything: bool) -> tuple[dict, list[str]]:
    absent = {k: r for k, r in doc["rows"].items() if k not in present}
    notes: list[str] = []
    if names:
        want = set(names)
        known = {r["strategies"]["Name"] for r in doc["rows"].values()}
        notes += [f"{n}: not in the backups" for n in sorted(want - known)]
        return {k: r for k, r in absent.items() if r["strategies"]["Name"] in want}, notes
    if everything:
        return absent, notes
    pick = {k: r for k, r in absent.items() if r.get("state") in ("live", "missing")}
    notes += [f"{r['strategies']['Name']}: removed while NinjaTrader was running - not put back "
              f"automatically (--name brings it back)" for k, r in sorted(absent.items()) if k not in pick]
    return pick, notes


def fk_problems(cur: sqlite3.Cursor, row: dict) -> list[str]:
    out = []
    for acc in row.get("strategy2account", []):
        if not cur.execute("select 1 from Accounts where Id = ?", (acc["Account"],)).fetchone():
            out.append(f"account id {acc['Account']} no longer exists")
    for ins in row.get("strategy2instrument", []):
        if not cur.execute("select 1 from Instruments where Id = ?", (ins["Instrument"],)).fetchone():
            out.append(f"instrument id {ins['Instrument']} no longer exists")
    return out


def insert_row(cur: sqlite3.Cursor, row: dict, new_id: int) -> None:
    s = row["strategies"]
    vals = [new_id if c == "Id" else (base64.b64decode(s[c]) if c == "Userdata" else s[c]) for c in COLS]
    cur.execute(f"insert into Strategies ({', '.join(COLS)}) values ({', '.join('?' * len(COLS))})", vals)
    for acc in row.get("strategy2account", []):
        cur.execute("insert into Strategy2Account (Account, Strategy, Nr) values (?, ?, ?)",
                    (acc["Account"], new_id, acc["Nr"]))
    for ins in row.get("strategy2instrument", []):
        cur.execute("insert into Strategy2Instrument (Instrument, Strategy, Nr) values (?, ?, ?)",
                    (ins["Instrument"], new_id, ins["Nr"]))


def fresh_id(cur: sqlite3.Cursor, doc: dict, workspace_ids: set[int]) -> int:
    """An Id nothing has ever meant: above the database's, every backed-up strategy's current and past
    Ids, and every Id a workspace chart lists. NT8 hands new rows max(Id)+1 and so reuses the Ids of
    dropped strategies; a plain max(Id)+1 here could take an Id a later strategy in this same pass
    still needs, or one a chart still lists for a different strategy."""
    known = [int(cur.execute("select coalesce(max(Id), 0) from Strategies").fetchone()[0])]
    for row in doc["rows"].values():
        known.append(int(row["strategies"]["Id"]))
        known += [int(i) for i in row.get("idHistory", [])]
    known += list(workspace_ids)
    return max(known) + 1


def backup_db(db: Path, stamp: str) -> Path:
    bak = db.with_name(f"{db.name}.bak-restore-{stamp}")
    shutil.copy2(db, bak)
    for old in sorted(db.parent.glob(f"{db.name}.bak-restore-*"))[:-DB_BACKUPS_KEPT]:
        old.unlink(missing_ok=True)
    return bak


def main(argv: list[str] | None = None, *, nt8_running=paths.nt8_running, db_in_use=paths.file_in_use) -> int:
    ap = argparse.ArgumentParser(description="Put dropped strategies back (NinjaTrader must be closed).")
    ap.add_argument("--nt8-dir")
    ap.add_argument("--guard-home")
    ap.add_argument("--db", help="operate on this database file instead of the NT8 folder's (it is backed up too)")
    ap.add_argument("--name", action="append", default=[])
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--no-relink", action="store_true")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args(argv)
    nt8 = paths.nt8_data_dir(a.nt8_dir)
    home = paths.guard_home(a.guard_home)
    reg_path = paths.backups_dir(home) / "rows.json"
    db = Path(a.db) if a.db else nt8 / "db" / "NinjaTrader.sqlite"
    rep = {"planned": [], "restored": [], "refused": [], "notes": [], "relinked": [], "relinkFailed": [],
           "backup": None}

    def finish(code: int) -> int:
        if a.json:
            print(json.dumps(rep, indent=1))
        return code

    def say(msg: str) -> None:            # human lines; --json prints only the report
        if not a.json:
            print(msg)

    # Two independent checks: NT8 running against the folder Guard finds by itself, and the target
    # database file being open at all - which also catches the live folder spelled another way.
    live_db = nt8 / "db" / "NinjaTrader.sqlite"
    if (paths.is_live_nt8_dir(nt8) and nt8_running()) or db_in_use(db) or (db != live_db and db_in_use(live_db)):
        print("REFUSED: NinjaTrader is running (or something has its database open). A strategy written now "
              "is overwritten from memory when it closes. Close NinjaTrader first.", file=sys.stderr)
        return 1
    try:
        doc = registry.load(reg_path)
    except registry.RegistryError as ex:
        print(f"ERROR: {ex}", file=sys.stderr)
        return 1
    if not db.exists():
        print(f"ERROR: no NinjaTrader database at {db}", file=sys.stderr)
        return 1

    con = sqlite3.connect(db, isolation_level=None)
    cur = con.cursor()
    try:
        problems = nt8db.layout_problems(con)
        if problems:
            for p in problems:
                print(f"REFUSED: {p} - this NinjaTrader version stores strategies differently; nothing written",
                      file=sys.stderr)
            return 1
        present = set(nt8db.live_strategy_ids(cur))
        pick, notes = choose(doc, present, a.name, a.all)
        rep["notes"] += notes
        for n in notes:
            say(f"  note    {n}")
        base_fk = len(cur.execute("pragma foreign_key_check").fetchall())
        plan = []
        for key, row in sorted(pick.items()):
            orig = int(row["strategies"]["Id"])
            taken = cur.execute("select 1 from Strategies where Id = ?", (orig,)).fetchone() is not None
            probs = fk_problems(cur, row)
            plan.append((key, row, orig, taken, probs))
            rep["planned"].append({"name": row["strategies"]["Name"], "id": None if taken else orig,
                                   "originalId": orig, "problems": probs})
            say(f"  {'REFUSE' if probs else ('apply ' if a.apply else 'plan  ')}  {row['strategies']['Name']:<32}"
                + (f" id {orig} is taken - a new id will be used" if taken else f" id {orig}")
                + (f"  ({'; '.join(probs)})" if probs else ""))
        if not a.apply:
            refs, tabs, wnotes = relink.scan(nt8)
            actions, rnotes = relink.plan(doc, nt8db.live_strategy_ids(cur), refs, tabs, set())
            for line in [x.describe() for x in actions] + wnotes + rnotes:
                say(f"  chart   {line}")
            return finish(2 if (plan or actions) else 0)

        stamp = relink.stamp_now()
        if plan:
            rep["backup"] = str(backup_db(db, stamp))
            say(f"  backup  {rep['backup']}")
        now = paths.utc_now()
        restored: set[str] = set()
        refs_now, _, _ = relink.scan(nt8)
        workspace_ids = {r.strategy_id for rs in refs_now.values() for r in rs}
        for key, row, orig, taken, probs in plan:
            name = row["strategies"]["Name"]
            if probs:
                rep["refused"].append({"name": name, "why": "; ".join(probs)})
                continue
            try:
                cur.execute("BEGIN")
                # Decided now, inside the transaction - an earlier insert in this pass may have changed it.
                free = cur.execute("select 1 from Strategies where Id = ?", (orig,)).fetchone() is None
                new_id = orig if free else fresh_id(cur, doc, workspace_ids)
                insert_row(cur, row, new_id)
                fk = len(cur.execute("pragma foreign_key_check").fetchall())
                ic = cur.execute("pragma integrity_check").fetchone()[0]
                if fk != base_fk or ic != "ok":
                    cur.execute("ROLLBACK")
                    rep["refused"].append({"name": name, "why": f"database checks failed after insert "
                                                                f"(foreign keys {base_fk}->{fk}, integrity {ic}); rolled back"})
                    continue
                cur.execute("COMMIT")
            except sqlite3.Error as ex:
                if con.in_transaction:
                    cur.execute("ROLLBACK")
                rep["refused"].append({"name": name, "why": f"{type(ex).__name__}: {ex}; rolled back"})
                continue
            registry.mark_restored(doc, key, new_id, now)
            restored.add(key)
            rep["restored"].append({"name": name, "id": new_id, "originalId": orig})
            say(f"  ok      {name:<32} id {new_id}")
        if restored:
            registry.save(reg_path, doc)

        if not a.no_relink:
            refs, tabs, wnotes = relink.scan(nt8)
            actions, rnotes = relink.plan(doc, nt8db.live_strategy_ids(cur), refs, tabs, restored)
            rep["notes"] += wnotes + rnotes
            rep["relinked"], rep["relinkFailed"] = relink.apply(
                actions, paths.backups_dir(home) / "workspaces", stamp) if actions else ([], [])
            for line in wnotes + rnotes:
                say(f"  note    {line}")
            for line in rep["relinked"]:
                say(f"  chart   {line}")
            for line in rep["relinkFailed"]:
                say(f"  FAIL    {line}")
        for r in rep["refused"]:
            say(f"  FAIL    {r['name']:<32} {r['why']}")
        if not plan and not rep["relinked"]:
            say("nothing to restore: every backed-up strategy is in NinjaTrader's list and on its chart")
        return finish(1 if (rep["refused"] or rep["relinkFailed"]) else 0)
    finally:
        con.close()
