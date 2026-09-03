"""Stage 0 of playback_run - the preflight - waits for a busy NinjaTrader.

Runs without NinjaTrader: the request/response function the module uses for
every stage (`stage`) is replaced by a scripted one, and the poll's clock,
sleep and process-list reading (`process`) are injected, so a poll that
would take half an hour of wall clock finishes in milliseconds and never
reads the process list. Two tests drive the REAL stage() against an empty
trigger folder - to prove the cleanup, and to prove what the transcript of an
unanswered poll holds - and four drive main() end to end so the contract a
caller sees (rc, the error string, result.json, the console explanation) is
what is tested.
"""
from __future__ import annotations

import json
import re
import sys
import time
import types

import pytest

from nt8bridge import playback_run as pr

# What the AddOn's `transport` stage answers before Playback is connected - a
# step that reports FAIL is still an ANSWER (stage 0 asks show=False for that).
ANSWER = {"id": "a1", "status": "ok", "stage": "transport",
          "steps": [{"step": "timer", "ok": False,
                     "detail": "field 'timer' not found or null"}]}
# What stage() returns when no result file appeared within wait + grace.
NOREACTION = {"status": "noreaction", "steps": [], "id": "0123456789abcdef"}
# What HandleTrigger writes for a trigger older than its ttlSec: a reaction
# without steps.
EXPIRED = {"id": "e1", "status": "expired", "executed": False,
           "errors": [{"code": "EXPIRED",
                       "message": "request waited 40s in the queue, past its "
                                  "20s TTL; not executed"}]}
# stage() listens wait + GRACE = 20 + 6 s for a probe that gets no answer, and
# returns within a round trip (about 3 s measured) for one that does.
UNANSWERED_S = 26.0
ANSWERED_S = 3.0
# What restart.process_listed returns - the three states, with the text it
# quotes (measured 2026-09-02; the INFO line is the console's locale text).
LISTED = (True, "tasklist lists NinjaTrader.exe 4711 Console 1 1,234,567 K")
UNLISTED = (False, "tasklist answered: INFO: No tasks are running which match "
                   "the specified criteria.")
UNREADABLE = (None, "tasklist could not be run: FileNotFoundError: [WinError 2] "
                    "The system cannot find the file specified")
UNREADABLE_2 = (None, "tasklist exited 1, printed nothing on stdout, stderr: "
                      "ERROR: The search filter cannot be recognized.")


def unreadable_lines(out: str) -> list:
    return [ln for ln in out.splitlines()
            if ln.startswith("The process list could not be read after probe")]


class FakeClock:
    """A clock that advances only when told."""

    def __init__(self, start: float = 1000.0) -> None:
        self.now = start

    def __call__(self) -> float:
        return self.now

    def advance(self, seconds: float) -> None:
        self.now += seconds


@pytest.fixture
def bridge(tmp_path, monkeypatch):
    """Trigger and result folders under tmp_path; debug console mode, so user()
    lines are plain print()s that capsys sees; module state restored after."""
    trig = tmp_path / "trigger"
    res = tmp_path / "result"
    trig.mkdir()
    res.mkdir()
    monkeypatch.setattr(pr, "TRIG", trig)
    monkeypatch.setattr(pr, "RES", res)
    monkeypatch.setitem(pr._UI, "user_mode", False)
    monkeypatch.setitem(pr._shots, "dir", None)
    return tmp_path


def scripted_stage(clock: FakeClock, script: list, calls: list):
    """A stand-in for stage(): answers from `script` in order, NOREACTION once
    the script is used up, and moves the clock by what the real one would
    have spent listening."""

    def fake_stage(title, req, wait=None, show=True, report_miss=True):
        calls.append({"title": title, "req": dict(req), "wait": wait, "show": show,
                      "report_miss": report_miss})
        d = script.pop(0) if script else dict(NOREACTION)
        clock.advance(ANSWERED_S if d.get("steps") else UNANSWERED_S)
        return d

    return fake_stage


def busy_lines(out: str) -> list:
    return [ln for ln in out.splitlines()
            if ln.startswith("NinjaTrader is busy - no answer for")]


def test_answered_first_probe_does_not_wait(bridge, monkeypatch, capsys):
    """(a) One answer, one probe, no waiting line - the fast path stays fast."""
    clock, calls, sleeps = FakeClock(), [], []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [dict(ANSWER)], calls))
    pre = pr.preflight(budget=1800, clock=clock, sleep=sleeps.append,
                       process=lambda: pytest.fail("process list read on the fast path"))
    assert pre["answered"] is True
    assert pre["process"] is None                  # no reading was made
    assert pre["probes"] == 1
    assert pre["steps"] == ANSWER["steps"]
    assert pre["seconds"] == pytest.approx(ANSWERED_S)
    assert sleeps == []
    out = capsys.readouterr().out
    assert busy_lines(out) == []
    assert "Bridge answered after" not in out
    # The probe is the cheap transport read with the short TTL, and never
    # visible: a FAIL step before connect must not trip the hard stop. Its
    # miss is the poll's to record - the stage's own NO REACTION verdict
    # ("not waiting longer") stays out of a transcript that asks again.
    assert calls == [{"title": "0 preflight",
                      "req": {"stage": "transport", "sampleMs": "200"},
                      "wait": pr.PREFLIGHT_PROBE_WAIT, "show": False,
                      "report_miss": False}]


def test_unanswered_probes_are_waited_out_and_reported_at_most_every_30_s(
        bridge, monkeypatch, capsys):
    """(b) Five silent probes with the process PRESENT, then an answer: the
    poll continues exactly as it did before the process check existed, the
    process list is asked once per unanswered probe and never for the answered
    one, and the console line came no more often than every PREFLIGHT_REPORT_S."""
    clock, calls, sleeps, checks = FakeClock(), [], [], []
    script = [dict(NOREACTION) for _ in range(5)] + [dict(ANSWER)]
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, script, calls))

    def present() -> tuple:
        checks.append(clock.now)
        return LISTED

    pre = pr.preflight(budget=1800, clock=clock, sleep=sleeps.append, process=present)
    assert pre["answered"] is True
    assert pre["process"] == {"listed": True, "detail": LISTED[1]}
    assert pre["probes"] == 6
    assert pre["seconds"] == pytest.approx(5 * UNANSWERED_S + ANSWERED_S)
    # one check per unanswered probe, each AFTER that probe's 26 s of
    # listening - and none for the sixth probe, which was answered
    assert checks == [1000.0 + UNANSWERED_S * n for n in range(1, 6)]
    out = capsys.readouterr().out
    assert "NinjaTrader.exe is not running" not in out
    assert unreadable_lines(out) == []
    reported = [int(re.search(r"no answer for (\d+) s", ln).group(1))
                for ln in busy_lines(out)]
    # the first miss at 26 s, then never closer than PREFLIGHT_REPORT_S
    assert reported == [26, 78, 130]
    assert all(b - a >= pr.PREFLIGHT_REPORT_S
               for a, b in zip(reported, reported[1:]))
    assert all("waiting up to 1800 s" in ln for ln in busy_lines(out))
    assert "Bridge answered after 133 s (6 probes)" in out
    # every probe carried the short TTL, so none can be executed late
    assert [c["wait"] for c in calls] == [pr.PREFLIGHT_PROBE_WAIT] * 6
    # a silent probe is re-asked at once - stage() already listened 26 s
    assert sleeps == []


def test_reaction_without_steps_is_re_asked_after_one_poller_tick(
        bridge, monkeypatch, capsys):
    """An `expired` file is the AddOn reading triggers again, not an answer:
    the next probe follows after one poller tick, not in a hot loop."""
    clock, calls, sleeps = FakeClock(), [], []
    monkeypatch.setattr(pr, "stage",
                        scripted_stage(clock, [dict(EXPIRED), dict(ANSWER)], calls))
    pre = pr.preflight(budget=1800, clock=clock, sleep=sleeps.append,
                       process=lambda: LISTED)
    assert pre["answered"] is True
    assert pre["probes"] == 2
    assert sleeps == [1.0]


def test_budget_exhausted_is_not_answered(bridge, monkeypatch, capsys):
    """(c) at the function: the poll gives up only once the budget is spent,
    and reports how long it listened."""
    clock, calls, sleeps = FakeClock(), [], []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [], calls))
    pre = pr.preflight(budget=100, clock=clock, sleep=sleeps.append,
                       process=lambda: LISTED)
    assert pre["answered"] is False
    assert pre["process"] == {"listed": True, "detail": LISTED[1]}
    assert pre["steps"] == []
    assert pre["probes"] == 4                     # 4 x 26 s = 104 s >= 100 s
    assert pre["seconds"] == pytest.approx(4 * UNANSWERED_S)
    assert pre["seconds"] >= 100


def test_no_trigger_file_is_left_behind_by_the_poll(bridge, monkeypatch):
    """(d) With the REAL stage() and no AddOn: the trigger it wrote is taken
    back once the probe went unanswered, and nothing stays in either folder."""
    # the real stage() sleeps 0.1 s per sample for wait + 6 s: make that free
    monkeypatch.setattr(pr, "time", types.SimpleNamespace(
        time=time.time, monotonic=time.monotonic, sleep=lambda s: None))
    real_stage = pr.stage
    after_stage = []

    def spy(title, req, wait=None, show=True, report_miss=True):
        d = real_stage(title, req, wait=wait, show=show, report_miss=report_miss)
        after_stage.append(sorted(p.name for p in pr.TRIG.iterdir()))
        return d

    monkeypatch.setattr(pr, "stage", spy)
    clock = FakeClock()

    def ticking() -> float:
        # 30 s per reading against a 1 s budget: one probe, then the poll ends
        clock.advance(30.0)
        return clock.now

    pre = pr.preflight(budget=1, clock=ticking, sleep=lambda s: None,
                       process=lambda: LISTED)
    assert pre["answered"] is False
    assert pre["probes"] == 1
    # the trigger existed while the probe was pending ...
    assert len(after_stage) == 1 and len(after_stage[0]) == 1
    assert after_stage[0][0].startswith("playbackrun_")
    assert after_stage[0][0].endswith(".json")
    # ... and is gone once the poll ended - and no result file appeared
    assert list(pr.TRIG.iterdir()) == []
    assert list(pr.RES.iterdir()) == []


def test_unanswered_probes_leave_only_the_polls_own_lines_in_the_transcript(
        bridge, monkeypatch, capsys):
    """(h) With the REAL stage() and no AddOn, budget 100 s: four probes go
    unanswered and the transcript holds the poll's record of them - one
    "asking again" line per probe that was asked again, the user line every
    PREFLIGHT_REPORT_S - and nothing from the stage itself. Measured
    2026-09-02 before the fix: the stage's NO REACTION verdict ("not waiting
    longer, that IS the finding") stood in the transcript four times, each
    directly above the poll's "asking again" - 25 lines contradicting each
    other once per probe. The default is unchanged: a stage nobody asks again
    keeps reporting its miss, visible or not."""
    monkeypatch.setattr(pr, "time", types.SimpleNamespace(
        time=time.time, monotonic=time.monotonic, sleep=lambda s: None))
    clock = FakeClock()

    def ticking() -> float:
        clock.advance(UNANSWERED_S)      # what one real probe costs
        return clock.now

    pre = pr.preflight(budget=100, clock=ticking, sleep=lambda s: None,
                       process=lambda: LISTED)
    assert pre["answered"] is False
    assert pre["probes"] == 4                     # 4 x 26 s = 104 s >= 100 s
    out = capsys.readouterr().out
    assert "NO REACTION" not in out
    assert "not waiting longer" not in out
    assert "progress file: never written" not in out
    assert "--- 0 preflight ---" not in out
    asked_again = [ln for ln in out.splitlines() if "no answer to probe" in ln]
    assert [int(re.search(r"probe (\d+)", ln).group(1)) for ln in asked_again] == [1, 2, 3]
    assert all(ln.endswith("- asking again") for ln in asked_again)
    reported = [int(re.search(r"no answer for (\d+) s", ln).group(1))
                for ln in busy_lines(out)]
    assert reported == [26, 78]
    # the last miss is the caller's verdict, not a line here; nothing else
    assert len(out.splitlines()) == len(asked_again) + len(reported)
    assert list(pr.TRIG.iterdir()) == []
    assert list(pr.RES.iterdir()) == []
    # the default: the same unanswered request, not part of a poll, reports
    # its miss - the hidden probe of a caller that stops there included
    pr.stage("lone probe", {"stage": "transport"}, wait=1, show=False)
    out = capsys.readouterr().out
    assert "--- lone probe ---" in out
    assert "NO REACTION in 7 s (server budget 1 s + 6 s grace)" in out
    assert "not waiting longer, that IS the finding" in out
    assert "progress file: never written" in out
    pr.stage("lone probe", {"stage": "transport"}, wait=1, show=False,
             report_miss=False)
    assert capsys.readouterr().out == ""


def test_process_absent_stops_the_poll_after_the_first_probe(
        bridge, monkeypatch, capsys):
    """(e) No NinjaTrader.exe: the poll stops after ONE unanswered probe - the
    26 s the single ask cost - not after the budget, and says why. The process
    list is asked once, AFTER the probe, never before it."""
    clock, calls, sleeps, checks = FakeClock(), [], [], []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [], calls))

    def absent() -> tuple:
        checks.append(clock.now)
        return UNLISTED

    pre = pr.preflight(budget=1800, clock=clock, sleep=sleeps.append, process=absent)
    assert pre["answered"] is False
    assert pre["process"] == {"listed": False, "detail": UNLISTED[1]}
    assert pre["steps"] == []
    assert pre["probes"] == 1
    assert pre["seconds"] == pytest.approx(UNANSWERED_S)
    assert checks == [1000.0 + UNANSWERED_S]      # after the probe, not before
    assert len(calls) == 1
    assert sleeps == []
    out = capsys.readouterr().out
    assert "NinjaTrader.exe is not running" in out
    assert UNLISTED[1] in out                     # what was measured, quoted
    assert "the poll stops after probe 1 (26 s of 1800 s)" in out
    assert busy_lines(out) == []                  # a dead process is not "busy"


def test_process_that_disappears_mid_poll_ends_it_on_that_round(
        bridge, monkeypatch, capsys):
    """The check is made every round, not once: a process that goes away
    during the wait ends the poll on the first round that misses it, and the
    rounds before it polled as usual."""
    clock, calls, checks = FakeClock(), [], []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [], calls))
    answers = [LISTED, LISTED, UNLISTED]

    def process() -> tuple:
        checks.append(clock.now)
        return answers.pop(0)

    pre = pr.preflight(budget=1800, clock=clock, sleep=lambda s: None, process=process)
    assert pre["answered"] is False
    assert pre["process"]["listed"] is False
    assert pre["probes"] == 3
    assert pre["seconds"] == pytest.approx(3 * UNANSWERED_S)
    assert len(checks) == 3
    out = capsys.readouterr().out
    reported = [int(re.search(r"no answer for (\d+) s", ln).group(1))
                for ln in busy_lines(out)]
    assert reported == [26]                       # round 2 at 52 s is < 30 s later
    assert "the poll stops after probe 3 (78 s of 1800 s)" in out


def test_unreadable_process_list_is_no_verdict_and_is_said_once_per_failure(
        bridge, monkeypatch, capsys):
    """(f) tasklist that cannot be run, then one that exits 1, then a listing,
    then an answer: a read that failed says nothing about the process, so the
    poll goes on exactly as with the process present - and the console names
    each distinct failure ONCE, not once per probe."""
    clock, calls, checks = FakeClock(), [], []
    script = [dict(NOREACTION) for _ in range(4)] + [dict(ANSWER)]
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, script, calls))
    answers = [UNREADABLE, UNREADABLE, UNREADABLE_2, LISTED]

    def process() -> tuple:
        checks.append(clock.now)
        return answers.pop(0)

    pre = pr.preflight(budget=1800, clock=clock, sleep=lambda s: None, process=process)
    assert pre["answered"] is True
    assert pre["probes"] == 5
    assert pre["process"] == {"listed": True, "detail": LISTED[1]}
    assert len(checks) == 4
    out = capsys.readouterr().out
    assert "NinjaTrader.exe is not running" not in out
    said = unreadable_lines(out)
    assert len(said) == 2                         # two distinct failures, two lines
    assert "after probe 1 (" in said[0] and UNREADABLE[1] in said[0]
    assert "after probe 3 (" in said[1] and UNREADABLE_2[1] in said[1]
    assert all("whether NinjaTrader.exe is running is not known" in ln for ln in said)
    reported = [int(re.search(r"no answer for (\d+) s", ln).group(1))
                for ln in busy_lines(out)]
    assert reported == [26, 78]                   # the wait is reported as before
    assert "Bridge answered after 107 s (5 probes)" in out


def test_unreadable_process_list_for_the_whole_budget_is_not_a_verdict(
        bridge, monkeypatch, capsys):
    """(g) Never readable: the budget is spent as with the process present -
    the poll never claims the process is absent - and the last reading says
    so for the caller's report."""
    clock, calls = FakeClock(), []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [], calls))
    pre = pr.preflight(budget=100, clock=clock, sleep=lambda s: None,
                       process=lambda: UNREADABLE)
    assert pre["answered"] is False
    assert pre["process"] == {"listed": None, "detail": UNREADABLE[1]}
    assert pre["probes"] == 4                     # 4 x 26 s = 104 s >= 100 s
    assert pre["seconds"] == pytest.approx(4 * UNANSWERED_S)
    out = capsys.readouterr().out
    assert "NinjaTrader.exe is not running" not in out
    assert len(unreadable_lines(out)) == 1        # one failure, said once


def test_default_process_check_is_the_restart_commands_own(bridge, monkeypatch):
    """One implementation of "is NinjaTrader.exe running": the poll's default
    is restart.process_listed itself - and it is resolved at call time, so a
    hook set on the module is honoured by a call that names no `process`."""
    from nt8bridge import restart
    assert pr.ninjatrader_process_listed is restart.process_listed
    clock, calls = FakeClock(), []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [], calls))
    monkeypatch.setattr(pr, "ninjatrader_process_listed", lambda: UNLISTED)
    pre = pr.preflight(budget=1800, clock=clock, sleep=lambda s: None)
    assert pre["process"] == {"listed": False, "detail": UNLISTED[1]}
    assert pre["probes"] == 1


def _main_args(tmp_path) -> list:
    nt8 = tmp_path / "nt8"
    nt8.mkdir()
    return ["playback_run.py", "--name", "PRE", "--source", "historical",
            "--tick-replay", "false", "--strategy", "EmptyStrategy",
            "--instrument", "NQ 06-26", "--bars-type", "Minute",
            "--bars-value", "1", "--from", "2026-08-10", "--to", "2026-08-10",
            "--nt8-dir", str(nt8), "--archive", str(tmp_path / "runs"),
            "--log", str(tmp_path / "transcript.log"), "--debug-console",
            "--result", str(tmp_path / "result.json")]


@pytest.fixture
def main_env(bridge, monkeypatch):
    """main() rewrites module paths, budgets and run state from its arguments;
    monkeypatch puts every one of them back afterwards."""
    for name in ("NT8", "TRIG", "RES", "ARCHIVE", "BOTLOGS",
                 "STAGE_WAIT", "SLOW_WAIT", "CONNECT_WAIT", "PREFLIGHT_WAIT"):
        monkeypatch.setattr(pr, name, getattr(pr, name))
    monkeypatch.setattr(pr, "BOTS", [])
    monkeypatch.setattr(pr, "FAIL", {"stage": None, "step": None, "detail": None})
    monkeypatch.setitem(pr.STEP_MODE, "on", False)
    monkeypatch.setattr(sys, "argv", _main_args(bridge))
    return bridge


def test_main_exhausted_budget_aborts_with_the_exact_error_and_records_the_wait(
        main_env, monkeypatch, capsys):
    """(c) end to end: rc 2, the error string callers match on, the measured
    cause and the budget in the explanation, `preflightSeconds` in the archive
    - and nothing but the teardown was sent after the probes."""
    clock, calls = FakeClock(), []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [], calls))
    real_preflight = pr.preflight

    def injected(budget=None, **_):
        return real_preflight(budget, clock=clock, sleep=lambda s: None,
                              process=lambda: LISTED)

    monkeypatch.setattr(pr, "preflight", injected)
    rc = pr.main()
    out = capsys.readouterr().out
    assert rc == 2
    result = json.loads((main_env / "result.json").read_text(encoding="utf-8"))
    assert result["error"] == "preflight: the bridge did not answer"
    assert result["rc"] == 2
    assert result["ok"] is False
    assert result["endReason"] == "aborted"
    assert result["preflightSeconds"] >= 1800
    assert "NinjaTrader.exe is not running" not in out
    assert "Its process exists" in out
    assert "the last read: " + LISTED[1] in out   # the evidence, not a claim
    assert "NOT known" not in out
    assert result["preflightSeconds"] == pytest.approx(70 * UNANSWERED_S, abs=0.1)
    probes = [c for c in calls if c["title"] == "0 preflight"]
    assert len(probes) == 70                      # 70 x 26 s = 1820 s >= 1800 s
    assert "budget 1800 s" in out
    assert "388 s on 2026-09-02" in out
    assert "735 s on 2026-09-01" in out
    assert "Clear the trigger folder" not in out
    # after the probes only the teardown's own two calls went out
    assert [c["req"]["stage"] for c in calls[len(probes):]] == ["restore", "botout"]
    # the archived copy carries the same number
    archived = json.loads(
        next((main_env / "runs").glob("PRE_*/result.json")).read_text(encoding="utf-8"))
    assert archived["preflightSeconds"] == result["preflightSeconds"]


def test_main_process_absent_aborts_at_once_with_the_exact_error(
        main_env, monkeypatch, capsys):
    """(e) end to end: no NinjaTrader.exe - rc 2 and the SAME error string
    callers match on, after ONE probe (26 s) instead of the 1800 s budget; the
    explanation names the missing process, not the busy-NinjaTrader causes;
    `preflightSeconds` records the 26 s; and only the teardown followed."""
    clock, calls = FakeClock(), []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [], calls))
    real_preflight = pr.preflight

    def injected(budget=None, **_):
        return real_preflight(budget, clock=clock, sleep=lambda s: None,
                              process=lambda: UNLISTED)

    monkeypatch.setattr(pr, "preflight", injected)
    rc = pr.main()
    out = capsys.readouterr().out
    assert rc == 2
    result = json.loads((main_env / "result.json").read_text(encoding="utf-8"))
    assert result["error"] == "preflight: the bridge did not answer"
    assert result["rc"] == 2
    assert result["ok"] is False
    assert result["endReason"] == "aborted"
    assert result["preflightSeconds"] == pytest.approx(UNANSWERED_S, abs=0.1)
    probes = [c for c in calls if c["title"] == "0 preflight"]
    assert len(probes) == 1
    assert "NinjaTrader.exe is not running" in out
    assert "The process list was read after probe" in out
    assert "1 (26 s) and does not name it; measured:" in out
    assert UNLISTED[1] in out                     # what tasklist answered, quoted
    assert "has no such" not in out               # no claim beyond the reading
    assert "388 s on 2026-09-02" not in out
    assert "Clear the trigger folder" not in out
    # after the one probe only the teardown's own two calls went out
    assert [c["req"]["stage"] for c in calls[len(probes):]] == ["restore", "botout"]


def test_main_unreadable_process_list_waits_out_the_budget_and_says_unknown(
        main_env, monkeypatch, capsys):
    """(g) end to end: tasklist never readable - the run waits out the budget
    like a busy NinjaTrader (rc 2, the same error string, 70 probes), and the
    explanation says the process is UNKNOWN with the failed read quoted - it
    neither claims the process exists nor that it is absent."""
    clock, calls = FakeClock(), []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [], calls))
    real_preflight = pr.preflight

    def injected(budget=None, **_):
        return real_preflight(budget, clock=clock, sleep=lambda s: None,
                              process=lambda: UNREADABLE)

    monkeypatch.setattr(pr, "preflight", injected)
    rc = pr.main()
    out = capsys.readouterr().out
    assert rc == 2
    result = json.loads((main_env / "result.json").read_text(encoding="utf-8"))
    assert result["error"] == "preflight: the bridge did not answer"
    assert result["preflightSeconds"] == pytest.approx(70 * UNANSWERED_S, abs=0.1)
    probes = [c for c in calls if c["title"] == "0 preflight"]
    assert len(probes) == 70
    assert "NinjaTrader.exe is not running" not in out
    assert "Its process exists" not in out
    assert "Whether its process exists is NOT known" in out
    assert "the last read: " + UNREADABLE[1] in out
    assert len(unreadable_lines(out)) == 1        # one failure, said once
    assert [c["req"]["stage"] for c in calls[len(probes):]] == ["restore", "botout"]


def test_main_answered_preflight_keeps_the_fast_path_and_its_contract(
        main_env, monkeypatch, capsys):
    """The answered path is unchanged: "Bridge connected.", the output-window
    discard right after the answer, and `preflightSeconds` near zero."""
    clock, calls = FakeClock(), []
    monkeypatch.setattr(pr, "stage", scripted_stage(clock, [dict(ANSWER)], calls))
    rc = pr.main()                # the real clock: the scripted stage answers at once
    out = capsys.readouterr().out
    result = json.loads((main_env / "result.json").read_text(encoding="utf-8"))
    # the run stops at step 1a - nothing answers after the probe
    assert rc == 2
    assert result["error"].startswith("mandatory step failed")
    assert 0.0 <= result["preflightSeconds"] < 1.0
    assert "Bridge connected." in out
    assert busy_lines(out) == []
    stages = [c["req"]["stage"] for c in calls]
    assert stages[:2] == ["transport", "botout"]
    assert stages[-2:] == ["restore", "botout"]
