"""NinjaTrader's database, as far as strategy backups need it.

layout_problems() is the version-safety gate: Guard only writes a database whose strategy tables
are exactly the layout it was built against (NT8 8.1.6.3 - 8.1.8.3), and only reads one that has the
columns it reads. Anything else is reported and nothing is written.
"""
from __future__ import annotations

import base64
import shutil
import sqlite3
from datetime import datetime, timedelta
from pathlib import Path

EXACT = {
    "Strategies": {"Id", "Category", "Classname", "IsReplay", "IsResetOnNewTradingDay", "IsTerminal",
                   "Name", "ServerId", "Template", "Userdata", "Workspace"},
    "Strategy2Account": {"Account", "Strategy", "Nr"},
    "Strategy2Instrument": {"Instrument", "Strategy", "Nr"},
}
READ = {
    "Accounts": {"Id", "Name"},
    "Instruments": {"Id", "Expiry", "MasterInstrument"},
    "MasterInstruments": {"Id", "Name"},
}
# NT8's own ATM strategy rows share the table; exclude exactly that class. A pattern match would also
# drop any user strategy whose class name merely contains "AtmStrategy" - silently never backed up.
LIVE_WHERE = "Classname <> 'NinjaTrader.NinjaScript.AtmStrategy' and IsTerminal = 0"


def layout_problems(con: sqlite3.Connection) -> list[str]:
    problems: list[str] = []
    for table, want in {**EXACT, **READ}.items():
        have = {r[1] for r in con.execute(f'pragma table_info("{table}")')}
        if not have:
            problems.append(f"table {table} is missing")
            continue
        missing = sorted(want - have)
        if missing:
            problems.append(f"{table} has no column {', '.join(missing)}")
        if table in EXACT:
            extra = sorted(have - want)
            if extra:
                problems.append(f"{table} has unexpected column {', '.join(extra)}")
    return problems


def live_strategy_ids(cur: sqlite3.Cursor) -> dict[str, int]:
    return {f"{name}@{cls}": sid for sid, name, cls in
            cur.execute(f"select Id, Name, Classname from Strategies where {LIVE_WHERE}")}


def expiry_label(ticks) -> str:
    """NT8 stores a contract's expiry as .NET ticks; 'MM-YY' as NT8 names the contract, '' for none."""
    if not ticks or ticks <= 0:
        return ""
    try:
        d = datetime(1, 1, 1) + timedelta(microseconds=int(ticks) // 10)
    except OverflowError:
        return ""
    return d.strftime("%m-%y") if 2000 <= d.year < 2100 else ""


def labels(cur: sqlite3.Cursor, sid: int) -> dict:
    """The account and instrument names a strategy runs on, for people to read (the panel's Strategy
    backups list). A link to a row deleted since reads 'account 99'."""
    accounts = [r[1] if r[1] is not None else f"account {r[0]}" for r in cur.execute(
        "select l.Account, a.Name from Strategy2Account l left join Accounts a on a.Id = l.Account "
        "where l.Strategy = ? order by l.Nr", (sid,)).fetchall()]
    instruments = []
    for iid, master, expiry in cur.execute(
            "select l.Instrument, m.Name, i.Expiry from Strategy2Instrument l "
            "left join Instruments i on i.Id = l.Instrument left join MasterInstruments m on m.Id = i.MasterInstrument "
            "where l.Strategy = ? order by l.Nr", (sid,)).fetchall():
        if master is None:
            instruments.append(f"instrument {iid}")
        else:
            mm = expiry_label(expiry)
            instruments.append(f"{master} {mm}" if mm else master)
    return {"accounts": accounts, "instruments": instruments}


def read_live(con: sqlite3.Connection) -> list[dict]:
    """Every live strategy row verbatim, with its account/instrument link rows (needs Row factory),
    plus the names those links point at (labels)."""
    out = []
    cur = con.cursor()
    for fr in cur.execute(f"select * from Strategies where {LIVE_WHERE} and Userdata is not null order by Id").fetchall():
        cols = {k: fr[k] for k in fr.keys()}
        raw = cols["Userdata"]
        cols["Userdata"] = base64.b64encode(raw).decode("ascii")
        out.append({
            "strategies": cols,
            "userdata": raw,
            "strategy2account": [dict(x) for x in cur.execute(
                "select Account, Strategy, Nr from Strategy2Account where Strategy = ? order by Nr", (fr["Id"],))],
            "strategy2instrument": [dict(x) for x in cur.execute(
                "select Instrument, Strategy, Nr from Strategy2Instrument where Strategy = ? order by Nr", (fr["Id"],))],
            "labels": labels(cur, fr["Id"]),
        })
    return out


def snapshot(db: Path, dest: Path) -> Path:
    """Copy a database NinjaTrader holds open. A share-tolerant streamed read works where
    shutil.copy2's open fails on the lock."""
    try:
        with open(db, "rb", buffering=0) as src, open(dest, "wb") as dst:
            shutil.copyfileobj(src, dst, 1 << 20)
    except PermissionError:
        shutil.copy2(db, dest)
    return dest
