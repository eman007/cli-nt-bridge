import pytest

from nt8bridge import chartseries


def test_build_request_active_target_full_series():
    req = chartseries.build_chartseries_request(
        "rid1",
        target={"mode": "active"},
        dataseries={"instrument": "MES 09-26", "barsPeriodType": "Minute", "barsPeriodValue": 5},
        force=False,
    )
    assert req == {
        "id": "rid1", "kind": "chartseries",
        "target": {"mode": "active"},
        "dataseries": {"instrument": "MES 09-26", "barsPeriodType": "Minute", "barsPeriodValue": 5},
        "force": False,
    }


def test_run_chartseries_target_by_instrument(monkeypatch):
    captured = {}
    monkeypatch.setattr(chartseries.ntio, "ensure_bridge_dirs", lambda: ("trig", "res"))
    monkeypatch.setattr(chartseries, "new_request_id", lambda: "rid2")
    def fake_write(path, obj):
        captured["obj"] = obj
    monkeypatch.setattr(chartseries.ntio, "atomic_write_json", fake_write)
    monkeypatch.setattr(chartseries.ntio, "poll_for_json", lambda p, timeout=30.0: {"id": "rid2", "status": "ok"})
    out = chartseries.run_chartseries(instrument="MES 09-26", bars_type="Minute", bars_value=5,
                                      on_instrument="MNQ 06-26")
    assert captured["obj"]["target"] == {"mode": "instrument", "value": "MNQ 06-26"}
    # sent as a string so the AddOn's quoted-value JSON reader picks it up
    assert captured["obj"]["dataseries"]["barsPeriodValue"] == "5"
    assert out["status"] == "ok"


def test_run_chartseries_requires_something_to_change():
    with pytest.raises(ValueError):
        chartseries.run_chartseries()


def test_run_chartseries_bars_type_requires_value():
    with pytest.raises(ValueError):
        chartseries.run_chartseries(bars_type="Minute")


def test_run_chartseries_sends_value2_as_string(monkeypatch):
    captured = {}
    monkeypatch.setattr(chartseries.ntio, "ensure_bridge_dirs", lambda: ("t", "r"))
    monkeypatch.setattr(chartseries, "new_request_id", lambda: "rid9")
    monkeypatch.setattr(chartseries.ntio, "atomic_write_json", lambda p, o: captured.update(obj=o))
    monkeypatch.setattr(chartseries.ntio, "poll_for_json", lambda p, timeout=30.0: {"id": "rid9", "status": "ok"})
    chartseries.run_chartseries(instrument="MNQ 09-26", bars_type="64", bars_value=64, bars_value2=16)
    ds = captured["obj"]["dataseries"]
    assert ds["barsPeriodType"] == "64"
    assert ds["barsPeriodValue"] == "64"     # string (existing behavior)
    assert ds["barsPeriodValue2"] == "16"    # string, only present when given


def test_run_chartseries_omits_value2_when_absent(monkeypatch):
    captured = {}
    monkeypatch.setattr(chartseries.ntio, "ensure_bridge_dirs", lambda: ("t", "r"))
    monkeypatch.setattr(chartseries, "new_request_id", lambda: "rid10")
    monkeypatch.setattr(chartseries.ntio, "atomic_write_json", lambda p, o: captured.update(obj=o))
    monkeypatch.setattr(chartseries.ntio, "poll_for_json", lambda p, timeout=30.0: {"status": "ok"})
    chartseries.run_chartseries(instrument="MES 09-26", bars_type="Minute", bars_value=5)
    assert "barsPeriodValue2" not in captured["obj"]["dataseries"]


def test_run_chartseries_sends_base_value_as_string(monkeypatch):
    captured = {}
    monkeypatch.setattr(chartseries.ntio, "ensure_bridge_dirs", lambda: ("t", "r"))
    monkeypatch.setattr(chartseries, "new_request_id", lambda: "rid11")
    monkeypatch.setattr(chartseries.ntio, "atomic_write_json", lambda p, o: captured.update(obj=o))
    monkeypatch.setattr(chartseries.ntio, "poll_for_json", lambda p, timeout=30.0: {"id": "rid11", "status": "ok"})
    # UniRenko (id 2018): BaseBarsPeriodValue=Open Offset, Value=Tick Trend, Value2=Tick Reversal.
    chartseries.run_chartseries(instrument="MNQ 09-26", bars_type="2018", bars_value=4, bars_value2=2, bars_base_value=1)
    ds = captured["obj"]["dataseries"]
    assert ds["barsPeriodType"] == "2018"
    assert ds["barsPeriodValue"] == "4"
    assert ds["barsPeriodValue2"] == "2"
    assert ds["baseBarsPeriodValue"] == "1"    # string, only present when given


def test_run_chartseries_omits_base_value_when_absent(monkeypatch):
    captured = {}
    monkeypatch.setattr(chartseries.ntio, "ensure_bridge_dirs", lambda: ("t", "r"))
    monkeypatch.setattr(chartseries, "new_request_id", lambda: "rid12")
    monkeypatch.setattr(chartseries.ntio, "atomic_write_json", lambda p, o: captured.update(obj=o))
    monkeypatch.setattr(chartseries.ntio, "poll_for_json", lambda p, timeout=30.0: {"status": "ok"})
    chartseries.run_chartseries(instrument="MES 09-26", bars_type="Minute", bars_value=5)
    assert "baseBarsPeriodValue" not in captured["obj"]["dataseries"]


def test_run_chartseries_sends_values_without_a_type(monkeypatch):
    """Values must reach the AddOn even when NO --bars-type is named.

    This asserts a contract CHANGE (2026-08-09). It used to raise, and that restriction made a
    custom bars type unreachable: switching TO a custom type makes NT apply that type's own
    defaults and discard the incoming values, so `SentinelTBars 6/24` came back as
    `212201_0_2_...` twice. With values nested under `if bars_type:` there was no second pass
    that could stamp them onto the already-selected type — leaving the Data Series dialog as the
    only way, i.e. exactly the hand-work this command exists to remove.
    """
    captured = {}
    monkeypatch.setattr(chartseries.ntio, "ensure_bridge_dirs", lambda: ("t", "r"))
    monkeypatch.setattr(chartseries, "new_request_id", lambda: "rid13")
    monkeypatch.setattr(chartseries.ntio, "atomic_write_json", lambda p, o: captured.update(obj=o))
    monkeypatch.setattr(chartseries.ntio, "poll_for_json", lambda p, timeout=30.0: {"status": "ok"})
    chartseries.run_chartseries(bars_value=6, bars_value2=24)
    ds = captured["obj"]["dataseries"]
    assert "barsPeriodType" not in ds          # no type switch was requested
    assert ds["barsPeriodValue"] == "6"        # ...but the values still go
    assert ds["barsPeriodValue2"] == "24"


def test_run_chartseries_type_still_requires_a_value():
    """A TYPE switch with no value is still refused — that guard is unchanged."""
    with pytest.raises(ValueError):
        chartseries.run_chartseries(bars_type="Minute")
