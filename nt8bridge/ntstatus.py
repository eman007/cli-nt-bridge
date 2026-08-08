"""Is the code NinjaTrader is RUNNING the code that is on disk?

WHY THIS EXISTS
    2026-08-02. A box ran an AddOn build for 33 minutes while the source on disk said a newer
    version — a deploy had copied source without rebuilding the assembly, and NT had not been
    restarted. Every conclusion drawn from that run was worthless. The version was displayed on
    screen the entire time and went unread.

    Comparing PROCESS START against ASSEMBLY BUILD makes that failure impossible to miss, and
    unlike a chip on a panel it can be read across a fleet in one command.

TWO SOURCES ON PURPOSE
    The AddOn answers from inside NinjaTrader, because only that process knows which assembly it
    actually loaded. This module ALSO stats the DLL on disk directly. The two can disagree — and
    that disagreement is the finding, not an error.

    The filesystem half also works when the AddOn does not answer at all, which is exactly when you
    most want an answer: a wedged or not-yet-signed-in NT still has a process start time and a DLL
    mtime. A timeout therefore degrades to a partial result rather than to nothing.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "ntstatus"}
  response: {"id": str, "status": "ok"|"error", "ts": str, "pid": int,
             "processStartUtc": str, "ntVersion": str, "userDataDir": str,
             "loadedAssembly": {"version": str|None, "location": str|None,
                                "inMemory": bool, "builtUtc": str|None},
             "dllOnDisk": {"path": str, "builtUtc": str|None},
             "assemblyOlderThanDisk": bool, "errors": [...]}
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class NtStatus:
    ok: bool
    stale: bool = False
    reason: str = ""
    pid: int | None = None
    process_start: str | None = None
    dll_built: str | None = None


def build_ntstatus_request(request_id: str) -> dict:
    return {"id": request_id, "kind": "ntstatus"}


def custom_dll_path() -> Path:
    return ntio.nt8_root() / "bin" / "Custom" / "NinjaTrader.Custom.dll"


def dll_built_utc() -> datetime | None:
    """Build time of NinjaTrader.Custom.dll on disk, or None if it is not there."""
    p = custom_dll_path()
    try:
        return datetime.fromtimestamp(p.stat().st_mtime, tz=timezone.utc)
    except OSError:
        return None


def _parse(ts: str | None) -> datetime | None:
    if not ts:
        return None
    try:
        dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
    except ValueError:
        return None
    return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)


def assess(payload: dict) -> NtStatus:
    """Turn the raw payload into the one judgement callers actually want.

    STALE means the assembly on disk was built AFTER this NinjaTrader process started, so the
    running process cannot be executing it. That is the 33-minute-wasted-cell condition, and the
    fix for it is a restart, not another deploy.
    """
    if payload.get("status") != "ok":
        return NtStatus(ok=False, reason="AddOn returned status=" + str(payload.get("status")))

    start = _parse(payload.get("processStartUtc"))
    disk = _parse((payload.get("dllOnDisk") or {}).get("builtUtc"))
    if start is None or disk is None:
        return NtStatus(
            ok=True,
            stale=False,
            reason="cannot compare (missing process start or DLL build time)",
            pid=payload.get("pid"),
            process_start=payload.get("processStartUtc"),
            dll_built=(payload.get("dllOnDisk") or {}).get("builtUtc"),
        )

    stale = disk > start
    mins = abs((disk - start).total_seconds()) / 60.0
    reason = (
        f"DLL built {mins:.0f} min AFTER NT started — NT is running older code; restart it"
        if stale
        else f"NT started {mins:.0f} min after the DLL was built — running current code"
    )
    return NtStatus(
        ok=True,
        stale=stale,
        reason=reason,
        pid=payload.get("pid"),
        process_start=payload.get("processStartUtc"),
        dll_built=(payload.get("dllOnDisk") or {}).get("builtUtc"),
    )


def run_ntstatus(timeout: float = 15.0) -> dict:
    """Read process/assembly identity from the in-NT8 AddOn.

    Raises TimeoutError if the AddOn does not respond; callers that want the degraded filesystem
    answer should catch it and fall back to `dll_built_utc()`.
    """
    trigger, result = ntio.ensure_bridge_dirs()
    request_id = new_request_id()
    ntio.atomic_write_json(
        trigger / f"ntstatus_{request_id}.json",
        build_ntstatus_request(request_id),
    )
    return ntio.poll_for_json(result / f"ntstatus_{request_id}.json", timeout=timeout)
