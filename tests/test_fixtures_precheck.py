import shutil

import pytest

from nt8bridge import precheck

FIX = __import__("pathlib").Path(__file__).parent / "fixtures"

requires_compiler = pytest.mark.skipif(
    shutil.which("powershell") is None or not precheck._compiler_script().is_file(),
    reason="offline NinjaScript compiler unavailable (set NT8BRIDGE_COMPILER)",
)


@requires_compiler
def test_bad_compile_is_caught_offline():
    errors = precheck.run_precheck(FIX / "BadCompileStrategy.cs")
    codes = {e.code for e in errors}
    assert "CS0103" in codes


@requires_compiler
def test_good_strategy_compiles_clean_offline():
    assert precheck.run_precheck(FIX / "GoodStrategy.cs") == []


@requires_compiler
def test_bad_load_compiles_clean_offline_but_is_the_gap_case():
    # Offline says clean; NT8 will reject it at load. Part 2 proves the catch.
    assert precheck.run_precheck(FIX / "BadLoadStrategy.cs") == []
