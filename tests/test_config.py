import json

import pytest

from nt8bridge import config


def _valid() -> dict:
    return {
        "strategy": "MyStrategy.cs",
        "typeName": "MyStrategy",
        "instrument": "NQ 06-26",
        "barType": {"type": "Minute", "value": 15},
        "from": "2025-01-01",
        "to": "2026-06-01",
        "capital": 50000,
        "commission": "MyCommissionTemplate",
        "slippageTicks": 1,
        "params": {"AtrMult": 2.0},
    }


def test_load_valid(tmp_path):
    p = tmp_path / "c.json"
    p.write_text(json.dumps(_valid()))
    cfg = config.load_config(p)
    assert cfg["typeName"] == "MyStrategy"
    assert cfg["barType"]["value"] == 15


def test_missing_required_field_raises(tmp_path):
    bad = _valid()
    del bad["typeName"]
    p = tmp_path / "c.json"
    p.write_text(json.dumps(bad))
    with pytest.raises(config.ConfigError) as e:
        config.load_config(p)
    assert "typeName" in str(e.value)


def test_bad_bartype_value_raises(tmp_path):
    bad = _valid()
    bad["barType"]["value"] = "fifteen"
    p = tmp_path / "c.json"
    p.write_text(json.dumps(bad))
    with pytest.raises(config.ConfigError):
        config.load_config(p)
