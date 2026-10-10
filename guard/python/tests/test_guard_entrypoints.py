from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parents[1]


@pytest.mark.parametrize("script,words", [
    ("backup_export.py", "Save every NinjaTrader strategy"),
    ("backup_restore.py", "Put dropped strategies back"),
    ("chart_relink.py", "Put strategies back on their charts"),
])
def test_entry_scripts_run_from_any_folder(script, words, tmp_path):
    p = subprocess.run([sys.executable, str(HERE / script), "--help"], capture_output=True, text=True,
                       cwd=tmp_path)
    assert p.returncode == 0, p.stderr
    assert words in p.stdout
