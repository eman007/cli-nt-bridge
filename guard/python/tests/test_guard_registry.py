from __future__ import annotations

import json

import pytest

from guard_fixtures import CLASS
from nt8guard_backup import registry as reg
from nt8guard_backup.workspaces import StrategyRef

S1, S2 = "2026-10-08T01:00:00Z", "2026-10-08T05:00:00Z"   # two NT8 sessions


def live(sid: int, name: str = "Demo") -> dict:
    return {"strategies": {"Id": sid, "Name": name, "Classname": CLASS, "Userdata": "AA=="},
            "strategy2account": [{"Account": 1, "Strategy": sid, "Nr": 0}],
            "strategy2instrument": [{"Instrument": 100, "Strategy": sid, "Nr": 0}]}


def key(name: str = "Demo") -> str:
    return reg.key_of(name, CLASS)


def test_new_strategy_is_live_without_a_transition():
    doc = reg.empty()
    assert reg.update(doc, [live(746)], "t1", S1) == []
    row = doc["rows"][key()]
    assert row["state"] == "live" and row["lastSeenNt8StartUtc"] == S1 and row["idHistory"] == []


def test_gone_in_the_same_session_is_removed():
    doc = reg.empty()
    reg.update(doc, [live(746)], "t1", S1)
    assert reg.update(doc, [], "t2", S1) == [reg.Transition(key(), "Demo", "live", "removed")]
    assert doc["rows"][key()]["state"] == "removed"


def test_gone_across_a_restart_is_missing():
    doc = reg.empty()
    reg.update(doc, [live(746)], "t1", S1)
    reg.update(doc, [], "t2", S2)
    assert doc["rows"][key()]["state"] == "missing"


def test_gone_while_nt8_is_not_running_is_missing():
    doc = reg.empty()
    reg.update(doc, [live(746)], "t1", S1)
    reg.update(doc, [], "t2", None)
    assert doc["rows"][key()]["state"] == "missing"


def test_state_is_not_reclassified_by_later_saves():
    doc = reg.empty()
    reg.update(doc, [live(746)], "t1", S1)
    reg.update(doc, [], "t2", S2)            # missing
    assert reg.update(doc, [], "t3", S2) == []
    assert doc["rows"][key()]["state"] == "missing"


def test_coming_back_is_a_transition_to_live():
    doc = reg.empty()
    reg.update(doc, [live(746)], "t1", S1)
    reg.update(doc, [], "t2", S2)
    assert reg.update(doc, [live(746)], "t3", S2) == [reg.Transition(key(), "Demo", "missing", "live")]


def test_new_id_is_recorded_in_history_once():
    doc = reg.empty()
    reg.update(doc, [live(816)], "t1", S1)
    reg.update(doc, [live(872)], "t2", S1)
    reg.update(doc, [live(872)], "t3", S1)
    row = doc["rows"][key()]
    assert row["strategies"]["Id"] == 872 and row["idHistory"] == [816]
    assert reg.owner_of(doc, 816) == key() and reg.owner_of(doc, 872) == key()
    assert reg.owner_of(doc, 5) is None


def ref(sid: int, tab: str = "Tab-b1") -> StrategyRef:
    return StrategyRef("Side Monitor", "Chart-a1", tab, "Alpha", "Strategy0", 0, sid)


def test_charts_recorded_including_through_an_old_id_and_kept_when_gone():
    doc = reg.empty()
    reg.update(doc, [live(816)], "t1", S1)
    reg.update(doc, [live(872)], "t2", S1)
    reg.record_charts(doc, [ref(816), ref(999)])          # chart still lists the old id; 999 unknown
    assert doc["rows"][key()]["charts"] == [ref(816).record()]
    reg.record_charts(doc, [])                              # NT8 forgot it: keep what we knew
    assert doc["rows"][key()]["charts"] == [ref(816).record()]


def test_mark_restored_under_a_new_id():
    doc = reg.empty()
    reg.update(doc, [live(746)], "t1", S1)
    reg.update(doc, [], "t2", S2)
    reg.mark_restored(doc, key(), 900, "t3")
    row = doc["rows"][key()]
    assert row["state"] == "live" and row["strategies"]["Id"] == 900 and row["idHistory"] == [746]
    assert row["strategy2account"][0]["Strategy"] == 900
    assert row["strategy2instrument"][0]["Strategy"] == 900


def test_forget():
    doc = reg.empty()
    reg.update(doc, [live(746, "Keep"), live(747, "Gone")], "t1", S1)
    reg.update(doc, [live(746, "Keep")], "t2", S1)
    forgotten, kept = reg.forget(doc, ["Keep", "Gone", "Nope"])
    assert forgotten == [key("Gone")]
    assert [n for n, _ in kept] == ["Keep", "Nope"]
    assert key("Keep") in doc["rows"] and key("Gone") not in doc["rows"]


def test_save_and_load_round_trip(tmp_path):
    doc = reg.empty()
    reg.update(doc, [live(746)], "t1", S1)
    p = tmp_path / "state" / "backups" / "rows.json"
    reg.save(p, doc)
    assert reg.load(p) == doc
    assert not p.with_suffix(".json.tmp").exists()


def test_load_missing_file_is_empty(tmp_path):
    assert reg.load(tmp_path / "nope.json") == reg.empty()


def test_load_refuses_corrupt_or_foreign_files(tmp_path):
    bad = tmp_path / "rows.json"
    bad.write_text('{"version": 2, "rows": {', encoding="utf-8")
    with pytest.raises(reg.RegistryError):
        reg.load(bad)
    bad.write_text(json.dumps({"utc": "x", "rows": {}}), encoding="utf-8")   # old format, no version
    with pytest.raises(reg.RegistryError):
        reg.load(bad)


def test_import_legacy():
    legacy = {"utc": "L2", "rows": {
        key("Live"): {**live(746, "Live"), "lastSeenUtc": "L2"},
        key("Old"): {**live(747, "Old"), "lastSeenUtc": "L1"}}}
    doc = reg.import_legacy(legacy, S1)
    assert doc["version"] == 2
    assert doc["rows"][key("Live")]["state"] == "live"
    assert doc["rows"][key("Live")]["lastSeenNt8StartUtc"] == S1
    assert doc["rows"][key("Old")]["state"] == "removed"
    assert doc["rows"][key("Old")]["lastSeenNt8StartUtc"] is None
