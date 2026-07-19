from pathlib import Path

from nt8bridge import peek


def test_run_peek_writes_peek_request(monkeypatch):
    captured = {}
    monkeypatch.setattr(peek.ntio, "ensure_bridge_dirs", lambda: (Path("trig"), Path("res")))
    monkeypatch.setattr(peek, "new_request_id", lambda: "rid1")
    monkeypatch.setattr(peek.ntio, "atomic_write_json", lambda p, o: captured.update(path=p, obj=o))
    monkeypatch.setattr(peek.ntio, "poll_for_json", lambda p, timeout=30.0: {"id": "rid1", "status": "ok", "metrics": None})
    out = peek.run_peek()
    assert captured["obj"] == {"id": "rid1", "kind": "peek"}
    assert captured["path"].name == "peek_rid1.json"
    assert out["status"] == "ok"
