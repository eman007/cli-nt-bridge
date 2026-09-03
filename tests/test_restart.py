"""restart path-selection and ordering — the pure logic, offline.

`restart`'s first version reported `ok: true` having restarted nothing: it launched without stopping,
then asked "is NinjaTrader running?" — which the ORIGINAL process answered. A check that cannot fail.
The selection logic is separated into `choose_start` precisely so that can be asserted without a
NinjaTrader, and `_restart`'s ordering is asserted here against a fake module.
"""
from __future__ import annotations

import json
import subprocess

import pytest

from nt8bridge import cli, restart


# ── restart: which mechanism do the flags select ────────────────────────────────────────────────────

def test_task_wins_when_given():
    kind, detail = restart.choose_start("NT8-Restart", r"C:\NT\bin\NinjaTrader.exe")
    assert kind == "task" and "NT8-Restart" in detail


def test_exe_used_when_no_task():
    kind, detail = restart.choose_start("", r"C:\NT\bin\NinjaTrader.exe")
    assert kind == "exe" and "NinjaTrader.exe" in detail


def test_neither_flag_refuses_rather_than_guessing():
    """No shipped default for either: a task name is site-specific, and a bare exe can start a
    process that stops at a login screen while a running-check still reads healthy."""
    kind, detail = restart.choose_start("", "")
    assert kind == "none"
    assert "--task" in detail and "--exe" in detail


def test_no_shipped_defaults():
    assert restart.DEFAULT_TASK == ""
    assert restart.DEFAULT_EXE == ""


def test_launch_exe_rejects_empty():
    ok, detail = restart.launch_exe("")
    assert ok is False and "--exe" in detail


# ── restart: the ORDER of operations ───────────────────────────────────────────────────────────────

class FakeRestart:
    """Records the call order so "did it stop before it started" is an assertion, not a reading."""

    def __init__(self, *, stop_ok=True, running_after_stop=False, start_ok=True, comes_back=True):
        self.calls: list[str] = []
        self._stop_ok = stop_ok
        self._running_after_stop = running_after_stop
        self._start_ok = start_ok
        self._comes_back = comes_back
        self._stopped = False

    DEFAULT_TASK = ""
    DEFAULT_EXE = ""

    def choose_start(self, task, exe):
        return restart.choose_start(task, exe)

    def is_running(self):
        self.calls.append("is_running")
        return self._running_after_stop if self._stopped else True

    def stop(self, timeout=60.0):
        self.calls.append("stop")
        self._stopped = True
        return self._stop_ok, "stopped" if self._stop_ok else "would not stop"

    def run_task(self, task):
        self.calls.append("run_task")
        return self._start_ok, "task fired"

    def launch_exe(self, exe):
        self.calls.append("launch_exe")
        return self._start_ok, "launched"

    def wait_for(self, state, timeout):
        self.calls.append("wait_for")
        return self._comes_back


def _run(monkeypatch, fake, task="NT8-Restart", exe="", wait=1.0, stop_timeout=1.0):
    monkeypatch.setattr(cli, "ntrestart", fake)
    rc = cli._restart(task, exe, wait, stop_timeout)
    return rc


def test_stop_happens_before_start(monkeypatch, capsys):
    fake = FakeRestart()
    rc = _run(monkeypatch, fake)
    out = json.loads(capsys.readouterr().out)
    assert rc == 0 and out["ok"] is True
    assert fake.calls.index("stop") < fake.calls.index("run_task"), fake.calls


def test_failed_stop_never_launches(monkeypatch, capsys):
    """Two NinjaTraders against one user directory is worse than a failed restart."""
    fake = FakeRestart(stop_ok=False)
    rc = _run(monkeypatch, fake)
    out = json.loads(capsys.readouterr().out)
    assert rc == 1 and out["ok"] is False and out["stage"] == "stop"
    assert "run_task" not in fake.calls and "launch_exe" not in fake.calls


def test_still_running_after_a_successful_stop_refuses(monkeypatch, capsys):
    """A relaunch watchdog can win the race between the stop and the launch."""
    fake = FakeRestart(stop_ok=True, running_after_stop=True)
    rc = _run(monkeypatch, fake)
    out = json.loads(capsys.readouterr().out)
    assert rc == 1 and out["stage"] == "stop"
    assert "watchdog" in out["hint"]
    assert "run_task" not in fake.calls


def test_no_start_mechanism_refuses_before_stopping(monkeypatch, capsys):
    """Stopping NT and then finding we cannot start it is the worst outcome available."""
    fake = FakeRestart()
    rc = _run(monkeypatch, fake, task="", exe="")
    out = json.loads(capsys.readouterr().out)
    assert rc == 1 and out["stage"] == "select"
    assert "stop" not in fake.calls


def test_not_coming_back_is_not_ok(monkeypatch, capsys):
    fake = FakeRestart(comes_back=False)
    rc = _run(monkeypatch, fake)
    out = json.loads(capsys.readouterr().out)
    assert rc == 1 and out["ok"] is False and out["stage"] == "wait"


def test_failed_start_says_nt_is_now_down(monkeypatch, capsys):
    fake = FakeRestart(start_ok=False)
    rc = _run(monkeypatch, fake)
    out = json.loads(capsys.readouterr().out)
    assert rc == 1 and out["stage"] == "start"
    assert "STOPPED" in out["hint"]


# ── process_listed: three states, and is_running() as the bool view of them ────────────────────────

class _Completed:
    """What subprocess.run returns, reduced to the three fields process_listed reads."""

    def __init__(self, returncode: int, stdout: str = "", stderr: str = "") -> None:
        self.returncode, self.stdout, self.stderr = returncode, stdout, stderr


def _tasklist_gives(monkeypatch, outcome) -> None:
    """subprocess.run replaced for the tasklist call: `outcome` is returned, or raised."""

    def fake_run(args, **kw):
        assert args[:2] == ["tasklist", "/FI"]
        if isinstance(outcome, BaseException):
            raise outcome
        return outcome

    monkeypatch.setattr(subprocess, "run", fake_run)


# tasklist's own shapes, measured 2026-09-02: a listing has a header, a rule and
# one row per process; an unlisted name answers exit 0 plus one INFO line (the
# console's locale text); an unknown filter answers exit 1, nothing on stdout.
ROW = ("\nImage Name                     PID Session Name        Session#    Mem Usage\n"
       "========================= ======== ================ =========== ============\n"
       "NinjaTrader.exe               4711 Console                    1  1,234,567 K\n")
INFO = "INFO: No tasks are running which match the specified criteria.\n"


def test_process_listed_true_quotes_the_row(monkeypatch):
    _tasklist_gives(monkeypatch, _Completed(0, ROW))
    listed, detail = restart.process_listed()
    assert listed is True
    assert detail == "tasklist lists NinjaTrader.exe 4711 Console 1 1,234,567 K"
    assert restart.is_running() is True


def test_process_listed_false_quotes_what_tasklist_answered(monkeypatch):
    _tasklist_gives(monkeypatch, _Completed(0, INFO))
    listed, detail = restart.process_listed()
    assert listed is False
    assert detail == "tasklist answered: " + INFO.strip()
    assert restart.is_running() is False


@pytest.mark.parametrize("outcome, said", [
    (FileNotFoundError(2, "The system cannot find the file specified"),
     "tasklist could not be run: FileNotFoundError: [Errno 2] The system cannot find the file "
     "specified"),
    (subprocess.TimeoutExpired(["tasklist"], 30),
     "tasklist could not be run: TimeoutExpired: Command '['tasklist']' timed out after 30 seconds"),
    (_Completed(1, "", "ERROR: The search filter cannot be recognized.\n\n"),
     "tasklist exited 1, printed nothing on stdout, stderr: ERROR: The search filter cannot be "
     "recognized."),
    (_Completed(0, ""), "tasklist exited 0, printed nothing on stdout"),
])
def test_process_listed_none_when_the_list_could_not_be_read(monkeypatch, outcome, said):
    """None, not False: a read that failed says nothing about the process. Before this existed,
    every one of these read as "not running" - and the preflight poll of playbackrun ended a run
    on it after one probe. is_running() folds None into False for the callers that act the same
    on "not seen" and "not there"."""
    _tasklist_gives(monkeypatch, outcome)
    listed, detail = restart.process_listed()
    assert listed is None
    assert detail == said
    assert restart.is_running() is False


# ── deploy: a missing flag is an argparse error, not a TypeError ────────────────────────────────────

def test_deploy_without_a_source_is_an_argparse_error(capsys):
    with pytest.raises(SystemExit) as e:
        cli.main(["deploy", "--kind", "strategy"])
    assert e.value.code == 2
    assert "--from" in capsys.readouterr().err

