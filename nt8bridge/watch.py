"""Account watchdog: detect NAKED (unprotected) positions and force-close them.

An automated strategy can leave a position stranded without a protective stop (it
stops managing the position, or never places the bracket). This is an INDEPENDENT,
out-of-band safety loop: read account state -> for each WATCHED account, for each
open position, check whether a protective STOP order exists; if a position stays
naked past the grace period, flatten it and log WHY.

Safety guards:
- Scoped to an explicit `accounts` allow-list — never touches positions on accounts
  you didn't name (other systems are left alone).
- Grace period before killing — a freshly-entered position has a moment before
  its bracket lands; the watchdog must NOT kill a trade mid-bracket-placement.
- Protection = a working STOP order for the position's instrument (a lone
  profit-target/limit is NOT protection).
"""
from __future__ import annotations

import json
import time

from nt8bridge import account as ntaccount
from nt8bridge import flatten as ntflatten
from nt8bridge import ntio


def has_protective_stop(account_block: dict, instrument: str) -> bool:
    """True iff a working STOP order covers `instrument` (a lone limit is NOT)."""
    for o in account_block.get("workingOrders", []):
        if o.get("instrument") == instrument and "Stop" in (o.get("type") or ""):
            return True
    return False


def scan_once(accounts: list[str], _state_fn=None) -> list[dict]:
    """One read of NT8 truth -> the open positions in `accounts` + protection flag."""
    state_fn = _state_fn or (lambda: ntaccount.parse_account_response(ntaccount.run_account_state("", timeout=15.0)))
    state = state_fn()
    findings = []
    for name in accounts:
        blk = state.account(name)
        if not blk:
            continue
        for p in blk.get("positions", []):
            instr = p.get("instrument")
            findings.append({
                "account": name,
                "instrument": instr,
                "marketPosition": p.get("marketPosition"),
                "quantity": p.get("quantity"),
                "protected": has_protective_stop(blk, instr),
            })
    return findings


def _log_event(event: dict, _now_iso: str | None = None) -> None:
    """Durable record so the operator sees WHY a position was killed."""
    event = dict(event)
    line = json.dumps(event)
    try:
        path = ntio.bridge_dir() / "watchdog_events.jsonl"
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(line + "\n")
    except OSError:
        pass
    print("[watchdog] KILLED NAKED POSITION " + line, flush=True)


def watch(
    accounts: list[str],
    grace_seconds: float = 20.0,
    interval: float = 5.0,
    max_iterations: int | None = None,
    *,
    _scan=None,
    _flatten=None,
    _sleep=time.sleep,
    _now=time.monotonic,
    _log=_log_event,
) -> list[dict]:
    """Run the watchdog. Returns the list of kill events (also for tests).

    Underscore params are injection seams for tests; in production they default
    to the real scan/flatten/sleep/clock.
    """
    if not accounts:
        raise ValueError("watch requires at least one account name")
    scan_fn = _scan or scan_once
    flatten_fn = _flatten or (lambda acct, instr: ntflatten.run_flatten(acct, instr))

    naked_since: dict[tuple, float] = {}
    killed: list[dict] = []
    it = 0
    while max_iterations is None or it < max_iterations:
        it += 1
        findings = scan_fn(accounts)
        seen = set()
        for f in findings:
            key = (f["account"], f["instrument"])
            seen.add(key)
            if f.get("protected"):
                naked_since.pop(key, None)
                continue
            first = naked_since.setdefault(key, _now())
            naked_for = _now() - first
            if naked_for >= grace_seconds:
                result = flatten_fn(f["account"], f["instrument"])
                event = {
                    "account": f["account"],
                    "instrument": f["instrument"],
                    "marketPosition": f.get("marketPosition"),
                    "quantity": f.get("quantity"),
                    "naked_for_s": round(naked_for, 1),
                    "reason": "no protective stop past grace period",
                    "flatten_result": result,
                }
                killed.append(event)
                _log(event)
                naked_since.pop(key, None)
        # drop timers for positions that are gone (flat/closed)
        for key in list(naked_since.keys()):
            if key not in seen:
                naked_since.pop(key, None)
        if max_iterations is None or it < max_iterations:
            if interval > 0:
                _sleep(interval)
    return killed
