# The REAL MatchStop + StopCandidate + StopIndex from NT8BridgeServer.cs, compiled and run: which stop
# level does `backtest` report for each trade (the "stopPrice" field)?
#
# WHY THIS EXISTS (2026-10-18): the stops were keyed by calendar DAY - written for one-trade-a-day event
# strategies. On a day with more trades every trade got that day's first stop, a stop-market ENTRY counted
# as a stop, and the day's stop even overrode the price of the stop a trade actually exited on. Reported
# by a user of the bridge.
#
# ! Order.Time means two things. With history (Order.OrderUpdates - live and sim accounts) it is the LAST
#   change of state, a gone stop's cancel or fill, and the first update is its placing. A Strategy
#   Analyzer order has NO history and its Order.Time stays its placing time: a YM backtest of 226 trades
#   (2026-10-19, a strategy with a 40-tick stop) proved it - read as a cancel time, all 44 wrongly-stopped target
#   exits had taken the next same-side trade's stop. Both models are covered below.
#
# The code is sliced out of the source verbatim (the Test-LadderTarget pattern), so this cannot pass
# against a stale copy of the logic.
$ErrorActionPreference = 'Stop'
$src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\addon\NT8BridgeServer.cs'))

function Slice([string]$startMarker, [string]$endMarker) {
    $i = $src.IndexOf($startMarker); if ($i -lt 0) { throw "missing $startMarker" }
    $j = $src.IndexOf($endMarker, $i); if ($j -lt 0) { throw "missing $endMarker" }
    return $src.Substring($i, $j - $i)
}
$types = Slice 'internal struct StopCandidate' '        // Every protective-stop candidate'
$match = Slice 'internal static double MatchStop' '        private static string Num('

$fail = 0
function Check($label, $cond, $detail) {
    if ($cond) { "  PASS  $label" } else { $script:fail++; "  FAIL  $label -> $detail" }
}

"=== structural ==="
Check 'no BuildStopByDate / stopByDate left' ($src -notmatch 'BuildStopByDate|stopByDate') 'still referenced'
Check 'BuildBacktestJson resolves each trade through BuildStopIndex + ResolveStop' ($src -match 'stops = BuildStopIndex\(perf\);' -and $src -match 'double stp = ResolveStop\(t, stops\);') 'not wired'
Check 'ResolveStop hands every trade to MatchStop' ($src -match 'return MatchStop\(stops, t\.Entry\.Time, t\.Exit\.Time, isLong') 'not wired'
Check 'with history Placed = first update and Ended = Order.Time; without, Placed = Order.Time' ($src -match 'c\.Placed = c\.HasEnd \? first : o\.Time;' -and $src -match 'c\.Ended = c\.HasEnd \? o\.Time : DateTime\.MinValue;' -and $src -match 'IList<OrderEventArgs> ups = o\.OrderUpdates;') 'not read that way'
Check 'a reversal''s "Close position" order is neither a candidate nor a stop exit' (([regex]::Matches($src, '"Close position"')).Count -ge 2) 'not filtered'
Check 'the output says how many stop orders carried their history' ($src -match '\\"stopOrders\\":\{\\"count\\":' -and $src -match 'withHistory') 'not reported'

"=== behavioural: the real MatchStop ==="
$dir = Join-Path $env:TEMP 'backteststops'; New-Item -ItemType Directory -Force $dir | Out-Null
$cs = @"
using System; using System.Collections.Generic;
class H {
    $types
    $match
    static int fail = 0;
    static DateTime D = new DateTime(2026, 10, 16, 0, 0, 0);
    static DateTime M(int minute) { return D.AddMinutes(minute); }
    // a stop order: minute placed; minute ended (cancel/fill) WITH history, or -1 = no history (a backtest);
    // exit side?, protects a long?, from which entry signal, OCO, price, order-history position
    static StopCandidate S(int placed, int ended, bool exitSide, bool forLong, string from, string oco, double price, int seq) {
        StopCandidate c = new StopCandidate();
        c.Placed = M(placed); c.HasEnd = ended >= 0; c.Ended = ended >= 0 ? M(ended) : DateTime.MinValue;
        c.ExitSide = exitSide; c.ForLong = forLong; c.FromEntry = from; c.Oco = oco; c.StopPrice = price; c.Seq = seq;
        return c;
    }
    static StopIndex I(params StopCandidate[] cs) { return StopIndex.Build(new List<StopCandidate>(cs)); }
    static void T(string label, double got, double want) {
        bool ok = (double.IsNaN(want) && double.IsNaN(got)) || got == want;
        if (ok) Console.WriteLine("  PASS  " + label);
        else { fail++; Console.WriteLine("  FAIL  " + label + " -> got " + got + ", wanted " + want); }
    }
    static double X(StopIndex ix, int entry, int exit, bool isLong, string sig) { return XO(ix, entry, exit, isLong, sig, ""); }
    static double XO(StopIndex ix, int entry, int exit, bool isLong, string sig, string oco) {
        return MatchStop(ix, M(entry), M(exit), isLong, sig, false, double.NaN, oco);
    }
    static int Main() {
        Console.WriteLine("--- without history (a Strategy Analyzer backtest: Order.Time = when it was placed)");
        // THE YM RUN: two same-side trades, target exits, each stop paired with its target.
        StopIndex ym = I(S(600, -1, true, true, "Long", "oco-A", 100.0, 1), S(700, -1, true, true, "Long", "oco-B", 110.0, 2));
        T("a target exit: its own OCO partner, not the next trade's stop", XO(ym, 600, 660, true, "Long", "oco-A"), 100.0);
        T("the next trade: ITS partner", XO(ym, 700, 760, true, "Long", "oco-B"), 110.0);
        T("a target exit with no OCO: the stop placed with its entry", X(ym, 600, 660, true, "Long"), 100.0);
        T("an exit on session close: the same", X(ym, 700, 755, true, "Long"), 110.0);
        T("a stop exit reports the price it exited on", MatchStop(ym, M(700), M(730), true, "Long", true, 110.25, ""), 110.25);
        T("no stop of its own: the next trade's is not borrowed", X(I(S(700, -1, true, true, "Long", "", 110.0, 1)), 600, 660, true, "Long"), double.NaN);
        T("no stop of its own: one placed on its exit bar by the next trade is not it", X(I(S(660, -1, true, true, "Long", "", 97.0, 1)), 600, 660, true, "Long"), double.NaN);
        StopIndex rentry = I(S(600, -1, true, true, "Long", "", 95.0, 1), S(660, -1, true, true, "Long", "", 97.0, 2));
        T("re-entered on the exit bar: the old trade keeps its stop", X(rentry, 600, 660, true, "Long"), 95.0);
        T("re-entered on the exit bar: the new trade gets ITS stop", X(rentry, 660, 720, true, "Long"), 97.0);
        StopIndex sides = I(S(600, -1, true, false, "S", "", 105.0, 1), S(600, -1, true, true, "S", "", 95.0, 2));
        T("a short gets the BuyToCover stop, not a long's Sell stop", X(sides, 600, 650, false, "S"), 105.0);
        StopIndex brk = I(S(601, -1, true, false, "Short", "", 105.0, 1), S(650, -1, false, false, "", "", 99.0, 2));
        T("a stop-market entry is never a stop", X(brk, 600, 700, false, "Short"), 105.0);
        StopIndex same = I(S(600, -1, true, true, "Long", "", 95.0, 1), S(610, -1, true, true, "Long", "", 96.5, 2));
        T("scaled in, one signal: the first entry keeps the stop placed at its entry", X(same, 600, 700, true, "Long"), 95.0);
        T("scaled in, one signal: the second entry its own", X(same, 610, 700, true, "Long"), 96.5);
        StopIndex legs = I(S(600, -1, true, true, "Long", "", 95.0, 1));
        T("partial exits: the first leg gets the shared stop", X(legs, 600, 700, true, "Long"), 95.0);
        T("partial exits: the last leg too", X(legs, 600, 800, true, "Long"), 95.0);
        T("a stop placed during the trade, not at its entry: still its stop", X(I(S(605, -1, true, true, "Long", "", 95.0, 1)), 600, 700, true, "Long"), 95.0);
        StopIndex replaced = I(S(600, -1, true, true, "Long", "oco-1", 95.0, 1), S(630, -1, true, true, "Long", "oco-2", 99.0, 2));
        T("a target exit after its stop was replaced: the target's own partner (OCO), not the first stop", XO(replaced, 600, 700, true, "Long", "oco-2"), 99.0);
        T("cancel-and-replace cannot be told apart without history: the one placed at the entry (documented)", X(I(S(600, -1, true, true, "Long", "", 95.0, 1), S(630, -1, true, true, "Long", "", 99.0, 2)), 600, 700, true, "Long"), 95.0);
        T("a PREVIOUS trade's stop is not it", X(I(S(500, -1, true, true, "Long", "", 93.0, 1)), 600, 700, true, "Long"), double.NaN);
        T("a stop placed before the fill is not guessed at", X(I(S(599, -1, true, true, "Long", "", 95.0, 1)), 600, 700, true, "Long"), double.NaN);

        Console.WriteLine("--- with history (live / sim: Order.Time = its last change, the first update = when placed)");
        StopIndex day = I(S(600, 660, true, true, "Long", "", 100.0, 1), S(720, 780, true, true, "Long", "", 110.0, 2));
        T("two trades in a day: the first gets its own stop", X(day, 600, 660, true, "Long"), 100.0);
        T("two trades in a day: the second gets ITS stop, not the day's first", X(day, 720, 780, true, "Long"), 110.0);
        T("a stop exit with no price falls back to the matching", MatchStop(day, M(600), M(660), true, "Long", true, double.NaN, ""), 100.0);
        T("a cancel stamped a bar after the exit still matches", X(I(S(600, 661, true, true, "Long", "", 95.0, 1)), 600, 660, true, "Long"), 95.0);
        StopIndex hbrk = I(S(650, 700, false, false, "", "", 99.0, 1), S(600, 701, true, false, "Short", "", 105.0, 2));
        T("a stop-market entry is never a stop", X(hbrk, 600, 700, false, "Short"), 105.0);
        StopIndex hsides = I(S(600, 650, true, true, "S", "", 95.0, 1), S(600, 652, true, false, "S", "", 105.0, 2));
        T("a short gets the BuyToCover stop, not a long's Sell stop", X(hsides, 600, 650, false, "S"), 105.0);
        StopIndex rev = I(S(600, 660, true, true, "Long", "", 95.0, 1), S(660, 720, true, false, "Short", "", 104.0, 2));
        T("a reversal: the long keeps its own stop", X(rev, 600, 660, true, "Long"), 95.0);
        T("a reversal: the short gets the one placed on the reversal bar", X(rev, 660, 720, false, "Short"), 104.0);
        StopIndex again = I(S(600, 660, true, true, "Long", "", 95.0, 1), S(660, 720, true, true, "Long", "", 97.0, 2));
        T("re-entered on the exit bar: the old trade keeps its stop", X(again, 600, 660, true, "Long"), 95.0);
        T("re-entered on the exit bar: the new trade gets ITS stop, not the dead one", X(again, 660, 720, true, "Long"), 97.0);
        StopIndex oco = I(S(600, 700, true, true, "Long", "oco-B", 96.0, 1), S(600, 700, true, true, "Long", "oco-A", 95.0, 2));
        T("a target exit: its OCO partner", XO(oco, 600, 700, true, "Long", "oco-A"), 95.0);
        T("a full tie without an OCO: the order history decides (undefined - pinned only to notice a change)", X(oco, 600, 700, true, "Long"), 96.0);
        StopIndex hsame = I(S(610, 700, true, true, "Long", "", 96.5, 1), S(600, 700, true, true, "Long", "", 95.0, 2));
        T("scaled in, one signal: the first entry gets the stop placed with it", X(hsame, 600, 700, true, "Long"), 95.0);
        T("scaled in, one signal: the second entry gets the stop placed with it", X(hsame, 610, 700, true, "Long"), 96.5);
        StopIndex scale = I(S(600, 700, true, true, "Entry1", "", 95.0, 1), S(610, 700, true, true, "Entry2", "", 96.5, 2));
        T("scaled in, own signals: Entry2's trade gets Entry2's stop", X(scale, 610, 700, true, "Entry2"), 96.5);
        StopIndex moved = I(S(600, 630, true, true, "Long", "", 95.0, 1), S(630, 700, true, true, "Long", "", 99.0, 2));
        T("a stop moved by cancel-and-replace: the one alive at the exit", X(moved, 600, 700, true, "Long"), 99.0);
        StopIndex hlegs = I(S(600, 800, true, true, "Long", "", 95.0, 1));
        T("partial exits: the first leg gets the shared stop", X(hlegs, 600, 700, true, "Long"), 95.0);
        T("partial exits: the last leg too", X(hlegs, 600, 800, true, "Long"), 95.0);
        T("a stop placed before the entry filled still matches", X(I(S(599, 660, true, true, "Long", "", 95.0, 1)), 600, 660, true, "Long"), 95.0);
        T("its stop cancelled just before the exit: still reported", X(I(S(600, 690, true, true, "Long", "", 95.0, 1)), 600, 700, true, "Long"), 95.0);
        T("a PREVIOUS trade's stop, cancelled before this one began, is not it", X(I(S(500, 590, true, true, "Long", "", 93.0, 1)), 600, 700, true, "Long"), double.NaN);
        T("no stop of its own; a later trade's stop is not borrowed", X(I(S(950, 1000, true, true, "Long", "", 97.0, 1)), 800, 900, true, "Long"), double.NaN);
        T("no stop of its own; the stop placed on its exit bar by the next trade is not it", X(I(S(660, 720, true, true, "Long", "", 97.0, 1)), 600, 660, true, "Long"), double.NaN);
        T("a trade entered and left on one bar still finds the stop placed then", X(I(S(660, 660, true, true, "Long", "", 97.0, 1)), 660, 660, true, "Long"), 97.0);
        T("a stop of the other side only: NaN", X(I(S(600, 650, true, false, "Short", "", 105.0, 1)), 600, 650, true, "Long"), double.NaN);
        T("no candidates at all: NaN", X(I(), 600, 650, true, "Long"), double.NaN);
        T("no signal on the order: matched by side and time", X(I(S(600, 650, true, true, "", "", 95.0, 1)), 600, 650, true, "Long"), 95.0);
        return fail == 0 ? 0 : 1;
    }
}
"@
$csPath = Join-Path $dir 'BS.cs'
[IO.File]::WriteAllText($csPath, $cs)
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $dir 'BS.exe'
Remove-Item $exe -ErrorAction SilentlyContinue
& $csc /nologo /nowarn:1701,1702 "/out:$exe" $csPath | Where-Object { $_ -match 'error' } | ForEach-Object { "  $_" }
if (-not (Test-Path $exe)) { "RESULT: compile FAILED"; exit 1 }
& $exe
$rc = $LASTEXITCODE

"=== the whole NT8 link add-on still compiles against this machine's NinjaTrader (C# 5) ==="
# The matching reads Order.FromEntrySignal / Order.Oco / Order.OrderUpdates: a member missing from a
# NinjaTrader version would break the compile of every NinjaScript file there. The add-on is ONE partial
# class in two files.
. (Join-Path $PSScriptRoot 'FeedGuardTestKit.ps1')
$bin = Get-NinjaTraderBin
if (-not $bin) { '  SKIP  NinjaTrader 8 is not installed here - nothing to compile against' }
else {
    $fw = Split-Path $csc -Parent
    $refs = @((Join-Path $bin 'NinjaTrader.Core.dll'), (Join-Path $bin 'NinjaTrader.Gui.dll'),
              "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll",
              "$fw\WPF\UIAutomationProvider.dll", "$fw\WPF\UIAutomationTypes.dll", "$fw\System.Xaml.dll",
              "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xml.dll", "$fw\System.Windows.Forms.dll",
              "$fw\System.Data.dll", "$fw\System.Drawing.dll")
    $out = Join-Path $env:TEMP ("brcompile_{0}.dll" -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    $msgs = & $csc /nologo /t:library /nowarn:1701,1702 "/out:$out" @($refs | ForEach-Object { "/r:$_" }) `
                (Join-Path $PSScriptRoot '..\addon\NT8BridgeServer.cs') (Join-Path $PSScriptRoot '..\addon\NT8BridgeServerPlayback.cs') 2>&1
    $built = Test-Path $out; Remove-Item $out -ErrorAction SilentlyContinue
    $errs = @($msgs | Where-Object { "$_" -match ': error ' })
    $errs | Select-Object -First 8 | ForEach-Object { "  $_" }
    $v = (Get-Item (Join-Path $bin 'NinjaTrader.exe')).VersionInfo.FileVersion
    Check "NT8BridgeServer.cs + NT8BridgeServerPlayback.cs compile against NinjaTrader $v" ($built -and -not $errs.Count) "$($errs.Count) error(s)"
}
if ($fail -or $rc) { "RESULT: FAILED"; exit 1 }
"RESULT: all checks passed"
exit 0
