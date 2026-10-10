# NT8 Guard — Strategy backups (Python tools)

Command-line tools that NT8 Guard runs for you. Standard-library Python 3.10+, no installs (the QR
code library is vendored in `vendor\`, unmodified, with its licence).

| Tool | What it does | When it runs |
|---|---|---|
| `backup_export.py` | Saves every strategy in NinjaTrader's Strategies list: a complete NT8 template (`templates\Strategy\<class>\Guard backup - <name>.xml`, loadable with NT8's *Load template*) and an exact copy of its database row, plus the chart tab it sits on and the names of its account and instrument (for the control panel's Strategy backups list). | Every 30 minutes while NinjaTrader runs |
| `backup_restore.py` | Puts back strategies NinjaTrader dropped from its list, with NinjaTrader's own bytes, then puts them back on their charts. **NinjaTrader must be closed.** | Before NinjaTrader is started |
| `chart_relink.py` | Points a chart at a strategy's new Id after it was re-added by hand. **NinjaTrader must be closed.** | On request (the restore already does it) |
| `qr_png.py` | Draws the QR code the control panel shows for the phone-alert address (the address comes in the `NT8GUARD_QR_TEXT` environment variable, never on the command line). | When you press Show code |

## Why strategies disappear

When NinjaTrader closes, it drops **switched-off** strategies from its Strategies list. Guard tells
the two ways a strategy can disappear apart by NinjaTrader's start time:

- **missing** — gone across a restart. Put back automatically.
- **removed** — removed while NinjaTrader was running, so probably on purpose. Only put back with
  `--name "<name>"` (or `--all`); `backup_export.py --forget "<name>"` drops it from the backups.

## Charts

A chart remembers its strategies by database Id. When a strategy comes back under a new Id, Guard
points the chart at the new Id; when it comes back after a layout save lost it, Guard adds it back to
the chart tab it was on. A strategy that is on no chart and was not just restored is left alone.
Each workspace file is copied to `state\backups\workspaces\<time>\` before it is changed.

## Safety

- Restore and relink **refuse while NinjaTrader runs** (it would overwrite the change from memory) —
  and whenever anything has the database file open, which catches a running NinjaTrader however its
  folder is spelled (mapped drive, another path to the same folder).
- Restore **refuses a database whose strategy tables are not laid out as expected** (another
  NinjaTrader version) and writes nothing; the templates still work by hand.
- The database is copied to `NinjaTrader.sqlite.bak-restore-<time>` before the first write (last 3 kept).
- Report only unless `--apply`.

## Usage

```
python backup_export.py  [--nt8-dir DIR] [--guard-home DIR] [--nt8-start-utc ISO|auto|none] [--dry-run] [--forget NAME]... [--json]
python backup_restore.py [--nt8-dir DIR] [--guard-home DIR] [--name NAME]... [--all] [--apply] [--no-relink] [--json]
python chart_relink.py   [--nt8-dir DIR] [--guard-home DIR] [--apply] [--json]
```

Folders default to `<Documents>\NinjaTrader 8` and `%LOCALAPPDATA%\NT8Guard` (environment overrides:
`NT8_DIR`, `NT8GUARD_HOME`). Exit codes: `0` done or nothing to do, `2` report mode found work, `1`
refused or failed.

## Tests

From the `NT8Bridge` folder: `./.venv/Scripts/python.exe -m pytest guard/python/tests -q`
