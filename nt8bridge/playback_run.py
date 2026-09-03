# MIT License
# Copyright (c) 2026 Quantrosoft Pty. Ltd.
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in all
# copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
# SOFTWARE.

r"""Run ONE Playback measurement end to end over the bridge, and archive it.

The order below was measured by hand on 2026-08-18/19 and renumbered on
2026-08-20, when switching every connection off became step 1:

    1a alloff       EVERY connection off, from the LIVE list - not from the
                    configuration, which hid a running connection (see below)
    1b clean start  strategy rows gone, dialogs closed, transport parked
    2  connect      source via the adapter statics, BEFORE connecting - and it
                    waits for the panel transition itself: grey seen -> free
                    seen -> stable, ~11-18 s, reported as `panel usable`
    3  source check verify the source took, and report what the panel shows
    4  dates        start and end into the panel editors, end AFTER start
    5  range        the same range into the adapter, then Reset - which parks
                    the clock at the range start
    6  speed        the panel's paused memory only; writing the adapter static
                    here would start the transport
    7  attach       ONE call: add the strategy and enable it. No window, no
                    dialog, no click - the dialog path WAS the clicking and is
                    gone. Enable/Disable/Remove go through StrategiesGrid.
    8  start        the transport is started by WRITING THE SPEED, never by
                    pressing play

⚠ There is no separate "panel ready" step any more. It waited for the same
transition as `connect`, and once `connect` waited for it too, the second wait
demanded an event that was already over: measured 2026-08-20, "never went grey
within 40000 ms". Trigger and wait belong together, in one step.
    wait            sample NowEst twice - a single reading cannot tell a parked
                    clock from a running one, and the play button answers a
                    different question entirely
    teardown        ALWAYS, see below

⚠ Step 1 exists because a run has to survive ANY starting situation: nothing
connected, one live feed, several at once. Playback refuses to connect while
anything else is up and says so in a MODAL dialog that blocks the dispatcher -
the bridge then stops answering at all. And the report that was supposed to
answer "is anything else connected?" was built from the CONFIGURATION: measured
2026-08-20, it listed six rows without the connection NinjaTrader had open.

Every value written is read back. A run on an unverified setting produces
numbers nobody can attribute.

THREE GUARANTEES, each paid for with a destroyed measurement
------------------------------------------------------------
1.  RESTORE THE BASELINE, ALWAYS. The teardown runs in a `finally`, so an
    exception, a failed mandatory step or Ctrl-C leaves the same state a clean
    finish does: transport parked, strategy disabled and removed, no dialog
    open. Measured 2026-08-19: an aborted attempt left an ObjectDialog and an
    enabled strategy behind, and the next run started on top of them.

2.  ORDER: range/Reset BEFORE enable. `range` calls PlaybackAdapter.Reset,
    which rebuilds the transport - NinjaTrader disables the strategy while
    doing so. Four modes ran with no bot at all: the grid reported
    IsEnabled=True and not one bot log folder appeared in 90 minutes.

3.  CLOCK PRECONDITION before enable. With the clock PAST the range end,
    NinjaTrader disables Enable, Disable and Remove - none of them is
    meaningful for a range already played. The menu then reports "disabled"
    even though the row is selected correctly. After a Reset to the range
    start, Enable works on the first try. The transport also restarts BY
    ITSELF after the Reset, so it must be parked again before the strategy is
    attached - otherwise the bot joins mid-stream and the runs are not
    comparable.

Every request carries `ttlSec`. A request that waits longer than that in the
server's queue is discarded instead of executed - see the AddOn's
HandleTrigger. Measured 2026-08-19: a backlog released after 11 minutes
enabled, disabled and removed a strategy in the middle of a running
measurement.

Before step 1 the run asks the bridge ONE cheap question (stage 0, the
preflight) - and when nothing answers it KEEPS ASKING, up to PREFLIGHT_WAIT.
A silent bridge can be a NinjaTrader that is busy, not one that is broken:
seven archived runs between 2026-08-29 and 2026-09-02 ended with "preflight:
the bridge did not answer" after a single 20 s ask, and for the last of them
NinjaTrader's own log shows a Playback connect in progress (05:15:46 ->
05:22:14, 388 s) that answered nothing meanwhile. Holds of the same kind
measured 423-453 s (2026-08-28/29) and 735 s for a cold start (2026-09-01).
The poll reports on the console at most every 30 s, and its wall-clock duration lands
in result.json as `preflightSeconds`. The one silence it does not wait out:
no NinjaTrader.exe process at all - the process list is read after every
unanswered probe, so a NinjaTrader that is not running costs one probe
(26 s), not the budget, and fails with the same error string. Only a list
that was READ and does not name the process is that verdict; a list that
could not be read says nothing, and the poll keeps waiting.

Usage:
    python playback_run.py --name RUN1 --source historical --tick-replay false \
        --strategy SampleMACrossOver --instrument "MNQ 09-26" \
        --bars-type Minute --bars-value 1 --from 2026-08-10 --to 2026-08-14
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import time
import traceback
import uuid
from datetime import date, datetime
from pathlib import Path

# The process check of the preflight poll - see the PREFLIGHT_WAIT block. ONE
# implementation, restart.process_listed, reached by the name that resolves in the
# way this file is running: cli.py starts the driver as its own process BY
# PATH (`[sys.executable, "-u", driver, ...]`), and there sys.path[0] is this
# folder, `__package__` is None and only the sibling name resolves; the tests
# and cli.py's own deferred import load this file as a member of the package,
# where `__package__` is "nt8bridge" and the package name resolves. Measured
# 2026-09-02 with sys.path[0] set to this folder, PYTHONPATH empty and the
# package not installed: `import nt8bridge` -> ModuleNotFoundError, `import
# restart` -> the sibling file.
if __package__:
    from nt8bridge.restart import process_listed as ninjatrader_process_listed
else:
    from restart import process_listed as ninjatrader_process_listed

# Every path hangs off the NinjaTrader data directory this run talks to, so
# --nt8-dir moves trigger, result and archive together. Nothing here is built
# from a literal carrying a user name.
_HOME = Path(os.path.expanduser("~"))
NT8 = _HOME / "Documents" / "NinjaTrader 8"
TRIG = NT8 / "NT8Bridge/trigger"
RES = NT8 / "NT8Bridge/result"
# Where a finished run is archived. --archive overrides it.
ARCHIVE = NT8 / "NT8Bridge" / "runs"
# A strategy that writes its own log files can have them collected into the
# archive - but only its author knows where it writes, so this stays OFF
# until --bot-logs names a directory.
BOTLOGS = None


def set_nt8_dir(path: str) -> None:
    """Point the driver at another data directory - trigger, result and the
    run archive all hang off it. Called once from main(), before the first
    request is written.

    BOTLOGS is NOT touched here: it names a directory belonging to a
    strategy, not to NinjaTrader, so only --bot-logs sets it.
    """
    global NT8, TRIG, RES, ARCHIVE
    NT8 = Path(path)
    TRIG = NT8 / "NT8Bridge/trigger"
    RES = NT8 / "NT8Bridge/result"
    ARCHIVE = NT8 / "NT8Bridge" / "runs"

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8")
    except AttributeError:
        pass

# ⚠ THE STATE MACHINE FOR EVERY ACTION
#
#   1. trigger it
#   2. wait for the reaction it MUST produce - seconds, not minutes
#   3. carry on
#
# A pure UI operation answers within seconds; a person would not sit through
# more either. So the budget is small, and running it out is not "slow", it is
# the finding: NinjaTrader is doing something this stage never asked for. The
# old 180 s / 900 s budgets hid exactly that - three minutes of silence, a
# shrugged "PROBLEM", and the actual cause never named.
#
# The one genuinely long operation, playing a range, is NOT a stage: it is the
# sampling loop in main(), which reports once per SAMPLE_S and says why it
# stopped.
# ⚠ A STAGE BUDGET IS A GUESS ABOUT SOMEONE ELSE'S MACHINE - SO IT IS A
# PARAMETER. --stage-wait overrides both numbers below.
#
# What they have to cover, measured 2026-08-24: the AddOn's own work per stage
# is 0-2 ms for everything except the connect (10.7 s, NinjaTrader talking to
# its provider) and `restore` (1.2 s). What costs the rest is the round trip -
# a steady ~3 s per request - and the driver sends `uiidle` and `ntlog` around
# every stage, so one logical step is three requests before any work happens.
#
# A machine that is slower, or a NinjaTrader that is busier, needs more, and
# that has to be settable without editing this file.
STAGE_WAIT = 10          # any ordinary UI action
SLOW_WAIT = 25           # Reset / range / speed - NinjaTrader talks to its provider

# ⚠ THE PLAYBACK CONNECT GETS ITS OWN BUDGET - IT DOES NOT SCALE WITH THE UI.
#
# Measured 2026-08-28 (NinjaTrader's own trace.20260828.00001.txt): 23 playback
# connects, each 16-30 s - and ONE that sat in PlaybackAdapter.Connect for
# 423 s (16:12:08 -> 16:19:11, derived from Core.Connection.Statistics'
# running mean: 34x38032.3 - 33x26365.3 ms) with zero log lines and zero
# disk reads while it waited, then came back Connected and served RequestBars
# normally. The stall is inside NinjaTrader's encrypted Core (not statically
# readable, see Nt8Tools finding of the same day), so the only place to be
# correct about it is the caller's budget: a bound of SLOW_WAIT (49 s at the
# caller's --stage-wait 20) turned that one healthy-but-slow connect into an
# ERROR, which ended a ~29 h batch of runs that stops on the first one - at
# the measured 1-in-23 rate such a batch could never finish. The budget
# must cover the measured worst case, not the typical one; a connect slower
# than SLOW_WAIT is still REPORTED loudly (see stage 2) so pathology stays
# visible instead of being averaged away.
CONNECT_WAIT = 600

# ⚠ THE PREFLIGHT WAITS FOR A BUSY NINJATRADER - IT DOES NOT FAIL ON ONE.
#
# Stage 0 asks one cheap `transport` question to learn whether the bridge
# answers at all. It used to ask ONCE with a 20 s budget and treat silence as
# "NinjaTrader is not answering - restart it". Seven archived runs ended that
# way with rc 2 and nothing started (result.json of 2026-08-29 10:12,
# 2026-08-31 12:12 / 12:15 / 12:18 / 12:21, 2026-09-01 08:47 and 2026-09-02
# 05:20, every one with wallClockSeconds 88.3-88.4). For the last one
# NinjaTrader's own log says what it was doing: a Playback connect ran from
# 05:15:46 to 05:22:14, 388 s, and the run's result.json is stamped 05:20:59
# - inside that window. Other measured holds of the same kind: 423 / 437 /
# 453 s on 2026-08-28/29, and a restarted NinjaTrader that wrote nothing to
# its log for 735 s on 2026-09-01 (a cold start).
#
# Why a connect silences EVERY request: the AddOn's poller handles triggers
# one at a time on one timer thread - Poll() takes a gate (Monitor.TryEnter)
# and a tick that finds it taken returns without reading a trigger
# (NT8BridgeServer.cs, Poll/HandleTrigger). Stage_Connect runs on that
# thread: it calls Connection.Connect and then waits for Status == Connected
# in a sleep loop bounded by the request budget (NT8BridgeServerPlayback.cs,
# Stage_Connect), so until the connect returns no trigger is read and no
# result written. The heartbeat cannot tell: TryBeat writes heartbeat.json
# through Globals.MainThreadDispatcher, not from the poller thread, so a
# fresh beat says the UI thread is alive - not that the poller is free.
#
# So silence is a WAIT, not a verdict: the driver repeats the probe until one
# is answered or this budget ends. 1800 s is 2.4x the longest measured silence
# (735 s) and 4.0-4.6x the measured connects (388-453 s). A budget that ends
# before the measured worst case turns a healthy NinjaTrader into rc 2 and a
# batch's stop-on-first-error into a dead batch (the same arithmetic as
# CONNECT_WAIT); a
# run that waits costs only the time it waits, and that time is REPORTED -
# every PREFLIGHT_REPORT_S on the console, and as `preflightSeconds` in
# result.json - so a slow preflight stays visible instead of averaged away.
#
# Each probe keeps the SHORT TTL the single ask always had: the AddOn compares
# a trigger's age with its ttlSec when it finally reads it (HandleTrigger) and
# discards a stale one unexecuted, so a probe the poller could not pick up in
# time can never run late. The driver also takes its own unanswered trigger
# back before asking again, so a poller that comes back finds ONE probe, not
# one per probe interval, and the trigger folder is clean after the poll.
#
# ⚠ THE ONE SILENCE THAT IS A VERDICT: NO NinjaTrader.exe PROCESS AT ALL.
#
# heartbeat.json cannot deliver that verdict. The AddOn writes the file when
# it has loaded and rewrites it from the main UI dispatcher, so until a cold
# start has loaded the AddOn the file is absent or left over from the previous
# session - and the cold start of 2026-09-01 answered nothing for 735 s. A
# poll that judged on the file would abort exactly the runs it exists for.
# The process list can deliver it: with no NinjaTrader.exe there is nothing
# that could ever answer, and every further probe is pure cost. So after each
# UNANSWERED probe the poll reads the process list once (restart.process_listed,
# one `tasklist` call; measured 2026-09-02: 0.065-0.072 s per call) and stops
# at once when the list was read and does not name the process. A NinjaTrader
# that is not running then costs one probe - PREFLIGHT_PROBE_WAIT plus the 6 s
# grace of stage(), 26 s, the same 26 s the single ask cost - where the poll
# alone would have spent the whole budget on it. The verdict keeps the error
# string of a spent budget, "preflight: the bridge did not answer" (callers
# match on it), and quotes what tasklist answered on the console and in the
# transcript. The check comes only AFTER an unanswered probe, never before the
# first one: a run whose first probe is answered never makes it, so the fast
# path is unchanged.
#
# ⚠ A PROCESS LIST THAT COULD NOT BE READ IS NOT THAT VERDICT. tasklist can be
# missing from PATH, time out, exit non-zero or print nothing (measured
# 2026-09-02: an unknown filter exits 1 with nothing on stdout, a missing
# executable raises FileNotFoundError, a hung one TimeoutExpired); none of
# that says anything about the process. The first version read every one of
# those as "not running" and ended the run after one probe with a message
# that asserted an empty process list nobody had seen. Such a read is now
# said once per distinct failure, and the silence stays what it was: a wait,
# up to the budget - whose report then says the process is UNKNOWN, not that
# it exists.
PREFLIGHT_WAIT = 1800
PREFLIGHT_PROBE_WAIT = 20    # TTL of one probe - the 20 s the single ask had
PREFLIGHT_REPORT_S = 30      # a console line while waiting, at most this often

# ⚠ ARMING IS NOT A UI ACTION - IT IS A DATA LOAD.
#
# Behind `attach` NinjaTrader loads the series the strategy asks for. That is
# minutes for a tick day, not seconds, and it happens on the UI thread, so the
# whole interface is busy while it runs.
#
# Measured 2026-08-20, the SAME attach on the same day and instrument:
#     1.5 s   NinjaScriptStrategyBaseEnabling1 arrived, run continued
#    26.1 s   nothing reported within the 25 s budget, then NinjaTrader stayed
#             busy for another 31 s
# Same action, different duration - because the second time the data was not
# already there. A budget of 25 s does not measure the action, it measures my
# impatience, and then reports a healthy step as failed.
#
# This is NOT a timeout in the forbidden sense: nothing here waits for a
# duration. The wait still ends on NinjaTrader's own log entry; this number is
# only how long the CALLER is willing to stay - the one bound the rules allow,
# and it has to be bigger than the work behind the step.
ARM_WAIT = 420           # attach: the enable plus whatever data it pulls in

# Single-step mode, set from --step in main(). Module level because `stage()` is
# where every sub-step ends, and a pause that has to be added at each call site is
# a pause someone will forget at exactly the call site that mattered.
STEP_MODE = {"on": False}

# The first mandatory step that failed, for the machine-readable result the
# headless caller gets (result.json). Filled by stage()'s hard stop and by
# mandatory(); empty step means no step-level failure was recorded.
FAIL = {"stage": None, "step": None, "detail": None}

# The bots of this run as (strategy, account, template) triples - exactly one
# entry, filled from --strategy / --account / --template.
BOTS = []


class _Tee:
    """Write to the console AND to a file, without a pipe in between.

    A pipe would buffer, and a buffered prompt is invisible - the run then looks
    hung at the very moment it is waiting for the user.
    """

    def __init__(self, stream, path) -> None:
        self.stream = stream
        self.fh = open(path, "a", encoding="utf-8", buffering=1)

    def write(self, s) -> int:
        self.stream.write(s)
        try:
            self.fh.write(s)
        except ValueError:
            pass
        return len(s)

    def flush(self) -> None:
        self.stream.flush()
        try:
            self.fh.flush()
        except ValueError:
            pass


class _FileOnly:
    """The debug sink of the end-user console mode: everything print()ed goes
    into the transcript file ONLY - the console stays clean for user() lines.

    Requirement (2026-08-21): no debug output in the user console. Where
    debug output is needed it belongs in a second console or in a log file.
    """

    def __init__(self, path) -> None:
        self.fh = open(path, "a", encoding="utf-8", buffering=1)

    def write(self, s) -> int:
        try:
            self.fh.write(s)
        except ValueError:
            pass
        return len(s)

    def flush(self) -> None:
        try:
            self.fh.flush()
        except ValueError:
            pass


# The REAL console, saved before any redirect. In user mode sys.stdout becomes
# the transcript file, so this is the only way user() lines still reach the eye.
_CONSOLE = sys.__stdout__
# user_mode: console shows ONLY the end-user view (one line per phase, plus the
# bot's output-window lines prefixed [BOT]); the full debug transcript goes to
# the --log file. on_progress: the last console write was a CR-overwritten
# progress line, so a normal line must terminate it with a newline first.
_UI = {"user_mode": False, "on_progress": False}


def user(text: str = "") -> None:
    """One line for the END USER's console - and into the transcript, so the
    log file stays the complete record."""
    if not _UI["user_mode"]:
        print(text, flush=True)      # debug mode: the tee reaches both already
        return
    if _UI["on_progress"]:
        _CONSOLE.write("\n")
        _UI["on_progress"] = False
    _CONSOLE.write(text + "\n")
    _CONSOLE.flush()
    print(text, flush=True)          # stdout is the transcript file here


def user_progress(text: str) -> None:
    """A progress line, overwritten in place with CR - never flooding the
    console. The transcript gets it as a normal line."""
    if not _UI["user_mode"]:
        print(text, flush=True)
        return
    width = 110
    _CONSOLE.write("\r" + text[:width].ljust(width))
    _CONSOLE.flush()
    _UI["on_progress"] = True
    print(text, flush=True)


def step_pause(label: str) -> None:
    """Stop until the user presses a key. Only in --step mode."""
    if not STEP_MODE["on"]:
        return
    print("")
    print("   " + "=" * 66)
    print(f"   PAUSED after: {label}")
    print("   Press ENTER to run the next sub-step, or type q + ENTER to stop.")
    print("   " + "=" * 66)
    try:
        answer = input("   > ").strip().lower()
    except EOFError:
        # no console attached - do not silently run on, that would defeat the mode
        raise RuntimeError("--step needs a console; stdin is closed")
    if answer.startswith("q"):
        print("   [key registered: q] stopping. Teardown follows.")
        raise KeyboardInterrupt(f"stopped by the user at: {label}")
    # ⚠ ACKNOWLEDGE THE KEYPRESS IMMEDIATELY.
    #
    # Requirement (2026-08-20): a person always needs feedback on their action.
    # The next stage can take 15 s before it prints anything, so without this line
    # the console sits silent after the key and the only honest reading is "did it
    # register?". A prompt that answers nothing invites a second press, and a
    # second press runs a sub-step nobody asked for.
    print("   [key registered] running the next sub-step ...")


def heartbeat_age() -> float:
    """Age of heartbeat.json in seconds, -1.0 when there is none.

    The AddOn rewrites the file once per second from Globals.MainThreadDispatcher
    (TryBeat), so its age is NinjaTrader's own statement about THAT UI thread.
    It is reported as a measurement, never used as a deadline. And it answers
    for that one thread only: the AddOn reads and answers triggers on its own
    poller thread, so a fresh beat does not say that thread is free - see the
    PREFLIGHT_WAIT block for the measurement behind that.
    """
    try:
        return time.time() - (RES / "heartbeat.json").stat().st_mtime
    except OSError:
        return -1.0


def stage(title: str, req: dict, wait: int = None, show: bool = True,
          report_miss: bool = True) -> dict:
    """Submit one playbackrun stage and wait for its result.

    `ttlSec` is the server-side guarantee that a request we stopped waiting for
    is never executed later.

    `report_miss=False` keeps the NO REACTION report out of the transcript. That
    report is written for a step whose run ENDS on the silence ("not waiting
    longer, that IS the finding"); a caller that asks AGAIN on silence records
    each miss in its own words (preflight()). Measured 2026-09-02 with the real
    stage(), no AddOn, budget 100 s: four unanswered probes wrote that verdict
    four times, with the poll's own "asking again" between them - 25 lines
    whose record contradicted itself once per probe, up to 70 times per
    PREFLIGHT_WAIT. The returned dict is the same either way.

    ⚠ `wait=STAGE_WAIT` IN THE SIGNATURE WAS A BUG, NOT A DEFAULT. A default
    argument is evaluated ONCE, when the def runs at import, so raising
    STAGE_WAIT in main() reached every call that passes `wait=` explicitly and
    none of the ones relying on the default. Measured in a single run: connect
    reported "caller allowed 74 s" while `source check` was still cut off at
    10 s + grace. Resolving it here, at call time, is what makes the setting
    apply.
    """
    if wait is None:
        wait = STAGE_WAIT
    # ⚠ A UNIQUE id per request, never a running number.
    #
    # Measured 2026-08-19: with ids pl001, pl002, ... a teardown read six result
    # files left in the directory by a run on 18.08 17:06 - they existed the
    # instant the trigger was written, so the poll returned yesterday's answer
    # before NinjaTrader had done anything. Five steps reported "ok" and one
    # reported a `dialogset` step that this stage never runs. A reused name turns
    # the result directory into a source of silent wrong answers.
    rid = uuid.uuid4().hex
    req = dict(req)
    req["id"] = rid
    req["kind"] = "playbackrun"
    req["ttlSec"] = wait
    # If this driver is killed, the AddOn restores the baseline itself after
    # this many seconds. A killed process cannot run its own teardown -
    # measured three times on 2026-08-19, each time the user found the mess.
    req["leaseSec"] = 120
    out = RES / (f"playbackrun_{rid}.json")
    try:
        out.unlink()          # belt and braces: never poll onto a pre-existing file
    except FileNotFoundError:
        pass
    t0 = time.time()
    _UI["last_permille"] = -1        # a fresh stage starts its own progress line
    (TRIG / (f"playbackrun_{rid}.json")).write_text(json.dumps(req), encoding="utf-8")
    # ⚠ POLL AT 100 ms, NOT 1 s.
    #
    # The old loop slept a full second between checks, so every stage cost at
    # least ~1 s of pure granularity even when the AddOn answered in 50 ms. Across
    # a dozen setup stages that was most of the setup time - and it was ours, not
    # NinjaTrader's. Measured per stage below, so the claim stays checkable.
    # ⚠ LISTEN LONGER THAN THE SERVER'S OWN BUDGET.
    #
    # `ttlSec` is what the AddOn may spend, and it is allowed to spend ALL of it -
    # its internal waits are bounded by exactly this number. Polling for the same
    # span means an answer written at the boundary arrives one sample too late,
    # and the run reports NO REACTION for a stage that did answer.
    #
    # Measured twice on 2026-08-20: `connect` answered after 122 s while the client
    # gave up at 120 s, and `attach` answered at 14:28:17 with a complete result
    # after the client had already declared it dead at 25 s. Both times the finding
    # was real and the report was wrong about where it came from.
    #
    # The grace is the client's own patience, not a second deadline for the server.
    GRACE = 6.0
    # Live sub-step feed. The AddOn appends one line per completed sub-step to
    # playbackrun_<id>.progress.txt the moment it happens, plus "->" markers
    # naming each call that is about to run on the UI thread. The result JSON
    # only exists once the whole stage RETURNS - measured 2026-08-20 (runs 5
    # and 8): the UI thread died inside the enable and nothing showed how far
    # the stage had got. Tailing this file is what shows WHERE a run dies,
    # while it dies.
    prog = RES / (f"playbackrun_{rid}.progress.txt")
    prog_pos = [0]
    def feed_progress() -> None:
        try:
            data = prog.read_text(encoding="utf-8", errors="replace")
        except OSError:
            return
        new = data[prog_pos[0]:]
        if not new:
            return
        prog_pos[0] = len(data)
        if show:
            for ln in new.splitlines():
                print(f"   [{title.strip()}] {ln}", flush=True)
    # UI-liveness: heartbeat_age() is reported as a measurement, not used as a
    # deadline - the wait stays bounded by ttlSec alone.
    for _i in range(int((wait + GRACE) / 0.1)):
        if out.exists():
            feed_progress()
            d = json.loads(out.read_text(encoding="utf-8"))
            if show:
                print(f"--- {title} ---   ({time.time() - t0:.1f} s)")
                if d.get("status") == "expired":
                    print("   EXPIRED - server dropped it, not executed")
                for s in d.get("steps", []):
                    print(f"   {s['step'].strip()[:30]:<30} "
                          f"{'ok' if s['ok'] else 'FAIL':<6} {s['detail'][:78]}")
            shot(title)          # look at what the stage actually did

            # ⚠ HANDSHAKE BETWEEN THE STEPS, NOT ONLY INSIDE THEM.
            #
            # Every stage waits for its OWN effect and then returns - while
            # NinjaTrader keeps working. The next stage used to fire into exactly
            # that. Measured 2026-08-20: the same `attach` came back in 1.9 s when
            # the user stepped through by hand and left its enable operation
            # Pending when the steps ran back to back. His keypresses had been
            # supplying the missing handshake.
            #
            # `uiidle` posts a delegate at ApplicationIdle and reports when the
            # dispatcher has drained its queue - NinjaTrader's own signal, no
            # duration guessed anywhere. It is sent HERE rather than at each call
            # site, because a handshake that has to be remembered per call is one
            # that will be missing exactly where it mattered.
            if show and req.get("stage") not in ("uiidle", "ntlog", "transport",
                                                 "strategystate"):
                idle = stage("   ui idle", {"stage": "uiidle"}, wait=wait, show=False)
                bad = [s for s in idle.get("steps", []) if not s.get("ok")]
                if not idle.get("steps"):
                    print("   ui idle                        NO ANSWER - NinjaTrader is "
                          "still working; the next step would fire into that")
                elif bad:
                    print(f"   ui idle                        STILL BUSY: {bad[0]['detail'][:70]}")
                else:
                    drained = [s["detail"] for s in idle.get("steps", [])
                               if s["step"].strip().startswith("idle ")]
                    print(f"   ui idle                        {'; '.join(drained)[:74]}")

            # ⚠ HARD STOP ON A FAILED STEP - BEFORE any pause.
            #
            # Asked for on 2026-08-20: hard stop on errors like these. Until then
            # the pause came first, so a FAIL was followed by "Press ENTER to run
            # the next sub-step" - an invitation to build on a step that did not do
            # what it says. Every later measurement would then rest on it.
            #
            # ANY failed step stops the run, not only the ones a call site listed
            # as mandatory. A step that reports FAIL did not do its job; deciding
            # afterwards that it did not matter is how four empty modes once got
            # pulled through and archived as a result.
            # ⚠ ONLY VISIBLE STEPS. Measured 2026-08-20: applied to every call, the
            # hard stop killed a healthy run twice on `strategystate`, an internal
            # READ whose `terminated` step used ok as a data field. A probe that
            # reports a state is not an action that failed. Visible steps are the
            # ones that DO something; those are the ones nothing may be built on.
            failed = [s for s in d.get("steps", [])
                      if not s.get("ok")] if show else []
            if failed:
                print("")
                print("   " + "!" * 66)
                print(f"   HARD STOP - a step reported FAIL in: {title}")
                for s in failed:
                    print(f"   {s['step'].strip()[:28]:28} {s.get('detail', '')[:96]}")
                print("   Nothing further runs on top of it. Teardown follows.")
                print("   " + "!" * 66)
                FAIL.update(stage=title.strip(), step=failed[0]["step"].strip(),
                            detail=failed[0].get("detail", ""))
                raise RuntimeError(f"{title}: {failed[0]['step'].strip()} - "
                                   f"{failed[0].get('detail', '')[:200]}")

            if show:
                step_pause(title)
            return d
        time.sleep(0.1)
        feed_progress()
        # ⚠ SHOW THAT IT IS STILL ALIVE.
        #
        # A stage that talks to NinjaTrader can be quiet for its whole budget -
        # `connect` measured 10.5 s to Connected, and before the fix it sat out
        # the full 25 s. The console showed one line and then nothing, which is
        # indistinguishable from a hang: the operator closed the window on a run
        # that was working (2026-08-24).
        #
        # One line, overwritten in place with CR, so it never scrolls the
        # transcript away - the same shape the closing result block uses. It is
        # written to the CONSOLE only; the transcript already has the stage's own
        # lines and does not need 250 progress samples per stage.
        # The shape is not invented here. It is the progress line the headless
        # runner already writes, so a run over the bridge and a run beside it read
        # the same:
        #     "\r" + <data time> + "Progress | " + label + " | " + pct + " %   "
        # emitted only when the permille CHANGED, which is what keeps it one line
        # instead of a stream. No bar, no seconds - percent, like everywhere else.
        #
        # No time in front here: a stage that is connecting has no stream time
        # yet, and a wall clock on a progress line is exactly what must not
        # appear. The data time comes back on the play loop, which has one.
        if _UI["user_mode"]:
            _pm = int(min(1.0, (time.time() - t0) / max(wait, 1)) * 1000)
            if _pm != _UI.get("last_permille"):
                _UI["last_permille"] = _pm
                # ⚠ PAD TO A FIXED WIDTH. A carriage return moves the cursor, it
                # does not erase: a shorter line leaves the tail of the longer one
                # behind it, and the console then reads
                #     Progress | ntlog | 10.0 %   %   %   1.1 %
                # which is three dead labels and one live number. The blanks are
                # what removes them.
                _CONSOLE.write(f"\r{title} | {_pm / 10.0:.1f} %".ljust(100))
                _CONSOLE.flush()
                _UI["on_progress"] = True
        # Report the heartbeat age every ~10 s while it is stale. The beat is
        # 1 s when the UI thread runs; an age of minutes IS the freeze, live.
        if show and (_i % 100) == 99:
            _age = heartbeat_age()
            if _age > 5.0:
                print(f"   [{title.strip()}] ! heartbeat.json is {_age:.0f} s old "
                      f"(UI dispatcher rewrites it every 1 s - the UI thread is not "
                      f"running)", flush=True)
    # ⚠ THIS VERDICT BELONGS TO A STEP THAT STOPS HERE. A caller that asks
    # again on silence passes report_miss=False and records the miss itself;
    # with the verdict printed anyway, its transcript read "not waiting longer"
    # and "asking again" back to back, once per probe (see the docstring).
    if report_miss:
        print(f"--- {title} ---   ({time.time() - t0:.1f} s)")
        print(f"   ⚠ NO REACTION in {wait + GRACE:.0f} s "
              f"(server budget {wait:d} s + {GRACE:.0f} s grace).")
        print("   A UI action answers in seconds. This means NinjaTrader is busy with")
        print("   work this stage never asked for - not waiting longer, that IS the finding.")
        feed_progress()
        _age = heartbeat_age()
        if _age >= 0:
            print(f"   heartbeat.json age: {_age:.0f} s (the UI dispatcher rewrites it "
                  f"every 1 s while the UI thread runs)")
        if prog_pos[0] == 0:
            print("   progress file: never written - the stage did not reach its first "
                  "sub-step (or the AddOn build predates the progress channel)")
    # Pause here too - but only for a VISIBLE step. Measured 2026-08-20: the
    # teardown's own `restore` got no answer while the transport was running at max
    # speed, and single-step mode stopped there waiting for a keypress. A teardown
    # cleans up; it does not measure, and it must never sit and wait while an armed
    # strategy runs on a moving transport.
    if show:
        step_pause(title + "  [NO REACTION]")
    # The id travels back like it does in an answered result, so a caller that
    # gave up can take its own trigger out of the folder (see preflight()).
    return {"status": "noreaction", "steps": [], "id": rid}


# Where this run's screenshots go. Set by main() before the first stage.
_shots = {"dir": None, "n": 0}


def shot(label: str) -> None:
    """Capture NinjaTrader's own windows after an action.

    An action is not verified until it has been LOOKED at. The bridge takes the
    picture inside NinjaTrader, which is the only way to reach the monitor the
    Control Center actually sits on (measured 2026-08-19: it is at Left=-942, and
    a desktop capture of the primary screen never shows it).

    Never raises - a missing picture must not end a run.
    """
    if _shots["dir"] is None:
        return
    _shots["n"] += 1
    for title in ("Control Center", "Playback"):
        rid = uuid.uuid4().hex
        out = RES / (f"screenshot_{rid}.json")
        png = _shots["dir"] / (f"{_shots['n']:02d}_{label.replace(' ', '-')}_"
                               f"{title.split()[0].lower()}.png")
        try:
            out.unlink()
        except FileNotFoundError:
            pass
        (TRIG / (f"screenshot_{rid}.json")).write_text(
            json.dumps({"id": rid, "kind": "screenshot", "title": title,
                        "out": str(png), "ttlSec": 120}), encoding="utf-8")
        for _ in range(120):
            if out.exists():
                break
            time.sleep(1)


# ---------------------------------------------------------------------------
# NinjaTrader's OWN log, through the AddOn's in-memory Cbi.Log buffer.
#
# ⚠ These three were CALLED in six places and DEFINED NOWHERE - not in this
# file, not in any backup, not in git (checked 2026-08-20 across commit
# 0a403be and every .bak in the workbench). Every one of those paths would have
# died with NameError, and a syntax-only check reports such a file as fine,
# because Python resolves names at run time. Only a name check finds it.
#
# ⚠ MATCH ON THE NAME, NOT ON THE TEXT. A buffer line is
#     time|LogLevel|LogCategory|Name|ResourceType|Message
# and field 3 is the resource name NinjaTrader passed to Log.Process - the same
# on any UI language and across versions. The message is only its rendering.


# How many bot-output lines the AddOn had seen at the last read, so the next call
# asks for exactly what arrived in between - same contract as nt_index().
#
# It is a running COUNT, not a position in the buffer: the buffer is a ring and
# drops from the front, so a slot index would stop meaning anything the moment it
# wraps. The AddOn hands back that count as `nowIndex` and converts it back into a
# slot on its side.
_bot_out = {"since": 0}


def bot_output() -> list:
    """The bot's Print() lines written since the last call.

    NinjaTrader routes every NinjaScript Print() through one static event
    (NinjaTrader.Code.Output.OutputEvent, measured 2026-08-21); the AddOn
    subscribes from its poller and buffers, this reads the buffer and moves the
    index on. So the console can show harness lines and [BOT] lines together
    in one stream.

    ⚠ OUTPUT ONLY, never evidence. Goal 1 is a bot that prints NOTHING - no
    step may wait for a line from here or conclude anything from one.
    """
    d = stage("botout", {"stage": "botout", "since": str(_bot_out["since"])},
              wait=STAGE_WAIT, show=False)
    lines, dropped = [], None
    for s in d.get("steps", []):
        k = s["step"].strip()
        if re.match(r"^o\d+$", k):
            lines.append(s["detail"])
        elif k == "nowIndex":
            try:
                _bot_out["since"] = int(s["detail"])
            except ValueError:
                pass
        elif k == "dropped" and not s.get("ok"):
            dropped = s["detail"]
    if dropped:
        # A ring buffer that overflowed has LOST bot output - say it, never let
        # a gap pass as silence.
        print(f"   [BOT] !! output buffer overflowed: {dropped}", flush=True)
    return lines


def print_bot_output() -> None:
    """Show what the bot printed since the last look, prefixed [BOT] so harness
    and bot lines stay distinguishable in one console."""
    for ln in bot_output():
        for part in ln.splitlines() or [ln]:
            user(f"[BOT] {part}")


def nt_index() -> int:
    """Where NinjaTrader's log buffer stands right now, so a later call can ask
    what arrived IN BETWEEN instead of guessing when to look."""
    d = stage("ntlog index", {"stage": "ntlog", "since": "2000000000"}, show=False)
    for s in d.get("steps", []):
        if s["step"].strip() == "index":
            try:
                return int(s["detail"])
            except ValueError:
                return 0
    return 0


def nt_entries(since: int, contains: str = None) -> list:
    """Every entry NinjaTrader wrote after index `since`.

    With `contains` the AddOn filters SERVER-SIDE, so only matching lines
    count against its cap. That matters because the cap hits the OLDEST
    entries first: Stage_Ntlog walks `for (i = from; i < snap.Count; i++)`
    and breaks at `++shown >= 60`, so an unfiltered call returns the 60
    oldest lines of the window - never the newest.

    Measured 2026-08-30: a playback connect emitted 21,578 lines in 11.6 s
    (one per recording, from the "12-99" alias junctions the NRD decoder
    needs). The buffer holds NtLogMax = 2000, so NtLogOffset returned 0 -
    correctly, "everything still held" - and the caller received that
    window's first 60, all of them alias noise. NinjaTrader had logged
    "Connected" as the YOUNGEST entry of the same window. The check
    reported "never logged ... 60 entries seen" and the run aborted 9 s
    later, on a connection that was up.
    """
    req = {"stage": "ntlog", "since": str(since)}
    if contains:
        req["contains"] = contains
    d = stage("ntlog", req, show=False)
    return [s["detail"] for s in d.get("steps", [])
            if re.match(r"^e\d+$", s["step"].strip())]


def nt_name(entry: str) -> str:
    """The resource name of one buffer line - field 3, or "" if the line is short."""
    parts = entry.split("|")
    return parts[3].strip() if len(parts) > 3 else ""


def nt_check(since: int, expect: str = None, contains: str = None, what: str = "") -> list:
    """Report what NinjaTrader itself logged since `since`, and abort on an error.

    `expect` is a resource NAME, not a message. With `contains`, the message of
    the matching entry must also carry that text - the same pair the AddOn's
    WaitForLogName uses, so both sides identify an event the same way.

    Fails loudly: an Error entry, or a missing expectation, raises. A step that
    reports "ok" for an action NinjaTrader complained about is worse than no
    check at all.
    """
    entries = nt_entries(since)
    # The expectation gets its OWN, server-side filtered query. The
    # unfiltered list above is capped at the 60 OLDEST lines of the window
    # (Stage_Ntlog: `if (++shown >= 60) break;`), while the awaited event is
    # always among the newest - so searching it there succeeds only while
    # the buffer is quiet. See nt_entries for the measurement.
    expect_pool = nt_entries(since, contains=expect) if expect else entries
    # Sound playback is cosmetics, not the action under test. Measured
    # 2026-08-21: in an RDP session without an audio device NinjaTrader logs
    #   |Error|...|CoreSoundThreadProc|...|Failed to play sound file
    #   '...Connected.wav': BadDeviceId calling waveOutOpen
    # for the CONNECT chime - the connection itself was up. Treating that as
    # fatal aborted the run before it ever reached the stage being tested.
    #
    # Measured 2026-08-30: the same holds for the alias junctions the NRD
    # decoder needs. `db\replay\<SYM> 12-99` is a junction onto the
    # `<SYM> ##-##` store - NT8 formats the MaxDate sentinel expiry
    # 2099-12-01 as "12-99" and DumpMarketDepth looks there (a store-side
    # junction of that name points back at the real store). NinjaTrader then SEES
    # EVERY RECORDING TWICE and logs the second view as
    #   |Error|Connection|AdapterPlaybackAdapterInit|Resource|Playback file
    #   '...\db\replay\YM 12-99\20200715.nrd' is corrupted and will be skipped
    # It says "will be skipped" and means it. Proof, two runs of the same day
    # on the same data: a headless Market Replay run (NinjaTrader's engine
    # hosted without its GUI) logged 21,578 of
    # these lines in its own instance log AND completed - "Range played;
    # NowEst=27.08.2026 23:59:59", exit 0, 34,010,547 Cbi events, 100 % of the
    # range. The bridge aborted at stage 'connect' with rc 2 on the identical
    # store. So the recordings are intact and NinjaTrader carries on; only this
    # check stopped.
    #
    # Narrowed on a count, not on a hunch: in that same log 21,578 of 21,578
    # such lines name a " 12-99" path and none names anything else. A genuinely
    # damaged recording surfaces under its REAL contract (" ##-##" or e.g.
    # "MNQ 09-26") and still aborts the run, as it must.
    #
    # Only these two measured patterns are excused; every other Error aborts.
    errors = [e for e in entries
              if "|Error|" in e
              and "CoreSoundThreadProc" not in e
              and not ("is corrupted and will be skipped" in e
                       and " 12-99" in e)]
    if errors:
        raise RuntimeError(f"NinjaTrader logged an error during '{what}': {errors[0][:200]}")
    if expect:
        hit = [e for e in expect_pool
               if nt_name(e) == expect and (not contains or contains.lower() in e.lower())]
        if not hit and not entries:
            # ⚠ NO SOURCE IS NOT A NEGATIVE RESULT.
            #
            # With ZERO entries the log channel said nothing at all - it cannot
            # confirm the effect and it cannot deny it either. Measured
            # 2026-08-24 in the GUI-less host: the connection object reported
            # "Status=Connected after 11369 ms" while this read 0 entries, and
            # aborting there threw away a connect that had demonstrably worked.
            #
            # An empty instrument is reported as empty. The moment entries DO
            # arrive and the expected one is missing, that is evidence of
            # absence and still aborts, below.
            print(f"   nt: no log entries available during '{what}' - this channel "
                  f"could not confirm {expect}"
                  f"{'/' + contains if contains else ''}. Not treated as its absence; "
                  f"the step's own read-back stands.")
        elif not hit:
            raise RuntimeError(
                f"NinjaTrader never logged {expect}"
                f"{'/' + contains if contains else ''} during '{what}' - "
                f"{len(entries):d} entries seen. "
                f"The action returned, its effect did not appear.")
        else:
            print(f"   nt: {hit[0][:150]}")
    elif entries:
        print(f"   nt: {len(entries):d} entr{'y' if len(entries) == 1 else 'ies'} "
              f"since '{what}', last: {entries[-1][:120]}")
    return entries


def clock_vs_end() -> tuple:
    """(NowEst, moving, ToEst) - the run is over when NowEst reaches ToEst.

    Both values come from PlaybackAdapter, so this works for any strategy and
    needs no GUI element.
    """
    d = stage("clockend", {"stage": "transport"}, wait=STAGE_WAIT, show=False)
    v = {str(s.get("step", "")).strip(): str(s.get("detail", "")) for s in d.get("steps", [])}
    raw = v.get("NowEst", "")
    a_, _, b_ = raw.partition("->")
    now = (b_.strip() or a_.strip()) or None
    moving = bool(a_.strip() and b_.strip() and a_.strip() != b_.strip())
    to = v.get("ToEst", "").strip() or None
    return now, moving, to


def preflight(budget: float = None, clock=time.monotonic, sleep=time.sleep,
              process=None) -> dict:
    """Ask the bridge ONE cheap question - and keep asking until it answers.

    Returns {"answered": bool, "steps": list, "seconds": float, "probes": int,
    "process": dict | None} and never raises: the caller decides what an
    unanswered preflight means. `seconds` is the wall clock the poll took -
    the one quantity a wall clock is right for - and goes into result.json as
    `preflightSeconds`. `process` is the LAST reading of the process list,
    {"listed": True | False | None, "detail": str} as restart.process_listed
    returns it, or None when no reading was made (the first probe answered).
    An unanswered preflight always carries one: `listed` is False only when
    the poll stopped because the list was read and does not name
    NinjaTrader.exe (see below), None when the last read failed.

    ⚠ SILENCE IS A WAIT, NOT A VERDICT - up to `budget` (PREFLIGHT_WAIT). See
    that block for the measurements: a Playback connect keeps the AddOn's
    single poller thread busy for 388-453 s, a cold start answers nothing for
    735 s, and no request of any kind is answered meanwhile.

    ⚠ EXCEPT ONE: NO NinjaTrader.exe PROCESS. After each unanswered probe the
    poll reads the process list once (`process`, restart.process_listed by
    default) and stops at once when the list was read and does not name the
    process - nothing could ever answer, so a NinjaTrader that is not running
    costs one probe, not the budget. Never before the first probe: an
    answered fast path makes no check. A list that could NOT be read is no
    verdict: it is said on the console once per distinct failure, and the
    poll goes on waiting.

    Each probe is one `transport` request with the short TTL
    PREFLIGHT_PROBE_WAIT, so the AddOn discards a probe it reads late instead
    of executing it (HandleTrigger compares the trigger's age with ttlSec).
    An unanswered probe is taken back out of the trigger folder before the
    next one is written; that keeps the folder at ONE pending probe and leaves
    it empty after the poll. A result that came back WITHOUT steps (an
    `expired` or `error` file) is a reaction, not an answer: the poller is
    reading triggers again, so the next probe follows after one poller tick
    (the AddOn polls once per second) instead of in a hot loop.

    An unanswered probe that is asked again leaves ONE line in the transcript,
    the poll's own "no answer to probe N ... asking again" (plus the user line
    every PREFLIGHT_REPORT_S); the last one is recorded by the caller's verdict.
    The probes are sent with report_miss=False: stage()'s NO REACTION report
    declares the silence final, and this poll does not - see the stage()
    docstring for the transcript that had both, once per probe.

    `clock`, `sleep` and `process` are parameters so a test can run the poll
    in milliseconds and decide what the process list says; the defaults are
    the real ones.
    """
    if budget is None:
        budget = PREFLIGHT_WAIT      # at call time - see `wait` in stage()
    if process is None:
        process = ninjatrader_process_listed     # at call time, like `budget`
    t0 = clock()
    last_report = None
    last_process = None          # the last reading of the process list
    unreadable_said = None       # the failure text already on the console
    probes = 0
    while True:
        probes += 1
        # report_miss=False: the miss is recorded below, in the poll's own
        # words; the stage's NO REACTION verdict is written for a step that
        # stops on silence, and this one asks again.
        d = stage("0 preflight", {"stage": "transport", "sampleMs": "200"},
                  wait=PREFLIGHT_PROBE_WAIT, show=False, report_miss=False)
        elapsed = clock() - t0
        if d.get("steps"):
            if probes > 1:
                # Loud, like SLOW CONNECT: a run that had to wait says so on
                # the console, in seconds of wall clock.
                user(f"Bridge answered after {elapsed:.0f} s ({probes:d} probes) - "
                     f"NinjaTrader was busy; the run continues.")
            return {"answered": True, "steps": d.get("steps", []),
                    "seconds": elapsed, "probes": probes, "process": last_process}
        # Take the unanswered probe back. Gone already = the AddOn consumed it
        # and is either executing it (blocked) or has discarded it as expired.
        # A file that cannot be removed is left to the TTL, which guarantees
        # the same thing: it is never executed late.
        rid = d.get("id")
        if rid:
            try:
                (TRIG / (f"playbackrun_{rid}.json")).unlink()
            except OSError:
                pass
        # The one silence that is a verdict - see the PREFLIGHT_WAIT block.
        # Read here, after the probe was taken back and before the budget is
        # judged, so a dead process is named even on the round the budget ends.
        listed, detail = process()
        last_process = {"listed": listed, "detail": detail}
        if listed is False:
            user(f"NinjaTrader.exe is not running - {detail}; no process can "
                 f"answer a probe, so the poll stops after probe {probes:d} "
                 f"({elapsed:.0f} s of {budget:.0f} s).")
            return {"answered": False, "steps": [], "seconds": elapsed,
                    "probes": probes, "process": last_process}
        if listed is None and detail != unreadable_said:
            # Not a verdict - nothing was measured about the process. Said
            # once per distinct failure, not once per probe.
            unreadable_said = detail
            user(f"The process list could not be read after probe {probes:d} "
                 f"({detail}) - whether NinjaTrader.exe is running is not "
                 f"known, so the silence stays a wait.")
        if elapsed >= budget:
            return {"answered": False, "steps": [], "seconds": elapsed,
                    "probes": probes, "process": last_process}
        print(f"   [0 preflight] no answer to probe {probes:d} "
              f"({elapsed:.0f} s of {budget:.0f} s) - asking again", flush=True)
        if last_report is None or elapsed - last_report >= PREFLIGHT_REPORT_S:
            last_report = elapsed
            age = heartbeat_age()
            beat = (f" (heartbeat.json is {age:.0f} s old - the main UI thread's "
                    f"beat, not the poller thread's)" if age >= 0 else "")
            user(f"NinjaTrader is busy - no answer for {elapsed:.0f} s, "
                 f"waiting up to {budget:.0f} s{beat}")
        if d.get("status") != "noreaction":
            sleep(1.0)


def restore_baseline(strategy: str) -> list:
    """Put NinjaTrader back with ONE call. Never raises.

    ⚠ The AddOn does the work; this only reports it.

    It used to be ten stages from here - park, disable, remove, answer, close -
    each with its own budget. Measured 2026-08-19: whenever NinjaTrader was busy
    (which is exactly when a cleanup is needed) that cost minutes, and the user
    watched an enabled strategy sit there while this tool "cleaned up". The
    AddOn's `restore` stage disconnects first - the universal abort, and the one
    thing proven to stop the transport - then clears rows and dialogs, and
    answers in about 5 s.
    """
    d = stage("restore", {"stage": "restore"}, wait=30, show=False)
    steps = d.get("steps", [])
    ok = bool(steps) and all(s.get("ok") for s in steps)
    if not steps:
        print("   teardown: NO REACTION - NinjaTrader is busy. Cleanup is queued, "
              "not repeated; piling more requests on a blocked queue is what "
              "produced the backlog on 2026-08-19.")
    else:
        for s in steps:
            print(f"   teardown {s['step'].strip()[:16]:<16} "
                  f"{'ok' if s.get('ok') else 'PROBLEM':<6} "
                  f"{str(s.get('detail', ''))[:50]}")
    print(f"   baseline {'clean' if ok else 'NOT clean'}")
    return [{"step": "restore", "ok": ok, "detail": d}]


def _iso_day(stamp: str) -> str:
    """Day part of a NinjaTrader stamp as ISO, so it can be compared with --to.

    ⚠ The two sides speak different date formats. NinjaTrader formats the stamp
    for the machine's locale, so it can arrive as `dd.MM.yyyy HH:mm:ss`, while
    `--to` is always ISO `yyyy-MM-dd`. The old check compared the two AS STRINGS:

        '11.08.2026' < '2026-08-10'   ->   True

    which is broken in both directions and was measured on 2026-08-19:
      * it fired on days 01-20 of ANY month, so a complete run was reported as
        "cut short" - it flagged this very comparison run, and the reference run
        from the dialog path carries the same false MISMATCH.txt;
      * it can never fire on days 21-31, so a genuinely truncated run there passes
        unnoticed.

    A check that both cries wolf and goes blind is worse than none, because the
    false alarms teach everyone to ignore the true ones.
    """
    s = (stamp or "").strip()
    if len(s) >= 10 and s[2] == "." and s[5] == ".":
        return f"{s[6:10]}-{s[3:5]}-{s[0:2]}"
    if len(s) >= 10 and s[4] == "-" and s[7] == "-":
        return s[:10]
    return ""



def wait_for_enable_event(strategy: str, since: int, timeout: float) -> str | None:
    """Wait for NinjaTrader's own "Enabling NinjaScript strategy" entry.

    Reads the AddOn's buffer of Cbi.Log events - not the log FILE, and never the
    Output window, which is entirely bot-written. Any Error entry in between aborts
    immediately instead of waiting out the deadline.
    """
    needle = f"Enabling NinjaScript strategy '{strategy}"
    deadline = time.time() + timeout
    while time.time() < deadline:
        # Same cap as in nt_check: unfiltered this returns the 60 OLDEST
        # lines, and the enable line is the newest. Filter server-side.
        entries = nt_entries(since, contains=needle)
        for e in entries:
            if "|Error|" in e:
                raise RuntimeError(f"NinjaTrader reported an error while arming: {e[:200]}")
        hit = [e for e in entries if needle.lower() in e.lower()]
        if hit:
            return hit[0][:160]
        time.sleep(1.0)
    return None



# --from/--to are checked at argument time - by the CLI parser and by this
# driver's own parser (iso_date, date_order_error) - and by no stage afterwards.
#
# Measured 2026-09-01: seven archived runs (05:16 to 08:23) carried
# "to": "2026-13-07". Nothing checked the value on the way in - the CLI
# declared both dates as plain strings and the AddOn hands them to
# DateTime.Parse as they arrive - so every one of them ran stage 1a, lasted
# 45-58 s of wall clock, and died with
#     2 connect: exception - FormatException: String was not recognized as
#     a valid DateTime.
# A month 13 must never reach NinjaTrader.
#
# The shape is pinned by the pattern and not by date.fromisoformat alone:
# measured on Python 3.11.9, fromisoformat also accepts "20260607" and
# "2026-W23-1", two shapes nobody documented. The pattern refuses the shape
# (2026-7-7, 07/07/2026), fromisoformat refuses the calendar (month 13,
# 30 February), and both refusals name the accepted form and the value sent.
DATE_FORMAT = "YYYY-MM-DD"
_DATE_SHAPE = re.compile(r"^\d{4}-\d{2}-\d{2}$")


def iso_date(value: str) -> str:
    """argparse `type=` for --from and --to: exactly YYYY-MM-DD and a real
    calendar day, returned as the SAME string - the request JSON and the
    archive carry what was typed. Refuses with the accepted form and the
    offending value."""
    if not _DATE_SHAPE.match(value):
        raise argparse.ArgumentTypeError(
            f"'{value}' is not a date of the form {DATE_FORMAT}")
    try:
        date.fromisoformat(value)
    except ValueError as ex:
        raise argparse.ArgumentTypeError(
            f"'{value}' is not a calendar date ({DATE_FORMAT}: {ex})") from None
    return value


def order_notice_counts(detail: str) -> tuple[int, int]:
    """(this sample, this run) from the AddOn's "order notices" step text.

    The `strategystate` stage answers `N dismissed this sample, M in this run`
    for NinjaTrader's order-rejection notice ("Stop price can't be changed above
    the market") it confirmed - see DismissOrderRejectNotices in the AddOn. An
    absent or unreadable step (an AddOn built before the step existed) reads as
    (0, 0): nothing was confirmed, nothing is claimed.
    """
    m = re.match(r"\s*(\d+) dismissed this sample, (\d+) in this run", detail or "")
    if not m:
        return 0, 0
    return int(m.group(1)), int(m.group(2))


def date_order_error(date_from: str, date_to: str) -> str | None:
    """The check `type=` cannot make, because it sees one value at a time:
    the range end must not lie before its start (the same day is a one-day
    run). Returns the refusal text, or None for a usable pair."""
    if date.fromisoformat(date_to) < date.fromisoformat(date_from):
        return (f"--to {date_to} lies before --from {date_from}; the range "
                f"end must be the same day as the start or later")
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--nt8-dir", dest="nt8_dir", default=None,
                    help="NinjaTrader data directory this run talks to;"
                         " default ~/Documents/NinjaTrader 8")
    ap.add_argument("--archive", default=None,
                    help="where the finished run is written;"
                         " default <nt8-dir>/NT8Bridge/runs")
    ap.add_argument("--bot-logs", dest="bot_logs", default=None,
                    help="directory a strategy writes its own logs to. Any"
                         " entry that appears there during the run is copied"
                         " into the archive. Off unless given.")
    ap.add_argument("--name", required=True)
    ap.add_argument("--source", choices=["historical", "marketreplay"], required=True)
    ap.add_argument("--tick-replay", dest="tickreplay", choices=["true", "false"], required=True)
    # No default: a strategy name is a fact about the caller's installation,
    # and guessing one turns "you forgot an argument" into "that strategy does
    # not exist", which points at the wrong problem.
    ap.add_argument("--strategy", required=True)
    ap.add_argument("--account", default=None,
                    help="account the strategy runs on; default is the playback"
                         " account NinjaTrader names itself. An account that does"
                         " not exist stops the run - it is not swapped for another.")
    ap.add_argument("--instrument", default="MNQ 09-26")
    # Only these three build a bar-type token below; anything else used to fall
    # through to the Minute token, so `--bars-type Day` silently ran a Minute
    # series. argparse rejects it now instead.
    ap.add_argument("--bars-type", dest="barsperiod", choices=["Tick", "Minute", "Wave"], default="Wave")
    ap.add_argument("--bars-value", dest="barvalue", default="120")
    ap.add_argument("--trading-hours", dest="tradinghours", default=None,
                    help="trading-hours template name to force on the strategy "
                         "(exact installed name; the attach step lists all names "
                         "on a miss). Default: the instrument's own template.")
    # type=iso_date: refused at parse time, before anything below runs -
    # see the block above iso_date for the seven runs that made it so.
    ap.add_argument("--from", dest="date_from", required=True, type=iso_date,
                    help=f"range start EST, {DATE_FORMAT}")
    ap.add_argument("--to", dest="date_to", required=True, type=iso_date,
                    help=f"range end EST, {DATE_FORMAT}, inclusive; the same day "
                         "as --from or later")
    ap.add_argument("--max-wait", dest="maxwait", type=int, default=40,
                    help="play-loop budget, in units of 30 s")
    # ⚠ SINGLE-STEP MODE. The run stops after every visible sub-step and waits for
    # a keypress. The pause belongs HERE, in the script, not in whoever is watching
    # it: a step that only pauses when someone remembers to pause it is not a
    # controlled run. Requested by the user on 2026-08-20 so each sub-step can be
    # inspected before the next one changes the state it was measured in.
    ap.add_argument("--step", action="store_true",
                    help="stop after every sub-step and wait for a keypress")
    # (The interactive --attach-only hold mode - load the strategy and then
    # pause - was removed 2026-08-21 once the enable freeze was
    # fixed at its root: the run is headless now. The AddOn keeps the `arm`
    # stage and attach's enable:false for hand-driven diagnosis.)
    # ⚠ EMPTY for the Playback path, and that is not an oversight.
    #
    # Playback needs every other connection DOWN - when playback is switched on,
    # every other connection MUST be closed first (2026-08-20).
    # `keep` exists for a future Strategy Analyzer path, which needs the
    # opposite: a LIVE connection up and no Playback at all.
    #
    # It doubles as a free build probe. A name that matches nothing changes no
    # outcome - everything is disconnected either way - but only the newer AddOn
    # reports a `kept up on request` step, so the answer says which build the
    # running process is executing.
    ap.add_argument("--keep", default=None,
                    help="connection names that must NOT be disconnected, comma "
                         "separated. Leave empty for Playback.")
    # A transcript that does not depend on a pipe. `tee` between the console and
    # this process would also buffer the prompts, and a prompt nobody sees is a
    # run that looks hung - so the file is written from inside instead.
    # The machine-readable contract for a headless caller (the nt8bridge CLI):
    # the same result.json every run archives, written additionally to a path
    # the caller chose, so it never has to parse human output for the verdict.
    ap.add_argument("--result", default=None,
                    help="also write the run's result.json to this path "
                         "(for the headless CLI caller)")
    ap.add_argument("--log", default=None,
                    help="write everything printed to this file as well")

    ap.add_argument("--rc-file", dest="rc_file", default=None,
                    help="also write the exit code to this file. Behind a "
                         "pipe a cmd chain sees the pipe's code, not ours.")
    ap.add_argument("--stage-wait", dest="stage_wait", type=int, default=None,
                    help="seconds a single stage may take (default 10). The "
                         "connect budget scales with it. Raise it on a slow "
                         "machine or a busy NinjaTrader: the round trip alone "
                         "costs about 3 s per request, and a logical step is "
                         "three of them.")
    ap.add_argument("--debug-console", action="store_true",
                    help="mirror the FULL debug transcript to the console. "
                         "Default: the console shows only the end-user view "
                         "(harness + bot output window); debug goes to --log.")
    # `dialog` is gone, not deprecated: the AddOn no longer contains a single click, and
    # the dialog path WAS the clicking (context menu, tree selection, OK button). The
    # option is kept only so an old command line fails LOUDLY instead of silently doing
    # something else.
    ap.add_argument("--setup", choices=["dialog", "template"], default="template",
                    help="template = the click-free `attach` call (the only path). "
                         "dialog = removed on 2026-08-19, aborts with an explanation.")
    ap.add_argument("--template", default=None,
                    help="template name, or a full path to the .xml. The bare name is "
                         "resolved in the folder NinjaTrader reports for THIS bot.")
    a = ap.parse_args()
    # The order check needs both values, which `type=` sees one at a time.
    # Still at argument time: nothing has been written or sent yet.
    _order = date_order_error(a.date_from, a.date_to)
    if _order:
        ap.error(_order)

    # Before the first request: TRIG and RES are read by every helper below,
    # so a late override would split one run across two data directories.
    if a.nt8_dir:
        set_nt8_dir(a.nt8_dir)

    # The stage budgets, before the first stage is sent. See the block comment
    # on STAGE_WAIT: headless, one dispatcher carries both the stages and the
    # message pump, so the GUI's numbers are too tight there.
    global STAGE_WAIT, SLOW_WAIT, CONNECT_WAIT, PREFLIGHT_WAIT
    if a.stage_wait:
        STAGE_WAIT = a.stage_wait
    SLOW_WAIT = int(STAGE_WAIT * 2.5)     # the ratio the GUI numbers already had
    # never below its measured floor, however small --stage-wait is; a huge
    # --stage-wait may still raise it further (see the CONNECT_WAIT block)
    CONNECT_WAIT = max(600, SLOW_WAIT)
    # the same floor rule for the preflight (see the PREFLIGHT_WAIT block)
    PREFLIGHT_WAIT = max(1800, SLOW_WAIT)

    # AFTER set_nt8_dir, never before: --nt8-dir recomputes ARCHIVE, so an
    # explicit --archive applied first would be silently overwritten.
    global ARCHIVE, BOTLOGS
    if a.archive:
        ARCHIVE = Path(a.archive)
    if a.bot_logs:
        BOTLOGS = Path(a.bot_logs)

    # Set BEFORE anything is printed, so the transcript carries the whole run and
    # the first sub-step already pauses.
    #
    # Two console modes. Only three things belong in the end-user console:
    # what the CLI itself prints, one harness line per phase, and everything
    # the strategy wrote to the output window:
    #   default        console = end-user view, --log file = full debug transcript
    #   --debug-console console = everything (the old tee), for debugging only
    if not a.log and not a.debug_console:
        # The debug transcript must land SOMEWHERE - without it a failed run
        # would be undiagnosable. No wall-clock stamp in the name (name + pid
        # identify the file); the content carries data-stream times only.
        Path("logs").mkdir(exist_ok=True)
        a.log = str(Path("logs")
                    / f"{a.name or 'playbackrun'}_pid{os.getpid():d}_transcript.log")
    if a.debug_console:
        if a.log:
            sys.stdout = _Tee(sys.stdout, a.log)
            sys.stderr = _Tee(sys.stderr, a.log)
            print(f"=== transcript -> {a.log} ===")
    else:
        _UI["user_mode"] = True
        sys.stdout = _FileOnly(a.log)
        # stderr keeps reaching the console: an uncaught traceback IS the
        # meaningful error message the caller was promised.
        sys.stderr = _Tee(sys.__stderr__, a.log)
        print(f"=== transcript -> {a.log} ===")
    # ONE bot per pass. The driver accepted several until 2026-08-26, when the
    # idea was measured: two bots on one connection took 1163.8 s for a week of
    # Market Replay against roughly 606 s for one, so they SHARE the replay walk
    # rather than parallelising it. The two-variant pass came to 1214.6 s against
    # 1311.7 s sequential - 7.4 %, and that saving was the second connect it
    # avoided, not the bots.
    BOTS.clear()
    BOTS.append((a.strategy, (a.account or "").strip(), (a.template or "").strip()))


    STEP_MODE["on"] = bool(a.step)
    if a.step:
        print("=== SINGLE-STEP MODE: stops after every sub-step ===")

    # The end-user header: a parameter table first, then one line per phase,
    # then the progress line.
    user(f"NT8 playback run: {a.name or a.strategy}")
    user("-" * 62)
    for k, v in (("Strategy", ", ".join(f"{b}{'@' + acc if acc else ''}"
                                        f"{' [' + tm + ']' if tm else ''}"
                                        for b, acc, tm in BOTS)),
                 ("Instrument", a.instrument),
                 ("Source", f"{a.source}  (TickReplay {a.tickreplay})"),
                 ("Period", f"{a.barsperiod} {a.barvalue}"),
                 ("Window", f"{a.date_from} .. {a.date_to}"),
                 ("Template", a.template or "(bot defaults)"),
                 ("Transcript", a.log or "-")):
        user(f"| {k:12} | {v}")
    user("-" * 62)

    # ⚠ --template IS OPTIONAL, and it has to be.
    #
    # The goal of this remote control is to be BOT-INDEPENDENT and to work with a
    # completely empty strategy. Demanding a hand-made template per bot is the
    # opposite of that: a bot with no parameters has nothing to put in one.
    #
    # The bridge never needed it. `attach` handles the empty case itself and says
    # so - measured in NT8BridgeServerPlayback.cs:
    #     if (string.IsNullOrWhiteSpace(tmplD)) { ... step("template", true, "none - defaults"); }
    # Only this script forbade it. Instrument, bar type, range and Tick Replay come
    # from the request either way; a template only adds the bot's own parameters.
    if a.setup == "dialog":
        ap.error("--setup dialog no longer exists. Configuring through the Strategies "
                 "window meant clicking (context menu, tree selection, OK button), and the "
                 "AddOn is click-free since 2026-08-19. Use: --setup template --template <name>")

    target = ARCHIVE / (f"{a.name}_{a.instrument.replace(' ', '')}_"
                        f"{a.date_from.replace('-', '')}_{a.date_to.replace('-', '')}")
    n = 1
    base = target
    while target.exists():
        n += 1
        target = base.with_name(base.name + f"__{n:d}")
    target.mkdir(parents=True)
    _shots["dir"] = target / "shots"
    _shots["dir"].mkdir(exist_ok=True)
    log = []

    def note(d: dict) -> dict:
        log.append(d)
        return d

    def mandatory(d: dict, *keys) -> dict:
        """Abort on the first failed MANDATORY step.

        Measured 2026-08-18/19: the chain pulled four modes through although
        enabling failed in every one - four empty cells, 90 minutes, and
        "archived" at the end. A run that logs failures and carries on is worse
        than one that stops: it looks like a result.
        """
        bad = [s for s in d.get("steps", [])
               if not s.get("ok") and any(k.lower() in s["step"].lower() for k in keys)]
        if not d.get("steps"):
            bad = [{"step": "(no answer)", "detail": "stage returned nothing"}]
        if d.get("status") == "expired":
            bad = [{"step": "(expired)", "detail": "server dropped the request, not executed"}]
        if bad:
            print("")
            print("⚠ ABORT - mandatory step failed:")
            for s in bad:
                user(f"ERROR: {s['step'].strip()} - {s.get('detail', '')[:140]}")
            for s in bad:
                print(f"   {s['step'].strip()[:28]:28} {s.get('detail', '')[:90]}")
            (target / "ABORT.txt").write_text(
                json.dumps(bad, indent=2, ensure_ascii=False), encoding="utf-8")
            FAIL.update(stage=d.get("stage") or FAIL.get("stage"),
                        step=bad[0]["step"].strip(),
                        detail=bad[0].get("detail", ""))
            raise RuntimeError(f"mandatory step failed: {bad[0]['step'].strip()} - "
                               f"{bad[0].get('detail', '')[:200]}")
        return d

    # BOTLOGS is None unless --bot-logs named a directory, so the guard tests
    # the constant itself before touching it.
    before = (set(p.name for p in BOTLOGS.iterdir())
              if BOTLOGS and BOTLOGS.exists() else set())
    # ⚠ Wall-clock duration - the one quantity a wall clock is right for.
    # It says how long the MACHINE took; everything about the DATA is stamped
    # from the stream. The assignment was lost in a restructure and the run
    # crashed with NameError while reporting (2026-08-19) - after the
    # measurement, so nothing was falsified, but the run counted as failed.
    wall_start = time.time()
    rc = 0
    teardown = []
    # For the machine-readable result the headless caller reads (result.json):
    # how the run ended, and the message of the exception that ended it early.
    end_reason = "aborted-before-play"
    err_text = None
    terminated = played = False
    end_gap = False   # pre-set like `played`: an abort before the play loop
                      # must not NameError in the end_reason chain
    # Wall clock of the preflight poll (stage 0), for result.json. Pre-set so
    # an abort before it ran still reports a number.
    preflight_s = 0.0

    try:
        # ⚠ CONFIGURE WHILE DISCONNECTED. Measured 2026-08-19, both halves:
        #
        #   player running  -> the ObjectDialog VANISHES between `select strategy`
        #                      (ok) and `data series` ("no ObjectDialog found");
        #                      "Enable" comes back greyed; bridge calls queue for
        #                      minutes behind the transport.
        #   disconnected    -> the same seven fields all write and read back:
        #                      Type=Wave, Wave Size=120, Tick Replay=False,
        #                      Account=Playback101, Label, Instrument=MNQ 09-26.
        #
        # A transport at Max speed saturates NinjaTrader's single UI thread, and the
        # dialog does not survive that. Disconnecting is also the ONLY reliable stop
        # found: toggling btnPlay six times left the clock running, while a
        # disconnect gave timer.Enabled=False and RealtimeTickCount delta=0.
        # ⚠ CONNECTED **AND** PARKED before touching the dialog. Two separate
        # requirements, and confusing them cost a NinjaTrader crash on 2026-08-19:
        #
        #   connection DOWN -> the account does not exist. The Account combo still
        #       reports `wrote ComboBox.Text=Playback101 read=Playback101` - that is
        #       the TEXT, not the bound account - and confirming the dialog raises
        #       "Trying to add realtime strategy with no account"
        #       (Cbi.Log.Assert <- StrategiesGrid.StrategyAdd). NinjaTrader had to be
        #       restarted. A read-back that checks a string instead of the binding is
        #       not a verification.
        #
        #   transport RUNNING -> the ObjectDialog vanishes between two bridge calls
        #       and "Enable" comes back greyed; a Max-speed player saturates
        #       NinjaTrader's single UI thread.
        #
        # Measured directly after `connect`: timer.Enabled=True, RealtimeTickCount
        # +1/s (the idle beat), NowEst 10.08. 00:00:00 -> 00:00:02 over six seconds.
        # Connected and quiet - which is exactly the window to configure in.
        # ================================================================
        # ⚠ PREFLIGHT: DOES THE BRIDGE ANSWER AT ALL? IF NOT YET: WAIT.
        #
        # Measured 2026-08-20: a run was started into a NinjaTrader that was still
        # blocked from the previous attempt. Its very first request - reading the
        # log index - got no answer in 16 s, `alloff` none in 96 s, and the whole
        # transcript read like a fresh failure of step 1. It was not: the state was
        # already broken before the run began, and every line after that measured
        # the wrong thing.
        #
        # So the first thing a run does is ask ONE cheap question - and when it
        # goes unanswered, ask it AGAIN, up to PREFLIGHT_WAIT (see that block
        # for the measurements). Nothing else is sent meanwhile: piling
        # requests onto a blocked queue is what produced the backlog on
        # 2026-08-19, and a report built on top of it is worse than no report.
        # Each probe carries a short TTL and is taken back when unanswered, so
        # the queue holds at most this one question.
        #
        # ⚠ SILENCE IS NOT "RESTART NINJATRADER". Seven archived runs between
        # 2026-08-29 and 2026-09-02 aborted here after one 20 s ask, with the
        # advice to clear the trigger folder and restart. For the last of them
        # NinjaTrader's own log shows a Playback connect in progress (388 s on
        # 2026-09-02); the connect runs on the AddOn's single poller thread,
        # so no trigger is read until it returns, and the run only had to
        # wait. Holds of the same kind measured 423-453 s (2026-08-28/29)
        # and 735 s for a cold start (2026-09-01). The poll waits out every
        # silence but one: the process list, read after the probe, does not
        # name NinjaTrader.exe - nothing could ever answer, so that one is
        # refused after the first unanswered probe (see the PREFLIGHT_WAIT
        # block). A list that could not be read is not that one.
        # ⚠ ONLY "DID IT ANSWER" - NOT "were all steps ok".
        #
        # First attempt asked this visibly, and the hard stop killed the run on
        # `timer  field 'timer' not found or null` - which is the NORMAL reading
        # before Playback is connected. The probe was right, the verdict was mine:
        # a stage whose steps legitimately fail in the pre-connect state cannot be
        # used as a pass/fail gate. show=False keeps the hard stop out of it, and
        # the only question asked here is whether an answer came back at all.
        pre = preflight()
        preflight_s = pre["seconds"]
        order_notices_total = 0     # NinjaTrader order-rejection notices the AddOn confirmed in this run
        if pre["answered"]:
            user("Bridge connected.")
            # Throw away output-window lines that predate this run - measured
            # 2026-08-21: the first poll returned a Terminated census from the
            # PREVIOUS session, which read like output of this run.
            bot_output()
        print("--- 0 preflight - does the bridge answer ---")
        _n_steps = len(pre["steps"])
        _answered = f"yes, {_n_steps:d} step(s)" if _n_steps else "NO"
        print(f"   bridge answered               {_answered}")
        print(f"   preflight wall clock          {preflight_s:.1f} s, "
              f"{pre['probes']:d} probe(s), budget {PREFLIGHT_WAIT:d} s")
        # The last reading of the process list - never None once the preflight
        # went unanswered (the poll reads it after every unanswered probe).
        _proc = pre["process"]
        if not pre["answered"] and _proc["listed"] is False:
            # The one verdict the poll passes itself - see the PREFLIGHT_WAIT
            # block. The error string is the one a spent budget raises, so a
            # caller sees ONE signature for "nothing answered"; the cause is
            # here and in the user line the poll already printed - as what
            # tasklist answered, not as a claim about the process list.
            print("")
            print("   " + "!" * 66)
            print("   NinjaTrader.exe is not running. The process list was read after probe")
            print(f"   {pre['probes']:d} ({preflight_s:.0f} s) and does not name it; measured:")
            print(f"      {_proc['detail']}")
            print("   NOTHING was started.")
            print("   Nothing can answer a probe without the process, so the poll did not")
            print(f"   wait out its budget ({PREFLIGHT_WAIT:d} s). Start NinjaTrader, sign in,")
            print("   let the AddOn load (it writes heartbeat.json when it has), then run")
            print("   again.")
            print("   " + "!" * 66)
            raise RuntimeError("preflight: the bridge did not answer")
        if not pre["answered"]:
            _age = heartbeat_age()
            print("")
            print("   " + "!" * 66)
            print(f"   NinjaTrader answered NO request for {preflight_s:.0f} s "
                  f"(budget {PREFLIGHT_WAIT:d} s). NOTHING was started.")
            print("   The measured causes of this signature end by themselves and are")
            print("   covered by the budget: a Playback connect keeping the AddOn's poller")
            print("   thread busy (388 s on 2026-09-02, 423/437/453 s on 2026-08-28/29,")
            print("   NinjaTrader's own log) and a cold start (735 s on 2026-09-01). This")
            print("   silence is longer than every measured one. heartbeat.json is written")
            print("   from the main UI thread, not from the poller thread, so its age")
            print("   cannot tell busy from broken - it is reported, not judged:")
            if _age >= 0:
                print(f"   heartbeat.json age: {_age:.0f} s")
            else:
                print("   heartbeat.json: none - the AddOn writes it when it loads, so it")
                print("   has not loaded for this data directory (or the file was removed)")
            if _proc["listed"] is True:
                print("   Its process exists - the process list was read after every probe;")
                print(f"   the last read: {_proc['detail']}")
            else:
                print("   Whether its process exists is NOT known - the process list could not")
                print("   be read after the last probe, so the silence was treated as a wait;")
                print(f"   the last read: {_proc['detail']}")
                print("   Check the process yourself first.")
            print("   Look at NinjaTrader itself before running again: is it signed in, is")
            print("   the AddOn loaded, is a modal dialog open (a modal blocks the dispatcher")
            print("   and the bridge stops answering - measured 2026-08-20)?")
            print("   " + "!" * 66)
            raise RuntimeError("preflight: the bridge did not answer")

        # ================================================================
        # THE OPERATING SEQUENCE, as specified by the user on 2026-08-19 and
        # verified step by step against the panel that same day.
        #
        #   1  EVERY connection off, then the baseline: no strategy rows, no
        #      dialogs, transport parked. Step 1 has to survive any starting
        #      situation - nothing connected, one live feed, several at once.
        #   2  connection -> Playback
        #   3  wait until the Playback box is no longer greyed
        #   4  Market Replay OR Historical
        #   5  start and end date, always end > start (otherwise NinjaTrader errors)
        #   6  the same range into the adapter, and reset the clock
        #   7  speed to Max, so the DISPLAY reads Max too
        #   8  set the strategy up and enable it
        #   9  and only NOW: start the playback
        #
        # ⚠ NOT ONE TICK MAY PASS BEFORE STEP 9. A transport that runs during
        # setup costs the strategy the data it consumed, and a bot attached
        # afterwards starts mid-stream. Measured through steps 1-8: the clock
        # stayed on 2026-08-10 00:00:00 at every single sub-step.
        #
        # ⚠ Numbering changed on 2026-08-20 when switching everything off became
        # step 1. Labels and comments were renumbered together - a step whose
        # label says 1 while the sequence calls it 2 is a defect, not cosmetics.
        # ================================================================
        # Start from a state we established, with the SAME one-call cleanup the
        # teardown uses. `baseline` used to be here and it is not a UI action but a
        # whole cleanup routine - it cannot answer inside a ten-second budget, so
        # every run aborted on its own opening step (measured 2026-08-19).
        _nt = nt_index()

        # ⚠ STEP 1 STARTS BY SWITCHING EVERYTHING OFF - UNCONDITIONALLY.
        #
        # When playback is switched on, every other connection MUST be closed
        # first, and that may happen without asking (2026-08-20).
        # This is not a judgement call and not an intrusion to ask
        # about: it is a precondition of the mode. No `keep` here - `keep` exists
        # for a future Strategy Analyzer path, where a LIVE connection must
        # survive and Playback must be absent.
        #
        # The run has to survive ANY starting situation: nothing connected, one live
        # feed, several at once. Playback refuses to connect while anything else is
        # up and says so in a MODAL dialog, which blocks the dispatcher - the bridge
        # then stops answering at all.
        #
        # Neither older stage did this: `disconnect` touches only the Playback
        # connection, and `connect` disconnects the others but connects Playback in
        # the same breath. So a run used to begin on whatever happened to be open.
        # Measured 2026-08-20: a report built from the CONFIGURATION showed six rows
        # and no "Live", while NinjaTrader had "Live" connected the whole time.
        #
        # `alloff` enumerates the LIVE list, disconnects every one, waits for
        # NinjaTrader's own status entry, and then for every connection to report
        # Disconnected - and it names them, so "nothing was connected" is a
        # measurement rather than an assumption.
        _req1a = {"stage": "alloff"}
        if a.keep:
            _req1a["keep"] = a.keep
        mandatory(note(stage("1a all connections off", _req1a, wait=90)),
                  "all disconnected")

        # Start from a state we established, with the SAME one-call cleanup the
        # teardown uses. `baseline` used to be here and it is not a UI action but a
        # whole cleanup routine - it cannot answer inside a ten-second budget, so
        # every run aborted on its own opening step (measured 2026-08-19).
        mandatory(note(stage("1b clean start", {"stage": "restore"}, wait=30)),
                  "strategy rows", "dialogs", "transport")
        nt_check(_nt, what="step 1 baseline")
        user("Baseline clean: no connections, no strategies.")

        # 2 - the SOURCE goes with the connect. The panel's radio buttons cannot be
        # written while connected: assigning IsChecked makes NinjaTrader rebuild the
        # transport synchronously on the UI thread, and the bridge call sits in that
        # same dispatcher (900 s on 2026-08-18; on 19.08. the stage simply stopped
        # answering). The adapter static decides the source, and the panel follows it
        # on connect - which is exactly why it is set here, before connecting.
        _nt = nt_index()
        user(f"Connecting Playback ({a.source}) ...")
        # ⚠ THE RANGE HAS TO TRAVEL WITH THE CONNECT, not only with stage `range`.
        #
        # Measured 2026-08-24: without it the connect answered
        #     playback connection   ok=false   Status=null
        # and NinjaTrader put up a modal "String was not recognized as a valid
        # DateTime. (Panic)" - which also blocks the dispatcher, so every later
        # request queued behind it and four of them expired unexecuted.
        #
        # The step list said it plainly: `FromEst` and `ToEst` were MISSING from
        # it entirely. The AddOn writes them only when the request carries `from`
        # and `to`, and this call sent neither, so the adapter went into Connect
        # with no range at all.
        #
        # Stage `range` still sets them afterwards, and has to: before the
        # connection is up the adapter discards them. These two writes are for
        # the connect itself, which reads them while connecting.
        # The instrument travels with the connect because the coverage step needs
        # it: it answers "is the requested window in the replay STORE", and the
        # store is per instrument. Without it that step had to fall back to the
        # panel's own range, which Connect restores from the saved configuration -
        # so it compared the request against whatever the last session used.
        # wait=CONNECT_WAIT, not SLOW_WAIT: the connect can sporadically sit
        # for minutes inside NinjaTrader and still succeed - see the
        # CONNECT_WAIT block at the top for the measurement.
        _connect_t0 = time.monotonic()
        mandatory(note(stage("2 connect", {"stage": "connect", "source": a.source,
                                           "instrument": a.instrument,
                                           "from": a.date_from, "to": a.date_to},
                             wait=CONNECT_WAIT)), "playback connection")
        _connect_s = time.monotonic() - _connect_t0
        if _connect_s > SLOW_WAIT:
            # loud, so a run full of slow connects reads as pathology in the
            # transcript instead of vanishing into a green verdict
            user("SLOW CONNECT: %.0f s (typical 16-30 s, budget %d s) - "
                 "NinjaTrader-internal stall, the run continues."
                 % (_connect_s, CONNECT_WAIT))
        # The measured resource name, not the English sentence - see nt_check.
        nt_check(_nt, expect="CbiConnectionProcessConnectionStatusUpdate",
                 contains="Connected", what="connect")
        user("Connected.")

        # ⚠ THE SEPARATE "panel ready" STEP IS GONE - step 2 already waits for it.
        #
        # The panel goes GREY for about 12 s after the connect and then comes back.
        # Measured 11 311 / 11 876 / 12 236 / 16 313 / 17 749 ms across five runs.
        # That transition used to be watched HERE, because `connect` returned as soon
        # as the connection was up.
        #
        # `connect` now waits for the transition itself and reports it:
        #     panel usable  ok  usable after 11876 ms  [noPlaybackWindow;wl:;hooked;enabled;]
        # so by the time this line was reached the panel had been free for seconds.
        # Asking for the transition a SECOND time demanded an event that was over:
        # measured 2026-08-20, "never went grey within 40000 ms - with
        # requireTransition the connect must show". The step could not pass, and the
        # step before it had already proven what it was there to prove.
        #
        # Two steps waiting for one transition is not extra safety; the later one is
        # guaranteed to fail. The wait belongs where the action is - trigger and wait
        # are one step - so it stays in `connect`.
        #
        # The `ready` stage itself is kept: it is the right tool wherever a panel
        # transition is NOT already covered by the action that caused it.
        # 3 - verify the source took, and report what the panel shows. Writing the
        # radios here is what deadlocks; reading them is free.
        # The panel has to EXIST before any stage writes it. NinjaTrader does not
        # persist the Playback window in a workspace (measured 2026-08-29: 52 windows
        # across 7 workspaces, none titled "Playback"), so after a NinjaTrader start
        # the source check finds nothing and the run ends there - measured 2026-09-03,
        # three attempts in one call, each "3 source check: panel radios - Playback
        # window not found" after a healthy connect. The stage is idempotent: an open
        # window is reported ("already open"), never a second one.
        # mandatory() keys are SUBSTRINGS of the step names it must see ok (:1456-1457),
        # and this stage answers with one of two shapes: "already open" when the window
        # is there, or "open PlaybackControlCenter" + "window present" when it had to be
        # created. Both failing shapes are named here; the "already open" step is only
        # ever emitted as ok.
        mandatory(note(stage("2b playback window", {"stage": "openwindow"})),
                  "window present", "open PlaybackControlCenter")
        mandatory(note(stage("3 source check", {"stage": "source", "source": a.source})),
                  "IsSourceHistoricalData")
        # 4 - the panel editors, with end AFTER start
        if a.date_to < a.date_from:
            raise RuntimeError(f"end {a.date_to} is before start {a.date_from}")
        mandatory(note(stage("4 dates", {"stage": "uiset", "from": a.date_from,
                                           "to": a.date_to})), "dtpStart", "dtpEnd")
        # the adapter's own range + the clock at the start
        mandatory(note(stage("5 range", {"stage": "range", "from": a.date_from,
                                          "to": a.date_to}, wait=SLOW_WAIT)),
                  "FromEst", "ToEst")
        # 6 - panel memory only; writing the adapter static would start the run
        mandatory(note(stage("6 speed", {"stage": "speed", "value": "max"})), "panel")

        # 7 - the click-free path: ONE call, no window, configuration out of the
        # template NinjaTrader itself reads and writes. The request's own fields are
        # applied on top of the template inside the bridge, so instrument and range
        # stay per-run while bar type and Tick Replay come from the template.
        if a.setup == "template":
            bp_token = f"77077:{a.barvalue}:1" if a.barsperiod.lower() == "wave" \
                       else (f"0:{a.barvalue}:1") if a.barsperiod.lower() == "tick" \
                       else (f"4:{a.barvalue}:1")
            # Remember where the log stands, so only lines written AFTER this attach count.
            _nt_attach = nt_index()
            # The names the GRID path reports. "added to account" and
            # "SetState(Realtime)" belong to the older via=account path and can
            # never appear here - a required step that cannot occur fails every
            # run regardless of what NinjaTrader did.
            # Every one of these is a MEASURED effect, not a call that returned.
            # (The interactive --attach-only hold mode was removed 2026-08-21
            # after the enable freeze was fixed at its root - the AddOn's
            # `arm` stage and attach's enable:false remain available for
            # diagnosis via hand-written triggers.)
            for _bi, (_bot, _acct, _tmpl) in enumerate(BOTS, start=1):
                user(f"Attaching strategy {_bot}"
                     f"{' on ' + _acct if _acct else ''}"
                     f"{' with template ' + _tmpl if _tmpl else ''} ...")
                _req = {"stage": "attach",
                        "strategy": _bot,
                        # optional trading-hours template override (coverage
                        # experiments); empty = the instrument's own
                        "tradingHours": a.tradinghours or "",
                        # empty string when none was given - the stage reads
                        # that as "use the bot's defaults" and reports it
                        # The template, when one was named; otherwise the bot's
                        # own defaults.
                        "template": _tmpl or a.template or "",
                        "instrument": a.instrument,
                        "barsPeriod": bp_token,
                        "tickReplay": a.tickreplay,
                        "from": a.date_from,
                        "to": a.date_to}
                # Empty = the playback account NinjaTrader names itself.
                if _acct:
                    _req["account"] = _acct
                mandatory(note(stage(f"7.{_bi:d} attach {_bot}", _req,
                                     wait=ARM_WAIT)),
                          "template", "configuration valid",
                          "StrategyEnable", "enable requested (NOT proof)")
                # Each bot is armed on its own; the log wait below covers the
                # LAST one, and every earlier bot has already been confirmed by
                # its own attach steps.
                if _bi < len(BOTS):
                    _ok = wait_for_enable_event(_bot, since=_nt_attach, timeout=90.0)
                    if not _ok:
                        raise RuntimeError(
                            f"NinjaTrader never logged 'Enabling NinjaScript "
                            f"strategy {_bot}' within 90 s.")
                    user(f"Strategy armed: {_bot}")

            # ⚠ WATCH NINJATRADER'S OWN LOG, NOT THE GRID.
            #
            # Arming is heavy: NinjaTrader loads the series and steps the state machine,
            # and it does that ON THE UI THREAD. Every observation that goes through the
            # dispatcher therefore queues BEHIND the very work it wants to observe - which
            # is how a busy NinjaTrader became a frozen one three times on 2026-08-19, the
            # last time with 13 of my own polls stacked up behind it.
            #
            # NinjaTrader logs `Enabling NinjaScript strategy '<Name>/<id>'` through
            # Cbi.Log, and the AddOn buffers those entries as they arrive. The `ntlog`
            # stage hands them out from that buffer under its own lock - still a request,
            # but one that needs no dispatcher, so it answers while the UI thread is busy.
            # That is the one channel that cannot make the situation worse.
            armed = wait_for_enable_event(a.strategy, since=_nt_attach, timeout=90.0)
            if not armed:
                raise RuntimeError(
                    f"NinjaTrader never logged 'Enabling NinjaScript strategy "
                    f"{a.strategy}' within 90 s. "
                    f"The enable was fired and did not start - do NOT poll it, that only "
                    f"adds load. Stage 'ntlog' shows NinjaTrader's own entries since the "
                    f"enable was fired - that is the channel this wait listens to.")
            print(f"   armed: {armed}")
            # Only the fact. The raw NinjaTrader log line sits in the debug
            # transcript and carries a WALL-CLOCK stamp - those never go into
            # the user console (data-stream times only).
            user("Strategy armed.")
        else:
            # unreachable: --setup dialog is rejected in main(). Kept as a tripwire so a
            # future edit that re-enables the flag fails here instead of clicking.
            raise RuntimeError("the dialog setup path was removed with the clicks")

        # Nothing may have moved yet. This is the whole point of the order above,
        # so it is CHECKED rather than trusted. One call answers all three: where
        # the clock stands, whether it is advancing, and where the range ends.
        c0, m0, to0 = clock_vs_end()
        print(f"   before start: clock={c0}  moving={m0}")
        # ⚠ THE CLOCK MUST BE AT THE RANGE START - not merely standing still.
        #
        # Measured 2026-08-19: a run began with the clock on 10.08. 23:59:59,
        # which is ToEst. It was not moving, so the old check passed; pressing
        # play then changed nothing because there was nothing left to play. A
        # standing clock at the END looks exactly like a standing clock at the
        # START, and only one of them is a run.
        if m0:
            raise RuntimeError(f"the transport was already running before step 8 - "
                               f"the strategy has lost the ticks up to {c0}")
        # Compare against ToEst, not against the start DATE. With a one-day range
        # start and end fall on the same date, so `c0[:10] == date_from` was true
        # for a clock sitting on 23:59:59 - the run then pressed play with nothing
        # left to play and failed one step later (measured 2026-08-19). What has to
        # hold is simply: there is still range ahead of us.
        if not c0:
            raise RuntimeError("no clock reading before the start")
        c0n = c0.replace("T", " ")[:19]
        to0n = (to0 or "").strip()[:19]
        if to0n:
            # Both come from PlaybackAdapter rendered by the machine's locale, so
            # either can arrive as dd.MM.yyyy HH:mm:ss - normalise both before
            # comparing, or the string order is meaningless.
            def _norm(s: str) -> str:
                s = s.strip()
                if "." in s[:10]:
                    d, t_ = s.split(" ", 1) if " " in s else (s, "00:00:00")
                    dd, mm, yy = d.split(".")
                    return f"{yy}-{mm}-{dd} {t_}"
                return s
            if _norm(c0n) >= _norm(to0n):
                raise RuntimeError(
                    f"the clock is on {c0n}, at or past ToEst {to0n} - nothing would play")

        # 8 - the transport is started by WRITING THE SPEED, not by pressing play.
        # The stage samples the clock before and after and reports movement, so the
        # verdict rests on the clock and not on the write having resolved.
        mandatory(note(stage("8 start", {"stage": "play", "value": "max"},
                             wait=SLOW_WAIT)), "PlaybackSpeed", "after")
        user(f"Playing {a.date_from} .. {a.date_to} ...")
        # Baseline for the console's percent figure: the first sampled data
        # time. Percent is DATA progress (NowEst between first sample and
        # ToEst), never wall clock.
        _pb = {"base": None}

        # ⚠ END OF RUN: the strategy reaches State.Terminated.
        #
        # Every NinjaScript passes through OnStateChange and ends at Terminated. No
        # strategy has to cooperate, nothing is written to disk, no GUI element is
        # read - which matters, because this goes back upstream.
        #
        # NowEst >= ToEst is kept as a second witness: it says the RANGE was played,
        # while Terminated says the STRATEGY finished. Both together separate "played
        # to the end" from "stopped early", and the run reports which one it was.
        #
        # Four candidates were rejected, each for a measured reason:
        #   clock standing still ......... a guess about stillness; a minute wasted
        #                                  at the end of every run
        #   IsAvailableChanged ........... subscribed 2026-08-19, never fired
        #   the progress slider .......... falls short of its maximum when data is
        #                                  missing (user, 2026-08-19) - waiting hangs
        #   a strategy's own counter file  only some strategies write one
        print("--- playing ---")
        terminated = played = False
        end_gap = False          # NowEst jumped over a tick-store gap - a failure
        prev_now = None          # previous sample's data time, the gap witness
        SAMPLE_S = 10
        for i in range(int(a.maxwait * 30 / SAMPLE_S)):
            time.sleep(SAMPLE_S)
            c, m, to = clock_vs_end()
            st = stage("state", {"stage": "strategystate"}, wait=STAGE_WAIT, show=False)
            sv = {str(s.get("step", "")).strip(): str(s.get("detail", "")) for s in st.get("steps", [])}
            # NinjaTrader's order-rejection notice ("Stop price can't be changed above
            # the market") is confirmed by the AddOn once per sample and counted in
            # the "order notices" step (DismissOrderRejectNotices). It is said on the
            # console when one was confirmed, and the run total lands in result.json,
            # so an unattended run's notices are visible afterwards - the box itself
            # is gone.
            _now_n, _total_n = order_notice_counts(sv.get("order notices", ""))
            order_notices_total = max(order_notices_total, _total_n)
            if _now_n:
                user(f"NinjaTrader order-rejection notice confirmed by the bridge "
                     f"({_now_n:d} this sample, {_total_n:d} in this run)")
            # ⚠ startswith, not equality - and that is a repair of my own regression.
            # The stage used to answer a bare "yes"/"no"; on 2026-08-20 the text was
            # changed to "yes - the strategy has terminated" so that `ok` could stop
            # being a data field. This comparison was not carried along, so it never
            # matched again and every run ended with "NowEst reached ToEst (strategy
            # not yet Terminated)" - a report that was wrong about why it stopped.
            terminated = sv.get("terminated", "no").strip().lower().startswith("yes")
            # ⚠ PARSE, NEVER STRING-COMPARE, DD.MM.YYYY TIMES.
            #
            # Measured 2026-08-21, twice in one afternoon: `c >= to` on the raw
            # strings ended "14.01.2026 ..." >= "13.03.2026 ..." (day field
            # compares first) - two runs reported ok=true at 5.7 % and 13.2 %
            # coverage. Every one-day run before was accidentally correct: with
            # an identical date prefix the string order equals the time order.
            _c = _to = None
            try:
                _c = datetime.strptime(c, "%d.%m.%Y %H:%M:%S")
                _to = datetime.strptime(to, "%d.%m.%Y %H:%M:%S")
            except (ValueError, TypeError):
                # Unparsable sample -> no end verdict this round. Say it loudly
                # in the transcript; a format change must not end runs silently.
                if c or to:
                    print(f"   ⚠ unparsable data time in end check: c={c!r} to={to!r}")
            played = bool(_c and _to and _c >= _to)
            # ⚠ A RUNNING CLOCK IS NOT A DATA STREAM.
            #
            # Until now this loop watched NowEst and the grid row, and a run that
            # reached ToEst was called finished. Neither says the strategy was ever
            # FED anything. Measured 2026-08-20: the empty bot "played" a full tick
            # day in 3:00 min while CallbackSonde needed 107 min on the same day -
            # and nothing in the transcript could tell whether one bar had arrived.
            #
            # These counters are NinjaTrader's own, on the strategy instance, not
            # the bot's: a bot that prints nothing still has them. They are gone the
            # moment the strategy is removed, so they have to be read WHILE it runs.
            lv = stage("live", {"stage": "stratlive"}, wait=STAGE_WAIT, show=False)
            lvv = {str(s.get("step", "")).strip(): str(s.get("detail", ""))
                   for s in lv.get("steps", [])}
            bars = ", ".join(f"{k}={v.split(' bars')[0]}"
                             for k, v in lvv.items() if k.startswith("bars["))
            print(f"   +{(i + 1) * SAMPLE_S:5d}s  {c}  moving={m:<5}  ToEst={to}  "
                  f"State={lvv.get('strategy', '?').split('State=')[-1][:12]}  "
                  f"CurrentBar={lvv.get('CurrentBar', '?')[:9]}  {bars[:44]}", flush=True)
            try:
                _now = datetime.strptime(c, "%d.%m.%Y %H:%M:%S")
                _to = datetime.strptime(to, "%d.%m.%Y %H:%M:%S")
                if _pb["base"] is None:
                    _pb["base"] = _now
                _span = (_to - _pb["base"]).total_seconds()
                _pct = (100.0 * (_now - _pb["base"]).total_seconds() / _span
                        if _span > 0 else 100.0)
                user_progress(
                    f"{c} | {min(_pct, 100.0):5.1f} % | "
                    f"State={lvv.get('strategy', '?').split('State=')[-1][:12].strip()} | "
                    f"Bar={lvv.get('CurrentBar', '?').strip()}")
            except (ValueError, TypeError):
                # No parsable data time -> no progress line. Never a wall clock.
                pass
            # What the bot printed since the last round - shown right after the
            # harness line, so [BOT] lines and phase lines interleave in order.
            print_bot_output()
            # ⚠ CAUSE AND EFFECT - not to be swapped (user, 2026-08-21):
            # State.Terminated is the EFFECT of the enable checkbox being taken
            # away (our teardown, a hand, or NT8's own error handling) - never a
            # sign that the data ran out. The grid row count reaching 0 is the
            # EFFECT of our own Remove. Both are success checks of the CLEANUP.
            # The only END-of-run signal is the data-side one: NowEst at the
            # right edge (>= ToEst).
            if terminated:
                user("ABORT: strategy left the account (State.Terminated) before the data end.")
                print("   strategy hit State.Terminated - it was DISABLED (externally "
                      "or by NT8's error handling). That is an ABORT, not the data end.")
                break
            if played:
                # ⚠ ARRIVING IS NOT THE SAME AS PLAYING THROUGH.
                #
                # Measured 2026-08-21 (MNQ ##-##): the
                # tick store had 123 missing weekdays; the player streamed to
                # the first gap (18.01.) and then JUMPED to the right edge
                # (17.08.) between two samples. NowEst >= ToEst was true, the
                # run reported ok=true - at 5.7 % actual coverage. A jump this
                # size is a DATA GAP, and a gap must fail loudly, not pass as
                # the data end. The previous sample's data time is the witness:
                # if the clock crossed more than 3 days between two samples,
                # the store did not carry the run to the edge.
                gap_days = None
                try:
                    _prev = datetime.strptime(prev_now, "%d.%m.%Y %H:%M:%S")
                    _here = datetime.strptime(c, "%d.%m.%Y %H:%M:%S")
                    gap_days = (_here - _prev).total_seconds() / 86400.0
                except (ValueError, TypeError):
                    pass
                if gap_days is not None and gap_days > 3.0:
                    user(f"ERROR: data gap - the player jumped {gap_days:.1f} days "
                         f"(from {prev_now} to {c}) instead of streaming there. The tick "
                         f"store does not cover the window.")
                    print(f"   ⚠ DATA GAP: NowEst jumped {gap_days:.1f} days "
                          f"({prev_now} -> {c}). NowEst >= ToEst is the effect of the "
                          f"jump, not of playing through. This is a FAILED run.")
                    FAIL.update({"stage": "play", "step": "coverage",
                                 "detail": f"player jumped {gap_days:.1f} days "
                                           f"({prev_now} -> {c}): tick store gap"})
                    end_gap = True
                    break
                user("End of data reached (player at the right edge).")
                print("   NowEst reached ToEst - the data end. (Strategy still enabled, "
                      "as expected: Terminated only follows a disable.)")
                break
            prev_now = c
        # ⚠ EVERY end that is not the DATA end is a failure for the caller.
        # `played` is the only good exit (NowEst at ToEst); a Terminated
        # strategy was DISABLED (NT8 error handling or a hand) and a loop
        # that ran out of budget saw neither - both used to leave rc at 0,
        # which is exactly the "looks like a result" trap mandatory() names.
        if end_gap:
            end_reason = "data-gap"
            rc = rc or 2
        elif played:
            end_reason = "data-end"
        elif terminated:
            end_reason = "strategy-stopped"
            rc = rc or 2
        else:
            end_reason = "did-not-finish"
            rc = rc or 2
            print("   ⚠ neither the data end nor an abort was seen - the run did not finish")
    except KeyboardInterrupt as ex:
        print("")
        print(f"   stopped by the user: {ex}")
        end_reason = "interrupted"
        err_text = str(ex) or "KeyboardInterrupt"
        rc = 2
    except RuntimeError as ex:
        # ⚠ A CONTROLLED STOP IS NOT A CRASH - do not print a stack trace for it.
        #
        # The hard stop and the mandatory check raise on purpose, and Python 3.13
        # renders the trace with ~~~^^^ markers under the call. A reader takes that
        # for a syntax error, which is exactly what it looks like (2026-08-20).
        # The reason for stopping was already
        # printed, in full, right above it - the trace adds nothing but noise and
        # sends the reader looking for a defect in the script.
        print("")
        print(f"   === RUN ABORTED ===  {ex}")
        print("   The reason is the FAIL block above. This is a controlled stop,")
        print("   not a crash: nothing was built on the step that failed.")
        end_reason = "aborted"
        err_text = str(ex)
        rc = 2
    except Exception as ex:                          # noqa: BLE001
        # Anything unforeseen still gets its full trace - that one IS a defect.
        traceback.print_exc()
        end_reason = "crashed"
        err_text = f"{type(ex).__name__}: {ex}"
        rc = 2
    finally:
        # Guarantee 1: the baseline is restored no matter how we got here.
        teardown = restore_baseline(a.strategy)
        # The strategy's Terminated pass prints its last lines DURING the
        # teardown - fetch them so the console shows the complete output window.
        try:
            print_bot_output()
        except Exception:
            pass
        user("Baseline restored." if all(bool(s.get("ok")) for s in teardown)
             else "WARNING: baseline NOT clean - see teardown.json in the archive.")


    new = ((set(p.name for p in BOTLOGS.iterdir()) - before)
           if BOTLOGS and BOTLOGS.exists() else set())
    (target / "run.json").write_text(json.dumps(log, indent=2, ensure_ascii=False),
                                     encoding="utf-8")
    (target / "teardown.json").write_text(json.dumps(teardown, indent=2, ensure_ascii=False,
                                                     default=str), encoding="utf-8")
    (target / "requested.json").write_text(json.dumps(vars(a), indent=2), encoding="utf-8")
    for o in sorted(new):
        q = BOTLOGS / o
        if q.is_dir():
            shutil.copytree(q, target / ("botlog_" + o), dirs_exist_ok=True)
    wall_s = time.time() - wall_start
    print("")
    print(f"Wall-clock duration: {int(wall_s // 60)}:{int(wall_s % 60):02} min:s")
    (target / "duration.json").write_text(
        json.dumps({"wallClockSeconds": round(wall_s, 1),
                    "note": "wall clock, not data time - how long this mode took"},
                   indent=2), encoding="utf-8")
    print(f"\nArchived in {target}")

    best = None
    for f in sorted(target.glob("botlog_*/DEBUG_callbacks.json")):
        j = json.loads(f.read_text(encoding="utf-8"))
        if best is None or j.get("OnBarUpdate_bip0", 0) > best.get("OnBarUpdate_bip0", 0):
            best = j
    problems = []
    if best is None:
        # NOT an error. The census is a MEASUREMENT, never a control: a strategy
        # that writes no counter file is a perfectly good run, and an empty test
        # strategy never writes one. An earlier `return rc or 3` here turned every
        # clean run of such a strategy into exit 2.
        print("   no counter file - the strategy wrote no census (an empty bot "
              "never does; the census is a measurement, not a control)")
    else:
        # ⚠ The bot reports what it ACTUALLY got. Compare it with what was asked
        # for. Measured 2026-08-19: a run requested Tick Replay on and the counter
        # file says IsTickReplay=False - a result that would have been filed under
        # the wrong heading. A run whose settings do not match the request is not a
        # measurement of what was requested.
        got_tr = str(best.get("IsTickReplay", "")).lower()
        if got_tr != a.tickreplay.lower():
            problems.append(f"Tick Replay requested {a.tickreplay}, bot reports {got_tr}")
        if str(best.get("Instrument", "")).strip() != a.instrument:
            problems.append(f"instrument requested {a.instrument!r}, "
                            f"bot reports {best.get('Instrument')!r}")
        # "no market data at all" and "market data stops early" are different answers
        # and must not share a message. Measured 2026-08-19: Market Replay without Tick
        # Replay delivers ZERO OnMarketData events over the whole range - the run is
        # complete, the count is the finding. Reporting that as "cut short" hid it.
        # The one census key this check needs by name: the timestamp of the LAST
        # market-data event. A strategy names its own counters, so when the key is
        # absent the check says so instead of passing quietly - a "cut short" test
        # that silently does not run reads exactly like one that found nothing
        # wrong.
        LAST_DATA_KEY = "MarketData_LastDataTime"
        if LAST_DATA_KEY not in best:
            span_to = ""
            print(f"   note: census has no {LAST_DATA_KEY!r} - the 'run was cut "
                  f"short' check cannot run for this strategy")
        else:
            span_to = str(best[LAST_DATA_KEY]).strip()
        if LAST_DATA_KEY in best and span_to in ("", "-"):
            print(f"   note: no market data events at all "
                  f"(OnMarketData={best.get('OnMarketData')}) - that is the measurement, "
                  f"not a truncated run")
        elif _iso_day(span_to) and _iso_day(span_to) < a.date_to:
            problems.append(f"market data ends {span_to[:10]}, range ends "
                            f"{a.date_to} - run was cut short")
        if problems:
            print("   ⚠ RUN DOES NOT MATCH THE REQUEST:")
            for x in problems:
                print(f"      {x}")
            (target / "MISMATCH.txt").write_text(chr(10).join(problems), encoding="utf-8")
            rc = rc or 2
        # Print the keys the census ACTUALLY carries, not a fixed list. A fixed
        # list is a contract with one particular strategy: it prints None for
        # every counter that strategy does not write, and silently drops every
        # counter it writes that nobody thought of. The ones below lead because
        # they answer "did the run get data at all"; everything else follows in
        # the order the file has it.
        LEAD = ("IsTickReplay", "Instrument", "OnBarUpdate_bip0", "CurrentBar",
                "OnMarketData", "MarketData_Last", "MarketData_Bid", "MarketData_Ask")
        for k in LEAD:
            if k in best:
                print(f"   {k:30} {best[k]}")
        for k in best:
            if k not in LEAD:
                print(f"   {k:30} {best[k]}")

    # ── The machine-readable contract for the headless caller. ─────────────────
    #
    # One JSON object that says whether the run is a RESULT: it is `ok` only if
    # the DATA END was reached (NowEst at ToEst - the sole good exit per the
    # cause-vs-effect rule of 2026-08-21), the teardown restored the baseline,
    # and nothing mismatched the request. Every failure carries the stage, the
    # sub-step and NinjaTrader's own detail line, so the caller of the CLI
    # bridge gets a message worth reading instead of an exit code alone.
    baseline_clean = bool(teardown) and all(bool(s.get("ok")) for s in teardown)
    ok = (rc == 0 and end_reason == "data-end" and baseline_clean and not problems)
    if not ok and rc == 0:
        rc = 2                       # e.g. data end reached but baseline NOT clean
    result = {
        "command": "playbackrun",
        "ok": ok,
        "rc": rc,
        "endReason": end_reason,
        "failed": dict(FAIL) if FAIL.get("step") else None,
        "error": err_text,
        "mismatch": problems or None,
        "baselineClean": baseline_clean,
        "archive": str(target),
        "name": a.name,
        "strategy": a.strategy,
        "strategies": [{"strategy": b, "account": acc or None,
                        "template": tm or a.template or None} for b, acc, tm in BOTS],
        "instrument": a.instrument,
        "source": a.source,
        "tickReplay": a.tickreplay,
        "barsPeriod": a.barsperiod,
        "barValue": a.barvalue,
        "from": a.date_from,
        "to": a.date_to,
        "wallClockSeconds": round(wall_s, 1),
        # Wall clock the preflight spent waiting for the bridge (stage 0). A
        # slow preflight is a busy NinjaTrader; here it stays visible in the
        # archive instead of hiding inside wallClockSeconds.
        "preflightSeconds": round(preflight_s, 1),
        # NinjaTrader order-rejection notices the AddOn confirmed during the play
        # loop (see the "order notices" step). 0 when none appeared.
        "orderNoticesDismissed": order_notices_total,
        "censusFound": best is not None,
        "census": best,
    }
    payload = json.dumps(result, indent=2, ensure_ascii=False, default=str)
    (target / "result.json").write_text(payload, encoding="utf-8")
    if a.result:
        Path(a.result).write_text(payload, encoding="utf-8")
    # The closing block of the end-user console: the result as JSON, then the
    # duration line.
    user("")
    user(payload)
    user(f"=== Backtest duration (min:s): {int(wall_s // 60)}:{int(wall_s % 60):02} ===")
    return rc


if __name__ == "__main__":
    # The exit code can go into a file too: behind `python ... | tee` a cmd batch
    # sees the pipe's code, not ours, and a chain meant to stop on the first
    # failure would run to the end regardless.
    #
    # Only when --rc-file asks for it. This used to write into the package
    # directory next to this module, which is read-only in a normal install and
    # is not the caller's choice of location either.
    _rc = 1
    try:
        _rc = main()
    finally:
        _target = None
        for _i, _arg in enumerate(sys.argv):
            if _arg == "--rc-file" and _i + 1 < len(sys.argv):
                _target = sys.argv[_i + 1]
            elif _arg.startswith("--rc-file="):
                _target = _arg.split("=", 1)[1]
        if _target:
            try:
                _p = Path(_target)
                _p.parent.mkdir(parents=True, exist_ok=True)
                _p.write_text(str(_rc), encoding="ascii")
            except OSError:
                pass
    raise SystemExit(_rc)
