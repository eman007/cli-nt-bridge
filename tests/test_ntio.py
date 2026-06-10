import json
import os

import pytest

from nt8bridge import ntio


def test_nt8_root_honors_env(monkeypatch, tmp_path):
    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    assert ntio.nt8_root() == tmp_path


def test_bridge_dirs_created(monkeypatch, tmp_path):
    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    trigger, result = ntio.ensure_bridge_dirs()
    assert trigger.is_dir() and trigger.name == "trigger"
    assert result.is_dir() and result.name == "result"
    assert trigger.parent.name == "NT8Bridge"


def test_atomic_write_json_roundtrip_no_tmp_left(tmp_path):
    target = tmp_path / "out.json"
    ntio.atomic_write_json(target, {"a": 1})
    assert json.loads(target.read_text()) == {"a": 1}
    assert list(tmp_path.glob("*.tmp")) == []


def test_poll_for_json_returns_when_present(tmp_path):
    target = tmp_path / "r.json"
    ntio.atomic_write_json(target, {"status": "ok"})
    assert ntio.poll_for_json(target, timeout=1.0, interval=0.05) == {"status": "ok"}


def test_poll_for_json_times_out(tmp_path):
    with pytest.raises(TimeoutError):
        ntio.poll_for_json(tmp_path / "missing.json", timeout=0.2, interval=0.05)
