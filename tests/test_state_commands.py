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


def test_playback_request_carries_the_coverage_opt_in_only_when_asked():
    """The wide .nrd scan is opt-in (measured 2026-08-19: 3-7 min for 35 instruments, and the
    AddOn's poller is held for that long). The flag travels as the STRING "true" because that is
    what the AddOn compares it to; a request without the flag must not carry the key at all."""
    assert ntplayback.build_playback_request("id3", coverage=True) == {
        "id": "id3", "kind": "playback", "coverage": "true"}
    assert "coverage" not in ntplayback.build_playback_request("id4")
    assert "coverage" not in ntplayback.build_playback_request("id5", "NQ 06-26")
    both = ntplayback.build_playback_request("id6", "NQ 06-26", coverage=True)
    assert both["instrument"] == "NQ 06-26" and both["coverage"] == "true"


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


def test_unscanned_coverage_is_not_read_as_an_empty_store():
    """A request naming neither an instrument nor the coverage opt-in comes back with
    coverageScanned false and coverage []. Nobody looked at the store, so the verdict must say so
    instead of "no readable .nrd" — a claim about data that was never read."""
    st = _pb(coverageScanned=False, coverage=[])
    assert st.coverage_scanned is False
    ok, why = st.ready_to_seek()
    assert ok is False
    assert "coverage not scanned" in why
    assert "no readable" not in why


def test_scanned_coverage_with_data_is_ready():
    ok, why = _pb(coverageScanned=True).ready_to_seek()
    assert ok is True
    assert "parked" in why


def test_absent_coverage_scanned_key_keeps_the_old_meaning():
    """An AddOn without the opt-in never writes coverageScanned and always scans, so its empty
    coverage list still means 'nothing readable'."""
    st = _pb(coverage=[])
    assert st.coverage_scanned is True
    ok, why = st.ready_to_seek()
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


def test_not_stale_when_the_running_code_is_newer_than_every_source():
    """Measured 2026-09-02/03: compile + reload in one process started at 16:02; the DLL on disk
    is newer than the process every time, NinjaTrader executes a temp assembly compiled from the
    sources, and that is what runs. Sources vs running code decides, not clocks of the process."""
    st = ntntstatus.assess({
        "status": "ok",
        "processStartUtc": "2026-09-02T14:02:37Z",
        "dllOnDisk": {"builtUtc": "2026-09-03T03:15:04Z"},
        "runningAssembly": {"name": "0d46200ae29d4fc2b124b520081cbd4f",
                            "location": r"C:\Users\x\Documents\NinjaTrader 8\tmp\0d46200ae29d4fc2b124b520081cbd4f.dll",
                            "builtUtc": "2026-09-03T03:15:03Z"},
        "newestSource": {"path": r"C:\Users\x\Documents\NinjaTrader 8\bin\Custom\AddOns\NT8BridgeServer.cs",
                         "modifiedUtc": "2026-09-03T03:14:40Z"},
        "sourcesNewerThanRunningCode": False,
    })
    assert st.stale is False
    assert "running current code" in st.reason
    assert st.sources_newer is False and st.running_built == "2026-09-03T03:15:03Z"


def test_stale_when_a_source_is_newer_than_the_running_code():
    """The 33-minute wasted cell: a source deployed after the running code was compiled."""
    st = ntntstatus.assess({
        "status": "ok",
        "processStartUtc": "2026-09-02T14:02:37Z",
        "dllOnDisk": {"builtUtc": "2026-09-03T03:15:04Z"},
        "runningAssembly": {"builtUtc": "2026-09-03T03:15:03Z"},
        "newestSource": {"path": r"C:\x\bin\Custom\Strategies\MyBot.cs", "modifiedUtc": "2026-09-03T03:40:00Z"},
        "sourcesNewerThanRunningCode": True,
    })
    assert st.stale is True
    assert "MyBot.cs" in st.reason and "reload or restart" in st.reason


def test_without_a_source_comparison_the_time_rule_applies_and_says_so():
    """An AddOn that could not read one side (or an older AddOn) reports no comparison; the
    verdict then falls back to the clocks and names that."""
    st = ntntstatus.assess({
        "status": "ok",
        "processStartUtc": "2026-09-02T14:02:37Z",
        "dllOnDisk": {"builtUtc": "2026-09-02T18:17:37Z"},
        "sourcesNewerThanRunningCode": None,
    })
    assert st.stale is True
    assert "time rule" in st.reason


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
