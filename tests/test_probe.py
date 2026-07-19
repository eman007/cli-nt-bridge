from pathlib import Path

from nt8bridge import probe


def test_run_probe_writes_probe_request(monkeypatch):
    captured = {}
    monkeypatch.setattr(probe.ntio, "ensure_bridge_dirs", lambda: (Path("trig"), Path("res")))
    monkeypatch.setattr(probe, "new_request_id", lambda: "rid2")
    monkeypatch.setattr(probe.ntio, "atomic_write_json", lambda p, o: captured.update(obj=o))
    monkeypatch.setattr(probe.ntio, "poll_for_json", lambda p, timeout=30.0: {"id": "rid2", "status": "ok"})
    out = probe.run_probe()
    assert captured["obj"] == {"id": "rid2", "kind": "probe"}
    assert out["status"] == "ok"
