# The REAL ShouldFlagStall + IsScheduledClosed from AutoReconnect.cs, compiled and run.
#
# Both are sliced out of the source verbatim (the Test-FrontMonth pattern), so this cannot pass
# against a stale copy of the logic.
#
# WHY THIS EXISTS: IsMarketLikelyOpen is a DATA-RECENCY proxy - "data arrived in the last 30 min".
# Past that window it answers false forever, so a feed that is ALREADY long dead could never be
# flagged and the recovery ladder could never start. The desktop slept 2026-09-23 06:17:29Z ->
# 10:15:32Z (Windows Update wake timer); on resume the staleness was already 4 hours, and the
# watchdog then logged "alive; last L1 update 25089s ago" once an hour for 7.8 hours with the market
# open and every chart frozen. The session gate answers "is the market open" from the CLOCK and has
# no such blind spot.
$ErrorActionPreference = 'Stop'
$src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\addon\AutoReconnect.cs'))

function Slice([string]$startMarker, [string]$endMarker) {
    $i = $src.IndexOf($startMarker); if ($i -lt 0) { throw "missing $startMarker" }
    $j = $src.IndexOf($endMarker, $i); if ($j -lt 0) { throw "missing $endMarker" }
    return $src.Substring($i, $j - $i)
}
$gate      = Slice 'internal static bool ShouldFlagStall' '        // FEED-RECENCY proxy'
$scheduled = Slice 'private bool IsScheduledClosed'       '        private DateTime ToEastern'
$calendar  = Slice '    // ===== BEGIN FeedGuardCalendar =====' '    // ===== END FeedGuardCalendar ====='
$simfeed   = Slice 'internal static bool IsSimulatedFeedOnly' '        // The provider KIND'
if ($scheduled -notmatch 'DayOfWeek.Saturday') { throw 'IsScheduledClosed slice did not capture the body' }
# The stall path must refresh the Simulated-Data-Feed answer BEFORE the calendar gate reads it (final review I4).
$tick = $src.Substring($src.IndexOf('bool scheduledClosed = _useSessionGate') - 600, 700)
$onsetRefresh = ($tick -match 'RefreshSimFeedOnly\(\)') -and $tick.IndexOf('RefreshSimFeedOnly()') -lt $tick.IndexOf('bool scheduledClosed')

$dir = Join-Path $env:TEMP 'stallgate'; New-Item -ItemType Directory -Force $dir | Out-Null
$cs = @"
using System; using System.Collections.Generic; using System.Globalization;
class H {
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // the session gate's configured minutes, as they ship
    int _weekCloseMin = 17 * 60, _weekOpenMin = 18 * 60, _breakStartMin = 17 * 60, _breakEndMin = 18 * 60;
    List<FeedGuardClosure> _extraClosed = new List<FeedGuardClosure>();
    bool _simFeedOnly = false;   // every watched instrument served by the Simulated Data Feed
    static void LogSafe(string s) { }
    DateTime ToEastern(DateTime utc) {
        return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
    }
    $gate
    $scheduled
    $simfeed
    static int Main() {
        var h = new H(); int fail = 0;
        Action<string,bool,double,bool,bool,bool> T = (label, want, stale, thr, recency, clock) => {
            bool got = ShouldFlagStall(stale, 75.0, recency, clock);
            if (got == want) Console.WriteLine("  PASS  " + label);
            else { fail++; Console.WriteLine("  FAIL  " + label + " -> got " + got + ", wanted " + want); }
        };
        // ---- the regression that cost 7.8 hours ----
        // 25089s stale, recency window long gone, but the clock says the market is open.
        T("long-dead feed, market open BY CLOCK => stall (the 09-23 case)", true, 25089, true, false, true);
        // ---- the behaviour that must not change ----
        T("fresh feed => never a stall", false, 5, true, true, true);
        T("just past the threshold, data was recent => stall", true, 76, true, true, false);
        T("at the threshold exactly => not yet", false, 75, true, true, true);
        T("long-dead feed, market CLOSED by clock, no recency => quiet", false, 25089, true, false, false);
        T("long-dead feed but data recency still true => stall", true, 3600, true, true, false);
        T("session gate OFF (openByClock false) falls back to recency alone", false, 25089, true, false, false);

        // ---- IsScheduledClosed itself, so "openByClock" means what the test assumes ----
        Func<string,bool> C = s => h.IsScheduledClosed(DateTime.SpecifyKind(
            DateTime.ParseExact(s, "yyyy-MM-dd HH:mm", Inv), DateTimeKind.Utc));
        Action<string,string,bool> S = (label, utc, want) => {
            bool got = C(utc);
            if (got == want) Console.WriteLine("  PASS  " + label);
            else { fail++; Console.WriteLine("  FAIL  " + label + " (" + utc + " UTC) -> closed=" + got + ", wanted " + want); }
        };
        // 2026-09-23 06:17 UTC = Wed 02:17 ET -> Globex open. This is the minute the desktop's feed died.
        S("Wed 02:17 ET (the moment the feed died) is OPEN", "2026-09-23 06:17", false);
        // 2026-09-23 14:11 UTC = Wed 10:11 ET -> RTH, open
        S("Wed 10:11 ET is OPEN", "2026-09-23 14:11", false);
        // 21:30 UTC = 17:30 ET -> inside the daily break
        S("Wed 17:30 ET daily break is CLOSED", "2026-09-23 21:30", true);
        // Saturday
        S("Saturday is CLOSED", "2026-09-26 12:00", true);
        // Sunday 21:00 UTC = 17:00 ET, before the 18:00 ET weekly open
        S("Sunday 17:00 ET (before the open) is CLOSED", "2026-09-27 21:00", true);
        // Sunday 23:00 UTC = 19:00 ET, after the weekly open
        S("Sunday 19:00 ET (after the open) is OPEN", "2026-09-27 23:00", false);
        // ---- holidays come from the FeedGuardCalendar block, wired in by IsScheduledClosed ----
        // 2026-11-26 19:00 UTC = Thu 14:00 ET, Thanksgiving: CME equity closes early at 13:00 ET
        S("Thanksgiving 14:00 ET is CLOSED", "2026-11-26 19:00", true);
        // 2026-01-01 00:00 UTC = Wed Dec 31 19:00 ET: shut until New Year's Day 18:00 ET
        S("New Year's Eve 19:00 ET is CLOSED", "2026-01-01 00:00", true);
        // 2026-11-25 19:00 UTC = Wed 14:00 ET, the day before Thanksgiving: an ordinary session
        S("the day before Thanksgiving is OPEN", "2026-11-25 19:00", false);

        // ---- the Simulated Data Feed ticks around the clock: the calendar does not apply to it ----
        Action<string,bool,string[]> F = (label, want, kinds) => {
            bool got = IsSimulatedFeedOnly(kinds == null ? null : new List<string>(kinds));
            if (got == want) Console.WriteLine("  PASS  " + label);
            else { fail++; Console.WriteLine("  FAIL  " + label + " -> got " + got + ", wanted " + want); }
        };
        F("every watched instrument on the Simulated Data Feed", true, new[] { "Simulator", "Simulator" });
        F("one on a real broker keeps the calendar", false, new[] { "Simulator", "Rithmic" });
        F("Playback is not the Simulated Data Feed", false, new[] { "Playback" });
        F("no answer keeps the calendar", false, new string[0]);
        F("a null answer keeps the calendar", false, null);
        h._simFeedOnly = true;
        S("Saturday on the Simulated Data Feed is OPEN", "2026-09-26 12:00", false);
        S("a daily break on the Simulated Data Feed is OPEN", "2026-09-23 21:30", false);
        h._simFeedOnly = false;
        S("Saturday on a broker's feed is still CLOSED", "2026-09-26 12:00", true);

        // ---- final review I4: the answer holds still while a recovery runs ----
        Action<string,bool,bool,int,string[]> N = (label, want, cur, rung, kinds) => {
            bool got = NextSimFeedOnly(cur, rung, new List<string>(kinds));
            if (got == want) Console.WriteLine("  PASS  " + label);
            else { fail++; Console.WriteLine("  FAIL  " + label + " -> got " + got + ", wanted " + want); }
        };
        N("the drill's own disconnect (no provider at rung 2) does not switch the calendar back on", true, true, 2, new[] { "" });
        N("nor does a recovery on a broker switch it off", false, false, 1, new[] { "Simulator" });
        N("between recoveries a switch to a broker turns the calendar on", false, true, 0, new[] { "Rithmic" });
        N("between recoveries the Simulated Data Feed turns it off", true, false, 0, new[] { "Simulator" });
        if ($(if ($onsetRefresh) { 'true' } else { 'false' })) Console.WriteLine("  PASS  a stall re-reads the feed's provider before the calendar decides");
        else { fail++; Console.WriteLine("  FAIL  a stall re-reads the feed's provider before the calendar decides"); }

        Console.WriteLine(fail == 0 ? "RESULT: all checks passed" : "RESULT: " + fail + " FAILED");
        return fail == 0 ? 0 : 1;
    }
}
$calendar
"@
$csPath = Join-Path $dir 'StallGate.cs'
[IO.File]::WriteAllText($csPath, $cs, [Text.UTF8Encoding]::new($false))

# The .NET Framework compiler, same as Test-FrontMonth. A bare Roslyn csc.dll references nothing
# implicitly (every System type comes back CS0518), and the AddOn targets Framework 4.8 anyway.
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
$exe = Join-Path $dir 'StallGate.exe'
& $csc /nologo /nowarn:1701,1702 /out:$exe $csPath | Where-Object { $_ -match 'error' } | ForEach-Object { "  $_" }
if (-not (Test-Path $exe)) { throw 'compile FAILED' }
& $exe
$rc = $LASTEXITCODE
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
exit $rc
