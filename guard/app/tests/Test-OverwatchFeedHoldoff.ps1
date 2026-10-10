# The 2026-09-08 source-outage hold-off: the feed guard ran its whole ladder and blamed the feed
# source, so restarting NinjaTrader cannot help - hold off, but only up to a ceiling. Replays the
# feed guard's real log lines (names replaced), then the same verdict as a structured event (the feed
# guard's newer builds). Runs the REAL Get-SourceOutageVerdictUtc.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes owfeed
$GuardHome = $h.Home
$base = $h.Nt8
$script:arLog = Join-Path $h.Nt8 'AutoReconnect.log'
$script:cfg = @{ sourceVerdictMaxAgeMinutes = 20; feedDeferMaxSeconds = 900
                 feedSourceOutageMaxSeconds = 1800; feedStaleSeconds = 300 }

Invoke-Expression (Get-OverwatchFunction 'Get-SourceOutageVerdictUtc')

# The feed guard's own lines, timestamp re-based so "recent" is meaningful.
function Write-ArLog([double]$minutesAgo, [switch]$NoVerdict) {
    $ts = [DateTime]::UtcNow.AddMinutes(-$minutesAgo).ToString('o')
    $lines = @(
        "$ts  recover: silent stall (400s), healthy watched=[Demo Feed] -> ladder RESTART after cooldown, rung 1 RE-SUBSCRIBE watchdog (non-disruptive)"
        "$ts  recover: stall survived re-subscribe (0 post-snapshot L1 update(s) => feed genuinely dead) -> rung 2 DISCONNECT 'Demo Feed'"
    )
    if (-not $NoVerdict) {
        $lines += "$ts  recover: still stalled after disconnect+reconnect settle -> ALERT + cooldown (feed likely down at the source)"
    }
    [IO.File]::WriteAllLines($script:arLog, $lines)
}

# The decision under test, lifted from the script so the test tracks the real expression.
function Decide([bool]$feedDead, $arRung, [double]$feedAge) {
    $ladderActive  = ($null -ne $arRung -and $arRung -gt 0)
    $srcVerdictUtc = $(if ($feedDead) { Get-SourceOutageVerdictUtc } else { $null })
    $sourceBlamed  = ($null -ne $srcVerdictUtc)
    $feedCeiling   = $(if ($sourceBlamed) { $cfg.feedSourceOutageMaxSeconds } else { $cfg.feedDeferMaxSeconds })
    $deferred      = ($feedDead -and ($ladderActive -or $sourceBlamed) -and $feedAge -lt $feedCeiling)
    return [pscustomobject]@{ sourceBlamed = $sourceBlamed; deferred = $deferred; kills = ($feedDead -and -not $deferred) }
}

'=== the actual 2026-09-08 kill - rung 0, source blamed, market data dead 600s'
Write-ArLog -minutesAgo 2
$d = Decide $true 0 600
Check 'recognises the source verdict despite rung 0' ($d.sourceBlamed) 'verdict not seen'
Check 'HOLDS OFF instead of killing (the old code killed here)' ($d.deferred -and -not $d.kills) "deferred=$($d.deferred) kills=$($d.kills)"

'=== the hold-off is BOUNDED - a long enough outage still restarts'
Write-ArLog -minutesAgo 2
$d = Decide $true 0 1900
Check 'kills anyway past the ceiling' ($d.kills) "kills=$($d.kills)"

'=== a STALE verdict does not license an indefinite hold'
Write-ArLog -minutesAgo 45
$d = Decide $true 0 600
Check 'old verdict is ignored' (-not $d.sourceBlamed) 'stale verdict still counted'
Check 'so it kills, as before' ($d.kills) "kills=$($d.kills)"

'=== ladder never ran (no verdict, rung 0) - unchanged behaviour'
Write-ArLog -minutesAgo 2 -NoVerdict
$d = Decide $true 0 600
Check 'no verdict found' (-not $d.sourceBlamed) 'phantom verdict'
Check 'kills, exactly as it did before this change' ($d.kills) "kills=$($d.kills)"

'=== mid-ladder deferral still works on its own ceiling'
Write-ArLog -minutesAgo 2 -NoVerdict
$d = Decide $true 2 600
Check 'held off while climbing' ($d.deferred) 'not deferred'
$d = Decide $true 2 1000
Check 'mid-ladder ceiling still 900s, not the longer one' ($d.kills) "kills=$($d.kills)"

'=== healthy feed is never deferred or killed'
Write-ArLog -minutesAgo 2
$d = Decide $false 0 0
Check 'no fault when the feed is alive' ((-not $d.kills) -and (-not $d.deferred)) "kills=$($d.kills)"
Check 'the verdict is not even consulted when the feed is fine' (-not $d.sourceBlamed) 'read it needlessly'

'=== a missing feed guard log must not throw'
Remove-Item $script:arLog -Force
$d = Decide $true 0 600
Check 'degrades to the old behaviour' ((-not $d.sourceBlamed) -and $d.kills) "kills=$($d.kills)"

'=== the verdict as a structured feed-guard EVENT (newer feed guard) counts the same'
$fg = Get-FeedGuardEventsPath $h.Nt8
New-Item -ItemType Directory -Path (Split-Path $fg -Parent) -Force | Out-Null
[IO.File]::WriteAllText($fg, (@{ utc = [DateTime]::UtcNow.AddMinutes(-2).ToString('o'); component = 'feedguard'; kind = 'feed.source-outage'; severity = 'warn'; text = 'feed likely down at the source' } | ConvertTo-Json -Compress) + "`n")
$d = Decide $true 0 600
Check 'event alone (no log line) -> source blamed, held off' ($d.sourceBlamed -and $d.deferred) "blamed=$($d.sourceBlamed)"
[IO.File]::WriteAllText($fg, (@{ utc = [DateTime]::UtcNow.AddMinutes(-45).ToString('o'); component = 'feedguard'; kind = 'feed.source-outage'; severity = 'warn'; text = 'old' } | ConvertTo-Json -Compress) + "`n")
$d = Decide $true 0 600
Check 'a stale event is ignored too' (-not $d.sourceBlamed) 'stale event counted'

'=== a feed guard that WRITES events is read from its events only - its log wording no longer matters'
$script:arHeart = Join-Path $h.Nt8 'AutoReconnect.heartbeat.json'
[IO.File]::WriteAllText($script:arHeart, '{"component":"AutoReconnect","version":"5.0.0","events":true,"rung":0}')
Remove-Item $fg -Force
Write-ArLog -minutesAgo 2
$d = Decide $true 0 600
Check 'the log sentence alone is NOT a verdict when the heartbeat says events:true' (-not $d.sourceBlamed) 'read the log anyway'
[IO.File]::WriteAllText($fg, (@{ utc = [DateTime]::UtcNow.AddMinutes(-2).ToString('o'); component = 'feedguard'; kind = 'feed.source-outage'; severity = 'alert'; text = 'feed likely down at the source' } | ConvertTo-Json -Compress) + "`n")
$d = Decide $true 0 600
Check '...and its event still is' ($d.sourceBlamed -and $d.deferred) "blamed=$($d.sourceBlamed)"
[IO.File]::WriteAllText($script:arHeart, '{"component":"AutoReconnect","build":"old","rung":0}')
Remove-Item $fg -Force
$d = Decide $true 0 600
Check 'an older feed guard (no "events" in its heartbeat) is still read from its log' ($d.sourceBlamed) 'old add-on ignored'
Check 'Test-FeedGuardWritesEvents: a missing heartbeat is "no"' (-not (Test-FeedGuardWritesEvents (Join-Path $h.Nt8 'nope.json'))) 'said yes'
[IO.File]::WriteAllText($script:arHeart, 'not json')
Check 'Test-FeedGuardWritesEvents: an unreadable heartbeat is "no"' (-not (Test-FeedGuardWritesEvents $script:arHeart)) 'said yes'

Complete-Test
