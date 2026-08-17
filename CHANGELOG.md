# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.7.0] - 2026-08-04 — SIX COMMANDS: THE MUTATING HALF

1.5.0 made NinjaTrader's hidden state readable. This makes it *operable*, and closes the two gaps
that let a "one provable tree" claim be true of only half the system.

Every mutating command here follows the same three rules, each bought with real time:
**it refuses an ambiguous match rather than resolving it**, **it requires explicit confirmation
before arming an order source**, and **it verifies the OUTCOME instead of trusting that the call
resolved.** A reflection call that returns cleanly while changing nothing is the single failure this
project keeps re-paying for.

### Added

- **`selfcheck` — the CLI audits itself.** The fleet was proven to run one identical `bin\Custom`
  tree and the conclusion drawn was "the fleet runs one tree". It did not: the PYTHON half — this
  package, *the thing every automation actually invokes* — was three different versions across six
  boxes, and nothing had ever checked it. Reports which nt8bridge is imported and from where, a
  content hash over its modules, whether the installed metadata matches the source tree, and whether
  declared dependencies are actually satisfied. `--requirements` verifies any extra manifest;
  `--python` audits another interpreter, because "is the manifest satisfied?" has no answer until you
  say which venv. `--expect-hash` / `--expect-version` turn it into a fleet assertion.

  ⭐ On its very first run it found three disagreeing version numbers on the reference box
  (pyproject 1.6.0 / installed 1.2.0 / `__init__` 0.1.0) — an editable install keeps the CODE current
  while the reported version stays whatever it was at install time. **+20 tests.**

- **`log` — grep a log file from inside NinjaTrader.** The verification loop ended at "read the live
  truth" and that last step was an ad-hoc remote shell per box. The remote transport encodes
  UTF-16LE then base64, so the usable payload is ~12 KB against files of tens of megabytes:
  filtering at the client is not slow, it is impossible. Matching happens NT-side and only matches
  travel. Opens with `FileShare.ReadWrite | Delete`, because NT holds its own logs open — the live
  file is exactly the one a naive reader cannot touch. A missing path is an ERROR, never `ok` with
  zero matches; a pattern that will not compile fails loudly rather than degrading to match-all or
  match-none, one of which reads all-clear. `--fail-on-match` makes it a pre-flight fault gate.
  **+10 tests.**

- **`dialog --all` and `dialog --close`.** The modal-only scan reported *"no dialogs"* on all six
  boxes while a sentry sat with an `Error` box **and** an `Auto Rollover Notification` open — a clean
  bill of health over a machine with two unanswered prompts on screen. Neither disables its owner, so
  neither counted as modal. `--all` widens to every visible top-level window; `modals` stays the
  incident list. `--close` posts `WM_CLOSE` (what the title-bar X sends) for a window whose buttons
  cannot be resolved — the WPF walk found none on that Error box though its OK was plainly visible
  in a screenshot. The outcome is still verified: the window has to actually go away.

  ⚠ The case that justifies never clicking an unnamed button: a .NET assertion box offers
  `Abort=Quit, Retry=Debug, Ignore=Continue`, and its **default is Abort**. On an unattended trading
  box the default answer kills the platform.

- **`dialog` — see and answer the modal blocking a headless box.** A modal stops everything and
  announces nothing; the only way to see one was an interactive session, which is itself barred
  during a bake. Modality is detected without touching WPF (Windows disables the owner), buttons are
  found via child HWNDs for native dialogs and a per-dispatcher visual-tree walk for WPF ones. It
  will not guess: `dismiss` requires an explicit dialog AND button and refuses either ambiguity —
  the default answer on a rollover prompt is the one that spends your holdout. The click is posted,
  then the window is re-probed, and `dismissed` reports whether it actually went away. **+13 tests.**

- **`strategy` — list / enable / disable / add, on a CHART.** A workspace does not contain its
  strategy: it stores an integer handle, and the type and parameters live in a database that also
  holds Accounts, Orders and Positions and so must never be copied between boxes. Staging a cell on a
  second machine ended in a human re-adding it by hand — and anything needing a GUI click per box
  cannot run a matrix. `enable` and `add` require `--confirm`; `disable` does not, because the safe
  direction must never be the harder one to reach. **+17 tests.**

  ⭐ Driving it against a live chart forced a distinction the first draft got wrong: `changed` and
  `succeeded` are different questions. Disabling an already-stopped strategy moves nothing yet
  succeeds; a SetState that resolved and left the state untouched moves nothing and FAILED.

- **`playbackctl` — seek, speed, and the replay range.** A day was lost to a seek that "did not
  work": every failing attempt reported back in 57 ms and every succeeding one took 5-7 seconds,
  because the seek is asynchronous and walks the clock toward the target — so judging it immediately
  froze it mid-flight. This polls until the clock SETTLES before rendering any verdict and returns
  the whole trajectory, because reporting only the final position makes "walked and stopped short"
  indistinguishable from "never moved", and those need opposite fixes. **+14 tests.**

  ⭐ `--api` (read-only member discovery) refuted the assumption the seek was written against: this
  build exposes **no `Reset(DateTime)` at all**. The real seek is a writable static `NowEst`, and the
  range is `FromEst`/`ToEst` — not the obfuscated ConnectOptions members. Both paths are kept and the
  response says which was used, so the day a build changes this it says so instead of silently doing
  nothing.

- **`chart` — list charts, attach and remove indicators, close.** The other half of reproducing a
  cell: a chart-derived sensor computes from its own chart's bars, so the indicator set is part of
  the cell rather than decoration. Add and remove are judged by the chart's indicator count before
  and after. **Creating chart windows is deliberately excluded** — it means building a WPF window on
  another UI thread of a platform hosting live order routing, and the value is not there: `layout`
  already places windows across machines and a workspace already carries the charts. **+13 tests.**

### Found by driving it against six live boxes

Everything below was invisible to the test suite and to reading the code. It is recorded because the
pattern repeats: *these tools are only worth having if using them can prove them wrong.*

- **A seek can succeed and land where there is no data.** Writing the clock validates nothing. On a
  transport with 04-19..04-24 loaded, a seek to 2026-05-01 answered `succeeded: true, offset 0` — and
  it was telling the truth. The clock really did go there. A bake from that position produces nothing
  while every check reads green. An out-of-range target now **fails closed**, `--force` is the only
  way past, and every seek result carries the loaded range.

- **Attaching an indicator is not running one.** `Indicators.Add` + `SetState(Active)` leaves it at
  `Configure` with `enabled=false` while its neighbours on the same chart read `Realtime`, and no
  `Reload`/`Refresh` member resolves on `ChartControl` to finish the job. The command reported
  success. It now reports `attached` and `running` separately and **fails** unless both hold.
  ⚠ This means `--add-indicator` currently ATTACHES BUT DOES NOT ACTIVATE. It says so.

- **`strategy` enable/disable: four rounds of "fixed", and the last one was the real bug.** Worth
  reading as a sequence, because each step looked like a complete answer:

  1. `SetState(Active)` on a `Finalized` chart strategy resolves and changes nothing. Verification
     returned exit 2 rather than a green — the design working on its own author.
  2. `chart --api` named the real member: `ChartControl.StrategyEnable(StrategyRenderBase, ChartBars,
     bool, Action)`. Discovery answered in one command what guessing had not.
  3. It takes an `Action` callback, so it is ASYNCHRONOUS — reading the state immediately after
     looked exactly like "the call did nothing". **The seek root cause, met a second time.** Fixed by
     polling until the state settles.
  4. A 4-second hold passed and the strategy came back 10 seconds later, so the hold became an active
     watch that fails the instant the state leaves the target.
  5. ⭐⭐ **And a 45-second watch still "confirmed" a disable that never happened.** `StrategyEnable`
     does not flip a flag on the object you hand it — it **terminates that instance and the chart
     re-applies a new one**. The verifier was holding the original reference and watching a corpse:
     it read `Finalized` and held there forever while the chart's live strategy was a different
     object at `Realtime`. Only an independent `strategy list`, which re-enumerates the collection,
     disagreed. **Identity here is the (type, chart) pair, never the pointer.**

  6. NT's own log finally named the mechanism, and it took two readings to hear it:
     `Disabling NinjaScript strategy …` immediately followed by `Enabling … On starting a real-time
     strategy … MaxRestarts=4 in 5 minutes`. **`StrategyEnable` always ends ENABLED** — it is a
     re-apply, not a setter, and its boolean is not the lever its position suggests.

  ⇒ **The durable answers, each measured rather than assumed:**
  | want | member that actually works | verified by |
  |---|---|---|
  | start a strategy | `ChartControl.StrategyEnable(...)` | state reaches Realtime (async) |
  | stop a strategy | `ChartControl.RemoveStrategyForChartBars(bars)` | `Realtime -> Absent`, held 20 s |
  | disable in place | **nothing found** — 5 mechanisms tried, all reverted | reports `reverted: true`, exit 2 |

  So `--mechanism` names every lever (`flag`, `flag-refresh`, `enable-call`, `setstate`, `remove`),
  `auto` climbs them in ascending order of violence and keeps the first rung that **holds**, and the
  response says which one won. When a future build moves this, the question stays answerable by
  measurement instead of by another round of guessing. `--hold-ms` is exposed because a hold shorter
  than a revert is just a slower way to print the same false green, and `--index` exists because
  refusing ambiguity must not become a dead end when a chart legitimately holds two of the same type.

  ⚠ Still true and now honestly reported: **disable-in-place does not work on this build**, and
  `--add` attaches at `Configure` — instances added this way never bind to a `ChartBars`, so
  `RemoveStrategyForChartBars` cannot clear them either. Removal is verified **by count**, because
  the state check happily read "not live" for two inert duplicates that were still on the chart.

- **`--speed 0` was rejected as if it were a missing argument** — in the validator *and*, one layer
  down, in the request builder's falsy-zero test. 0 is what a parked transport reads, so the single
  value needed to STOP a running replay was the one value that could not be sent. Found while putting
  a test box back exactly as it was found.

- **Setting a speed starts the transport.** Not a defect, but it is not obvious from the name, and it
  moved a test box's clock 2h15m before it was noticed. Worth knowing before scripting it.

### Fixed

- **The AddOn's JSON key lookup matched VALUES as well as keys.** It took the first `"key"` anywhere
  in the request and then hunted for the next `':'`, so `ExtractJsonString(req, "chart")` matched the
  value in `"kind":"chart"` and returned the following field. The chart filter silently became
  `"list"` and a box with three charts answered *"no matching charts"* — confident, well-formed and
  completely wrong. A key is now required to be a quoted token followed by a colon. Found by driving
  the command, not by reading the code.

- `log --text` crashed with `UnicodeEncodeError` on a cp1252 console, losing an entire read over one
  glyph. These logs are full of arrows and box-drawing. The text path now degrades the glyph; the
  JSON path stays ASCII-escaped so it is exact on any console.

- `nt8bridge.__version__` (0.1.0) and `pyproject` (1.6.0) had drifted apart. Both now say 1.7.0, and
  `selfcheck` compares them plus the installed metadata so they cannot silently diverge again.

## [1.6.0] - 2026-08-04 — LOCAL INTEGRATION BUILD

Not a release. This is the union of the two in-flight PRs plus `layout`, built so the six-node
fleet runs ONE coherent tree instead of whichever PR branch was deployed last. Upstream should take
1.4.0 and 1.5.0 on their own; this section exists so a node reporting 1.6.0 is self-explanatory.

### Added

- **`layout` — capture and apply where NinjaTrader's windows sit.** Window placement was the last
  uncontrolled input to a replay-equivalence run: the code, the `.nrd`, the historical bars and the
  chart+strategy blob were all files we could hash, and layout lived only inside a running
  NinjaTrader, set by hand, per box. Stored as **fractions of the monitor work area**, so one file
  describes the same arrangement on a 2560x1440 desktop and a 1920x1080 VM, and matched on
  **identity rather than HWND**, so it survives the restart it exists to survive.

  ⚠ That last part needed a specific fix: NinjaTrader's WPF windows are classed
  `HwndWrapper[NinjaTrader.exe;UI thread 1;<GUID>]` and the GUID is regenerated on every launch, so
  the raw class is useless as a key. Only the app segment is stable.

  The AddOn half enumerates and moves an HWND it is told to move — nothing else. Matching,
  fractions and monitor mapping are pure functions in `layout.py`, because the AddOn is the one
  component that cannot be tested without a running NinjaTrader. **+28 tests.**

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
