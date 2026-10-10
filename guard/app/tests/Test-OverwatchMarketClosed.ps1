# A CME holiday is not a dead feed. The overwatch's own clock check knows the weekend and the daily
# 17:00-18:00 ET break only; the feed guard's calendar also knows the holidays and early closes and
# publishes its verdict in its heartbeat ("scheduledClosed"). Without it, Thanksgiving afternoon (CME
# shut from 13:00 ET) reads as market data dead for hours and the overwatch restarts NinjaTrader -
# taking its strategies down - every cooldown until 18:00. Runs the REAL fault-inputs block of
# Guard-Overwatch.ps1.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes owclosed
$script:arHeart = Join-Path $h.Nt8 'AutoReconnect.heartbeat.json'
$script:cfg = @{ guardianStaleSeconds = 90; bridgeStaleSeconds = 180; feedStaleSeconds = 300 }
$block = Get-OverwatchBlock '$feedAge = $null; $arRung = $null' '# Is the feed guard actively working'

function Invoke-FaultInputs([bool]$clockClosed, [string]$heartbeat) {
    # the inputs the block expects from the lines above it in the script
    $script:closed = $clockClosed; $script:responding = $true
    $script:arAge = 5; $script:brAge = 5; $script:arNotLoaded = $false; $script:brNotLoaded = $false
    [IO.File]::WriteAllText($script:arHeart, $heartbeat)
    . ([scriptblock]::Create($block))
    return [pscustomobject]@{ feedDead = $feedDead; closed = $closed; feedAge = $feedAge }
}

'=== Thanksgiving 14:00 ET: the clock says open, the feed guard''s calendar says closed'
$r = Invoke-FaultInputs $false '{"component":"AutoReconnect","version":"5.0.0","events":true,"rung":0,"lastL1AgeSeconds":3600,"scheduledClosed":true}'
Check 'a holiday''s silent feed is NOT a dead feed' (-not $r.feedDead) "feedDead=$($r.feedDead)"
Check 'and the verdict reports the market closed' ($r.closed) "closed=$($r.closed)"

'=== an ordinary session with a silent feed is still a dead feed'
$r = Invoke-FaultInputs $false '{"component":"AutoReconnect","version":"5.0.0","events":true,"rung":0,"lastL1AgeSeconds":3600,"scheduledClosed":false}'
Check 'dead feed detected' ($r.feedDead) "feedDead=$($r.feedDead)"

'=== an older feed guard (no scheduledClosed in its heartbeat): unchanged behaviour'
$r = Invoke-FaultInputs $false '{"component":"AutoReconnect","build":"old","rung":0,"lastL1AgeSeconds":3600}'
Check 'dead feed detected, as before' ($r.feedDead) "feedDead=$($r.feedDead)"

'=== the weekend by the overwatch''s own clock still counts'
$r = Invoke-FaultInputs $true '{"component":"AutoReconnect","version":"5.0.0","events":true,"rung":0,"lastL1AgeSeconds":3600,"scheduledClosed":false}'
Check 'clock-closed -> not a dead feed' (-not $r.feedDead) "feedDead=$($r.feedDead)"

'=== a fresh feed is never dead, closed or not'
$r = Invoke-FaultInputs $false '{"component":"AutoReconnect","version":"5.0.0","events":true,"rung":0,"lastL1AgeSeconds":4,"scheduledClosed":false}'
Check 'fresh feed -> fine' (-not $r.feedDead) "feedDead=$($r.feedDead)"

Complete-Test
