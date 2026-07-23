"""Compile half of the IPC contract (request builder + response parser).

JSON contract (Part 2 AddOn must honor):
  request : {"id": str, "kind": "compile", "typeName": str}
  response: {"id": str, "status": "ok"|"error",
             "errors": [{"file","line","code","message"}],
             "assemblyReloaded": bool, "ts": str}
"""
from __future__ import annotations

import uuid
from dataclasses import dataclass, field

from nt8bridge import ntio


@dataclass
class CompileResult:
    ok: bool
    errors: list[dict] = field(default_factory=list)
    assembly_reloaded: bool = False


def new_request_id() -> str:
    return uuid.uuid4().hex


def build_compile_request(request_id: str, type_name: str) -> dict:
    return {"id": request_id, "kind": "compile", "typeName": type_name}


def parse_compile_response(payload: dict) -> CompileResult:
    return CompileResult(
        ok=payload.get("status") == "ok",
        errors=payload.get("errors", []),
        assembly_reloaded=bool(payload.get("assemblyReloaded", False)),
    )


def run_compile(type_name: str = "", timeout: float = 120.0) -> CompileResult:
    """Drop a compile request for the in-NT8 AddOn and wait for its result.

    `type_name` is accepted for back-compat and ignored: NinjaTrader's compiler
    always builds the whole tree (exactly as F5 does), so there is nothing to scope.

    Raises TimeoutError if the AddOn does not respond within `timeout` (NT8 not
    running, AddOn not compiled, NT8 hung -- or simply a tree that compiles slower
    than the timeout, in which case NT is still working and a retry will succeed).
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"compile_{request_id}.json",
        build_compile_request(request_id, type_name),
    )
    payload = ntio.poll_for_json(result / f"compile_{request_id}.json", timeout=timeout)
    return parse_compile_response(payload)
