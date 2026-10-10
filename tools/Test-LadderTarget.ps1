# The REAL SelectLadderTargets from AutoReconnect.cs, compiled and run - which connection does the
# silent-stall recovery ladder cycle?
#
# WHY THIS EXISTS (2026-09-29, desktop): the ladder cycled the CONFIGURED feed owner, Broker-Demo.
# NinjaTrader was actually serving MNQ/MES from DataVendor, and DataVendor's feed is the one a machine sleep
# wedges. Seven full ladders disconnected and reconnected a HEALTHY Broker-Demo between 05:49 and
# 07:08 PT, each ending "feed likely down at the source" (false); DataVendor was never touched. The
# operator disconnected DataVendor at 07:16:28 and L1 resumed at 07:16:29. The ladder now asks NT8
# (Instrument.MarketDataConnection) which connection is the provider.
#
# The method is sliced out of the source verbatim (the Test-FrontMonth / Test-StallGate pattern), so
# this cannot pass against a stale copy of the logic.
$ErrorActionPreference = 'Stop'
$srcPath = Join-Path $PSScriptRoot '..\addon\AutoReconnect.cs'
$src = [IO.File]::ReadAllText($srcPath)

function Slice([string]$startMarker, [string]$endMarker) {
    $i = $src.IndexOf($startMarker); if ($i -lt 0) { throw "missing $startMarker" }
    $j = $src.IndexOf($endMarker, $i); if ($j -lt 0) { throw "missing $endMarker" }
    return $src.Substring($i, $j - $i)
}
$select = Slice 'internal static List<string> SelectLadderTargets' '        // Instrument.MarketDataConnection is INTERNAL'

$fail = 0
function Check($label, $cond, $detail) {
    if ($cond) { "  PASS  $label" } else { $script:fail++; "  FAIL  $label -> $detail" }
}

# ---- structural pin: the zero-candidate branch must NOT burn the cooldown -------------------------
"=== structural: a transient 'nothing healthy' must retry, not escalate ==="
$z0 = $src.IndexOf('if (conns.Count == 0)')
$z1 = $src.IndexOf('if (conns.Count > 1)', $z0)
Check 'the zero-candidate branch exists, separate from the multi-candidate refusal' (($z0 -ge 0) -and ($z1 -gt $z0)) "zero=$z0 multi=$z1"
if ($z0 -ge 0 -and $z1 -gt $z0) {
    $zb = $src.Substring($z0, $z1 - $z0)
    Check 'it does NOT call EscalateToAlert (that is the 10-minute cooldown)' ($zb -notmatch 'EscalateToAlert') 'EscalateToAlert found in the zero branch'
    Check 'it drops back to rung 0 so the next tick restarts the ladder' ($zb -match '_rung = 0') 'no _rung = 0'
}

# ---- behavioural: the real SelectLadderTargets ----------------------------------------------------
$dir = Join-Path $env:TEMP 'laddertarget'; New-Item -ItemType Directory -Force $dir | Out-Null
$cs = @"
using System; using System.Collections.Generic; using System.Linq;
class H {
    $select
    static int fail = 0;
    static void T(string label, string[] providers, Dictionary<string,string> health, string[] owners,
                  string[] allow, string[] want, string wantSource) {
        string source;
        var got = SelectLadderTargets(providers, health, new HashSet<string>(owners), new HashSet<string>(allow),
                                      n => n == "Playback", out source);
        bool ok = got.SequenceEqual(want) && source == wantSource;
        if (ok) Console.WriteLine("  PASS  " + label);
        else { fail++; Console.WriteLine("  FAIL  " + label + " -> got [" + string.Join(",", got) + "] via " + source
                                         + ", wanted [" + string.Join(",", want) + "] via " + wantSource); }
    }
    static Dictionary<string,string> Hl(params string[] kv) {
        var d = new Dictionary<string,string>();
        for (int i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i+1];
        return d;
    }
    static int Main() {
        var none = new string[0];
        // THE 09-29 MORNING: both connections healthy after the reconnect, NT8 serving the data from DataVendor,
        // config pointing at Broker-Demo. Old behaviour cycled Broker-Demo seven times for nothing.
        T("09-29: NT8 says DataVendor serves MNQ/MES -> cycle DATAVENDOR, not the configured Broker-Demo",
          new[]{"DataVendor"}, Hl("Broker-Demo","healthy","DataVendor","healthy"), new[]{"Broker-Demo"}, none,
          new[]{"DataVendor"}, "provider");
        // The first seconds after a wake: the provider is still flapping. Must NOT fall back to the
        // config target - cycling the wrong connection is precisely the bug.
        T("provider still flapping after wake -> NO candidate (never the config fallback)",
          new[]{"DataVendor"}, Hl("Broker-Demo","healthy","DataVendor","lost"), new[]{"Broker-Demo"}, none,
          none, "provider");
        // NT8 cannot answer (a future version renames the internal property) -> previous behaviour.
        T("provider unknown -> config fallback (the pre-fix behaviour, unchanged)",
          none, Hl("Broker-Demo","healthy","DataVendor","healthy"), new[]{"Broker-Demo"}, none,
          new[]{"Broker-Demo"}, "config");
        // The VPS: provider and config agree; unrelated healthy prop connections must not add candidates.
        T("VPS shape: provider == config target, prop connections ignored",
          new[]{"Broker Demo"}, Hl("Broker Demo","healthy","Data Vendor","healthy","PropFirm","healthy"),
          new[]{"Broker Demo"}, none, new[]{"Broker Demo"}, "provider");
        T("an excluded provider (Playback) is never cycled",
          new[]{"Playback"}, Hl("Playback","healthy"), none, none, none, "provider");
        T("the watch allow-list still applies to the provider",
          new[]{"DataVendor"}, Hl("DataVendor","healthy"), none, new[]{"Broker-Demo"}, none, "provider");
        T("two distinct healthy providers are both reported (the ladder then refuses to mass-disconnect)",
          new[]{"DataVendor","Broker-Demo"}, Hl("DataVendor","healthy","Broker-Demo","healthy"), none, none,
          new[]{"DataVendor","Broker-Demo"}, "provider");
        T("the same provider for MNQ and MES is one candidate, not two",
          new[]{"DataVendor","DataVendor"}, Hl("DataVendor","healthy"), none, none, new[]{"DataVendor"}, "provider");
        T("a provider missing from the health map is not a candidate",
          new[]{"Ghost"}, Hl("DataVendor","healthy"), none, none, none, "provider");
        Console.WriteLine(fail == 0 ? "RESULT: all checks passed" : "RESULT: " + fail + " FAILED");
        return fail == 0 ? 0 : 1;
    }
}
"@
$csPath = Join-Path $dir 'LT.cs'
[IO.File]::WriteAllText($csPath, $cs, [Text.UTF8Encoding]::new($false))
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $dir 'LT.exe'
& $csc /nologo /nowarn:1701,1702 "/out:$exe" $csPath | Where-Object { $_ -match 'error' } | ForEach-Object { "  $_" }
if (-not (Test-Path $exe)) { throw 'compile FAILED' }
& $exe
$rc = $LASTEXITCODE
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
if ($fail) { "RESULT (structural): $fail FAILED"; exit 1 }
exit $rc
