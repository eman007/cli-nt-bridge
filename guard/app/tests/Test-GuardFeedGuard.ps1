# guard.json -> the feed guard's AutoReconnect.config.json (Guard.FeedGuard.psm1), then read back by
# the add-on's OWN config parser, cut out of addon\AutoReconnect.cs and compiled - the writer is
# PowerShell, the reader is a deliberately tiny C# parser inside NinjaTrader, and only this test holds
# the two together.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes feedcfg
$cfgPath = Join-Path $h.Nt8 'AutoReconnect.config.json'
$reload = Join-Path $h.Nt8 'AutoReconnect.reload'
function Read-Cfg { return ([IO.File]::ReadAllText($cfgPath) | ConvertFrom-Json) }
function New-Cfg([string]$Preset = 'recover') { $c = Get-GuardDefaults; $c.protection.preset = $Preset; return $c }

'=== Recover NT8 (the default preset)'
$r = Write-FeedGuardConfig -Config (New-Cfg)
$j = Read-Cfg
Check 'written in NinjaTrader''s folder' ($r.Path -eq $cfgPath -and (Test-Path $cfgPath)) $r.Path
Check 'the watch list (ES/NQ ##-##), stale threshold and market time zone' ((@($j.watchdogInstruments) -join ',') -eq 'ES ##-##,NQ ##-##' -and $j.watchdogStaleSeconds -eq 75 -and $j.marketTimeZoneId -eq 'Eastern Standard Time') ($j | ConvertTo-Json -Compress)
Check 'reconnect on, silent-feed recovery on, re-enable OFF (alert only)' ($j.reconnect -eq $true -and $j.autoRecover -eq $true -and $j.reEnableAfterRecover -eq $false) ($j | ConvertTo-Json -Compress)
Check 'the session gate is always on' ($j.useSessionGate -eq $true) "$($j.useSessionGate)"
Check 'no alert channel set up -> no webhook' ($j.webhookUrl -eq '' -and $j.webhookFormat -eq 'json') "$($j.webhookUrl)/$($j.webhookFormat)"
Check 'the reload signal is dropped' ($r.Reloaded -and (Test-Path $reload)) "reloaded=$($r.Reloaded)"
Check 'no temp file left behind' (-not (Test-Path "$cfgPath.tmp")) 'tmp left'

'=== unchanged settings: nothing written, no reload'
Remove-Item $reload -Force
$r = Write-FeedGuardConfig -Config (New-Cfg)
Check 'Changed = false' (-not $r.Changed) "changed=$($r.Changed)"
Check 'no reload signal' (-not (Test-Path $reload)) 'reload dropped for nothing'

'=== Watch only and Hands-off'
$r = Write-FeedGuardConfig -Config (New-Cfg 'watch') -NoReload
$j = Read-Cfg
Check 'Watch only: no reconnect, no recovery, no re-enable' ($j.reconnect -eq $false -and $j.autoRecover -eq $false -and $j.reEnableAfterRecover -eq $false) ($j | ConvertTo-Json -Compress)
Check '-NoReload drops no signal' (-not (Test-Path $reload) -and -not $r.Reloaded) 'signal dropped'
$null = Write-FeedGuardConfig -Config (New-Cfg 'handsoff') -NoReload
$j = Read-Cfg
Check 'Hands-off: re-enable on' ($j.reconnect -eq $true -and $j.autoRecover -eq $true -and $j.reEnableAfterRecover -eq $true) ($j | ConvertTo-Json -Compress)
$c = New-Cfg; $c.protection.reconnect = $false
$null = Write-FeedGuardConfig -Config $c -NoReload
Check 'a single toggle override (custom) is honoured' ((Read-Cfg).reconnect -eq $false) 'override ignored'

'=== alerts: the phone channel goes to the add-on, and its address is never printed'
$c = New-Cfg; $c.alerts.ntfyTopic = 'guard-test-topic-7Qx'
$out = (Write-FeedGuardConfig -Config $c -NoReload *>&1 | Out-String)
$j = Read-Cfg
Check 'ntfy topic -> webhookUrl = server/topic, plain text' ($j.webhookUrl -eq 'https://ntfy.sh/guard-test-topic-7Qx' -and $j.webhookFormat -eq 'text') "$($j.webhookFormat)"
Check 'the topic is not in anything returned or printed' ($out -notmatch 'guard-test-topic-7Qx') 'topic leaked'
$c = New-Cfg; $c.alerts.webhookUrl = 'https://hooks.example.com/abc?x=1&y=2'
$null = Write-FeedGuardConfig -Config $c -NoReload
$j = Read-Cfg
Check 'a generic webhook goes over as JSON, & intact' ($j.webhookUrl -eq 'https://hooks.example.com/abc?x=1&y=2' -and $j.webhookFormat -eq 'json') $j.webhookUrl

'=== your own tuning in the file is kept; dead keys are dropped'
[IO.File]::WriteAllText($cfgPath, '{"graceSeconds": 30, "connections": ["My Broker"], "feedOwnerConnections": ["Feed A"], "canaryInstrument": "M2K ##-##", "watchdogInstrument": "MNQ 09-26", "watchdogInstruments": ["MNQ 09-26"], "autoRecover": false, "nested": {"a": 1}}')
$c = New-Cfg; $c.watch.extraDates = @('2026-03-10', '2026-03-11 12:30')
$r = Write-FeedGuardConfig -Config $c -NoReload
$j = Read-Cfg
Check 'graceSeconds, connections and feedOwnerConnections kept' ($j.graceSeconds -eq 30 -and (@($j.connections) -join ',') -eq 'My Broker' -and (@($j.feedOwnerConnections) -join ',') -eq 'Feed A') ($j | ConvertTo-Json -Compress)
Check 'Kept names them' ((((@($r.Kept) | Sort-Object) -join ',')) -eq 'connections,feedOwnerConnections,graceSeconds') ($r.Kept -join ',')
Check 'Guard''s keys win over the old file' ((@($j.watchdogInstruments) -join ',') -eq 'ES ##-##,NQ ##-##' -and $j.autoRecover -eq $true) ($j | ConvertTo-Json -Compress)
Check 'canaryInstrument and the old single watchdogInstrument are dropped' ($null -eq $j.canaryInstrument -and $null -eq $j.watchdogInstrument) ($j | ConvertTo-Json -Compress)
Check 'nested objects (nothing the add-on reads) are not carried' ($null -eq $j.nested) 'carried'
Check 'extra dates go to "holidays"' ((@($j.holidays) -join ',') -eq '2026-03-10,2026-03-11 12:30') (@($j.holidays) -join ',')
$text = [IO.File]::ReadAllText($cfgPath)
Check 'Guard''s keys come first (a kept value can never shadow one for the add-on''s parser)' ($text.IndexOf('"webhookFormat"') -lt $text.IndexOf('"graceSeconds"')) 'order'

'=== text the add-on cannot read is refused, and nothing is written'
$before = [IO.File]::ReadAllText($cfgPath)
$c = New-Cfg; $c.watch.instruments = @('ES "##-##"')
$threw = $false
try { $null = Write-FeedGuardConfig -Config $c -NoReload } catch { $threw = $true }
Check 'a quote in a value throws' $threw 'accepted'
Check '...and the file is untouched' ([IO.File]::ReadAllText($cfgPath) -ceq $before) 'file changed'
$c = New-Cfg; $c.watch.marketTimeZone = 'Eastern\Standard'
$threw = $false
try { $null = Write-FeedGuardConfig -Config $c -NoReload } catch { $threw = $true }
Check 'a backslash in a value throws' $threw 'accepted'

'=== a corrupt existing file: Guard''s settings still land'
[IO.File]::WriteAllText($cfgPath, '{ this is not json')
$r = Write-FeedGuardConfig -Config (New-Cfg) -NoReload
Check 'written anyway, with a note' ((Read-Cfg).reconnect -eq $true -and $r.Note -match 'could not be read') $r.Note
# The add-on's own parser is looser than ConvertFrom-Json (a trailing comma, a raw C:\path), so a file
# Guard cannot read may still hold the user's connections / exclude / feedOwnerConnections: keep a copy.
Check 'the unreadable file is kept as AutoReconnect.config.json.bak' ((Test-Path "$cfgPath.bak") -and [IO.File]::ReadAllText("$cfgPath.bak") -ceq '{ this is not json') 'no backup'
Check '...and the note says so' ($r.Note -match 'AutoReconnect\.config\.json\.bak') $r.Note

'=== a kept value with a backslash is carried over exactly as the add-on read it (final review)'
# Valid JSON with an escaped path: the add-on reads the raw text between the quotes, C:\\Sounds\\x.wav,
# and Windows takes the doubled separators. Guard must write that same raw text back - not refuse to save.
[IO.File]::WriteAllText($cfgPath, '{"alertSound": "C:\\Sounds\\x.wav", "connections": ["My Broker"]}')
$threw = $null
try { $null = Write-FeedGuardConfig -Config (New-Cfg) -NoReload } catch { $threw = $_.Exception.Message }
Check 'no throw for a backslash in a key Guard does not own' ($null -eq $threw) "$threw"
$written = [IO.File]::ReadAllText($cfgPath)
Check 'the file is rewritten with Guard''s keys and the raw text "C:\\Sounds\\x.wav" unchanged' ($written.Contains('"reconnect": true') -and $written.Contains('"alertSound": "C:\\Sounds\\x.wav"')) $written
$c = New-Cfg; $c.watch.marketTimeZone = 'Eastern\Standard'
$threw = $false
try { $null = Write-FeedGuardConfig -Config $c -NoReload } catch { $threw = $true }
Check 'a backslash in a key Guard DOES own is still refused' $threw 'accepted'

'=== the add-on''s OWN parser reads what Guard wrote'
$c = New-Cfg 'handsoff'; $c.watch.extraDates = @('2026-03-10'); $c.watch.staleSeconds = 90; $c.alerts.ntfyTopic = 'guard-test-topic-7Qx'
[IO.File]::WriteAllText($cfgPath, '{"graceSeconds": 25}')
$null = Write-FeedGuardConfig -Config $c -NoReload
. (Join-Path $PSScriptRoot '..\..\..\tools\FeedGuardTestKit.ps1')
$parsers = Get-AddOnSlice 'private static double NumCfg(' '        private void LogSafe('
$code = @"
using System; using System.Collections.Generic; using System.Globalization; using System.IO;
class T {
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static void LogSafe(string s) { }
    $parsers
    static int Main(string[] args) {
        string txt = File.ReadAllText(args[0]);
        Console.WriteLine("watchdogInstruments=" + string.Join("|", ListCfg(txt, "watchdogInstruments").ToArray()));
        Console.WriteLine("watchdogStaleSeconds=" + NumCfg(txt, "watchdogStaleSeconds", -1).ToString(Inv));
        Console.WriteLine("graceSeconds=" + NumCfg(txt, "graceSeconds", -1).ToString(Inv));
        Console.WriteLine("reconnect=" + BoolCfg(txt, "reconnect", false));
        Console.WriteLine("autoRecover=" + BoolCfg(txt, "autoRecover", false));
        Console.WriteLine("reEnableAfterRecover=" + BoolCfg(txt, "reEnableAfterRecover", false));
        Console.WriteLine("useSessionGate=" + BoolCfg(txt, "useSessionGate", false));
        Console.WriteLine("marketTimeZoneId=" + StrCfg(txt, "marketTimeZoneId", "?"));
        Console.WriteLine("holidays=" + string.Join("|", ListCfg(txt, "holidays").ToArray()));
        Console.WriteLine("webhookFormat=" + StrCfg(txt, "webhookFormat", "?"));
        Console.WriteLine("webhookIsNtfy=" + StrCfg(txt, "webhookUrl", "").StartsWith("https://ntfy.sh/"));
        return 0;
    }
}
"@
$parsed = @{}
Invoke-CSharpTest 'cfgparse' $code -Arguments @($cfgPath) | ForEach-Object { if ("$_" -match '^(\w+)=(.*)$') { $parsed[$matches[1]] = $matches[2] } }
Check 'the add-on reads the watch list' ($parsed.watchdogInstruments -eq 'ES ##-##|NQ ##-##') $parsed.watchdogInstruments
Check 'the add-on reads the stale threshold' ($parsed.watchdogStaleSeconds -eq '90') $parsed.watchdogStaleSeconds
Check 'the add-on reads a kept key' ($parsed.graceSeconds -eq '25') $parsed.graceSeconds
Check 'the add-on reads the three switches' ($parsed.reconnect -eq 'True' -and $parsed.autoRecover -eq 'True' -and $parsed.reEnableAfterRecover -eq 'True') "$($parsed.reconnect) $($parsed.autoRecover) $($parsed.reEnableAfterRecover)"
Check 'the add-on reads the session gate and time zone' ($parsed.useSessionGate -eq 'True' -and $parsed.marketTimeZoneId -eq 'Eastern Standard Time') "$($parsed.useSessionGate) $($parsed.marketTimeZoneId)"
Check 'the add-on reads the extra dates' ($parsed.holidays -eq '2026-03-10') $parsed.holidays
Check 'the add-on reads the alert channel' ($parsed.webhookFormat -eq 'text' -and $parsed.webhookIsNtfy -eq 'True') "$($parsed.webhookFormat) $($parsed.webhookIsNtfy)"

Complete-Test
