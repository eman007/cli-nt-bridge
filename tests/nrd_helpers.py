"""Data-free synthetic .nrd builder for tests — no exchange market data shipped.

A minimal, hand-encoded replay file: a 44-slot header activating Last(2)/ask(10)/bid(11),
then 4 events whose decoded values are hand-verified against the .nrd format spec. Lets the
offline decoder be tested in CI without a real (licensed) market-replay file.
"""
import struct

_EPOCH_TICKS = 621355968000000000
_T0 = _EPOCH_TICKS + 10_000_000_000     # EPOCH + 1000s (clean round number)
HEADER_LEN = 44 * 80


def _slot(count=0, first=0.0, tick=0.0, t0=0, pmin=0.0, pmax=0.0, volsum=0):
    # struct: last, count, pmax, pmin, first, one, tick, flag, t0, t1, volsum
    return struct.pack("<diddddd i qq q", 0.0, count, pmax, pmin, first, 1.0, tick, 0, t0, t0, volsum)


def synthetic_nrd() -> bytes:
    """A valid minimal .nrd: header + 2 L1 (Last) + 2 L2 (ask/bid) events.

    Header volsum/price-range fields match the events so the integrity check passes clean."""
    slots = [_slot() for _ in range(44)]
    slots[2] = _slot(10, 21800.0, 0.25, _T0, pmin=21800.25, pmax=21800.25, volsum=3)    # Last (vol 1+2)
    slots[10] = _slot(10, 21810.0, 0.25, _T0, pmin=21810.0, pmax=21810.0, volsum=5)     # ask (vol 5)
    slots[11] = _slot(10, 21790.0, 0.25, _T0, pmin=21789.5, pmax=21789.5, volsum=3)     # bid (vol 3)
    events = bytes([
        0x20, 0x4F, 0xC0, 0x01,                  # L1 Last: price +1 tick, vol 1 -> 21800.25
        0x20, 0x00, 0x00, 0x05,                  # L2 ask add pos0, vol 5 -> 21810.0
        0x28, 0x80, 0x01, 0x7E, 0x03,            # L2 bid add pos1, price -2 ticks, vol 3 -> 21789.5
        0x21, 0x40, 0xC0, 0x64, 0x02,            # L1 Last: ts +100 (x100ns), vol 2 -> 21800.25
    ])
    return b"".join(slots) + events


# decoded expectations (hand-verified)
EXPECTED_L1 = [                 # (ts_ns_utc, mdt, price, vol)
    (1000000000000, 2, 21800.25, 1),
    (1000000010000, 2, 21800.25, 2),
]
EXPECTED_L2 = [                 # (ts_ns_utc, side, op, pos, price, vol)
    (1000000000000, 0, 0, 0, 21810.0, 5),
    (1000000000000, 1, 0, 1, 21789.5, 3),
]
