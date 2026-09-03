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

35 commands, grouped by what they do:

**Setup & diagnostics**
```
doctor        check preconditions (NT8 dir, AddOn compiled)
precheck      offline compile gate (optional; see note)
deploy        atomic copy a .cs into bin/Custom (--kind strategy|indicator|addon)
compile       compile INSIDE NinjaTrader, return its real Roslyn errors
reload        compile AND load it (what F5 does) — DISRUPTIVE, see below
regions       find/strip DUPLICATED NinjaScript generated regions
restart       restart NinjaTrader
windows       inventory NinjaTrader's top-level windows
probe         dump the SA tab's writable property names (discover names for `configure`)
peek          read the SA tab's latest result + param read-back, without a new Run
```

**Read-only state** — what is this NinjaTrader actually doing right now?
```
ntstatus      is NT running the sources on disk? (newest .cs vs the code NT executes; exit 2 if not)
workspace     charts + indicators + strategies on them, and their State
strategies    Control Center strategies: enabled AND running? --enable/--disable to change
playback      replay transport: clock, MOVING?, speed; .nrd coverage only on request (--coverage = every instrument, minutes; --instrument X = that one); --require-ready = exit 2 unless parked and loaded
screenshot    capture a window (or the screen) as PNG
```

**Backtesting**
```
backtest      run a configured Strategy Analyzer backtest (--pdf for a report)
batch         run N param-sets -> one combined report (--pdf)
sweep         backtest a matrix: instrument x bar-type x param-set
configure      write instrument/dates/bar-type/fill/params onto the SA tab (headless setup)
satemplate    put one of NinjaTrader's own strategy templates on the SA tab, by file
playbackrun   drive a Playback run end to end: connect, range, attach, play, archive
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
```

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
- `configure --config c.json` — writes each key in the config onto whichever of (tab, tab-strategy-properties, template) has a matching writable property; type-aware (Instrument, DateTime, BarsPeriod, enums). Returns a per-key `applied[]` status (`set`/`skip`/`error`), each `set` carrying **`nowReads`** — the value the *live* tab holds afterwards, re-read off a freshly resolved chain rather than off the object just written to. `Strategy` is always applied **first**, because writing it makes NT8 install a fresh strategy template and anything written to the old one is silently discarded (see the 1.5.1 note in the changelog).
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

### strategies — is it enabled, and is it actually running?

Reads, and changes, the enabled state of the strategies in the Control Center's **Strategies** tab.

```bash
python -m nt8bridge strategies                          # read: what is enabled, and is it running?
python -m nt8bridge strategies --strategy Breakout      # assert one is at state=Realtime (exit 2 if not)
python -m nt8bridge strategies --enable 'Morning Breakout'   # turn it back on
python -m nt8bridge strategies --disable X --dry-run    # what would happen, clicking nothing
```

Returns `{status, gridResolved, strategies:[{name, type, enabled, state, account, instrument}],
changed:[…], skipped:[{name, code, reason}], notes:[…]}`. Skip `code`s — branch on these, not on the
prose — are `alreadyEnabled`, `alreadyDisabled`, `notInGrid`, `exposure`, `bothLists`, `clickFailed`.

Exit **0** did what was asked · **1** could not reach the grid, or the AddOn errored · **2** partially:
refused by the exposure guard, not in the grid, or enabled without `state` reaching `Realtime` before
the settle expired.

**Why it exists.** An explicit `Connection.Disconnect()` disables every running strategy, and NinjaTrader
restores none of them — not on reconnect, and **not on an app restart either**. So "are my strategies
running on that machine?" needed a remote desktop session, and "no" needed a human clicking checkboxes.

**`strategies` vs `workspace`.** `workspace` walks the **chart** windows, so it sees chart-attached
strategies. `strategies` reads the **Control Center** grid — the other population, and the one a
connection cycle turns off. Neither is a superset of the other.

> ⚠ **`enabled` is not proof a strategy is running.** `enabled` is the grid checkbox: it says the click
> landed. The evidence is the strategy's own `state` reaching `Realtime`, which is why every row carries
> both and why an acting call waits `--settle-ms` (default 3000) before re-reading. Where they disagree,
> believe `state`. Rows reported under `unverified` were clicked but had not reached `Realtime` yet —
> **re-read rather than clicking again**; a strategy loading historical data is legitimately
> mid-transition, and a second click would toggle it back off.

> ⚠ **`--disable` does not flatten.** It stops the strategy *managing* what it holds; the position and
> any working orders remain. So `--disable` refuses when the strategy's account has exposure on its
> instrument, and `--force` is the deliberate override. The guard checks the **account's** position
> rather than the strategy's own, because the strategy-level view can read flat while the account still
> carries the fill.

Enabling something already running exits **0**, not 2 — "make sure X is on" is the normal shape of an
unattended caller, and failing its no-op would make every retry look like a failure. The exception is
`alreadyEnabled` on a row whose `state` is *not* live: an enabled checkbox above a `Terminated` strategy
is the looks-healthy-but-isn't case, and the checkbox is the less trustworthy of the two readings.

### satemplate — a backtest by template file

```bash
python -m nt8bridge satemplate --template "C:\...\bin\Custom\Strategies\MyBot\Tests\VariantA.xml"
python -m nt8bridge backtest                 # --config is now optional
```

`--template` is a full path to one of NinjaTrader's own strategy template `.xml` files, or a
bare name looked up in the strategy's template folder. The template is **assigned** to the
Strategy Analyzer tab (the complete parameter set plus instrument and window, exactly what the
GUI would run); when it belongs to a different strategy than the tab shows, that strategy is
selected first, and the instrument travels to the tab because NinjaTrader does not carry it over
on its own. The response's `applied` is a reference comparison of the assigned template, and the
run window is reported.

### playback — the replay transport's state

```bash
python -m nt8bridge playback                            # clock, MOVING?, speed - no store scan
python -m nt8bridge playback --instrument "MNQ 09-26"   # plus .nrd coverage of that instrument
python -m nt8bridge playback --coverage --timeout 600   # plus coverage of every instrument (minutes)
python -m nt8bridge playback --require-ready            # exit 2 unless connected, loaded and parked
```

The `.nrd` coverage scan is opt-in because the wide scan holds the AddOn's poller for minutes;
the response says `coverageScanned: true|false`, and an unscanned store is reported as
`coverage not scanned`, never as "nothing to replay". `--require-ready` asserts "connected, loaded
and parked" for bake scripts and therefore implies the scan (of `--instrument` when given).

### playbackrun — one Playback measurement, end to end

Drives NinjaTrader's **Playback** connection through a whole run and archives the result:
every connection off, clean start, connect, source, dates, range, speed, attach the strategy,
play to the data end, restore the baseline. Every value it writes is read back.

```bash
python -m nt8bridge playbackrun --strategy EmptyStrategy --instrument 'NQ ##-##' \
    --source marketreplay --tick-replay false --bars-type Minute --bars-value 1 \
    --from 2026-08-10 --to 2026-08-10
```

`--source` is `marketreplay` (recorded .nrd data) or `historical` (the historical store);
`--tick-replay` turns Tick Replay on or off; `--bars-type` is `Tick`, `Minute` or `Wave` and
`--bars-value` is its period. Exit **0** only when the run reached the data end **and** the
teardown restored the baseline; **2** when it did not (the JSON says which), **1** on an error.

`--from` and `--to` are calendar days written `YYYY-MM-DD`, the end inclusive and not before
the start. Anything else — `2026-13-07`, `2026-7-7`, `07/07/2026`, an end before its start —
is refused by the argument parser (usage line on stderr, exit 2, the accepted form and the
offending value in the message) before a single request is written. Measured 2026-09-01: seven
runs carried `--to 2026-13-07` all the way into NinjaTrader, and each lasted 45–58 s of wall
clock before it died with `FormatException: String was not recognized as a valid DateTime.`

Before anything else the run asks the bridge one cheap question (stage 0, the preflight). A
NinjaTrader that is busy answers nothing for minutes - a Playback connect measured 388-453 s, a
cold start 735 s - so silence is waited out, up to 1800 s, with a console line at most every
30 s and the wait recorded as `preflightSeconds` in the result. One silence is refused at once:
no `NinjaTrader.exe` process at all.

**Accounts — which one the strategy trades on.** A Playback connection carries one or more
simulation accounts; their names come from NinjaTrader, and an unknown one is refused with the
list of what exists. A run uses one of them:

```bash
# on the account NinjaTrader itself calls the playback account
python -m nt8bridge playbackrun --strategy MyBot ...

# on a named account
python -m nt8bridge playbackrun --strategy MyBot --account Playback2 ...
```

- **`--account <name>`** — the account the strategy trades on. Omit it and the run asks
  NinjaTrader for its own playback account name (`Account.PlaybackAccountName`) rather than
  assuming one. A name that does not exist **stops the run** and lists what does exist; it is
  never silently swapped for another, because a strategy trading on an account nobody watches
  produces a run that looks successful.
- **`--template <file>`** — attach the strategy from one of NinjaTrader's own template `.xml`
  files (the complete parameter set); without it the strategy runs with its defaults.
- **`--name <archive>`** — name of the run directory under `NT8Bridgeuns` (default
  `PB_<strategy>`; a suffix `__N` keeps repeated runs apart).
- **`--stage-wait <s>`** — seconds one ordinary stage may take; the connect and Reset stages get
  2.5× that. Defaults to the driver's 10 / 25; raise it on a box where NinjaTrader is slow. Two
  stages keep their own floors on top: the connect (`CONNECT_WAIT`, min. 600 s) and the preflight
  (`PREFLIGHT_WAIT`, min. 1800 s, see above).
- **`--max-wait <rounds>`** — the play loop's budget once the clock has reached the range end,
  in rounds of 30 s (default 40).
- **`--nt8-dir <path>`** — the NinjaTrader data directory this run talks to (the mailbox lives
  under it); give each run its own to keep several NinjaTrader installations apart.

> ⚠ **One bot per pass — and running several is not worth it.** The driver accepted a list of
> bots until the idea was measured: two bots on one connection took **1163.8 s** for a week of
> Market Replay against roughly **606 s** for one. They SHARE the replay walk rather than
> parallelising it. The whole two-variant pass came to 1214.6 s against 1311.7 s run one after
> the other — 7.4 %, and that saving is the second connect it avoided, not the bots. The clock,
> the window, `--source` and `--tick-replay` are process-wide in NinjaTrader anyway, so a second
> bot could only ever vary its own bookkeeping.

`--account` also exists on the driver itself
(`python nt8bridge/playback_run.py --help`), which additionally offers `--step` for a
stop-after-every-sub-step walkthrough and `--archive` for where the run is written.

**The census — an optional counter file the strategy may write.**

At the end of a run the driver looks for `botlog_*/DEBUG_callbacks.json` inside the archive
and, if it finds one, prints what it holds. **Nothing in this repository writes that file** —
it is a convention a strategy can opt into, not a requirement, and a strategy that writes
none is a perfectly good run.

It is a **measurement, never a control**: the exit code does not depend on the file existing.
Two things are read by name when they are present, and both say so when they are not:

| key | used for |
|---|---|
| `IsTickReplay`, `Instrument` | compared against what the run asked for — a mismatch is reported and sets exit 2, because a run whose settings differ from the request is not a measurement of that request |
| `MarketData_LastDataTime` | the timestamp of the last market-data event, to tell "no data at all" from "data stopped early". Absent, the cut-short check prints that it cannot run rather than passing quietly |

Every other key in the file is printed as it comes, so a strategy can add counters without
this driver having to know their names.

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

## Run modes and the stores they read

The bridge runs a strategy over recorded data in three ways: the Strategy Analyzer (`backtest`)
and Playback from one of two sources (`playbackrun --source historical|marketreplay`). The two
Playback sources do **not** read the same store. What backs the table is the AddOn's own code,
not a log line: the coverage pre-flight in `addon/NT8BridgeServerPlayback.cs` scans exactly the
store the run will read, and its two scanners name the folders.

| Playback source | Store | The AddOn's scanner |
|---|---|---|
| **Historical** | `db\tick` (`.ncd`, one file per hour and data type) | `TickCoverage` lists `db\tick\<instrument>\*.ncd` |
| **Market Replay** | `db\replay` (`.nrd`, one file per instrument and day) | `ReplayCoverage` lists `db\replay\<instrument>\*.nrd` — the store `histget` fills and `histdump` decodes |

Only Market Replay reads the `.nrd` recordings. The Strategy Analyzer is driven as its tab was
configured (`backtest`) and has no such pre-flight: the bridge scans no store for it, and this
section claims none.

`playbackrun --source historical|marketreplay` chooses between the two. What decides the
source is the adapter static `PlaybackAdapter.IsSourceHistoricalData`: the bridge writes it for the
connect, re-asserts it after the connect and verifies it at the `source` stage, and every write is
read back. The two radio buttons on the Playback panel are display only: the `source` stage reads
them and reports what the panel shows, and a radio write NinjaTrader refuses is reported as a
display defect, never as a failed run, because the source was already settled by the static.

The coverage pre-flight after the connect asks the store the run will read: a Historical run
checks `db\tick` for the requested days, a Market Replay run checks `db\replay`. Measured
2026-08-30: a Historical run whose instrument had NCD files for the day but no `.nrd` at all was
refused by a scan of `db\replay`, and the fallback then judged the request against the panel
range of the previous run.

Switching the panel to Historical makes NinjaTrader raise a modal notice that Level II market
depth is not available in this mode. The bridge ticks "Don't show this message again" and confirms
that one notice, identified by its wording, and touches no other dialog: any other modal is left
standing and reported as the finding.

One more kind of box is confirmed during a run: NinjaTrader's order-rejection notices for the
playback account - `Stop price can't be changed above/below the market`, `... stop orders can't be
placed above/below the market`, `Order ... can't be submitted: The OCO ID ... cannot be reused` -
raised when the market crosses a strategy's stop between the strategy's own check and NinjaTrader's
validation (measured 2026-09-02: seven in one Historical run, every one handled by the strategy
itself). They are informational but modal, and an unattended run collects one per rejection. The
`strategystate` stage the play loop runs every sample confirms exactly those boxes - type name
`MessageBox`, text carrying NinjaTrader's trailer `affected Order:` - and counts them in its `order
notices` step; the driver prints each confirmation and result.json carries `orderNoticesDismissed`.
Any other modal still stands and is still the finding.

A playback run that is killed instead of torn down leaves the transport running. The next run then
refuses with `the transport was already running before step 8 - the strategy has lost the ticks up
to <time>`. The cure is the stage pair every run starts with: `alloff` (every connection off, from
the live list) and `restore` (strategy rows removed, dialogs closed, transport parked). NinjaTrader
stays up.

## How it actually works (the interesting bits)

- **Compile:** the AddOn calls `NinjaTrader.Code.Compiler.Compile(...)` (a public static method in `NinjaTrader.Core.dll`) via reflection and reads the returned Roslyn `EmitResult.Diagnostics`. No UI scraping.
- **Backtest:** it locates the open Strategy Analyzer window via `NinjaTrader.Core.Globals.AllWindows`, reads its `StrategyAnalyzerViewModel`, injects params onto the configured `StrategyTemplate`, and **executes the Run `RoutedCommand`** — exactly what the Run button does, so NinjaTrader runs it correctly on a background thread. Do **not** call `StrategyRunner.RunStrategyAsync` directly: on the SA UI thread it deadlocks and crashes NinjaTrader.
- **Data export:** `histdump` decodes the `.nrd` binary **offline** (a 44-slot header + a variable-length event stream) straight to L1/L2 parquet — verified byte-exact against NT8's `MarketReplay.DumpMarketDepth`, which `--nt8` still drives for the legacy CSV path.
- **Live reads:** `account`, `performance`, `feedhealth` use public NT8 APIs; `perfwindow` and `chartseries` read non-public view-model / chart internals via reflection.
- **Strategy enablement:** `strategies` drives the Control Center's own grid, and three things have to be true at once or it silently returns nothing. NinjaTrader's real windows are **not** in `Application.Current.Windows` (only custom AddOn windows are), so the static `ControlCenter.Instance` is the only way in. The Control Center owns a UI thread **separate** from `Globals.MainThreadDispatcher`, and a WPF read from the wrong one throws "calling thread cannot access this object", which reflection re-wraps and a silent `catch` turns into a convincing `null` — so every read is marshalled onto `((DispatcherObject)cc).Dispatcher`, on a *bounded* `Invoke` so a saturated UI thread cannot wedge the poller. And the Strategies tab is **virtualized while inactive**, so the grid isn't in the visual tree until its tab is selected; the command cycles tabs to materialize it and restores yours afterwards. Then the part that is genuinely surprising: setting `StrategiesGridEntry.IsEnabled = true` **does not start the strategy** — the read-back says `True` and nothing runs. Neither does executing the grid's `EnableStrategyCommand` (it acts on the grid *selection*, which is unset, so `CanExecute` is false) nor its per-row sibling, which *does* execute and *does* flip the bool while the strategy stays stopped. What starts it is the checkbox's `Checked` **routed event**, which executing a command never raises — so the AddOn clicks the real checkbox via `ButtonBase.OnClick()`. In-process; no synthetic mouse or keystrokes.

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
