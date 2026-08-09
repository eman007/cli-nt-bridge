"""selfcheck — the client half must be verifiable on its own.

The cases here are the conditions that actually occurred the first time this check was run against a
real machine on 2026-08-04, not hypotheticals:
  • three version numbers disagreed (pyproject 1.6.0 / installed 1.2.0 / __init__ 0.1.0)
  • two packages read MISSING because the check ran under the WRONG one of the box's two venvs
  • an interpreter with an older nt8bridge answers nothing at all, which must never read as a pass
"""
from pathlib import Path

from nt8bridge import selfcheck as sc


# ---- requirement parsing ----

def test_parse_plain_and_pinned():
    assert sc.parse_requirement("tzdata") == ("tzdata", "", "")
    assert sc.parse_requirement("markdown>=3.5") == ("markdown", "", ">=3.5")
    assert sc.parse_requirement("scikit-learn >= 1.3") == ("scikit-learn", "", ">= 1.3")


def test_comments_blanks_and_flags_are_not_requirements():
    assert sc.parse_requirement("") is None
    assert sc.parse_requirement("# numpy>=1.24") is None
    assert sc.parse_requirement("-r other.txt") is None


def test_extras_markers_are_opt_in_and_skipped():
    assert sc._marker_applies('pytest>=7; extra == "dev"') is False
    assert sc._marker_applies("numpy>=1.24") is True


# ---- the fallback comparator ----

def test_naive_comparator_basic_ordering():
    assert sc._naive_satisfied("2.5.1", ">=1.24") is True
    assert sc._naive_satisfied("1.2.0", ">=1.24") is False
    assert sc._naive_satisfied("3.10.3", ">=3.5") is True
    assert sc._naive_satisfied("1.6.0", "==1.6.0") is True


def test_naive_comparator_refuses_rather_than_guesses():
    """A spec it cannot parse must return None, never True. A check that quietly downgrades its own
    rigour is exactly the failure this module exists to prevent."""
    assert sc._naive_satisfied("1.0", "~~1.0") is None
    assert sc._naive_satisfied("abc", ">=1.0") is None


def test_no_specifier_is_satisfied_by_any_version():
    assert sc._naive_satisfied("2026.3", "") is True


# ---- requirement checks ----

def test_missing_package_reports_missing():
    row = sc.check_requirement("definitely-not-installed-xyz>=1.0")
    assert row["status"] == "missing"
    assert row["installed"] is None


def test_present_package_reports_ok():
    row = sc.check_requirement("nt8bridge")
    assert row["status"] == "ok"
    assert row["installed"]


# ---- module digest ----

def test_digest_is_stable_and_counts_modules():
    root = sc.package_dir()
    c1, h1 = sc.module_digest(root)
    c2, h2 = sc.module_digest(root)
    assert h1 == h2
    assert c1 == c2 > 30
    assert len(h1) == 16


def test_digest_ignores_bytecode(tmp_path: Path):
    """Build output must never enter a fleet-equality hash — the localised satellite DLLs carried a
    fresh MVID every compile and so made every node differ forever while sources were identical."""
    pkg = tmp_path / "pkg"
    (pkg / "__pycache__").mkdir(parents=True)
    (pkg / "a.py").write_text("x = 1", encoding="utf-8")
    (pkg / "__pycache__" / "a.cpython-312.pyc").write_bytes(b"\x00\x01")
    count, digest = sc.module_digest(pkg)
    assert count == 1
    (pkg / "__pycache__" / "b.cpython-312.pyc").write_bytes(b"\x02")
    assert sc.module_digest(pkg) == (count, digest)


def test_digest_changes_when_source_changes(tmp_path: Path):
    pkg = tmp_path / "pkg"
    pkg.mkdir()
    (pkg / "a.py").write_text("x = 1", encoding="utf-8")
    before = sc.module_digest(pkg)[1]
    (pkg / "a.py").write_text("x = 2", encoding="utf-8")
    assert sc.module_digest(pkg)[1] != before


# ---- the verdict ----

def test_clean_run_reports_exit_zero():
    res = sc.run_selfcheck()
    assert res.report["command"] == "selfcheck"
    assert res.report["package"]["modules"] > 30
    assert res.report["exit"] in (0, 2)


def test_expect_hash_mismatch_is_drift():
    res = sc.run_selfcheck(expect_hash="0000000000000000")
    assert res.ok is False
    assert res.report["exit"] == 2
    assert any("NOT running the fleet tree" in d for d in res.drift)


def test_expect_version_mismatch_is_drift():
    res = sc.run_selfcheck(expect_version="0.0.1-nope")
    assert res.ok is False
    assert any("expected 0.0.1-nope" in d for d in res.drift)


def test_missing_manifest_is_drift_not_silence():
    res = sc.run_selfcheck(requirements=["Z:/no/such/requirements.txt"])
    assert res.ok is False
    assert any("manifest not found" in d for d in res.drift)


def test_manifest_missing_package_is_drift(tmp_path: Path):
    """The `markdown>=3.5` case: declared on line 4 of a manifest, never installed, and nothing
    checked — which made the tool enforcing the docs rule silently unrunnable."""
    man = tmp_path / "requirements.txt"
    man.write_text("# comment\nnt8bridge\ndefinitely-not-installed-xyz>=1.0\n", encoding="utf-8")
    res = sc.run_selfcheck(requirements=[str(man)])
    assert res.ok is False
    rows = res.report["manifests"][0]["requirements"]
    assert [r["status"] for r in rows] == ["ok", "missing"]
    assert any("MISSING definitely-not-installed-xyz" in d for d in res.drift)


def test_source_version_drift_is_reported(monkeypatch):
    """An editable install keeps the CODE current while the reported version stays whatever it was
    at install time — found live, three versions disagreeing on the reference box."""
    monkeypatch.setattr(sc, "declared_version", lambda tree: "9.9.9")
    res = sc.run_selfcheck()
    assert any("re-install" in d for d in res.drift)


def test_unreadable_package_metadata_refuses_to_render_a_verdict(monkeypatch):
    monkeypatch.setattr(sc, "installed_version", lambda name: None)
    res = sc.run_selfcheck()
    assert res.ok is False
    assert res.report["exit"] == 1
    assert "cannot verify" in res.report["verdict"]


# ---- the other-interpreter path ----

def test_run_under_missing_interpreter_is_an_error_not_a_pass():
    rc, payload = sc.run_under("Z:/no/such/python.exe", [])
    assert rc == 1
    assert payload["status"] == "error"


def test_run_under_no_json_is_reported_as_the_drift(monkeypatch):
    """⭐ A PROBE THAT CAN ONLY PASS ONE WAY IS NOT A CHECK. An interpreter whose nt8bridge predates
    this subcommand emits no JSON — that silence IS the finding, and it must not read as clean.
    A reboot watcher that waited on a subcommand four boxes did not have read a healthy fleet as a
    failed reboot; this is the same shape, caught deliberately."""
    import subprocess

    class Fake:
        returncode = 2
        stdout = ""
        stderr = "invalid choice: 'selfcheck'"

    monkeypatch.setattr(subprocess, "run", lambda *a, **k: Fake())
    rc, payload = sc.run_under("python.exe", [])
    assert rc == 2
    assert payload["status"] == "no-json"
    assert "predates the check" in payload["verdict"]
