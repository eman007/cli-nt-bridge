"""Watchdog — detect a hung/crashed NinjaTrader and restart it.

Two liveness signals:
  1. The NinjaTrader process is present (tasklist).
  2. The AddOn rewrites <NT8>/NT8Bridge/result/heartbeat.json from the MAIN UI
     thread each ~1s. If its mtime goes stale, the UI thread is hung even if the
     process is alive (the deadlock case). Stale beyond `threshold` OR process
     gone => restart.

Restart only recovers NT8 to a running state; it does NOT resume an in-flight
batch (NT8 reopens its last workspace — reconfigure the SA and re-run).

Requires the AddOn's periodic heartbeat (NT8BridgeServer writes it from
Globals.MainThreadDispatcher). Without it, heartbeat goes stale and the watchdog
would false-restart — `is_hung` returns False when no heartbeat file exists so a
not-yet-loaded AddOn never triggers a restart.
"""
from __future__ import annotations

import os
import subprocess
import time

from nt8bridge import ntio

NT8_EXE_DEFAULT = r"C:\Program Files\NinjaTrader 8\bin\NinjaTrader.exe"
PROCESS_NAME = "NinjaTrader.exe"


def nt8_running(process_name: str = PROCESS_NAME) -> bool:
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq " + process_name],
            capture_output=True,
            text=True,
            errors="replace",
        ).stdout
        return process_name.lower() in (out or "").lower()
    except OSError:
        return False


def heartbeat_age_sec(now=None):
    hb = ntio.bridge_dir() / "result" / "heartbeat.json"
    if not hb.exists():
        return None
    now = now if now is not None else time.time()
    return now - os.path.getmtime(hb)


def is_hung(threshold_sec: float, now=None) -> bool:
    if not nt8_running():
        return True
    age = heartbeat_age_sec(now)
    if age is None:
        return False  # AddOn not loaded yet — never false-restart on a missing heartbeat
    return age > threshold_sec


def kill_nt8(process_name: str = PROCESS_NAME) -> None:
    try:
        subprocess.run(["taskkill", "/F", "/IM", process_name],
                       capture_output=True, text=True, errors="replace")
    except OSError:
        pass


def launch_nt8(exe: str = NT8_EXE_DEFAULT) -> None:
    if not os.path.exists(exe):
        raise FileNotFoundError(f"NinjaTrader exe not found: {exe} (pass --exe)")
    subprocess.Popen([exe], close_fds=True)


def restart_nt8(exe: str = NT8_EXE_DEFAULT) -> None:
    kill_nt8()
    time.sleep(3)
    launch_nt8(exe)


def watch(threshold_sec: float = 60.0, interval_sec: float = 10.0,
          exe: str = NT8_EXE_DEFAULT, max_restarts: int = 5, _loops=None) -> dict:
    """Run the watch loop. `_loops` caps iterations (tests); None = forever."""
    restarts = 0
    i = 0
    while _loops is None or i < _loops:
        i += 1
        if is_hung(threshold_sec):
            if restarts >= max_restarts:
                return {"action": "give_up", "restarts": restarts}
            restart_nt8(exe)
            restarts += 1
        time.sleep(interval_sec)
    return {"action": "stop", "restarts": restarts}
