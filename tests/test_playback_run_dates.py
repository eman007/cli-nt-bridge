"""--from/--to of playback_run are refused at argument time, before any request.

Measured 2026-09-01: seven archived runs carried "to": "2026-13-07" and every
one of them died in stage 2 with "FormatException: String was not recognized
as a valid DateTime." after 45-58 s of wall clock - nothing had checked the
value on the way in. These tests drive the validator directly, and main() end
to end against trigger and result folders under tmp_path, so "no request was
written" is a reading of the folder and not a belief.
"""
from __future__ import annotations

import argparse
import json
import sys

import pytest

from nt8bridge import playback_run as pr

# What the AddOn's `transport` stage answers before Playback is connected: a
# step that reports FAIL is still an ANSWER, so the preflight is satisfied.
ANSWER = {"id": "a1", "status": "ok", "stage": "transport",
          "steps": [{"step": "timer", "ok": False,
                     "detail": "field 'timer' not found or null"}]}
# What stage() returns when no result file appeared within its wait.
NOREACTION = {"status": "noreaction", "steps": [], "id": "0" * 32}


@pytest.mark.parametrize("value", ["2026-06-07", "2026-08-10", "2024-02-29"])
def test_iso_date_returns_a_valid_day_unchanged(value):
    """The same string comes back: the request JSON and the archive carry
    what was typed, exactly as before the check existed."""
    assert pr.iso_date(value) == value


@pytest.mark.parametrize("value, reason", [
    ("2026-13-07", "month must be in 1..12"),          # the seven archived runs
    ("2026-02-30", "day is out of range for month"),
    ("2026-7-7", "not a date of the form"),
    ("07/07/2026", "not a date of the form"),
    ("20260607", "not a date of the form"),            # fromisoformat alone takes this on 3.11
    ("2026-W23-1", "not a date of the form"),          # and this one
    ("2026-06-07T00:00", "not a date of the form"),
    (" 2026-06-07", "not a date of the form"),
])
def test_iso_date_refuses_with_the_accepted_form_and_the_value(value, reason):
    with pytest.raises(argparse.ArgumentTypeError) as ex:
        pr.iso_date(value)
    text = str(ex.value)
    assert value in text
    assert pr.DATE_FORMAT in text
    assert reason in text


def test_date_order_allows_the_same_day_and_refuses_an_earlier_end():
    assert pr.date_order_error("2026-06-07", "2026-06-07") is None
    assert pr.date_order_error("2026-06-07", "2026-06-08") is None
    text = pr.date_order_error("2026-06-07", "2026-06-01")
    assert "--to 2026-06-01" in text and "--from 2026-06-07" in text
    assert "before" in text


# ---------------------------------------------------------------- main() end to end

def _argv(tmp_path, date_from: str, date_to: str) -> list:
    nt8 = tmp_path / "nt8"
    nt8.mkdir(exist_ok=True)
    return ["playback_run.py", "--name", "DATES", "--source", "historical",
            "--tick-replay", "false", "--strategy", "EmptyStrategy",
            "--instrument", "NQ 06-26", "--bars-type", "Minute",
            "--bars-value", "1", "--from", date_from, "--to", date_to,
            "--nt8-dir", str(nt8), "--archive", str(tmp_path / "runs"),
            "--log", str(tmp_path / "transcript.log"), "--debug-console",
            "--result", str(tmp_path / "result.json")]


@pytest.fixture
def driver(tmp_path, monkeypatch):
    """Trigger and result folders under tmp_path, and every module global
    main() rewrites put back afterwards."""
    trig = tmp_path / "trigger"
    res = tmp_path / "result"
    trig.mkdir()
    res.mkdir()
    for name in ("NT8", "TRIG", "RES", "ARCHIVE", "BOTLOGS",
                 "STAGE_WAIT", "SLOW_WAIT", "CONNECT_WAIT", "PREFLIGHT_WAIT"):
        monkeypatch.setattr(pr, name, getattr(pr, name))
    monkeypatch.setattr(pr, "TRIG", trig)
    monkeypatch.setattr(pr, "RES", res)
    monkeypatch.setattr(pr, "BOTS", [])
    monkeypatch.setattr(pr, "FAIL", {"stage": None, "step": None, "detail": None})
    monkeypatch.setitem(pr.STEP_MODE, "on", False)
    monkeypatch.setitem(pr._UI, "user_mode", False)
    monkeypatch.setitem(pr._shots, "dir", None)
    return tmp_path, trig, res


@pytest.mark.parametrize("date_from, date_to, expect", [
    ("2026-06-07", "2026-13-07", ["2026-13-07", "YYYY-MM-DD", "month must be in 1..12"]),
    ("2026-06-07", "07/07/2026", ["07/07/2026", "YYYY-MM-DD"]),
    ("2026-06-07", "2026-06-01", ["--to 2026-06-01", "--from 2026-06-07", "before"]),
])
def test_main_refuses_before_any_request_is_written(
        driver, monkeypatch, capsys, date_from, date_to, expect):
    """Exit 2 with the accepted form and the offending value on stderr, and
    nothing else happened: no request asked, no trigger file, no result file,
    no archive, no transcript."""
    tmp_path, trig, res = driver
    monkeypatch.setattr(sys, "argv", _argv(tmp_path, date_from, date_to))
    sent = []

    def must_not_be_called(title, req, wait=None, show=True, report_miss=True):
        sent.append(dict(req))
        raise AssertionError("a request was sent: " + title)

    monkeypatch.setattr(pr, "stage", must_not_be_called)
    with pytest.raises(SystemExit) as ex:
        pr.main()
    err = capsys.readouterr().err
    assert ex.value.code == 2
    for piece in expect:
        assert piece in err
    assert sent == []
    assert list(trig.iterdir()) == []
    assert list(res.iterdir()) == []
    assert not (tmp_path / "runs").exists()
    assert not (tmp_path / "result.json").exists()
    assert not (tmp_path / "transcript.log").exists()


def test_main_accepts_a_valid_pair_and_carries_it_unchanged(driver, monkeypatch, capsys):
    """A usable pair passes the parser, the bridge is asked, and result.json
    carries the strings as typed. The scripted bridge answers the preflight
    probe only, so the run stops at step 1a with rc 2, as it always did."""
    tmp_path, trig, res = driver
    monkeypatch.setattr(sys, "argv", _argv(tmp_path, "2026-06-07", "2026-06-08"))
    sent, script = [], [dict(ANSWER)]

    def scripted(title, req, wait=None, show=True, report_miss=True):
        sent.append(dict(req))
        return script.pop(0) if script else dict(NOREACTION)

    monkeypatch.setattr(pr, "stage", scripted)
    rc = pr.main()
    capsys.readouterr()
    assert rc == 2
    assert sent and sent[0]["stage"] == "transport"
    result = json.loads((tmp_path / "result.json").read_text(encoding="utf-8"))
    assert result["from"] == "2026-06-07"
    assert result["to"] == "2026-06-08"
