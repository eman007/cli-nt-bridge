from __future__ import annotations

import xml.etree.ElementTree as ET

import pytest

from guard_fixtures import nt8_folder, write_workspace
from nt8guard_backup import workspaces as ws

TABS = [
    {"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha", "refs": [("Strategy0", 0, 746)]},
    {"chart": "Chart-a1", "tab": "Tab-b2", "name": "Beta", "refs": []},
    {"chart": "Chart-c1", "tab": "Tab-d1", "name": "Gamma",
     "refs": [("Strategy0", 0, 859), ("Strategy1", 0, 858)]},
]


@pytest.fixture()
def wsfile(tmp_path):
    nt8 = nt8_folder(tmp_path)
    (nt8 / "workspaces" / "_Workspaces.xml").write_text("<NinjaTrader/>", encoding="utf-8")
    return write_workspace(nt8, "Side Monitor", TABS)


def test_workspace_files_skip_the_index(wsfile):
    assert ws.workspace_files(wsfile.parent.parent) == [wsfile]


def test_reads_only_tab_level_strategies(wsfile):
    refs = ws.read_refs(wsfile)
    assert [(r.chart, r.tab, r.tab_name, r.slot, r.strategy_id) for r in refs] == [
        ("Chart-a1", "Tab-b1", "Alpha", "Strategy0", 746),
        ("Chart-c1", "Tab-d1", "Gamma", "Strategy0", 859),
        ("Chart-c1", "Tab-d1", "Gamma", "Strategy1", 858),
    ]
    assert refs[0].workspace == "Side Monitor"
    assert refs[0].record() == {"workspace": "Side Monitor", "chart": "Chart-a1", "tab": "Tab-b1",
                                "tabName": "Alpha", "slot": "Strategy0", "barsIndex": 0}


def test_read_tabs(wsfile):
    assert ws.read_tabs(wsfile) == [("Chart-a1", "Tab-b1", "Alpha"), ("Chart-a1", "Tab-b2", "Beta"),
                                    ("Chart-c1", "Tab-d1", "Gamma")]


def test_repoint_changes_only_the_id(wsfile):
    before = wsfile.read_bytes()
    ws.repoint(wsfile, "Chart-c1", "Tab-d1", "Strategy1", 858, 872)
    after = wsfile.read_bytes()
    assert after.startswith(b"\xef\xbb\xbf") and b"\r\n" in after
    assert after.replace(b">872</Strategy1>", b">858</Strategy1>") == before
    assert [r.strategy_id for r in ws.read_refs(wsfile) if r.tab == "Tab-d1"] == [859, 872]


def test_repoint_refuses_when_the_old_id_is_not_there(wsfile):
    before = wsfile.read_bytes()
    with pytest.raises(ws.WorkspaceEditError):
        ws.repoint(wsfile, "Chart-c1", "Tab-d1", "Strategy1", 999, 872)
    assert wsfile.read_bytes() == before


def test_insert_into_an_empty_tab(wsfile):
    slot = ws.insert(wsfile, "Chart-a1", "Tab-b2", 0, 900)
    assert slot == "Strategy0"
    beta = [r for r in ws.read_refs(wsfile) if r.tab == "Tab-b2"]
    assert [(r.slot, r.bars_index, r.strategy_id) for r in beta] == [("Strategy0", 0, 900)]
    text = wsfile.read_bytes().decode("utf-8-sig")
    assert '          <Strategies>\r\n            <Strategy0 BarsIndex="0">900</Strategy0>\r\n          </Strategies>' in text


def test_insert_appends_after_existing_entries(wsfile):
    assert ws.insert(wsfile, "Chart-c1", "Tab-d1", 0, 901) == "Strategy2"
    assert [r.strategy_id for r in ws.read_refs(wsfile) if r.tab == "Tab-d1"] == [859, 858, 901]
    ET.parse(wsfile)                       # still well-formed


def test_unknown_tab_is_refused_and_file_untouched(wsfile):
    before = wsfile.read_bytes()
    with pytest.raises(ws.WorkspaceEditError):
        ws.insert(wsfile, "Chart-a1", "Tab-zz", 0, 900)
    assert wsfile.read_bytes() == before


def test_broken_file_raises_parse_error(tmp_path):
    p = tmp_path / "broken.xml"
    p.write_text("<NinjaTrader><NTWindows><Chart-a1>", encoding="utf-8")
    with pytest.raises(ET.ParseError):
        ws.read_refs(p)


def test_backup_and_prune(wsfile, tmp_path):
    root = tmp_path / "bk"
    for i in range(25):
        ws.backup(wsfile, root, f"20261008-0000{i:02d}")
    ws.prune_backups(root, keep=20)
    kept = sorted(p.name for p in root.iterdir())
    assert len(kept) == 20 and kept[0] == "20261008-000005"
    assert (root / "20261008-000024" / "Side Monitor.xml").read_bytes() == wsfile.read_bytes()
