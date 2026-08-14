r"""server — MCP over stdio, dispatching to `nt8bridge` through an argv LIST and never a shell.

PROTOCOL. JSON-RPC 2.0, one message per line on stdin/stdout, hand-rolled because the whole server is
three methods (`initialize`, `tools/list`, `tools/call`) and a dependency would be the larger risk.

⛔ TWO RULES THAT ARE LOAD-BEARING, NOT STYLE

1. **stdout carries protocol ONLY.** A stray print corrupts the stream and the failure looks like a
   client bug rather than a server one. Every diagnostic goes to stderr, and the one `print` in this
   file writes framed JSON deliberately.

2. **Never build a command STRING.** `subprocess.run([...], shell=False)` is the entire reason this
   exists: the four quoting failures that motivated it were all a path or a title crossing a shell.
   With an argv list there is no shell to cross.

WHAT IT REPORTS BACK. The bridge already answers in JSON and already distinguishes `succeeded` from
`changed` — a call that resolved and moved nothing FAILED. That distinction is preserved verbatim
rather than flattened into a boolean, because it is the difference between "disabled a strategy" and
"disabled a strategy that was already stopped".
"""
import json
import os
import subprocess
import sys
import time

from . import __version__
from .tools import BY_NAME, TOOLS, account_is_allowed

PROTOCOL_VERSION = "2024-11-05"
PYTHON = os.environ.get("SENTINEL_MCP_PYTHON", sys.executable)
REMOTE_PYTHON = os.environ.get("SENTINEL_MCP_REMOTE_PYTHON", "C:/ntbv/Scripts/python.exe")

# ⚠ MEASURED, not assumed: `--host` is NOT a global option. Exactly five verbs accept it; the rest
# are local-only and die in argparse if you hand them one. Passing --host blindly is what my first
# cut did, and `nt_status host=sentry-1` came back as an argparse usage dump.
HOST_CAPABLE = {"stage", "fleet", "corpus", "versions", "builds"}

# ⚠ ALSO MEASURED: a nonzero exit code does NOT mean failure here. `ntstatus` returns 2 while
# emitting perfectly good JSON; `selfcheck` returns 0. Exit codes are verb-specific, so the PAYLOAD
# decides. Same lesson as the background job whose "exit 0" was the shell's and not the program's:
# read what the thing said, not the envelope it came in.
def _verdict(body, returncode):
    if isinstance(body, dict):
        if isinstance(body.get("ok"), bool):
            return body["ok"]
        st = body.get("status")
        if isinstance(st, str):
            return st.lower() in ("ok", "success", "succeeded")
    return returncode == 0


def log(msg):
    """stderr only. See rule 1."""
    print("[sentinel-mcp] %s" % msg, file=sys.stderr, flush=True)


def _win_quote(arg):
    r"""Quote ONE argument for a Windows remote shell.

    Windows OpenSSH hands the command to cmd.exe, where a single quote is an ordinary character —
    which is why `--db 'C:/.../NinjaTrader 8/db/x.sqlite'` split into two arguments earlier today and
    `--close 'Auto Rollover Notification'` lost everything after the first space. cmd.exe honours
    DOUBLE quotes, so that is what we use, and embedded double quotes are doubled.
    """
    s = str(arg)
    if s and not any(c in s for c in ' \t"&|<>^()'):
        return s
    return '"' + s.replace('"', '""') + '"'


def run_bridge(verb, extra, host=None, timeout=120):
    extra = [str(x) for x in extra]
    if host and verb in HOST_CAPABLE:
        argv = [PYTHON, "-m", "nt8bridge", verb, "--host", host] + extra
    elif host:
        # No --host on this verb ⇒ run it ON the box over ssh. One quoting layer, applied per
        # argument, rather than a hand-built string that loses a path with a space in it.
        remote = " ".join(_win_quote(x) for x in
                          [REMOTE_PYTHON, "-m", "nt8bridge", verb] + extra)
        argv = ["ssh", "-o", "ConnectTimeout=10", "-o", "BatchMode=yes", host, remote]
    else:
        argv = [PYTHON, "-m", "nt8bridge", verb] + extra
    t0 = time.time()
    try:
        p = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, shell=False)
    except subprocess.TimeoutExpired:
        return {"ok": False, "error": "timeout after %ss" % timeout,
                "argv": argv[1:], "hint": "raise the tool's timeout, or the box is not answering"}
    out = (p.stdout or "").strip()
    body = None
    if out:
        try:
            body = json.loads(out)
        except json.JSONDecodeError:
            # ⚠ Some verbs are human-formatted tables, and an ssh banner can precede the JSON.
            # Try to recover the JSON object; otherwise hand the text back rather than pretending
            # it parsed — a silently empty result is worse than an obviously textual one.
            i, j = out.find("{"), out.rfind("}")
            if 0 <= i < j:
                try:
                    body = json.loads(out[i:j + 1])
                except json.JSONDecodeError:
                    body = {"text": out}
            else:
                body = {"text": out}
    res = {"ok": _verdict(body, p.returncode), "exit_code": p.returncode,
           "elapsed_s": round(time.time() - t0, 2), "argv": argv[1:]}
    if body is not None:
        res["result"] = body
    err = (p.stderr or "").strip()
    if err:
        res["stderr"] = err[-4000:]
    return res


def call_tool(name, args):
    t = BY_NAME.get(name)
    if t is None:
        return {"ok": False, "error": "unknown tool %r" % name,
                "known": sorted(BY_NAME)}
    args = dict(args or {})

    # required
    missing = [k for k in t["schema"]["required"] if k not in args]
    if missing:
        return {"ok": False, "error": "missing required: %s" % ", ".join(missing)}

    # unknown keys are refused rather than ignored — a typo'd key that is silently dropped is how a
    # caller believes it passed a limit price it never passed.
    unknown = [k for k in args if k not in t["schema"]["properties"] and k != "confirm"]
    if unknown:
        return {"ok": False, "error": "unknown argument(s): %s" % ", ".join(sorted(unknown)),
                "accepted": sorted(t["schema"]["properties"])}

    # ⛔ the account allowlist — the contract's first ALWAYS-STOP, asserted in code
    good, why = account_is_allowed(args.get("name"))
    if not good:
        return {"ok": False, "refused": True, "error": why}

    # ⛔ mutation gate
    if t["mutates"] and not args.pop("confirm", False):
        return {"ok": False, "refused": True,
                "error": "%s MUTATES (it can arm an order source, move files, or bounce NT). "
                         "Re-call with confirm=true once you mean it." % name}
    args.pop("confirm", None)

    host = args.pop("host", None)
    try:
        extra = t["argv"](args)
    except Exception as ex:
        return {"ok": False, "error": "could not build arguments: %s" % ex}
    return run_bridge(t["verb"], extra, host=host, timeout=t["timeout"])


def handle(req):
    method = req.get("method")
    rid = req.get("id")
    if method == "initialize":
        return {"jsonrpc": "2.0", "id": rid, "result": {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "sentinel-nt8", "version": __version__},
        }}
    if method in ("notifications/initialized", "initialized"):
        return None
    if method == "tools/list":
        return {"jsonrpc": "2.0", "id": rid, "result": {"tools": [
            {"name": t["name"], "description": t["description"], "inputSchema": t["schema"]}
            for t in TOOLS]}}
    if method == "tools/call":
        p = req.get("params") or {}
        res = call_tool(p.get("name"), p.get("arguments"))
        return {"jsonrpc": "2.0", "id": rid, "result": {
            "content": [{"type": "text", "text": json.dumps(res, indent=2)}],
            "isError": not res.get("ok", False)}}
    if method == "ping":
        return {"jsonrpc": "2.0", "id": rid, "result": {}}
    return {"jsonrpc": "2.0", "id": rid,
            "error": {"code": -32601, "message": "method not found: %s" % method}}


def main():
    log("v%s — %d tools, python=%s" % (__version__, len(TOOLS), PYTHON))
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError as ex:
            log("bad JSON: %s" % ex)
            continue
        try:
            resp = handle(req)
        except Exception as ex:                       # a crash must not kill the session
            log("handler raised: %r" % ex)
            resp = {"jsonrpc": "2.0", "id": req.get("id"),
                    "error": {"code": -32603, "message": "internal error: %s" % ex}}
        if resp is not None:
            sys.stdout.write(json.dumps(resp) + "\n")
            sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
