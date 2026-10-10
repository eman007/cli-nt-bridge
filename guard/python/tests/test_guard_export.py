from __future__ import annotations

import json
import xml.etree.ElementTree as ET

import pytest

from guard_fixtures import (BRUSH, CLASS, CLASS2, TYPED_INNER, add_strategy, delete_like_nt8, make_db, nt8_folder,
                            undeclared_type_prefixes, userdata_raw, write_workspace)
from nt8guard_backup import export, registry

S1, S2 = "2026-10-08T01:00:00Z", "2026-10-08T05:00:00Z"


@pytest.fixture()
def env(tmp_path):
    nt8 = nt8_folder(tmp_path)
    con = make_db(nt8)
    add_strategy(con, 746, "Demo")
    add_strategy(con, 858, "Other", classname=CLASS2)
    write_workspace(nt8, "Side Monitor", [
        {"chart": "Chart-a1", "tab": "Tab-b1", "name": "Alpha", "refs": [("Strategy0", 0, 746)]}])
    home = tmp_path / "guard"
    return nt8, home, con


def run(env, *extra, start=S1):
    nt8, home, _ = env
    return export.main(["--nt8-dir", str(nt8), "--guard-home", str(home), "--nt8-start-utc", start or "none", *extra])


def doc(env):
    return registry.load(env[1] / "state" / "backups" / "rows.json")


def test_saves_complete_readable_templates(env, capsys):
    assert run(env) == 0
    tpl = env[0] / "templates" / "Strategy" / "DemoStrategy" / "Guard backup - Demo.xml"
    strat = ET.fromstring(tpl.read_text(encoding="utf-8-sig")).find("Strategy")[0]
    assert strat.findtext("UpColorSerializable") == BRUSH
    assert (env[0] / "templates" / "Strategy" / "Vendor.OtherStrategy" / "Guard backup - Other.xml").exists()
    rows = doc(env)["rows"]
    assert {r["state"] for r in rows.values()} == {"live"}
    assert "2 saved, 0 failed" in capsys.readouterr().out


def test_templates_keep_the_xsd_declaration_nt8_needs(env):
    add_strategy(env[2], 960, "Typed", blob=userdata_raw(TYPED_INNER))
    assert run(env) == 0
    text = (env[0] / "templates" / "Strategy" / "DemoStrategy" / "Guard backup - Typed.xml").read_text(encoding="utf-8-sig")
    assert 'xsi:type="xsd:int"' in text
    assert undeclared_type_prefixes(text) == set()


def test_removed_in_the_same_session(env, capsys):
    run(env)
    delete_like_nt8(env[2], 746)
    assert run(env) == 0
    assert doc(env)["rows"][f"Demo@{CLASS}"]["state"] == "removed"
    assert "removed" in capsys.readouterr().out


def test_missing_across_a_restart(env, capsys):
    run(env)
    delete_like_nt8(env[2], 746)
    run(env, start=S2)
    assert doc(env)["rows"][f"Demo@{CLASS}"]["state"] == "missing"
    assert "MISSING Demo" in capsys.readouterr().out


def test_missing_when_nt8_is_not_running(env):
    run(env)
    delete_like_nt8(env[2], 746)
    run(env, start=None)
    assert doc(env)["rows"][f"Demo@{CLASS}"]["state"] == "missing"


def test_rows_carry_account_and_contract_names(env):
    assert run(env) == 0
    assert doc(env)["rows"][f"Demo@{CLASS}"]["labels"] == {"accounts": ["Sim101"], "instruments": ["NQ 12-26"]}


def test_records_the_chart_tab(env):
    run(env)
    assert doc(env)["rows"][f"Demo@{CLASS}"]["charts"] == [
        {"workspace": "Side Monitor", "chart": "Chart-a1", "tab": "Tab-b1", "tabName": "Alpha",
         "slot": "Strategy0", "barsIndex": 0}]


def test_dry_run_writes_nothing(env):
    assert run(env, "--dry-run") == 0
    assert not (env[1] / "state" / "backups" / "rows.json").exists()
    assert not list((env[0] / "templates").rglob("Guard backup - *.xml"))


def test_auto_start_uses_the_probe(env, monkeypatch):
    nt8, home, _ = env
    assert export.main(["--nt8-dir", str(nt8), "--guard-home", str(home)], start_probe=lambda: S2) == 0
    assert doc(env)["nt8StartUtc"] == S2


def test_layout_problem_writes_nothing(env, capsys):
    env[2].execute("alter table Strategies add column Owner text")
    env[2].commit()
    assert run(env) == 1
    assert "unexpected column Owner" in capsys.readouterr().err
    assert not (env[1] / "state" / "backups" / "rows.json").exists()


def test_corrupt_registry_is_left_untouched(env, capsys):
    p = env[1] / "state" / "backups" / "rows.json"
    p.parent.mkdir(parents=True)
    p.write_text('{"version": 2, "rows": {', encoding="utf-8")
    assert run(env) == 1
    assert p.read_text(encoding="utf-8") == '{"version": 2, "rows": {'
    assert "not overwriting" in capsys.readouterr().err


def test_unreadable_database_copy_writes_nothing(env, capsys):
    env[2].close()
    (env[0] / "db" / "NinjaTrader.sqlite").write_bytes(b"this is not a database" * 100)
    assert run(env) == 1
    assert not (env[1] / "state" / "backups" / "rows.json").exists()
    assert "could not be read" in capsys.readouterr().err


def test_unparseable_workspace_does_not_stop_the_backup(env, capsys):
    (env[0] / "workspaces" / "Broken.xml").write_text("<NinjaTrader><NTWindows>", encoding="utf-8")
    assert run(env) == 0
    out = capsys.readouterr().out
    assert "Broken.xml" in out and "2 saved" in out
    assert doc(env)["rows"][f"Demo@{CLASS}"]["charts"]          # the good workspace still recorded


def test_duplicate_names_both_saved_one_registered(env, capsys):
    add_strategy(env[2], 900, "Demo")
    assert run(env) == 0
    folder = env[0] / "templates" / "Strategy" / "DemoStrategy"
    assert (folder / "Guard backup - Demo.xml").exists()
    assert (folder / "Guard backup - Demo (id 900).xml").exists()
    assert doc(env)["rows"][f"Demo@{CLASS}"]["strategies"]["Id"] == 746
    assert "DUPLICATE Demo" in capsys.readouterr().out


def test_names_that_map_to_the_same_file_get_distinct_templates(env, capsys):
    add_strategy(env[2], 901, "A/B")
    add_strategy(env[2], 902, "A:B")                       # same safe name as "A/B"
    add_strategy(env[2], 903, "demo")                      # same file as "Demo" on Windows
    assert run(env) == 0
    folder = env[0] / "templates" / "Strategy" / "DemoStrategy"
    assert sorted(p.name for p in folder.glob("Guard backup - *.xml")) == [
        "Guard backup - A_B (id 902).xml", "Guard backup - A_B.xml",
        "Guard backup - Demo.xml", "Guard backup - demo (id 903).xml"]
    assert "SAME FILE" in capsys.readouterr().out


def test_names_with_forbidden_characters(env):
    add_strategy(env[2], 901, 'A/B: "x" & <y>')
    assert run(env) == 0
    assert (env[0] / "templates" / "Strategy" / "DemoStrategy" / "Guard backup - A_B_ _x_ & _y_.xml").exists()
    assert f'A/B: "x" & <y>@{CLASS}' in doc(env)["rows"]


def test_forget(env, capsys):
    run(env)
    assert run(env, "--forget", "Demo") == 1                # live: refused
    delete_like_nt8(env[2], 746)
    run(env)
    assert run(env, "--forget", "Demo") == 0
    assert f"Demo@{CLASS}" not in doc(env)["rows"]


def test_json_output(env, capsys):
    run(env)
    capsys.readouterr()
    delete_like_nt8(env[2], 746)
    assert run(env, "--json", start=S2) == 0
    out = json.loads(capsys.readouterr().out)
    assert out["transitions"] == [{"name": "Demo", "old": "live", "new": "missing"}]
    assert [s["name"] for s in out["saved"]] == ["Other"]
