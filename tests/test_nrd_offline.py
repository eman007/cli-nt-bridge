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
