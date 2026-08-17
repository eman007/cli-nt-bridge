r"""test_server — drive the server over real stdio and assert its REFUSALS fire.

⛔ THE POINT IS THE NEGATIVE CASES. A tool layer whose safety is "the agent will pass confirm" is not
a safety layer. This project has shipped a guard that read as working and measured nothing more than
once — an inert Stop-hook regex, an `AttachExisting` that returned 0 forever — so each refusal here is
provoked and required to fire:

  · a mutating tool called WITHOUT confirm            -> refused
  · a live-sounding account name                      -> refused, even WITH confirm
  · an unknown argument key                           -> refused, not silently dropped
  · a missing required argument                       -> refused

And the positive control: a read-only tool must succeed, or the harness is testing nothing. Runs
offline — it never needs NinjaTrader, because every case above is decided at the seam, before a
subprocess is spawned. The one case that does spawn is marked and skipped when NT is absent.
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)


def rpc(*messages):
    """Feed messages to a fresh server over stdio; return the parsed responses."""
    payload = "".join(json.dumps(m) + "\n" for m in messages)
    p = subprocess.run([sys.executable, "-m", "sentinel_mcp.server"],
                       input=payload, capture_output=True, text=True, cwd=REPO, timeout=180)
    out = []
    for line in (p.stdout or "").splitlines():
        line = line.strip()
        if line:
            out.append(json.loads(line))
    return out, (p.stderr or "")


def body(resp):
    return json.loads(resp["result"]["content"][0]["text"])


def main():
    fails = []

    def check(label, cond, detail=""):
        print("  %-58s %s" % (label, "ok" if cond else "FAIL " + detail))
        if not cond:
            fails.append(label)

    print("\n== protocol ==")
    resp, err = rpc({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}},
                    {"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
    check("initialize returns a serverInfo", len(resp) >= 1
          and resp[0]["result"]["serverInfo"]["name"] == "sentinel-nt8", repr(resp[:1]))
    tools = resp[1]["result"]["tools"] if len(resp) > 1 else []
    check("tools/list returns tools (>=15)", len(tools) >= 15, "got %d" % len(tools))
    check("every tool has an inputSchema", all("inputSchema" in t for t in tools))
    check("stdout carried no stray prints", all(
        l.strip().startswith("{") for l in (
            subprocess.run([sys.executable, "-m", "sentinel_mcp.server"],
                           input=json.dumps({"jsonrpc": "2.0", "id": 1, "method": "ping"}) + "\n",
                           capture_output=True, text=True, cwd=REPO).stdout or ""
        ).splitlines() if l.strip()))

    print("\n== refusals (each MUST fire) ==")
    r, _ = rpc({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                "params": {"name": "nt_compile", "arguments": {}}})
    b = body(r[0])
    check("mutating tool without confirm is REFUSED", b.get("refused") is True, json.dumps(b))

    r, _ = rpc({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                "params": {"name": "nt_order_place", "arguments": {
                    "name": "MyLiveFundedAccount", "instrument": "MNQ 06-26", "side": "Buy",
                    "type": "Market", "quantity": 1, "confirm": True}}})
    b = body(r[0])
    check("live-sounding account REFUSED even with confirm", b.get("refused") is True, json.dumps(b))
    check("  ...and the refusal explains why", "ALWAYS-STOP" in b.get("error", ""))

    r, _ = rpc({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                "params": {"name": "nt_orders", "arguments": {"acount": "Sim101"}}})
    b = body(r[0])
    check("typo'd argument key is REFUSED, not dropped",
          "unknown argument" in b.get("error", ""), json.dumps(b))

    r, _ = rpc({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                "params": {"name": "nt_stage_list", "arguments": {}}})
    b = body(r[0])
    check("missing required argument is REFUSED",
          "missing required" in b.get("error", ""), json.dumps(b))

    print("\n== the allowlist accepts what it should ==")
    from sentinel_mcp.tools import account_is_allowed
    for acct, want in (("Sim101", True), ("Playback101", True), ("SIM-ALPHA-01", True),
                       ("Apex-12345", False), ("", True), (None, True)):
        got, _ = account_is_allowed(acct)
        check("account_is_allowed(%r) == %s" % (acct, want), got == want)

    print("\n== argv is a LIST, never a shell string (the whole reason this exists) ==")
    from sentinel_mcp.tools import BY_NAME
    nasty = r"C:\Users\Administrator\Documents\NinjaTrader 8\db\NinjaTrader.sqlite"
    argv = BY_NAME["nt_log"]["argv"]({"file": nasty, "grep": "Auto Rollover Notification"})
    check("a path with a space survives as ONE argv element", nasty in argv, repr(argv))
    check("a grep pattern with spaces survives as ONE element",
          "Auto Rollover Notification" in argv, repr(argv))
    check("no element contains a shell metacharacter we introduced",
          not any(c in e for e in argv for c in ('"', "'", ";", "|", "&")), repr(argv))

    print("\n== positive control: a read-only tool actually dispatches ==")
    r, _ = rpc({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                "params": {"name": "nt_selfcheck", "arguments": {}}})
    b = body(r[0])
    dispatched = "argv" in b and b["argv"][:2] == ["-m", "nt8bridge"]
    check("read-only tool reached the bridge (NT need not be up)", dispatched, json.dumps(b)[:200])

    print()
    if fails:
        print("RESULT: FAIL — %d check(s): %s" % (len(fails), "; ".join(fails)))
        return 1
    print("RESULT: PASS — protocol, every refusal fired, and argv stays a list")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
