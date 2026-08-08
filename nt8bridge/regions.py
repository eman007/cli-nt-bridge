"""regions — find and strip DUPLICATED NinjaScript generated regions.

THE HAZARD
----------
NinjaTrader appends a `#region NinjaScript generated code` block to an indicator/strategy source when
it regenerates wrappers. If the file is then edited OUTSIDE the NinjaScript Editor, NT appends
ANOTHER one on its next pass. They accumulate silently — 8 copies in one file has been observed in the
wild — and once two exist the tree stops compiling with a wall of CS0111 (duplicate member) and CS0102
errors that name the symbol, never the cause.

Because every `.cs` under `bin\\Custom` compiles into ONE assembly, a single afflicted file takes the
whole platform down: nothing you built appears anywhere, and the error text points at innocent code.

This is entirely mechanical to detect and fix, which is why it belongs in the tool rather than in a
human's memory of a rule.

⚠ ONE region is CORRECT — that is NT's own wrapper and the file needs it. Only the 2nd and later
copies are the defect. `--strip` removes ALL of them (the safe, idempotent choice: NT regenerates
exactly one on its next real compile). Strip before an external-edit compile, and let NT put its copy
back.

⚠ Only files NT generates wrappers for are ever affected — in practice `Indicators\\` and
`Strategies\\`. AddOns are not touched by the generator.
"""
from __future__ import annotations

import io
import os
import re
from pathlib import Path

MARKER = "#region NinjaScript generated code"
# ⚠ MATCH ANCHORED AT LINE START, NEVER `text.count(MARKER)`.
#
# A substring count treats any PROSE MENTION of the marker as a region — and the files most likely to
# mention it are the ones whose header comment explains this very rule. A healthy file with one real
# region plus one explanatory comment counts as 2, reads as "duplicated", and `--strip` then truncates
# from the COMMENT to EOF: reported upstream against a file where that deleted 4,838 bytes, 21% of it,
# including the legitimate region. Detection must therefore be anchored to a real preprocessor
# directive, which is only ever `^[ \t]*#region` — C# permits leading whitespace before `#`, nothing
# else. (A marker inside a block comment at line start would still fool this; a `.bak` is the backstop.)
_REGION_RE = re.compile(r"^[ \t]*#region NinjaScript generated code", re.MULTILINE)


def count_regions(text: str) -> int:
    """Real generated regions in `text` — anchored directives only, never prose mentions."""
    return len(_REGION_RE.findall(text))


def custom_root(nt8_root: Path) -> Path:
    return Path(nt8_root) / "bin" / "Custom"


def scan(root: Path, subdirs=("Indicators", "Strategies", "AddOns", "BarsTypes", "DrawingTools")) -> list[dict]:
    """Every .cs under `root`/<subdirs> that carries at least one generated region."""
    out = []
    for sub in subdirs:
        d = Path(root) / sub
        if not d.is_dir():
            continue
        for p in sorted(d.rglob("*.cs")):
            # _archive holds frozen versions NT no longer compiles; leave them alone.
            if "_archive" in p.parts:
                continue
            try:
                text = io.open(p, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            n = count_regions(text)
            if n:
                out.append({"path": str(p), "regions": n, "duplicated": n > 1})
    return out


def strip_file(path: Path, backup: bool = True) -> int:
    """Cut from the FIRST generated-region DIRECTIVE to end of file. Returns regions removed.

    Truncating at the first marker rather than surgically excising each block is deliberate: the
    region is always the tail, the blocks are separated only by namespace scaffolding that is itself
    generated, and a partial excision that leaves a stray brace turns a mechanical fix into a broken
    file. Whole-tail removal cannot half-work.

    ⚠ It CAN, however, remove the wrong tail if detection is wrong — which is why this writes
    `<path>.bak` before touching anything. A tool that truncates source needs an undo even when its
    matcher is correct, because "correct" here is a judgement about someone else's file.
    """
    text = io.open(path, encoding="utf-8", errors="replace").read()
    m = _REGION_RE.search(text)
    if m is None:
        return 0
    n = count_regions(text)
    # keep any indentation-only prefix off the final line
    line_start = text.rfind("\n", 0, m.start()) + 1
    kept = text[:line_start].rstrip() + "\n"
    if backup:
        io.open(str(path) + ".bak", "w", encoding="utf-8", newline="").write(text)
    io.open(path, "w", encoding="utf-8", newline="").write(kept)
    return n
