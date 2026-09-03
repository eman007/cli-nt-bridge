# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **`playbackrun` opens NinjaTrader's Playback window itself (stage `openwindow`).**

  Every stage that writes the panel — the source radios, the date editors, the speed,
  the transport — looks the window up with `FindWindowByTitle("Playback")`, and nothing
  in the run opened it. NinjaTrader does not persist that window in a workspace
  (measured 2026-08-29 after a restart: 52 windows loaded across all 7 open workspaces,
  none titled `Playback`), so after every NinjaTrader start the first run died at the
  source check. Measured 2026-09-03: three attempts inside one call, each `rc 2`,
  `3 source check: panel radios - Playback window not found`, each after a healthy
  connect (`SLOW CONNECT: 225 s … Connected.`) — 1238 s of wall clock for no result.

  The new stage runs before the source check and constructs NinjaTrader's own window
  type on the UI thread (`NinjaTrader.Gui.Data.PlaybackControlCenter`, parameterless
  constructor, `Show()`) — no clicks, no UI automation. It is idempotent: an existing
  window is reported (`already open`) and never duplicated. Measured against a running
  NinjaTrader 8.1.8.2 the same day: answered in 1.2 s with
  `open PlaybackControlCenter ok "shown: Playback"` and `window present ok "found"`.

- **`playbackrun` stage 2 (connect) gets its own budget — `CONNECT_WAIT`, min. 600 s.**

  The connect shared `SLOW_WAIT` (2.5 × `--stage-wait`; 49 s at a caller's
  `--stage-wait 20`). Measured 2026-08-28: of 23 playback connects that day, each took
  16–30 s — and one sat silently inside `PlaybackAdapter.Connect` for 423 s (no log
  lines, no disk reads) and then came back **Connected** and healthy. The 49 s bound
  turned that one slow connect into an ERROR, which ended a ~29 h batch of runs that
  stops on the first one. The stall is inside NinjaTrader's encrypted Core, so the
  caller's budget is the only correct place: `CONNECT_WAIT = max(600, SLOW_WAIT)`,
  and a connect slower than `SLOW_WAIT` is reported loudly
  (`SLOW CONNECT: … s`) instead of vanishing into a green verdict.

- **`playbackrun` refuses a malformed `--from`/`--to` before anything is sent to NinjaTrader.**

  Both dates go through one validator in the driver (`iso_date`, the argparse `type=` of the
  CLI's `playbackrun` subparser and of the driver's own parser, plus `date_order_error` for the
  pair): exactly `YYYY-MM-DD`, a real calendar day, and `--to` not before `--from`.
  `2026-13-07`, `2026-7-7`, `07/07/2026` or an end before its start are refused by the argument
  parser — usage line on stderr, exit 2, the accepted form and the offending value in the
  message — and no trigger file is written, no driver process started. A valid pair travels
  unchanged: the request JSON and the archive carry the string that was typed.

  Measured 2026-09-01: seven archived runs (05:16–08:23) carried `"to": "2026-13-07"`. Nothing
  checked the value on the way in — the CLI declared both dates as plain strings and the AddOn
  hands them to `DateTime.Parse` as they arrive — so every one of them ran stage 1a, lasted
  45–58 s of wall clock, and died with
  `2 connect: exception - FormatException: String was not recognized as a valid DateTime.`
  The shape is pinned by a pattern and not by `date.fromisoformat` alone: on Python 3.11 that
  also accepts `20260607` and `2026-W23-1`, two shapes nobody documented.

- **`playbackrun --source historical` — the Historical run mode no longer dies on the panel's
  source radios, the modal notice, or a coverage scan of the wrong store.** (AddOn
  `NT8BridgeServerPlayback.cs`; measured 2026-08-30/31 in NinjaTrader.)

  - **The source radios are DISPLAY ONLY.** Writing `rbHistoricalData.IsChecked` while the
    connection is up threw `TargetInvocationException <- FormatException: "String was not
    recognized as a valid DateTime"` — NinjaTrader re-parses the panel's date fields on that
    write, the same wording as the modal `(Panic)` it raises when Playback connects without a
    date range — and took the whole run with it (rc 2). The source itself is decided by
    `PlaybackAdapter.IsSourceHistoricalData` at stage `3 source`; the radios only keep the
    panel from contradicting the run. They are now written through a soft step: a refused
    write is reported (`source radios ... panel keeps the PREVIOUS source ...`) and is never a
    failure verdict.
  - **The "no Level II market depth in this mode" notice is dismissed by a watcher armed BEFORE
    the write.** Switching to Historical makes NinjaTrader raise that modal synchronously inside
    the `IsChecked` write, so a handler placed after the write never ran (measured 2026-08-30:
    the dialog came up unchanged). The watcher runs on its own thread, started before the
    write, confirms through the dispatcher (which keeps pumping inside a modal's own frame),
    identifies the dialog by its wording alone — text carrying both "Level II market depth" and
    "Market Replay"; no other dialog is touched — ticks "Don't show this message again" and
    invokes OK through the button's automation peer. The step `historical notice` reports the
    outcome.
  - **The coverage pre-flight asks the store this run reads.** Playback/Historical is served
    from `db\tick` (NCD); only Market Replay reads `db\replay` (NRD). A Historical run on
    `MNQ 09-26` was refused with `store scan unavailable, panel range used / requested
    28.08.2026..28.08.2026 inside it: False` while that day held 24 Ask / 24 Bid / 23 Last NCD
    files: the instrument has no `.nrd` at all (the recordings live under the continuous name
    `MNQ ##-##`), so the `db\replay` scan could only return nothing, and the fallback then
    judged the request against the PREVIOUS run's panel range (27.08.). New `TickCoverage`
    scans `db\tick\<instrument>\*.ncd` when the trigger's `source` is `historical` — the day is
    taken from the first 8 name characters, deliberately a pre-flight ("is there anything for
    that day"); the content is decided when the series loads and a run with missing data still
    fails loudly there. The step text says `NCD files` vs `files readable` accordingly.
  - **`SetElementValue` reports the cause, not only the symptom.** A `TargetInvocationException`
    carries only "the callee threw"; the step text now unwraps `InnerException` up to three
    levels (`... <- FormatException: ...`). Dropping it had left the log naming a symptom with
    no cause (measured 2026-08-30 on `rbHistoricalData.IsChecked`).

- **`playbackrun` stage 0 (preflight) waits for a busy NinjaTrader instead of failing —
  `PREFLIGHT_WAIT`, min. 1800 s — and refuses at once when there is no `NinjaTrader.exe` process.**

  The preflight asked ONE `transport` question with a 20 s TTL and, on silence, aborted with
  `preflight: the bridge did not answer` (rc 2, nothing started) and the advice to clear the
  trigger folder and restart NinjaTrader. Seven archived runs ended that way between
  2026-08-29 and 2026-09-02 (`NT8Bridge/runs/*/result.json`, every one with
  `wallClockSeconds` 88.3–88.4). For the last of them NinjaTrader's own log shows what it was
  doing: a Playback connect from 05:15:46 to 05:22:14 — 388 s — and the run's result.json is
  stamped 05:20:59, inside that window. The AddOn's poller handles one trigger at a time on one
  timer thread (`Poll` takes a gate; a tick that finds it taken reads nothing), and
  `Stage_Connect` runs on that thread — it calls `Connection.Connect` and waits for `Connected`
  in a sleep loop bounded by the request budget — so until the connect returns no request of
  any kind is read or answered. `heartbeat.json` cannot tell: `TryBeat` writes it through
  `Globals.MainThreadDispatcher`, not from the poller thread, so a fresh beat says the UI thread
  is alive, not that the poller is free. Holds of the same kind measured 423/437/453 s
  (2026-08-28/29); a cold-started NinjaTrader left its own log silent for 735 s (2026-09-01).

  The preflight is now a poll: it repeats the cheap probe — each with the short 20 s TTL the
  AddOn honours (`HandleTrigger` discards a trigger older than its `ttlSec` unexecuted), and
  each unanswered trigger taken back before the next one is written, so nothing piles up —
  until one is answered or `PREFLIGHT_WAIT = max(1800, SLOW_WAIT)` ends: 2.4× the longest
  measured silence (735 s) and 4.0–4.6× the measured connects (388–453 s). While it waits the
  console says so at most every 30 s (`NinjaTrader is busy - no answer for N s, waiting up to
  M s`), an answer after a wait is reported (`Bridge answered after N s`), and result.json
  carries `preflightSeconds` so a slow preflight stays visible in the archive instead of hiding
  inside `wallClockSeconds`.

  One silence is not waited out: no `NinjaTrader.exe` process at all. A stale or absent
  `heartbeat.json` cannot prove that waiting is useless — the AddOn writes the file only once it
  has loaded, so a cold start shows the same file while the wait is exactly right — but the
  process list can: without the process nothing could ever answer. After every unanswered probe
  the poll therefore reads the process list once (`restart.process_listed`, one `tasklist` call,
  measured 0.065–0.072 s) and stops on the spot when the list was read and does not name the
  process — one probe, 26 s, what the single ask cost, instead of the budget. A list that could
  NOT be read (tasklist missing from PATH, timed out, exit non-zero, no output; measured
  2026-09-02) is no verdict: it is said once per distinct failure, the silence stays a wait, and
  the budget-exhausted report calls the process UNKNOWN. The check is made only after an
  unanswered probe, never before the first one, so a run whose first probe is answered is
  untouched. The error string is the same in every case — callers match on `preflight: the
  bridge did not answer` — while the explanation names the cause. The poll and the process check
  are covered without NinjaTrader in `tests/test_playback_run_preflight.py`; `restart.is_running()`
  is unchanged for its other callers.

- **`ntstatus` decides "stale" by comparing the sources with the code NinjaTrader executes.**

  The verdict compared the DLL's build time with the process start, so after every `reload` it
  answered `DLL built N min AFTER NT started — NT is running older code; restart it` while the
  reloaded code was exactly what ran (measured 2026-09-02: three compile+reload rounds at 19:14,
  19:41 and 20:17 in one process started at 16:02, each followed by that verdict and by runs on
  the new code). Two attempts were dropped on measurement: remembering the last reload's time
  (the old code handles the reload that deploys it), and matching the DLL's ModuleVersionId
  against the loaded assemblies (NinjaTrader executes a reload from a temp assembly,
  `Documents\NinjaTrader 8\tmp\<guid>.dll`, compiled once more from the sources - its own
  MVID, never the DLL's; measured 2026-09-03 05:15 via `typeof(NT8BridgeServer).Assembly`).

  What answers the operator's question is the executing assembly's build time against the
  newest `.cs` under `bin\Custom`: a source newer than the running code is code NinjaTrader has
  not compiled into what it runs. The AddOn reports `runningAssembly` (name, location, builtUtc),
  `newestSource` (path, modifiedUtc) and `sourcesNewerThanRunningCode`; the verdict is that
  answer, names the newer source, and falls back to the old time rule only when a side could not
  be read - saying so (`time rule`).

- **`playbackrun` teardown: the "strategy rows" verdict measures what the removal waited for, and
  gives the grid a moment to catch up.**

  `restore` removes the strategy rows and waits until no row carries a strategy object any more
  (`restore: strategy rows 1 -> 0 after 103 ms`), then the stage's verdict re-read the grid's raw
  entry count and, measured 2026-09-03 06:32 on a loaded machine (twelve concurrent headless
  NinjaTrader hosts), got `1`: the grid had not refreshed yet. That made `baselineClean` false and
  rc 2 for a run that had reached its data end with nothing running. The verdict now counts the
  rows carrying a strategy (the removal's own measurement), reports the raw entry count next to it,
  and polls for up to 10 s before it judges: `1 entries, 0 with a strategy after 250 ms` is clean,
  `1 entries, 1 with a strategy after 10000 ms` is not.

- **`playbackrun` confirms NinjaTrader's order-rejection notice during a run instead of letting it
  stack.**

  `Playback101, Stop price can't be changed above the market. affected Order: Sell 1 StopMarket @
  29861,5` is an `NTMessageBox` NinjaTrader raises when a strategy's stop modification is refused
  because the market crossed the stop between the strategy's own side check and NinjaTrader's
  validation. Measured 2026-09-02 (Playback/Historical, NinjaTrader log 16:55:34): seven in one run,
  every one handled by the strategy itself (the remainder closed at market), the run reached the data
  end - but each box stays until someone clicks OK, so an unattended run collects one per rejection,
  and a modal holds the UI thread every later stage needs.

  This is the second, and only other, exception to "a modal must never be clicked away" (the first is
  the Historical Level II notice), and it is as narrow: the `strategystate` stage the play loop runs
  every sample confirms only a window whose type name carries `MessageBox` AND whose text carries
  NinjaTrader's order-rejection trailer `affected Order:` (measured wordings on 2026-09-02:
  `Stop price can't be changed above/below the market`, `Sell/Buy stop ... can't be placed
  above/below the market`, `Order ... can't be submitted: The OCO ID ... cannot be reused` - a match
  on the first wording alone confirmed one of seven boxes and left six standing), invokes its OK
  through the button's automation peer, and reports
  `order notices: N dismissed this sample, M in this run`. The driver prints every confirmation and
  result.json carries `orderNoticesDismissed`. Every other modal stays standing and stays the finding.

  NinjaTrader opens that box without an owner: it is in neither `Globals.AllWindows` nor any
  window's `OwnedWindows` (measured 2026-09-02 19:22-19:24: seven rejections in NinjaTrader's log,
  three `Error` windows in the Win32 inventory, and a scan over those two lists answered
  `0 dismissed` every sample). The stage therefore also walks `PresentationSource.CurrentSources`
  - every WPF top-level window of the process, each read and confirmed on the dispatcher of the
  UI thread that owns it - and reports what it saw (`windows N, message boxes M, matching K`).
  Measured right after: one sample against three standing boxes answered `3 dismissed ...
  (windows 10, message boxes 3, matching 3)` and the inventory went from three `Error` windows to
  none within five seconds.

### Added

- **`satemplate` — ask for a backtest by template FILE instead of by parameter list.**

  ```bash
  python -m nt8bridge satemplate --template "…\VariantA.xml"
  python -m nt8bridge backtest            # --config is now optional
  ```

  `configure` writes individual properties from a config.json. NinjaTrader's own strategy
  templates carry the *complete* parameter set plus instrument and window, and a test suite is
  usually one strategy class against many of them. Naming the file is shorter and cannot drift
  from what the GUI runs, because it is what the GUI runs.

  The template is **assigned**, not copied member by member: `RestoreFullStrategyTemplate`
  already returns the strategy a file describes, and `TabStrategyProperties.StrategyTemplate`
  takes it (`canWrite=True`, read off a running instance with `probe`). When the template names
  a different class the class is selected **first** — writing `Strategy` installs a fresh
  template and drops the old one silently (see `configure.py`, issue #6), so the other order
  loses everything.

  The instrument travels too. NinjaTrader does not hand it over: after assigning a template
  naming `NQ 06-26` the strategy read `NQ 06-26` while the tab still read `ES 09-26` — and it is
  the tab's instrument the Analyzer runs. `applied` in the response is a *reference* comparison,
  so a property that accepts a write and keeps its own object fails instead of passing.

- **`playbackrun` — one Playback measurement, end to end.**

  ```bash
  python -m nt8bridge playbackrun --strategy MyBot --instrument "MNQ 09-26" --source marketreplay --tick-replay false --bars-type Minute --bars-value 1 --from 2026-08-10 --to 2026-08-10
  ```

  Every connection off, clean start, connect, source, dates, range, speed, attach the strategy,
  play to the data end, restore the baseline — then one JSON verdict and an archived run
  directory. Every value it writes is READ BACK, and it exits 0 only when the data end was
  reached **and** the teardown restored the baseline, so a run that ended for any other reason
  cannot be mistaken for a result. It waits on events, never on a fixed sleep: the transport is
  confirmed by the clock moving, the data end by the clock reaching the requested end, and the
  attach by NinjaTrader's own log entry `Enabling NinjaScript strategy`.

  Adds a second AddOn file (`NT8BridgeServerPlayback.cs`) carrying the stages this needs.

  - **`--account` — name the simulation account the strategy trades on.**

    ```bash
    python -m nt8bridge playbackrun --strategy MyBot --account Playback101 ...
    ```

    Without the switch the run asks NinjaTrader for its own playback account
    (`Account.PlaybackAccountName`) instead of assuming a name. An account that does not exist
    **stops the run** and lists the ones that do — it is never swapped for another, because a
    strategy trading on an account nobody watches produces a run that looks successful.

    Accounts separate bookkeeping, not the run's mode: `--source` and `--tick-replay` are
    process-wide in NinjaTrader, so every bot in one pass shares them.

  - **`--stage-wait` — seconds one ordinary stage may take.**

    The connect and Reset stages get 2.5× that; without the switch the driver's own 10 / 25
    apply. Raise it on a box where NinjaTrader is slow. Two stages keep their own floors on top
    of it: the connect (`CONNECT_WAIT`, see Fixed) and the preflight (`PREFLIGHT_WAIT`).

### Changed

- **Every request now carries a TTL.** A trigger that has waited longer than `ttlSec` in the
  server's queue is discarded instead of executed, and answered with `status: "expired"` and code
  `EXPIRED`. The default is 300 s and it applies to **all** commands. Measured 2026-08-19: a
  backlog released after 11 minutes enabled, disabled and removed a strategy in the middle of a
  running measurement.

- **`connections` says where each row came from.** A new `source` field reads `configured` for a
  row from the connection list and `live-only` for one that is running without being in it — the
  case that made a live connection invisible to a caller reading the configuration alone.

- **`playback` — the `.nrd` coverage scan is opt-in: `--coverage` (every instrument) or
  `--instrument X` (that one).**

  ```bash
  python -m nt8bridge playback                            # clock, MOVING?, speed — no store scan
  python -m nt8bridge playback --instrument "MNQ 09-26"   # coverage of that one instrument
  python -m nt8bridge playback --coverage --timeout 600   # coverage of every instrument
  ```

  The wide scan reads every `.nrd` of every instrument and holds the AddOn's poller for minutes
  (measured 2026-08-19: 3-7 min, 5 MB, 35 instruments); a named instrument scans just that one
  (17 s). The AddOn now scans only when the request names an instrument or carries
  `"coverage": "true"`, and always answers `coverageScanned: true|false` before `coverage`
  (an empty list when not scanned). The readiness verdict keeps the two apart: an unscanned
  store reads `coverage not scanned (pass --coverage or --instrument)` instead of `no readable
  .nrd files on disk — nothing to replay`, which was a claim about data nobody had looked at.
  A response without the key — an AddOn built before this change always scanned — keeps its
  old meaning. `--require-ready` implies the scan (of `--instrument` when given, else of every
  instrument), so a bake script keeps its old meaning: connected, loaded and parked.

### Fixed

- **Subprocess output is decoded with `errors="replace"`.** `tasklist`, `schtasks` and `taskkill`
  write in the console codepage, not UTF-8; on a non-English Windows the first non-ASCII byte
  raised `UnicodeDecodeError` and took the command down with it.

### Documentation

- **`playbackrun` and `satemplate` added to the README's command list**, and the count in its
  heading brought along: the list named 31 of the 33 commands (`performance` and `perfwindow`
  had only their prose section), and now names 33 of 35.

- **README: one public section "Run modes and the stores they read" replaces internal
  test-project notes.** What backs its table is the AddOn's own code, not quoted log lines: for a
  Historical run the coverage pre-flight scans `db\tick\<instrument>\*.ncd` (`TickCoverage`),
  for a Market Replay run `db\replay\<instrument>\*.nrd` (`ReplayCoverage`), and `--source`
  picks the scanner; the Strategy Analyzer has no such pre-flight and the README claims no store
  for it. The section also documents what decides `playbackrun --source`
  (`PlaybackAdapter.IsSourceHistoricalData`, written and read back; the panel radios are display
  only), the store-aware pre-flight, the Level II notice the bridge dismisses without touching
  any other dialog, and the teardown rule for a killed run (`alloff` + `restore`). A line break
  that had cut `db\replay` in two is fixed. The command list says that `playback` scans
  coverage only on request, and the `playbackrun` section documents the date format and the
  preflight wait. New sections `satemplate` and `playback` (with `--require-ready` and
  `coverageScanned`), and a flag reference for `playbackrun` (`--template`, `--name`,
  `--stage-wait`, `--max-wait`, `--nt8-dir`); the `ntstatus` line names the rule it applies.

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
