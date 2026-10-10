"""Tests for `output` — reading NT8's Output 1 / Output 2 (Print) tabs.

The gap these cover: a strategy's own diagnostics only existed inside NT8's Output window,
so debugging a running strategy meant alt-tabbing into the platform and reading pixels.
The AddOn now tees `NinjaTrader.Code.Output.OutputEvent` to per-tab JSONL under
<NT8>/NT8Bridge/output/, and these cover the reading half: tail boundaries, reading back
across a rotation, UTC `--since` handling (this box is PST and NT8's data is ET, so an
unlabelled timestamp is a bug waiting to happen), grep, and the two marker record types
that make a GAP in the record visible instead of silent.
"""
from __future__ import annotations

import json
from datetime import datetime, timedelta, timezone

import pytest

from nt8bridge import output


@pytest.fixture()
def nt8(tmp_path, monkeypatch):
    """Point the bridge at a temp NT8 dir so nothing touches the real platform."""
    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    return tmp_path


def _write(path, records):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("".join(json.dumps(r) + "\n" for r in records), encoding="utf-8")


def _msg(ts, text, tab=1):
    return {"t": ts, "tab": tab, "msg": text}


# ---- paths + enable/disable (the tee is opt-in) ----

def test_tab_path_names_live_and_rotated_generations(nt8):
    assert output.tab_path(1).name == "tab1.jsonl"
    assert output.tab_path(2).name == "tab2.jsonl"
    assert output.tab_path(1, generation=1).name == "tab1.1.jsonl"
    assert output.tab_path(1).parent == output.output_dir()


def test_tab_path_rejects_a_tab_that_does_not_exist(nt8):
    # NT8 has exactly two Output tabs (PrintTo.OutputTab1 / OutputTab2).
    with pytest.raises(ValueError):
        output.tab_path(3)


def test_tee_is_disabled_until_enabled_and_disable_is_idempotent(nt8):
    assert output.is_enabled() is False       # opt-in: a fresh box records nothing
    output.enable()
    assert output.is_enabled() is True
    assert output.marker_path().exists()
    assert output.disable() is True           # was on
    assert output.is_enabled() is False
    assert output.disable() is False          # already off -> no error


# ---- reading ----

def test_read_returns_records_oldest_first(nt8):
    _write(output.tab_path(1), [
        _msg("2026-08-10T04:00:00.000Z", "first"),
        _msg("2026-08-10T04:00:01.000Z", "second"),
    ])
    recs, _ = output.read_records(tab=1)
    assert [r["msg"] for r in recs] == ["first", "second"]


def test_tail_returns_the_LAST_n_records(nt8):
    _write(output.tab_path(1), [
        _msg("2026-08-10T04:00:0%d.000Z" % i, "line%d" % i) for i in range(5)
    ])
    recs, _ = output.read_records(tab=1, tail=2)
    assert [r["msg"] for r in recs] == ["line3", "line4"]


def test_tail_larger_than_the_file_returns_everything_without_error(nt8):
    _write(output.tab_path(1), [_msg("2026-08-10T04:00:00.000Z", "only")])
    recs, _ = output.read_records(tab=1, tail=500)
    assert [r["msg"] for r in recs] == ["only"]


def test_tail_reads_back_across_the_rotated_generation(nt8):
    # Rotation keeps one previous generation; a tail deeper than the live file must not
    # silently stop at its head and imply that is all that was ever printed.
    _write(output.tab_path(1, generation=1), [
        _msg("2026-08-10T03:59:58.000Z", "old1"),
        _msg("2026-08-10T03:59:59.000Z", "old2"),
    ])
    _write(output.tab_path(1), [_msg("2026-08-10T04:00:00.000Z", "new1")])
    recs, _ = output.read_records(tab=1, tail=3)
    assert [r["msg"] for r in recs] == ["old1", "old2", "new1"]
    # and a tail that fits inside the live file must not drag the rotated one in
    recs, _ = output.read_records(tab=1, tail=1)
    assert [r["msg"] for r in recs] == ["new1"]


def test_tabs_are_separate_files(nt8):
    _write(output.tab_path(1), [_msg("2026-08-10T04:00:00.000Z", "to tab one", tab=1)])
    _write(output.tab_path(2), [_msg("2026-08-10T04:00:01.000Z", "to tab two", tab=2)])
    one, _ = output.read_records(tab=1)
    two, _ = output.read_records(tab=2)
    assert [r["msg"] for r in one] == ["to tab one"]
    assert [r["msg"] for r in two] == ["to tab two"]


def test_missing_file_reads_as_empty_not_an_exception(nt8):
    recs, stats = output.read_records(tab=1)
    assert recs == []
    assert stats.missing is True


def test_malformed_line_is_skipped_and_counted(nt8):
    # The tail of a live file can be a partial write; that must not kill the read.
    path = output.tab_path(1)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(_msg("2026-08-10T04:00:00.000Z", "good")) + "\n"
        + '{"t":"2026-08-10T04:00:01.000Z","msg":"trunca\n',
        encoding="utf-8",
    )
    recs, stats = output.read_records(tab=1)
    assert [r["msg"] for r in recs] == ["good"]
    assert stats.malformed == 1


# ---- filtering ----

def test_grep_filters_messages_by_regex(nt8):
    _write(output.tab_path(1), [
        _msg("2026-08-10T04:00:00.000Z", "AlphaStrat: scenario 118 resolved"),
        _msg("2026-08-10T04:00:01.000Z", "BetaStrat: gate=OPEN"),
        _msg("2026-08-10T04:00:02.000Z", "AlphaStrat: scenario 119 opened"),
    ])
    recs, _ = output.read_records(tab=1, grep="AlphaStrat")
    assert len(recs) == 2
    assert all("AlphaStrat" in r["msg"] for r in recs)


def test_grep_keeps_gap_markers_so_a_filtered_view_still_shows_a_gap(nt8):
    # A cleared window or a dropped batch explains missing lines. Hiding those markers
    # because they do not match the pattern would make a filtered read look complete.
    _write(output.tab_path(1), [
        _msg("2026-08-10T04:00:00.000Z", "AlphaStrat: one"),
        {"t": "2026-08-10T04:00:01.000Z", "tab": 1, "reset": True},
        {"t": "2026-08-10T04:00:02.000Z", "tab": 1, "dropped": 417},
        _msg("2026-08-10T04:00:03.000Z", "BetaStrat: unrelated"),
    ])
    recs, _ = output.read_records(tab=1, grep="AlphaStrat")
    kinds = [("reset" in r, "dropped" in r) for r in recs]
    assert (True, False) in kinds and (False, True) in kinds
    assert not any(r.get("msg") == "BetaStrat: unrelated" for r in recs)


def test_since_filters_on_utc_and_is_inclusive_of_the_boundary(nt8):
    _write(output.tab_path(1), [
        _msg("2026-08-10T04:39:59.000Z", "before"),
        _msg("2026-08-10T04:40:00.000Z", "boundary"),
        _msg("2026-08-10T04:40:01.000Z", "after"),
    ])
    since = datetime(2026, 8, 10, 4, 40, tzinfo=timezone.utc)
    recs, _ = output.read_records(tab=1, since=since)
    assert [r["msg"] for r in recs] == ["boundary", "after"]


# ---- --since parsing (UTC, explicitly) ----

def test_parse_since_accepts_full_iso_with_z():
    got = output.parse_since("2026-08-10T04:40:00Z", now=datetime(2026, 8, 10, 5, 0, tzinfo=timezone.utc))
    assert got == datetime(2026, 8, 10, 4, 40, tzinfo=timezone.utc)


def test_parse_since_bare_hhmm_is_UTC_today_not_local():
    # The box is PST and NT8's own data is ET. A bare HH:MM resolving to local time would
    # silently shift every window by hours, so it is defined as UTC.
    now = datetime(2026, 8, 10, 5, 0, tzinfo=timezone.utc)
    assert output.parse_since("04:40", now=now) == datetime(2026, 8, 10, 4, 40, tzinfo=timezone.utc)


def test_parse_since_relative_forms():
    now = datetime(2026, 8, 10, 5, 0, tzinfo=timezone.utc)
    assert output.parse_since("10m", now=now) == now - timedelta(minutes=10)
    assert output.parse_since("2h", now=now) == now - timedelta(hours=2)
    assert output.parse_since("30s", now=now) == now - timedelta(seconds=30)


def test_parse_since_rejects_garbage_with_a_clear_error():
    with pytest.raises(ValueError):
        output.parse_since("yesterday-ish", now=datetime.now(timezone.utc))


# ---- rendering ----

def test_render_marks_times_as_utc_and_keeps_the_message_verbatim():
    lines = output.render([_msg("2026-08-10T04:52:10.123Z", "BetaStrat: gate=OPEN atr=14.25")])
    assert lines == ["04:52:10Z  BetaStrat: gate=OPEN atr=14.25"]


def test_render_makes_both_gap_markers_explicit():
    lines = output.render([
        {"t": "2026-08-10T04:52:11.000Z", "tab": 1, "reset": True},
        {"t": "2026-08-10T04:52:12.000Z", "tab": 1, "dropped": 417},
    ])
    assert "Output cleared" in lines[0]
    assert "417" in lines[1] and "dropped" in lines[1].lower()


def test_render_preserves_a_multiline_print_as_one_record():
    # A single Print() can contain newlines; JSONL keeps it one record, and the render
    # must not let the continuation masquerade as a separate timestamped line.
    lines = output.render([_msg("2026-08-10T04:52:10.000Z", "header\n  detail")])
    assert len(lines) == 1
    assert "header" in lines[0] and "detail" in lines[0]


# ---- status ----

def test_status_reports_enabled_state_and_per_tab_size(nt8):
    _write(output.tab_path(1), [
        _msg("2026-08-10T04:00:00.000Z", "a"),
        {"t": "2026-08-10T04:00:01.000Z", "tab": 1, "dropped": 5},
    ])
    output.enable()
    st = output.status()
    assert st["enabled"] is True
    assert st["tab1"]["records"] == 2
    assert st["tab1"]["dropped"] == 5      # counted from the markers in the file itself
    assert st["tab1"]["bytes"] > 0
    assert st["tab2"]["records"] == 0
