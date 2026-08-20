# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.6.0] - 2026-08-20

### Added

- **`strategies` — read, and change, the enabled state of the Control Center's strategies.**

  ```bash
  python -m nt8bridge strategies                        # what is enabled, and is it running?
  python -m nt8bridge strategies --enable 'My Strategy' # turn it back on
  python -m nt8bridge strategies --disable X --dry-run  # what would happen, clicking nothing
  ```

  An explicit `Connection.Disconnect()` disables every running strategy, and NinjaTrader restores
  none of them — not on reconnect, and **not on an app restart either**. So "are my strategies
  running on that machine?" needed a remote desktop session, and "no" needed a human clicking
  checkboxes.

  Distinct from `workspace`, which walks the **chart** windows: this reads the **Control Center**
  grid, the other population of strategies and the one a connection cycle turns off. Neither is a
  superset of the other.

  Two things the contract deliberately refuses to collapse:

  - **`enabled` is not proof a strategy is running.** `enabled` is the grid checkbox — it says the
    click landed. The evidence is the strategy's own `state` reaching `Realtime`, so every row
    returns both, and an acting call waits `--settle-ms` (default 3000) before re-reading.
    Anything clicked but not yet there comes back under `unverified`, telling the caller to
    **re-read, not re-click** — a strategy loading historical data is legitimately mid-transition,
    and a second click would toggle it back off. Where the two readings disagree, believe `state`.
  - **`--disable` does not flatten.** It stops the strategy *managing* what it holds; the position
    and any working orders remain. So it refuses when the strategy's **account** has exposure on
    its instrument (the strategy-level view can read flat while the account still carries the
    fill), and `--force` is the deliberate override.

  Exit `0` did what was asked · `1` could not reach the grid · `2` partially — refused, not in the
  grid, or enabled without reaching `Realtime` in time. Enabling something already running is `0`,
  not `2`: "make sure X is on" is the normal shape of an unattended caller and failing its no-op
  would make every retry look like a failure. Skips carry a machine-readable `code`
  (`alreadyEnabled`, `alreadyDisabled`, `notInGrid`, `exposure`, `bothLists`, `clickFailed`) so
  callers branch on that rather than on the prose.

  Implementation notes, because none of this is a published API: `ControlCenter.Instance` is the
  only way in (NinjaTrader's real windows are not in `Application.Current.Windows`); the Control
  Center owns a UI thread separate from `Globals.MainThreadDispatcher`, and a read from the wrong
  one throws in a way reflection turns into a convincing `null`; and the Strategies tab is
  virtualized while inactive, so the grid must be materialized by cycling tabs and the user's tab
  restored afterwards. Then the surprising part: setting `StrategiesGridEntry.IsEnabled = true`
  does **not** start the strategy, and neither does executing the grid's routed commands — only
  the checkbox's `Checked` routed event does, so the AddOn clicks the real checkbox via
  `ButtonBase.OnClick()`. In-process; no synthetic mouse or keystrokes.

### Fixed

- **README listed 24 commands; 32 had shipped.** The eight added by the merged pull requests
  (`reload`, `regions`, `restart`, `windows`, `ntstatus`, `workspace`, `playback`, `screenshot`)
  reached the CLI but never the grouped command list — five of them appeared nowhere in the README
  at all. The list now covers all 33 (32 plus `strategies`), with a new **Read-only state** group
  for the ones that just answer "what is this NinjaTrader doing right now".

## [1.5.1] - 2026-08-19

### Fixed

- **`configure` wrote to a discarded strategy template, and reported success for every key it
  lost.** Writing `Strategy` makes NinjaTrader install a *fresh* `StrategyTemplate` instance, but
  the write targets were resolved once before the key loop — so every key processed after
  `Strategy` landed on the previous, now-detached object. `PropertyInfo.SetValue` on a detached
  object does not throw, so each one returned `"status": "set"` with its value echoed back while
  the Strategy Analyzer kept its old data series. **The result was not a failed call but a
  successful-looking backtest on settings the caller believed it had replaced.** Reported by
  [@Quantrosoft](https://github.com/Quantrosoft) in #6, with a repro whose run kept `Tick 1` +
  Tick Replay over a far wider range than requested, grew to 232 GB resident and stopped
  responding.

  Two changes close it, and only together:

  1. **Targets are re-resolved per key**, which rescues the keys written after the swap.
  2. **Template-swapping keys are applied first**, which rescues the keys written before it.
     `ParseParams` returns a `Dictionary`, and dictionary iteration order is not part of its
     contract — "`Strategy` processed last" was always reachable, and in that ordering
     re-resolution alone saves nothing.

  Probed on a live SA tab: `From`, `To`, `BarsPeriod` and `IsTickReplay` are writable **only** on
  the template, so those four are exactly what a mis-ordered call drops.
  `InstrumentOrInstrumentList` also exists on `TabStrategyProperties`, which is earlier in the
  target list and survives the swap — which is why the instrument looked applied while the bar type
  and date range silently did not.

### Added

- **`applied[].nowReads`** — every `set` now reports what the tab holds afterwards, read back off a
  **freshly resolved** chain rather than off the reference just written to. A read-back from the
  object you wrote to would have passed cleanly in the bug above: a detached template echoes its own
  value quite happily, and only the live chain shows the tab still holding the old one. It is a
  **value, never a verdict** — setters legitimately transform (`BarsPeriod "77077:120:1"` reads back
  as `"Wave 120"`), and an equality check there would manufacture false failures. Additive, so an
  existing `status == "set"` check is unaffected.

Verified live on 8.1.6.3 with `Strategy` deliberately listed **last** in the params map: it is
applied first, and all six keys land, confirmed by an independent `probe` read-back afterwards.

## [1.5.0] - 2026-08-04

Four **read-only** commands that answer questions which previously required an RDP session. Each one
exists because of a specific failure that cost real time; none of them mutate NinjaTrader state.

Numbered 1.5.0 to stay clear of the in-flight 1.4.0 (`regions` + `restart`). This release does not
depend on it and can merge in either order — say the word and it renumbers.

### Added

- **`playback` — replay transport state.** Connection, replay clock, speed, and per-`.nrd` coverage
  via NinjaTrader's own `GetReplayMinMaxDates`. **The clock is sampled twice, a real gap apart**, and
  the delta is reported as `movingSec`: a single reading cannot distinguish a parked transport from a
  running one, and that distinction blocked a replay-equivalence gate for a day — the same seek landed
  on a parked clock and silently no-opped on a moving one. `--require-ready` turns the report into an
  assertion (exit 2) for bake scripts; reporting stays the default, because a box with no Playback
  configured is not a failure of this command.

  Coverage is read from the `.nrd` reader, **not the Playback slider** — the slider's bounds are the
  connection range you typed, not the indexed data, and reading it as proof that data was loaded cost
  hours on the same day.

- **`ntstatus` — is NinjaTrader running the code on disk?** Process start time vs the built assembly's
  timestamp, and it **exits 2 when the DLL is newer than the process**. This is the stale-DLL
  condition that once ran a 33-minute cell against code the operator believed had been replaced: the
  source on disk said one version, the running assembly was another, and every downstream conclusion
  was drawn from the wrong build. On a timeout it degrades rather than fails — a wedged or signed-out
  NinjaTrader is exactly when this matters, and the filesystem half of the answer is still available.

- **`workspace` — charts, indicators, strategies, and their `State`.** Toggling Playback silently
  disables chart strategies, and two runs were lost to a cell that replayed for half an hour with
  nothing armed. Marshals to **each chart window's own dispatcher** with a bounded wait, and reports
  `null` — never `[]` — when a member does not resolve: an unreadable chart and an empty chart are
  different claims and must not share a representation.

- **`screenshot` — capture a window (or the screen) as PNG.** A fleet of headless workers cannot be
  operated through a human describing a window; during one incident the Playback window and a panel
  were displaying two different clock values the whole time and nobody noticed, because nobody could
  look at both at once. Matches by HWND or case-insensitive substring of the title.

  ⚠ **It must run inside NinjaTrader.** SSH lands in session 0, which owns no desktop, and a session-0
  capture returns **black — which looks like an answer** rather than an error. GDI `PrintWindow` with
  `PW_RENDERFULLCONTENT`, encoded through WPF's `PngBitmapEncoder`, so no `System.Drawing` reference
  is needed and no dispatcher is required (NinjaTrader is multi-UI-threaded; Win32 is thread-agnostic).

### Notes for implementers

**`PlaybackAdapter` members are bound by reflection, not directly.** NinjaTrader compiles every `.cs`
under `bin\Custom` into one assembly, so a hard binding to an internal API turns any NinjaTrader
change into a whole-tree compile break — which takes down every unrelated tool in the same tree.

### Tests

- **+17 tests**, `204 passed, 6 skipped` (1.3.0 baseline: 187). Two of them exist because driving
  these against a live NinjaTrader within the hour found two defects in them:
  - `playback` reported **ready** for a transport reading `2099-12-01` with nothing loaded. It was
    stationary, so it passed the moving/not-moving test — a false green of exactly the kind this
    command exists to eliminate.
  - `workspace` matched strategies on `name` only. Tools that blank their own `Name` at `DataLoaded`
    (the on-chart label *is* the `Name` property) were therefore invisible, and it found nothing on a
    real chart. It now matches on name **or** type.

## [1.4.0] - 2026-08-04

Stacks on 1.3.0 (`reload` + `windows`). These two commands were split out of that PR for review, and
the review found a data-loss bug and a command that did not do what it said.

### Added

- **`regions` — find and strip DUPLICATED NinjaScript generated regions.** NinjaTrader appends a
  `#region NinjaScript generated code` block when it regenerates wrappers; if the file is then edited
  outside the NinjaScript Editor, NT appends *another* on its next pass. They accumulate silently
  (8 copies in one file observed), and because every `.cs` under `bin\Custom` compiles into one
  assembly, a single afflicted file takes the whole platform down with CS0111/CS0102 errors that name
  the symbol but never the cause. `regions` reports them; `regions --strip` removes them (NT
  regenerates exactly one on its next real build). **`compile` now warns when it sees any**, because
  this is entirely mechanical to detect and does not belong in a human's memory of a rule.

  **Detection is anchored to a real preprocessor directive** (`^[ \t]*#region …`), never a substring
  count. A count treats any *prose mention* of the marker as a region — and the files most likely to
  mention it are the ones whose header comment explains this very rule. A healthy file with one region
  plus one explanatory comment counts as 2, reads as "duplicated", and a strip then truncates from the
  **comment** to end of file. Reported against a real file where that removed 21% of it, the
  legitimate region included.

  **`--strip` writes `<path>.bak` before truncating anything.** A tool that removes the tail of
  someone else's source needs an undo even when its matcher is correct.

- **`restart` — restart the NinjaTrader process.** Some changes survive a reload: bars-type instances
  are *not* recreated by a reload or a chart reload, so they keep executing the pre-reload assembly
  and publishing into its static state while everything else reads the new one — a silent split that
  nothing in NinjaTrader surfaces. Prefers a Scheduled Task (a task created with `/IT` reaches the
  interactive session, the only way UI automation works from an SSH/session-0 shell) and falls back
  to an explicitly-supplied executable.

  **It stops NinjaTrader, confirms it stopped, and only then starts it.** The first draft launched
  without stopping and then asked "is NinjaTrader running?" — which the *original* process answered,
  so it reported success having restarted nothing and left two instances against one user directory.
  A failed stop now refuses to launch, and a process still running after a successful stop refuses too
  (a relaunch watchdog can win that race). `--stop-timeout` bounds the graceful close before it
  escalates; the escalation is not optional, because NinjaTrader's own save-workspace prompt can block
  a graceful close and nothing here can answer it.

  **There is no default executable**, for the same reason there is no default task name and with a
  worse failure: on a box that starts NinjaTrader through a credential-supplying wrapper, launching
  `bin\NinjaTrader.exe` directly produces a process that stops at the Welcome screen — up, unusable,
  and indistinguishable from healthy to a running-check. `restart` refuses when given neither
  `--task` nor `--exe`, and it refuses *before* stopping anything.

### Changed

- **`deploy --strategy` renamed to `deploy --from`** (`--strategy` kept as a deprecated alias). It
  takes a source **path** to stage into `bin\Custom`, not a class name; the old name read as a class
  name and produced a bare `FileNotFoundError: 'MyThing'`. Passing a non-existent path now returns a
  structured error that says what the flag wants and points at `reload` for making in-tree code live.
  The two spellings form a **required mutually-exclusive group**, so omitting both is argparse's clean
  error rather than a `TypeError` from inside the command.

### Tests

- **+23 tests** covering `regions.scan`/`strip_file` (including the comment-mentions-the-marker case,
  the `.bak`, and `_archive` exclusion), `restart` path-selection and the stop-before-start ordering
  (asserted against a fake that records call order, so "did it stop first" is a test rather than a
  reading), and `deploy`'s missing-argument path. **`210 passed, 6 skipped`** (1.3.0 baseline: 187).

## [1.3.0] - 2026-07-30

### Added

- **`reload` — compile AND load it.** `compile` invokes NinjaTrader's compiler with
  `checkCompileOnly=true`: it answers "does this build?" and emits nothing, so a brand-new type never
  appears in the pickers and edited code is not running. Every headless edit loop still ended with a
  human alt-tabbing to the NinjaScript Editor to press F5 — the one step the bridge exists to remove.
  `reload` runs the same compiler with `checkCompileOnly=false`, so NinjaTrader emits and swaps the
  NinjaScript assembly exactly as F5 does. Measured: **27s on a ~560-file tree, `assemblyReloaded: true`.**

  Deliberately a **separate command, not a flag on `compile`**: a reload restarts indicators, can
  interrupt a running strategy, and orphans bars-type instances (recreated only by an NT process
  restart). It must never happen as a side effect of validating code.

- **`windows` — inventory NinjaTrader's top-level windows.** Type/title/HWND, visible, minimized,
  maximized, and screen geometry. Useful for UI and AddOn work, for asserting platform state before
  driving automation, and for finding windows dragged or thrown off-screen. `--offscreen` filters to
  the ones that look unreachable.

### Changed

- **`assemblyReloaded` is now truthful.** It was hardcoded `false` in the AddOn's result builder, so a
  caller could never tell whether its code was live. It is now true only when a non-check build
  actually succeeded. This is also why `checkCompileOnly=false` looked like it did nothing when tested
  directly: the Roslyn diagnostics are genuinely identical either way, and the one field that would
  have shown the assembly swap was hardcoded.

### Notes for implementers

⚠ **NinjaTrader runs each window on its own dispatcher thread.** Measured via this release's own
`windows` command: **179 windows across 26 distinct UI threads**, with the visible ones spread over
six. Any cross-window code must use Win32 on the HWND — `w.Left`, `.IsVisible`, `.WindowState`,
`Mouse.LeftButton` and `Keyboard.Modifiers` all throw `InvalidOperationException` from a central loop.

**`new WindowInteropHelper(w).Handle` throws too** — it reads the Window's thread-affine `HwndSource`,
so you cannot even obtain the handle off-thread. The first draft of `windows` did exactly that and
returned a confident `status:"ok", count:0` that looked like NinjaTrader had no windows. `EnumWindows`
filtered by process id is the correct approach: thread-agnostic, no dispatcher marshalling (which can
deadlock against a busy window thread), and it also catches windows absent from `Globals.AllWindows`.

## [1.2.0] - 2026-07-22

### Changed

- `compile --type` is now **optional**. NinjaTrader's compiler always builds the whole tree (the
  AddOn never read `typeName`), so requiring the flag forced every caller — especially scripted and
  agent-driven ones — to invent a meaningless argument, and a bare `compile` exited 2. Still
  accepted, and documented as ignored.
- `compile --timeout` default raised **30s → 120s**. A real tree compiles slower than 30s, so the
  old default reported `{"status":"timeout"}` for a perfectly healthy compile that NinjaTrader was
  still running.
- A `compile` timeout now carries a `hint` distinguishing "NinjaTrader is still compiling" from
  "the AddOn isn't loaded", instead of leaving the caller to guess which failure it hit.

### Documentation

- New README section **"What actually loads your code"**: `compile` validates only
  (`checkCompileOnly=true` ⇒ `assemblyReloaded` is always `false`), but writing a `.cs` into
  `bin\Custom` while NinjaTrader is running makes NinjaTrader recompile and **hot-reload the
  assembly on its own** — so `deploy` is not an inert file copy. Verified that `deploy`'s temp-write
  + `os.replace` rename triggers the reload too (DLL rebuilt within seconds on 8.1.6.x), matching the
  in-place overwrite measured on 8.1.7.2 (~19s). Documents the two consequences: the reload happens
  underneath whatever is running, and it does **not** close already-open AddOn windows — the old
  window keeps executing the old assembly while the new assembly's statics start empty, so an AddOn
  that auto-opens a window can stack a second instance on a live one. Neither statics nor `Type`
  identity can detect that; the interlock has to live in an artifact that crosses the reload
  boundary. Bars types are the exception (sticky instance — still need F5 + a chart reload).

## [1.1.2] - 2026-07-22

### Added

- **Corrupt-`.nrd` detection.** The offline `histdump` decoder now cross-checks each decode against
  the `.nrd` header's own per-slot volume-sum and price range. A corrupt file — byte damage that
  would otherwise decode to **silently-wrong** data — is flagged in a `corrupt[]` bucket and **no
  parquet is written** for that date (re-download it, e.g. `histget --force`), instead of emitting
  silently-wrong output. Validated clean across 14/14 known-good files (3.6 MB – 263 MB, all symbols
  and both tick sizes); truncated files are exempt (their prefix legitimately falls short).

## [1.1.1] - 2026-07-19

### Fixed / Added

- `histget` **never downloads the current or future day** — replay data is only partial until the
  session closes, so the effective latest date is yesterday. The cutoff is computed in **ET**
  (`America/New_York`, DST-correct) to match NT8's session dates regardless of the machine's own
  timezone; the result reports `today` and `skipped_current[]`.
- `histget --force` re-downloads and overwrites dates that already have a `.nrd` (alias of the
  existing `--no-skip-existing`).
- `tzdata` added to dependencies (Windows has no system tz database, needed to resolve ET).

## [1.1.0] - 2026-07-19

### Added

- **Offline `histdump` engine — now the default.** `histdump` decodes NinjaTrader `.nrd`
  MarketReplay files **directly, with no running NinjaTrader**, straight to per-day **L1 + L2
  UTC parquet** at `<out>/<SEASON>/<SYM>-<SEASON>_<L1|L2>/<date>.parquet`. Verified byte-exact
  against NT8's own `MarketReplay.DumpMarketDepth` across MNQ (0.25 tick) and MGC (0.10 tick),
  ~20 days / tens of millions of events, and ~3× faster than driving NT8.
  - **Clean salvage** of truncated `.nrd`: every valid record is written and the incomplete
    final record is dropped (the NT8 engine instead emits one garbage trailing row).
  - `--validate` — decode a `.nrd` and diff every field against a fresh NT8 dump (needs NT8).
  - `--nt8` — the previous `DumpMarketDepth` CSV engine, kept as a legacy fallback for damaged
    files (needs NinjaTrader). `--levels L1 L2` selects which record levels to write.

### Changed

- `histdump`'s default output is now L1/L2 UTC parquet (offline); the old full-depth CSV is
  produced by `--nt8`.
- `numpy` and `pyarrow` are now required dependencies (the default offline engine needs them).

## [1.0.0] - 2026-07-18

First full public release — the toolset grows from 13 to 24 commands. Verified against
NinjaTrader 8.1.6.x on Windows.

### Added

- **Backtesting**
  - `sweep` — backtest a full matrix: instrument × bar-type × param-set.
  - `configure` — write instrument / dates / bar-type / fill / params onto the Strategy
    Analyzer tab, so a headless sweep needs no manual clicking.
  - `probe` — dump the SA tab's writable property names (discover names before you
    `configure`).
  - `peek` — read the SA tab's latest result plus a read-back of the injected params,
    without firing a new Run.
- **Data export**
  - `histget` — download missing MarketReplay `.nrd` files for a date range (drives
    NinjaTrader's own `RequestMarketReplay`).
  - `histdump` — batch-export replay `.nrd` → per-day depth CSV, byte-identical to the
    in-NT8 `NRDToCSV` full-depth mode; optional `--parquet`, `--validate-only`, `--force`.
- **Live ops**
  - `performance` — account trade-performance report (PF / win% / expectancy / drawdown)
    from NinjaTrader's trade database.
  - `perfwindow` — read commissions **and fees** from an open Trade Performance window
    (matters for funded/prop accounts, where NinjaTrader does not persist per-fill
    commission locally).
  - `feedhealth` / `feedwatch` — detect a FROZEN-but-connected ("dark") feed via
    last-tick age; `feedwatch` is a detect-only alert loop.
  - `chartseries` — change a live chart's data series (instrument + bar type/period) from
    the CLI.
- `parquet` optional-dependency extra (`pip install -e ".[parquet]"`) for `histdump`.

### Changed

- README rewritten for newcomers: a 60-second quickstart, all 24 commands grouped by
  purpose, and a worked deploy → compile → configure → backtest → peek example.

### Safety

- `chartseries` fails **closed**: it refuses to switch a live chart that has an
  enabled/realtime strategy or an open position on it, and **blocks** rather than guessing
  when it cannot verify that state — pass `--force` to override.
- `perfwindow` flags an unverifiable fee read: if `areFeesCalculated` resolves but
  `TotalFeesAll` does not (a non-public member renamed on another NinjaTrader build), it
  sets `feesCalculated: false`, attaches a `feeReadNote`, and prints a warning rather than
  reporting a fabricated `0`. **Trust `totalFees` only when `feesCalculated` is `true`.**

## [0.1.0]

Initial (pre-release) command set — the core edit → compile → backtest loop plus
out-of-band account/connection recovery: `doctor`, `precheck`, `deploy`, `compile`,
`backtest`, `batch`, `account`, `flatten`, `watch`, `connections`, `reconnect`,
`connwatch`, `watchdog`.
