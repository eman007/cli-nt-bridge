"""restart — restart the NinjaTrader process.

WHY A COMMAND AND NOT A NOTE IN A RUNBOOK
-----------------------------------------
Some changes are NOT picked up by a compile, a reload, or even a chart reload:

  • **Bars types.** A reload recreates indicators but NOT bars-type instances. They keep executing the
    PRE-reload assembly and publish into that assembly's static state, while everything else reads the
    new one. The write succeeds into an orphaned store, every guard reads healthy, and the only
    symptom is a consumer reporting data "missing" from a publisher that is demonstrably running.
    Nothing in NinjaTrader surfaces this.
  • AddOns that registered menu items or long-lived services at load.

So the rule is: **after any reload on a machine that runs unattended jobs, restart NinjaTrader before
starting one.** That rule has historically lived in a runbook, which is the wrong place for something
whose failure mode is silent and expensive.

MECHANISM
    Prefers a Windows Scheduled Task, because a task created with `/IT` runs in the INTERACTIVE
    session — the only way UI automation works when the caller is an SSH/session-0 shell. Falls back
    to launching the executable directly.

⚠ This does not save your workspace and does not ask. Do not call it with unsaved work or a live
position you are managing by hand.
"""
from __future__ import annotations

import shutil
import subprocess
import time
from pathlib import Path

# No default task name: a scheduled-task name is site-specific, so shipping one as a default would
# silently no-op (or worse, fire something unrelated) on every machine but its author's. Empty means
# "go straight to the executable"; pass --task to use a task you have created yourself.
DEFAULT_TASK = ""
# ⚠ NO DEFAULT EXE, for the same reason there is no default task — and the failure is worse.
#
# Launching `bin\NinjaTrader.exe` directly produces a PROCESS, not a usable PLATFORM: on a box where
# NT is started through a wrapper that supplies credentials, the bare exe stops at the Welcome screen.
# A running-check sees NinjaTrader.exe and reports healthy, so the caller believes it restarted the
# platform and every subsequent headless step fails against a login prompt. Reported upstream from a
# box that launches via an auto-login wrapper. Pass --exe (or better, --task) explicitly.
DEFAULT_EXE = ""


def process_listed() -> tuple[bool | None, str]:
    """Read the process list once. Returns (listed, detail).

    `listed` has THREE values, and the third is why this exists next to is_running():
        True   tasklist lists NinjaTrader.exe
        False  tasklist was read and does not list it
        None   the list could NOT be read - tasklist is missing, timed out (30 s), exited non-zero
               or printed nothing - so nothing is known about the process
    Any NinjaTrader.exe counts: the list does not tell instances apart, so a second NinjaTrader
    next to a dead target keeps a caller waiting instead of stopping it.
    `detail` is what was measured, for the caller's message: the row(s) naming the process, the
    line tasklist printed instead (locale text - "INFO: No tasks are running which match the
    specified criteria." on an English Windows), or the failure.

    Measured 2026-09-02: an unlisted name answers exit 0 plus that one line; an unknown filter
    answers exit 1 with the error on stderr and nothing on stdout; a missing executable raises
    FileNotFoundError and a hung one TimeoutExpired. A caller that reads every one of those as
    "not running" passes a verdict it never measured - the preflight poll of playback_run did,
    and ended a run after one probe with a message asserting an empty process list nobody had seen.
    """
    try:
        r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq NinjaTrader.exe"],
                           capture_output=True, text=True, errors="replace", timeout=30)
    except Exception as e:  # noqa: BLE001 - reported, not swallowed
        return None, f"tasklist could not be run: {type(e).__name__}: {e}"
    rows = [" ".join(ln.split()) for ln in r.stdout.splitlines() if "NinjaTrader.exe" in ln]
    if rows:
        return True, "tasklist lists " + "; ".join(rows)
    text = " ".join(r.stdout.split())
    if r.returncode == 0 and text:
        return False, f"tasklist answered: {text}"
    err = " ".join(r.stderr.split())
    return None, (f"tasklist exited {r.returncode}"
                  + (f", stdout: {text}" if text else ", printed nothing on stdout")
                  + (f", stderr: {err}" if err else ""))


def is_running() -> bool:
    """True when the process list was read and lists NinjaTrader.exe; False otherwise - a list
    that could not be read included. The callers of this bool (stop, wait_for, the restart
    command) act the same on "not seen" and "not there"; a caller that must keep them apart
    reads process_listed() itself (the preflight poll of playback_run does)."""
    return process_listed()[0] is True


def run_task(task: str) -> tuple[bool, str]:
    """Fire a scheduled task. Returns (started, detail)."""
    if not task:
        return False, "no --task given; using the executable directly"
    if not shutil.which("schtasks"):
        return False, "schtasks not available"
    try:
        r = subprocess.run(["schtasks", "/run", "/tn", task],
                           capture_output=True, text=True, errors="replace", timeout=60)
        ok = r.returncode == 0
        return ok, (r.stdout or r.stderr or "").strip()
    except Exception as e:  # noqa: BLE001 - reported, not swallowed
        return False, str(e)


def choose_start(task: str, exe: str) -> tuple[str, str]:
    """Pure: which start mechanism the flags select. Returns (kind, detail).

    Split out of the command so it is testable without a NinjaTrader — the original had no path at all
    for "neither flag given" and silently fell through to a hardcoded exe.
    """
    if task:
        return "task", f"schtasks /run /tn {task}"
    if exe:
        return "exe", f"exec {exe}"
    return "none", ("no --task and no --exe. There is no safe default: a task name is site-specific, "
                    "and a bare exe path can start a process that stops at a login screen. Pass one.")


def stop(timeout: float = 60.0, force_after: float = 25.0) -> tuple[bool, str]:
    """Stop NinjaTrader — graceful close first, terminate only if it will not go. (stopped, detail).

    ⚠ A graceful close can BLOCK on NinjaTrader's own save-workspace prompt, which nothing here can
    answer. That is why the force step exists rather than being an option: a stop that cannot complete
    would otherwise leave the caller believing a restart happened.

    ⚠ Terminating does not save the workspace. `restart` is documented as not for use with unsaved
    work or a hand-managed position.
    """
    if not is_running():
        return True, "not running"
    try:
        subprocess.run(["taskkill", "/IM", "NinjaTrader.exe"],
                       capture_output=True, text=True, errors="replace", timeout=30)
    except Exception as e:  # noqa: BLE001 - reported, not swallowed
        return False, f"graceful close failed to dispatch: {e}"
    if wait_for(False, force_after):
        return True, "closed gracefully"
    try:
        subprocess.run(["taskkill", "/F", "/IM", "NinjaTrader.exe"],
                       capture_output=True, text=True, errors="replace", timeout=30)
    except Exception as e:  # noqa: BLE001
        return False, f"terminate failed to dispatch: {e}"
    remaining = max(1.0, timeout - force_after)
    if wait_for(False, remaining):
        return True, "terminated (did not close gracefully — likely a modal prompt)"
    return False, "still running after graceful close and terminate"


def launch_exe(exe: str) -> tuple[bool, str]:
    if not exe:
        return False, "no --exe given"
    p = Path(exe)
    if not p.exists():
        return False, f"not found: {exe}"
    try:
        subprocess.Popen([str(p)], close_fds=True)
        return True, f"launched {exe}"
    except Exception as e:  # noqa: BLE001
        return False, str(e)


def wait_for(state: bool, timeout: float) -> bool:
    """Block until is_running() == state, or timeout."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if is_running() == state:
            return True
        time.sleep(1.0)
    return False
