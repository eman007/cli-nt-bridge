# Connection restore: bring back what was connected before the outage, without becoming a retry loop.
# Runs the REAL functions cut out of Guard-Overwatch.ps1.
#
# ! FIXTURE NAMES ARE $script:-QUALIFIED AND DELIBERATELY UGLY. PowerShell resolves variables
#   DYNAMICALLY and case-insensitively, so a helper called from inside the function under test sees
#   that function's LOCALS - a fixture sharing a name with one of them is silently shadowed.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
$tmp = New-TestDir owconn
$script:connFile = Join-Path $tmp 'connections.json'
$script:cfg = @{ restoreConnections = $true; connSettleSeconds = 10; connRetryCooldownMinutes = 10
                 bridgeTimeoutSeconds = 30; dryRunRemedy = $false; reconnect = $true
                 connRestoreMinUptimeSeconds = 180; restoreDeferMaxMinutes = 12 }
# Uptimes either side of connRestoreMinUptimeSeconds. The failures this gate exists for were at
# 38s, 52s and 53s after launch; the successes were at 4-5 minutes.
$script:FX_UP_READY = 600
$script:FX_UP_YOUNG = 53
$script:state = [pscustomobject]@{ connAttemptUtc = '' }

$script:logLines = @(); $script:alerts = @(); $script:connectCalls = @()
function Log($m) { $script:logLines += $m }
function Send-Alert($title, $body, $prio) { $script:alerts += [pscustomobject]@{ title = $title; prio = $prio; body = $body } }
function Start-Sleep { param([int]$Seconds, [int]$Milliseconds) }

function Set-ConnRecord([string[]]$names) {
    ([ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); why = 'periodic'; connected = $names; emptySeen = 0 } |
        ConvertTo-Json -Depth 4) | Out-File -FilePath $script:connFile -Encoding utf8
}

# A reconnect request as the NT8 link itself reads it (NT8BridgeServer.cs, RunReconnect): the name comes from
# "connection"; without it the link answers an error and connects nothing.
$script:FX_NO_NAME = [pscustomobject]@{ status = 'error'; errors = @([pscustomobject]@{ code = 'BRIDGE'; message = 'reconnect requires a connection name' }) }
function Read-FakeReconnect($fields) {
    $n = [string]$fields.connection
    if ($n) { $script:connectCalls += $n }
    return $n
}

function Invoke-Bridge($kind, $fields, $timeout) {
    if ($kind -eq 'reconnect') {
        $n = Read-FakeReconnect $fields
        if (-not $n) { return $script:FX_NO_NAME }
        if ($script:FX_CONNECT_WORKS) { $script:FX_LIVE = @($script:FX_LIVE + $n) }
        return [pscustomobject]@{ status = 'ok' }
    }
    if ($kind -eq 'connections') {
        if ($script:FX_CONN_BROKEN) { return $null }
        return [pscustomobject]@{ status = 'ok'; connections = @(
            $script:FX_ALL | ForEach-Object {
                [pscustomobject]@{ name = $_; status = $(if ($script:FX_LIVE -contains $_) { 'Connected' } else { '' }) } }) }
    }
    return [pscustomobject]@{ status = 'ok' }
}

Invoke-Expression (Get-OverwatchFunction 'Read-CachedConnections')
Invoke-Expression (Get-OverwatchFunction 'Update-ConnectionCache')
$script:FX_STOPPED = $false
function Test-GuardStopHeld { param($GuardHome) $script:FX_STOPPED }     # Tools > Stop Guard: see Test-GuardStopStart
Invoke-Expression (Get-OverwatchFunction 'Invoke-ConnectionRestore')

function Reset {
    $script:logLines = @(); $script:alerts = @(); $script:connectCalls = @()
    $script:FX_LIVE = @('Demo Feed'); $script:FX_ALL = @('Demo Feed', 'BrokerA', 'BrokerB')
    $script:FX_CONNECT_WORKS = $true; $script:FX_CONN_BROKEN = $false
    $script:state.connAttemptUtc = ''
    $script:cfg.dryRunRemedy = $false; $script:cfg.connSettleSeconds = 10; $script:cfg.reconnect = $true
}

'=== Guard stopped (Tools > Stop Guard) while the restore was going: no reconnect is sent'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA', 'BrokerB'); $script:FX_STOPPED = $true
$r = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'nothing reconnected, not reported as done, and it says why' ($script:connectCalls.Count -eq 0 -and $r -eq $false -and [bool]($script:logLines -match 'Guard was stopped')) "calls: $($script:connectCalls -join '|'); returned $r"
$script:FX_STOPPED = $false

'=== the 2026-09-08 case - two broker connections live before, only the demo feed back after'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA', 'BrokerB')
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'reconnected exactly the two that were down' (
    ($script:connectCalls.Count -eq 2) -and ($script:connectCalls -contains 'BrokerA') -and ($script:connectCalls -contains 'BrokerB')
) "calls: $($script:connectCalls -join '|')"
Check 'did NOT touch the one already connected' ($script:connectCalls -notcontains 'Demo Feed') "calls: $($script:connectCalls -join '|')"
Check 'reported success' ([bool]($script:alerts | Where-Object { $_.title -match 'connections restored' })) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== everything already up - do nothing at all'
Reset; $script:FX_LIVE = @('Demo Feed', 'BrokerA', 'BrokerB'); Set-ConnRecord @('Demo Feed', 'BrokerA', 'BrokerB')
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'no connect attempted' ($script:connectCalls.Count -eq 0) "calls: $($script:connectCalls -join '|')"
Check 'no alert raised' ($script:alerts.Count -eq 0) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== the connect FAILS - alert a human, do not storm'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA'); $script:FX_CONNECT_WORKS = $false; $script:cfg.connSettleSeconds = 0
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'tried exactly once per connection' ($script:connectCalls.Count -eq 1) "calls: $($script:connectCalls -join '|')"
Check 'priority-5 alert (the feed guard will never retry a parked one)' (
    [bool]($script:alerts | Where-Object { $_.title -match 'did NOT come back' -and $_.prio -eq '5' })
) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== cooldown blocks a second attempt (no retry loop against the broker)'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA'); $script:FX_CONNECT_WORKS = $false; $script:cfg.connSettleSeconds = 0
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
$firstCount = $script:connectCalls.Count
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'second call inside the cooldown sends nothing' ($script:connectCalls.Count -eq $firstCount) "calls: $($script:connectCalls -join '|')"

'=== connections unreadable - retry later, attempt nothing'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA'); $script:FX_CONN_BROKEN = $true
$r = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'returns false so a later cycle retries' ($r -eq $false) "returned $r"
Check 'nothing attempted blind' ($script:connectCalls.Count -eq 0) "calls: $($script:connectCalls -join '|')"

'=== a recorded connection no longer configured in NinjaTrader is skipped'
Reset; Set-ConnRecord @('Demo Feed', 'RetiredBroker')
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'not attempted' ($script:connectCalls -notcontains 'RetiredBroker') "calls: $($script:connectCalls -join '|')"
Check 'and said so' ([bool]($script:logLines -match 'no longer configured')) 'not logged'

'=== dryRunRemedy never sends a connect'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA'); $script:cfg.dryRunRemedy = $true
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'no connect sent' ($script:connectCalls.Count -eq 0) "calls: $($script:connectCalls -join '|')"

'=== reconnecting set to alert only (Watch only): name the dead ones once, connect nothing'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA', 'BrokerB'); $script:cfg.reconnect = $false
$r = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'handled, no connect sent' ($r -eq $true -and $script:connectCalls.Count -eq 0) "calls: $($script:connectCalls -join '|')"
$a = @($script:alerts | Where-Object { $_.title -match 'connections down' })
Check 'priority-5 alert naming both' ($a.Count -eq 1 -and $a[0].prio -eq '5' -and $a[0].body -match 'BrokerA' -and $a[0].body -match 'BrokerB') "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'not re-alerted every minute (cooldown)' (@($script:alerts | Where-Object { $_.title -match 'connections down' }).Count -eq 1)

'=== empty record is a no-op, never a mass connect'
Reset; Set-ConnRecord @()
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'nothing attempted' ($script:connectCalls.Count -eq 0) "calls: $($script:connectCalls -join '|')"

'=== an empty READ must not erase a non-empty record'
Reset
$null = Update-ConnectionCache @(
    [pscustomobject]@{ name = 'Demo Feed'; status = 'Connected' }
    [pscustomobject]@{ name = 'BrokerA'; status = 'Connected' }) 'periodic'
Check 'record starts with 2' ((Read-CachedConnections).connected.Count -eq 2) "got $((Read-CachedConnections).connected.Count)"
$null = Update-ConnectionCache @([pscustomobject]@{ name = 'Demo Feed'; status = '' }) 'pre-kill'
Check 'pre-kill zero-connected read does NOT erase it' ((Read-CachedConnections).connected.Count -eq 2) "got $((Read-CachedConnections).connected.Count)"
$null = Update-ConnectionCache @([pscustomobject]@{ name = 'Demo Feed'; status = '' }) 'periodic'
Check 'first healthy zero read is refused too' ((Read-CachedConnections).connected.Count -eq 2) "got $((Read-CachedConnections).connected.Count)"
$null = Update-ConnectionCache @([pscustomobject]@{ name = 'Demo Feed'; status = '' }) 'periodic'
Check 'second consecutive healthy zero read IS accepted' ((Read-CachedConnections).connected.Count -eq 0) "got $((Read-CachedConnections).connected.Count)"

'=== a deliberate park is recorded, so it stops being restored'
Reset
$null = Update-ConnectionCache @(
    [pscustomobject]@{ name = 'Demo Feed'; status = 'Connected' }
    [pscustomobject]@{ name = 'BrokerA'; status = 'Connected' }) 'periodic'
$null = Update-ConnectionCache @(
    [pscustomobject]@{ name = 'Demo Feed'; status = 'Connected' }
    [pscustomobject]@{ name = 'BrokerA'; status = '' }) 'periodic'
Check 'parked connection dropped from the record' ((Read-CachedConnections).connected -notcontains 'BrokerA') "record: $((Read-CachedConnections).connected -join ',')"
Reset
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'so it is never reconnected against your wishes' ($script:connectCalls -notcontains 'BrokerA') "calls: $($script:connectCalls -join '|')"

'=== ORDERING - the strategy restore does connections FIRST'
$body = Get-OverwatchFunction 'Invoke-StrategyRestore'
$iConn = $body.IndexOf('Invoke-ConnectionRestore')
$iGrid = $body.IndexOf("Invoke-Bridge 'strategies'")
Check 'Invoke-ConnectionRestore is called before the grid read' (($iConn -ge 0) -and ($iGrid -ge 0) -and ($iConn -lt $iGrid)) "connIdx=$iConn gridIdx=$iGrid"

'=== NOT READY - a connect inside the first minutes is not even attempted'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA')
$r = Invoke-ConnectionRestore 'restart we did not cause' $script:FX_UP_YOUNG
Check 'returns false (caller must retry, not proceed)' ($r -eq $false) "returned $r"
Check 'no connect sent while NinjaTrader is still coming up' ($script:connectCalls.Count -eq 0) "calls: $($script:connectCalls -join '|')"
Check 'no alert - nothing is wrong yet' ($script:alerts.Count -eq 0) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"
Check 'cooldown NOT consumed, so the real attempt can still happen' ($script:state.connAttemptUtc -eq '') "connAttemptUtc=$($script:state.connAttemptUtc)"
Check 'said why' ([bool]($script:logLines -match 'not ready')) "log: $($script:logLines -join ' | ')"
$null = Invoke-ConnectionRestore 'restart we did not cause' $script:FX_UP_READY
Check 'and once it IS old enough the connect goes out' ($script:connectCalls -contains 'BrokerA') "calls: $($script:connectCalls -join '|')"

'=== an unknown uptime (process gone) must not block the restore'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA')
$null = Invoke-ConnectionRestore 'we restarted it' $null
Check 'connect still attempted' ($script:connectCalls -contains 'BrokerA') "calls: $($script:connectCalls -join '|')"

'=== connects go ONE AT A TIME, each confirmed before the next'
# NinjaTrader restarts running strategies on every connect and duplicates them when two land
# together (2026-09-21: six rows with two live instances; one entered 10 for a 5-lot template).
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA', 'BrokerB')
$script:FX_ORDER = @()
function Invoke-Bridge($kind, $fields, $timeout) {
    if ($kind -eq 'reconnect') {
        $n = Read-FakeReconnect $fields
        if (-not $n) { return $script:FX_NO_NAME }
        $script:FX_ORDER += "connect:$n"
        if ($script:FX_CONNECT_WORKS) { $script:FX_LIVE = @($script:FX_LIVE + $n) }
        return [pscustomobject]@{ status = 'ok' }
    }
    if ($kind -eq 'connections') {
        $script:FX_ORDER += 'read'
        return [pscustomobject]@{ status = 'ok'; connections = @(
            $script:FX_ALL | ForEach-Object {
                [pscustomobject]@{ name = $_; status = $(if ($script:FX_LIVE -contains $_) { 'Connected' } else { '' }) } }) }
    }
    return [pscustomobject]@{ status = 'ok' }
}
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
$seq = ($script:FX_ORDER -join ' ')
Check 'a confirming read separates the two connects' ($seq -match 'connect:\S+ read.* connect:') "sequence: $seq"

'=== a connect that THROWS is named in the log, not just counted'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA'); $script:cfg.connSettleSeconds = 0
function Invoke-Bridge($kind, $fields, $timeout) {
    if ($kind -eq 'reconnect') {
        if (-not (Read-FakeReconnect $fields)) { return $script:FX_NO_NAME }
        return [pscustomobject]@{ status = 'ok'; connectAttempted = $true; connectThrew = $true
                                  connectTimedOut = $false; connectError = 'InvalidOperationException: boom' }
    }
    if ($kind -eq 'connections') {
        return [pscustomobject]@{ status = 'ok'; connections = @(
            $script:FX_ALL | ForEach-Object {
                [pscustomobject]@{ name = $_; status = $(if ($script:FX_LIVE -contains $_) { 'Connected' } else { '' }) } }) }
    }
    return [pscustomobject]@{ status = 'ok' }
}
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'the exception text reaches the log' ([bool]($script:logLines -match 'boom')) "log: $($script:logLines -join ' | ')"

'=== a reconnect the NT8 link REFUSES is logged with its reason, not just counted as still down'
Reset; Set-ConnRecord @('Demo Feed', 'BrokerA'); $script:cfg.connSettleSeconds = 0
function Invoke-Bridge($kind, $fields, $timeout) {
    if ($kind -eq 'reconnect') {
        if (-not (Read-FakeReconnect $fields)) { return $script:FX_NO_NAME }
        return [pscustomobject]@{ status = 'error'; errors = @([pscustomobject]@{ code = 'BRIDGE'; message = 'connection not configured: BrokerA' }) }
    }
    if ($kind -eq 'connections') {
        return [pscustomobject]@{ status = 'ok'; connections = @(
            $script:FX_ALL | ForEach-Object {
                [pscustomobject]@{ name = $_; status = $(if ($script:FX_LIVE -contains $_) { 'Connected' } else { '' }) } }) }
    }
    return [pscustomobject]@{ status = 'ok' }
}
$null = Invoke-ConnectionRestore 'we restarted it' $script:FX_UP_READY
Check 'the refusal and its reason reach the log' ([bool]($script:logLines -match 'refused the reconnect: connection not configured: BrokerA')) "log: $($script:logLines -join ' | ')"

Complete-Test
