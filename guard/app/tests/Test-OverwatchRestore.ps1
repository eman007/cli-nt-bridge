# The RESTORE half: turning the record back into the right clicks, against the grid exactly as a
# real VPS reported it on 2026-09-08 (names replaced). Runs the REAL Invoke-StrategyRestore.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
$tmp = New-TestDir owrestore
$script:cacheFile = Join-Path $tmp 'cache.json'
$script:stratLog  = Join-Path $tmp 'trail.log'
$script:cfg       = @{ restoreMaxAgeMinutes = 60; bridgeTimeoutSeconds = 45; restoreDeferMaxMinutes = 12 }
$script:state     = [pscustomobject]@{ verifyNames = ''; verifyUntil = ''; restoreDeferSinceUtc = '' }

$script:logLines = @(); $script:alerts = @(); $script:enableCalls = @()
function Log($m) { $script:logLines += $m }
function Send-Alert($title, $body, $prio) { $script:alerts += [pscustomobject]@{ title = $title; prio = $prio; body = $body } }

# StrategyB vs StrategyBX: a prefix match here would arm the wrong account's strategy, so the pair
# stays in the fixture. StrategyD Eval runs on a FUNDED account and must never be touched.
$script:GRID = @(
    @{ name = 'StrategyA';           account = 'SimA' }
    @{ name = 'StrategyBX';          account = 'SimBX' }
    @{ name = 'StrategyC';           account = 'SimC' }
    @{ name = 'StrategyD';           account = 'SimD' }
    @{ name = 'StrategyD PM';        account = 'Sim101' }
    @{ name = 'StrategyD Eval';      account = 'FUNDED-ACCT-001' }
    @{ name = 'StrategyE NY';        account = 'SimE NY' }
    @{ name = 'StrategyE Asia';      account = 'SimE Asia' }
    @{ name = 'StrategyE Asia Open'; account = 'SimE AsiaOpen' }
    @{ name = 'StrategyB';           account = 'SimB' }
    @{ name = 'StrategyE London';    account = 'SimE' }
)
# The eight the pre-kill log named as running.
$script:RECORDED = @('StrategyA', 'StrategyBX', 'StrategyD', 'StrategyD PM', 'StrategyE NY', 'StrategyE Asia',
                     'StrategyE Asia Open', 'StrategyE London')

function Set-Record([string[]]$names, [double]$ageMin = 1, [array]$extraGrid = @()) {
    $enabled = @($names | ForEach-Object {
        $n = $_; $row = @($script:GRID + $extraGrid | Where-Object { $_.name -eq $n })[0]
        [ordered]@{ name = $n; account = $row.account; instrument = 'MNQ 09-26' } })
    ([ordered]@{ utc = [DateTime]::UtcNow.AddMinutes(-$ageMin).ToString('o'); why = 'periodic'; enabled = $enabled } |
        ConvertTo-Json -Depth 4) | Out-File -FilePath $script:cacheFile -Encoding utf8
}

# $script:gridRows / $script:enabledAfter drive the stub; $null gridRows = grid unreadable.
$script:FX_ACCT = [pscustomobject]@{ status = 'ok'; accounts = @() }
function Invoke-Bridge($kind, $fields, $timeout) {
    if ($kind -eq 'account') { return $script:FX_ACCT }
    if ($fields.ContainsKey('enable')) {
        $script:enableCalls += $fields.enable
        $rows = @($script:gridRows | ForEach-Object {
            $on = ($script:enabledAfter -contains $_.name)
            [pscustomobject]@{ name = $_.name; account = $_.account; enabled = $on
                               state = $(if ($on) { 'Realtime' } else { 'Finalized' }) } })
        return [pscustomobject]@{ status = 'ok'; strategies = $rows }
    }
    if ($null -eq $script:gridRows) { return $null }
    $rows = @($script:gridRows | ForEach-Object {
        [pscustomobject]@{ name = $_.name; account = $_.account; instrument = 'MNQ 09-26'; enabled = $false; state = 'Finalized' } })
    return [pscustomobject]@{ status = 'ok'; strategies = $rows }
}

# The connection restore runs first (ordering is asserted in Test-OverwatchConnections); stubbed here.
# $true = connections dealt with; $false = NOT READY, which must DEFER the whole restore.
$script:FX_CONN_RESULT = $true
function Invoke-ConnectionRestore { param([string]$reason, $upSeconds) return $script:FX_CONN_RESULT }

$stateDir = $tmp
Invoke-Expression (Get-OverwatchFunction 'Read-CachedEnabled')
Invoke-Expression (Get-OverwatchFunction 'StratKey')
Invoke-Expression (Get-OverwatchFunction 'Send-AlertOnce')
Invoke-Expression (Get-OverwatchFunction 'Clear-AlertOnce')
Invoke-Expression (Get-OverwatchFunction 'Get-PositionState')
Invoke-Expression (Get-OverwatchFunction 'Invoke-StrategyRestore')

function Reset { $script:logLines = @(); $script:alerts = @(); $script:enableCalls = @()
                 $script:gridRows = $script:GRID; $script:enabledAfter = @(); $script:cfg.Remove('reenableStrategies')
                 $script:FX_ACCT = [pscustomobject]@{ status = 'ok'; accounts = @() }; Clear-AlertOnce '' }

'=== the real post-restart case - 8 recorded, all off in the grid'
Reset; Set-Record $RECORDED; $script:enabledAfter = $RECORDED
$r = Invoke-StrategyRestore 'we restarted it' 600
$sent = @($script:enableCalls[0] -split ',')
Check 'handled (returns true)' ($r -eq $true) "returned $r"
Check 'exactly the 8 recorded were enabled' (($sent.Count -eq 8) -and (@($RECORDED | Where-Object { $sent -notcontains $_ }).Count -eq 0)) "sent: $($script:enableCalls[0])"
Check 'StrategyB was NOT armed by StrategyBX' ($sent -notcontains 'StrategyB') "sent: $($script:enableCalls[0])"
Check 'the funded StrategyD Eval was NOT touched' ($sent -notcontains 'StrategyD Eval') "sent: $($script:enableCalls[0])"
Check 'no ambiguity refusal' (-not ($script:logLines -match 'AMBIGUOUS')) 'refused something'
Check 'success alert raised' ([bool]($script:alerts | Where-Object { $_.title -match 'restored' })) 'no alert'

'=== empty record after OUR kill must ALARM (a box running with everything off, nobody told)'
Reset; Set-Record @()
$r = Invoke-StrategyRestore 'we restarted it' 600
$a = @($script:alerts | Where-Object { $_.title -match 'NO strategies restored' })
Check 'priority-5 alert fired' (($a.Count -eq 1) -and ($a[0].prio -eq '5')) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"
Check 'nothing was enabled' ($script:enableCalls.Count -eq 0) "enabled: $($script:enableCalls -join '|')"

'=== empty record when we did NOT cause the restart - stay quiet'
Reset; Set-Record @()
$null = Invoke-StrategyRestore 'restart we did not cause' 600
Check 'no alert (avoids paging on an unrelated restart)' ($script:alerts.Count -eq 0) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== record older than restoreMaxAgeMinutes - refuse, do not guess'
Reset; Set-Record $RECORDED -ageMin 75
$null = Invoke-StrategyRestore 'we restarted it' 600
Check 'nothing enabled from a stale record' ($script:enableCalls.Count -eq 0) "enabled: $($script:enableCalls -join '|')"
Check 'told a human' ([bool]($script:alerts | Where-Object { $_.title -match 'NOT restored' })) 'silent'
Check 'alert names the strategies, not blanks' ([bool]($script:alerts[0].body -match 'StrategyA@SimA')) "body: $($script:alerts[0].body)"

'=== duplicate name in the grid - refuse THAT one, still restore the rest'
Reset
$dupe = @{ name = 'StrategyA'; account = 'SomeFundedAcct' }
$script:gridRows = $script:GRID + $dupe
Set-Record $RECORDED
$script:enabledAfter = @($RECORDED | Where-Object { $_ -ne 'StrategyA' })
$null = Invoke-StrategyRestore 'we restarted it' 600
$sent = @($script:enableCalls[0] -split ',')
Check 'the ambiguous name was NOT clicked' ($sent -notcontains 'StrategyA') "sent: $($script:enableCalls[0])"
Check 'the other 7 still went' ($sent.Count -eq 7) "sent $($sent.Count)"
Check 'escalated to a human' ([bool]($script:alerts | Where-Object { $_.title -match 'ambiguous' })) 'no ambiguity alert'

'=== grid not readable yet - retry, do not burn the one attempt'
Reset; Set-Record $RECORDED; $script:gridRows = $null
$r = Invoke-StrategyRestore 'we restarted it' 600
Check 'returns false so a later cycle retries' ($r -eq $false) "returned $r"
Check 'nothing enabled' ($script:enableCalls.Count -eq 0) "enabled: $($script:enableCalls -join '|')"

'=== enabled but not Realtime yet - defer, do not page at 3am'
Reset; Set-Record $RECORDED; $script:enabledAfter = @()
$null = Invoke-StrategyRestore 'we restarted it' 600
Check 'no failure alert while it is still loading' (-not ($script:alerts | Where-Object { $_.prio -eq '5' })) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"
Check 'scheduled a re-check instead' ([bool]$state.verifyUntil) 'no deferred verification set'

'=== connections NOT READY - the whole restore waits, grid untouched'
Reset; Set-Record $RECORDED; $script:enabledAfter = $RECORDED; $script:FX_CONN_RESULT = $false; $script:state.restoreDeferSinceUtc = ''
$r = Invoke-StrategyRestore 'restart we did not cause' 53
Check 'returns false so the caller retries (restart stays unhandled)' ($r -eq $false) "returned $r"
Check 'no strategy was enabled onto a dead connection' ($script:enableCalls.Count -eq 0) "enables: $($script:enableCalls -join '|')"
Check 'deferral clock started' ($script:state.restoreDeferSinceUtc -ne '') "restoreDeferSinceUtc=$($script:state.restoreDeferSinceUtc)"
Check 'and said so' ([bool]($script:logLines -match 'deferred')) "log: $($script:logLines -join ' | ')"

'=== the deferral is BOUNDED - a broker outage cannot keep the grid off all session'
Reset; Set-Record $RECORDED; $script:enabledAfter = $RECORDED; $script:FX_CONN_RESULT = $false
$script:state.restoreDeferSinceUtc = [DateTime]::UtcNow.AddMinutes(-($script:cfg.restoreDeferMaxMinutes + 1)).ToString('o')
$null = Invoke-StrategyRestore 'restart we did not cause' 600
Check 'strategies restored anyway once the bound is passed' ($script:enableCalls.Count -gt 0) "enables: $($script:enableCalls -join '|')"
Check 'and a human is told to connect BY HAND, one at a time' (
    [bool]($script:alerts | Where-Object { $_.title -match 'connections DOWN' -and $_.body -match 'ONE AT A TIME' })
) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== a normal restore clears the deferral clock'
Reset; Set-Record $RECORDED; $script:enabledAfter = $RECORDED; $script:FX_CONN_RESULT = $true
$script:state.restoreDeferSinceUtc = [DateTime]::UtcNow.ToString('o')
$null = Invoke-StrategyRestore 'we restarted it' 600
Check 'clock cleared' ($script:state.restoreDeferSinceUtc -eq '') "restoreDeferSinceUtc=$($script:state.restoreDeferSinceUtc)"

'=== re-enabling set to alert only (Recover NT8, Watch only): say which are off, switch nothing on'
Reset; Set-Record $RECORDED; $script:enabledAfter = $RECORDED; $script:cfg.reenableStrategies = $false
$r = Invoke-StrategyRestore 'we restarted it' 600
Check 'handled, nothing enabled' ($r -eq $true -and $script:enableCalls.Count -eq 0) "enables: $($script:enableCalls -join '|')"
$a = @($script:alerts | Where-Object { $_.title -match 'are OFF' })
Check 'one priority-5 alert naming them with their accounts' ($a.Count -eq 1 -and $a[0].prio -eq '5' -and $a[0].body -match 'StrategyA@SimA') "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"
Check 'the alert warns that enabling cancels a held stop/target' ($a[0].body -match 'cancels the stop/target') $a[0].body

'=== alert only, and an account holds an UNPROTECTED position: the alert says so'
Reset; Set-Record @('StrategyA', 'StrategyD'); $script:cfg.reenableStrategies = $false
$script:FX_ACCT = [pscustomobject]@{ status = 'ok'; accounts = @(
    [pscustomobject]@{ name = 'SimA'; positions = @([pscustomobject]@{ instrument = 'MNQ 09-26'; marketPosition = 'Long'; quantity = 2 }); workingOrders = @() }
    [pscustomobject]@{ name = 'SimD'; positions = @([pscustomobject]@{ instrument = 'MNQ 09-26'; marketPosition = 'Short'; quantity = 1 })
                       workingOrders = @([pscustomobject]@{ instrument = 'MNQ 09-26'; state = 'Working'; orderName = 'Stop1' }) }) }
$null = Invoke-StrategyRestore 'we restarted it' 600
Check 'still nothing enabled' ($script:enableCalls.Count -eq 0)
$a = @($script:alerts | Where-Object { $_.title -match 'UNPROTECTED' })
Check 'the title says UNPROTECTED' ($a.Count -eq 1 -and $a[0].prio -eq '5') "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"
Check 'the naked one is named with its position' ($a[0].body -match 'StrategyA@SimA \[MNQ 09-26 Long 2\]' -and $a[0].body -match 'no stop or target') $a[0].body
Check 'the protected one is told apart' ($a[0].body -match 'StrategyD@SimD \[MNQ 09-26 Short 1\][^\n]*protected by 1 working order') $a[0].body
Reset; Set-Record @('StrategyA'); $script:cfg.reenableStrategies = $false; $script:FX_ACCT = $null
$null = Invoke-StrategyRestore 'we restarted it' 600
Check 'positions unreadable -> still alerts, and says so' ([bool]($script:alerts | Where-Object { $_.body -match 'positions could not be read' })) "alerts: $(($script:alerts | ForEach-Object { $_.body }) -join '; ')"

Complete-Test
