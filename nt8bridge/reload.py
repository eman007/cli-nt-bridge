"""reload — make NinjaTrader actually LOAD the code, not just check it.

WHY THIS EXISTS
---------------
`compile` invokes NinjaTrader's compiler with `checkCompileOnly=true`. That answers "does this build?"
and nothing else: no assembly is emitted, so a brand-new indicator/strategy/AddOn type does not appear
in the pickers and edited code is not running. Every headless edit loop therefore ended with a human
alt-tabbing to the NinjaScript Editor and pressing F5 — which is the one step the bridge existed to
remove.

`reload` runs the same compiler with `checkCompileOnly=false`. NinjaTrader emits and swaps in the new
NinjaScript assembly exactly as F5 does.

⚠ RELOAD IS DISRUPTIVE, ON PURPOSE IT IS A SEPARATE COMMAND
    It restarts indicators, can interrupt a running strategy, and orphans bars-type instances (those
    are recreated only by an NT process restart, not by a reload or a chart reload). Do not fire it
    into a live bake or a running automated session. Keeping it separate from `compile` means it can
    never happen as a side effect of validating code.

CONTRACT
    request : {"id": str, "kind": "reload"}
    response: {"id","status":"ok"|"error","errors":[...],"assemblyReloaded":bool}

`assemblyReloaded` is now meaningful: true only when a non-check build succeeded. It used to be
hardcoded false, so a caller could never tell whether its code was live.
"""
from __future__ import annotations

import uuid

from nt8bridge import ntio
from nt8bridge.compile import CompileResult, parse_compile_response


def run_reload(timeout: float = 240.0) -> CompileResult:
    """Full build + assembly swap. Blocks until NinjaTrader answers.

    The default timeout is higher than `compile`'s: emitting and swapping the assembly is real work,
    and on a large tree with many charts open the reload itself takes noticeably longer than the
    syntax check.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = uuid.uuid4().hex
    ntio.atomic_write_json(
        trigger / f"reload_{request_id}.json",
        {"id": request_id, "kind": "reload"},
    )
    payload = ntio.poll_for_json(result / f"reload_{request_id}.json", timeout=timeout)
    return parse_compile_response(payload)
