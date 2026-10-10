from __future__ import annotations

import os
import subprocess
import types
from pathlib import Path

import pytest

from nt8guard_backup import paths


def runner(stdout: str = "", exc: Exception | None = None):
    def run(cmd, **kw):
        if exc:
            raise exc
        return types.SimpleNamespace(stdout=stdout, returncode=0)
    return run


def test_override_beats_env(monkeypatch, tmp_path):
    monkeypatch.setenv("NT8_DIR", str(tmp_path / "env"))
    assert paths.nt8_data_dir(tmp_path / "explicit") == tmp_path / "explicit"


def test_env_beats_known_folder(monkeypatch, tmp_path):
    monkeypatch.setenv("NT8_DIR", str(tmp_path / "env"))
    assert paths.nt8_data_dir() == tmp_path / "env"


def test_default_follows_redirected_documents(monkeypatch):
    monkeypatch.delenv("NT8_DIR", raising=False)
    redirected = Path(r"D:\OneDrive\Documents")
    monkeypatch.setattr(paths, "known_folder",
                        lambda fid: redirected if fid == paths.FOLDERID_DOCUMENTS else None)
    assert paths.nt8_data_dir() == redirected / "NinjaTrader 8"


@pytest.mark.skipif(os.name != "nt", reason="Windows known folders")
def test_known_folder_documents_is_a_real_folder():
    p = paths.known_folder(paths.FOLDERID_DOCUMENTS)
    assert p is not None and p.is_dir()


def test_guard_home_default_is_local_appdata(monkeypatch):
    monkeypatch.delenv("NT8GUARD_HOME", raising=False)
    local = Path(r"C:\Users\someone\AppData\Local")
    monkeypatch.setattr(paths, "known_folder",
                        lambda fid: local if fid == paths.FOLDERID_LOCAL_APPDATA else None)
    assert paths.guard_home() == local / "NT8Guard"
    assert paths.backups_dir(paths.guard_home()) == local / "NT8Guard" / "state" / "backups"


def test_is_live_nt8_dir(monkeypatch, tmp_path):
    monkeypatch.setenv("NT8_DIR", str(tmp_path / "live"))
    assert paths.is_live_nt8_dir(tmp_path / "live")
    assert not paths.is_live_nt8_dir(tmp_path / "copy")


def test_nt8_start_parsed():
    assert paths.nt8_start_utc(runner("2026-10-08T03:05:02Z\r\n")) == "2026-10-08T03:05:02Z"


def test_nt8_start_none_when_not_running():
    assert paths.nt8_start_utc(runner("")) is None


def test_nt8_start_none_when_powershell_fails():
    assert paths.nt8_start_utc(runner(exc=FileNotFoundError("powershell"))) is None
    assert paths.nt8_start_utc(runner(exc=subprocess.TimeoutExpired("powershell", 30))) is None


def test_nt8_running():
    assert paths.nt8_running(runner("NinjaTrader.exe   1234 Console   1   900,000 K"))
    assert not paths.nt8_running(runner("INFO: No tasks are running which match the specified criteria."))
    assert paths.nt8_running(runner(exc=OSError("tasklist")))   # cannot tell -> assume running


@pytest.mark.skipif(os.name != "nt", reason="Windows share modes")
def test_file_in_use_sees_any_open_handle(tmp_path):
    db = tmp_path / "NinjaTrader.sqlite"
    db.write_bytes(b"x")
    assert not paths.file_in_use(db)
    with open(db, "rb"):                                  # NT8 holds its database open while it runs
        assert paths.file_in_use(db)
    assert not paths.file_in_use(db)
    assert not paths.file_in_use(tmp_path / "missing.sqlite")


def test_utc_now_format():
    s = paths.utc_now()
    assert len(s) == 20 and s[10] == "T" and s.endswith("Z")
