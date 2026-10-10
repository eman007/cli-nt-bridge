from __future__ import annotations

import base64
import subprocess
import sys
from pathlib import Path

QR = Path(__file__).resolve().parents[1] / "qr_png.py"


def run(env_text):
    env = {"SYSTEMROOT": __import__("os").environ.get("SYSTEMROOT", "")}
    if env_text is not None:
        env["NT8GUARD_QR_TEXT"] = env_text
    return subprocess.run([sys.executable, "-I", str(QR)], capture_output=True, text=True, env=env, timeout=60)


def test_prints_a_png_for_the_text_in_the_environment():
    r = run("https://ntfy.sh/nt8guard-abcdefghijklmnopqrstuv")
    assert r.returncode == 0, r.stderr
    png = base64.b64decode(r.stdout.strip())
    assert png[:8] == b"\x89PNG\r\n\x1a\n"
    assert len(png) > 200


def test_no_text_is_an_error_not_an_empty_code():
    r = run(None)
    assert r.returncode == 1
    assert "NT8GUARD_QR_TEXT" in r.stderr
    assert r.stdout == ""
