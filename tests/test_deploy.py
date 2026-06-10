import pytest

from nt8bridge import deploy


def _make_src(tmp_path, name="MyStrategy.cs"):
    src = tmp_path / name
    src.write_text("// strategy", encoding="utf-8")
    return src


def test_deploy_strategy_copies_to_strategies(monkeypatch, tmp_path):
    nt8 = tmp_path / "nt8"
    monkeypatch.setenv("NT8_DIR", str(nt8))
    src = _make_src(tmp_path)
    dest = deploy.deploy(src, kind="strategy")
    assert dest == nt8 / "bin" / "Custom" / "Strategies" / "MyStrategy.cs"
    assert dest.read_text(encoding="utf-8") == "// strategy"


def test_deploy_addon_and_indicator_targets(monkeypatch, tmp_path):
    nt8 = tmp_path / "nt8"
    monkeypatch.setenv("NT8_DIR", str(nt8))
    assert deploy.deploy(_make_src(tmp_path, "A.cs"), kind="addon").parent.name == "AddOns"
    assert deploy.deploy(_make_src(tmp_path, "I.cs"), kind="indicator").parent.name == "Indicators"


def test_deploy_unknown_kind_raises(monkeypatch, tmp_path):
    monkeypatch.setenv("NT8_DIR", str(tmp_path / "nt8"))
    with pytest.raises(ValueError):
        deploy.deploy(_make_src(tmp_path), kind="bogus")
