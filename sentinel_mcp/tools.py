r"""tools — the tool registry: what an agent may call, and what it must not be able to do by accident.

THE SHAPE. Every tool is a declarative row: a JSON schema for its inputs, the `nt8bridge` verb it
maps to, and how its arguments become an argv LIST. No tool builds a shell string, so nothing here
can be broken by a space in a path or a quote in a window title.

⛔⛔ THE TWO REFUSALS THAT ARE THE POINT OF THIS FILE

1. **`mutates: True` requires `confirm: true` in the call.** Arming a strategy, placing an order,
   flattening, restarting NT and staging replay files are not reads. The bridge already asks for
   `--confirm` on its own order-source verbs; this re-asserts it at the seam so an agent cannot arm
   an order source as a side effect of exploring.

2. **`account` is checked against an ALLOWLIST, and the default is refuse.** The operating contract's
   ALWAYS-STOP list begins with *live/funded accounts*. Trust in an agent must never substitute for
   a refusal in code, so a tool that names an account it cannot prove is a sim account does not run.
   `SENTINEL_MCP_ALLOW_ACCOUNTS` widens it deliberately; nothing widens it implicitly.

⚠ `compile` and `restart` are marked mutating even though neither places an order. A recompile can
orphan the bar-type seams on a live chart ([[f5-decouples-bartype-seams]]) and restarting NT on the
operator's own trading box is an ALWAYS-STOP. `host` exists so those land on a sentry on purpose
rather than on `main` by omission.
"""
import os
import re

# Sim/test accounts. Anything not matching these is refused — the safe direction is the default,
# never the flag. Widen with SENTINEL_MCP_ALLOW_ACCOUNTS="Foo,Bar" only when you mean it.
_DEFAULT_ALLOWED = ("Sim101", "Playback101", "Backtest")
_ALLOW_PATTERNS = (re.compile(r"^SIM-[A-Z0-9]+-\d+$"),)   # the SIM-<LANE>-<SLOT> convention


def allowed_accounts():
    extra = os.environ.get("SENTINEL_MCP_ALLOW_ACCOUNTS", "")
    return tuple(_DEFAULT_ALLOWED) + tuple(a.strip() for a in extra.split(",") if a.strip())


def account_is_allowed(name):
    if not name:
        return True, None                       # no account named ⇒ nothing to refuse
    if name in allowed_accounts():
        return True, None
    if any(p.match(name) for p in _ALLOW_PATTERNS):
        return True, None
    return False, (
        "REFUSED: account %r is not a known sim/test account. The operating contract's ALWAYS-STOP "
        "list begins with live/funded accounts, so this seam refuses rather than trusts. Allowed: %s "
        "(plus SIM-<LANE>-<SLOT>). Widen with SENTINEL_MCP_ALLOW_ACCOUNTS if you truly mean to."
        % (name, ", ".join(allowed_accounts())))


def _s(desc, **kw):
    d = {"type": "string", "description": desc}
    d.update(kw)
    return d


def _b(desc):
    return {"type": "boolean", "description": desc}


def _i(desc):
    return {"type": "integer", "description": desc}


HOST = _s("Target machine from fleet.conf (e.g. sentry-1). Omit to act on THIS box. "
          "⚠ Name a sentry for anything disruptive — main is the operator's trading platform.")

# ── the registry ────────────────────────────────────────────────────────────────────────────────
# argv: callable(args) -> list[str] appended after the verb.
TOOLS = []


def tool(name, verb, summary, props=None, required=None, mutates=False, argv=None, timeout=120):
    TOOLS.append({
        "name": name, "verb": verb, "mutates": mutates, "timeout": timeout,
        "description": summary + ("  ⚠ MUTATES: requires confirm=true." if mutates else ""),
        "schema": {
            "type": "object",
            "properties": dict({"host": HOST}, **(props or {})),
            "required": list(required or []),
            "additionalProperties": False,
        },
        "argv": argv or (lambda a: []),
    })


def _flag(a, key, flag):
    v = a.get(key)
    if v is None or v is False:
        return []
    if v is True:
        return [flag]
    return [flag, str(v)]


# ── health / inventory (read-only) ──────────────────────────────────────────────────────────────
tool("nt_status", "ntstatus",
     "NT process, version, and the LOADED vs ON-DISK assembly build times. The two timestamps "
     "matching is the proof a compile actually took effect — a compile alone does not reload it.")

tool("nt_selfcheck", "selfcheck", "Prove the bridge's own Python tree and venv against its manifest.")

tool("nt_connections", "connections",
     "Data/order connections and whether each is connected. UNREACHABLE is its own outcome, never "
     "folded into 'fine'.")

tool("nt_windows", "windows",
     "Every top-level NT window. ⭐ Use this when `nt_charts` returns 0 — a chart that exists but "
     "whose UI thread did not answer within the internal 5s wait reads as absent, and this is how "
     "you tell the difference.",
     props={"timeout": _i("seconds to wait for NT to answer")},
     argv=lambda a: _flag(a, "timeout", "--timeout"))

tool("nt_dialogs", "dialog",
     "List dialogs, MODAL AND NON-MODAL. A modal-only scan once gave a clean bill of health to a box "
     "with an Error window and a rollover prompt on screen.",
     argv=lambda a: ["--all"])

tool("nt_log", "log",
     "Grep a log file NT holds open, filtered NT-side so nothing large crosses the wire.",
     props={"file": _s("log file path ON the node"), "grep": _s("pattern"),
            "tail": _i("last N lines"), "since_min": _i("only the last N minutes")},
     required=["file"],
     argv=lambda a: (["--file", a["file"]] + _flag(a, "grep", "--grep")
                     + _flag(a, "tail", "--tail") + _flag(a, "since_min", "--since-min")))

tool("nt_screenshot", "screenshot",
     "PNG of a window, captured in-process. ⚠ A black SharpDX chart proves nothing either way — NT "
     "does not paint unattended.",
     props={"title": _s("substring of the window caption"), "out": _s("PNG path ON the node")},
     argv=lambda a: _flag(a, "title", "--title") + _flag(a, "out", "--out"))

# ── accounts / orders / positions ───────────────────────────────────────────────────────────────
tool("nt_accounts", "account", "Accounts with balances and positions.",
     props={"name": _s("account name")},
     argv=lambda a: _flag(a, "name", "--name"))

tool("nt_orders", "order", "List orders. Working only by default.",
     props={"name": _s("account"), "instrument": _s("e.g. 'MNQ 06-26'"),
            "all_states": _b("include terminal orders")},
     argv=lambda a: (["--action", "list"] + _flag(a, "name", "--name")
                     + _flag(a, "instrument", "--instrument")
                     + (["--all-states"] if a.get("all_states") else [])))

tool("nt_order_place", "order",
     "Place an order. ORDER SOURCE.",
     props={"name": _s("account (sim/test only — allowlisted)"),
            "instrument": _s("e.g. 'MNQ 06-26'"),
            "side": _s("Buy | Sell | BuyToCover | SellShort"),
            "type": _s("Market | Limit | StopMarket | StopLimit | MIT"),
            "quantity": _i("contracts"), "limit_price": {"type": "number"},
            "stop_price": {"type": "number"}, "tif": _s("Day | Gtc | Ioc | Opg | Gtd"),
            "oco": _s("OCO group id"), "order_name": _s("label")},
     required=["name", "instrument", "side", "type", "quantity"], mutates=True,
     argv=lambda a: (["--action", "place", "--name", a["name"], "--instrument", a["instrument"],
                      "--side", a["side"], "--type", a["type"],
                      "--quantity", str(a["quantity"]), "--confirm"]
                     + _flag(a, "limit_price", "--limit-price")
                     + _flag(a, "stop_price", "--stop-price") + _flag(a, "tif", "--tif")
                     + _flag(a, "oco", "--oco") + _flag(a, "order_name", "--order-name")))

tool("nt_order_cancel", "order", "Cancel one order by id, or every working order with all=true.",
     props={"name": _s("account"), "order_id": _s("OrderId"), "all": _b("cancel all working")},
     required=["name"], mutates=True,
     argv=lambda a: (["--action", "cancel", "--name", a["name"], "--confirm"]
                     + _flag(a, "order_id", "--order-id") + (["--all"] if a.get("all") else [])))

tool("nt_flatten", "flatten", "Flatten positions. ORDER SOURCE.",
     props={"name": _s("account"), "instrument": _s("limit to one instrument")},
     required=["name"], mutates=True,
     argv=lambda a: (["--name", a["name"], "--confirm"] + _flag(a, "instrument", "--instrument")))

# ── charts / strategies / indicators ────────────────────────────────────────────────────────────
tool("nt_charts", "chart",
     "Charts, their instruments and attached indicators. ⚠ A count of 0 WITH a 'chart did not answer "
     "within 5s' note is not evidence of no charts — cross-check with nt_windows.",
     props={"chart": _s("substring of the chart title"), "api": _b("reflect available members"),
            "indicators": _b("list attached indicators")},
     argv=lambda a: (_flag(a, "chart", "--chart") + (["--api"] if a.get("api") else [])
                     + (["--indicators"] if a.get("indicators") else [])))

tool("nt_strategies", "strategy", "Strategies on charts and their State.",
     props={"chart": _s("substring of the chart title")},
     argv=lambda a: _flag(a, "chart", "--chart"))

tool("nt_strategy_enable", "strategy",
     "ARM a strategy already attached to a chart. ORDER SOURCE. hold_ms is not optional in spirit: "
     "a state observed once is not a state change — NT re-applies a fresh instance, so the target "
     "must HOLD.",
     props={"strategy": _s("name fragment"), "chart": _s("chart title fragment"),
            "hold_ms": _i("how long the new state must hold before it is believed (default 5000)")},
     required=["strategy"], mutates=True,
     argv=lambda a: (["--enable", a["strategy"], "--confirm",
                      "--hold-ms", str(a.get("hold_ms", 5000))]
                     + _flag(a, "chart", "--chart")), timeout=300)

tool("nt_strategy_disable", "strategy",
     "STOP a strategy. Deliberately does NOT need confirm — the safe direction must never be the "
     "harder one to reach.",
     props={"strategy": _s("name fragment"), "chart": _s("chart title fragment")},
     required=["strategy"],
     argv=lambda a: ["--disable", a["strategy"]] + _flag(a, "chart", "--chart"), timeout=300)

# ── build / lifecycle ───────────────────────────────────────────────────────────────────────────
tool("nt_compile", "compile",
     "Compile with NT's OWN compiler and return real Roslyn {file,line,code,message}. This is the "
     "authoritative check. ⚠ Marked mutating because a recompile can orphan bar-type seams on a live "
     "chart — name a sentry unless you mean this box.",
     mutates=True, timeout=300)

tool("nt_restart", "restart",
     "Restart NT via a /IT scheduled task so it reaches the interactive session. ⛔ NEVER on the "
     "operator's trading box — that is an ALWAYS-STOP. Pass host.",
     props={"task": _s("Scheduled Task created with /IT, e.g. SentinelLaunchNT"),
            "wait": _i("seconds to wait for NT to come back")},
     mutates=True, timeout=600,
     argv=lambda a: _flag(a, "task", "--task") + _flag(a, "wait", "--wait"))

# ── replay / data ───────────────────────────────────────────────────────────────────────────────
tool("nt_playback", "playbackctl", "Read Playback state, or discover its API with api=true.",
     props={"api": _b("reflect available members")},
     argv=lambda a: (["--api"] if a.get("api") else []))

tool("nt_stage_list", "stage", "List staged vs parked .nrd day files. Read-only, and the right first "
     "call on a box you did not set up.",
     props={"instrument": _s("e.g. 'GC 02-26'")}, required=["instrument"],
     argv=lambda a: ["--instrument", a["instrument"], "--list"])

tool("nt_stage", "stage",
     "Position a replay by STAGING day files and PARKING the rest. ⭐ This is how you position a "
     "replay — `--seek` only moves a displayed clock and never the tape. Refuses staging nothing, and "
     "refuses a day present on neither side.",
     props={"instrument": _s("e.g. 'GC 02-26'"), "day": _s("yyyymmdd"),
            "restore": _b("undo: restore everything parked")},
     required=["instrument"], mutates=True, timeout=300,
     argv=lambda a: (["--instrument", a["instrument"]] + _flag(a, "day", "--day")
                     + (["--restore"] if a.get("restore") else [])))

# ── fleet ───────────────────────────────────────────────────────────────────────────────────────
tool("nt_fleet", "fleet", "One read across every box in fleet.conf. UNREACHABLE is its own outcome.",
     timeout=600)
tool("nt_versions", "versions", "Deployed-vs-source bridge DRIFT per box.", timeout=600)
tool("nt_builds", "builds",
     "Digest the .cs SOURCE on every box and judge the fleet against itself. ⚠ Consensus finds the "
     "ODD box, not the RIGHT one — the majority once ran the stale build.", timeout=600)
tool("nt_corpus", "corpus",
     "Corpus size AND FRESHNESS per box. ⭐ A big count with a stale timestamp is a bake that DIED, "
     "indistinguishable from a healthy one if you only count files.", timeout=600)

BY_NAME = {t["name"]: t for t in TOOLS}
