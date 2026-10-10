# Detecting "something disabled the strategies and it wasn't you", from the feed guard's real log
# lines of 2026-09-09 (names replaced) and from its structured events. That night NinjaTrader disabled
# all nine strategies on a connection flap, the feed guard fixed the feed in 40s - inside
# feedStaleSeconds - so no fault was raised and nothing put them back. Runs the REAL
# Get-InvoluntaryDisableUtc.
#
# ! Fixtures are $script:-qualified and uniquely named: PowerShell resolves variables dynamically and
#   case-insensitively, so a fixture sharing a name with a local of the function under test is
#   silently shadowed.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes owdisable
$GuardHome = $h.Home
$base = $h.Nt8
$script:arLog = Join-Path $h.Nt8 'AutoReconnect.log'
$script:cfg = @{ strategyDisableMarkerWindowMinutes = 60 }

Invoke-Expression (Get-OverwatchFunction 'Get-InvoluntaryDisableUtc')

function Write-Log([double]$minutesAgo, [string]$kind) {
    $ts = [DateTime]::UtcNow.AddMinutes(-$minutesAgo).ToString('o')
    $lines = switch ($kind) {
        'flap'     { @("$ts  status: Demo Feed -> Connected/ConnectionLost (err=NoError)") }
        'rung2'    { @("$ts  recover: stall survived re-subscribe (0 post-snapshot L1 update(s) => feed genuinely dead) -> rung 2 DISCONNECT 'Demo Feed' (clean cycle; NT8 will DISABLE running strategies on this connection)") }
        'resumed'  { @("$ts  recover: heartbeat RESUMED after DISCONNECT+RECONNECT (strategies disabled), 40s after stall start") }
        'harmless' { @("$ts  watchdog: alive; last L1 update 0s ago",
                       "$ts  status: Demo Feed -> Connected/Connected (err=NoError)",
                       "$ts  grid poll: ok") }
    }
    [IO.File]::WriteAllLines($script:arLog, $lines)
}

'=== the connection flap that actually did it is detected'
Write-Log 5 'flap'
Check 'ConnectionLost flap is a marker' ($null -ne (Get-InvoluntaryDisableUtc)) 'not detected'

'=== the feed guard''s own disconnect rung is detected'
Write-Log 5 'rung2'
Check 'rung 2 DISCONNECT is a marker' ($null -ne (Get-InvoluntaryDisableUtc)) 'not detected'

'=== its recovery line is detected'
Write-Log 5 'resumed'
Check '(strategies disabled) is a marker' ($null -ne (Get-InvoluntaryDisableUtc)) 'not detected'

'=== a HEALTHY log is not a marker - a manual disable is never undone'
Write-Log 5 'harmless'
Check 'no marker on a clean log' ($null -eq (Get-InvoluntaryDisableUtc)) 'false positive - would fight the operator'

'=== an OLD marker is outside the window'
Write-Log 180 'flap'
Check 'stale marker ignored' ($null -eq (Get-InvoluntaryDisableUtc)) 'acted on ancient history'

'=== a missing log must not throw'
Remove-Item $script:arLog -Force
Check 'returns null, no exception' ($null -eq (Get-InvoluntaryDisableUtc)) 'threw or false positive'

'=== the newest marker wins (so a later flap re-triggers)'
$old = [DateTime]::UtcNow.AddMinutes(-30).ToString('o')
$new = [DateTime]::UtcNow.AddMinutes(-2).ToString('o')
[IO.File]::WriteAllLines($script:arLog, @(
    "$old  status: Demo Feed -> Connected/ConnectionLost (err=NoError)",
    "$new  status: Demo Feed -> Connected/ConnectionLost (err=NoError)"))
Check 'returns the LATEST marker' ((Get-InvoluntaryDisableUtc) -gt [DateTime]::UtcNow.AddMinutes(-3)) "got $(Get-InvoluntaryDisableUtc)"

'=== the feed guard''s structured event counts the same, and the newer of event and log wins'
Remove-Item $script:arLog -Force
$fg = Get-FeedGuardEventsPath $h.Nt8
New-Item -ItemType Directory -Path (Split-Path $fg -Parent) -Force | Out-Null
$t1 = [DateTime]::UtcNow.AddMinutes(-4)
[IO.File]::WriteAllText($fg, (@{ utc = $t1.ToString('o'); component = 'feedguard'; kind = 'feed.strategies-disabled'; severity = 'warn'; text = 'flap' } | ConvertTo-Json -Compress) + "`n")
$m = Get-InvoluntaryDisableUtc
Check 'event alone is a marker' ($null -ne $m -and [math]::Abs(($m - $t1).TotalSeconds) -lt 1) "got $m"
[IO.File]::WriteAllLines($script:arLog, @("$([DateTime]::UtcNow.AddMinutes(-1).ToString('o'))  status: Demo Feed -> Connected/ConnectionLost (err=NoError)"))
Check 'a newer log line beats an older event' ((Get-InvoluntaryDisableUtc) -gt $t1.AddMinutes(2)) "got $(Get-InvoluntaryDisableUtc)"

'=== a feed guard that WRITES events is read from its events only'
$script:arHeart = Join-Path $h.Nt8 'AutoReconnect.heartbeat.json'
[IO.File]::WriteAllText($script:arHeart, '{"component":"AutoReconnect","version":"5.0.0","events":true,"rung":0}')
$m = Get-InvoluntaryDisableUtc
Check 'the newer log line is ignored - the event decides' ($null -ne $m -and [math]::Abs(($m - $t1).TotalSeconds) -lt 1) "got $m"
Remove-Item $fg -Force
Check 'no event -> no marker, whatever the log says' ($null -eq (Get-InvoluntaryDisableUtc)) 'a log sentence still acted'
Remove-Item $script:arHeart -Force

'=== the trigger waits for the feed guard to be idle and the feed to be back'
$text = Get-OverwatchSource
$body = $text.Substring($text.IndexOf('SOMETHING DISABLED THE STRATEGIES'))
$body = $body.Substring(0, [Math]::Min(2000, $body.Length))
Check 'guarded on rung 0'      ($body -match '\$arRung -eq 0')            'no rung guard - would re-enable mid-ladder'
Check 'guarded on a live feed' ($body -match '\$feedAge -lt \$cfg\.feedStaleSeconds') 'no feed guard'
Check 'guarded on -not $Report' ($body -match '-not \$Report')            '-Report would act'
Check 'only acts on a NEWER marker' ($body -match '\$mark -gt \$handled') 'would re-fire on the same event'

Complete-Test
