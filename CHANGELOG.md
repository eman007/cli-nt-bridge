# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
