> **⚠ Reality check on what a mutating tool does.** `nt_order_place`, `nt_flatten` and
> `nt_strategy_enable` submit real orders to whatever account you name. They are gated on
> `confirm: true` **and** an account allowlist, and the allowlist defaults to refusing anything it
> cannot prove is a sim account. That is deliberate: the operating contract's first ALWAYS-STOP is
> live/funded accounts, and trust must never substitute for a refusal in code.

# sentinel-mcp — NinjaTrader 8 as MCP tools

A typed MCP seam over `nt8bridge`. It does not reimplement anything: the bridge already has 50 verbs,
a validated in-process AddOn, and refusals earned by measurement. This exposes the useful subset as
schema-checked tools so an agent stops losing time to shells.

## Why it exists — the actual cost it removes

One session on 2026-08-14 hit four quoting failures, none of them about NinjaTrader:

```
nt8bridge: error: unrecognized arguments: 8/db/NinjaTrader.sqlite'
nt8bridge: error: unrecognized arguments: Rollover Notification'
nt8bridge: error: unrecognized arguments: ; ... playbackctl
```

Each was a path with a space or a quoted title crossing `bash → ssh → cmd.exe → PowerShell`. The CLI
was never wrong. `subprocess.run([...], shell=False)` has no shell in it, so
`C:\Users\...\NinjaTrader 8\db\NinjaTrader.sqlite` is one argument and the class of bug is gone.
`test_server.py` asserts exactly that with that exact path.

## Install

```
claude mcp add sentinel-nt8 -- C:/ntbv/Scripts/python.exe -m sentinel_mcp.server
```

Run it from `C:\ntbv-src` (or set `PYTHONPATH` there). Restart Claude Code so it re-reads MCP servers.
Verify:

```
C:/ntbv/Scripts/python.exe -m sentinel_mcp.test_server     # 19 checks, offline
```

| env var | default | why |
|---|---|---|
| `SENTINEL_MCP_PYTHON` | the running interpreter | which python runs `nt8bridge` locally |
| `SENTINEL_MCP_REMOTE_PYTHON` | `C:/ntbv/Scripts/python.exe` | its path **on a sentry** |
| `SENTINEL_MCP_ALLOW_ACCOUNTS` | — | widen the account allowlist, deliberately |

## Tools

Every tool takes optional `host` (a `fleet.conf` name). Read-only unless marked.

| tool | notes |
|---|---|
| `nt_status` | ⭐ `loadedAssembly` vs `dllOnDisk` build times — equal is the proof a compile took effect |
| `nt_selfcheck` · `nt_connections` | |
| `nt_windows` | ⭐ use when `nt_charts` returns 0 — see the trap below |
| `nt_dialogs` | modal **and** non-modal |
| `nt_log` · `nt_screenshot` | |
| `nt_accounts` · `nt_orders` | |
| `nt_order_place` · `nt_order_cancel` · `nt_flatten` | ⚠ order sources |
| `nt_charts` · `nt_strategies` | |
| `nt_strategy_enable` | ⚠ order source; holds the state before believing it |
| `nt_strategy_disable` | not gated — the safe direction must never be harder to reach |
| `nt_compile` | ⚠ NT's own compiler; can orphan bar-type seams on a live chart |
| `nt_restart` | ⚠ ⛔ never the operator's trading box |
| `nt_playback` · `nt_stage_list` · `nt_stage` | ⚠ `nt_stage` mutates; it is how you position a replay |
| `nt_fleet` · `nt_versions` · `nt_builds` · `nt_corpus` | across every box |

## Three things measured while building it, because each would have shipped as a silent bug

1. **`--host` is not global.** Exactly five verbs accept it — `stage`, `fleet`, `corpus`, `versions`,
   `builds`. The first cut passed it to everything and `nt_status host=sentry-1` returned an argparse
   usage dump. Other verbs now run **on** the box over ssh, with each argument quoted for `cmd.exe`
   (double quotes — a single quote is an ordinary character there, which is what split that
   `NinjaTrader 8` path in the first place).

2. **A nonzero exit code does not mean failure.** `ntstatus` returns **2** while emitting perfectly
   good JSON; `selfcheck` returns 0. Exit codes are verb-specific, so the **payload** decides the
   verdict. Same lesson as a background job whose "exit 0" was the shell's and not the program's:
   read what the thing said, not the envelope.

3. **A typo'd argument is refused, not dropped.** An ignored `acount=` key is how a caller believes
   it passed a limit price it never passed.

## Traps worth knowing before you trust an answer

- **`nt_charts` returning `count: 0` is not evidence of no charts.** The chart probe has an internal
  5-second wait that `timeout` does not raise; a chart whose UI thread did not answer reads as absent.
  Cross-check with `nt_windows`. This misdiagnosis has been made twice.
- **`succeeded` ≠ `changed`.** Disabling an already-stopped strategy succeeds and moves nothing. The
  bridge's distinction is passed through verbatim rather than flattened.
- **A black chart in `nt_screenshot` proves nothing** — SharpDX does not paint unattached.
- **Attaching a strategy is not here.** `nt8bridge strategy --add` cannot do it on this build; use
  `Sentinel\tools\nt_attach_strategy.py`, which writes the three places NT actually reads (the
  `Strategies` row, its instrument/account links, and the workspace handle) with NT stopped.
