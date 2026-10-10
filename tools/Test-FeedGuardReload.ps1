# AutoReconnect.reload (spec section 11.5): NT8 Guard's control panel rewrites AutoReconnect.config.json
# and drops this signal; the feed guard re-reads its settings within one tick (10 s), without a
# NinjaTrader restart and without disturbing a recovery in progress.
#   - the REAL FeedGuardReload block: what a changed watch list needs, in every state
#   - the wiring around it in AutoReconnect.cs, by structure
# Also the "reconnect" switch (NT8 Guard's Watch only preset): a dropped link is alerted, not reconnected.
. (Join-Path $PSScriptRoot 'FeedGuardTestKit.ps1')
$reload = Get-AddOnBlock 'FeedGuardReload'

$code = @"
using System; using System.Collections.Generic;
$reload
class T {
$script:CSharpHarness
    static List<string> L(params string[] s) { return new List<string>(s); }
    static void P(string label, List<string> from, List<string> cfg, int subs, int rung, string want) {
        string got = FeedGuardReload.WatchPlan(from, cfg, subs, rung);
        Check(label + " -> " + want, got == want, got);
    }
    static int Main() {
        P("unchanged list", L("MNQ ##-##", "MES ##-##"), L("MNQ ##-##", "MES ##-##"), 2, 0, "same");
        P("unchanged apart from spaces", L("MNQ ##-##"), L("  MNQ ##-## "), 1, 0, "same");
        P("an instrument added", L("MNQ ##-##"), L("MNQ ##-##", "MES ##-##"), 1, 0, "restart");
        P("an instrument replaced", L("MNQ ##-##"), L("ES ##-##"), 1, 0, "restart");
        P("same names, new order (re-subscribe; cheap and unambiguous)", L("A", "B"), L("B", "A"), 2, 0, "restart");
        P("the list emptied", L("MNQ ##-##"), L(), 1, 0, "stop");
        P("watchdog was off, now a list", L(), L("MNQ ##-##"), 0, 0, "start");
        P("nothing could be subscribed before, list changed", L("XYZ ##-##"), L("MNQ ##-##"), 0, 0, "start");
        P("a recovery at rung 1 - wait", L("MNQ ##-##"), L("ES ##-##"), 1, 1, "wait");
        P("a recovery at rung 2 - wait", L("MNQ ##-##"), L("ES ##-##"), 1, 2, "wait");
        P("emptied during a recovery - wait too", L("MNQ ##-##"), L(), 1, 3, "wait");
        P("unchanged during a recovery - same", L("MNQ ##-##"), L("MNQ ##-##"), 1, 2, "same");
        return Done();
    }
}
"@
Invoke-CSharpTest 'reload' $code
$fail = $script:CSharpExit
$src = $script:AddOnSource
function Check2([string]$Label, $Cond, [string]$Detail = '') {
    if ($Cond) { "  PASS  $Label" } else { $script:fail = 1; "  FAIL  $Label -> $Detail" }
}
function Body([string]$Signature) {
    $i = $src.IndexOf($Signature)
    if ($i -lt 0) { return '' }
    $depth = 0; $started = $false
    for ($j = $i; $j -lt $src.Length; $j++) {
        if ($src[$j] -eq '{') { $depth++; $started = $true }
        elseif ($src[$j] -eq '}') { $depth--; if ($started -and $depth -eq 0) { return $src.Substring($i, $j - $i + 1) } }
    }
    return ''
}

'=== the signal'
$triggers = Body 'private void CheckManualTriggers('
Check2 'AutoReconnect.reload is read and deleted, then DoReload runs' ($triggers.Contains('"AutoReconnect.reload"') -and $triggers.Contains('DoReload()')) 'not wired'
$doReload = Body 'private void DoReload('
Check2 'DoReload re-reads the config' ($doReload.Contains('ReadConfig()')) 'no ReadConfig'
Check2 'DoReload re-resolves the market time zone' ($doReload.Contains('ResolveMarketTz()')) 'stale time zone'
Check2 'DoReload applies a new tick interval to the running timer' ($doReload.Contains('_timer.Change(')) 'interval ignored'
Check2 'DoReload reports itself as an event (feed.loaded, reason reload)' ($doReload.Contains('Emit("feed.loaded"') -and $doReload.Contains('"reload"')) 'not evented'
Check2 'DoReload applies a changed watch list' ($doReload.Contains('ApplyWatchListChange()')) 'watch list ignored'
Check2 'the guardian retries a waiting watch-list change every tick' ($src.Contains('ApplyWatchListChange();     // a reload changed the watch list')) 'never retried after a recovery'

'=== a reload never shows another thread a half-built list'
$read = Body 'private bool ReadConfig('
foreach ($f in '_allow', '_feedOwners', '_exclude', '_watchdogInstruments') {
    Check2 "ReadConfig never edits $f in place" (-not $read.Contains("$f.Add(")) 'in-place Add found'
    Check2 "ReadConfig swaps $f in whole" ($read -match [regex]::Escape($f) + ' = \w+;') 'no swap'
    Check2 "$f is not readonly (it must be replaceable)" ($src -notmatch ('readonly [^;]*' + [regex]::Escape($f) + ' =')) 'still readonly'
}
Check2 'a replaced exclude list still always excludes Playback' ($read.Contains('{ "Playback" }')) 'Playback lost on reload'

'=== the recovery is never stranded by a reload'
$tick = Body 'private void Tick('
Check2 'a reload that switches auto-recovery off mid-cycle still finishes the reconnect (|| _rung >= 2)' ($tick.Contains('if (_autoRecover || _rung >= 2)')) 'a disconnected connection could be left disconnected'

'=== a watch list that resolves to nothing turns the watchdog OFF loudly - never into a "dead feed" (final review)'
# A reload with "MNQ" instead of "MNQ ##-##": nothing subscribes. A kept heartbeat clock would then age for
# ever, lastL1AgeSeconds would read as a dead feed, and the overwatch would restart NinjaTrader over a typo.
$restart = Body 'private void RestartWatchdog('
$none = $restart.IndexOf('if (n == 0)')
$tail = $(if ($none -gt 0) { $restart.Substring($none) } else { '' })
Check2 'RestartWatchdog: nothing subscribed -> the heartbeat clock is cleared' ($tail.Contains('Interlocked.Exchange(ref _lastDataTicks, 0)')) 'clock kept - reads as a dead feed'
Check2 '...and an alert says the watchdog is off' ($tail.Contains('AlertRaise("watchlist-empty"')) 'silent'
$startW = Body 'private void StartWatchdog('
Check2 'StartWatchdog: nothing subscribed -> the same alert' ($startW.Contains('AlertRaise("watchlist-empty"')) 'silent'
Check2 'the alert clears once something subscribes again' ($restart.Contains('AlertClear("watchlist-empty"') -and $startW.Contains('AlertClear("watchlist-empty"')) 'never cleared'

'=== the reconnect switch'
$k = $tick.IndexOf('if (!_reconnect)')
$r = $tick.IndexOf('DoReconnect(name)')
Check2 '"reconnect": false is checked before any reconnect attempt' ($k -gt 0 -and $k -lt $r) "switch at $k, reconnect at $r"
Check2 '...and raises a dropped-connection alert instead' ($tick.Contains('AlertRaise("dropped:" + name')) 'no alert'
$switchBlock = ''
if ($k -gt 0) {   # the braces of the if (!_reconnect) block itself
    $open = $tick.IndexOf('{', $k); $depth = 0
    for ($j = $open; $j -lt $tick.Length; $j++) {
        if ($tick[$j] -eq '{') { $depth++ } elseif ($tick[$j] -eq '}') { $depth--; if ($depth -eq 0) { $switchBlock = $tick.Substring($open, $j - $open + 1); break } }
    }
}
Check2 '...and skips the reconnect (continue) - alert only, never alert AND reconnect' ($switchBlock.Contains('continue;')) 'falls through to DoReconnect'
Check2 'the alert clears when the connection is healthy again' ($tick.Contains('AlertClear("dropped:" + name, now)')) 'never cleared'
Check2 'ReadConfig reads "reconnect"' ($read.Contains('BoolCfg(txt, "reconnect", _reconnect)')) 'not read'

''
if ($fail -eq 0) { 'RESULT: all checks passed'; exit 0 }
'RESULT: FAILED'; exit 1
