"""The backup registry: every strategy Guard has seen, verbatim, and where it lived.

rows.json (version 2) is keyed name@class. Each row holds the Strategies columns (Userdata base64)
and the account/instrument link rows exactly as NT8 wrote them, so a restore re-inserts NT8's own
bytes. Plus:
  state         live | missing | removed
  idHistory     earlier DB Ids (a hand re-add or a remapped restore gives the strategy a new Id)
  charts        the chart tabs it sat on, from the saved workspaces; kept after NT8 forgets them
  labels        {accounts, instruments}: the names its link rows pointed at when last saved, for people
  lastSeenUtc / lastSeenNt8StartUtc    the save that last saw it, and NT8's start time then

LOST vs REMOVED. A strategy that disappears between two saves made under DIFFERENT NinjaTrader start
times went missing across a restart (NT8 drops switched-off strategies when it closes): `missing`,
put back automatically. One that disappears within a single NT8 session was removed by the user:
`removed`, only put back on request. Not knowing the start time counts as a restart.
"""
from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path

from .workspaces import StrategyRef

VERSION = 2


class RegistryError(Exception):
    pass


@dataclass(frozen=True)
class Transition:
    key: str
    name: str
    old: str
    new: str


def empty() -> dict:
    return {"version": VERSION, "utc": None, "nt8StartUtc": None, "rows": {}}


def load(path: Path) -> dict:
    if not path.exists():
        return empty()
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as ex:
        raise RegistryError(f"cannot read {path}: {ex}") from ex
    if not isinstance(doc, dict) or doc.get("version") != VERSION:
        raise RegistryError(f"{path} is not a version-{VERSION} backup list")
    return doc


def save(path: Path, doc: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    os.replace(tmp, path)


def key_of(name: str, classname: str) -> str:
    return f"{name}@{classname}"


def update(doc: dict, live: list[dict], now: str, nt8_start: str | None) -> list[Transition]:
    rows = doc["rows"]
    seen: set[str] = set()
    out: list[Transition] = []
    for rec in live:
        s = rec["strategies"]
        k = key_of(s["Name"], s["Classname"])
        seen.add(k)
        row = rows.setdefault(k, {"idHistory": [], "charts": [], "state": "live", "stateSinceUtc": now})
        prev = row.get("strategies", {}).get("Id")
        if prev is not None and prev != s["Id"] and prev not in row["idHistory"]:
            row["idHistory"].append(prev)
        if row["state"] != "live":
            out.append(Transition(k, s["Name"], row["state"], "live"))
            row["state"], row["stateSinceUtc"] = "live", now
        row.update(strategies=s, strategy2account=rec["strategy2account"],
                   strategy2instrument=rec["strategy2instrument"], labels=rec.get("labels", {}),
                   lastSeenUtc=now, lastSeenNt8StartUtc=nt8_start)
    for k, row in rows.items():
        if k in seen or row["state"] != "live":
            continue
        same_session = nt8_start is not None and row.get("lastSeenNt8StartUtc") == nt8_start
        new = "removed" if same_session else "missing"
        out.append(Transition(k, row["strategies"]["Name"], "live", new))
        row["state"], row["stateSinceUtc"] = new, now
    doc["utc"], doc["nt8StartUtc"] = now, nt8_start
    return out


def owner_of(doc: dict, strategy_id: int) -> str | None:
    for k, row in doc["rows"].items():
        if row["strategies"]["Id"] == strategy_id:
            return k
    for k, row in doc["rows"].items():
        if strategy_id in row.get("idHistory", []):
            return k
    return None


def record_charts(doc: dict, refs: list[StrategyRef]) -> None:
    found: dict[str, list[dict]] = {}
    for r in refs:
        k = owner_of(doc, r.strategy_id)
        if k is None:
            continue
        rec = r.record()
        if rec not in found.setdefault(k, []):
            found[k].append(rec)
    for k, charts in found.items():
        doc["rows"][k]["charts"] = charts


def mark_restored(doc: dict, key: str, new_id: int, now: str) -> None:
    row = doc["rows"][key]
    old = row["strategies"]["Id"]
    if old != new_id and old not in row["idHistory"]:
        row["idHistory"].append(old)
    row["strategies"]["Id"] = new_id
    for link in row.get("strategy2account", []) + row.get("strategy2instrument", []):
        link["Strategy"] = new_id
    row["state"], row["stateSinceUtc"] = "live", now


def forget(doc: dict, names: list[str]) -> tuple[list[str], list[tuple[str, str]]]:
    forgotten: list[str] = []
    kept: list[tuple[str, str]] = []
    for name in names:
        hits = [k for k, r in doc["rows"].items() if r["strategies"]["Name"] == name]
        if not hits:
            kept.append((name, "not in the backups"))
        for k in hits:
            if doc["rows"][k]["state"] == "live":
                kept.append((name, "is in NinjaTrader's list right now - remove it there first"))
            else:
                forgotten.append(k)
                del doc["rows"][k]
    return forgotten, kept


def import_legacy(legacy: dict, nt8_start: str | None) -> dict:
    """Convert the pre-Guard registry (NT8Overwatch.grid/rows.json: {"utc", "rows"}): a row seen at
    its last export is live (stamped with the current NT8 session), anything older was removed."""
    doc = empty()
    stamp = legacy.get("utc")
    for k, r in legacy.get("rows", {}).items():
        is_live = r.get("lastSeenUtc") == stamp
        doc["rows"][k] = {
            "strategies": r["strategies"],
            "strategy2account": r.get("strategy2account", []),
            "strategy2instrument": r.get("strategy2instrument", []),
            "lastSeenUtc": r.get("lastSeenUtc"),
            "lastSeenNt8StartUtc": nt8_start if is_live else None,
            "state": "live" if is_live else "removed",
            "stateSinceUtc": r.get("lastSeenUtc"),
            "idHistory": [],
            "charts": [],
        }
    doc["utc"], doc["nt8StartUtc"] = stamp, nt8_start
    return doc
