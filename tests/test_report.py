from nt8bridge import report


def test_format_metrics_table_contains_keys_and_values():
    table = report.format_metrics_table(
        {"netProfit": 1234.5, "profitFactor": 1.8, "maxDrawdown": -456.0}
    )
    assert "netProfit" in table
    assert "1234.5" in table
    assert "profitFactor" in table


def test_format_metrics_table_empty():
    assert "no metrics" in report.format_metrics_table({}).lower()


def test_assess_reads_metrics():
    txt = report.assess(
        {"netProfit": -390.0, "profitFactor": 0.43, "totalTrades": 18, "maxDrawdown": -478.0}
    )
    assert "unprofitable" in txt
    assert "no edge" in txt
    assert "18 trades" in txt


def test_assess_profitable_with_edge():
    txt = report.assess({"netProfit": 1000.0, "profitFactor": 1.8})
    assert "profitable" in txt and "edge" in txt


def test_render_pdf_creates_file(tmp_path):
    import pytest

    pytest.importorskip("matplotlib")
    out = tmp_path / "r.pdf"
    res = {
        "strategy": "DemoStrat",
        "metrics": {"totalTrades": 3, "netProfit": 100.0, "profitFactor": 1.5, "maxDrawdown": -20.0},
        "trades": [{"pnl": 50.0}, {"pnl": -10.0}, {"pnl": 60.0}],
    }
    path = report.render_pdf(res, out)
    assert out.exists() and out.stat().st_size > 0
    assert path == str(out)
