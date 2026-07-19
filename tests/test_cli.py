import json

from nt8bridge import cli


def test_no_args_prints_capability(capsys):
    rc = cli.main([])
    out = capsys.readouterr().out
    assert rc == 0
    assert "Claude CAN" in out
    assert "backtest" in out


def test_doctor_reports_checks_as_json(monkeypatch, tmp_path, capsys):
    nt8 = tmp_path / "nt8"
    (nt8 / "bin" / "Custom" / "AddOns").mkdir(parents=True)
    monkeypatch.setenv("NT8_DIR", str(nt8))
    rc = cli.main(["doctor"])
    payload = json.loads(capsys.readouterr().out)
    assert payload["command"] == "doctor"
    names = {c["name"] for c in payload["checks"]}
    assert "nt8_dir_exists" in names
    assert "addon_compiled" in names
    # AddOn DLL is absent -> that check fails -> doctor returns non-zero
    assert rc == 1


def test_doctor_passes_when_addon_dll_present(monkeypatch, tmp_path, capsys):
    nt8 = tmp_path / "nt8"
    addons = nt8 / "bin" / "Custom" / "AddOns"
    addons.mkdir(parents=True)
    (nt8 / "bin" / "Custom" / "NinjaTrader.Custom.dll").write_bytes(b"x")
    (addons / "NT8BridgeServer.cs").write_text("// addon")
    monkeypatch.setenv("NT8_DIR", str(nt8))
    rc = cli.main(["doctor"])
    capsys.readouterr()
    assert rc == 0


def test_precheck_subcommand_ok(monkeypatch, capsys):
    monkeypatch.setattr(cli.precheck, "run_precheck", lambda p: [])
    rc = cli.main(["precheck", "--strategy", "X.cs"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 0
    assert payload["command"] == "precheck"
    assert payload["ok"] is True
    assert payload["errors"] == []


def test_precheck_subcommand_reports_errors(monkeypatch, capsys):
    from nt8bridge.precheck import CompileError

    monkeypatch.setattr(
        cli.precheck,
        "run_precheck",
        lambda p: [CompileError(file="X.cs", line=4, code="CS0103", message="nope")],
    )
    rc = cli.main(["precheck", "--strategy", "X.cs"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 1
    assert payload["ok"] is False
    assert payload["errors"][0]["code"] == "CS0103"


def test_deploy_subcommand(monkeypatch, tmp_path, capsys):
    nt8 = tmp_path / "nt8"
    monkeypatch.setenv("NT8_DIR", str(nt8))
    src = tmp_path / "MyStrategy.cs"
    src.write_text("// s", encoding="utf-8")
    rc = cli.main(["deploy", "--strategy", str(src)])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 0
    assert payload["command"] == "deploy"
    assert payload["dest"].replace("\\", "/").endswith("Strategies/MyStrategy.cs")


def test_precheck_subcommand_missing_file_is_error(capsys):
    # A missing file must error loudly, never report a false "clean".
    rc = cli.main(["precheck", "--strategy", "does_not_exist_98765.cs"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 1
    assert payload["ok"] is False
    assert payload["errors"][0]["code"] == "ENOENT"


def test_compile_subcommand_ok(monkeypatch, capsys):
    from nt8bridge.compile import CompileResult

    monkeypatch.setattr(
        cli.ntcompile, "run_compile", lambda t, timeout=30.0: CompileResult(ok=True)
    )
    rc = cli.main(["compile", "--type", "MyStrategy"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 0
    assert payload["command"] == "compile"
    assert payload["ok"] is True


def test_compile_subcommand_timeout(monkeypatch, capsys):
    def boom(t, timeout=30.0):
        raise TimeoutError("no result from AddOn")

    monkeypatch.setattr(cli.ntcompile, "run_compile", boom)
    rc = cli.main(["compile", "--type", "X"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 1
    assert payload["status"] == "timeout"


def test_batch_subcommand_ok(monkeypatch, tmp_path, capsys):
    bf = tmp_path / "b.json"
    bf.write_text(json.dumps({"runs": [{"label": "a", "params": {}}]}))
    monkeypatch.setattr(
        cli.ntbatch,
        "run_batch",
        lambda spec, timeout=120.0: [
            {"label": "a", "params": {}, "result": {"status": "ok", "metrics": {}}}
        ],
    )
    rc = cli.main(["batch", "--batch", str(bf)])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 0
    assert payload["command"] == "batch"
    assert len(payload["runs"]) == 1


def test_performance_subcommand_ok(monkeypatch, capsys):
    monkeypatch.setattr(
        cli.ntperformance,
        "run_performance",
        lambda name, from_="", to="", instrument="", timeout=20.0: {
            "status": "ok", "account": name, "source": "db",
            "metrics": {"totalTrades": 2, "netProfit": 150.0, "profitFactor": 2.0, "maxDrawdown": -50.0},
            "trades": [
                {"pnl": 200.0, "marketPosition": "Long"},
                {"pnl": -50.0, "marketPosition": "Short"},
            ],
            "warnings": [], "errors": [],
        },
    )
    rc = cli.main(["performance", "--name", "Sim101", "--from", "2026-06-20"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 0
    assert payload["command"] == "performance"
    assert payload["account"] == "Sim101"
    assert payload["metrics"]["netProfit"] == 150.0
    # client-side scorecard derived from trades[]
    assert payload["scorecard"]["win_rate"] == 50.0
    assert payload["scorecard"]["n_wins"] == 1 and payload["scorecard"]["n_losses"] == 1
    assert payload["scorecard"]["net"] == 150.0


def test_performance_subcommand_account_missing_is_error(monkeypatch, capsys):
    def boom(name, from_="", to="", instrument="", timeout=20.0):
        raise ValueError("performance requires an account name")

    monkeypatch.setattr(cli.ntperformance, "run_performance", boom)
    rc = cli.main(["performance", "--name", ""])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 1
    assert payload["command"] == "performance" and payload["ok"] is False
    assert payload["status"] == "error"


def test_perfwindow_subcommand_ok(monkeypatch, capsys):
    monkeypatch.setattr(
        cli.ntperfwindow,
        "run_perfwindow",
        lambda name, generate=False, from_="", to="", timeout=20.0: {
            "status": "ok", "reportCount": 1,
            "reports": [{
                "account": "", "scope": "account", "trades": 167,
                "netProfit": 1623.12, "commission": 61.38, "fees": 0.0,
                "accountBreakdown": [{"account": name, "trades": 167, "netProfit": 1623.12,
                                      "commission": 61.38, "fee": 0.0}],
            }],
        },
    )
    rc = cli.main(["perfwindow", "--name", "Sim101"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 0
    assert payload["command"] == "perfwindow"
    assert payload["reports"][0]["scope"] == "account"
    assert payload["reports"][0]["netProfit"] == 1623.12
    assert payload["reports"][0]["commission"] == 61.38


def test_perfwindow_generate_flag_passthrough(monkeypatch, capsys):
    seen = {}

    def fake(name, generate=False, from_="", to="", timeout=20.0):
        seen.update(name=name, generate=generate, from_=from_, to=to, timeout=timeout)
        return {"status": "ok", "reportCount": 0, "reports": []}

    monkeypatch.setattr(cli.ntperfwindow, "run_perfwindow", fake)
    rc = cli.main(["perfwindow", "--name", "X", "--generate", "--from", "2026-06-01", "--to", "2026-07-13"])
    assert rc == 0  # status "ok" -> exit 0
    # generate must be forwarded true, dates forwarded, and the default timeout bumped for a fetch.
    assert seen["generate"] is True
    assert seen["from_"] == "2026-06-01" and seen["to"] == "2026-07-13"
    assert seen["timeout"] == 200.0


def test_perfwindow_warns_when_fees_untrusted(monkeypatch, capsys):
    fake = {"status": "ok", "reportCount": 1, "reports": [
        {"account": "Sim101", "feesCalculated": False, "totalFees": 0.0,
         "feeReadNote": "TotalFeesAll unreadable (NT8 build mismatch?)"}]}
    monkeypatch.setattr(cli.ntperfwindow, "run_perfwindow", lambda *a, **k: fake)
    rc = cli.main(["perfwindow", "--name", "Sim101"])
    captured = capsys.readouterr()
    assert rc == 0
    assert "WARNING" in captured.err and "feesCalculated" in captured.err


def test_sweep_subcommand_ok(monkeypatch, tmp_path, capsys):
    import json as _json
    from nt8bridge import cli
    cfgf = tmp_path / "base.json"
    cfgf.write_text(_json.dumps({"params": {"Contracts": 1}}))
    monkeypatch.setattr(
        cli.ntsweep, "run_sweep",
        lambda base, instruments, bars, paramsets=None, timeout=120.0: [
            {"label": "MNQ 09-26 Minute:1", "instrument": "MNQ 09-26", "bars": "Minute:1",
             "params": {"Contracts": 1}, "result": {"status": "ok", "metrics": {"netProfit": 100.0}}}
        ],
    )
    rc = cli.main(["sweep", "--config", str(cfgf), "--instruments", "MNQ 09-26", "--bars", "Minute:1"])
    payload = _json.loads(capsys.readouterr().out)
    assert rc == 0
    assert payload["command"] == "sweep"
    assert len(payload["runs"]) == 1
    assert payload["runs"][0]["instrument"] == "MNQ 09-26"


def test_sweep_parses_comma_lists(monkeypatch, tmp_path, capsys):
    import json as _json
    from nt8bridge import cli
    cfgf = tmp_path / "base.json"
    cfgf.write_text(_json.dumps({"params": {}}))
    seen = {}
    def fake(base, instruments, bars, paramsets=None, timeout=120.0):
        seen["instruments"] = instruments
        seen["bars"] = bars
        return []
    monkeypatch.setattr(cli.ntsweep, "run_sweep", fake)
    cli.main(["sweep", "--config", str(cfgf), "--instruments", "MNQ 09-26, MES 09-26", "--bars", "Minute:1,Minute:5"])
    capsys.readouterr()
    assert seen["instruments"] == ["MNQ 09-26", "MES 09-26"]
    assert seen["bars"] == ["Minute:1", "Minute:5"]


def test_chartseries_subcommand_ok(monkeypatch, capsys):
    import json as _json
    from nt8bridge import cli
    monkeypatch.setattr(
        cli.ntchartseries, "run_chartseries",
        lambda **kw: {"id": "x", "status": "ok", "matched": 1,
                      "chart": {"title": "t", "before": {"instrument": "MNQ 06-26", "bars": "Minute:1"},
                                "after": {"instrument": "MES 09-26", "bars": "Minute:5"}}},
    )
    rc = cli.main(["chartseries", "--instrument", "MES 09-26", "--bars-type", "Minute", "--bars-value", "5"])
    payload = _json.loads(capsys.readouterr().out)
    assert rc == 0
    assert payload["command"] == "chartseries"
    assert payload["chart"]["after"]["instrument"] == "MES 09-26"


def test_chartseries_subcommand_blocked_returns_nonzero(monkeypatch, capsys):
    import json as _json
    from nt8bridge import cli
    monkeypatch.setattr(cli.ntchartseries, "run_chartseries",
                        lambda **kw: {"id": "x", "status": "blocked", "message": "enabled strategy on chart"})
    rc = cli.main(["chartseries", "--instrument", "MES 09-26"])
    payload = _json.loads(capsys.readouterr().out)
    assert rc == 1
    assert payload["status"] == "blocked"


def test_histdump_cli_dispatch(monkeypatch, capsys):
    from nt8bridge import cli, histdump
    called = {}

    def fake_run(**kw):
        called.update(kw)
        return {"status": "ok", "exported": ["MNQ 03-25/20241202"], "failed": [], "count": 1,
                "gate": {"ok": True, "gate": "MNQ 03-25/20241201", "detail": "byte-identical"}}

    monkeypatch.setattr(histdump, "run_histdump", fake_run)
    rc = cli.main(["histdump", "--instrument", "MNQ*", "--out", "D:/x"])
    assert rc == 0
    assert called["instrument_glob"] == "MNQ*" and called["mode"] == "depth"
    out = json.loads(capsys.readouterr().out)
    assert out["command"] == "histdump" and out["count"] == 1
