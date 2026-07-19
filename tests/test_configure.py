from pathlib import Path

from nt8bridge import configure


def test_run_configure_wraps_config_and_extracts_params(monkeypatch):
    captured = {}
    cfg = {"typeName": "MyStrat", "params": {"Fast": 5}}
    monkeypatch.setattr(configure.ntio, "ensure_bridge_dirs", lambda: (Path("trig"), Path("res")))
    monkeypatch.setattr(configure, "new_request_id", lambda: "rid3")
    monkeypatch.setattr(configure.ntio, "atomic_write_json", lambda p, o: captured.update(obj=o))
    monkeypatch.setattr(configure.ntio, "poll_for_json", lambda p, timeout=30.0: {"status": "ok", "applied": []})
    out = configure.run_configure(cfg)
    assert captured["obj"]["kind"] == "configure"
    assert captured["obj"]["config"] == cfg
    assert captured["obj"]["params"] == {"Fast": 5}
    assert out["status"] == "ok"


def test_run_configure_params_default_empty(monkeypatch):
    captured = {}
    monkeypatch.setattr(configure.ntio, "ensure_bridge_dirs", lambda: (Path("t"), Path("r")))
    monkeypatch.setattr(configure, "new_request_id", lambda: "rid4")
    monkeypatch.setattr(configure.ntio, "atomic_write_json", lambda p, o: captured.update(obj=o))
    monkeypatch.setattr(configure.ntio, "poll_for_json", lambda p, timeout=30.0: {"status": "ok"})
    configure.run_configure({"typeName": "X"})
    assert captured["obj"]["params"] == {}
