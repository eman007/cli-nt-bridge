"""playback / ntstatus / workspace — the read-only state commands.

Every case below is a condition that actually occurred on 2026-08-02, not a hypothetical. The two
marked REGRESSION are defects found by running the commands against a live NinjaTrader half an hour
after they were written, which is the only reason they are here rather than in production.
"""
from nt8bridge import ntstatus as ntntstatus
from nt8bridge import playback as ntplayback
from nt8bridge import workspace as ntworkspace


# ---- playback ----

def test_playback_build_request():
    assert ntplayback.build_playback_request("id1") == {"id": "id1", "kind": "playback"}
    req = ntplayback.build_playback_request("id2", "NQ 06-26")
    assert req["instrument"] == "NQ 06-26"


def _pb(**over):
    payload = {
        "status": "ok",
        "connection": {"status": "Connected", "connected": True},
        "clockEst": "2026-04-20T00:00:00.0000000",
        "moving": False,
        "speed": 0,
        "coverage": [{"instrument": "NQ 06-26", "files": 4, "readable": 4,
                      "from": "2026-04-19T23:00:00", "to": "2026-04-23T22:59:00"}],
    }
    payload.update(over)
    return ntplayback.parse_playback_response(payload)


def test_parked_transport_with_data_is_ready():
    ok, why = _pb().ready_to_seek()
    assert ok is True
    assert "parked" in why


def test_moving_transport_is_refused():
    """The Gate 3 divergence: node01's clock was parked and seeked; sentry-1's was moving and the
    identical Reset() call silently no-opped."""
    ok, why = _pb(moving=True, clockEst="2026-04-21T02:53:52").ready_to_seek()
    assert ok is False
    assert "advancing" in why


def test_disconnected_is_refused():
    ok, why = _pb(connection={"status": "Disconnected", "connected": False}).ready_to_seek()
    assert ok is False
    assert "not connected" in why


def test_unset_clock_is_refused_even_though_it_is_stationary():
    """REGRESSION. A connected Playback with nothing loaded reads 2099-12-01 at speed 0 — perfectly
    stationary, and so passed the moving/not-moving test as READY on the first live run. A transport
    with no data is the emptiest kind of not-ready; reporting it green is the exact success-shaped
    nothing these commands exist to prevent."""
    st = _pb(clockEst="2099-12-01T00:00:00.0000000", coverage=[])
    assert st.clock_unset() is True
    ok, why = st.ready_to_seek()
    assert ok is False
    assert "no replay data loaded" in why


def test_no_readable_nrd_is_refused():
    ok, why = _pb(coverage=[{"instrument": "NQ 06-26", "files": 3, "readable": 0}]).ready_to_seek()
    assert ok is False
    assert "no readable" in why


def test_span_lookup():
    st = _pb()
    assert st.span("NQ 06-26") == ("2026-04-19T23:00:00", "2026-04-23T22:59:00")
    assert st.span("ES 06-26") == (None, None)


# ---- ntstatus ----

def test_stale_when_dll_is_newer_than_the_process():
    """The 33-minute wasted cell: source deployed, assembly never rebuilt, NT never restarted."""
    st = ntntstatus.assess({
        "status": "ok", "pid": 1234,
        "processStartUtc": "2026-08-02T21:13:01Z",
        "dllOnDisk": {"builtUtc": "2026-08-02T21:16:43Z"},
    })
    assert st.ok is True and st.stale is True
    assert "restart it" in st.reason


def test_not_stale_when_the_process_started_after_the_build():
    st = ntntstatus.assess({
        "status": "ok",
        "processStartUtc": "2026-08-02T21:13:01Z",
        "dllOnDisk": {"builtUtc": "2026-08-02T21:10:59Z"},
    })
    assert st.stale is False
    assert "running current code" in st.reason


def test_missing_timestamps_do_not_silently_pass_as_fresh():
    """Absence is reported as 'cannot compare', never as 'not stale' — a check that could not look
    must not answer."""
    st = ntntstatus.assess({"status": "ok", "processStartUtc": None, "dllOnDisk": {}})
    assert st.stale is False
    assert "cannot compare" in st.reason


def test_error_payload_is_not_ok():
    assert ntntstatus.assess({"status": "error"}).ok is False


# ---- workspace ----

def _ws(strategies):
    return ntworkspace.parse_workspace_response({
        "status": "ok", "workspace": "Gate3", "chartCount": 1,
        "charts": [{"title": "Chart - NQ 06-26", "instrument": "NQ 06-26 Globex",
                    "barsPeriod": "1 Minute", "indicators": [], "strategies": strategies}],
    })


def test_enabled_strategy_is_found():
    ok, why = _ws([{"name": "Sentinel Keel", "type": "SentinelKeel", "state": "Realtime", "enabled": True}]) \
        .strategy_running("Keel")
    assert ok is True and "Realtime" in why


def test_disabled_strategy_is_reported_as_found_but_off():
    """Toggling the Playback connection disables chart strategies. 'Present but off' and 'absent'
    need different fixes, so they must not collapse to the same answer."""
    ok, why = _ws([{"name": "Sentinel Keel", "type": "SentinelKeel", "state": "Terminated", "enabled": False}]) \
        .strategy_running("Keel")
    assert ok is False
    assert "found but not enabled" in why


def test_matches_on_type_when_the_name_is_blank():
    """REGRESSION. Sentinel tools BLANK their own Name at DataLoaded — the on-chart label IS the
    Name property — so name-only matching found nothing on the first live run against a real chart."""
    ok, why = _ws([{"name": "", "type": "SentinelKeel_v0_1_0", "state": "Realtime", "enabled": True}]) \
        .strategy_running("keel")
    assert ok is True
    assert "SentinelKeel_v0_1_0" in why


def test_absent_strategy_says_absent():
    ok, why = _ws([]).strategy_running("Keel")
    assert ok is False
    assert "no strategy matching" in why


def test_unreadable_chart_is_skipped_not_counted_as_empty():
    """`strategies: null` means the member did not resolve; it is NOT an empty list. Treating the
    two alike would let an unreadable chart read as a chart with nothing on it."""
    state = ntworkspace.parse_workspace_response({
        "status": "ok",
        "charts": [{"title": "Chart - NQ 06-26", "strategies": None},
                   {"title": "Chart - ES 06-26",
                    "strategies": [{"name": "Sentinel Keel", "type": "SentinelKeel",
                                    "state": "Realtime", "enabled": True}]}],
    })
    ok, why = state.strategy_running("Keel")
    assert ok is True and "ES 06-26" in why


def test_enabled_strategies_are_tagged_with_their_chart():
    state = _ws([{"name": "Sentinel Keel", "type": "SentinelKeel", "state": "Realtime", "enabled": True},
                 {"name": "Other", "type": "Other", "state": "Terminated", "enabled": False}])
    got = state.enabled_strategies()
    assert len(got) == 1
    assert got[0]["chart"] == "Chart - NQ 06-26"
