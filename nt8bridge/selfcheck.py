"""selfcheck — does THIS machine's nt8bridge match its source and its manifest?

WHY THIS EXISTS
    2026-08-04. A six-box fleet was proven to be running one identical `bin\\Custom` tree, and the
    conclusion drawn was "the fleet runs one tree". It did not. The PYTHON half — this package, the
    thing every automation actually invokes — was three different versions across those six boxes
    (two at 38 modules, four at 34 plus an older `cli.py`), and `ntstatus` simply did not exist on
    four of them. Nothing had ever checked it, because the fleet's sync tool only ever knew about
    the C# tree.

    The same week, `markdown>=3.5` sat on line 4 of a requirements file and had never been
    installed, which made the tool that enforces the documentation rule silently unrunnable.

    Both failures are one shape: A SECOND SURFACE THAT NOTHING VERIFIES. The cheapest place to close
    it is here — inside the thing that drifts — because every caller already runs this CLI.

WHAT IT ANSWERS
    • which nt8bridge is imported, from where, and is it an editable install pointing at a source tree
    • does the INSTALLED metadata version match the version declared in that source tree
      (a source update that was never re-installed reads as up to date everywhere else)
    • a stable content hash over the package's modules, so a fleet can be asserted equal in one field
    • are the package's own declared dependencies actually importable at satisfying versions
    • the same question for any EXTRA manifest handed in with --requirements (that is the hook for a
      project-level requirements.txt this repo knows nothing about)

⭐ A PROBE THAT CAN ONLY PASS ONE WAY IS NOT A CHECK
    An older CLI has no `selfcheck` subcommand, so it exits non-zero from argparse and prints NOTHING
    to stdout. That is deliberate and it is the point: callers key on the `"command": "selfcheck"`
    field, and its ABSENCE is itself the finding ("this node's CLI predates the check"). Compare the
    reboot watcher that waited for a subcommand four boxes did not have and read a healthy fleet as a
    failed reboot.

EXIT CODES
    0  clean
    1  could not determine (package metadata unreadable) — a refusal to guess, not a pass
    2  DRIFT: a dependency missing/unsatisfied, or an --expect-* assertion failed
"""
from __future__ import annotations

import hashlib
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

import nt8bridge

# ---------------------------------------------------------------- version comparison
# `packaging` is present in virtually every environment that has pip, but it is NOT a declared
# dependency of this package, so it must not be REQUIRED. When it is missing we fall back to a
# numeric comparator and SAY SO in the output — a check that quietly downgrades its own rigour is
# the failure mode this whole module exists to prevent.
try:  # pragma: no cover - environment dependent
    from packaging.requirements import Requirement as _Requirement
    from packaging.version import Version as _Version

    COMPARATOR = "packaging"
except Exception:  # pragma: no cover - environment dependent
    _Requirement = None
    _Version = None
    COMPARATOR = "naive"

_REQ_RE = re.compile(r"^\s*([A-Za-z0-9._-]+)\s*(\[[^\]]*\])?\s*([<>=!~][^;]*)?\s*(;.*)?$")


def _naive_key(v: str):
    """Compare only the leading numeric release segment, and refuse anything else."""
    parts = []
    for chunk in re.split(r"[._-]", v.strip()):
        if chunk.isdigit():
            parts.append(int(chunk))
        else:
            m = re.match(r"^(\d+)", chunk)
            if m:
                parts.append(int(m.group(1)))
            break
    return tuple(parts)


def _naive_satisfied(installed: str, spec: str) -> bool | None:
    """None = could not decide. Never guess True."""
    if not spec:
        return True
    have = _naive_key(installed)
    if not have:
        return None
    for clause in spec.split(","):
        clause = clause.strip()
        m = re.match(r"^(===|==|!=|>=|<=|~=|>|<)\s*(.+)$", clause)
        if not m:
            return None
        op, want_s = m.group(1), m.group(2).strip().rstrip("*").rstrip(".")
        want = _naive_key(want_s)
        if not want:
            return None
        n = max(len(have), len(want))
        h = have + (0,) * (n - len(have))
        w = want + (0,) * (n - len(want))
        if op in ("==", "==="):
            ok = h == w
        elif op == "!=":
            ok = h != w
        elif op == ">=":
            ok = h >= w
        elif op == "<=":
            ok = h <= w
        elif op == ">":
            ok = h > w
        elif op == "<":
            ok = h < w
        elif op == "~=":
            ok = h >= w and h[: len(w) - 1] == w[: len(w) - 1]
        else:
            return None
        if not ok:
            return False
    return True


def parse_requirement(line: str) -> tuple[str, str, str] | None:
    """-> (name, extras, specifier). None for a line that is not a requirement at all."""
    line = line.strip()
    if not line or line.startswith("#") or line.startswith("-"):
        return None
    line = line.split(" #", 1)[0].strip()
    m = _REQ_RE.match(line)
    if not m:
        return None
    return m.group(1), (m.group(2) or ""), (m.group(3) or "").strip()


def _marker_applies(line: str) -> bool:
    """Skip requirements gated behind an extra (dev/report/parquet) — they are opt-in by design."""
    if ";" not in line:
        return True
    marker = line.split(";", 1)[1]
    return "extra" not in marker


def installed_version(name: str) -> str | None:
    from importlib import metadata

    for candidate in (name, name.replace("-", "_"), name.replace("_", "-")):
        try:
            return metadata.version(candidate)
        except Exception:
            continue
    return None


def check_requirement(line: str) -> dict:
    parsed = parse_requirement(line)
    if parsed is None:
        return {"raw": line.strip(), "status": "skipped"}
    name, _extras, spec = parsed
    have = installed_version(name)
    row = {"name": name, "required": spec or "*", "installed": have}
    if have is None:
        row["status"] = "missing"
        return row
    if _Requirement is not None and _Version is not None:
        try:
            ok = _Version(have) in _Requirement(line.split(";", 1)[0]).specifier or not spec
        except Exception:
            ok = _naive_satisfied(have, spec)
    else:
        ok = _naive_satisfied(have, spec)
    row["status"] = {True: "ok", False: "unsatisfied", None: "unknown"}[ok]
    return row


# ---------------------------------------------------------------- package identity
def package_dir() -> Path:
    return Path(nt8bridge.__file__).resolve().parent


def module_files(root: Path) -> list[Path]:
    return sorted(
        (p for p in root.rglob("*.py") if "__pycache__" not in p.parts),
        key=lambda p: p.relative_to(root).as_posix(),
    )


def module_digest(root: Path) -> tuple[int, str]:
    """Content hash over the package's .py files: name + bytes, order-stable.

    Hashes SOURCE only. Build output must never enter a fleet-equality hash — that lesson was paid
    for by the localised satellite DLLs, which carry a fresh MVID every compile and so made every
    node differ forever while their sources were byte-identical.
    """
    h = hashlib.sha256()
    files = module_files(root)
    for p in files:
        h.update(p.relative_to(root).as_posix().encode("utf-8"))
        h.update(b"\0")
        h.update(hashlib.sha256(p.read_bytes()).digest())
    return len(files), h.hexdigest()[:16]


def source_tree(root: Path) -> Path | None:
    """The checkout this package lives in, if it is an editable/source install rather than a copy."""
    parent = root.parent
    if (parent / "pyproject.toml").is_file():
        return parent
    return None


def declared_version(tree: Path) -> str | None:
    try:
        text = (tree / "pyproject.toml").read_text(encoding="utf-8")
    except Exception:
        return None
    m = re.search(r'(?m)^\s*version\s*=\s*"([^"]+)"', text)
    return m.group(1) if m else None


def declared_requirements() -> list[str]:
    from importlib import metadata

    try:
        reqs = metadata.requires("nt8bridge") or []
    except Exception:
        reqs = []
    return [r for r in reqs if _marker_applies(r)]


def run_under(python_exe: str, argv_tail: list[str]) -> tuple[int, dict]:
    """Re-run this same check under ANOTHER interpreter and return its verdict.

    A machine can host more than one virtualenv — this one hosts two, and each has its own
    nt8bridge and its own idea of what is installed. "Is the manifest satisfied?" therefore has no
    answer until you say WHICH interpreter, and the first run of this tool reported two packages
    missing that were present all along in the other venv.

    An interpreter whose nt8bridge is too old has no `selfcheck` subcommand and emits no JSON. That
    is reported as the finding it is, never as a pass.
    """
    import json as _json
    import subprocess

    cmd = [python_exe, "-m", "nt8bridge", "selfcheck"] + argv_tail
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
    except Exception as e:
        return 1, {"command": "selfcheck", "viaPython": python_exe, "status": "error",
                   "verdict": "could not launch %s: %s" % (python_exe, e)}
    try:
        payload = _json.loads(proc.stdout)
    except Exception:
        return 2, {
            "command": "selfcheck", "viaPython": python_exe, "status": "no-json",
            "exitCode": proc.returncode,
            "stderr": (proc.stderr or "").strip()[:400],
            "verdict": "that interpreter's nt8bridge produced no selfcheck output — it predates the "
                       "check, or the package is not installed there. That IS the drift.",
        }
    payload["viaPython"] = python_exe
    return int(payload.get("exit", proc.returncode)), payload


@dataclass
class SelfCheck:
    ok: bool = True
    drift: list[str] = field(default_factory=list)
    report: dict = field(default_factory=dict)


def run_selfcheck(requirements: list[str] | None = None,
                  expect_hash: str | None = None,
                  expect_version: str | None = None) -> SelfCheck:
    res = SelfCheck()
    root = package_dir()
    count, digest = module_digest(root)
    tree = source_tree(root)
    inst_ver = installed_version("nt8bridge")
    src_ver = declared_version(tree) if tree else None

    res.report = {
        "command": "selfcheck",
        "python": {"executable": sys.executable,
                   "version": "%d.%d.%d" % sys.version_info[:3]},
        "package": {
            "importedFrom": str(root),
            "installedVersion": inst_ver,
            "moduleVersion": getattr(nt8bridge, "__version__", None),
            "modules": count,
            "moduleHash": digest,
            "sourceTree": str(tree) if tree else None,
            "editable": tree is not None,
            "sourceVersion": src_ver,
        },
        "comparator": COMPARATOR,
    }

    if inst_ver is None:
        # Refuse to render a verdict on a package we cannot identify.
        res.ok = False
        res.report["verdict"] = "package metadata unreadable — cannot verify this install"
        res.report["exit"] = 1
        return res

    # A source tree whose declared version differs from the INSTALLED metadata means somebody
    # updated the checkout and never re-installed it. Every other check would still read clean.
    if src_ver and inst_ver and src_ver != inst_ver:
        res.drift.append(
            "source tree declares %s but the INSTALLED package is %s — re-install (pip install -e)"
            % (src_ver, inst_ver))

    # THREE places carry a version — pyproject, the installed dist metadata, and __init__.__version__
    # — and on the first run of this check all three disagreed (1.6.0 / 1.2.0 / 0.1.0). An editable
    # install keeps the CODE current while the reported version stays whatever it was at install
    # time, so a version chip can read stale while the behaviour is new. Compare all three or the
    # check inherits the same blind spot.
    mod_ver = getattr(nt8bridge, "__version__", None)
    if mod_ver and src_ver and mod_ver != src_ver:
        res.drift.append("nt8bridge.__version__ is %s but pyproject declares %s" % (mod_ver, src_ver))

    deps = [check_requirement(r) for r in declared_requirements()]
    res.report["dependencies"] = deps
    for d in deps:
        if d.get("status") == "missing":
            res.drift.append("dependency MISSING: %s %s" % (d["name"], d["required"]))
        elif d.get("status") == "unsatisfied":
            res.drift.append("dependency UNSATISFIED: %s %s (have %s)"
                             % (d["name"], d["required"], d["installed"]))
        elif d.get("status") == "unknown":
            # Reported, never silently passed — but it does not fail the fleet on its own.
            res.report.setdefault("unparsed", []).append(d["name"])

    manifests = []
    for path_s in requirements or []:
        p = Path(path_s)
        entry = {"path": str(p)}
        if not p.is_file():
            entry["status"] = "missing"
            res.drift.append("requirements manifest not found: %s" % p)
        else:
            rows = []
            for line in p.read_text(encoding="utf-8", errors="replace").splitlines():
                if not _marker_applies(line):
                    continue
                row = check_requirement(line)
                if row.get("status") == "skipped":
                    continue
                rows.append(row)
                if row["status"] == "missing":
                    res.drift.append("%s: MISSING %s %s" % (p.name, row["name"], row["required"]))
                elif row["status"] == "unsatisfied":
                    res.drift.append("%s: UNSATISFIED %s %s (have %s)"
                                     % (p.name, row["name"], row["required"], row["installed"]))
            entry["status"] = "ok"
            entry["requirements"] = rows
        manifests.append(entry)
    if manifests:
        res.report["manifests"] = manifests

    if expect_hash:
        res.report["expectedHash"] = expect_hash
        if expect_hash != digest:
            res.drift.append("module hash %s != expected %s — this node is NOT running the fleet tree"
                             % (digest, expect_hash))
    if expect_version:
        res.report["expectedVersion"] = expect_version
        if expect_version != inst_ver:
            res.drift.append("version %s != expected %s" % (inst_ver, expect_version))

    res.ok = not res.drift
    res.report["drift"] = res.drift
    res.report["verdict"] = "clean" if res.ok else "; ".join(res.drift)
    res.report["exit"] = 0 if res.ok else 2
    return res
