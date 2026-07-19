from nt8bridge import performance


def test_build_request_basic():
    req = performance.build_performance_request("id1", "Sim101", "2026-06-01", "2026-06-24", "MNQ 09-26")
    assert req["id"] == "id1"
    assert req["kind"] == "performance"
    assert req["account"] == "Sim101"
    assert req["from"] == "2026-06-01"
    assert req["to"] == "2026-06-24"
    assert req["instrument"] == "MNQ 09-26"


def test_build_request_defaults_blank():
    req = performance.build_performance_request("id1", "Sim101")
    assert req["from"] == "" and req["to"] == "" and req["instrument"] == ""


def test_parse_ok_response():
    res = performance.parse_performance_response(
        {
            "status": "ok", "account": "Sim101", "source": "db",
            "metrics": {"totalTrades": 2, "netProfit": 150.0, "profitFactor": 2.0, "maxDrawdown": -50.0},
            "trades": [
                {"pnl": 200.0, "marketPosition": "Long", "exitName": "Profit target", "quantity": 1},
                {"pnl": -50.0, "marketPosition": "Short", "exitName": "Stop loss", "quantity": 1},
            ],
            "warnings": [], "errors": [],
        }
    )
    assert res.ok is True
    assert res.account == "Sim101"
    assert res.source == "db"
    assert res.metrics["netProfit"] == 150.0
    assert len(res.trades) == 2 and res.trades[0]["pnl"] == 200.0


def test_parse_error_response_carries_errors():
    res = performance.parse_performance_response(
        {"status": "error", "errors": [{"code": "BRIDGE", "message": "account not found: Nope"}]}
    )
    assert res.ok is False
    assert res.errors[0]["message"] == "account not found: Nope"


def test_parse_degraded_response_exposes_memory_source_and_warning():
    res = performance.parse_performance_response(
        {"status": "ok", "account": "Sim101", "source": "memory",
         "metrics": {"totalTrades": 0}, "trades": [],
         "warnings": ["DB history unavailable; limited to ~3-day in-memory window"], "errors": []}
    )
    assert res.ok is True and res.source == "memory"
    assert "DB history unavailable" in res.warnings[0]


def test_run_performance_requires_account():
    import pytest
    with pytest.raises(ValueError):
        performance.run_performance("")


def test_run_performance_writes_trigger_and_reads_result(monkeypatch, tmp_path):
    import json
    from nt8bridge import ntio

    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    monkeypatch.setattr(performance, "new_request_id", lambda: "pf1")
    trigger, result = ntio.ensure_bridge_dirs()
    ntio.atomic_write_json(
        result / "perf_pf1.json",
        {"id": "pf1", "status": "ok", "account": "Sim101", "source": "db",
         "metrics": {"totalTrades": 0}, "trades": [], "warnings": [], "errors": []},
    )
    payload = performance.run_performance("Sim101", from_="2026-06-20", timeout=1.0)
    assert payload["status"] == "ok" and payload["account"] == "Sim101"
    req = json.loads((trigger / "perf_pf1.json").read_text(encoding="utf-8"))
    assert req["kind"] == "performance" and req["account"] == "Sim101"
    assert req["from"] == "2026-06-20"
