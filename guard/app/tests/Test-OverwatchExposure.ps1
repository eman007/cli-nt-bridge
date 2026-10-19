# The exposure gate: never re-enable a strategy in a way that strips protection from a live position,
# and close a NAKED one first. Runs the REAL Invoke-StrategyRestore.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
$tmp = New-TestDir owexposure
$script:cacheFile = Join-Path $tmp 'cache.json'
$script:stratLog  = Join-Path $tmp 'trail.log'
$script:state     = [pscustomobject]@{ verifyNames = ''; verifyUntil = ''; restoreDeferSinceUtc = '' }
$script:cfg = @{ restoreDeferMaxMinutes = 12; restoreMaxAgeMinutes = 60; bridgeTimeoutSeconds = 45
                 exposureGate = $true; flattenNakedBeforeEnable = $true; dryRunRemedy = $false; reenableStrategies = $true }

$script:logLines = @(); $script:alerts = @(); $script:enableCalls = @(); $script:flattenCalls = @()
function Log($m) { $script:logLines += $m }
function Send-Alert($title, $body, $prio) { $script:alerts += [pscustomobject]@{ title = $title; prio = $prio; body = $body } }
# The gate sleeps 3s before re-reading; make the suite fast without touching the logic under test.
function Start-Sleep { param([int]$Seconds, [int]$Milliseconds) }

# ! FIXTURE NAMES ARE DELIBERATELY UGLY, AND $script:-QUALIFIED WHERE THEY ARE READ.
#   PowerShell resolves variables DYNAMICALLY: a helper called from inside Invoke-StrategyRestore sees
#   that function's LOCALS. The gate assigns `$acct` locally, and PowerShell is case-insensitive, so a
#   fixture once literally named $ACCT was silently shadowed by it and PASSED the one case that must
#   never pass: enabling on top of a position the flatten had failed to close. Do not reuse any name
#   the script itself uses.
$script:FX_STRATEGY = 'StrategyA'
$script:FX_ACCOUNT  = 'SimA'
$script:FX_INSTRUMENT = 'MNQ 12-26'

function Set-Record {
    ([ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); why = 'periodic'
                 enabled = @([ordered]@{ name = $script:FX_STRATEGY; account = $script:FX_ACCOUNT; instrument = $script:FX_INSTRUMENT }) } |
        ConvertTo-Json -Depth 4) | Out-File -FilePath $script:cacheFile -Encoding utf8
}

# $script:pos / $script:orders describe the account BEFORE the flatten; $script:flatAfter says whether
# the flatten actually worked.
function Invoke-Bridge($kind, $fields, $timeout) {
    if ($kind -eq 'flatten') {
        $script:flattenCalls += "$($fields.account)|$($fields.instrument)"
        return [pscustomobject]@{ status = 'ok' }
    }
    if ($kind -eq 'account') {
        if ($script:accountBroken) { return $null }
        $flat = ($script:flattenCalls.Count -gt 0 -and $script:flatAfter)
        $positions = @()
        if ($script:pos -and -not $flat) {
            $positions = @([pscustomobject]@{ instrument = $script:FX_INSTRUMENT; marketPosition = 'Long'; quantity = 1 })
        }
        return [pscustomobject]@{ status = 'ok'; accounts = @(
            [pscustomobject]@{ name = $script:FX_ACCOUNT; positions = $positions; workingOrders = $script:orders }) }
    }
    if ($fields.ContainsKey('enable')) {
        $script:enableCalls += $fields.enable
        return [pscustomobject]@{ status = 'ok'; strategies = @(
            [pscustomobject]@{ name = $script:FX_STRATEGY; account = $script:FX_ACCOUNT; instrument = $script:FX_INSTRUMENT; enabled = $true; state = 'Realtime' }) }
    }
    # plain grid read: the strategy is currently OFF
    return [pscustomobject]@{ status = 'ok'; strategies = @(
        [pscustomobject]@{ name = $script:FX_STRATEGY; account = $script:FX_ACCOUNT; instrument = $script:FX_INSTRUMENT; enabled = $false; state = 'Finalized' }) }
}

function Invoke-ConnectionRestore { param([string]$reason, $upSeconds) return $true }

$stateDir = $tmp
Invoke-Expression (Get-OverwatchFunction 'Read-CachedEnabled')
Invoke-Expression (Get-OverwatchFunction 'StratKey')
Invoke-Expression (Get-OverwatchFunction 'Send-AlertOnce')
Invoke-Expression (Get-OverwatchFunction 'Clear-AlertOnce')
Invoke-Expression (Get-OverwatchFunction 'Get-PositionState')
function Test-GuardStopHeld { param($GuardHome) $false }     # Guard not stopped (Tools > Stop Guard): see Test-GuardStopStart
Invoke-Expression (Get-OverwatchFunction 'Invoke-StrategyRestore')

function Reset {
    $script:logLines = @(); $script:alerts = @(); $script:enableCalls = @(); $script:flattenCalls = @()
    $script:pos = $false; $script:orders = @(); $script:flatAfter = $true; $script:accountBroken = $false
    $script:cfg.flattenNakedBeforeEnable = $true; $script:cfg.dryRunRemedy = $false; $script:cfg.reenableStrategies = $true
    Clear-AlertOnce ''
    Set-Record
}

'=== no position - enable normally, send no orders'
Reset
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'strategy enabled' ($script:enableCalls -contains $script:FX_STRATEGY) "enabled: $($script:enableCalls -join '|')"
Check 'no flatten sent' ($script:flattenCalls.Count -eq 0) "flattens: $($script:flattenCalls -join '|')"

'=== position STILL PROTECTED - the 2026-08-26 naked-position maker'
Reset
$script:pos = $true
$script:orders = @([pscustomobject]@{ instrument = $script:FX_INSTRUMENT; state = 'Working'; orderName = 'Stop1' })
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'NOT enabled (would have cancelled the stop)' ($script:enableCalls.Count -eq 0) "enabled: $($script:enableCalls -join '|')"
Check 'the protective order was NOT touched' ($script:flattenCalls.Count -eq 0) "flattens: $($script:flattenCalls -join '|')"
Check 'escalated to a human' ([bool]($script:alerts | Where-Object { $_.title -match 'still protected' -and $_.prio -eq '5' })) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== NAKED position - close it, verify flat, then enable'
Reset
$script:pos = $true; $script:orders = @(); $script:flatAfter = $true
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'flattened the strategy OWN account+instrument only' ($script:flattenCalls -contains "$script:FX_ACCOUNT|$script:FX_INSTRUMENT") "flattens: $($script:flattenCalls -join '|')"
Check 'enabled after it was confirmed flat' ($script:enableCalls -contains $script:FX_STRATEGY) "enabled: $($script:enableCalls -join '|')"
Check 'told you an order was sent' ([bool]($script:alerts | Where-Object { $_.title -match 'naked position' })) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== flatten fires but the position SURVIVES - never enable on top of it'
Reset
$script:pos = $true; $script:orders = @(); $script:flatAfter = $false
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'flatten was attempted' ($script:flattenCalls.Count -eq 1) "flattens: $($script:flattenCalls -join '|')"
Check 'NOT enabled, because it is not actually flat' ($script:enableCalls.Count -eq 0) "enabled: $($script:enableCalls -join '|')"
Check 'alerted that exposure was not cleared' ([bool]($script:alerts | Where-Object { $_.title -match 'exposure not cleared' })) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== account unreadable - refuse rather than assume flat'
Reset
$script:accountBroken = $true
$r = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'nothing enabled blind' ($script:enableCalls.Count -eq 0) "enabled: $($script:enableCalls -join '|')"
Check 'no orders sent blind' ($script:flattenCalls.Count -eq 0) "flattens: $($script:flattenCalls -join '|')"
Check 'returns false so a later cycle retries' ($r -eq $false) "returned $r"
Check 'alerted that exposure is unknown' ([bool]($script:alerts | Where-Object { $_.title -match 'exposure unknown' })) 'silent'
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'retried every minute, but paged ONCE' (@($script:alerts | Where-Object { $_.title -match 'exposure unknown' }).Count -eq 1) "pages: $(@($script:alerts).Count)"

'=== closing a naked position set to alert only - never send an order'
Reset
$script:pos = $true; $script:orders = @(); $script:cfg.flattenNakedBeforeEnable = $false
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'no order sent' ($script:flattenCalls.Count -eq 0) "flattens: $($script:flattenCalls -join '|')"
Check 'and not enabled onto the naked position either' ($script:enableCalls.Count -eq 0) "enabled: $($script:enableCalls -join '|')"
$a = @($script:alerts | Where-Object { $_.title -match 'UNPROTECTED' })
Check 'paged as UNPROTECTED, priority 5, with why it was left' ($a.Count -eq 1 -and $a[0].prio -eq '5' -and $a[0].body -match 'closing an unprotected position is set to alert only') "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"
Check 'never called "still protected"' (-not ($script:alerts | Where-Object { $_.title -match 'still protected' })) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== dryRunRemedy - report, never trade'
Reset
$script:pos = $true; $script:orders = @(); $script:cfg.dryRunRemedy = $true
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'dry run sends no order' ($script:flattenCalls.Count -eq 0) "flattens: $($script:flattenCalls -join '|')"
Check 'dry run enables nothing' ($script:enableCalls.Count -eq 0) "enabled: $($script:enableCalls -join '|')"
Check 'and calls it UNPROTECTED, not protected' ([bool]($script:alerts | Where-Object { $_.title -match 'UNPROTECTED' }) -and -not ($script:alerts | Where-Object { $_.title -match 'still protected' })) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

'=== re-enabling set to alert only - a naked position is not flattened either'
Reset
$script:pos = $true; $script:orders = @(); $script:cfg.reenableStrategies = $false
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'no order, no enable' ($script:flattenCalls.Count -eq 0 -and $script:enableCalls.Count -eq 0) "flattens: $($script:flattenCalls -join '|') enabled: $($script:enableCalls -join '|')"
$a = @($script:alerts | Where-Object { $_.title -match 'UNPROTECTED' })
Check 'but the alert NAMES the unprotected position (the exposure check still ran)' ($a.Count -eq 1 -and $a[0].body -match 'StrategyA@SimA \[MNQ 12-26 Long 1\]' -and $a[0].body -match 'no stop or target') "alerts: $(($script:alerts | ForEach-Object { $_.body }) -join '; ')"

'=== a position on ANOTHER instrument must not block or get flattened'
Reset
$script:pos = $false
$script:orders = @([pscustomobject]@{ instrument = 'ES 12-26'; state = 'Working'; orderName = 'Stop1' })
$null = Invoke-StrategyRestore 'feed recovered without a restart' 600
Check 'unrelated instrument ignored, strategy enabled' ($script:enableCalls -contains $script:FX_STRATEGY) "enabled: $($script:enableCalls -join '|')"
Check 'nothing flattened' ($script:flattenCalls.Count -eq 0) "flattens: $($script:flattenCalls -join '|')"

Complete-Test
