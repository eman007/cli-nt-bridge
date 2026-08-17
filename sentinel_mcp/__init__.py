r"""sentinel_mcp — an MCP server over `nt8bridge`, so an agent stops fighting the shell.

⚠ This docstring is r-prefixed and must stay that way: it names Windows paths, and `\U` in a normal
literal is a truncated unicode escape that stops the whole package importing. That is the FOURTH time
this exact bug landed in one session (an inert Stop-hook regex, a module that would not import,
control characters burned into NOW.md, and this) — a backslash in a non-raw Python literal is the
most reliable self-inflicted wound in this codebase.

WHY THIS EXISTS, and it is not "MCP is nicer". On 2026-08-14 a single session lost real time to
FOUR separate shell-quoting failures, none of them about NinjaTrader at all:

    nt8bridge: error: unrecognized arguments: 8/db/NinjaTrader.sqlite'
    nt8bridge: error: unrecognized arguments: Rollover Notification'
    nt8bridge: error: unrecognized arguments: ; ... playbackctl

Every one was a path containing a space, or a quote, crossing bash -> ssh -> cmd.exe -> PowerShell.
The CLI was never wrong; the four layers of shell under it were. `subprocess` with an **argv list**
has no shell in it, so `C:\Users\...\NinjaTrader 8\db\NinjaTrader.sqlite` is simply one argument and
the whole class of bug disappears.

⇒ This is a TYPED SEAM over a CLI that already works, not a reimplementation. `nt8bridge` has 50
verbs, a validated in-process AddOn, and refusals that were earned by measurement. Rewriting that
against NT's .NET API would throw away every lesson baked into it. Each tool here is a thin,
schema-checked wrapper that builds an argv list and reports what came back.
"""

__version__ = "0.1.0"
