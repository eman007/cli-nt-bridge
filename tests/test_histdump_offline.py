from pathlib import Path

import pyarrow.parquet as pq

from nt8bridge import histdump
from tests import nrd_helpers as H


def _setup_replay(tmp_path):
    """A replay dir with one synthetic .nrd at MNQ 03-25/20241207 (season 2024)."""
    contract = tmp_path / "replay" / "MNQ 03-25"
    contract.mkdir(parents=True)
    (contract / "20241207.nrd").write_bytes(H.synthetic_nrd())
    return tmp_path / "replay"


def test_run_offline_writes_l1_l2_parquet(tmp_path):
    replay = _setup_replay(tmp_path)
    out = tmp_path / "parq"
    res = histdump.run_histdump(instrument_glob="MNQ*", out_dir=out,
                                replay_dir=replay, engine="offline")
    assert res["engine"] == "offline" and res["count"] == 1
    # MNQ on 20241207 -> season 2024 (before the Dec roll)
    l1 = out / "2024" / "MNQ-2024_L1" / "20241207.parquet"
    l2 = out / "2024" / "MNQ-2024_L2" / "20241207.parquet"
    assert l1.exists() and l2.exists()
    t1 = pq.read_table(l1)
    t2 = pq.read_table(l2)
    assert t1.num_rows == len(H.EXPECTED_L1) and t2.num_rows == len(H.EXPECTED_L2)
    assert t1.schema.field("Timestamp").type.tz == "UTC"
    assert set(t2.column_names) == {"Timestamp", "MarketDataType", "Operation",
                                    "Position", "MarketMaker", "Price", "Volume"}
    # values round-trip through parquet
    assert t1.column("Price").to_pylist() == [r[2] for r in H.EXPECTED_L1]


def test_run_offline_is_idempotent(tmp_path):
    replay = _setup_replay(tmp_path)
    out = tmp_path / "parq"
    histdump.run_histdump(instrument_glob="MNQ*", out_dir=out, replay_dir=replay, engine="offline")
    res2 = histdump.run_histdump(instrument_glob="MNQ*", out_dir=out, replay_dir=replay, engine="offline")
    assert res2["count"] == 0          # both levels already present -> skipped


def test_run_offline_levels_l1_only(tmp_path):
    replay = _setup_replay(tmp_path)
    out = tmp_path / "parq"
    res = histdump.run_histdump(instrument_glob="MNQ*", out_dir=out, replay_dir=replay,
                                engine="offline", levels=("L1",))
    assert res["count"] == 1
    assert (out / "2024" / "MNQ-2024_L1" / "20241207.parquet").exists()
    assert not (out / "2024" / "MNQ-2024_L2").exists()


def test_histdump_cli_defaults_to_offline(monkeypatch):
    from nt8bridge import cli
    seen = {}

    def fake(**kw):
        seen.update(kw)
        return {"status": "ok", "engine": "offline", "count": 0, "exported": [], "failed": []}

    monkeypatch.setattr(cli.nthistdump, "run_histdump", fake)
    assert cli.main(["histdump", "--instrument", "MNQ*", "--out", "x"]) == 0
    assert seen["engine"] == "offline" and seen["levels"] == ("L1", "L2")
    cli.main(["histdump", "--instrument", "MNQ*", "--out", "x", "--nt8"])
    assert seen["engine"] == "nt8"
    cli.main(["histdump", "--instrument", "MNQ*", "--out", "x", "--levels", "L1"])
    assert seen["levels"] == ("L1",)
