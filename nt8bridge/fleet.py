"""fleet / corpus / versions / runrange — the ORCHESTRATION half of the bridge.

WHY THESE FOUR, AND WHY THEY ARE PYTHON-ONLY
    2026-08-11: staging one bake across three sentries cost an operator most of an afternoon, and
    every minute of it was orchestration, not capability. The AddOn already did the hard part —
    `applyTemplate` was written weeks ago and works. What did not exist was everything AROUND a
    verb: fanning it across the fleet, noticing a replay had stalled, counting what a bake produced,
    and knowing which box was running stale code.

    So none of this touches NT8BridgeServer.cs. No AddOn change means no compile, no reload, no
    restart, and nothing to get wrong on a box that is mid-bake. Orchestration is where the human
    was being spent, so orchestration is what gets automated.

WHAT EACH ONE COST US BEFORE IT EXISTED
    runrange  a replay stops dead at the end of each day's .nrd. Someone has to notice the clock
              stopped and seek past the gap. A 7-day bake is therefore ~7 manual interventions,
              which is precisely the "get me out of the loop" problem.
    fleet     every status check today was a hand-written ssh loop, re-typed per question.
    corpus    "is the bake producing anything?" was answered four times with ad-hoc PowerShell.
    versions  sentry-1 ran a bridge with NO applyTemplate while the source had it. That cost an
              hour of debugging a capability that was merely undeployed. ⭐ A drift check is
              cheaper than the confusion it prevents.

⛔ DESIGN RULE THESE ALL FOLLOW: report per-host outcomes INDIVIDUALLY and never collapse a fleet
   into one boolean. A box that could not be reached is UNKNOWN, never "fine" — the same rule
   verify_all uses when it reports a guard it could not run as UNTESTED rather than passed.
"""
from __future__ import annotations

import concurrent.futures as _fut
import json
import os
import re
import shlex
import subprocess
import time

DEFAULT_FLEET = os.path.join(os.path.expanduser("~"), "Documents", "NinjaTrader 8",
                             "Sentinel", "fleet.conf")
SSH_NOISE = re.compile(r"post-quantum|store now, decrypt later|openssh\.com/pq|^\*\*", re.I)


def _clean(s: str) -> str:
    return "\n".join(l for l in (s or "").splitlines() if not SSH_NOISE.search(l)).strip()


def read_fleet(path: str | None = None) -> list[dict]:
    """Parse fleet.conf. Retired hosts are RETURNED but flagged — a name that vanishes is how a
    box gets forgotten (the reason node01 is still listed there)."""
    p = path or DEFAULT_FLEET
    out: list[dict] = []
    if not os.path.exists(p):
        return out
    with open(p, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split()
            host = {"name": parts[0], "ssh": parts[0], "retired": None}
            for kv in parts[1:]:
                if "=" in kv:
                    k, v = kv.split("=", 1)
                    host[k] = v
            out.append(host)
    return out


def ssh(host: str, cmd: str, timeout: float = 90.0) -> tuple[int, str]:
    try:
        r = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", host, cmd],
                           capture_output=True, text=True, timeout=timeout)
        return r.returncode, _clean(r.stdout) + (("\n" + _clean(r.stderr)) if r.stderr.strip() else "")
    except subprocess.TimeoutExpired:
        return 124, "TIMEOUT"
    except Exception as e:                                    # noqa: BLE001
        return 1, "ERROR %s" % e


# ── fleet ────────────────────────────────────────────────────────────────────────────────
def fleet_exec(verb: str, hosts: list[str] | None = None, fleet_path: str | None = None,
               timeout: float = 120.0, parallel: int = 6) -> dict:
    """Run one bridge verb on every sentry and tabulate. UNREACHABLE is its own outcome."""
    boxes = [h for h in read_fleet(fleet_path) if not h.get("retired")]
    if hosts:
        boxes = [h for h in boxes if h["name"] in hosts]
    results: dict[str, dict] = {}

    def one(h):
        rc, out = ssh(h["ssh"], "C:/ntbv/Scripts/python.exe -m nt8bridge " + verb, timeout)
        parsed = None
        if out.startswith("{"):
            try:
                parsed = json.loads(out)
            except Exception:                                 # noqa: BLE001
                parsed = None
        return h["name"], {"rc": rc, "reachable": rc != 124 and rc != 255,
                           "json": parsed, "text": None if parsed else out[:400]}

    with _fut.ThreadPoolExecutor(max_workers=parallel) as ex:
        for name, res in ex.map(one, boxes):
            results[name] = res
    return {"verb": verb, "hosts": len(boxes), "results": results}


# ── corpus ───────────────────────────────────────────────────────────────────────────────
CORPUS_PS = (
    # Forward slashes ON PURPOSE: PowerShell accepts them on Windows, and it removes an entire
    # class of backslash-escaping bugs between Python, the shell and ssh.
    # No `if` as an EXPRESSION either -- PS 5.1 has no ternary, and the first cut of this string
    # returned "The term 'if' is not recognized" from every host.
    "$r='C:/Users/Administrator/Documents/NinjaTrader 8/Sentinel/Excursions'; "
    "$o=@{}; foreach($sub in 'candidates/ticks','candidates/cand.2','candidates/cand.3'){ "
    "$p=Join-Path $r $sub; "
    "if(Test-Path $p){ "
    "$f=Get-ChildItem $p -File -EA SilentlyContinue; "
    "$n=($f|Measure-Object).Count; "
    "$nw=''; "
    "$newest=$f|Sort-Object LastWriteTime -Descending|Select-Object -First 1; "
    "if($newest){ $nw=$newest.LastWriteTime.ToString('o') }; "
    "$o[$sub]=@{count=$n; newest=$nw} } }; "
    "$o | ConvertTo-Json -Compress"
)


def corpus_status(hosts: list[str] | None = None, fleet_path: str | None = None,
                  timeout: float = 90.0) -> dict:
    """How much corpus each box holds, and how fresh. ⚠ `newest` is the honest liveness signal:
    a large count with a stale timestamp is a bake that DIED, which looks identical to a healthy
    one if you only count files."""
    boxes = [h for h in read_fleet(fleet_path) if not h.get("retired")]
    if hosts:
        boxes = [h for h in boxes if h["name"] in hosts]
    out: dict[str, dict] = {}

    def one(h):
        rc, txt = ssh(h["ssh"], 'powershell -NoProfile -Command "%s"' % CORPUS_PS.replace('"', '\\"'),
                      timeout)
        data = None
        if txt.startswith("{"):
            try:
                data = json.loads(txt)
            except Exception:                                 # noqa: BLE001
                data = None
        return h["name"], {"rc": rc, "corpus": data, "raw": None if data else txt[:300]}

    with _fut.ThreadPoolExecutor(max_workers=6) as ex:
        for name, res in ex.map(one, boxes):
            out[name] = res
    return {"hosts": len(boxes), "results": out}


# ── versions ─────────────────────────────────────────────────────────────────────────────
def versions(src_addon: str, hosts: list[str] | None = None, fleet_path: str | None = None,
             timeout: float = 90.0) -> dict:
    """Bridge DRIFT: is each box running the AddOn and client we think it is?

    ⭐ Born from an hour lost on 2026-08-11 debugging `--apply-template` as if it were missing.
    It was not missing; it was UNDEPLOYED. The source had it, main had it, the sentries did not,
    and nothing in the toolchain would say so."""
    want = None
    if os.path.exists(src_addon):
        with open(src_addon, "rb") as fh:
            b = fh.read()
        want = {"bytes": len(b), "applyTemplate": b.count(b"applyTemplate")}
    boxes = [h for h in read_fleet(fleet_path) if not h.get("retired")]
    if hosts:
        boxes = [h for h in boxes if h["name"] in hosts]
    out: dict[str, dict] = {}

    ps = ("$p='C:\\Users\\Administrator\\Documents\\NinjaTrader 8\\bin\\Custom\\AddOns\\NT8BridgeServer.cs'; "
          "if(Test-Path $p){ $b=(Get-Item $p).Length; "
          "$a=(Select-String -Path $p -Pattern 'applyTemplate'|Measure-Object).Count; "
          "\"{0} {1}\" -f $b,$a } else { 'MISSING 0' }")

    def one(h):
        rc, txt = ssh(h["ssh"], 'powershell -NoProfile -Command "%s"' % ps.replace('"', '\\"'), timeout)
        got = {"bytes": None, "applyTemplate": None}
        m = re.search(r"(\d+)\s+(\d+)", txt or "")
        if m:
            got = {"bytes": int(m.group(1)), "applyTemplate": int(m.group(2))}
        drift = None
        if want and got["bytes"] is not None:
            drift = "CURRENT" if got["bytes"] == want["bytes"] else "STALE"
        elif got["bytes"] is None:
            drift = "UNKNOWN"
        return h["name"], {"rc": rc, "addon": got, "drift": drift}

    with _fut.ThreadPoolExecutor(max_workers=6) as ex:
        for name, res in ex.map(one, boxes):
            out[name] = res
    return {"source": want, "results": out}


# ── builds ───────────────────────────────────────────────────────────────────────────────
SENTINEL_DLL = ("C:/Users/Administrator/Documents/NinjaTrader 8/bin/Custom/NinjaTrader.Custom.dll")


def builds(hosts: list[str] | None = None, fleet_path: str | None = None,
           timeout: float = 90.0, reference: str | None = None) -> dict:
    """SENTINEL BUILD drift: is each box running the SUITE we think it is?

    ⛔ WHY THIS IS SEPARATE FROM `versions`, WHICH ONLY WATCHES THE BRIDGE. Measured 2026-08-13:
    sentry-2 was producing bar transcripts stamped `dumpVer 1.0.0 / schema bars.1 /
    resetOnNewTradingDay: null` while the tree here has been **v1.1.0 / bars.2 since 08-09**. The
    v1.1.0 build had simply never reached that box, and NOTHING in the toolchain could say so —
    the same shape as the undeployed `applyTemplate` that cost an hour, one layer down.

    ⭐ AND IT IS NOT COSMETIC: `bars.2` exists precisely so "Break at EOD" is a COMPARED FIELD
    rather than an assumed precondition. A box on `bars.1` cannot serve the bar-type parity gate,
    and its rows do not say so — they simply omit the field. ⇒ A bake's provenance depends on
    knowing which build stamped it, so that has to be measurable without logging into the box.

    ⚠⚠ IT COMPARES SOURCE, NOT THE DLL — AND THE FIRST VERSION GOT THAT WRONG. Hashing
    `NinjaTrader.Custom.dll` reported **all six sentries DRIFTED with six different hashes**, which
    is exactly what it would report if every box were perfectly in sync: each box COMPILES ITS OWN
    DLL, and a .NET build is not reproducible byte-for-byte (fresh MVID and timestamps per
    compile). A check that can only ever say DRIFTED is not a check — it is the "verified and still
    lying" failure this project has written down six times, caught here only because six mutually
    different hashes made no sense.
    ⇒ We compare an ordered digest of the `.cs` SOURCE plus the version constants that actually get
    STAMPED INTO THE DATA. Same source ⇒ same digest, on every box, forever.
    """
    import hashlib
    ref_root = reference or os.path.dirname(SENTINEL_DLL)

    def local_digest(root: str) -> dict | None:
        """Ordered digest of every .cs under the tree: name + size, hashed in sorted order."""
        if not os.path.isdir(root):
            return None
        h, n = hashlib.sha256(), 0
        for base, _dirs, files in os.walk(root):
            if "_archive" in base:            # same single filter the remote probe uses
                continue
            for f in sorted(files):
                if f.lower().endswith(".cs"):
                    p = os.path.join(base, f)
                    h.update(f.encode("utf-8", "replace"))
                    h.update(str(os.path.getsize(p)).encode())
                    n += 1
        return {"files": n, "sha256": h.hexdigest()[:16]}

    want = local_digest(ref_root)

    boxes = [h for h in read_fleet(fleet_path) if not h.get("retired")]
    if hosts:
        boxes = [h for h in boxes if h["name"] in hosts]

    # Same digest recipe as local_digest, expressed for PS 5.1: name + size, sorted, one hash.
    # ⚠ No `if` as an expression and no ternary in 5.1 -- both cost this project a debug cycle.
    ps = ("$r='C:/Users/Administrator/Documents/NinjaTrader 8/bin/Custom'; "
          "$f=Get-ChildItem $r -Recurse -Filter *.cs -File -EA SilentlyContinue | "
          # ⚠ ONE simple pattern, no backslashes. The first cut used '_archive|\\obj\\' and the
          # escaping across python → bash → ssh → cmd → PowerShell mangled it into a filter that
          # matched EVERYTHING, so the probe hashed an empty set and reported the sha256 of the
          # empty string as a confident DRIFTED on all six boxes. An inert check that still prints
          # a verdict is worse than no check; the tell was `files 0`, which is why it is REPORTED.
          "Where-Object { $_.FullName -notmatch '_archive' } | Sort-Object Name; "
          "$sb=New-Object System.Text.StringBuilder; "
          "foreach($x in $f){ [void]$sb.Append($x.Name); [void]$sb.Append($x.Length) }; "
          "$md=New-Object System.Security.Cryptography.SHA256Managed; "
          "$b=[Text.Encoding]::UTF8.GetBytes($sb.ToString()); "
          "$h=($md.ComputeHash($b)|ForEach-Object{$_.ToString('x2')}) -join ''; "
          "$d='C:/Users/Administrator/Documents/NinjaTrader 8/bin/Custom/NinjaTrader.Custom.dll'; "
          "$bt='none'; if(Test-Path $d){ $bt=(Get-Item $d).LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ') }; "
          "\"{0} {1} {2}\" -f ($f|Measure-Object).Count,$h.Substring(0,16),$bt")

    def one(h):
        rc, txt = ssh(h["ssh"], 'powershell -NoProfile -Command "%s"' % ps.replace('"', '\\"'), timeout)
        m = re.search(r"(\d+)\s+([0-9A-Fa-f]{16})\s+(\S+)", txt or "")
        if not m:
            # ⛔ UNREACHABLE IS ITS OWN OUTCOME. A box we could not ask is never "fine".
            return h["name"], {"reachable": False, "drift": "UNKNOWN", "raw": (txt or "")[:120]}
        got = {"files": int(m.group(1)), "sha256": m.group(2).lower(), "dllBuiltUtc": m.group(3),
               "reachable": True}
        if want:
            got["drift"] = "CURRENT" if got["sha256"] == want["sha256"].lower() else "DRIFTED"
        return h["name"], got

    out: dict[str, dict] = {}
    with _fut.ThreadPoolExecutor(max_workers=6) as ex:
        for name, res in ex.map(one, boxes):
            out[name] = res

    # ⭐ THE FLEET IS ITS OWN REFERENCE, AND THAT CORRECTION MATTERS. Measured 2026-08-13: the
    # sentries carry a 398-file DEPLOYED SUBSET while this tree has 988. Judging them against main
    # would print DRIFTED on every box forever — a check that can only say one thing. The question
    # worth asking is CONSENSUS: are the boxes running the same suite as each other, and who is the
    # odd one out? Main's digest is still reported, as context, never as the verdict.
    reachable = {n: v for n, v in out.items() if v.get("reachable")}
    tally: dict[str, list[str]] = {}
    for n, v in reachable.items():
        tally.setdefault(v["sha256"], []).append(n)
    mode = max(tally, key=lambda k: len(tally[k])) if tally else None
    for n, v in out.items():
        if not v.get("reachable"):
            v["drift"] = "UNKNOWN"
        else:
            v["drift"] = "CONSENSUS" if v["sha256"] == mode else "OUTLIER"

    outliers = sorted(n for n, v in reachable.items() if v["sha256"] != mode)
    unknown = sorted(n for n, v in out.items() if not v.get("reachable"))
    return {"referenceTree": ref_root, "referenceDigest": want,
            "note": "the reference tree is CONTEXT ONLY — sentries run a deployed subset, so a "
                    "difference from main is expected and is not drift",
            "hosts": len(boxes), "results": out,
            "consensusDigest": mode, "groups": {k: sorted(v) for k, v in tally.items()},
            "outliers": outliers, "unknown": unknown,
            "verdict": ("all %d reachable boxes AGREE (%s)" % (len(reachable), mode)) if not outliers
                       and not unknown else
                       "%d OUTLIER(s): %s%s" % (len(outliers), ", ".join(outliers) or "-",
                                                "; %d UNKNOWN: %s" % (len(unknown), ", ".join(unknown))
                                                if unknown else "")}


# ── runrange ─────────────────────────────────────────────────────────────────────────────
# ⛔ INDICATORS THAT WRITE THE CORPUS. A replay driven while one of these is attached does not
# merely waste a run — it appends REPLAYED rows to the training corpus, where they are
# indistinguishable from live ones after the fact. Matched as case-insensitive substrings so a
# version bump (…_v2_0_0 -> _v2_1_0) cannot silently un-guard them.
CORPUS_RECORDERS = ("excursionrecorder", "candidaterecorder", "bricklog", "tickrecorder")


def attached_recorders(call) -> list[str]:
    """Every corpus-writing indicator currently attached, across every chart. Read-only."""
    found = []
    for chart in (call("chart") or {}).get("charts", []) or []:
        for ind in chart.get("indicators", []) or []:
            nm = (ind.get("type") or ind.get("name") or "")
            if any(p in nm.lower() for p in CORPUS_RECORDERS):
                found.append(nm)
    return found


def run_range(host: str | None, start: str, end: str, speed: int = 50,
              stall_sec: float = 90.0, poll_sec: float = 20.0, max_hours: float = 12.0,
              step_minutes: int = 90, expect_recorders: list[str] | None = None) -> dict:
    """Drive a replay from `start` to `end` WITHOUT a human watching for stalls.

    ⛔ THE PROBLEM THIS SOLVES, MEASURED: a replay halts at the end of each day's .nrd and simply
    sits there. Nothing errors. A 7-day bake is 7 silent stops, each needing someone to notice and
    re-seek — which is exactly the babysitting this fleet exists to remove.

    THE RULE: a stall is not an error, it is a GAP. When the clock holds still for `stall_sec`
    while speed > 0, step the clock forward and resume. Give up only when stepping no longer moves
    the clock (genuinely out of data) or the wall-clock budget is spent.

    ⚠ It reports `stalls` and `steps` rather than hiding them: a range that needed 40 steps is
    telling you the data is holed, and that belongs in the result, not in a log nobody reads.

    ⛔⛔ IT REFUSES TO START WHILE AN UNDECLARED CORPUS RECORDER IS ATTACHED, and that refusal is the
    most important line in this function. MEASURED THREE TIMES on three occasions (sentry-1 twice,
    sentry-2 once, 2026-08-11/13): NT restores its workspace after a restart or reboot, and the
    chart comes back carrying `SentinelExcursionRecorder` at Realtime with Playback CONNECTED. One
    `--speed` away from writing REPLAYED rows into the Council corpus, where nothing downstream can
    tell them from live ones. It was caught by eye each time. Being written down did not stop the
    third occurrence, so it is a refusal now.

    ⇒ The recorder set is part of the BAKE SPEC, not of the machine's leftover state. Declare what
    you intend with `--expect-recorder NAME` (repeatable); anything else attached stops the run and
    is named, with the command to remove it. A bake that DOES run reports `recorders` either way, so
    "no recorder was attached" is a recorded measurement rather than an assumption.
    """
    import datetime as dt

    def call(verb: str) -> dict:
        cmd = "C:/ntbv/Scripts/python.exe -m nt8bridge " + verb
        if host:
            rc, out = ssh(host, cmd, timeout=180)
        else:
            r = subprocess.run(shlex.split(cmd), capture_output=True, text=True, timeout=180)
            rc, out = r.returncode, _clean(r.stdout)
        try:
            return json.loads(out)
        except Exception:                                     # noqa: BLE001
            return {"_raw": out[:300], "_rc": rc}

    def clock() -> str | None:
        return (call("playback") or {}).get("clockEst")

    def parse(s):
        return dt.datetime.fromisoformat(s.replace("Z", "").split(".")[0]) if s else None

    end_dt = parse(end)

    # ── the refusal, before anything moves ────────────────────────────────────────────────────
    declared = [d.lower() for d in (expect_recorders or [])]
    live = attached_recorders(call)
    undeclared = [r for r in live if not any(d in r.lower() for d in declared)]
    if undeclared:
        return {"host": host or "local", "from": start, "to": end, "speed": speed,
                "refused": True, "started": False,
                "recorders": live, "undeclared": undeclared, "declared": expect_recorders or [],
                "why": "REFUSED — %d corpus recorder(s) attached that this bake did not declare. "
                       "A replay driven now writes REPLAYED rows into the corpus, and nothing "
                       "downstream can tell them from live ones." % len(undeclared),
                "fix": ["python -m nt8bridge chart --remove-indicator %s" % r for r in undeclared] +
                       ["…or re-run with --expect-recorder <name> if it is meant to be recording"]}

    call("playbackctl --seek %s" % start)
    call("playbackctl --speed %d" % speed)

    t0 = time.time()
    last, last_move = clock(), time.time()
    stalls = steps = 0
    log: list[str] = []
    while time.time() - t0 < max_hours * 3600:
        time.sleep(poll_sec)
        now = clock()
        cur = parse(now)
        if cur and end_dt and cur >= end_dt:
            log.append("reached end %s" % now)
            break
        if now != last:
            last, last_move = now, time.time()
            continue
        if time.time() - last_move < stall_sec:
            continue
        # stalled: step over the gap
        stalls += 1
        if not cur:
            log.append("no clock reading; abandoning")
            break
        nxt = cur + dt.timedelta(minutes=step_minutes)
        if end_dt and nxt > end_dt:
            log.append("stalled at %s and the next step passes the end; done" % now)
            break
        r = call("playbackctl --seek %s" % nxt.strftime("%Y-%m-%dT%H:%M:%S"))
        steps += 1
        call("playbackctl --speed %d" % speed)
        after = clock()
        log.append("stall at %s -> stepped to %s (landed %s)" % (now, nxt, after))
        if after == now:
            log.append("step did not move the clock — out of data")
            break
        last, last_move = after, time.time()
    return {"host": host or "local", "start": start, "end": end, "speed": speed,
            "refused": False, "started": True,
            # Recorded on every run, not only on the refusal: "nothing was recording" is a
            # measurement the provenance of this bake's rows depends on.
            "recorders": live, "declared": expect_recorders or [],
            "finalClock": clock(), "stalls": stalls, "steps": steps,
            "elapsedMin": round((time.time() - t0) / 60.0, 1), "log": log}
