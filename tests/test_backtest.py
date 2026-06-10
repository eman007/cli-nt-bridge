from nt8bridge import backtest


def test_build_request_embeds_config():
    cfg = {"typeName": "MyStrategy", "instrument": "NQ 06-26"}
    req = backtest.build_backtest_request("id1", cfg)
    assert req["id"] == "id1"
    assert req["kind"] == "backtest"
    assert req["config"] == cfg


def test_parse_ok_response():
    res = backtest.parse_backtest_response(
        {
            "id": "id1",
            "status": "ok",
            "metrics": {"netProfit": 1234.5, "profitFactor": 1.8, "maxDrawdown": -456.0},
            "trades": [{"pnl": 100.0}],
            "equity": [{"t": "2025-01-01", "v": 50000.0}],
        }
    )
    assert res.ok is True
    assert res.metrics["profitFactor"] == 1.8
    assert len(res.trades) == 1
    assert res.equity[0]["v"] == 50000.0


def test_parse_error_response_carries_errors():
    res = backtest.parse_backtest_response(
        {"id": "id1", "status": "error", "errors": [{"message": "no data"}]}
    )
    assert res.ok is False
    assert res.errors[0]["message"] == "no data"


def test_run_backtest_writes_trigger_and_reads_result(monkeypatch, tmp_path):
    from nt8bridge import ntio

    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    monkeypatch.setattr(backtest, "new_request_id", lambda: "bt1")
    trigger, result = ntio.ensure_bridge_dirs()
    ntio.atomic_write_json(
        result / "backtest_bt1.json",
        {"id": "bt1", "status": "ok", "metrics": {"netProfit": 100.0}},
    )
    payload = backtest.run_backtest({"typeName": "X"}, timeout=1.0)
    assert payload["status"] == "ok"
    assert payload["metrics"]["netProfit"] == 100.0
    assert (trigger / "backtest_bt1.json").exists()
