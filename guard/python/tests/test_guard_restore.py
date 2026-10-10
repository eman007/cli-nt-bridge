from __future__ import annotations

import json
import sqlite3

import pytest

from guard_fixtures import CLASS, add_strategy, delete_like_nt8, make_db, nt8_folder, write_workspace  # noqa: F401
from nt8guard_backup import export, registry, restore, workspaces

S1, S2 = "2026-10-08T01:00:00Z", "2026-10-08T05:00:00Z"


@pytest.fixture()
def env(tmp_path, monkeypatch):
    nt8 = nt8_folder(tmp_path)
    con = make_db(nt8)
    add_strategy(con, 746, "Demo")
    add_strategy(con, 747, "Second")
    write_workspace(nt8, "Side Monitor", [
        {"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha", "refs": [("Strategy0", 0, 746)]},
        {"chart": "Chart-a1", "tab": "Tab-b2", "name": "Beta", "refs": [("Strategy0", 0, 747)]}])
    home = tmp_path / "guard"
    monkeypatch.setenv("NT8_DIR", str(nt8))
    exp(nt8, home, S1)
    return nt8, home, con


def exp(nt8, home, start):
    assert export.main(["--nt8-dir", str(nt8), "--guard-home", str(home), "--nt8-start-utc", start]) == 0


def rs(env, *extra, running=False, in_use=False):
    # The fixture keeps its own connection open, so "is the database file open" is injected here;
    # test_guard_paths proves the real check, test_refused_while_the_database_is_open the refusal.
    nt8, home, _ = env
    return restore.main(["--nt8-dir", str(nt8), "--guard-home", str(home), *extra],
                        nt8_running=lambda: running, db_in_use=lambda p: in_use)


def test_refused_while_the_database_is_open_even_for_another_folder_spelling(env, monkeypatch, capsys):
    monkeypatch.setenv("NT8_DIR", str(env[0].parent / "elsewhere"))   # not recognised as the live folder
    delete_like_nt8(env[2], 746)
    assert rs(env, "--apply", in_use=True) == 1
    assert "REFUSED" in capsys.readouterr().err
    assert rowset(env[2], 746)[0] is None


def test_db_override_is_backed_up_too(env, tmp_path):
    delete_like_nt8(env[2], 746)
    copy = tmp_path / "copy.sqlite"
    copy.write_bytes((env[0] / "db" / "NinjaTrader.sqlite").read_bytes())
    assert rs(env, "--apply", "--db", str(copy)) == 0
    assert list(tmp_path.glob("copy.sqlite.bak-restore-*"))


def rowset(con, sid):
    con.row_factory = sqlite3.Row
    s = con.execute("select * from Strategies where Id = ?", (sid,)).fetchone()
    return (dict(s) if s else None,
            [dict(r) for r in con.execute("select * from Strategy2Account where Strategy = ?", (sid,))],
            [dict(r) for r in con.execute("select * from Strategy2Instrument where Strategy = ?", (sid,))])


def test_dropped_at_shutdown_is_restored_byte_identical(env):
    before = rowset(env[2], 746)
    delete_like_nt8(env[2], 746)                       # NT8 closed and dropped it; no save since
    assert rs(env) == 2                                # report: something to do, nothing written
    assert rowset(env[2], 746)[0] is None
    assert rs(env, "--apply") == 0
    assert rowset(env[2], 746) == before
    assert list((env[0] / "db").glob("NinjaTrader.sqlite.bak-restore-*"))
    assert rs(env, "--apply") == 0                     # second run: nothing to do


def test_missing_across_restart_is_restored_removed_is_not(env, capsys):
    delete_like_nt8(env[2], 746)
    exp(env[0], env[1], S2)                            # other session -> missing
    delete_like_nt8(env[2], 747)
    exp(env[0], env[1], S2)                            # same session -> removed
    assert rs(env, "--apply") == 0
    assert rowset(env[2], 746)[0] is not None
    assert rowset(env[2], 747)[0] is None
    assert "removed while NinjaTrader was running" in capsys.readouterr().out
    assert rs(env, "--apply", "--name", "Second") == 0
    assert rowset(env[2], 747)[0] is not None


def test_two_remaps_in_one_pass_get_distinct_ids(env):
    delete_like_nt8(env[2], 746)
    delete_like_nt8(env[2], 747)
    for squat in (746, 747):                           # both original ids taken by something else
        env[2].execute("insert into Strategies (Id, Classname, IsTerminal, Name, Userdata) "
                       "values (?, 'NinjaTrader.NinjaScript.AtmStrategy', 1, 'atm', x'00')", (squat,))
    env[2].commit()
    assert rs(env, "--apply") == 0
    ids = {r[0]: r[1] for r in env[2].execute("select Name, Id from Strategies where Name in ('Demo','Second')")}
    assert ids["Demo"] != ids["Second"] and min(ids.values()) > 747
    for sid in ids.values():
        _, acc, ins = rowset(env[2], sid)
        assert acc and ins
    p = env[0] / "workspaces" / "Side Monitor.xml"     # rule 2 via remap: charts follow the new ids
    assert sorted(r.strategy_id for r in workspaces.read_refs(p)) == sorted(ids.values())
    doc = registry.load(env[1] / "state" / "backups" / "rows.json")
    assert doc["rows"][f"Demo@{CLASS}"]["idHistory"] == [746]


def test_mixed_reuse_and_remap_never_collides_or_crosses_charts(env, capsys):
    # NT8 drops both at a restart, then hands a NEW strategy id 746 (it reuses max+1 ids).
    delete_like_nt8(env[2], 746)
    delete_like_nt8(env[2], 747)
    add_strategy(env[2], 746, "New")
    exp(env[0], env[1], S2)                                  # Demo, Second -> missing; New live
    capsys.readouterr()
    assert rs(env, "--apply") == 0
    ids = {n: i for n, i in env[2].execute("select Name, Id from Strategies where Name in ('Demo','Second','New')")}
    assert ids["New"] == 746 and ids["Second"] == 747
    assert ids["Demo"] not in (746, 747)
    tabs: dict[str, list[int]] = {}
    for r in workspaces.read_refs(env[0] / "workspaces" / "Side Monitor.xml"):
        tabs.setdefault(r.tab, []).append(r.strategy_id)
    assert tabs["Tab-b2"] == [747]                           # Second alone on its own tab
    assert ids["Demo"] in tabs["Tab-b1"]                     # Demo back on its own tab
    assert "belonged to" in capsys.readouterr().out          # the reused id on Demo's tab is reported


def test_restored_strategy_is_put_back_on_its_tab(env):
    p = env[0] / "workspaces" / "Side Monitor.xml"
    delete_like_nt8(env[2], 747)
    lost = b'          <Strategies>\r\n            <Strategy0 BarsIndex="0">747</Strategy0>\r\n          </Strategies>'
    raw = p.read_bytes()
    assert raw.count(lost) == 1
    p.write_bytes(raw.replace(lost, b"          <Strategies />"))     # a layout save that lost it
    assert rs(env, "--apply") == 0
    assert [(r.tab, r.strategy_id) for r in workspaces.read_refs(p) if r.tab == "Tab-b2"] == [("Tab-b2", 747)]


def test_fk_target_gone_is_refused_and_db_untouched(env):
    delete_like_nt8(env[2], 746)
    env[2].execute("pragma foreign_keys = off")
    env[2].execute("delete from Accounts where Id = 1")
    env[2].commit()
    n = env[2].execute("select count(*) from Strategies").fetchone()[0]
    assert rs(env, "--apply") == 1
    assert env[2].execute("select count(*) from Strategies").fetchone()[0] == n


def test_layout_problem_refused(env, capsys):
    delete_like_nt8(env[2], 746)
    env[2].execute("alter table Strategies add column Owner text")
    env[2].commit()
    assert rs(env, "--apply") == 1
    assert "unexpected column Owner" in capsys.readouterr().err
    assert rowset(env[2], 746)[0] is None


def test_refused_while_nt8_runs(env, capsys):
    delete_like_nt8(env[2], 746)
    assert rs(env, "--apply", running=True) == 1
    assert "REFUSED" in capsys.readouterr().err
    assert rowset(env[2], 746)[0] is None


def test_corrupt_registry_refused(env, capsys):
    (env[1] / "state" / "backups" / "rows.json").write_text("{", encoding="utf-8")
    assert rs(env, "--apply") == 1
    assert (env[1] / "state" / "backups" / "rows.json").read_text(encoding="utf-8") == "{"


def test_json_report(env, capsys):
    delete_like_nt8(env[2], 746)
    capsys.readouterr()
    assert rs(env, "--apply", "--json") == 0
    out = json.loads(capsys.readouterr().out)
    assert out["restored"] == [{"name": "Demo", "id": 746, "originalId": 746}]
    assert out["refused"] == [] and out["relinkFailed"] == []
