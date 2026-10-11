from pathlib import Path

import numpy as np
import pytest

from nt8bridge import nrd_offline as N
from tests import nrd_helpers as H

FIX = Path(__file__).parent / "fixtures" / "nrd" / "sample_MNQ_20241207.nrd"
# The real .nrd is exchange market data (not shipped publicly); those tests run only where present.
_real = pytest.mark.skipif(not FIX.exists(), reason="real .nrd fixture absent (private tree only)")


# ---- synthetic-buffer tests: data-free, run everywhere (incl. public CI) ----

def test_decode_synthetic():
    raw = H.synthetic_nrd()
    slots = N.parse_headers(raw[:H.HEADER_LEN])
    d = N.decode(raw[H.HEADER_LEN:], slots)
    assert d["truncated"] is False
    l1 = list(zip(d["L1"][0].tolist(), d["L1"][1].tolist(),
                  d["L1"][2].tolist(), d["L1"][3].tolist()))
    l2 = list(zip(d["L2"][0].tolist(), d["L2"][1].tolist(), d["L2"][2].tolist(),
                  d["L2"][3].tolist(), d["L2"][4].tolist(), d["L2"][5].tolist()))
    assert l1 == H.EXPECTED_L1
    assert l2 == H.EXPECTED_L2


def test_salvage_synthetic():
    raw = H.synthetic_nrd()
    slots = N.parse_headers(raw[:H.HEADER_LEN])
    events = raw[H.HEADER_LEN:]
    d = N.decode(events[:-1], slots, salvage=True)      # cut the last byte -> final record incomplete
    assert d["truncated"] is True
    assert len(d["L1"][0]) == 1 and len(d["L2"][0]) == 2   # only the final L1 event dropped


def test_salvage_off_raises_synthetic():
    raw = H.synthetic_nrd()
    slots = N.parse_headers(raw[:H.HEADER_LEN])
    with pytest.raises(N.FormatError):
        N.decode(raw[H.HEADER_LEN:-1], slots, salvage=False)


def test_l2_types_synthetic():
    raw = H.synthetic_nrd()
    slots = N.parse_headers(raw[:H.HEADER_LEN])
    _, side, op, pos, _, _ = N.decode(raw[H.HEADER_LEN:], slots)["L2"]
    assert side.dtype == np.int8 and op.dtype == np.int8 and pos.dtype == np.int32


def test_integrity_clean_synthetic():
    raw = H.synthetic_nrd()
    d = N.decode(raw[H.HEADER_LEN:], N.parse_headers(raw[:H.HEADER_LEN]))
    assert d["integrity_ok"] is True and d["integrity_errors"] == []


def test_integrity_catches_corruption():
    raw = bytearray(H.synthetic_nrd())
    raw[H.HEADER_LEN + 3] = 0xFF          # corrupt the first L1 event's volume byte (1 -> 255)
    d = N.decode(bytes(raw[H.HEADER_LEN:]), N.parse_headers(bytes(raw[:H.HEADER_LEN])))
    assert d["integrity_ok"] is False
    assert any("volume" in e for e in d["integrity_errors"])


def _slots_with_lastclose(pmin, pmax, tick=0.005, volsum=3, lc_tick=None):
    slots = [dict(count=0, first=0.0, tick=tick, t0=0, t1=0, pmax=0.0, pmin=0.0, volsum=0)
             for _ in range(12)]
    slots[2] = dict(count=1, first=50.0, tick=tick, t0=0, t1=0, pmax=50.0, pmin=50.0, volsum=1)
    slots[6] = dict(count=1, first=pmin, tick=tick if lc_tick is None else lc_tick, t0=0, t1=0,
                    pmax=pmax, pmin=pmin, volsum=volsum)
    return slots


def _l1_lastclose(price):
    # one trade (slot 2, volume 1, inside the header range) and one LastClose event (slot 6)
    return {"L1": (np.array([0, 1], dtype=np.int64), np.array([2, 6], dtype=np.int8),
                   np.array([50.0, price]), np.array([1, 3], dtype=np.int64))}


def test_integrity_offgrid_header_within_a_tick_is_clean():
    # SI settlements are 3 decimals on a 0.005 tick; the decoder lands on a neighbouring grid
    # point, up to ~0.6 tick from the header value (22.388 -> 22.385)
    assert N._integrity_errors(_slots_with_lastclose(61.153, 61.153), _l1_lastclose(61.155)) == []
    assert N._integrity_errors(_slots_with_lastclose(60.566, 60.566), _l1_lastclose(60.565)) == []
    assert N._integrity_errors(_slots_with_lastclose(22.388, 22.642), _l1_lastclose(22.385)) == []


def test_integrity_price_beyond_a_tick_still_flagged():
    errs = N._integrity_errors(_slots_with_lastclose(61.153, 61.153), _l1_lastclose(61.17))
    assert any("price outside header range" in e for e in errs)


def test_integrity_inflated_tick_does_not_widen_its_own_check():
    # a damaged tick (0.005 -> 327.68) on the LastClose slot must not become its own slack
    errs = N._integrity_errors(_slots_with_lastclose(61.153, 61.153, lc_tick=327.68),
                               _l1_lastclose(0.0))
    assert any("price outside header range" in e for e in errs)


def test_integrity_skipped_when_truncated():
    # a truncated file legitimately falls short of the header totals -> not flagged as corrupt
    raw = H.synthetic_nrd()
    d = N.decode(raw[H.HEADER_LEN:-1], N.parse_headers(raw[:H.HEADER_LEN]), salvage=True)
    assert d["truncated"] is True and d["integrity_ok"] is True


def test_season_year_dec_roll_vs_calendar():
    assert N.season_year("MNQ", "20251216") == "2026"   # quarterly: after Dec roll
    assert N.season_year("MNQ", "20251201") == "2025"   # quarterly: before Dec roll
    assert N.season_year("MGC", "20251231") == "2025"   # non-quarterly: calendar year


def test_symbol_of_and_discover_regex():
    assert N.symbol_of("MNQ 09-25") == "MNQ"
    assert N.symbol_of("MGC ##-##") == "MGC"
    assert N.symbol_of("NQ ##-26") is None              # half-renamed junk -> skipped


# ---- real-fixture tests: private tree only (skip in public) ----

@_real
def test_decode_counts_and_first_event():
    d = N.convert_file(FIX)
    assert d["truncated"] is False
    l1_ts, l1_mdt, l1_price, l1_vol = d["L1"]
    assert len(l1_ts) == 12 and len(d["L2"][0]) == 20
    assert l1_ts[0] == 1733587536600550300
    assert l1_mdt[0] == 3 and l1_price[0] == 21935.25 and l1_vol[0] == 0


@_real
def test_salvage_drops_only_truncated_final_record():
    raw = FIX.read_bytes()
    slots = N.parse_headers(raw[:N.N_SLOTS * 80])
    full = N.decode(raw[N.N_SLOTS * 80:], slots)
    trunc = N.decode(raw[N.N_SLOTS * 80:-1], slots, salvage=True)
    assert trunc["truncated"] is True
    nf = len(full["L1"][0]) + len(full["L2"][0])
    nt = len(trunc["L1"][0]) + len(trunc["L2"][0])
    assert nf - nt == 1
    assert np.array_equal(trunc["L1"][2], full["L1"][2][:len(trunc["L1"][0])])
    assert np.array_equal(trunc["L2"][4], full["L2"][4][:len(trunc["L2"][0])])


@_real
def test_integrity_clean_real_fixture():
    d = N.convert_file(FIX)
    assert d["integrity_ok"] is True and d["integrity_errors"] == []
