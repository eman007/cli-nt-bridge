from nt8bridge import sweep

# configure() result that reports both tab-level keys actually applied.
_CFG_OK = {"status": "ok", "applied": [
    {"key": "Instrument", "status": "set"},
    {"key": "BarsPeriod", "status": "set"},
]}


def test_combos_is_full_cross_product():
    c = sweep.combos(["MNQ 09-26", "MES 09-26"], ["Minute:1", "Minute:5"], None)
    assert len(c) == 4
    assert c[0] == {"instrument": "MNQ 09-26", "bars": "Minute:1", "params": {}, "label": "MNQ 09-26 Minute:1"}


def test_combos_crosses_paramsets():
    ps = [{"label": "tight", "params": {"StopTicks": 30}}, {"label": "wide", "params": {"StopTicks": 50}}]
    c = sweep.combos(["MNQ 09-26"], ["Minute:1"], ps)
    assert len(c) == 2
    assert c[0]["label"] == "MNQ 09-26 Minute:1 tight"
    assert c[0]["params"] == {"StopTicks": 30}


def test_run_sweep_configures_then_backtests_each_combo():
    cfg_calls, bt_calls = [], []

    def fake_configure(cfg, timeout=30.0):
        cfg_calls.append(cfg["params"])
        return _CFG_OK

    def fake_backtest(cfg, timeout=120.0):
        bt_calls.append(cfg["params"])
        return {"status": "ok", "metrics": {"netProfit": 100.0}}

    base = {"params": {"Contracts": 1}}
    res = sweep.run_sweep(
        base, ["MNQ 09-26", "MES 09-26"], ["Minute:1"],
        configure_fn=fake_configure, backtest_fn=fake_backtest,
    )
    assert len(res) == 2
    # configure receives tab-level Instrument + BarsPeriod for each combo (plus base params)
    assert cfg_calls[0]["Instrument"] == "MNQ 09-26" and cfg_calls[0]["BarsPeriod"] == "Minute:1"
    assert cfg_calls[1]["Instrument"] == "MES 09-26"
    # backtest receives strategy params (base merged), never Instrument/BarsPeriod keys
    assert bt_calls[0] == {"Contracts": 1}
    assert "Instrument" not in bt_calls[0]
    assert res[0]["result"]["metrics"]["netProfit"] == 100.0


def test_run_sweep_merges_base_and_paramset_for_backtest():
    bt_calls = []
    res = sweep.run_sweep(
        {"params": {"Contracts": 1}}, ["MNQ 09-26"], ["Minute:1"],
        paramsets=[{"label": "x", "params": {"StopTicks": 40}}],
        configure_fn=lambda cfg, timeout=30.0: _CFG_OK,
        backtest_fn=lambda cfg, timeout=120.0: bt_calls.append(cfg["params"]) or {"status": "ok"},
    )
    assert bt_calls[0] == {"Contracts": 1, "StopTicks": 40}
    assert res[0]["label"] == "MNQ 09-26 Minute:1 x"


def test_run_sweep_skips_backtest_when_configure_did_not_apply():
    # Typo'd instrument: configure returns ok but the Instrument key errored, so the
    # series wasn't changed -> sweep must NOT backtest (avoids a mislabeled row).
    bt_called = []

    def fake_configure(cfg, timeout=30.0):
        return {"status": "ok", "applied": [
            {"key": "Instrument", "status": "error", "message": "bad symbol"},
            {"key": "BarsPeriod", "status": "set"},
        ]}

    res = sweep.run_sweep(
        {"params": {}}, ["BOGUS 09-26"], ["Minute:1"],
        configure_fn=fake_configure,
        backtest_fn=lambda cfg, timeout=120.0: bt_called.append(cfg) or {"status": "ok"},
    )
    assert bt_called == []  # backtest skipped
    assert res[0]["result"]["status"] == "error"
    assert res[0]["instrument"] == "BOGUS 09-26"


def test_run_sweep_continues_after_configure_timeout():
    # A configure timeout on one combo must not abort the whole sweep.
    calls = {"n": 0}

    def fake_configure(cfg, timeout=30.0):
        calls["n"] += 1
        if calls["n"] == 1:
            raise TimeoutError("no result")
        return _CFG_OK

    res = sweep.run_sweep(
        {"params": {}}, ["A 09-26", "B 09-26"], ["Minute:1"],
        configure_fn=fake_configure,
        backtest_fn=lambda cfg, timeout=120.0: {"status": "ok", "metrics": {}},
    )
    assert len(res) == 2
    assert res[0]["result"]["status"] == "timeout"   # first combo timed out
    assert res[1]["result"]["status"] == "ok"        # sweep continued
