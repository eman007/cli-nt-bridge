import json

import pytest

from nt8bridge import batch


def test_run_batch_loops_and_merges():
    calls = []

    def fake_runner(cfg, timeout=120.0):
        calls.append(cfg)
        return {"status": "ok", "metrics": {"netProfit": cfg["params"]["StopTicks"]}}

    spec = {
        "base": {"params": {"Contracts": 1}},
        "runs": [
            {"label": "a", "params": {"StopTicks": 30}},
            {"label": "b", "params": {"StopTicks": 50}},
        ],
    }
    res = batch.run_batch(spec, runner=fake_runner)
    assert len(res) == 2
    assert res[0]["label"] == "a"
    assert res[0]["params"] == {"Contracts": 1, "StopTicks": 30}  # base + run merged
    assert res[1]["result"]["metrics"]["netProfit"] == 50
    assert len(calls) == 2


def test_run_batch_captures_timeout():
    def boom(cfg, timeout=120.0):
        raise TimeoutError("no result")

    res = batch.run_batch({"runs": [{"label": "a"}]}, runner=boom)
    assert res[0]["result"]["status"] == "timeout"


def test_load_batch_requires_nonempty_runs(tmp_path):
    p = tmp_path / "b.json"
    p.write_text(json.dumps({"base": {}}))
    with pytest.raises(batch.BatchError):
        batch.load_batch(p)


def test_load_batch_missing_file(tmp_path):
    with pytest.raises(batch.BatchError):
        batch.load_batch(tmp_path / "nope.json")


def test_summary_table_has_labels_and_header():
    results = [
        {"label": "a", "params": {}, "result": {"status": "ok", "metrics": {"netProfit": 100.0, "profitFactor": 1.5, "totalTrades": 5}}},
        {"label": "b", "params": {}, "result": {"status": "ok", "metrics": {"netProfit": -50.0, "profitFactor": 0.8, "totalTrades": 7}}},
    ]
    table = batch.summary_table(results)
    assert "label" in table and "net" in table
    assert "a" in table and "b" in table
    assert "100.0" in table and "-50.0" in table


def test_render_batch_pdf_creates_file(tmp_path):
    pytest.importorskip("matplotlib")
    from nt8bridge import report

    results = [
        {"label": "tight", "params": {}, "result": {"status": "ok", "metrics": {"netProfit": 100.0, "profitFactor": 1.5, "totalTrades": 5, "maxDrawdown": -20.0}}},
        {"label": "wide", "params": {}, "result": {"status": "ok", "metrics": {"netProfit": -50.0, "profitFactor": 0.8, "totalTrades": 7, "maxDrawdown": -90.0}}},
    ]
    out = tmp_path / "batch.pdf"
    path = report.render_batch_pdf(results, out)
    assert out.exists() and out.stat().st_size > 0
    assert path == str(out)
