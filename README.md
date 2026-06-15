# NT8 Bridge

Drive **NinjaTrader 8** from the command line — deploy a NinjaScript strategy, compile it *inside* NinjaTrader, read NinjaTrader's own compile/load errors, run Strategy Analyzer backtests, and get structured JSON (plus PDF reports) back. Built so an AI agent (or any script) can run the full **edit → compile → fix → backtest** loop with no manual clicking.

Everything runs locally and in-process: a small Python CLI talks to a NinjaScript **AddOn** running inside NinjaTrader through plain JSON files. No UI automation, no network.

## Why

NinjaTrader's *offline* compilers can't see the errors that only surface when NinjaTrader itself loads and compiles your code (custom-indicator references, properties set from the wrong `State`, and so on). NT8 Bridge compiles **through NinjaTrader's own compiler** and hands you the real Roslyn diagnostics — so a fix-and-retry loop runs against ground truth. Then it drives the Strategy Analyzer for you and reads the performance back as data.

## How it works

```
┌──────────────────────────────┐   files     ┌──────────────────────────────┐
│  Python CLI (you / agent run  │ ──────────▶ │  NT8BridgeServer.cs (AddOn)   │
│  it from the shell)           │  trigger/   │  runs INSIDE NinjaTrader 8    │
│   - offline precheck          │             │   - compile via NT's compiler │
│   - deploy (.cs -> bin/Custom)│ ◀────────── │   - read Roslyn diagnostics    │
│   - read JSON, build PDF       │  result/    │   - run the Strategy Analyzer  │
└──────────────────────────────┘             └──────────────────────────────┘
```

The AddOn polls `…\Documents\NinjaTrader 8\NT8Bridge\trigger\` for request files and writes results to `…\result\`. Every command prints structured JSON to stdout.

## Requirements

- NinjaTrader 8 (developed/verified against 8.1.x), with historical data loaded
- Python 3.10+
- Windows (NinjaTrader is Windows-only)

## Install

```bash
git clone git@github.com:eman007/cli-nt-bridge.git
cd cli-nt-bridge
python -m venv .venv
.venv\Scripts\Activate.ps1                  # PowerShell: activate the venv for this shell
python -m pip install -e ".[dev,report]"
```

Every command below assumes the venv is **active** — you'll see `(.venv)` in your
prompt. If you don't activate it, a bare `python` runs your *global* interpreter,
which won't have this project's dependencies (you'll get
`ModuleNotFoundError: No module named 'jsonschema'`). If activation is inconvenient
or PowerShell blocks the activate script, just prefix every command with the venv's
Python instead: `.venv\Scripts\python -m nt8bridge …`

Then load the AddOn into NinjaTrader **once**:

1. `python -m nt8bridge deploy --strategy addon/NT8BridgeServer.cs --kind addon`
   (copies it to `…\NinjaTrader 8\bin\Custom\AddOns\`)
2. In NinjaTrader, open the NinjaScript Editor and **compile (F5)**. After this one compile the AddOn loads on every NinjaTrader start, and strategy compiles no longer need F5.

Verify the setup:

```bash
python -m nt8bridge doctor
```

## Commands

```
python -m nt8bridge                            # capability + command list
python -m nt8bridge doctor                     # check preconditions
python -m nt8bridge precheck --strategy X.cs   # offline compile (no NinjaTrader; see note)
python -m nt8bridge deploy   --strategy X.cs   # atomic copy into bin/Custom (--kind strategy|indicator|addon)
python -m nt8bridge compile  --type MyStrategy # compile INSIDE NinjaTrader, return its real errors
python -m nt8bridge backtest --config c.json   # auto-run a Strategy Analyzer backtest (--pdf for a report)
python -m nt8bridge batch    --batch  b.json   # run N param-sets -> combined report (--pdf)
python -m nt8bridge account  --name SimAccount2 # read NinjaTrader live state (positions/orders/PnL/fills)
python -m nt8bridge flatten  --name SimAccount2 # force-close an account's positions + orders (kill switch)
python -m nt8bridge watch    --name SimAccount2 # auto-flatten NAKED (unprotected) positions (loop)
python -m nt8bridge connections                # read connection status (live / inadvertently dropped)
python -m nt8bridge reconnect --name X         # reconnect a dropped connection (on-demand override)
python -m nt8bridge connwatch --name X         # auto-reconnect INADVERTENT drops only (loop)
python -m nt8bridge watchdog                   # restart NinjaTrader if it hangs/crashes
```

### compile

Triggers a compile inside NinjaTrader via the AddOn and returns NinjaTrader's own Roslyn errors — including the ones an offline compiler can't see:

```json
{ "ok": false, "errors": [ { "file": "MyStrategy.cs", "line": 42, "code": "CS0103", "message": "…" } ] }
```

### backtest

Drives an **open, configured** Strategy Analyzer. Set the SA tab up once (strategy, instrument, dates, commission); the bridge injects `config.json`'s `params` onto the strategy, fires the Run button's command, waits for the run to finish, and reads the completed `SystemPerformance`:

```json
{ "status": "ok", "strategy": "MyStrategy",
  "metrics": { "totalTrades": 42, "netProfit": 1250.0, "profitFactor": 1.62, "maxDrawdown": -380.0 },
  "trades": [ { "pnl": 72.5, "entryTime": "2024-05-19T09:30:03" } ] }
```

```bash
python -m nt8bridge backtest --config config/config.json --timeout 300 --pdf report.pdf
```

`config.json` (see `config/config.json`):

| field | notes |
|---|---|
| `typeName` | the NinjaScript class name |
| `instrument`, `barType`, `from`, `to`, `capital`, `commission`, `slippageTicks` | reference fields |
| `params` | strategy inputs injected before each run (matched by property name) |

The SA tab supplies strategy/instrument/dates; `params` lets you parameterize each run without touching the UI.

### batch

Run many param-sets (same strategy) through the Strategy Analyzer and aggregate them into one report:

```bash
python -m nt8bridge batch --batch config/batch.json --timeout 600 --pdf batch_report.pdf
```

### PDF reports

`--pdf` renders a one-page report (needs the `report` extra / matplotlib): KPI tiles, a filled equity curve with the running peak, an underwater-drawdown panel, and a win/loss trade histogram. The batch report is a summary table plus a net-P&L-by-run bar chart.

### account

Reads NinjaTrader's **live** account state directly from the in-process AddOn — an independent, out-of-band channel from any status/position feed your strategy publishes. Use `--name` to filter to one account; omit it for all accounts.

```bash
python -m nt8bridge account --name SimAccount2
```

```json
{ "status": "ok", "ts": "2026-01-02T15:04:05Z",
  "accounts": [ { "name": "SimAccount2", "realizedPnl": 90.9, "unrealizedPnl": 0.0,
    "positions": [],
    "workingOrders": [],
    "recentExecutions": [ { "instrument": "MNQ 06-26", "marketPosition": "Long", "quantity": 1,
                            "price": 28678.5, "time": "2026-01-02T14:04:00Z", "commission": 0.65,
                            "orderName": "E_13c1bd63" } ] } ] }
```

It answers the questions a stalled status feed can't: **is a position actually open right now**, what are the live stop/target orders, and what were the real fills. Read-only — it never submits, cancels, or flattens. A timeout means NinjaTrader is down or the AddOn isn't loaded (itself a useful signal).

### Recovery — positions & connections

Out-of-band recovery that works independently of whatever feed your strategy uses, so it still functions when that feed stalls. Everything reads NinjaTrader's own truth through the AddOn.

**Positions**

- `flatten --name X` — force-closes account `X`'s open position(s) and cancels its working orders; a kill switch for a position a strategy lost track of. The account name is **required** (it refuses to flatten everything); add `--instrument "MNQ 06-26"` to limit it to one instrument.
- `watch --name X` — a loop that flattens **naked** positions (an open position with no working protective **stop**; a lone profit-target limit is not protection). Scoped to the `--name` allow-list, with a `--grace` period so it never kills a trade mid-bracket-placement.

**Connections**

- `connections` — lists every configured connection with its live status and whether it dropped **inadvertently**.
- `reconnect --name X` — reconnects a connection on demand (an unconditional override).
- `connwatch --name X` — a loop that auto-reconnects **only inadvertent drops** (`ConnectionLost` / error-disconnect). A connection you disconnect yourself (`Disconnected` / `UserAbort`) is classified *parked* and never auto-reconnected. Allow-list (`--name`, repeatable) + `--grace` + exponential backoff; it logs and gives up (surfacing the problem) if a connection won't come back — e.g. an expired login needing a manual refresh. Runs alongside NinjaTrader's own auto-reconnect, covering the tail it doesn't.

```json
{ "status": "ok", "connections": [
  { "name": "MyBroker", "status": "Connected", "connected": true, "inadvertentlyDropped": false } ] }
```

The intent classification comes from NinjaTrader's own `ConnectionStatusUpdate` event + `ErrorCode`, so "did the user disconnect this, or did it drop?" is NinjaTrader's answer, not a guess.

### watchdog

```bash
python -m nt8bridge watchdog --threshold 60 --interval 10
```

The AddOn writes a heartbeat from NinjaTrader's main UI thread each second. The watchdog restarts NinjaTrader if that heartbeat goes stale (UI hang) or the process disappears (crash). If your NinjaTrader isn't at the default `C:\Program Files\NinjaTrader 8\bin\NinjaTrader.exe`, pass `--exe`.

## precheck note

`precheck` is an optional fast offline gate. It needs an external NinjaScript offline-compiler PowerShell script — point at it with the `NT8BRIDGE_COMPILER` environment variable. Without it, `precheck` errors clearly (it will not pretend your code is clean). The in-NinjaTrader `compile` command does **not** need it and is the primary error-checking path.

## How it actually works (the interesting bits)

- **Compile:** the AddOn calls `NinjaTrader.Code.Compiler.Compile(...)` (a public static method in `NinjaTrader.Core.dll`) via reflection and reads the returned Roslyn `EmitResult.Diagnostics`. No UI scraping.
- **Backtest:** it locates the open Strategy Analyzer window via `NinjaTrader.Core.Globals.AllWindows`, reads its `StrategyAnalyzerViewModel`, injects params onto the configured `StrategyTemplate`, and **executes the Run `RoutedCommand`** — exactly what the Run button does, so NinjaTrader runs it correctly on a background thread. It then polls the tab's results for a fresh `SystemPerformance`.
- Do **not** call `StrategyRunner.RunStrategyAsync` directly: on the Strategy Analyzer's UI thread it deadlocks and crashes NinjaTrader. Fire the `RoutedCommand` instead.

These reach into NinjaTrader's non-public internals (located by decompiling with ILSpy), so they may need adjusting across NinjaTrader versions.

## Tests

```bash
.venv/Scripts/python -m pytest
```

The live offline-compile fixture tests skip automatically when no offline compiler is configured.

## Compatibility & disclaimer

Developed and verified against NinjaTrader 8.1.x on Windows. NT8 Bridge uses some of NinjaTrader's internal APIs discovered by decompilation; a NinjaTrader update could change them. This project is **not affiliated with or endorsed by NinjaTrader**. Use it on your own NinjaTrader installation at your own risk — backtests are not predictions, and automated order handling carries real financial risk.

## License

[MIT](LICENSE) — free to use, copy, modify, and distribute; provided "as is" with no
warranty and no liability.
