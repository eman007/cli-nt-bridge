# The REAL FeedGuardFiles block from addon\AutoReconnect.cs writes events that NT8 Guard's REAL reader
# (guard\app\lib\Guard.Events.psm1, Read-GuardEvents) understands - the writer is C# inside NinjaTrader,
# the reader is PowerShell outside it, and nothing else holds the two to one format. Also: rotation at
# 5 MB-style limits, and no torn lines when several threads write at once (NinjaTrader's status thread,
# the market-data threads and the guardian all log).
. (Join-Path $PSScriptRoot 'FeedGuardTestKit.ps1')
$files = Get-AddOnBlock 'FeedGuardFiles'
$dir = Join-Path $env:TEMP ("fgevents_{0}" -f [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $dir -Force | Out-Null

$code = @"
using System; using System.Collections.Generic; using System.Globalization; using System.IO; using System.Text; using System.Threading;
$files
class T {
$script:CSharpHarness
    static int Main(string[] args) {
        string dir = args[0];
        string ev = Path.Combine(Path.Combine(dir, "NT8Guard"), "feedguard-events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(ev));
        DateTime t = new DateTime(2026, 10, 9, 7, 14, 13, DateTimeKind.Utc);
        // quotes, a backslash, a newline, a tab, an em dash and a control character - all must survive
        FeedGuardFiles.AppendLine(ev, FeedGuardFiles.EventLine(t, "feed.source-outage", "alert",
            "dead \"feed\" \\ at\nthe source \u2014 tab\there \u0001", new[] { "connection", "Demo \"Feed\"", "seconds", "412" }), FeedGuardFiles.MaxBytes);
        FeedGuardFiles.AppendLine(ev, FeedGuardFiles.EventLine(t.AddMinutes(1), "feed.strategies-disabled", "warn", "flap", null), FeedGuardFiles.MaxBytes);
        Check("two event lines written", File.ReadAllLines(ev).Length == 2, File.ReadAllLines(ev).Length.ToString());

        // rotation: a small limit, many lines -> one previous copy, the live file small again
        string log = Path.Combine(dir, "rot.log");
        for (int i = 0; i < 60; i++) FeedGuardFiles.AppendLine(log, "line " + i + " " + new string('x', 40), 1000);
        Check("rotation keeps one previous copy (.1)", File.Exists(log + ".1"), "no .1 file");
        Check("the live file restarted under the limit", new FileInfo(log).Length < 1100, new FileInfo(log).Length.ToString());
        Check("no second generation (.2) is made", !File.Exists(log + ".2"), ".2 exists");
        string[] tail = File.ReadAllLines(log);
        Check("the newest line is in the live file", tail.Length > 0 && tail[tail.Length - 1].StartsWith("line 59 "), tail.Length > 0 ? tail[tail.Length - 1] : "(empty)");

        // eight writers at once: every line lands whole
        string conc = Path.Combine(dir, "conc.jsonl");
        var threads = new List<Thread>();
        for (int k = 0; k < 8; k++) {
            int id = k;
            var th = new Thread(delegate () {
                for (int i = 0; i < 200; i++)
                    FeedGuardFiles.AppendLine(conc, FeedGuardFiles.EventLine(DateTime.UtcNow, "feed.alert", "alert", "writer " + id + " line " + i, null), FeedGuardFiles.MaxBytes);
            });
            threads.Add(th); th.Start();
        }
        foreach (Thread th in threads) th.Join();
        Check("8 writers x 200 lines: all 1600 landed", File.ReadAllLines(conc).Length == 1600, File.ReadAllLines(conc).Length.ToString());

        // another process reading the file for a moment (FileShare.Read, as PowerShell's ReadAllLines opens
        // it - the overwatch reads this file every minute): the line must still land
        string busy = Path.Combine(dir, "busy.jsonl");
        File.WriteAllText(busy, "first" + Environment.NewLine);
        var reader = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read);
        var closer = new Thread(delegate () { Thread.Sleep(20); reader.Dispose(); });
        closer.Start();
        FeedGuardFiles.AppendLine(busy, "second", FeedGuardFiles.MaxBytes);
        closer.Join();
        Check("a line written while a reader briefly holds the file still lands", File.ReadAllLines(busy).Length == 2, File.ReadAllLines(busy).Length + " line(s)");

        // at the size limit with a tailing reader open (ReadWrite share, no Delete): the file cannot be moved
        // aside, but the line must still be appended - rotating late is fine, losing every line is not
        string tailed = Path.Combine(dir, "tailed.log");
        File.WriteAllText(tailed, new string('x', 2000) + Environment.NewLine);
        using (var tailer = new FileStream(tailed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            FeedGuardFiles.AppendLine(tailed, "after", 1000);
        }
        string[] tl = File.ReadAllLines(tailed);
        Check("at the size limit with a tailing reader open, the line is still appended", tl.Length == 2 && tl[1] == "after", tl.Length + " line(s)");

        // never throws: a folder that does not exist
        FeedGuardFiles.AppendLine(Path.Combine(dir, @"no\such\folder\x.log"), "x", 100);
        Check("an unwritable path is swallowed, not thrown", true, "");
        return Done();
    }
}
"@
Invoke-CSharpTest 'events' $code -Arguments @($dir)
$fail = $script:CSharpExit
function Check2([string]$Label, $Cond, [string]$Detail = '') {
    if ($Cond) { "  PASS  $Label" } else { $script:fail = 1; "  FAIL  $Label -> $Detail" }
}

'=== what the C# wrote, read the way NT8 Guard reads it'
$ev = Join-Path $dir 'NT8Guard\feedguard-events.jsonl'
$lines = @(if (Test-Path $ev) { [IO.File]::ReadAllLines($ev, [Text.Encoding]::UTF8) })
$e = $null
try { $e = $lines[0] | ConvertFrom-Json } catch { }
Check2 'the line is JSON' ($null -ne $e) ($lines | Select-Object -First 1)
if ($e) {
    Check2 'utc round-trips' ($e.utc -eq '2026-10-09T07:14:13.0000000Z') $e.utc
    Check2 'component is feedguard' ($e.component -eq 'feedguard') $e.component
    Check2 'kind and severity' ($e.kind -eq 'feed.source-outage' -and $e.severity -eq 'alert') "$($e.kind) $($e.severity)"
    $want = "dead `"feed`" \ at`nthe source $([char]0x2014) tab`there $([char]1)"
    Check2 'text round-trips exactly (quotes, backslash, newline, tab, em dash, control char)' ($e.text -ceq $want) $e.text
    Check2 'data round-trips' ($e.data.connection -ceq 'Demo "Feed"' -and $e.data.seconds -eq '412') ($e.data | ConvertTo-Json -Compress)
}
$e2 = $null
try { $e2 = $lines[1] | ConvertFrom-Json } catch { }
Check2 'an event without data still parses (empty data object)' ($null -ne $e2 -and $e2.kind -eq 'feed.strategies-disabled') ($lines | Select-Object -Last 1)

Import-Module (Join-Path $PSScriptRoot '..\guard\app\lib\Guard.Events.psm1') -Force -DisableNameChecking
$gh = Join-Path $dir 'guardhome'
$got = @(Read-GuardEvents -GuardHome $gh -Nt8Dir $dir -Component feedguard -KindPattern '^feed\.source-outage$')
Check2 "NT8 Guard's Read-GuardEvents finds the feed guard's event" ($got.Count -eq 1) "found $($got.Count)"
$all = @(Read-GuardEvents -GuardHome $gh -Nt8Dir $dir -Component feedguard -Max 10)
Check2 '...newest first, both kinds' ($all.Count -eq 2 -and $all[0].kind -eq 'feed.strategies-disabled') (($all | ForEach-Object { $_.kind }) -join ',')
$bad = @([IO.File]::ReadAllLines((Join-Path $dir 'conc.jsonl')) | Where-Object { try { $null = $_ | ConvertFrom-Json; $false } catch { $true } })
Check2 'every concurrently written line parses' ($bad.Count -eq 0) "$($bad.Count) torn line(s)"

Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
''
if ($fail -eq 0) { 'RESULT: all checks passed'; exit 0 }
'RESULT: FAILED'; exit 1
