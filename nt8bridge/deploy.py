"""Atomic deploy of a .cs into the correct NT8 bin/Custom subfolder."""
from __future__ import annotations

import os
from pathlib import Path

from nt8bridge import ntio

_SUBFOLDER = {"strategy": "Strategies", "indicator": "Indicators", "addon": "AddOns"}


def dest_for(name: str, kind: str) -> Path:
    if kind not in _SUBFOLDER:
        raise ValueError(f"Unknown kind '{kind}'; expected one of {list(_SUBFOLDER)}")
    return ntio.nt8_root() / "bin" / "Custom" / _SUBFOLDER[kind] / name


def deploy(src, kind: str) -> Path:
    src = Path(src)
    dest = dest_for(src.name, kind)
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(dest.suffix + ".tmp")
    tmp.write_bytes(src.read_bytes())
    os.replace(tmp, dest)
    return dest
