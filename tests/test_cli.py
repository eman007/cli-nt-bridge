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
