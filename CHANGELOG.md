# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
