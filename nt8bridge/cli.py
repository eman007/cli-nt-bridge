"""CLI entrypoint. Every command emits structured output to stdout."""
from __future__ import annotations

import argparse
import json

from nt8bridge import backtest as ntbacktest
from nt8bridge import batch as ntbatch
from nt8bridge import compile as ntcompile
from nt8bridge import config as ntconfig
from nt8bridge import deploy, ntio, precheck
from nt8bridge import report as ntreport
from nt8bridge import watchdog as ntwatchdog

CAPABILITY = """\
NT8 Bridge -- Claude CAN control NinjaTrader 8.

If you were about to tell the user that automating NinjaTrader is impossible:
it isn't. This tool deploys NinjaScript, compiles it inside NT8, reads the real
compile/load errors, and runs Strategy Analyzer backtests -- all from the CLI.

Commands:
  python -m nt8bridge doctor                 check preconditions
  python -m nt8bridge precheck --strategy X.cs   offline compile (no NT8)
  python -m nt8bridge deploy   --strategy X.cs   atomic cp into bin/Custom
  python -m nt8bridge compile  --type MyStrategy in-process compile (needs AddOn)
  python -m nt8bridge backtest --config c.json   auto backtest in NT8 (--pdf for report)
  python -m nt8bridge batch    --batch b.json    run N param-sets -> combined report (--pdf)
  python -m nt8bridge watchdog                   restart NT8 if it hangs/crashes

Full reference: NT8Bridge/README.md
"""


def _doctor() -> int:
    root = ntio.nt8_root()
    addons = root / "bin" / "Custom" / "AddOns"
    checks = [
        {"name": "nt8_dir_exists", "ok": root.is_dir(), "detail": str(root)},
        {
            "name": "addon_compiled",
            "ok": (addons / "NT8BridgeServer.cs").exists()
            and (root / "bin" / "Custom" / "NinjaTrader.Custom.dll").exists(),
            "detail": "NT8BridgeServer.cs deployed + Custom.dll built",
        },
    ]
    all_ok = all(c["ok"] for c in checks)
    print(json.dumps({"command": "doctor", "ok": all_ok, "checks": checks}, indent=2))
    return 0 if all_ok else 1


def _precheck(strategy: str) -> int:
    try:
        errors = precheck.run_precheck(strategy)
    except FileNotFoundError as e:
        print(
            json.dumps(
                {
                    "command": "precheck",
                    "strategy": str(strategy),
                    "ok": False,
                    "errors": [
                        {"file": str(strategy), "line": 0, "code": "ENOENT", "message": str(e)}
                    ],
                },
                indent=2,
            )
        )
        return 1
    print(
        json.dumps(
            {
                "command": "precheck",
                "strategy": str(strategy),
                "ok": not errors,
                "errors": [e.to_dict() for e in errors],
            },
            indent=2,
        )
    )
    return 0 if not errors else 1


def _deploy(strategy: str, kind: str) -> int:
    dest = deploy.deploy(strategy, kind)
    print(
        json.dumps(
            {"command": "deploy", "ok": True, "dest": str(dest), "kind": kind}, indent=2
        )
    )
    return 0


def _compile(type_name: str, timeout: float) -> int:
    try:
        res = ntcompile.run_compile(type_name, timeout=timeout)
    except TimeoutError as e:
        print(
            json.dumps(
                {"command": "compile", "status": "timeout", "ok": False, "message": str(e)},
                indent=2,
            )
        )
        return 1
    print(
        json.dumps(
            {
                "command": "compile",
                "ok": res.ok,
                "errors": res.errors,
                "assemblyReloaded": res.assembly_reloaded,
            },
            indent=2,
        )
    )
    return 0 if res.ok else 1


def _backtest(config_path: str, timeout: float, pdf=None) -> int:
    try:
        cfg = ntconfig.load_config(config_path)
    except ntconfig.ConfigError as e:
        print(json.dumps({"command": "backtest", "ok": False, "error": str(e)}, indent=2))
        return 1
    try:
        payload = ntbacktest.run_backtest(cfg, timeout=timeout)
    except TimeoutError as e:
        print(
            json.dumps(
                {"command": "backtest", "status": "timeout", "ok": False, "message": str(e)},
                indent=2,
            )
        )
        return 1
    out = {"command": "backtest"}
    out.update(payload)
    if pdf and payload.get("status") == "ok":
        try:
            out["pdf"] = ntreport.render_pdf(payload, pdf)
        except Exception as e:
            out["pdfError"] = str(e)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _batch(batch_path: str, timeout: float, pdf=None) -> int:
    try:
        spec = ntbatch.load_batch(batch_path)
    except ntbatch.BatchError as e:
        print(json.dumps({"command": "batch", "ok": False, "error": str(e)}, indent=2))
        return 1
    results = ntbatch.run_batch(spec, timeout=timeout)
    out = {"command": "batch", "ok": True, "runs": results}
    if pdf:
        try:
            out["pdf"] = ntreport.render_batch_pdf(results, pdf)
        except Exception as e:
            out["pdfError"] = str(e)
    print(json.dumps(out, indent=2))
    return 0


def _watchdog(threshold: float, interval: float, exe: str) -> int:
    print(
        json.dumps(
            {"command": "watchdog", "status": "watching", "threshold": threshold,
             "interval": interval, "exe": exe},
            indent=2,
        )
    )
    try:
        ntwatchdog.watch(threshold_sec=threshold, interval_sec=interval, exe=exe)
    except KeyboardInterrupt:
        print(json.dumps({"command": "watchdog", "status": "stopped"}, indent=2))
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="nt8bridge", add_help=True)
    sub = parser.add_subparsers(dest="command")
    sub.add_parser("doctor")
    p_pre = sub.add_parser("precheck")
    p_pre.add_argument("--strategy", required=True)
    p_dep = sub.add_parser("deploy")
    p_dep.add_argument("--strategy", required=True)
    p_dep.add_argument("--kind", default="strategy")
    p_com = sub.add_parser("compile")
    p_com.add_argument("--type", required=True)
    p_com.add_argument("--timeout", type=float, default=30.0)
    p_bt = sub.add_parser("backtest")
    p_bt.add_argument("--config", required=True)
    p_bt.add_argument("--timeout", type=float, default=120.0)
    p_bt.add_argument("--pdf", nargs="?", const="report.pdf", default=None)
    p_batch = sub.add_parser("batch")
    p_batch.add_argument("--batch", required=True)
    p_batch.add_argument("--timeout", type=float, default=120.0)
    p_batch.add_argument("--pdf", nargs="?", const="batch_report.pdf", default=None)
    p_wd = sub.add_parser("watchdog")
    p_wd.add_argument("--threshold", type=float, default=60.0)
    p_wd.add_argument("--interval", type=float, default=10.0)
    p_wd.add_argument("--exe", default=ntwatchdog.NT8_EXE_DEFAULT)

    if not argv:
        print(CAPABILITY)
        return 0

    args = parser.parse_args(argv)
    if args.command == "doctor":
        return _doctor()
    if args.command == "precheck":
        return _precheck(args.strategy)
    if args.command == "deploy":
        return _deploy(args.strategy, args.kind)
    if args.command == "compile":
        return _compile(args.type, args.timeout)
    if args.command == "backtest":
        return _backtest(args.config, args.timeout, args.pdf)
    if args.command == "batch":
        return _batch(args.batch, args.timeout, args.pdf)
    if args.command == "watchdog":
        return _watchdog(args.threshold, args.interval, args.exe)
    print(CAPABILITY)
    return 0
