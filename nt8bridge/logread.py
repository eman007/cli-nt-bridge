"""Read a log file from inside NinjaTrader, filtered at the source.

WHY THIS EXISTS
    The project's own verification rule ends at "read the live truth" — the logs. Every other step
    of that loop is one command (`compile`, `ntstatus`, `workspace`), and the last step was an ad-hoc
    scp or a remote shell per box.

    2026-08-02, a cold unattended boot logged
        [Sentinel:Fault] SentinelBinds.AttachExisting — the calling thread cannot access this object
    into a file nobody read for two days. A fault that only fires when nobody is watching is the
    worst kind to leave logged and unread, and the reason it stayed unread is that reading it cost
    an interactive session per machine.

WHY THE FILTER RUNS ON THE FAR SIDE
    The remote transport encodes UTF-16LE then base64, so the usable payload is about 12 KB against
    log files of tens of megabytes. Grep-at-the-client is not slow here, it is impossible. The AddOn
    matches inside NinjaTrader and only matches come back.

    It also opens the file with FileShare.ReadWrite | FileShare.Delete, because NT holds its own logs
    open — the live file is precisely the one a naive reader cannot touch.

⭐ ABSENCE IS NOT EVIDENCE
    A path that does not exist is an ERROR, never `ok` with zero matches. "No faults" and "that log
    lives elsewhere on this box" must never render alike. Same for a pattern that fails to compile:
    it fails loudly rather than degrading to match-all or match-none, one of which reads all-clear.

NOT SENTINEL-AWARE
    The caller supplies the path. This keeps the AddOn free of any Sentinel reference, which is a
    standing rule for that file. Project-specific presets belong in the project, not in this repo.

JSON contract (the in-NT8 AddOn honors):
  request : {"id": str, "kind": "log", "file": str, "grep": str, "ignoreCase": "true"|"false",
             "sinceMin": str, "tail": str, "maxBytes": str, "maxLineChars": str}
  response: {"id","status","ts","file","exists","sizeBytes","modifiedUtc",
             "scannedLines","matched","returned","tail","truncatedFromStart","windowStartByte",
             "lineNumbersFrom":"file"|"window",
             "timeFilter":{"sinceMin","applied","stampedLines","note"?},
             "lines":[{"n":int,"text":str}], "errors":[...]}
"""
from __future__ import annotations

from dataclasses import dataclass, field

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


@dataclass
class LogResult:
    ok: bool
    file: str | None = None
    matched: int = 0
    returned: int = 0
    scanned: int = 0
    truncated: bool = False
    time_filter_applied: bool = False
    note: str = ""
    lines: list[dict] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)

    def texts(self) -> list[str]:
        return [ln.get("text", "") for ln in self.lines]


def build_log_request(request_id: str, file: str, grep: str | None = None,
                      since_min: float = 0, tail: int = 200, ignore_case: bool = False,
                      max_bytes: int = 8 * 1024 * 1024, max_line_chars: int = 2000) -> dict:
    return {
        "id": request_id,
        "kind": "log",
        "file": file,
        "grep": grep or "",
        "ignoreCase": "true" if ignore_case else "false",
        "sinceMin": str(since_min),
        "tail": str(tail),
        "maxBytes": str(max_bytes),
        "maxLineChars": str(max_line_chars),
    }


def parse_log_response(payload: dict) -> LogResult:
    tf = payload.get("timeFilter") or {}
    return LogResult(
        ok=payload.get("status") == "ok",
        file=payload.get("file"),
        matched=payload.get("matched", 0),
        returned=payload.get("returned", 0),
        scanned=payload.get("scannedLines", 0),
        truncated=bool(payload.get("truncatedFromStart")),
        time_filter_applied=bool(tf.get("applied")),
        note=tf.get("note", "") or "",
        lines=payload.get("lines") or [],
        errors=payload.get("errors") or [],
    )


def run_log(file: str, grep: str | None = None, since_min: float = 0, tail: int = 200,
            ignore_case: bool = False, max_bytes: int = 8 * 1024 * 1024,
            max_line_chars: int = 2000, timeout: float = 60.0) -> dict:
    """Timeout is generous: a multi-megabyte scan inside NT is real work, and a timeout here would
    be indistinguishable from a wedged AddOn."""
    trigger, result = ntio.ensure_bridge_dirs()
    rid = new_request_id()
    ntio.atomic_write_json(
        trigger / f"log_{rid}.json",
        build_log_request(rid, file, grep, since_min, tail, ignore_case, max_bytes, max_line_chars),
    )
    return ntio.poll_for_json(result / f"log_{rid}.json", timeout=timeout)
