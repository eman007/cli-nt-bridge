"""playbackctl — seek / speed / range.

The seek cases encode the root cause of a full lost day: `Reset` is asynchronous and walks the clock
toward the target, so a verdict rendered immediately froze the seek mid-flight. Every failing attempt
came back in 57 ms; every succeeding one took 5-7 seconds.
"""
from nt8bridge import playbackctl as ntpb


# ---- request shape ----

def test_seek_request():
    req = ntpb.build_request("id1", "seek", to="2026-04-21T17:00:00", settle_ms=2000, timeout_ms=45000)
    assert req["kind"] == "playbackctl"
    assert req["action"] == "seek"
    assert req["to"] == "2026-04-21T17:00:00"
    assert req["settleMs"] == "2000"
    assert req["timeoutMs"] == "45000"


def test_range_request_carries_confirm():
    req = ntpb.build_request("id2", "range", start="2026-04-21", end="2026-04-23", confirm=True)
    assert req["action"] == "range"
    assert req["confirm"] == "true"


def test_speed_request():
    assert ntpb.build_request("id3", "speed", speed=100)["speed"] == "100"


def test_confirm_defaults_to_false():
    """A mutation must never be armed by omission."""
    assert ntpb.build_request("id4", "range")["confirm"] == "false"


# ---- the seek verdict ----

def _seek(**over):
    p = {
        "status": "ok", "action": "seek", "succeeded": True,
        "target": "2026-04-21T17:00:00", "clockBefore": "2026-04-20T00:00:00",
        "landedAt": "2026-04-21T17:00:00", "offsetSec": 0.0, "via": "NowEst = target",
        "settledAfterMs": 5250, "timedOut": False,
        "trajectory": ["2026-04-20T00:00:00", "2026-04-21T03:06:14", "2026-04-21T17:00:00"],
        "errors": [],
    }
    p.update(over)
    return ntpb.parse_response(p)


def test_a_landed_seek_succeeds():
    st = _seek()
    assert st.succeeded is True
    assert st.moved() is True


def test_a_seek_that_walked_and_stopped_short_is_distinguishable():
    """⭐ THE WHOLE POINT. Reporting only the final position makes 'walked toward the target and
    stopped' identical to 'never moved' — and the two need opposite fixes. The clock's own history
    was the proof and it sat unread in a log: 04-20 00:00 -> 04-21 03:06 -> 04-21 03:51."""
    st = _seek(succeeded=False, landedAt="2026-04-21T03:51:01", offsetSec=-47339.0,
               trajectory=["2026-04-20T00:00:00", "2026-04-21T03:06:14", "2026-04-21T03:51:01"],
               verdict="SETTLED SHORT: stopped -47339s from target")
    assert st.succeeded is False
    assert st.moved() is True
    assert "clock DID move" in st.describe()


def test_a_seek_that_never_started_is_distinguishable():
    st = _seek(succeeded=False, landedAt="2026-04-20T00:00:00", offsetSec=-147600.0,
               trajectory=["2026-04-20T00:00:00"], verdict="SETTLED SHORT")
    assert st.moved() is False
    assert "never moved" in st.describe()


def test_a_timed_out_seek_says_the_clock_was_still_moving():
    """Settling and timing out are different outcomes. A clock still in flight when we stopped
    watching has NOT failed — judging it as failed is the original defect."""
    st = _seek(succeeded=False, timedOut=True,
               verdict="TIMED OUT after 60000ms still -900s from target — the clock was still moving")
    assert st.timed_out is True
    assert "still moving" in st.verdict


def test_trajectory_absent_is_not_an_error():
    st = ntpb.parse_response({"status": "ok", "action": "speed", "succeeded": True})
    assert st.trajectory == []
    assert st.moved() is False


# ---- discovery ----

def test_api_action_is_read_only_and_needs_no_confirm():
    req = ntpb.build_request("id5", "api")
    assert req["action"] == "api"
    assert req["confirm"] == "false"


def test_missing_transport_is_an_error_not_a_quiet_zero():
    st = ntpb.parse_response({
        "status": "error", "action": "seek", "succeeded": False,
        "errors": [{"code": "NOSEEK", "message": "neither a Reset/Seek(DateTime) method nor a "
                                                 "writable NowEst on PlaybackAdapter"}],
    })
    assert st.ok is False
    assert st.errors[0]["code"] == "NOSEEK"


# ---- range ----

def test_range_write_that_does_not_read_back_is_a_failure():
    """These members are obfuscated on some builds and located by type. A write that resolved is not
    a write that took — the same lesson as a deploy hook reporting Success."""
    st = ntpb.parse_response({
        "status": "ok", "action": "range", "succeeded": False,
        "verdict": "THE WRITE RESOLVED BUT THE RANGE DID NOT READ BACK — treat it as unchanged",
    })
    assert st.succeeded is False
    assert "DID NOT READ BACK" in st.verdict


def test_successful_range_tells_you_it_is_not_yet_applied():
    """Setting the range does not apply it — Playback must be reconnected. Leaving that implicit is
    how a bake runs against the range you thought you replaced."""
    st = ntpb.parse_response({
        "status": "ok", "action": "range", "succeeded": True,
        "verdict": "range set — RECONNECT Playback for it to take effect",
    })
    assert st.succeeded is True
    assert "RECONNECT" in st.verdict


# ---- client timeout ----

def test_client_wait_outlasts_the_addon_poll_window(monkeypatch):
    """If the client gives up before the AddOn stops polling, a seek that is working correctly gets
    reported as a dead AddOn — precisely the misreading this command exists to end."""
    captured = {}

    def fake_poll(target, timeout):
        captured["timeout"] = timeout
        return {"status": "ok", "action": "seek", "succeeded": True}

    monkeypatch.setattr(ntpb.ntio, "poll_for_json", fake_poll)
    monkeypatch.setattr(ntpb.ntio, "ensure_bridge_dirs", lambda: (__import__("pathlib").Path("."),
                                                                 __import__("pathlib").Path(".")))
    monkeypatch.setattr(ntpb.ntio, "atomic_write_json", lambda *a, **k: None)
    ntpb.run_playbackctl("seek", to="2026-04-21", timeout_ms=60000)
    assert captured["timeout"] > 60.0


# ---- range check (added after driving it on a real transport) ----

def test_force_defaults_to_false_in_the_request():
    assert ntpb.build_request("id6", "seek", to="2026-05-01")["force"] == "false"
    assert ntpb.build_request("id7", "seek", to="2026-05-01", force=True)["force"] == "true"


def test_out_of_range_seek_is_refused_by_default():
    """⭐ FOUND BY DRIVING IT. Writing the clock validates nothing: a seek to 2026-05-01 against a
    range loaded 04-19..04-24 answered `succeeded: true, offset 0` — truthfully. The clock really did
    go there; there is no data there. A bake from that position produces nothing while every check
    reads green. A success-shaped nothing, so it now fails CLOSED."""
    st = ntpb.parse_response({
        "status": "error", "action": "seek", "succeeded": False,
        "errors": [{"code": "OUTOFRANGE",
                    "message": "target is OUTSIDE the loaded replay range — the clock would move "
                               "there and find no data, which reads as a successful seek and "
                               "produces an empty run. Pass --force if you mean it."}],
    })
    assert st.ok is False
    assert st.errors[0]["code"] == "OUTOFRANGE"
    assert "empty run" in st.errors[0]["message"]


def test_seek_result_carries_the_loaded_range():
    """'Landed on target' means nothing without the range it landed inside."""
    st = ntpb.parse_response({
        "status": "ok", "action": "seek", "succeeded": True, "inRange": True,
        "rangeFrom": "2026-04-19T00:00:00", "rangeTo": "2026-04-24T23:59:59",
        "landedAt": "2026-04-21T17:00:00", "trajectory": ["2026-04-21T17:00:00"],
    })
    assert st.in_range is True
    assert st.payload["rangeFrom"].startswith("2026-04-19")


def test_a_forced_out_of_range_seek_is_flagged_in_the_result():
    st = ntpb.parse_response({
        "status": "ok", "action": "seek", "succeeded": True, "inRange": False, "forced": True,
        "landedAt": "2026-05-01T12:00:00",
    })
    assert st.in_range is False
    assert st.payload["forced"] is True


# ---- zero is a value (found while restoring a test box to its original state) ----

def test_speed_zero_is_sent_not_swallowed():
    """⭐ 0 IS A REAL SPEED — it is what a PARKED transport reads. A falsy-zero test sent an empty
    string, so the one value needed to STOP a running replay was the one value that could not be
    sent. Found by putting a box back exactly as it was found, which is why that discipline is
    worth the extra step."""
    assert ntpb.build_request("id8", "speed", speed=0)["speed"] == "0"
    assert ntpb.build_request("id9", "speed", speed=100)["speed"] == "100"


def test_speed_omitted_is_still_empty():
    assert ntpb.build_request("id10", "api")["speed"] == ""


def test_parking_a_running_transport_reports_the_real_transition():
    st = ntpb.parse_response({
        "status": "ok", "action": "speed", "succeeded": True,
        "speedBefore": 50, "speedAfter": 0, "verdict": "speed is now 0",
    })
    assert st.succeeded is True
    assert st.payload["speedAfter"] == 0
