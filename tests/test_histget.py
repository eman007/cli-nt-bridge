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
        # A download that really writes. Before the 2026-08-09 fix this fake wrote nothing and the
        # test still passed, because the only assertion was count == len(downloaded) and both were
        # zero. An assertion that holds at zero is not an assertion.
        (tmp_path / instrument).mkdir(parents=True, exist_ok=True)
        (tmp_path / instrument / f"{ds}.nrd").write_bytes(b"fresh")
        return {"status": "ok", "exists": True}

    monkeypatch.setattr(histget, "download_one", fake_download)
    inst = "MNQ 09-26"
    (tmp_path / inst).mkdir(parents=True)
    (tmp_path / inst / "20260706.nrd").write_bytes(b"x")   # pre-existing -> must be skipped

    out = histget.run_histget(instrument=inst, from_date="20260703", to_date="20260709",
                              replay_dir=tmp_path, write_dir=tmp_path)
    assert out["downloaded"], "nothing was counted as downloaded — the check is vacuous again"
    assert not out["failed"], out["failed"]

    # No Saturday was ever attempted (weekday()==5), regardless of the calendar.
    for ds in calls:
        assert datetime.strptime(ds, "%Y%m%d").weekday() != 5
    # The pre-existing date is skipped, never downloaded.
    assert "20260706" in out["skipped"]
    assert "20260706" not in calls
    # count matches the downloaded list.
    assert out["count"] == len(out["downloaded"])
    assert out["instrument"] == inst


def test_run_histget_excludes_current_and_future_day(monkeypatch, tmp_path):
    called = []

    def fake(instrument, ds, timeout=600.0):
        called.append(ds)
        return {"status": "ok", "exists": True}

    monkeypatch.setattr(histget, "download_one", fake)
    inst = "MNQ 09-26"
    (tmp_path / inst).mkdir(parents=True)
    out = histget.run_histget(instrument=inst, from_date="20260713", to_date="20260716",
                              replay_dir=tmp_path, today=date(2026, 7, 15))
    assert out["today"] == "20260715"
    # today (15, partial) and the future date (16) are never attempted
    assert set(out["skipped_current"]) == {"20260715", "20260716"}
    assert all(ds < "20260715" for ds in called)


def test_histget_cli_force_disables_skip(monkeypatch):
    from nt8bridge import cli
    seen = {}

    def fake(**kw):
        seen.update(kw)
        return {"status": "ok", "downloaded": [], "skipped": [],
                "skipped_current": [], "failed": [], "count": 0}

    monkeypatch.setattr(cli.nthistget, "run_histget", fake)
    cli.main(["histget", "--instrument", "MNQ 09-26", "--from", "20260101", "--to", "20260102"])
    assert seen["skip_existing"] is True
    cli.main(["histget", "--instrument", "MNQ 09-26", "--from", "20260101", "--to", "20260102", "--force"])
    assert seen["skip_existing"] is False


# ---------------------------------------------------------------- the 2026-08-09 defects
# Both were found by DRIVING the tool against a live NT, not by reading it. Pinned here so they
# cannot come back: one reported downloads it never made, the other accepted a flag that read as a
# sandbox while writes went to the corpus of record.

def _fake(writes: bool, tmp_path, calls=None):
    def f(instrument, ds, timeout=600.0):
        if calls is not None:
            calls.append(ds)
        if writes:
            (tmp_path / instrument).mkdir(parents=True, exist_ok=True)
            (tmp_path / instrument / f"{ds}.nrd").write_bytes(b"fresh")
        return {"status": "ok", "exists": True}          # the AddOn's reply, unchanged
    return f


def test_bridge_says_ok_but_nothing_was_written_is_a_FAILURE(monkeypatch, tmp_path):
    """THE DEFECT: the AddOn answers `exists`, which is equally true for a file that was already
    there and never fetched — so every pre-existing date came back as `downloaded`. A reply is not
    an artifact."""
    monkeypatch.setattr(histget, "download_one", _fake(writes=False, tmp_path=tmp_path))
    inst = "GC 08-26"
    out = histget.run_histget(instrument=inst, from_date="20260709", to_date="20260709",
                              replay_dir=tmp_path, write_dir=tmp_path, today=date(2026, 8, 9))
    assert out["downloaded"] == []
    assert [f["date"] for f in out["failed"]] == ["20260709"]
    assert "nothing was written" in out["failed"][0]["error"]


def test_an_unchanged_file_is_not_reported_as_downloaded(monkeypatch, tmp_path):
    """A forced re-download that produced byte-identical output is `unchanged`, never `downloaded`.
    We cannot prove a fetch we cannot see, and under-claiming is the safe direction."""
    inst = "GC 08-26"
    (tmp_path / inst).mkdir(parents=True)
    (tmp_path / inst / "20260709.nrd").write_bytes(b"same")
    monkeypatch.setattr(histget, "download_one", _fake(writes=False, tmp_path=tmp_path))
    out = histget.run_histget(instrument=inst, from_date="20260709", to_date="20260709",
                              skip_existing=False, replay_dir=tmp_path, write_dir=tmp_path,
                              today=date(2026, 8, 9))
    assert out["unchanged"] == ["20260709"]
    assert out["downloaded"] == [] and out["count"] == 0
    assert not out["failed"]


def test_a_changed_file_IS_a_download(monkeypatch, tmp_path):
    """The other direction — the guard must not turn real downloads into non-events."""
    inst = "GC 08-26"
    (tmp_path / inst).mkdir(parents=True)
    (tmp_path / inst / "20260709.nrd").write_bytes(b"old-and-shorter")
    monkeypatch.setattr(histget, "download_one", _fake(writes=True, tmp_path=tmp_path))
    out = histget.run_histget(instrument=inst, from_date="20260709", to_date="20260709",
                              skip_existing=False, replay_dir=tmp_path, write_dir=tmp_path,
                              today=date(2026, 8, 9))
    assert out["downloaded"] == ["20260709"] and out["count"] == 1


def test_result_states_where_writes_actually_go(monkeypatch, tmp_path):
    """No caller should have to guess. The write path is on every result."""
    monkeypatch.setattr(histget, "download_one", _fake(writes=True, tmp_path=tmp_path))
    out = histget.run_histget(instrument="GC 08-26", from_date="20260709", to_date="20260709",
                              replay_dir=tmp_path, write_dir=tmp_path, today=date(2026, 8, 9))
    assert out["write_dir"] == str(tmp_path)
    assert out["check_dir"] == str(tmp_path)


def test_cli_refuses_a_replay_dir_that_is_not_the_real_one(monkeypatch, tmp_path, capsys):
    """THE DANGEROUS ONE. `--replay-dir <scratch>` read as a sandbox; measured on 2026-08-09, the
    download landed in the LIVE corpus and the scratch stayed empty. The CLI now refuses rather
    than letting a caller believe they are isolated."""
    import json as _json
    from nt8bridge import cli
    monkeypatch.setattr(cli.nthistget, "nt_replay_dir", lambda: tmp_path / "real")
    rc = cli.main(["histget", "--instrument", "GC 08-26", "--from", "20260709",
                   "--to", "20260709", "--replay-dir", str(tmp_path / "scratch")])
    out = _json.loads(capsys.readouterr().out)
    assert rc == 2
    assert out["status"] == "refused"
    assert str(tmp_path / "real") in out["writes_to"]
    assert "--check-dir" in out["message"]


def test_cli_accepts_replay_dir_when_it_names_the_real_one(monkeypatch, tmp_path, capsys):
    """A refusal that also blocks the legitimate call is worse than the bug. Passing the true
    directory is harmless and must still run."""
    import json as _json
    from nt8bridge import cli
    real = tmp_path / "real"
    real.mkdir()
    monkeypatch.setattr(cli.nthistget, "nt_replay_dir", lambda: real)
    monkeypatch.setattr(cli.nthistget, "run_histget",
                        lambda **kw: {"status": "ok", "downloaded": [], "unchanged": [],
                                      "skipped": [], "skipped_current": [], "failed": [],
                                      "count": 0, "write_dir": str(real)})
    rc = cli.main(["histget", "--instrument", "GC 08-26", "--from", "20260709",
                   "--to", "20260709", "--replay-dir", str(real)])
    out = _json.loads(capsys.readouterr().out)
    assert out.get("status") != "refused"
    assert rc in (0, 1)
