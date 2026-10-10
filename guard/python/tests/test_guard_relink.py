from __future__ import annotations

import json

import pytest

from guard_fixtures import CLASS, add_strategy, make_db, nt8_folder, rekey, write_workspace
from nt8guard_backup import registry, relink, workspaces

K = f"Demo@{CLASS}"


def reg_with(rows: dict) -> dict:
    doc = registry.empty()
    for key, (sid, history, charts, state) in rows.items():
        doc["rows"][key] = {"strategies": {"Id": sid, "Name": key.split("@")[0], "Classname": CLASS},
                            "idHistory": history, "charts": charts, "state": state,
                            "strategy2account": [], "strategy2instrument": []}
    return doc


def chart(tab="Tab-b1", name="Alpha"):
    return {"workspace": "Side Monitor", "chart": "Chart-a1", "tab": tab, "tabName": name,
            "slot": "Strategy0", "barsIndex": 0}


@pytest.fixture()
def nt8(tmp_path):
    return nt8_folder(tmp_path)


def scan_plan(nt8, doc, live_ids, restored=frozenset()):
    refs, tabs, notes = relink.scan(nt8)
    actions, more = relink.plan(doc, live_ids, refs, tabs, set(restored))
    return actions, notes + more


def test_rule1_same_id_needs_nothing(nt8):
    write_workspace(nt8, "Side Monitor", [{"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha",
                                           "refs": [("Strategy0", 0, 746)]}])
    actions, _ = scan_plan(nt8, reg_with({K: (746, [], [chart()], "live")}), {K: 746}, {K})
    assert actions == []


def test_rule2_dead_id_from_history_is_repointed(nt8):
    p = write_workspace(nt8, "Side Monitor", [{"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha",
                                               "refs": [("Strategy0", 0, 816)]}])
    actions, _ = scan_plan(nt8, reg_with({K: (872, [816], [chart()], "live")}), {K: 872})
    assert [(a.kind, a.old_id, a.new_id, a.slot) for a in actions] == [("repoint", 816, 872, "Strategy0")]
    done, failed = relink.apply(actions, nt8.parent / "bk", "s1")
    assert failed == [] and len(done) == 1
    assert [r.strategy_id for r in workspaces.read_refs(p)] == [872]
    assert (nt8.parent / "bk" / "s1" / "Side Monitor.xml").exists()


def test_rule3_restored_strategy_put_back_on_its_tab(nt8):
    p = write_workspace(nt8, "Side Monitor", [{"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha", "refs": []}])
    actions, _ = scan_plan(nt8, reg_with({K: (746, [], [chart()], "live")}), {K: 746}, {K})
    assert [(a.kind, a.new_id) for a in actions] == [("insert", 746)]
    relink.apply(actions, nt8.parent / "bk", "s1")
    assert [(r.tab, r.strategy_id) for r in workspaces.read_refs(p)] == [("Tab-b1", 746)]


def test_rule3_skips_a_tab_that_no_longer_exists(nt8):
    write_workspace(nt8, "Side Monitor", [{"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha", "refs": []}])
    actions, notes = scan_plan(nt8, reg_with({K: (746, [], [chart("Tab-gone", "Old")], "live")}), {K: 746}, {K})
    assert actions == [] and any("no longer exists" in n for n in notes)


def test_rule4_live_strategy_on_no_chart_is_left_alone(nt8):
    p = write_workspace(nt8, "Side Monitor", [{"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha", "refs": []}])
    before = p.read_bytes()
    actions, _ = scan_plan(nt8, reg_with({K: (746, [], [chart()], "live")}), {K: 746})   # not restored now
    assert actions == [] and p.read_bytes() == before


def test_rule5_another_strategys_id_is_never_touched(nt8):
    other = f"Other@{CLASS}"
    write_workspace(nt8, "Side Monitor", [{"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha",
                                           "refs": [("Strategy0", 0, 500)]}])
    doc = reg_with({K: (872, [500], [chart()], "live"), other: (500, [], [], "live")})
    actions, _ = scan_plan(nt8, doc, {K: 872, other: 500})
    assert actions == []                                   # 500 is alive: it belongs to Other


def test_rule1_ignores_a_chart_entry_another_backup_also_claims(nt8):
    # K came back under its original id 10, but NT8 had meanwhile given 10 to X (since re-added as 20),
    # and X's chart still lists 10. That entry is not evidence K is on a chart.
    write_workspace(nt8, "Side Monitor", [
        {"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha", "refs": []},
        {"chart": "Chart-a1", "tab": "Tab-b3", "name": "Gamma", "refs": [("Strategy0", 0, 10)]}])
    x = f"X@{CLASS}"
    doc = reg_with({K: (10, [], [chart()], "live"), x: (20, [10], [chart("Tab-b3", "Gamma")], "live")})
    actions, notes = scan_plan(nt8, doc, {K: 10, x: 20}, {K})
    assert [(a.kind, a.tab, a.new_id) for a in actions] == [("insert", "Tab-b1", 10)]
    assert any("belonged to" in n for n in notes)


def test_unreadable_workspace_is_skipped_with_a_note(nt8):
    (nt8 / "workspaces" / "Broken.xml").write_text("<NinjaTrader>", encoding="utf-8")
    actions, notes = scan_plan(nt8, reg_with({K: (746, [], [], "live")}), {K: 746})
    assert actions == [] and any("Broken.xml" in n for n in notes)


def cli_env(tmp_path, monkeypatch):
    nt8 = nt8_folder(tmp_path)
    con = make_db(nt8)
    add_strategy(con, 816, "Demo")
    p = write_workspace(nt8, "Side Monitor", [{"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha",
                                               "refs": [("Strategy0", 0, 816)]}])
    home = tmp_path / "guard"
    from nt8guard_backup import export
    export.main(["--nt8-dir", str(nt8), "--guard-home", str(home), "--nt8-start-utc", "none"])
    rekey(con, 816, 872)                                   # hand re-add: same strategy, new Id
    export.main(["--nt8-dir", str(nt8), "--guard-home", str(home), "--nt8-start-utc", "none"])
    con.close()                                            # NT8 closed: nothing holds the database open
    monkeypatch.setenv("NT8_DIR", str(nt8))                # this folder is "the live one"
    return nt8, home, p


def test_cli_report_then_apply(tmp_path, monkeypatch, capsys):
    nt8, home, p = cli_env(tmp_path, monkeypatch)
    base = ["--nt8-dir", str(nt8), "--guard-home", str(home)]
    assert relink.main(base, nt8_running=lambda: False) == 2
    assert [r.strategy_id for r in workspaces.read_refs(p)] == [816]          # report only
    capsys.readouterr()
    assert relink.main(base + ["--apply", "--json"], nt8_running=lambda: False) == 0
    report = json.loads(capsys.readouterr().out)
    assert len(report["done"]) == 1 and report["failed"] == []
    assert [r.strategy_id for r in workspaces.read_refs(p)] == [872]
    assert relink.main(base + ["--apply"], nt8_running=lambda: False) == 0     # nothing left to do


def test_cli_refuses_while_nt8_runs(tmp_path, monkeypatch, capsys):
    nt8, home, p = cli_env(tmp_path, monkeypatch)
    before = p.read_bytes()
    assert relink.main(["--nt8-dir", str(nt8), "--guard-home", str(home), "--apply"],
                       nt8_running=lambda: True) == 1
    assert p.read_bytes() == before
    assert "REFUSED" in capsys.readouterr().err


def test_cli_refuses_while_the_database_is_open_even_for_another_folder_spelling(tmp_path, monkeypatch, capsys):
    nt8, home, p = cli_env(tmp_path, monkeypatch)
    monkeypatch.setenv("NT8_DIR", str(tmp_path / "elsewhere"))      # not recognised as the live folder
    before = p.read_bytes()
    assert relink.main(["--nt8-dir", str(nt8), "--guard-home", str(home), "--apply"],
                       nt8_running=lambda: False, db_in_use=lambda path: True) == 1
    assert p.read_bytes() == before
    assert "REFUSED" in capsys.readouterr().err
