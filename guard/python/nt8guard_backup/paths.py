"""Where NinjaTrader and NT8 Guard keep their files, found the way Windows reports them.

Documents can be redirected (OneDrive, another drive), so `~/Documents` is not necessarily where
NinjaTrader's folder is. Windows' known-folder API is the authority; NT8_DIR and NT8GUARD_HOME
override it for tests and unusual setups.
"""
from __future__ import annotations

import ctypes
import os
import subprocess
import uuid
from datetime import datetime, timezone
from pathlib import Path

FOLDERID_DOCUMENTS = uuid.UUID("FDD39AD0-238F-46AF-ADB4-6C85480369C7")
FOLDERID_LOCAL_APPDATA = uuid.UUID("F1B32785-6FBA-4FCF-9D55-7B8E7F157091")


class _GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32), ("Data2", ctypes.c_uint16),
                ("Data3", ctypes.c_uint16), ("Data4", ctypes.c_ubyte * 8)]


def known_folder(folder_id: uuid.UUID) -> Path | None:
    """The path Windows reports for a known folder; None off Windows or on failure."""
    if os.name != "nt":
        return None
    guid = _GUID.from_buffer_copy(folder_id.bytes_le)
    out = ctypes.c_void_p()
    hr = ctypes.windll.shell32.SHGetKnownFolderPath(ctypes.byref(guid), 0, None, ctypes.byref(out))
    try:
        if hr != 0 or not out.value:
            return None
        return Path(ctypes.wstring_at(out.value))
    finally:
        ctypes.windll.ole32.CoTaskMemFree(out)


def nt8_data_dir(override: str | os.PathLike | None = None) -> Path:
    """NinjaTrader's user folder: explicit override, then NT8_DIR, then <Documents>\\NinjaTrader 8."""
    if override:
        return Path(override)
    env = os.environ.get("NT8_DIR")
    if env:
        return Path(env)
    docs = known_folder(FOLDERID_DOCUMENTS) or Path.home() / "Documents"
    return docs / "NinjaTrader 8"


def guard_home(override: str | os.PathLike | None = None) -> Path:
    """NT8 Guard's own folder: explicit override, then NT8GUARD_HOME, then %LOCALAPPDATA%\\NT8Guard."""
    if override:
        return Path(override)
    env = os.environ.get("NT8GUARD_HOME")
    if env:
        return Path(env)
    local = known_folder(FOLDERID_LOCAL_APPDATA) or Path(
        os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    return local / "NT8Guard"


def backups_dir(home: Path) -> Path:
    return home / "state" / "backups"


def is_live_nt8_dir(nt8: Path) -> bool:
    """True when `nt8` is the folder the running NinjaTrader uses (the one Guard would find by itself)."""
    try:
        return Path(nt8).resolve() == nt8_data_dir().resolve()
    except OSError:
        return True


def file_in_use(path: Path) -> bool:
    """True when any process has the file open. NinjaTrader holds its database open while it runs, so
    this catches a running NT8 however its folder is spelled (mapped drive, subst, another user path).
    Asks Windows for an exclusive open: a sharing violation means someone else has it."""
    if os.name != "nt" or not Path(path).exists():
        return False
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.CreateFileW.restype = ctypes.c_void_p
    k32.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                                ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    k32.CloseHandle.argtypes = [ctypes.c_void_p]
    generic_read, no_sharing, open_existing, normal = 0x80000000, 0, 3, 0x80
    handle = k32.CreateFileW(str(path), generic_read, no_sharing, None, open_existing, normal, None)
    if handle is None or handle == ctypes.c_void_p(-1).value:
        return ctypes.get_last_error() == 32          # ERROR_SHARING_VIOLATION
    k32.CloseHandle(handle)
    return False


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


_START_PS = ("$p = Get-Process NinjaTrader -ErrorAction SilentlyContinue | Select-Object -First 1; "
             "if ($p) { $p.StartTime.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }")


def nt8_start_utc(runner=subprocess.run) -> str | None:
    """Start time of the running NinjaTrader.exe, ISO UTC; None when it is not running or unknown."""
    try:
        p = runner(["powershell", "-NoProfile", "-NonInteractive", "-Command", _START_PS],
                   capture_output=True, text=True, timeout=30)
    except (OSError, subprocess.SubprocessError):
        return None
    out = (p.stdout or "").strip()
    return out if len(out) == 20 and out.endswith("Z") else None


def nt8_running(runner=subprocess.run) -> bool:
    """True when NinjaTrader.exe is running - or when that cannot be told (assume the worst)."""
    try:
        p = runner(["tasklist", "/FI", "IMAGENAME eq NinjaTrader.exe", "/NH"],
                   capture_output=True, text=True, timeout=15)
    except (OSError, subprocess.SubprocessError):
        return True
    return "NinjaTrader.exe" in (p.stdout or "")
