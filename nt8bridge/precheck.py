"""Offline compile pre-check (Channel 1, fast). Wraps the PowerShell compiler."""
from __future__ import annotations

import os
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path

# Matches MSBuild/csc error lines:
#   path\File.cs(line,col): error CSxxxx: message [optional trailing project]
_ERR_RE = re.compile(
    r"^(?P<file>.+?)\((?P<line>\d+),\d+\):\s*error\s+(?P<code>CS\d+):\s*(?P<message>.+?)(?:\s*\[[^\]]*\])?\s*$"
)

_DEFAULT_COMPILER = (
    Path(__file__).resolve().parents[2] / "tools" / "offline-compiler" / "compile_all_v2.ps1"
)


def _compiler_script() -> Path:
    """Path to the NinjaScript offline-compiler PowerShell script. Override with
    the NT8BRIDGE_COMPILER env var (the default assumes a monorepo layout)."""
    override = os.environ.get("NT8BRIDGE_COMPILER")
    return Path(override) if override else _DEFAULT_COMPILER


@dataclass
class CompileError:
    file: str
    line: int
    code: str
    message: str

    def to_dict(self) -> dict:
        return {"file": self.file, "line": self.line, "code": self.code, "message": self.message}


def parse_errors(output: str) -> list[CompileError]:
    errors: list[CompileError] = []
    for raw in output.splitlines():
        m = _ERR_RE.match(raw.strip())
        if m:
            errors.append(
                CompileError(
                    file=m.group("file").strip(),
                    line=int(m.group("line")),
                    code=m.group("code"),
                    message=m.group("message").strip(),
                )
            )
    return errors


def run_precheck(strategy_path) -> list[CompileError]:
    """Compile one .cs offline; return structured errors ([] == clean).

    The path is resolved to absolute before it reaches the compiler: a relative
    path resolves against the PowerShell script's own working directory, not the
    caller's, so the intended file silently wasn't compiled and precheck falsely
    reported "clean". Raises FileNotFoundError for a missing file rather than
    returning a false-clean result.

    Sets NT_OFFLINE_INCLUDE_CUSTOM=1 so the offline compiler references
    NinjaTrader.Custom.dll. Without it the base Strategy/Indicator types do
    not resolve and every NinjaScript file fails with CS0246.
    """
    path = Path(strategy_path).resolve()
    if not path.is_file():
        raise FileNotFoundError(f"Strategy file not found: {path}")
    compiler = _compiler_script()
    if not compiler.is_file():
        raise FileNotFoundError(
            f"Offline compiler not found: {compiler}. precheck needs a NinjaScript "
            "offline-compiler PowerShell script — set NT8BRIDGE_COMPILER to its path. "
            "(The in-NT8 'compile' command does not need it.)"
        )
    env = dict(os.environ, NT_OFFLINE_INCLUDE_CUSTOM="1")
    proc = subprocess.run(
        ["powershell", "-NoProfile", "-File", str(compiler), str(path)],
        capture_output=True,
        text=True,
        errors="replace",
        env=env,
    )
    return parse_errors(proc.stdout + "\n" + proc.stderr)
