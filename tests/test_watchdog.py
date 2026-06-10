from nt8bridge import watchdog


def test_is_hung_when_process_gone(monkeypatch):
    monkeypatch.setattr(watchdog, "nt8_running", lambda process_name="NinjaTrader.exe": False)
    assert watchdog.is_hung(60) is True


def test_not_hung_when_running_and_fresh(monkeypatch):
    monkeypatch.setattr(watchdog, "nt8_running", lambda process_name="NinjaTrader.exe": True)
    monkeypatch.setattr(watchdog, "heartbeat_age_sec", lambda now=None: 5.0)
    assert watchdog.is_hung(60) is False


def test_hung_when_heartbeat_stale(monkeypatch):
    monkeypatch.setattr(watchdog, "nt8_running", lambda process_name="NinjaTrader.exe": True)
    monkeypatch.setattr(watchdog, "heartbeat_age_sec", lambda now=None: 120.0)
    assert watchdog.is_hung(60) is True


def test_no_false_restart_when_no_heartbeat(monkeypatch):
    monkeypatch.setattr(watchdog, "nt8_running", lambda process_name="NinjaTrader.exe": True)
    monkeypatch.setattr(watchdog, "heartbeat_age_sec", lambda now=None: None)
    assert watchdog.is_hung(60) is False


def test_heartbeat_age_uses_mtime(monkeypatch, tmp_path):
    nt8 = tmp_path / "nt8"
    (nt8 / "NT8Bridge" / "result").mkdir(parents=True)
    hb = nt8 / "NT8Bridge" / "result" / "heartbeat.json"
    hb.write_text("{}")
    monkeypatch.setenv("NT8_DIR", str(nt8))
    import os as _os

    age = watchdog.heartbeat_age_sec(now=_os.path.getmtime(hb) + 42.0)
    assert abs(age - 42.0) < 0.01


def test_watch_restarts_until_give_up(monkeypatch):
    monkeypatch.setattr(watchdog, "is_hung", lambda t, now=None: True)
    calls = []
    monkeypatch.setattr(watchdog, "restart_nt8", lambda exe: calls.append(exe))
    monkeypatch.setattr(watchdog.time, "sleep", lambda s: None)
    out = watchdog.watch(threshold_sec=60, interval_sec=0, max_restarts=2, _loops=5)
    assert out["action"] == "give_up"
    assert out["restarts"] == 2
    assert len(calls) == 2


def test_watch_no_restart_when_healthy(monkeypatch):
    monkeypatch.setattr(watchdog, "is_hung", lambda t, now=None: False)
    calls = []
    monkeypatch.setattr(watchdog, "restart_nt8", lambda exe: calls.append(exe))
    monkeypatch.setattr(watchdog.time, "sleep", lambda s: None)
    out = watchdog.watch(threshold_sec=60, interval_sec=0, _loops=3)
    assert out["action"] == "stop"
    assert calls == []
