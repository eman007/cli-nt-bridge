"""Which chart each strategy sits on, read from NT8's saved workspace files - and the two edits
Guard makes to them: point a chart at a strategy's new DB Id, or put a strategy back on a chart.

A chart window is <Chart-<guid>> > <TabControl> > <Tab-<guid>>, and each tab lists its strategies by
DB Id: <Strategies><Strategy0 BarsIndex="0">746</Strategy0></Strategies>. Edits are text edits, so
every byte outside the changed element stays as NT8 wrote it (BOM, CRLF, attribute order).
Only run with NinjaTrader CLOSED: it rewrites the loaded workspace from memory when it exits.
"""
from __future__ import annotations

import os
import re
import shutil
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

SLOT = re.compile(r"Strategy(\d+)")
BOM = b"\xef\xbb\xbf"


class WorkspaceEditError(Exception):
    pass


@dataclass(frozen=True)
class StrategyRef:
    workspace: str
    chart: str
    tab: str
    tab_name: str
    slot: str
    bars_index: int
    strategy_id: int

    def record(self) -> dict:
        return {"workspace": self.workspace, "chart": self.chart, "tab": self.tab,
                "tabName": self.tab_name, "slot": self.slot, "barsIndex": self.bars_index}


def workspace_files(nt8: Path) -> list[Path]:
    d = nt8 / "workspaces"
    return sorted(p for p in d.glob("*.xml") if not p.name.startswith("_")) if d.is_dir() else []


def _chart_tabs(root: ET.Element):
    for win in root.iter():
        if not win.tag.startswith("Chart-"):
            continue
        control = win.find("TabControl")
        if control is None:
            continue
        for tab in control:
            if tab.tag.startswith("Tab-"):
                yield win.tag, tab


def read_tabs(path: Path) -> list[tuple[str, str, str]]:
    root = ET.parse(path).getroot()
    return [(chart, tab.tag, (tab.findtext("TabName") or "").strip()) for chart, tab in _chart_tabs(root)]


def read_refs(path: Path) -> list[StrategyRef]:
    root = ET.parse(path).getroot()
    out: list[StrategyRef] = []
    for chart, tab in _chart_tabs(root):
        listing = tab.find("Strategies")
        if listing is None:
            continue
        name = (tab.findtext("TabName") or "").strip()
        for s in listing:
            text = (s.text or "").strip()
            if SLOT.fullmatch(s.tag) and text.isdigit():
                out.append(StrategyRef(path.stem, chart, tab.tag, name, s.tag,
                                       int(s.get("BarsIndex", "0")), int(text)))
    return out


def _read(path: Path) -> tuple[bytes, str]:
    raw = path.read_bytes()
    bom = BOM if raw.startswith(BOM) else b""
    return bom, raw[len(bom):].decode("utf-8")


def _write(path: Path, bom: bytes, text: str) -> None:
    tmp = path.with_name(path.name + ".guard-tmp")
    tmp.write_bytes(bom + text.encode("utf-8"))
    os.replace(tmp, path)


def _span(text: str, tag: str, start: int = 0, end: int | None = None) -> tuple[int, int]:
    end = len(text) if end is None else end
    i = text.find(f"<{tag}>", start, end)
    j = text.find(f"</{tag}>", start, end)
    if i < 0 or j < i or text.find(f"<{tag}>", i + 1, end) >= 0:
        raise WorkspaceEditError(f"<{tag}> not found exactly once")
    return i, j + len(tag) + 3


def _indent(text: str, i: int) -> str:
    return text[text.rfind("\n", 0, i) + 1:i]


def _tab_listing(text: str, chart: str, tab: str) -> tuple[int, int, str]:
    """(start, end, indent) of the tab's own <Strategies> element: the one exactly one level below the
    tab, never a <Strategies> nested inside an indicator."""
    c0, c1 = _span(text, chart)
    t0, t1 = _span(text, tab, c0, c1)
    want = _indent(text, t0) + "  "
    for m in re.finditer(r"<Strategies\s*/>|<Strategies>", text[t0:t1]):
        i = t0 + m.start()
        if _indent(text, i) != want:
            continue
        if m.group(0).endswith("/>"):
            return i, i + len(m.group(0)), want
        j = text.find("</Strategies>", i, t1)
        if j < 0:
            raise WorkspaceEditError(f"{tab}: unterminated <Strategies>")
        return i, j + len("</Strategies>"), want
    raise WorkspaceEditError(f"{tab} has no <Strategies> list at tab level")


def repoint(path: Path, chart: str, tab: str, slot: str, old_id: int, new_id: int) -> None:
    bom, text = _read(path)
    s0, s1, _ = _tab_listing(text, chart, tab)
    block = text[s0:s1]
    pat = re.compile(rf"(<{slot}\b[^>]*>)\s*{old_id}\s*(</{slot}>)")
    if len(pat.findall(block)) != 1:
        raise WorkspaceEditError(f"{tab}/{slot} does not hold id {old_id} exactly once")
    block = pat.sub(lambda m: f"{m.group(1)}{new_id}{m.group(2)}", block, count=1)
    _write(path, bom, text[:s0] + block + text[s1:])


def insert(path: Path, chart: str, tab: str, bars_index: int, new_id: int) -> str:
    bom, text = _read(path)
    nl = "\r\n" if "\r\n" in text else "\n"
    s0, s1, ind = _tab_listing(text, chart, tab)
    block = text[s0:s1]
    used = [int(n) for n in re.findall(r"<Strategy(\d+)\b", block)]
    slot = f"Strategy{max(used) + 1 if used else 0}"
    line = f'{ind}  <{slot} BarsIndex="{bars_index}">{new_id}</{slot}>'
    if block.endswith("/>"):
        new_block = f"<Strategies>{nl}{line}{nl}{ind}</Strategies>"
    else:
        head = block[:block.rfind("</Strategies>")].rstrip(" \t")
        new_block = f"{head}{line}{nl}{ind}</Strategies>"
    _write(path, bom, text[:s0] + new_block + text[s1:])
    return slot


def backup(path: Path, dest_root: Path, stamp: str) -> Path:
    d = dest_root / stamp
    d.mkdir(parents=True, exist_ok=True)
    dst = d / path.name
    shutil.copy2(path, dst)
    return dst


def prune_backups(dest_root: Path, keep: int = 20) -> None:
    if not dest_root.is_dir():
        return
    for old in sorted(p for p in dest_root.iterdir() if p.is_dir())[:-keep]:
        shutil.rmtree(old, ignore_errors=True)
