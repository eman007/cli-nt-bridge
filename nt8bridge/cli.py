"""CLI entrypoint. Every command emits structured output to stdout."""
from __future__ import annotations

import argparse
import json
import sys

from nt8bridge import account as ntaccount
from nt8bridge import backtest as ntbacktest
from nt8bridge import peek as ntpeek
from nt8bridge import probe as ntprobe
from nt8bridge import configure as ntconfigure
from nt8bridge import batch as ntbatch
from nt8bridge import flatten as ntflatten
from nt8bridge import watch as ntwatch
from nt8bridge import connections as ntconnections
from nt8bridge import reconnect as ntreconnect
from nt8bridge import connwatch as ntconnwatch
from nt8bridge import compile as ntcompile
from nt8bridge import config as ntconfig
from nt8bridge import deploy, ntio, precheck
from nt8bridge import report as ntreport
from nt8bridge import sweep as ntsweep
from nt8bridge import watchdog as ntwatchdog
from nt8bridge import feedhealth as ntfeedhealth
from nt8bridge import feedwatch as ntfeedwatch
from nt8bridge import performance as ntperformance
from nt8bridge import perfwindow as ntperfwindow
from nt8bridge import chartseries as ntchartseries
from nt8bridge import histdump as nthistdump
from nt8bridge import histget as nthistget

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
  python -m nt8bridge sweep    --config c.json --instruments 'MNQ 09-26,MES 09-26' --bars 'Minute:1,Minute:5'  backtest matrix (--pdf)
  python -m nt8bridge account  --name Sim101 read NT8 live state (positions/orders/PnL)
  python -m nt8bridge performance --name Sim101 [--from D --to D] account trade performance (PF/win%/DD; --pdf)
  python -m nt8bridge perfwindow --name BSKELA... [--generate --from D --to D]  read prop commissions/fees from the Trade Performance window (server-side; --generate opens+builds it hands-off)
  python -m nt8bridge peek                       read latest SA result + param read-back (no run)
  python -m nt8bridge probe                      dump tab/tabStrategyProperties/template props (discover names)
  python -m nt8bridge configure --config c.json  set instrument/dates/bar type/fill/params on the SA tab
  python -m nt8bridge chartseries --instrument 'MES 09-26' --bars-type Minute --bars-value 5  change a LIVE chart's data series
  python -m nt8bridge histdump --instrument 'MNQ*' --out ./out/PARQUET  offline .nrd -> L1/L2 UTC parquet (default, no NinjaTrader; --nt8 for legacy CSV)
  python -m nt8bridge histget  --instrument 'MNQ 09-26' --from 20260706 --to 20260709  download missing MarketReplay .nrd (RequestMarketReplay per date)
  python -m nt8bridge flatten  --name Sim101 force-close positions + cancel orders (kill switch)
  python -m nt8bridge watch    --name Sim101 auto-flatten NAKED positions (loop)
  python -m nt8bridge feedhealth --instrument 'MNQ 09-26' last-tick age (detect FROZEN feed)
  python -m nt8bridge feedwatch  --instrument 'MNQ 09-26' alert on a frozen-but-connected feed (loop)
  python -m nt8bridge connections                read connection status (live/dropped)
  python -m nt8bridge reconnect --name X         reconnect a dropped connection
  python -m nt8bridge connwatch --name X         auto-reconnect inadvertent drops (loop)
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


def _account(name: str, timeout: float) -> int:
    try:
        payload = ntaccount.run_account_state(name, timeout=timeout)
    except TimeoutError as e:
        # A timeout is itself diagnostic: NT8 down, or NT8BridgeServer AddOn not loaded.
        print(
            json.dumps(
                {"command": "account", "status": "timeout", "ok": False, "message": str(e)},
                indent=2,
            )
        )
        return 1
    out = {"command": "account"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _performance(name: str, from_: str, to: str, instrument: str, timeout: float, pdf=None) -> int:
    try:
        payload = ntperformance.run_performance(
            name, from_=from_, to=to, instrument=instrument, timeout=timeout
        )
    except (TimeoutError, ValueError) as e:
        # A timeout is itself diagnostic: NT8 down, or NT8BridgeServer AddOn not loaded.
        status = "timeout" if isinstance(e, TimeoutError) else "error"
        print(json.dumps({"command": "performance", "status": status, "ok": False, "message": str(e)}, indent=2))
        return 1
    out = {"command": "performance"}
    out.update(payload)
    if payload.get("status") == "ok":
        try:
            s = ntreport.compute_stats(payload)
            out["scorecard"] = {
                k: s[k] for k in (
                    "net", "trades", "profit_factor", "max_dd", "win_rate",
                    "avg_trade", "avg_win", "avg_loss", "n_wins", "n_losses",
                )
            }
        except Exception as e:
            out["scorecardError"] = str(e)
        if pdf:
            try:
                out["pdf"] = ntreport.render_pdf(payload, pdf)
            except Exception as e:
                out["pdfError"] = str(e)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _perfwindow(name: str, generate: bool, from_: str, to: str, timeout: float) -> int:
    try:
        payload = ntperfwindow.run_perfwindow(name, generate=generate, from_=from_, to=to, timeout=timeout)
    except TimeoutError as e:
        # A timeout is itself diagnostic: NT8 down, or NT8BridgeServer AddOn not loaded.
        print(json.dumps({"command": "perfwindow", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    out = {"command": "perfwindow"}
    out.update(payload)
    reports = payload.get("reports") or []
    if any(r.get("feesCalculated") is False for r in reports):
        print("WARNING: feesCalculated=false — treat totalFees as unverified "
              "(NT8 build mismatch, or fees not yet generated in the Trade Performance window).",
              file=sys.stderr)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _peek(timeout: float) -> int:
    try:
        payload = ntpeek.run_peek(timeout=timeout)
    except TimeoutError as e:
        # A timeout is diagnostic: NT8 down, or NT8BridgeServer AddOn not loaded.
        print(json.dumps({"command": "peek", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    out = {"command": "peek"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _probe(timeout: float) -> int:
    try:
        payload = ntprobe.run_probe(timeout=timeout)
    except TimeoutError as e:
        print(json.dumps({"command": "probe", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    out = {"command": "probe"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _configure(config_path: str, timeout: float) -> int:
    cfg = ntconfig.load_config(config_path)
    try:
        payload = ntconfigure.run_configure(cfg, timeout=timeout)
    except TimeoutError as e:
        print(json.dumps({"command": "configure", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    out = {"command": "configure"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _chartseries(args) -> int:
    try:
        payload = ntchartseries.run_chartseries(
            instrument=args.instrument or None,
            bars_type=args.bars_type or None,
            bars_value=args.bars_value,
            bars_value2=args.bars_value2,
            bars_base_value=args.bars_base_value,
            on_instrument=args.on_instrument or None,
            on_title=args.on_title or None,
            force=args.force,
            timeout=args.timeout,
        )
    except ValueError as e:
        print(json.dumps({"command": "chartseries", "ok": False, "status": "error", "message": str(e)}, indent=2))
        return 1
    except TimeoutError as e:
        print(json.dumps({"command": "chartseries", "ok": False, "status": "timeout", "message": str(e)}, indent=2))
        return 1
    out = {"command": "chartseries"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _histdump(args) -> int:
    try:
        payload = nthistdump.run_histdump(
            instrument_glob=args.instrument,
            out_dir=args.out,
            replay_dir=args.replay_dir or None,
            engine="nt8" if args.nt8 else "offline",
            levels=tuple(args.levels),
            validate=args.validate,
            mode=args.mode,
            parquet=args.parquet,
            force=args.force,
            validate_only=args.validate_only,
            timeout=args.timeout,
        )
    except TimeoutError as e:
        print(json.dumps({"command": "histdump", "ok": False, "status": "timeout", "message": str(e)}, indent=2))
        return 1
    out = {"command": "histdump"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") in ("ok", "validated") else 1


def _histget(args) -> int:
    try:
        payload = nthistget.run_histget(
            instrument=args.instrument,
            from_date=args.from_,
            to_date=args.to,
            skip_existing=not (args.no_skip_existing or args.force),
            replay_dir=args.replay_dir or None,
            timeout=args.timeout,
        )
    except TimeoutError as e:
        print(json.dumps({"command": "histget", "ok": False, "status": "timeout", "message": str(e)}, indent=2))
        return 1
    out = {"command": "histget"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    # non-zero if nothing downloaded AND something failed (so a script can gate on it)
    ok = payload.get("status") == "ok" and not payload.get("failed")
    return 0 if ok else (0 if payload.get("count") else 1)


def _flatten(name: str, instrument: str, timeout: float) -> int:
    try:
        payload = ntflatten.run_flatten(name, instrument, timeout=timeout)
    except (TimeoutError, ValueError) as e:
        print(json.dumps({"command": "flatten", "ok": False, "message": str(e)}, indent=2))
        return 1
    out = {"command": "flatten"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _watch(names: list, grace: float, interval: float, once: bool) -> int:
    try:
        killed = ntwatch.watch(
            names, grace_seconds=grace, interval=interval,
            max_iterations=(1 if once else None),
        )
    except (ValueError, KeyboardInterrupt) as e:
        print(json.dumps({"command": "watch", "status": "stopped", "message": str(e)}, indent=2))
        return 0
    print(json.dumps({"command": "watch", "ok": True, "accounts": names, "killed": killed}, indent=2))
    return 0


def _connections(timeout: float) -> int:
    try:
        payload = ntconnections.run_connections(timeout=timeout)
    except TimeoutError as e:
        # A timeout is diagnostic: NT8 down, or NT8BridgeServer AddOn not loaded.
        print(json.dumps({"command": "connections", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    out = {"command": "connections"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _reconnect(name: str, timeout: float) -> int:
    try:
        payload = ntreconnect.run_reconnect(name, timeout=timeout)
    except (TimeoutError, ValueError) as e:
        print(json.dumps({"command": "reconnect", "ok": False, "message": str(e)}, indent=2))
        return 1
    out = {"command": "reconnect"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


def _connwatch(names: list, grace: float, interval: float, max_attempts: int, once: bool) -> int:
    try:
        events = ntconnwatch.watch(
            names, grace_seconds=grace, interval=interval, max_attempts=max_attempts,
            max_iterations=(1 if once else None),
        )
    except (ValueError, KeyboardInterrupt) as e:
        print(json.dumps({"command": "connwatch", "status": "stopped", "message": str(e)}, indent=2))
        return 0
    print(json.dumps({"command": "connwatch", "ok": True, "connections": names, "events": events}, indent=2))
    return 0


def _feedhealth(instruments: list, max_age: float, timeout: float) -> int:
    try:
        payload = ntfeedhealth.run_feedhealth(instruments, timeout=timeout)
    except TimeoutError as e:
        # A timeout is diagnostic: NT8 down, or the NT8BridgeServer AddOn is not loaded.
        print(json.dumps({"command": "feedhealth", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    except ValueError as e:
        print(json.dumps({"command": "feedhealth", "status": "error", "ok": False, "message": str(e)}, indent=2))
        return 1
    state = ntfeedhealth.parse_feedhealth_response(payload)
    stale = state.stale_feeds(max_age, allow=instruments)
    out = {"command": "feedhealth"}
    out.update(payload)
    out["staleFeeds"] = [f.get("instrument") for f in stale]
    print(json.dumps(out, indent=2))
    # non-zero exit when any watched feed is frozen, so a caller/script can gate on it.
    return 1 if stale else 0


def _feedwatch(instruments: list, grace: float, interval: float, realert: float,
               max_age: float, once: bool) -> int:
    try:
        events = ntfeedwatch.watch(
            instruments, grace_seconds=grace, interval=interval, realert_seconds=realert,
            max_age_seconds=max_age, max_iterations=(1 if once else None),
        )
    except (ValueError, KeyboardInterrupt) as e:
        print(json.dumps({"command": "feedwatch", "status": "stopped", "message": str(e)}, indent=2))
        return 0
    print(json.dumps({"command": "feedwatch", "ok": True, "instruments": instruments, "events": events}, indent=2))
    return 0


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


def _sweep(config_path, instruments, bars, params_file, timeout, pdf=None) -> int:
    import json as _json
    from pathlib import Path
    base = _json.loads(Path(config_path).read_text(encoding="utf-8"))
    instrs = [s.strip() for s in instruments.split(",") if s.strip()]
    barlist = [s.strip() for s in bars.split(",") if s.strip()]
    paramsets = None
    if params_file:
        paramsets = _json.loads(Path(params_file).read_text(encoding="utf-8"))
    runs = ntsweep.run_sweep(base, instrs, barlist, paramsets=paramsets, timeout=timeout)
    out = {"command": "sweep", "ok": True, "runs": runs}
    if pdf:
        try:
            out["pdf"] = ntreport.render_batch_pdf(runs, pdf)
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
    p_acct = sub.add_parser("account")
    p_acct.add_argument("--name", default="", help="account name filter, e.g. Sim101 (empty = all)")
    p_acct.add_argument("--timeout", type=float, default=15.0)
    p_perf = sub.add_parser("performance")
    p_perf.add_argument("--name", default="", help="account, e.g. Sim101 (REQUIRED)")
    p_perf.add_argument("--from", dest="from_", default="", help="start date ET YYYY-MM-DD (default: today)")
    p_perf.add_argument("--to", default="", help="end date ET YYYY-MM-DD, inclusive (default: now)")
    p_perf.add_argument("--instrument", default="", help="limit to one instrument, e.g. 'MNQ 09-26'")
    p_perf.add_argument("--timeout", type=float, default=20.0)
    p_perf.add_argument("--pdf", nargs="?", const="performance.pdf", default=None)
    p_pw = sub.add_parser("perfwindow")
    p_pw.add_argument("--name", default="", help="account shown in the open Trade Performance window (empty = all report tabs)")
    p_pw.add_argument("--generate", action="store_true", help="drive the window: set account+date range and click Generate before reading (hands-off)")
    p_pw.add_argument("--from", dest="from_", default="", help="start date ET YYYY-MM-DD (with --generate)")
    p_pw.add_argument("--to", default="", help="end date ET YYYY-MM-DD (with --generate)")
    p_pw.add_argument("--timeout", type=float, default=20.0)
    p_peek = sub.add_parser("peek")
    p_peek.add_argument("--timeout", type=float, default=30.0)
    p_probe = sub.add_parser("probe")
    p_probe.add_argument("--timeout", type=float, default=30.0)
    p_conf = sub.add_parser("configure")
    p_conf.add_argument("--config", required=True, help="config.json with params to apply to the SA tab")
    p_conf.add_argument("--timeout", type=float, default=30.0)
    p_flat = sub.add_parser("flatten")
    p_flat.add_argument("--name", required=True, help="account to flatten, e.g. Sim101 (REQUIRED)")
    p_flat.add_argument("--instrument", default="", help="limit to one instrument, e.g. 'MNQ 06-26' (empty = all)")
    p_flat.add_argument("--timeout", type=float, default=20.0)
    p_watch = sub.add_parser("watch")
    p_watch.add_argument("--name", action="append", required=True, help="account(s) to watch; repeatable")
    p_watch.add_argument("--grace", type=float, default=20.0, help="seconds a position may stay naked before kill")
    p_watch.add_argument("--interval", type=float, default=5.0, help="seconds between scans")
    p_watch.add_argument("--once", action="store_true", help="single scan (no loop) — for testing")
    p_conns = sub.add_parser("connections")
    p_conns.add_argument("--timeout", type=float, default=15.0)
    p_recon = sub.add_parser("reconnect")
    p_recon.add_argument("--name", required=True, help="connection name to reconnect (REQUIRED)")
    p_recon.add_argument("--timeout", type=float, default=30.0)
    p_cwatch = sub.add_parser("connwatch")
    p_cwatch.add_argument("--name", action="append", required=True, help="connection(s) to guard; repeatable")
    p_cwatch.add_argument("--grace", type=float, default=20.0, help="seconds a connection may stay dropped before reconnect")
    p_cwatch.add_argument("--interval", type=float, default=10.0, help="seconds between scans")
    p_cwatch.add_argument("--max-attempts", type=int, default=5, help="reconnect attempts before giving up (per drop)")
    p_cwatch.add_argument("--once", action="store_true", help="single scan (no loop) — for testing")
    p_fh = sub.add_parser("feedhealth")
    p_fh.add_argument("--instrument", action="append", required=True, help="instrument full name, e.g. 'MNQ 09-26'; repeatable")
    p_fh.add_argument("--max-age", type=float, default=10.0, help="seconds since last tick before a connected feed is called frozen")
    p_fh.add_argument("--timeout", type=float, default=15.0)
    p_fw = sub.add_parser("feedwatch")
    p_fw.add_argument("--instrument", action="append", required=True, help="instrument(s) to watch for a frozen feed; repeatable")
    p_fw.add_argument("--grace", type=float, default=20.0, help="seconds a feed may read stale before the first alert")
    p_fw.add_argument("--interval", type=float, default=5.0, help="seconds between scans")
    p_fw.add_argument("--realert", type=float, default=30.0, help="seconds between repeat alerts while a feed stays frozen")
    p_fw.add_argument("--max-age", type=float, default=10.0, help="seconds since last tick = frozen")
    p_fw.add_argument("--once", action="store_true", help="single scan (no loop) — for testing")
    p_batch = sub.add_parser("batch")
    p_batch.add_argument("--batch", required=True)
    p_batch.add_argument("--timeout", type=float, default=120.0)
    p_batch.add_argument("--pdf", nargs="?", const="batch_report.pdf", default=None)
    p_sweep = sub.add_parser("sweep")
    p_sweep.add_argument("--config", required=True, help="base config.json (strategy template + base params)")
    p_sweep.add_argument("--instruments", required=True, help="comma list, e.g. 'MNQ 09-26,MES 09-26'")
    p_sweep.add_argument("--bars", required=True, help="comma list of <BarsPeriodType>:<Value>, e.g. 'Minute:1,Minute:5'")
    p_sweep.add_argument("--params-file", dest="params_file", default=None, help="optional JSON list of {label,params} to cross")
    p_sweep.add_argument("--timeout", type=float, default=120.0)
    p_sweep.add_argument("--pdf", nargs="?", const="sweep_report.pdf", default=None)
    p_wd = sub.add_parser("watchdog")
    p_wd.add_argument("--threshold", type=float, default=60.0)
    p_wd.add_argument("--interval", type=float, default=10.0)
    p_wd.add_argument("--exe", default=ntwatchdog.NT8_EXE_DEFAULT)
    p_cs = sub.add_parser("chartseries")
    p_cs.add_argument("--instrument", default="", help="new instrument, e.g. 'MES 09-26' (omit to keep current)")
    p_cs.add_argument("--bars-type", dest="bars_type", default="", help="BarsPeriodType, e.g. Minute/Second/Tick/Day/Range/Renko/Volume")
    p_cs.add_argument("--bars-value", dest="bars_value", type=int, default=None, help="period value, e.g. 5")
    p_cs.add_argument("--bars-value2", dest="bars_value2", type=int, default=None, help="2nd bar value for custom types (e.g. NinzaRenko reversal)")
    p_cs.add_argument("--bars-base-value", dest="bars_base_value", type=int, default=None, help="3rd bar value (BaseBarsPeriodValue) for UniRenko-style types (Open Offset)")
    p_cs.add_argument("--on-instrument", dest="on_instrument", default="", help="target the chart showing this instrument")
    p_cs.add_argument("--on-title", dest="on_title", default="", help="target the chart with this window/tab title")
    p_cs.add_argument("--force", action="store_true", help="override the safety guard (enabled strategy / open position)")
    p_cs.add_argument("--timeout", type=float, default=30.0)

    p_hd = sub.add_parser("histdump")
    p_hd.add_argument("--instrument", required=True, help="instrument name or glob, e.g. 'MNQ*' or 'MNQ 03-25'")
    p_hd.add_argument("--out", required=True, help="output root, e.g. ./out/MNQ_TICK")
    p_hd.add_argument("--replay-dir", dest="replay_dir", default="", help="db/replay dir (default: <NT8>/db/replay)")
    p_hd.add_argument("--mode", default="depth", choices=["depth"], help="export mode (v1: depth only)")
    p_hd.add_argument("--parquet", action="store_true", help="also write a parquet beside each CSV")
    p_hd.add_argument("--force", action="store_true", help="re-export dates that already have a CSV")
    p_hd.add_argument("--validate-only", dest="validate_only", action="store_true", help="(--nt8) run the CSV equivalence gate and stop")
    p_hd.add_argument("--nt8", action="store_true", help="use the legacy NT8 DumpMarketDepth CSV engine (needs NinjaTrader)")
    p_hd.add_argument("--levels", nargs="+", choices=["L1", "L2"], default=["L1", "L2"], help="offline: record levels to write (default both)")
    p_hd.add_argument("--validate", action="store_true", help="offline: decode + diff vs a fresh NT8 dump (needs NinjaTrader); writes nothing")
    p_hd.add_argument("--timeout", type=float, default=300.0, help="seconds per date export")

    p_hg = sub.add_parser("histget")
    p_hg.add_argument("--instrument", required=True, help="instrument full name, e.g. 'MNQ 09-26'")
    p_hg.add_argument("--from", dest="from_", required=True, help="start date YYYYMMDD (ET)")
    p_hg.add_argument("--to", required=True, help="end date YYYYMMDD inclusive (ET)")
    p_hg.add_argument("--no-skip-existing", dest="no_skip_existing", action="store_true", help="re-download dates that already have a .nrd")
    p_hg.add_argument("--force", action="store_true", help="force re-download + overwrite dates that already have a .nrd (alias of --no-skip-existing)")
    p_hg.add_argument("--replay-dir", dest="replay_dir", default="", help="db/replay dir (default: <NT8>/db/replay)")
    p_hg.add_argument("--timeout", type=float, default=600.0, help="AddOn wait per date, seconds (heavy MNQ days run 300-460s)")

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
    if args.command == "account":
        return _account(args.name, args.timeout)
    if args.command == "performance":
        return _performance(args.name, args.from_, args.to, args.instrument, args.timeout, args.pdf)
    if args.command == "perfwindow":
        # generating drives a server fetch + report rebuild -> allow more time by default.
        to_ = args.timeout if args.timeout != 20.0 else (200.0 if args.generate else 20.0)
        return _perfwindow(args.name, args.generate, args.from_, args.to, to_)
    if args.command == "peek":
        return _peek(args.timeout)
    if args.command == "probe":
        return _probe(args.timeout)
    if args.command == "configure":
        return _configure(args.config, args.timeout)
    if args.command == "flatten":
        return _flatten(args.name, args.instrument, args.timeout)
    if args.command == "watch":
        return _watch(args.name, args.grace, args.interval, args.once)
    if args.command == "connections":
        return _connections(args.timeout)
    if args.command == "reconnect":
        return _reconnect(args.name, args.timeout)
    if args.command == "connwatch":
        return _connwatch(args.name, args.grace, args.interval, args.max_attempts, args.once)
    if args.command == "feedhealth":
        return _feedhealth(args.instrument, args.max_age, args.timeout)
    if args.command == "feedwatch":
        return _feedwatch(args.instrument, args.grace, args.interval, args.realert, args.max_age, args.once)
    if args.command == "batch":
        return _batch(args.batch, args.timeout, args.pdf)
    if args.command == "sweep":
        return _sweep(args.config, args.instruments, args.bars, args.params_file, args.timeout, args.pdf)
    if args.command == "watchdog":
        return _watchdog(args.threshold, args.interval, args.exe)
    if args.command == "chartseries":
        return _chartseries(args)
    if args.command == "histdump":
        return _histdump(args)
    if args.command == "histget":
        return _histget(args)
    print(CAPABILITY)
    return 0
