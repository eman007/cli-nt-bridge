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


def test_compile_subcommand_without_type(monkeypatch, capsys):
    """NT compiles the whole tree, so --type is optional (and ignored)."""
    from nt8bridge.compile import CompileResult

    seen = {}

    def fake(type_name, timeout=120.0):
        seen["type_name"] = type_name
        seen["timeout"] = timeout
        return CompileResult(ok=True)

    monkeypatch.setattr(cli.ntcompile, "run_compile", fake)
    rc = cli.main(["compile"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 0
    assert payload["ok"] is True
    assert seen["type_name"] == ""
    assert seen["timeout"] == 120.0  # a real tree needs more than the old 30s


def test_compile_subcommand_timeout(monkeypatch, capsys):
    def boom(t, timeout=120.0):
        raise TimeoutError("no result from AddOn")

    monkeypatch.setattr(cli.ntcompile, "run_compile", boom)
    rc = cli.main(["compile", "--type", "X"])
    payload = json.loads(capsys.readouterr().out)
    assert rc == 1
    assert payload["status"] == "timeout"
    assert "hint" in payload  # a timeout must not read as "your code is broken"


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


def test_backtest_without_config_runs_the_tab_as_it_stands(monkeypatch, capsys):
    """No --config is a plain Run, not a missing argument.

    A template put on the tab by `satemplate` already carries instrument, window
    and every parameter, so demanding them again here would reintroduce exactly
    the restatement that naming a file removes. The AddOn injects params only
    when the request carries some, which makes an empty config a Run and nothing
    else.
    """
    seen = {}
    monkeypatch.setattr(cli.ntbacktest, "run_backtest",
                        lambda cfg, timeout=120.0: seen.update(cfg=cfg) or {"status": "ok"})
    rc = cli.main(["backtest"])
    out = json.loads(capsys.readouterr().out)
    assert seen["cfg"] == {}
    assert out["command"] == "backtest" and rc == 0


def test_backtest_with_a_bad_config_still_refuses(monkeypatch, capsys):
    """Optional is not ignored: a config that IS given is still validated."""
    def boom(_path):
        raise cli.ntconfig.ConfigError("Config not found: nope.json")

    monkeypatch.setattr(cli.ntconfig, "load_config", boom)
    rc = cli.main(["backtest", "--config", "nope.json"])
    out = json.loads(capsys.readouterr().out)
    assert rc == 1 and out["ok"] is False and "nope.json" in out["error"]


def test_satemplate_forwards_the_template_and_timeout(monkeypatch, capsys):
    seen = {}
    monkeypatch.setattr(cli.ntsatemplate, "run_satemplate",
                        lambda template, timeout=30.0:
                        seen.update(template=template, timeout=timeout)
                        or {"status": "ok", "applied": True})
    rc = cli.main(["satemplate", "--template", r"C:\t\A.xml", "--timeout", "45"])
    out = json.loads(capsys.readouterr().out)
    assert seen["template"] == r"C:\t\A.xml" and seen["timeout"] == 45.0
    assert out["command"] == "satemplate" and out["applied"] is True and rc == 0


def test_satemplate_that_did_not_apply_is_a_failure(monkeypatch, capsys):
    """A template the tab did not take must not exit 0.

    The run afterwards would use whatever was there before, and every step would
    report ok while the numbers belonged to another variant.
    """
    monkeypatch.setattr(cli.ntsatemplate, "run_satemplate",
                        lambda template, timeout=30.0:
                        {"status": "error", "applied": False,
                         "error": "the tab kept its previous StrategyTemplate instance"})
    rc = cli.main(["satemplate", "--template", "A.xml"])
    capsys.readouterr()
    assert rc == 1


def _fake_playbackrun_driver(result):
    """Stand in for the driver process, writing the result file it would write."""
    import subprocess

    seen = {}

    def fake_run(cmd, **kw):
        seen["cmd"] = cmd
        path = cmd[cmd.index("--result") + 1]
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(result, fh)
        return subprocess.CompletedProcess(cmd, 0)

    return seen, fake_run


def test_playbackrun_cli_dispatch(monkeypatch, capsys):
    """The wrapper's job is the argument list and the verdict, not the run itself."""
    import subprocess

    from nt8bridge import cli

    seen, fake_run = _fake_playbackrun_driver({"command": "playbackrun", "ok": True, "rc": 0})
    monkeypatch.setattr(subprocess, "run", fake_run)
    rc = cli.main(["playbackrun", "--strategy", "MyBot", "--instrument", "NQ 06-26",
                   "--source", "marketreplay", "--tick-replay", "false",
                   "--bars-type", "Minute", "--bars-value", "1",
                   "--from", "2026-08-10", "--to", "2026-08-10"])
    capsys.readouterr()
    assert rc == 0
    cmd = seen["cmd"]
    for flag, value in (("--strategy", "MyBot"), ("--instrument", "NQ 06-26"),
                        ("--source", "marketreplay"), ("--tick-replay", "false"),
                        ("--bars-type", "Minute"), ("--bars-value", "1"),
                        ("--from", "2026-08-10"), ("--to", "2026-08-10")):
        assert cmd[cmd.index(flag) + 1] == value
    assert cmd[cmd.index("--setup") + 1] == "template"


def test_playbackrun_that_did_not_reach_the_data_end_is_a_failure(monkeypatch, capsys):
    """`ok: false` must not exit 0 - a run that stopped early is not a measurement."""
    import subprocess

    from nt8bridge import cli

    _, fake_run = _fake_playbackrun_driver(
        {"command": "playbackrun", "ok": False, "rc": 2, "endReason": "did-not-finish"})
    monkeypatch.setattr(subprocess, "run", fake_run)
    rc = cli.main(["playbackrun", "--strategy", "MyBot", "--instrument", "NQ 06-26",
                   "--source", "historical", "--tick-replay", "true",
                   "--bars-type", "Tick", "--bars-value", "1",
                   "--from", "2026-08-10", "--to", "2026-08-10"])
    capsys.readouterr()
    assert rc == 2


def test_playbackrun_rejects_an_unsupported_bar_type(capsys):
    """`--bars-type Day` used to fall through to the Minute token and run silently."""
    import pytest

    with pytest.raises(SystemExit):
        cli.main(["playbackrun", "--strategy", "MyBot", "--instrument", "NQ 06-26",
                  "--source", "marketreplay", "--tick-replay", "false",
                  "--bars-type", "Day", "--bars-value", "1",
                  "--from", "2026-08-10", "--to", "2026-08-10"])
    assert "invalid choice" in capsys.readouterr().err


def test_playbackrun_refuses_a_malformed_date_before_starting_the_driver(monkeypatch, capsys):
    """Measured 2026-09-01: seven runs carried --to 2026-13-07 into NinjaTrader and
    each ended 45-58 s of wall clock later with "FormatException: String was not recognized as a
    valid DateTime." The parser refuses it here, and the driver never starts."""
    import subprocess

    import pytest

    seen, fake_run = _fake_playbackrun_driver({"command": "playbackrun", "ok": True, "rc": 0})
    monkeypatch.setattr(subprocess, "run", fake_run)
    for bad in ("2026-13-07", "07/07/2026", "2026-7-7"):
        with pytest.raises(SystemExit) as ex:
            cli.main(["playbackrun", "--strategy", "MyBot", "--instrument", "NQ 06-26",
                      "--from", "2026-06-07", "--to", bad])
        err = capsys.readouterr().err
        assert ex.value.code == 2
        assert bad in err and "YYYY-MM-DD" in err
    assert "cmd" not in seen            # no driver process, hence no request


def test_playbackrun_refuses_an_end_before_the_start(monkeypatch, capsys):
    """Both dates well-formed, the pair unusable: the same refusal, the same silence."""
    import subprocess

    import pytest

    seen, fake_run = _fake_playbackrun_driver({"command": "playbackrun", "ok": True, "rc": 0})
    monkeypatch.setattr(subprocess, "run", fake_run)
    with pytest.raises(SystemExit) as ex:
        cli.main(["playbackrun", "--strategy", "MyBot", "--instrument", "NQ 06-26",
                  "--from", "2026-06-07", "--to", "2026-06-01"])
    err = capsys.readouterr().err
    assert ex.value.code == 2
    assert "--to 2026-06-01" in err and "--from 2026-06-07" in err
    assert "cmd" not in seen


def test_playbackrun_forwards_a_valid_pair_unchanged(monkeypatch, capsys):
    """type= returns the string it was given, so the driver sees what was typed;
    the same day at both ends is a one-day run, not a refusal."""
    import subprocess

    seen, fake_run = _fake_playbackrun_driver({"command": "playbackrun", "ok": True, "rc": 0})
    monkeypatch.setattr(subprocess, "run", fake_run)
    rc = cli.main(["playbackrun", "--strategy", "MyBot", "--instrument", "NQ 06-26",
                   "--from", "2026-06-07", "--to", "2026-06-07"])
    capsys.readouterr()
    assert rc == 0
    cmd = seen["cmd"]
    assert cmd[cmd.index("--from") + 1] == "2026-06-07"
    assert cmd[cmd.index("--to") + 1] == "2026-06-07"


def test_playbackrun_passes_account_and_stage_wait_to_the_driver(monkeypatch, capsys):
    """--account and --stage-wait are the driver's; the wrapper only has to hand them over."""
    import subprocess

    from nt8bridge import cli

    seen, fake_run = _fake_playbackrun_driver({"command": "playbackrun", "ok": True, "rc": 0})
    monkeypatch.setattr(subprocess, "run", fake_run)
    rc = cli.main(["playbackrun", "--strategy", "MyBot", "--instrument", "NQ 06-26",
                   "--source", "marketreplay", "--tick-replay", "false",
                   "--bars-type", "Minute", "--bars-value", "1",
                   "--from", "2026-08-10", "--to", "2026-08-10",
                   "--account", "Playback101", "--stage-wait", "20"])
    capsys.readouterr()
    assert rc == 0
    cmd = seen["cmd"]
    assert cmd[cmd.index("--account") + 1] == "Playback101"
    assert cmd[cmd.index("--stage-wait") + 1] == "20"


def test_playbackrun_without_account_and_stage_wait_sends_neither(monkeypatch, capsys):
    """Absent switches must stay absent: the driver's own defaults decide, not a wrapper value."""
    import subprocess

    from nt8bridge import cli

    seen, fake_run = _fake_playbackrun_driver({"command": "playbackrun", "ok": True, "rc": 0})
    monkeypatch.setattr(subprocess, "run", fake_run)
    rc = cli.main(["playbackrun", "--strategy", "MyBot", "--instrument", "NQ 06-26",
                   "--source", "marketreplay", "--tick-replay", "false",
                   "--bars-type", "Minute", "--bars-value", "1",
                   "--from", "2026-08-10", "--to", "2026-08-10"])
    capsys.readouterr()
    assert rc == 0
    assert "--account" not in seen["cmd"]
    assert "--stage-wait" not in seen["cmd"]
