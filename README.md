# NT8 Bridge

Drive **NinjaTrader 8** from the command line — deploy a NinjaScript strategy, compile it *inside* NinjaTrader, read NinjaTrader's own compile/load errors, run Strategy Analyzer backtests, export historical data, and watch live accounts/feeds — all returning structured JSON (plus PDF reports). Built so an AI agent (or any script) can run the full **edit → compile → fix → backtest** loop, and keep a live account safe, with no manual clicking.

Everything runs locally and in-process: a small Python CLI talks to a NinjaScript **AddOn** running inside NinjaTrader through plain JSON files. No UI automation, no network.

## Why

NinjaTrader's *offline* compilers can't see the errors that only surface when NinjaTrader itself loads and compiles your code (custom-indicator references, properties set from the wrong `State`, and so on). NT8 Bridge compiles **through NinjaTrader's own compiler** and hands you the real Roslyn diagnostics — so a fix-and-retry loop runs against ground truth. Then it drives the Strategy Analyzer for you and reads the performance back as data, exports the replay data your backtests need, and gives you an out-of-band read of live account/feed state that keeps working when your strategy's own status feed stalls.

## How it works

```
┌──────────────────────────────┐   files     ┌──────────────────────────────┐
│  Python CLI (you / agent run  │ ──────────▶ │  NT8BridgeServer.cs (AddOn)   │
│  it from the shell)           │  trigger/   │  runs INSIDE NinjaTrader 8    │
│   - offline precheck          │             │   - compile via NT's compiler │
│   - deploy (.cs -> bin/Custom)│ ◀────────── │   - read Roslyn diagnostics    │
│   - read JSON, build PDF       │  result/    │   - run the Strategy Analyzer  │
│   - export data, watch live    │             │   - read live account/feed     │
└──────────────────────────────┘             └──────────────────────────────┘
```

The AddOn polls `…\Documents\NinjaTrader 8\NT8Bridge\trigger\` for request files and writes results to `…\result\`. Every command prints structured JSON to stdout.

## Requirements

- NinjaTrader 8 (developed/verified against 8.1.6.x), with historical data loaded
- Python 3.10+
- Windows (NinjaTrader is Windows-only)

## Install

```bash
git clone git@github.com:eman007/cli-nt-bridge.git
cd cli-nt-bridge
python -m venv .venv
.venv\Scripts\Activate.ps1                  # PowerShell: activate the venv for this shell
python -m pip install -e ".[dev,report]"    # numpy+pyarrow are base deps; report adds matplotlib for --pdf
```

Every command below assumes the venv is **active** — you'll see `(.venv)` in your prompt. If activation is inconvenient or PowerShell blocks the activate script, prefix every command with the venv's Python instead: `.venv\Scripts\python -m nt8bridge …`

Then load the AddOn into NinjaTrader **once**:

1. `python -m nt8bridge deploy --strategy addon/NT8BridgeServer.cs --kind addon`
   (copies it to `…\NinjaTrader 8\bin\Custom\AddOns\`)
2. In NinjaTrader, open the NinjaScript Editor and **compile (F5)**. After this one compile the AddOn loads on every NinjaTrader start, and strategy compiles no longer need F5.

Verify the setup:

```bash
python -m nt8bridge doctor
```

## 60-second quickstart

With the AddOn loaded (above) and NinjaTrader running:

```bash
python -m nt8bridge doctor                         # preconditions OK?
python -m nt8bridge deploy --strategy MyStrategy.cs # copy your strategy into bin/Custom
python -m nt8bridge compile                         # compile INSIDE NT8 -> real errors as JSON
# ...fix any errors, redeploy, recompile until clean...
python -m nt8bridge backtest --config config/config.json --pdf report.pdf
```

Set up a Strategy Analyzer tab once (strategy, instrument, dates, commission); `backtest` injects your `config.json`'s `params`, fires Run, and reads the result back. That's the whole loop.

## Commands

39 commands, grouped by what they do:

**Self-audit, state & UI** (see [Driving NinjaTrader headlessly](#driving-ninjatrader-headlessly))
```
selfcheck     is THIS CLI the fleet's CLI? version, module hash, dependency manifests
ntstatus      is NT running the code on disk? (catches a stale DLL)
playback      replay transport: clock, MOVING?, speed, per-.nrd coverage
playbackctl   MOVE the transport: seek (settle-polled, range-checked), speed, range
workspace     charts + indicators + strategies AND their enabled state
chart         charts: list, attach/remove an indicator, close, --api discovery
strategy      chart strategies: list, enable, disable/remove
log           grep a log file INSIDE NT (server-side filter; NT holds them open)
dialog        list/answer/close dialogs — modal AND non-modal
screenshot    capture a window (or the whole screen) as PNG
windows       inventory NT's top-level windows
layout        capture/apply where NT's windows sit (fractions, not pixels)
```

**Setup & diagnostics**
```
doctor        check preconditions (NT8 dir, AddOn compiled)
precheck      offline compile gate (optional; see note)
deploy        atomic copy a .cs into bin/Custom (--kind strategy|indicator|addon)
compile       compile INSIDE NinjaTrader, return its real Roslyn errors
probe         dump the SA tab's writable property names (discover names for `configure`)
peek          read the SA tab's latest result + param read-back, without a new Run
```

**Backtesting**
```
backtest      run a configured Strategy Analyzer backtest (--pdf for a report)
batch         run N param-sets -> one combined report (--pdf)
sweep         backtest a matrix: instrument x bar-type x param-set
configure      write instrument/dates/bar-type/fill/params onto the SA tab (headless setup)
```

**Data export**
```
histget       download missing MarketReplay .nrd files for a date range
histdump      offline .nrd -> L1/L2 UTC parquet (default, no NinjaTrader; --nt8 for legacy CSV)
```

**Live ops & recovery** (out-of-band; keeps working when a strategy's own feed stalls)
```
account       read NinjaTrader live state (positions/orders/PnL/fills)
flatten       force-close an account's positions + orders (kill switch)
watch         auto-flatten NAKED (unprotected) positions (loop)
connections   read connection status (live / inadvertently dropped)
reconnect     reconnect a dropped connection (on-demand override)
connwatch     auto-reconnect INADVERTENT drops only (loop)
feedhealth    detect a FROZEN-but-connected feed via last-tick age
feedwatch     loop-alert on a frozen feed (detect-only)
chartseries   change a LIVE chart's data series (instrument + bar type/period)
```

**Resilience**
```
watchdog      restart NinjaTrader if it hangs (stale heartbeat) or crashes
restart       graceful close + relaunch
regions       find and strip DUPLICATED NinjaScript generated regions (they break the whole compile)
reload        recompile and report whether the assembly actually reloaded
```

## Driving NinjaTrader headlessly

These commands read and change state that previously existed only inside a running GUI. Everything
below was **measured on live boxes**, not inferred — where a thing does not work, that is recorded
as plainly as where it does.

### What works, and what does not

| Operation | Works? | Member that actually does it |
|---|---|---|
| Read charts / indicators / strategies + state | ✅ | `ChartControl.Indicators` / `.Strategies` |
| Seek the replay clock | ✅ | writable static `PlaybackAdapter.NowEst` |
| Set replay speed / range | ✅ | `PlaybackSpeed`, `FromEst` / `ToEst` |
| Grep a log NT holds open | ✅ | `FileShare.ReadWrite \| Delete` |
| List / answer / close dialogs | ✅ | Win32 `BM_CLICK`, WPF `ButtonBase`, `WM_CLOSE` |
| **Start** a chart strategy | ✅ | `ChartControl.StrategyEnable(...)` (asynchronous) |
| **Stop** a chart strategy | ✅ | `RemoveStrategyForChartBars` **+ `Strategies.Remove`** |
| **Disable in place** (leave attached, stopped) | ❌ | 5 mechanisms tried; every one reverts |
| Attach a strategy programmatically | ❌ | instance never binds to a `ChartBars` — refused by default |
| Activate an added indicator | ❌ | attaches at `Configure`; `RefreshIndicators` does not finish it |

### The rules every mutating command follows

1. **Refuse an ambiguous match** — never resolve it by taking the first. `--index` makes the choice
   explicit when a chart legitimately holds two of the same type.
2. **Require confirmation before arming an order source.** `--enable` and `--add` need `--confirm`;
   `--disable` does not, because the safe direction must never be the harder one to reach.
3. **Verify the OUTCOME, never the call.** A reflection call that returns cleanly while changing
   nothing is the single failure this project keeps re-paying for.

### Six ways a verified check still lied

Each of these shipped looking correct and was caught by driving it. They are the reason the rules
above exist, and they generalise well beyond NinjaTrader.

- **A seek can succeed and land where there is no data.** Writing the clock validates nothing: a
  seek outside the loaded range returns `succeeded: true, offset 0` and is telling the truth. Now
  fails closed; `--force` overrides; every result carries its range.
- **A state observed once is not a state change.** `StrategyEnable` terminates the instance and the
  chart re-applies a new one. The target must *hold* — `--hold-ms`, watched throughout.
- **A stale object reference watches a corpse.** Holding the original strategy pointer read
  `Finalized` for 45 seconds while the chart's live strategy sat at `Realtime`. **Identity is the
  (type, chart) pair, never the pointer.**
- **`MethodInfo.Invoke` hides the real error.** `TargetInvocationException.Message` is the
  content-free *"Exception has been thrown by the target of an invocation."* — unwrap `InnerException`.
- **`GetMethod` binds public-only by default**, and NT's chart API is nearly all internal. Even the
  binder overload misses inherited non-public members; enumerate `GetMethods` instead.
- **A modal-only dialog scan gave a clean bill of health** to a machine with an `Error` box and a
  rollover prompt on screen. Non-modal still blocks an unattended box — use `dialog --all`.

### ⛔ Two things deliberately not done

- **`ChartControl.ApplyStrategy` is a trap.** It is the member the chart uses itself, so it looks
  like the principled attach path — but invoked externally it blocks the chart's UI thread past 30 s
  and raises .NET assertion dialogs **whose default button is `Abort = Quit NinjaTrader`**. Left
  documented in the AddOn source so nobody re-derives it.
- **`chart` does not create chart windows.** That means building a WPF window on another UI thread
  of a platform hosting live order routing, and the value is not there: `layout` already places
  windows across machines and a workspace already carries the charts.

### Threading, briefly

NinjaTrader is **multi-UI-threaded** — every window owns its own dispatcher. Touching a WPF member
from the bridge's poller thread throws on every window, every time, which returns an empty list that
looks like a real answer. Win32 is thread-agnostic and is preferred wherever it can do the job;
otherwise marshal to the owning dispatcher with a **bounded** wait and report a timeout as a fact.

### compile

Triggers a compile inside NinjaTrader via the AddOn and returns NinjaTrader's own Roslyn errors — including the ones an offline compiler can't see:

```json
{ "ok": false, "errors": [ { "file": "MyStrategy.cs", "line": 42, "code": "CS0103", "message": "…" } ] }
```

`--type` is accepted but ignored — NinjaTrader's compiler always builds the **whole tree**, exactly as F5 does, so there is nothing to scope. `compile` on its own is enough.

**`compile` validates; it does not load.** The AddOn calls NinjaTrader's compiler with `checkCompileOnly=true`, so `assemblyReloaded` is always `false` — your code is *proved correct*, but the running NinjaTrader is still executing the previously loaded assembly. See below for what does load it.

### What actually loads your code

Writing a `.cs` into `bin\Custom` **while NinjaTrader is running** makes NinjaTrader recompile and hot-reload the assembly by itself — no F5, nobody at the GUI. Measured on NinjaTrader 8.1.7.2: `NinjaTrader.Custom.dll` was rebuilt ~19 s after the file landed, and the new code was live. `deploy` writes a temp file and renames it over the destination; that rename path was separately verified to trigger the same reload (`Custom.dll` rebuilt within seconds on 8.1.6.x), so `deploy` is not an inert copy either.

That is useful — it is the missing "load" step, and it works headlessly — but it has two teeth worth knowing before you script it:

- **It reloads the assembly underneath whatever is running.** A code sync during a live session restarts strategies and indicators mid-flight. Deploy deliberately, not casually.
- **The reload does *not* close already-open AddOn windows.** A `NTWindow` you opened survives, still executing the *old* assembly's code, while the new assembly's statics start empty. So an AddOn that opens a window on load can stack a second instance on top of a live one, and neither statics nor `Type` identity can detect it (both reset across the reload). If your AddOn auto-opens anything, interlock it on an artifact that crosses the reload boundary — a file, or a timestamped line in your own log — and test the interlock by deploying while the window is open.

A **bars type** is the exception: its instance is sticky on a chart, so new bars-type code needs an Editor F5 *and* a chart reload.

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

### batch & sweep

`batch` runs many param-sets (same strategy) through the Strategy Analyzer and aggregates them into one report:

```bash
python -m nt8bridge batch --batch config/batch.json --timeout 600 --pdf batch_report.pdf
```

`sweep` runs a full matrix — every combination of instrument × bar-type × param-set — interleaving `configure` + `backtest` per cell:

```bash
python -m nt8bridge sweep --config config/config.json --instruments "MNQ 09-26" --bars "Minute:1" --params-file sets.json --pdf sweep.pdf
```

### configure, probe & peek

Automate the SA tab so a sweep needs no manual clicking:

- `probe` — dumps the SA tab / `TabStrategyProperties` / strategy-template **writable property names** (NT8's members are partly obfuscated, so discover before you write).
- `configure --config c.json` — writes each key in the config onto whichever of (tab, tab-strategy-properties, template) has a matching writable property; type-aware (Instrument, DateTime, BarsPeriod, enums). Returns a per-key `applied[]` status (`set`/`skip`/`error`).
- `peek` — reads the SA tab's latest completed result **plus a read-back of the injected params**, without firing a new Run — useful to capture a result a watcher missed, and to confirm param injection actually took.

### histget & histdump — replay data export

- `histget --instrument 'MNQ 09-26' --from 20260706 --to 20260709` — downloads the missing MarketReplay `.nrd` files for a date range (drives NT8's own `RequestMarketReplay` per date; skips weekends and dates already present).
- `histdump --instrument 'MNQ*' --out ./out/PARQUET` — **offline by default**: decodes the replay `.nrd` binary directly to per-day **L1 + L2 UTC parquet** (`<out>/<SEASON>/<SYM>-<SEASON>_<L1|L2>/<date>.parquet`), **no NinjaTrader**, ~3× faster than driving NT8, and verified byte-exact against NT8's own `DumpMarketDepth`. Truncated `.nrd` are salvaged cleanly (every valid row, no garbage tail). `--validate` re-checks a decode against a fresh NT8 dump; `--nt8` uses the legacy CSV engine (needs NinjaTrader); `--levels L1 L2` and `--force` as expected.

The CSV is always the source of truth; parquet is opt-in and never replaces it. A byte-equivalence gate catches any NT8-side format drift before a batch write.

### account

Reads NinjaTrader's **live** account state directly from the in-process AddOn — an independent, out-of-band channel from any status/position feed your strategy publishes. Use `--name` to filter to one account; omit it for all accounts.

```bash
python -m nt8bridge account --name Sim101
```

```json
{ "status": "ok", "ts": "2026-01-02T15:04:05Z",
  "accounts": [ { "name": "Sim101", "realizedPnl": 90.9, "unrealizedPnl": 0.0,
    "positions": [],
    "workingOrders": [],
    "recentExecutions": [ { "instrument": "MNQ 06-26", "marketPosition": "Long", "quantity": 1,
                            "price": 28678.5, "time": "2026-01-02T14:04:00Z", "commission": 0.65,
                            "orderName": "E_13c1bd63" } ] } ] }
```

It answers the questions a stalled status feed can't: **is a position actually open right now**, what are the live stop/target orders, and what were the real fills. Read-only — it never submits, cancels, or flattens.

### performance & perfwindow — trade cost reporting

- `performance --name Sim101 [--from D --to D] [--instrument 'MNQ 09-26'] [--pdf]` — an account trade-performance report (round-trip trades + profit factor / win% / expectancy / drawdown), sourced from NinjaTrader's trade database. Uses public NT8 APIs.
- `perfwindow --name Sim101 [--generate]` — reads commissions **and fees** straight from an open **Trade Performance** window's view model. This matters for live/funded (e.g. prop) accounts, where NinjaTrader does **not** persist per-fill commission locally — only the Trade Performance window has the broker's cash history. `--generate` opens and builds the window hands-off.

> **Trust the `feesCalculated` flag.** `perfwindow` reads a non-public view model. If it returns `feesCalculated: false`, treat `totalFees` as unverified — either the fees haven't been generated in the window yet, or you're on an NT8 build that renamed the underlying member. When `feesCalculated: true`, the totals are real. (A build mismatch is also surfaced as a `feeReadNote` and a `WARNING:` line.)

### Recovery — positions & connections

Out-of-band recovery that works independently of whatever feed your strategy uses, so it still functions when that feed stalls.

**Positions**

- `flatten --name X` — force-closes account `X`'s open position(s) and cancels its working orders; a kill switch for a position a strategy lost track of. The account name is **required** (it refuses to flatten everything); add `--instrument "MNQ 06-26"` to limit it to one instrument.
- `watch --name X` — a loop that flattens **naked** positions (an open position with no working protective **stop**; a lone profit-target limit is not protection). Scoped to the `--name` allow-list, with a `--grace` period so it never kills a trade mid-bracket-placement.

**Connections**

- `connections` — lists every configured connection with its live status and whether it dropped **inadvertently**.
- `reconnect --name X` — reconnects a connection on demand (an unconditional override).
- `connwatch --name X` — a loop that auto-reconnects **only inadvertent drops** (`ConnectionLost` / error-disconnect). A connection you disconnect yourself is classified *parked* and never auto-reconnected. Allow-list (`--name`, repeatable) + `--grace` + exponential backoff; it logs and gives up (surfacing the problem) if a connection won't come back.

**Feeds**

- `feedhealth --instrument 'MNQ 09-26'` — reports each watched instrument's **last-tick age**, so a feed NinjaTrader still calls `connected` but whose ticks have stopped (a "dark" feed) is detectable. A tick older than a threshold is a frozen feed.
- `feedwatch --instrument 'MNQ 09-26'` — a detect-only loop that alerts (durable jsonl + stdout) when a watched feed freezes past a grace period. Deliberately detect-only: thawing a frozen feed needs an operator to cycle NinjaTrader.

### chartseries

Change a **live** chart's data series (instrument and/or bar type + period) from the CLI:

```bash
python -m nt8bridge chartseries --instrument 'MES 09-26' --bars-type Minute --bars-value 5
```

> **Safety guard (fails closed).** Because it mutates a *live* chart, `chartseries` refuses to switch when the chart has an enabled/realtime strategy or an open position on the current chart-trader account — and if it **cannot verify** that state, it **blocks** rather than guessing. Pass `--force` to override.

### watchdog

```bash
python -m nt8bridge watchdog --threshold 60 --interval 10
```

The AddOn writes a heartbeat from NinjaTrader's main UI thread each second. The watchdog restarts NinjaTrader if that heartbeat goes stale (UI hang) or the process disappears (crash). If your NinjaTrader isn't at the default `C:\Program Files\NinjaTrader 8\bin\NinjaTrader.exe`, pass `--exe`.

### PDF reports

`--pdf` (on `backtest`/`batch`/`sweep`/`performance`) renders a one-page report (needs the `report` extra / matplotlib): KPI tiles, a filled equity curve with the running peak, an underwater-drawdown panel, and a win/loss trade histogram.

## A worked example — deploy → compile → configure → backtest → peek

```bash
python -m nt8bridge deploy --strategy MyStrategy.cs
python -m nt8bridge compile                               # fix errors until: {"ok": true, "errors": []}
python -m nt8bridge configure --config config/config.json  # set instrument/dates/bar-type on the SA tab
python -m nt8bridge backtest  --config config/config.json --pdf report.pdf
python -m nt8bridge peek                                   # re-read the result + confirm params injected
```

## precheck note

`precheck` is an optional fast offline gate. It needs an external NinjaScript offline-compiler PowerShell script — point at it with the `NT8BRIDGE_COMPILER` environment variable. Without it, `precheck` errors clearly (it will not pretend your code is clean), and its fixture tests skip. The in-NinjaTrader `compile` command does **not** need it and is the primary error-checking path.

## How it actually works (the interesting bits)

- **Compile:** the AddOn calls `NinjaTrader.Code.Compiler.Compile(...)` (a public static method in `NinjaTrader.Core.dll`) via reflection and reads the returned Roslyn `EmitResult.Diagnostics`. No UI scraping.
- **Backtest:** it locates the open Strategy Analyzer window via `NinjaTrader.Core.Globals.AllWindows`, reads its `StrategyAnalyzerViewModel`, injects params onto the configured `StrategyTemplate`, and **executes the Run `RoutedCommand`** — exactly what the Run button does, so NinjaTrader runs it correctly on a background thread. Do **not** call `StrategyRunner.RunStrategyAsync` directly: on the SA UI thread it deadlocks and crashes NinjaTrader.
- **Data export:** `histdump` decodes the `.nrd` binary **offline** (a 44-slot header + a variable-length event stream) straight to L1/L2 parquet — verified byte-exact against NT8's `MarketReplay.DumpMarketDepth`, which `--nt8` still drives for the legacy CSV path.
- **Live reads:** `account`, `performance`, `feedhealth` use public NT8 APIs; `perfwindow` and `chartseries` read non-public view-model / chart internals via reflection.

These reach into NinjaTrader's non-public internals, so they may need adjusting across NinjaTrader versions. The bridge is built to fail **loud** where it can (a moved type fails at compile/deploy time), and the two commands that read non-public members defensively — `perfwindow` (via its `feesCalculated` flag) and `chartseries` (fail-closed) — tell you when they can't trust what they read rather than returning a wrong answer.

## Tests

```bash
.venv/Scripts/python -m pytest
```

The live offline-compile fixture tests skip automatically when no offline compiler is configured.

## Compatibility & disclaimer

Developed and verified against NinjaTrader 8.1.6.x on Windows. NT8 Bridge uses some of NinjaTrader's internal APIs; a NinjaTrader update could change them. On a different build, `perfwindow` may report `feesCalculated: false` (trust that flag) and other internal reads may error out loudly rather than mislead. This project is **not affiliated with or endorsed by NinjaTrader**. Use it on your own NinjaTrader installation at your own risk — backtests are not predictions, and automated order handling carries real financial risk.

## License

[MIT](LICENSE) — free to use, copy, modify, and distribute; provided "as is" with no warranty and no liability.
