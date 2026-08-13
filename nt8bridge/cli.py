"""CLI entrypoint. Every command emits structured output to stdout."""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

from nt8bridge import account as ntaccount
from nt8bridge import backtest as ntbacktest
from nt8bridge import peek as ntpeek
from nt8bridge import probe as ntprobe
from nt8bridge import configure as ntconfigure
from nt8bridge import batch as ntbatch
from nt8bridge import flatten as ntflatten
from nt8bridge import order as ntorder
from nt8bridge import watch as ntwatch
from nt8bridge import connections as ntconnections
from nt8bridge import playback as ntplayback
from nt8bridge import screenshot as ntscreenshot
from nt8bridge import ntstatus as ntntstatus
from nt8bridge import workspace as ntworkspace
from nt8bridge import layout as ntlayout
from nt8bridge import selfcheck as ntselfcheck
from nt8bridge import logread as ntlogread
from nt8bridge import dialog as ntdialog
from nt8bridge import strategy as ntstrategy
from nt8bridge import playbackctl as ntpbctl
from nt8bridge import chart as ntchart
from nt8bridge import reconnect as ntreconnect
from nt8bridge import connwatch as ntconnwatch
from nt8bridge import compile as ntcompile
from nt8bridge import reload as ntreload
from nt8bridge import windows as ntwindows
from nt8bridge import regions as ntregions
from nt8bridge import restart as ntrestart
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
  python -m nt8bridge compile                    in-process compile (needs AddOn)
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
  python -m nt8bridge playback                   replay transport: clock, MOVING?, speed, .nrd coverage
  python -m nt8bridge ntstatus                   is NT running the code on disk? (catches a stale DLL)
  python -m nt8bridge selfcheck                  is THIS CLI the fleet's CLI? version, module hash, deps
  python -m nt8bridge log --file trace --grep ERROR   grep a log INSIDE NT (the file NT holds open)
  python -m nt8bridge chart                      charts + their indicators
  python -m nt8bridge chart --add-indicator SentinelTrend --chart NQ   attach one (verified by count)
  python -m nt8bridge playbackctl --api          what this NT build's replay transport exposes
  python -m nt8bridge playbackctl --seek '2026-04-21 17:00'  seek, WAIT for the clock to settle, then judge
  python -m nt8bridge strategy                   chart strategies + whether they are actually RUNNING
  python -m nt8bridge strategy --disable Keel    stop one (verified by re-reading its State)
  python -m nt8bridge dialog                     list modal dialogs blocking this box
  python -m nt8bridge dialog --dismiss 'Rollover' --button No   answer one (explicit, verified)
  python -m nt8bridge workspace                  charts + indicators + strategies AND their enabled state
  python -m nt8bridge screenshot --title Conductor   capture a window as PNG (see the screen, don't relay it)
  python -m nt8bridge layout --out fleet.json      capture where NT's windows sit (a hashable file)
  python -m nt8bridge layout --apply fleet.json    put them back, headlessly, on any box
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
    # `strategy` is a SOURCE PATH to stage into bin\Custom, not a class name. The old flag name
    # implied otherwise and cost a caller a wasted round trip ("deploy --strategy MyThing" fails with
    # FileNotFoundError: 'MyThing'). --from is the accurate name; --strategy still works.
    if not os.path.exists(strategy):
        print(json.dumps({
            "command": "deploy", "ok": False, "status": "error",
            "message": f"not a file: {strategy}",
            "hint": "deploy stages a SOURCE FILE into bin\\Custom. Pass a path "
                    "(--from C:\\src\\MyThing.cs), not a class name. To make already-in-tree code "
                    "live, use `reload`.",
        }, indent=2))
        return 2
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
                {
                    "command": "compile",
                    "status": "timeout",
                    "ok": False,
                    "message": str(e),
                    # A timeout is NOT proof of a broken bridge: NinjaTrader may still be
                    # compiling. Say so, so a caller does not read it as a failed compile.
                    "hint": (
                        "no result within the timeout -- NinjaTrader may still be compiling "
                        "a large tree. Run `doctor` to tell 'AddOn not loaded' apart from "
                        "'slow compile', or retry with a longer --timeout."
                    ),
                },
                indent=2,
            )
        )
        return 1
    # Duplicated generated regions break the WHOLE-TREE compile with CS0111/CS0102 that never name
    # the cause. Cheap to detect, so detect it here rather than leave it to a human's memory.
    dupes = []
    try:
        dupes = [f["path"] for f in ntregions.scan(ntregions.custom_root(ntio.nt8_root()))
                 if f["duplicated"]]
    except Exception:  # noqa: BLE001 - a scan failure must never mask the compile result
        pass
    print(
        json.dumps(
            {
                "command": "compile",
                "ok": res.ok,
                "errors": res.errors,
                "assemblyReloaded": res.assembly_reloaded,
                "duplicatedRegions": dupes or None,
                "regionsHint": (
                    "DUPLICATED NinjaScript generated regions found — these break the whole-tree "
                    "compile with CS0111/CS0102 that do not name the cause. Run `regions --strip`."
                ) if dupes else None,
            },
            indent=2,
        )
    )
    return 0 if res.ok else 1


def _reload(timeout: float) -> int:
    """Full build + assembly swap — the step that used to require a human pressing F5."""
    try:
        res = ntreload.run_reload(timeout=timeout)
    except TimeoutError as e:
        print(json.dumps({
            "command": "reload", "status": "timeout", "ok": False, "message": str(e),
            "hint": "a reload emits and swaps the assembly, which is real work — slower than "
                    "`compile`. NT may still be reloading; retry with a longer --timeout.",
        }, indent=2))
        return 2
    print(json.dumps({
        "command": "reload", "ok": res.ok, "errors": res.errors,
        "assemblyReloaded": res.assembly_reloaded,
        "note": None if res.assembly_reloaded else
                "assembly NOT reloaded — the build failed, so NT kept the previous assembly.",
    }, indent=2))
    return 0 if res.ok else 1


def _windows(timeout: float, offscreen_only: bool) -> int:
    try:
        payload = ntwindows.run_windows(timeout=timeout)
    except TimeoutError as e:
        print(json.dumps({"command": "windows", "status": "timeout", "ok": False,
                          "message": str(e)}, indent=2))
        return 2
    wins = payload.get("windows", [])
    if offscreen_only:
        wins = [w for w in wins if ntwindows.offscreen(w)]
    print(json.dumps({"command": "windows", "ok": payload.get("status") == "ok",
                      "count": len(wins), "windows": wins}, indent=2))
    return 0


def _regions(root: str, strip: bool, all_files: bool) -> int:
    base = ntregions.custom_root(root or ntio.nt8_root())
    found = ntregions.scan(base)
    dupes = [f for f in found if f["duplicated"]]
    stripped = []
    if strip:
        for f in (found if all_files else dupes):
            n = ntregions.strip_file(f["path"])
            if n:
                stripped.append({"path": f["path"], "removed": n})
    print(json.dumps({
        "command": "regions", "ok": True, "root": str(base),
        "filesWithRegions": len(found), "filesDuplicated": len(dupes),
        "duplicated": dupes,
        "stripped": stripped,
        "hint": None if not dupes or strip else
                "DUPLICATED generated regions WILL break the whole-tree compile (CS0111/CS0102) and "
                "the errors will not name the cause. Run `regions --strip` before compiling; NT "
                "regenerates exactly one on its next real build.",
    }, indent=2))
    return 0


def _restart(task: str, exe: str, wait: float, stop_timeout: float) -> int:
    """STOP, confirm it stopped, then START. In that order, and the stop may not be skipped.

    The first version never stopped anything: it launched, then called wait_for(True), which returned
    immediately because the ORIGINAL process was up. It reported ok:true having restarted nothing and
    left two NinjaTraders against one user directory — a check that could not fail.
    """
    was = ntrestart.is_running()
    kind, selected = ntrestart.choose_start(task, exe)
    method = "none" if kind == "none" else selected

    def emit(ok: bool, stage: str, detail: str, **extra) -> int:
        print(json.dumps({
            "command": "restart", "ok": ok, "stage": stage, "wasRunning": was,
            "method": method, "detail": detail, "runningNow": ntrestart.is_running(),
            "note": "a scheduled task created with /IT reaches the interactive session — required "
                    "for UI automation from an SSH/session-0 shell.",
            **extra,
        }, indent=2))
        return 0 if ok else 1

    # Refuse before doing anything if there is no way to start it again. Stopping NinjaTrader and then
    # discovering we cannot restart it is the worst outcome available here.
    if kind == "none":
        return emit(False, "select", selected)

    stopped, stop_detail = ntrestart.stop(timeout=stop_timeout)
    if not stopped:
        return emit(False, "stop", stop_detail,
                    hint="refusing to launch while NinjaTrader is still running — a second instance "
                         "against one user directory is a worse state than a failed restart.")
    if ntrestart.is_running():   # re-check: the stop and the launch are not atomic
        return emit(False, "stop", "still running after a stop that reported success",
                    hint="something restarted it between the stop and the launch — a relaunch "
                         "watchdog, most likely. Disable it for the duration, or restart via its task.")

    started, detail = (ntrestart.run_task(task) if kind == "task"
                       else ntrestart.launch_exe(exe))
    if not started:
        return emit(False, "start", detail,
                    hint="NinjaTrader is now STOPPED and the start failed — start it by hand.")
    up = ntrestart.wait_for(True, wait)
    return emit(up, "start" if up else "wait",
                detail if up else f"{detail}; not running again within {wait}s",
                stopDetail=stop_detail)


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
    # ⛔ REFUSE A --replay-dir THAT PRETENDS TO BE A SANDBOX. The AddOn writes into NT8's own
    # db\replay and takes no destination from us, so pointing this at a scratch directory used to
    # produce downloads in the LIVE corpus while the scratch stayed empty (measured 2026-08-09).
    # The flag's real job is choosing which inventory to check for existing files; that is now
    # --check-dir, and this one is only accepted when it names the directory writes truly go to.
    real = nthistget.nt_replay_dir()
    chosen = args.check_dir or args.replay_dir
    if args.replay_dir and Path(args.replay_dir).resolve() != real.resolve():
        print(json.dumps({
            "command": "histget", "ok": False, "status": "refused",
            "message": "--replay-dir does not choose where downloads are written; NT8 always "
                       f"writes to {real}. It only selects the inventory checked for existing "
                       "files. Re-run with --check-dir for that, or drop the flag.",
            "writes_to": str(real), "you_passed": str(Path(args.replay_dir))}, indent=2))
        return 2
    try:
        payload = nthistget.run_histget(
            instrument=args.instrument,
            from_date=args.from_,
            to_date=args.to,
            skip_existing=not (args.no_skip_existing or args.force),
            replay_dir=chosen or None,
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


def _order(args) -> int:
    try:
        payload = ntorder.run_order(
            args.action, timeout=args.timeout,
            account=args.name, instrument=args.instrument, working=not args.all_states,
            side=args.side, type=args.type, tif=args.tif, quantity=args.quantity,
            limitPrice=args.limit_price, stopPrice=args.stop_price,
            orderId=args.order_id, name=args.order_name, oco=args.oco,
            settle=args.settle, all=args.all, confirm=args.confirm,
        )
    except (TimeoutError, ValueError) as e:
        print(json.dumps({"command": "order", "ok": False, "message": str(e)}, indent=2))
        return 2 if isinstance(e, ValueError) else 1
    out = {"command": "order"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("status") == "ok" else 1


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


def _connections_mutate(args) -> int:
    """Raise or drop a connection.

    Exit 1 unless the status actually SETTLED where it was asked to go — a call that
    resolved and moved nothing is a failure, not a success.
    """
    action = "connect" if args.connect else "disconnect"
    name = args.connect or args.disconnect
    try:
        payload = ntconnections.run_connect(action=action, name=name,
                                            confirm=args.confirm, wait_ms=args.wait_ms)
    except ValueError as e:
        print(json.dumps({"command": "connections", "status": "error", "ok": False,
                          "errors": [{"code": "ARGS", "message": str(e)}]}, indent=2))
        return 2
    except TimeoutError as e:
        print(json.dumps({"command": "connections", "status": "timeout", "ok": False,
                          "message": str(e)}, indent=2))
        return 1
    out = {"command": "connections"}
    out.update(payload)
    print(json.dumps(out, indent=2))
    return 0 if payload.get("succeeded") else 1


def _playback(instrument: str | None, timeout: float, require_ready: bool) -> int:
    try:
        payload = ntplayback.run_playback(instrument, timeout=timeout)
    except TimeoutError as e:
        print(json.dumps({"command": "playback", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    state = ntplayback.parse_playback_response(payload)
    ready, why = state.ready_to_seek()
    out = {"command": "playback"}
    out.update(payload)
    # State the judgement, do not leave the caller to derive it from movingSec. The whole point of
    # this command is that a moving transport is not obvious from a single clock reading.
    out["readyToSeek"] = ready
    out["readyReason"] = why
    print(json.dumps(out, indent=2))
    if payload.get("status") != "ok":
        return 1
    # Reporting state is the default; ASSERTING it is opt-in. A box with no Playback configured is
    # not a failure of this command, so --require-ready is what a bake script uses to make it one.
    return 2 if (require_ready and not ready) else 0


def _ntstatus(timeout: float) -> int:
    try:
        payload = ntntstatus.run_ntstatus(timeout=timeout)
    except TimeoutError as e:
        # Degrade rather than fail: a wedged or signed-out NT is exactly when this matters, and the
        # filesystem half of the answer is still available.
        built = ntntstatus.dll_built_utc()
        print(json.dumps({
            "command": "ntstatus", "status": "timeout", "ok": False, "message": str(e),
            "dllOnDisk": {"path": str(ntntstatus.custom_dll_path()),
                          "builtUtc": built.isoformat() if built else None},
            "note": "AddOn did not answer — NT8 down, not signed in, or NT8BridgeServer not loaded. "
                    "DLL build time above is from the filesystem.",
        }, indent=2))
        return 1
    st = ntntstatus.assess(payload)
    out = {"command": "ntstatus"}
    out.update(payload)
    out["stale"] = st.stale
    out["verdict"] = st.reason
    print(json.dumps(out, indent=2))
    # A stale assembly is a FAILING condition, not a note: acting on it is a restart.
    if payload.get("status") != "ok":
        return 1
    return 2 if st.stale else 0


def _selfcheck(requirements: list[str], expect_hash: str | None, expect_version: str | None,
               python_exe: str | None = None) -> int:
    """Local-only: no AddOn, no NinjaTrader, answers on a box where NT is not even installed.

    That is deliberate — the client half must be verifiable independently of the half it talks to,
    or a broken CLI and a down NT look identical.
    """
    if python_exe:
        tail: list[str] = []
        for r in requirements:
            tail += ["--requirements", r]
        if expect_hash:
            tail += ["--expect-hash", expect_hash]
        if expect_version:
            tail += ["--expect-version", expect_version]
        rc, payload = ntselfcheck.run_under(python_exe, tail)
        print(json.dumps(payload, indent=2))
        return rc
    res = ntselfcheck.run_selfcheck(requirements, expect_hash, expect_version)
    print(json.dumps(res.report, indent=2))
    return int(res.report.get("exit", 0))


def _log(args) -> int:
    """Exit codes: 0 read it, 1 could not, 2 --fail-on-match and something matched.

    `--fail-on-match` inverts the usual sense on purpose: as a pre-flight gate, FINDING a fault is
    the failure. Without it, matches are just data and the command succeeds.
    """
    try:
        payload = ntlogread.run_log(args.file, args.grep, args.since_min, args.tail,
                                    args.ignore_case, args.max_bytes, timeout=args.timeout)
    except TimeoutError as e:
        print(json.dumps({"command": "log", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    res = ntlogread.parse_log_response(payload)
    if args.text:
        # These logs carry box-drawing and arrow glyphs, and a Windows console still defaults to
        # cp1252 — printing them raw raises UnicodeEncodeError and loses the whole read over one
        # character. Degrade the glyph, never the answer. (The JSON path below stays ASCII-escaped
        # for the same reason, which keeps it exact on any console.)
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
        if not res.ok:
            for err in res.errors:
                print(f"error {err.get('code')}: {err.get('message')}", file=sys.stderr)
            return 1
        # The caveats go to stderr so a pipeline gets clean lines on stdout and still cannot miss
        # that it is looking at a window rather than the whole file.
        if res.truncated:
            print(f"[read the last {args.max_bytes} bytes only — line numbers count from there]",
                  file=sys.stderr)
        if args.since_min and not res.time_filter_applied:
            print(f"[{res.note or '--since was not applied'}]", file=sys.stderr)
        for ln in res.lines:
            print(ln.get("text", ""))
    else:
        out = {"command": "log"}
        out.update(payload)
        print(json.dumps(out, indent=2))
    if not res.ok:
        return 1
    return 2 if (args.fail_on_match and res.matched) else 0


def _chart(args) -> int:
    """Exit codes: 0 fine, 1 the AddOn did not answer or refused, 2 the change did not take."""
    if args.api:
        action, type_name, name = "api", None, None
    elif args.close:
        action, type_name, name = "close", None, None
    elif args.add_indicator:
        action, type_name, name = "addIndicator", args.add_indicator, None
    elif args.remove_indicator:
        action, type_name, name = "removeIndicator", None, args.remove_indicator
    elif args.apply_template:
        action, type_name, name = "applyTemplate", None, None
    elif any(v is not None for v in (args.range_type, args.days_back, args.bars_back,
                                     args.months_back, args.from_date, args.to_date)):
        action, type_name, name = "dataWindow", None, None
    else:
        action, type_name, name = "list", None, None

    try:
        params = ntstrategy.parse_params(args.param)
    except ValueError as e:
        print(json.dumps({"command": "chart", "status": "error", "ok": False, "message": str(e)}, indent=2))
        return 1
    # ⛔ NT's enum member is `CustomRange`, not `Custom` — measured 2026-08-13, and this tool's own
    # --help said "custom". A rejected value is reported honestly (PARTIALLY APPLIED … treat as NOT
    # applied), but the operator is then told to fix an input the help told them to write. Accept the
    # obvious spellings and send NT the one it actually parses; the name is not the interesting part.
    range_type = args.range_type
    if range_type and range_type.strip().lower() in ("custom", "customrange", "custom range"):
        range_type = "CustomRange"
    try:
        payload = ntchart.run_chart(action, args.chart, type_name, name, params,
                                    args.confirm, args.timeout,
                                    template_path=args.apply_template,
                                    window={"rangeType": range_type,
                                            "daysBack": args.days_back,
                                            "barsBack": args.bars_back,
                                            "monthsBack": args.months_back,
                                            "from": args.from_date,
                                            "to": args.to_date})
    except TimeoutError as e:
        print(json.dumps({"command": "chart", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1

    state = ntchart.parse_chart_response(payload)
    out = {"command": "chart"}
    out.update(payload)
    out["summary"] = state.describe()
    print(json.dumps(out, indent=2))
    if not state.ok:
        return 1
    return 0 if (action == "list" or state.succeeded) else 2


def _playbackctl(args) -> int:
    """Exit codes: 0 it took, 1 the AddOn did not answer or refused, 2 it did not take.

    A seek that settles short of its target returns 2 WITH its trajectory. Reporting only the final
    position is what made a walking-but-short seek indistinguishable from one that never moved.

    ⭐ `--seek X --speed N` RUNS BOTH, **seek first, speed last**. Measured 2026-08-13: the dispatch
    below was an if/elif chain, so a combined call executed the SEEK, printed a successful seek
    verdict, and DROPPED THE SPEED — `playback` then read `speed 0` and nothing replayed. The call
    looked like it worked, which is worse than an error, and it cost a session's worth of "why is
    nothing moving".

    ⚠ THE ORDER IS NOT ARBITRARY, AND THE FIRST VERSION OF THIS FIX GOT IT BACKWARDS. Speed-first
    was tried and driven: `--seek … --speed 7` set the speed, then the seek's settle poll spent its
    full 60 s timeout chasing a clock the speed write had already set walking — *"still 420s from
    target — the clock was still moving when we stopped watching"*. A seek can only be JUDGED
    against a parked transport. Seek to the target while it is still, then start the replay: that is
    also what the operator means by "go here and run at N".
    """
    if args.set_start or args.set_end:
        if not (args.set_start and args.set_end):
            print(json.dumps({"command": "playbackctl", "status": "error", "ok": False,
                              "message": "--set-start and --set-end must be given together"}, indent=2))
            return 1
        actions = ["range"]
    else:
        actions = []
        if args.seek:
            actions.append("seek")
        # `is not None`, not truthiness: 0 is a real speed (a parked transport reads 0) and
        # `elif args.speed` silently turned `--speed 0` into a plain status read.
        if args.speed is not None:
            actions.append("speed")
        if not actions:
            actions = ["api"]

    results, rc = [], 0
    for action in actions:
        try:
            payload = ntpbctl.run_playbackctl(action, args.seek, args.speed, args.set_start, args.set_end,
                                              args.settle_ms, args.seek_timeout_ms, args.confirm,
                                              args.force, args.timeout)
        except TimeoutError as e:
            print(json.dumps({"command": "playbackctl", "status": "timeout", "ok": False,
                              "requested": actions, "completed": [r.get("action") for r in results],
                              "message": str(e)}, indent=2))
            return 1

        state = ntpbctl.parse_response(payload)
        out = {"command": "playbackctl"}
        out.update(payload)
        out["summary"] = state.describe()
        results.append(out)
        if not state.ok:
            rc = 1
        elif action != "api" and not state.succeeded:
            rc = rc or 2
        # ⛔ Do not start the replay after a seek that did not land: running from a position we never
        # confirmed is the silent half-success this fix exists to remove.
        if rc:
            break

    if len(results) == 1:
        print(json.dumps(results[0], indent=2))
    else:
        print(json.dumps({"command": "playbackctl", "requested": actions,
                          "completed": [r.get("action") for r in results],
                          "allSucceeded": rc == 0,
                          "summary": " · ".join(r.get("summary", "") for r in results),
                          "steps": results}, indent=2))
    return rc


def _strategy(args) -> int:
    """Exit codes: 0 fine, 1 the AddOn did not answer or refused, 2 a mutation did not take
    (or --require-enabled found nothing running).

    A mutation whose call resolved while the State never moved returns 2. That is the point: the
    33-minute wasted cell and the two silent strategy-disables were both a green result over a
    machine that was doing nothing.
    """
    mutations = [bool(args.enable), bool(args.disable), bool(args.add)]
    if sum(mutations) > 1:
        print(json.dumps({"command": "strategy", "status": "error", "ok": False,
                          "message": "pick one of --enable / --disable / --add"}, indent=2))
        return 1
    if args.enable:
        action, name, type_name = "enable", args.enable, None
    elif args.disable:
        action, name, type_name = "disable", args.disable, None
    elif args.add:
        action, name, type_name = "add", None, args.add
    else:
        action, name, type_name = "list", args.require_enabled, None

    try:
        params = ntstrategy.parse_params(args.param)
    except ValueError as e:
        print(json.dumps({"command": "strategy", "status": "error", "ok": False, "message": str(e)}, indent=2))
        return 1

    try:
        payload = ntstrategy.run_strategy(action, args.chart, name, type_name, params,
                                          args.confirm, args.timeout, args.hold_ms, args.mechanism,
                                          args.index, args.force)
    except TimeoutError as e:
        print(json.dumps({"command": "strategy", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1

    state = ntstrategy.parse_strategy_response(payload)
    out = {"command": "strategy"}
    out.update(payload)
    out["summary"] = state.describe()
    print(json.dumps(out, indent=2))
    if not state.ok:
        return 1
    if action in ("enable", "disable", "add"):
        # Gate on `succeeded`, not `changed`: "already disabled" is a success that moved nothing.
        return 0 if state.succeeded else 2
    if args.require_enabled:
        return 0 if state.enabled else 2
    return 0


def _dialog(args) -> int:
    """Exit codes: 0 fine, 1 the AddOn did not answer or refused, 2 a verdict worth acting on.

    A dismiss that POSTED but left the dialog standing returns 2, not 0. The whole point of the
    command is that a click which changed nothing must not read as success.
    """
    if args.close or (args.hwnd and not args.button and not args.dismiss):
        action = "close"
    elif args.dismiss or (args.hwnd and args.button):
        action = "dismiss"
    else:
        action = "list"
    try:
        payload = ntdialog.run_dialog(action, args.close or args.dismiss, args.button, args.hwnd,
                                      args.wait_ms, args.timeout,
                                      "all" if args.scope_all else "modal")
    except TimeoutError as e:
        print(json.dumps({"command": "dialog", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    state = ntdialog.parse_dialog_response(payload)
    out = {"command": "dialog"}
    out.update(payload)
    out["summary"] = state.describe()
    print(json.dumps(out, indent=2))
    if not state.ok:
        return 1
    if action in ("dismiss", "close"):
        return 0 if state.dismissed else 2
    return 2 if (args.fail_on_modal and state.modals) else 0


def _workspace(strategy: str | None, timeout: float) -> int:
    try:
        payload = ntworkspace.run_workspace(timeout=timeout)
    except TimeoutError as e:
        print(json.dumps({"command": "workspace", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    state = ntworkspace.parse_workspace_response(payload)
    out = {"command": "workspace"}
    out.update(payload)
    rc = 0 if payload.get("status") == "ok" else 1
    if strategy:
        ok, why = state.strategy_running(strategy)
        out["strategyQuery"] = strategy
        out["strategyRunning"] = ok
        out["strategyReason"] = why
        if rc == 0 and not ok:
            rc = 2
    print(json.dumps(out, indent=2))
    return rc


def _layout(out_path, apply_path, name, dry_run, timeout) -> int:
    """Capture where NT's windows sit, or put them back.

    Exit codes carry the verdict so a bake script can gate on it: 0 clean, 1 the AddOn did not
    answer, 3 applied but something did not land. A partial apply must never read as success.
    """
    try:
        payload = ntlayout.run_layout(timeout=timeout)
    except TimeoutError as e:
        print(json.dumps({"command": "layout", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    state = ntlayout.parse_layout_response(payload)
    if not state.ok:
        print(json.dumps({"command": "layout", **payload}, indent=2))
        return 1

    if not apply_path:
        doc = ntlayout.capture(state, name or "layout")
        if out_path:
            ntlayout.write_layout_file(out_path, doc)
        print(json.dumps({"command": "layout", "action": "capture",
                          "written": str(out_path) if out_path else None,
                          "monitors": len(doc["monitors"]), "windows": len(doc["windows"]),
                          "layout": None if out_path else doc}, indent=2))
        return 0

    doc = ntlayout.read_layout_file(apply_path)
    plan, problems = ntlayout.plan_apply(doc, state)
    blocking = [p for p in problems if p.get("severity") != "note"]
    if dry_run:
        print(json.dumps({"command": "layout", "action": "plan", "dryRun": True,
                          "plan": plan, "problems": problems}, indent=2))
        return 0 if not blocking else 3
    if not plan:
        print(json.dumps({"command": "layout", "action": "apply", "placed": 0,
                          "problems": problems,
                          "hint": "nothing matched — run `layout --out cur.json` on this box and "
                                  "diff titleKey/classKey against the layout you are applying"}, indent=2))
        return 3

    try:
        after = ntlayout.run_layout(place=ntlayout.format_place(plan), timeout=timeout)
    except TimeoutError as e:
        print(json.dumps({"command": "layout", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    st2 = ntlayout.parse_layout_response(after)
    print(json.dumps({"command": "layout", "action": "apply",
                      "requested": len(plan), "placed": st2.placed,
                      "failed": st2.failed, "problems": problems}, indent=2))
    return 0 if (st2.placed == len(plan) and not blocking and not st2.failed) else 3


def _screenshot(title: str | None, hwnd: int | None, out: str | None, timeout: float) -> int:
    try:
        payload = ntscreenshot.run_screenshot(title, hwnd, out, timeout=timeout)
    except TimeoutError as e:
        print(json.dumps({"command": "screenshot", "status": "timeout", "ok": False, "message": str(e)}, indent=2))
        return 1
    result = {"command": "screenshot"}
    result.update(payload)
    if payload.get("status") == "ok":
        # A valid PNG is not evidence that anything was captured — a session-0 grab produces a
        # perfectly well-formed black frame. Flag the suspicion; the answer is to look at the image.
        result["looksBlank"] = ntscreenshot.looks_blank(payload.get("path", ""))
    print(json.dumps(result, indent=2))
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
    # It takes a PATH. --strategy was a misleading name (it reads as a class name); kept as an alias.
    # REQUIRED, but either spelling satisfies it. Making both plain optionals meant `deploy --kind
    # strategy` reached _deploy with strategy=None and died on a TypeError from os.path.exists,
    # trading argparse's clean "one of the arguments is required" for a stack trace.
    g_dep = p_dep.add_mutually_exclusive_group(required=True)
    g_dep.add_argument("--from", dest="strategy", help="source .cs file to stage into bin\\Custom")
    g_dep.add_argument("--strategy", dest="strategy",
                       help="deprecated alias for --from (it is a PATH, not a class name)")
    p_dep.add_argument("--kind", default="strategy")
    p_com = sub.add_parser("compile")
    # NT's compiler always builds the WHOLE tree (same as F5), so there is nothing
    # for --type to scope; the AddOn never reads it. Kept accepted for back-compat.
    p_com.add_argument(
        "--type",
        default="",
        help="accepted for back-compat and ignored — NT compiles the whole tree",
    )
    # A real tree can take well over 30s to compile; the old default turned a healthy
    # compile into a spurious "timeout".
    p_com.add_argument("--timeout", type=float, default=120.0)
    p_rel = sub.add_parser("reload", help="compile AND load it (what F5 does) — DISRUPTIVE")
    p_rel.add_argument("--timeout", type=float, default=240.0)
    p_win = sub.add_parser("windows", help="inventory NT's top-level windows")
    p_win.add_argument("--timeout", type=float, default=30.0)
    p_win.add_argument("--offscreen", action="store_true", help="only windows that look unreachable")
    p_reg = sub.add_parser("regions", help="find/strip DUPLICATED NinjaScript generated regions")
    p_reg.add_argument("--root", default="", help="NT8 user dir (default: auto)")
    p_reg.add_argument("--strip", action="store_true", help="remove them (NT regenerates one)")
    p_reg.add_argument("--all", action="store_true", dest="all_files",
                       help="strip every region, not just duplicated ones")
    p_rst = sub.add_parser("restart", help="restart NinjaTrader (required after reload before a bake)")
    p_rst.add_argument("--task", default=ntrestart.DEFAULT_TASK,
                       help="Scheduled Task to run instead of launching the exe. Create it with "
                            "/IT so it reaches the interactive session (required for UI automation "
                            "from an SSH/session-0 shell).")
    p_rst.add_argument("--exe", default=ntrestart.DEFAULT_EXE,
                       help="NinjaTrader executable to launch. No default: on a box that starts NT "
                            "through a credential-supplying wrapper, the bare exe stops at the "
                            "Welcome screen and a running-check still reads healthy.")
    p_rst.add_argument("--wait", type=float, default=120.0, help="seconds to wait for NT to come back")
    p_rst.add_argument("--stop-timeout", dest="stop_timeout", type=float, default=60.0,
                       help="seconds to wait for NT to stop before giving up (it will NOT launch a "
                            "second instance if the stop fails)")
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
    p_ord = sub.add_parser("order")
    p_ord.add_argument("--action", default="list",
                       help="api | list | status | place | cancel | change")
    p_ord.add_argument("--name", default="", help="account, e.g. Sim101 (REQUIRED to mutate)")
    p_ord.add_argument("--instrument", default="", help="e.g. 'MNQ 06-26'")
    p_ord.add_argument("--all-states", action="store_true",
                       help="list: include terminal orders (default: working only)")
    p_ord.add_argument("--side", default="", help="place: Buy | Sell | BuyToCover | SellShort")
    p_ord.add_argument("--type", default="", help="place: Market | Limit | StopMarket | StopLimit | MIT")
    p_ord.add_argument("--tif", default="", help="place: Day (default) | Gtc | Ioc | Opg | Gtd")
    # ⛔ These default to None, NOT 0. `change` treats an absent field as "leave alone", and a
    # default of 0 turns every omitted field into an explicit zero — which the AddOn correctly
    # rejected as BADQTY, and which for a price would have been a silent reprice to 0. Found by
    # driving `change --limit-price` on a live resting order, not by reading the code.
    p_ord.add_argument("--quantity", type=int, default=None, help="place/change: contracts")
    p_ord.add_argument("--limit-price", type=float, default=None)
    p_ord.add_argument("--stop-price", type=float, default=None)
    p_ord.add_argument("--order-id", default="", help="status/cancel/change: the OrderId")
    p_ord.add_argument("--order-name", default="", help="place: order label (default 'bridge')")
    p_ord.add_argument("--oco", default="", help="place: OCO group id")
    p_ord.add_argument("--settle", type=float, default=5.0,
                       help="seconds to settle-poll the outcome (0-60)")
    p_ord.add_argument("--all", action="store_true", help="cancel: every working order on the account")
    p_ord.add_argument("--confirm", action="store_true",
                       help="REQUIRED for place/cancel/change. SIM accounts only — a non-simulation "
                            "account is refused, and this build has no escalation flag.")
    p_ord.add_argument("--timeout", type=float, default=20.0)
    p_flat = sub.add_parser("flatten")
    p_flat.add_argument("--name", required=True, help="account to flatten, e.g. Sim101 (REQUIRED)")
    p_flat.add_argument("--instrument", default="", help="limit to one instrument, e.g. 'MNQ 06-26' (empty = all)")
    p_flat.add_argument("--timeout", type=float, default=20.0)
    p_watch = sub.add_parser("watch")
    p_watch.add_argument("--name", action="append", required=True, help="account(s) to watch; repeatable")
    p_watch.add_argument("--grace", type=float, default=20.0, help="seconds a position may stay naked before kill")
    p_watch.add_argument("--interval", type=float, default=5.0, help="seconds between scans")
    p_watch.add_argument("--once", action="store_true", help="single scan (no loop) — for testing")
    p_conns = sub.add_parser("connections",
                             help="list connections and their live status, or raise/drop one")
    p_conns.add_argument("--connect", metavar="NAME",
                         help="raise a configured connection (needs --confirm — it can arm an "
                              "order-capable surface)")
    p_conns.add_argument("--disconnect", metavar="NAME",
                         help="drop a connection (no --confirm: the safe direction stays easy)")
    p_conns.add_argument("--confirm", action="store_true", help="required for --connect")
    p_conns.add_argument("--wait-ms", dest="wait_ms", type=int, default=30000,
                         help="how long the status must be given to settle before a verdict")
    p_conns.add_argument("--timeout", type=float, default=15.0)
    p_pb = sub.add_parser("playback", help="replay transport state: clock, MOVING?, speed, .nrd coverage")
    p_pb.add_argument("--instrument", help="limit .nrd coverage to one instrument (default: all)")
    p_pb.add_argument("--require-ready", action="store_true",
                      help="exit 2 unless the transport is connected, loaded and PARKED (for bake scripts)")
    p_pb.add_argument("--timeout", type=float, default=30.0)
    p_nts = sub.add_parser("ntstatus", help="is NT running the code on disk? exit 2 if the DLL is newer")
    p_nts.add_argument("--timeout", type=float, default=15.0)
    p_sc = sub.add_parser("selfcheck",
                          help="audit THIS CLI: version, module hash, editable target, dependency manifests")
    p_sc.add_argument("--requirements", action="append", default=[],
                      help="extra requirements.txt to verify against this interpreter; repeatable")
    p_sc.add_argument("--expect-hash", help="fail (exit 2) unless the module hash matches — fleet assertion")
    p_sc.add_argument("--expect-version", help="fail (exit 2) unless the installed version matches")
    p_sc.add_argument("--python", dest="python_exe",
                      help="audit ANOTHER interpreter on this box (a manifest is only satisfied "
                           "relative to one venv)")
    p_log = sub.add_parser("log", help="grep a log file INSIDE NT (server-side filter; NT holds them open)")
    p_log.add_argument("--file", required=True,
                       help="path to read; relative resolves under the NT8 user data dir "
                            "(e.g. 'trace\\trace.20260804.00003.txt')")
    p_log.add_argument("--grep", help="regex; a pattern that will not compile is an ERROR, not a no-match")
    p_log.add_argument("-i", "--ignore-case", action="store_true")
    p_log.add_argument("--since-min", type=float, default=0,
                       help="only lines newer than N minutes (log stamps are LOCAL time)")
    p_log.add_argument("--tail", type=int, default=200, help="keep at most the last N matches")
    p_log.add_argument("--max-bytes", type=int, default=8 * 1024 * 1024,
                       help="read at most this many bytes from the END of the file")
    p_log.add_argument("--text", action="store_true", help="print bare lines instead of JSON")
    p_log.add_argument("--fail-on-match", action="store_true",
                       help="exit 2 if anything matched — for a pre-flight fault gate")
    p_log.add_argument("--timeout", type=float, default=60.0)
    p_ch = sub.add_parser("chart", help="charts: list, attach/remove an indicator, close")
    p_ch.add_argument("--chart", help="substring of the chart title")
    p_ch.add_argument("--add-indicator", metavar="CLASSNAME", help="attach an indicator by CLASS name")
    p_ch.add_argument("--remove-indicator", metavar="NAME", help="remove an indicator by name or type")
    p_ch.add_argument("--apply-template", dest="apply_template", metavar="XMLPATH",
                      help="load a chart template's <Indicators> via NT's OWN loader (path is on the "
                           "NT machine). Unlike --add-indicator this produces RUNNING indicators.")
    p_ch.add_argument("--range-type", dest="range_type", metavar="KIND",
                      help="data window: Days / Bars / Months / CustomRange (BarsProperties."
                           "RangeType). ⚠ From/To are IGNORED unless RangeType is CustomRange")
    p_ch.add_argument("--days-back", dest="days_back", type=int, help="data window: days to load")
    p_ch.add_argument("--bars-back", dest="bars_back", type=int, help="data window: bars to load")
    p_ch.add_argument("--months-back", dest="months_back", type=int, help="data window: months to load")
    p_ch.add_argument("--from", dest="from_date", metavar="YYYY-MM-DD",
                      help="data window start (parsed culture-invariantly)")
    p_ch.add_argument("--to", dest="to_date", metavar="YYYY-MM-DD", help="data window end")
    p_ch.add_argument("--param", action="append", default=[], metavar="KEY=VALUE",
                      help="parameter for --add-indicator; repeatable")
    p_ch.add_argument("--close", action="store_true", help="close the matched chart (needs --confirm)")
    p_ch.add_argument("--api", action="store_true",
                      help="read-only: what this build's ChartControl exposes for indicators/reload")
    p_ch.add_argument("--confirm", action="store_true")
    p_ch.add_argument("--timeout", type=float, default=60.0)
    p_pbc = sub.add_parser("playbackctl",
                           help="MOVE the replay transport: seek (settle-polled), speed, range")
    p_pbc.add_argument("--api", action="store_true",
                       help="read-only: which transport members this NT build actually exposes")
    p_pbc.add_argument("--seek", metavar="DATETIME",
                       help="seek the replay clock, then WAIT for it to settle before judging")
    p_pbc.add_argument("--speed", type=int, help="set playback speed multiplier")
    p_pbc.add_argument("--set-start", metavar="DATETIME", help="connection range start (needs --confirm)")
    p_pbc.add_argument("--set-end", metavar="DATETIME", help="connection range end (needs --confirm)")
    p_pbc.add_argument("--settle-ms", type=int, default=1500,
                       help="how long the clock must hold still before a verdict is rendered")
    p_pbc.add_argument("--seek-timeout-ms", type=int, default=60000)
    p_pbc.add_argument("--confirm", action="store_true", help="required for --set-start/--set-end")
    p_pbc.add_argument("--force", action="store_true",
                       help="allow a seek OUTSIDE the loaded replay range (it will succeed and find "
                            "no data)")
    p_pbc.add_argument("--timeout", type=float, help="client wait (defaults to outlast the poll window)")
    p_st = sub.add_parser("strategy", help="chart strategies: list, enable, disable, add")
    p_st.add_argument("--chart", help="substring of the chart title (narrows every action)")
    p_st.add_argument("--enable", metavar="NAME", help="arm a strategy (ORDER SOURCE — needs --confirm)")
    p_st.add_argument("--disable", metavar="NAME", help="stop a strategy")
    p_st.add_argument("--add", metavar="CLASSNAME",
                      help="attach a strategy by CLASS name (ORDER SOURCE — needs --confirm)")
    p_st.add_argument("--param", action="append", default=[], metavar="KEY=VALUE",
                      help="parameter for --add; repeatable")
    p_st.add_argument("--confirm", action="store_true", help="required for --enable and --add")
    p_st.add_argument("--hold-ms", type=int, default=15000,
                      help="how long the new state must HOLD before it is believed. A hold shorter "
                           "than a revert is a slower way to print the same false green.")
    p_st.add_argument("--mechanism", default="auto",
                      choices=["auto", "flag", "flag-refresh", "enable-call", "setstate", "remove"],
                      help="which lever to pull. auto climbs the ladder and keeps the first rung "
                           "that HOLDS; remove takes the strategy off the chart entirely")
    p_st.add_argument("--force", action="store_true",
                      help="attempt --add anyway; it does not work on this build (see --help notes)")
    p_st.add_argument("--index", type=int,
                      help="pick one of several same-type matches (0-based); ambiguity is otherwise refused")
    p_st.add_argument("--require-enabled", metavar="NAME",
                      help="exit 2 unless a strategy matching NAME is running (bake pre-flight)")
    p_st.add_argument("--timeout", type=float, default=60.0)
    p_dlg = sub.add_parser("dialog", help="list modal dialogs, or answer one explicitly")
    p_dlg.add_argument("--dismiss", metavar="TITLE",
                       help="substring of the dialog caption to answer (requires --button)")
    p_dlg.add_argument("--hwnd", type=int, help="exact HWND instead of a title match")
    p_dlg.add_argument("--button", help="EXACT-ish button text to click; there is no default answer")
    p_dlg.add_argument("--wait-ms", type=int, default=5000,
                       help="how long to wait for the dialog to actually disappear")
    p_dlg.add_argument("--close", metavar="TITLE",
                       help="send WM_CLOSE (what the title-bar X does) — for a window whose buttons "
                            "cannot be resolved; outcome is still verified")
    p_dlg.add_argument("--all", action="store_true", dest="scope_all",
                       help="include NON-modal windows (Error boxes, notifications) — a non-modal "
                            "prompt still blocks an unattended box")
    p_dlg.add_argument("--fail-on-modal", action="store_true",
                       help="exit 2 if any MODAL dialog is up — for a bake pre-flight")
    p_dlg.add_argument("--timeout", type=float, default=30.0)
    p_shot = sub.add_parser("screenshot", help="capture a window (or the whole screen) as PNG")
    p_shot.add_argument("--title", help="substring of the window caption (case-insensitive)")
    p_shot.add_argument("--hwnd", type=int, help="exact HWND from `windows`")
    p_shot.add_argument("--out", help="PNG path ON THE NODE (default: NT8Bridge\\result\\shot_<id>.png)")
    p_shot.add_argument("--timeout", type=float, default=30.0)
    p_ws = sub.add_parser("workspace", help="charts, indicators, strategies + enabled state")
    p_ws.add_argument("--strategy", help="assert a strategy matching this fragment is enabled (exit 2 if not)")
    p_ws.add_argument("--timeout", type=float, default=30.0)
    p_lay = sub.add_parser("layout", help="capture/apply where NT's windows sit (fractions, not pixels)")
    p_lay.add_argument("--out", help="write the captured layout here (default: print it)")
    p_lay.add_argument("--apply", dest="apply_path", help="layout file to apply")
    p_lay.add_argument("--name", help="name recorded inside a captured layout")
    # A layout moves real windows and there is no undo, so the plan is inspectable before it runs.
    p_lay.add_argument("--dry-run", action="store_true", help="resolve the plan and print it, move nothing")
    p_lay.add_argument("--timeout", type=float, default=30.0)
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
    p_hg.add_argument("--replay-dir", dest="replay_dir", default="", help="DEPRECATED and refused unless it names <NT8>/db/replay — it never chose where downloads land; use --check-dir")
    p_hg.add_argument("--check-dir", dest="check_dir", default="", help="inventory to check for already-present .nrd (default: <NT8>/db/replay). Downloads ALWAYS land in <NT8>/db/replay")
    p_hg.add_argument("--timeout", type=float, default=600.0, help="AddOn wait per date, seconds (heavy MNQ days run 300-460s)")

    # ── ORCHESTRATION VERBS (2026-08-11) ─────────────────────────────────────────────────
    # Python-only on purpose: no AddOn change means no compile, no reload, no restart, and
    # nothing to break on a box that is mid-bake. Orchestration is where the operator was
    # actually being spent, so orchestration is what gets automated.
    p_fl = sub.add_parser("fleet", help="run ONE bridge verb on every sentry and tabulate")
    p_fl.add_argument("--verb", required=True, help="the verb to run, quoted, e.g. \"ntstatus\"")
    p_fl.add_argument("--hosts", default="", help="comma-separated subset (default: all non-retired)")
    p_fl.add_argument("--fleet", dest="fleet_path", default="", help="path to fleet.conf")
    p_fl.add_argument("--timeout", type=float, default=120.0)

    p_cp = sub.add_parser("corpus", help="how much corpus each box holds, and HOW FRESH")
    p_cp.add_argument("--hosts", default="")
    p_cp.add_argument("--fleet", dest="fleet_path", default="")

    p_vr = sub.add_parser("versions", help="bridge DRIFT: is each box running the AddOn we think it is?")
    p_vr.add_argument("--hosts", default="")
    p_vr.add_argument("--fleet", dest="fleet_path", default="")
    p_vr.add_argument("--source", default="", help="source NT8BridgeServer.cs to compare against")

    p_rr = sub.add_parser("runrange", help="drive a replay across a range UNATTENDED, stepping over gaps")
    p_rr.add_argument("--host", default="", help="sentry name (omit = this machine)")
    p_rr.add_argument("--from", dest="from_", required=True, help="start, e.g. 2025-12-26T00:05:00")
    p_rr.add_argument("--to", required=True, help="end, e.g. 2026-01-02T22:00:00")
    p_rr.add_argument("--speed", type=int, default=50, help="replay multiplier (⚠ high speed may drop ticks — Replay Test C is OPEN)")
    p_rr.add_argument("--stall-sec", dest="stall_sec", type=float, default=90.0, help="clock still this long = a GAP, not an error")
    p_rr.add_argument("--step-minutes", dest="step_minutes", type=int, default=90, help="how far to jump when stepping over a gap")
    p_rr.add_argument("--max-hours", dest="max_hours", type=float, default=12.0)
    p_rr.add_argument("--expect-recorder", dest="expect_recorders", action="append", default=[],
                      metavar="NAME",
                      help="declare a corpus recorder this bake MEANS to run (repeatable). Anything "
                           "else attached REFUSES the run — a workspace restore brings recorders "
                           "back at Realtime and they would write replayed rows into the corpus")

    if not argv:
        print(CAPABILITY)
        return 0

    args = parser.parse_args(argv)
    if args.command in ("fleet", "corpus", "versions", "runrange"):
        from nt8bridge import fleet as _fleet
        hosts = [h for h in getattr(args, "hosts", "").split(",") if h] or None
        fp = getattr(args, "fleet_path", "") or None
        if args.command == "fleet":
            out = _fleet.fleet_exec(args.verb, hosts, fp, args.timeout)
        elif args.command == "corpus":
            out = _fleet.corpus_status(hosts, fp)
        elif args.command == "versions":
            src = args.source or os.path.join(os.path.dirname(os.path.dirname(
                os.path.abspath(__file__))), "addon", "NT8BridgeServer.cs")
            out = _fleet.versions(src, hosts, fp)
        else:
            out = _fleet.run_range(args.host or None, args.from_, args.to, args.speed,
                                   args.stall_sec, 20.0, args.max_hours, args.step_minutes,
                                   args.expect_recorders)
        print(json.dumps(out, indent=2, default=str))
        # ⛔ A refused bake must not exit 0. A wrapper script that only checks the exit code would
        # otherwise treat "I did nothing because a recorder was live" as "the bake ran".
        if isinstance(out, dict) and out.get("refused"):
            return 2
        return 0

    if args.command == "doctor":
        return _doctor()
    if args.command == "precheck":
        return _precheck(args.strategy)
    if args.command == "deploy":
        return _deploy(args.strategy, args.kind)
    if args.command == "compile":
        return _compile(args.type, args.timeout)
    if args.command == "reload":
        return _reload(args.timeout)
    if args.command == "windows":
        return _windows(args.timeout, args.offscreen)
    if args.command == "regions":
        return _regions(args.root, args.strip, args.all_files)
    if args.command == "restart":
        return _restart(args.task, args.exe, args.wait, args.stop_timeout)
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
    if args.command == "order":
        return _order(args)
    if args.command == "flatten":
        return _flatten(args.name, args.instrument, args.timeout)
    if args.command == "watch":
        return _watch(args.name, args.grace, args.interval, args.once)
    if args.command == "connections":
        if args.connect and args.disconnect:
            print(json.dumps({"command": "connections", "status": "error", "errors": [
                {"code": "BOTH", "message": "pass --connect or --disconnect, not both"}]}, indent=2))
            return 2
        if args.connect or args.disconnect:
            return _connections_mutate(args)
        return _connections(args.timeout)
    if args.command == "playback":
        return _playback(args.instrument, args.timeout, args.require_ready)
    if args.command == "ntstatus":
        return _ntstatus(args.timeout)
    if args.command == "selfcheck":
        return _selfcheck(args.requirements, args.expect_hash, args.expect_version, args.python_exe)
    if args.command == "log":
        return _log(args)
    if args.command == "dialog":
        return _dialog(args)
    if args.command == "strategy":
        return _strategy(args)
    if args.command == "playbackctl":
        return _playbackctl(args)
    if args.command == "chart":
        return _chart(args)
    if args.command == "screenshot":
        return _screenshot(args.title, args.hwnd, args.out, args.timeout)
    if args.command == "workspace":
        return _workspace(args.strategy, args.timeout)
    if args.command == "layout":
        return _layout(args.out, args.apply_path, args.name, args.dry_run, args.timeout)
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
