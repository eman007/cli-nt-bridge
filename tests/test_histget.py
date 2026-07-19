from datetime import date, datetime

from nt8bridge import histget


def test_iter_dates_inclusive():
    days = list(histget._iter_dates("20260706", "20260709"))
    assert days[0] == date(2026, 7, 6)
    assert days[-1] == date(2026, 7, 9)
    assert len(days) == 4


def test_run_histget_skips_saturdays_and_existing(monkeypatch, tmp_path):
    calls = []

    def fake_download(instrument, ds, timeout=600.0):
        calls.append(ds)
        return {"status": "ok", "exists": True}

    monkeypatch.setattr(histget, "download_one", fake_download)
    inst = "MNQ 09-26"
    (tmp_path / inst).mkdir(parents=True)
    (tmp_path / inst / "20260706.nrd").write_bytes(b"x")   # pre-existing -> must be skipped

    out = histget.run_histget(instrument=inst, from_date="20260703", to_date="20260709",
                              replay_dir=tmp_path)

    # No Saturday was ever attempted (weekday()==5), regardless of the calendar.
    for ds in calls:
        assert datetime.strptime(ds, "%Y%m%d").weekday() != 5
    # The pre-existing date is skipped, never downloaded.
    assert "20260706" in out["skipped"]
    assert "20260706" not in calls
    # count matches the downloaded list.
    assert out["count"] == len(out["downloaded"])
    assert out["instrument"] == inst
