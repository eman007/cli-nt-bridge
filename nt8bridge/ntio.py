"""File-based IPC + NT8 path resolution. Shared by every other module."""
from __future__ import annotations

import json
import os
import time
from pathlib import Path


def nt8_root() -> Path:
    """NinjaTrader 8 user data dir. Override with NT8_DIR (used in tests)."""
    override = os.environ.get("NT8_DIR")
    if override:
        return Path(override)
    return Path.home() / "Documents" / "NinjaTrader 8"


def bridge_dir() -> Path:
    return nt8_root() / "NT8Bridge"


def ensure_bridge_dirs() -> tuple[Path, Path]:
    """Create <NT8>/NT8Bridge/{trigger,result} and return (trigger, result)."""
    trigger = bridge_dir() / "trigger"
    result = bridge_dir() / "result"
    trigger.mkdir(parents=True, exist_ok=True)
    result.mkdir(parents=True, exist_ok=True)
    return trigger, result


def atomic_write_json(target: Path, payload: dict) -> None:
    """Write JSON via temp-then-rename so readers never see a partial file."""
    target = Path(target)
    tmp = target.with_suffix(target.suffix + ".tmp")
    tmp.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    os.replace(tmp, target)


def poll_for_json(target: Path, timeout: float, interval: float = 0.25) -> dict:
    """Block until `target` exists and parses as JSON, or raise TimeoutError."""
    target = Path(target)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if target.exists():
            try:
                return json.loads(target.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass  # still being written; retry
        time.sleep(interval)
    raise TimeoutError(f"No valid JSON at {target} within {timeout}s")
