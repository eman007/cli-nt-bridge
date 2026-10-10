# The remedy (kill NinjaTrader) must NOT run while the screen is LOCKED, nor when Protection says
# alert only, nor in a dry run. The blocks are cut out of Guard-Overwatch.ps1, not copied.
#
# The whole remedy is "kill, and something starts it again and logs in" - and the login cannot reach
# NinjaTrader's window while Windows' lock screen owns the desktop, so a kill while locked turns a
# stale feed into NO NinjaTrader at all until a human logs in. A desktop that sleeps overnight wakes
# to a lock screen with the market open, so this is a normal path, not an edge case.
. (Join-Path $PSScriptRoot 'TestKit.ps1')

$script:logLines = @(); $script:alerts = @(); $script:stateWrites = 0
function Log($m) { $script:logLines += $m }
function Send-Alert($title, $body, $prio) { $script:alerts += [pscustomobject]@{ title = $title; body = $body; prio = $prio } }
function Write-State($s) { $script:stateWrites++ }
$stateDir = New-TestDir owhold
Invoke-Expression (Get-OverwatchFunction 'Send-AlertOnce')
Invoke-Expression (Get-OverwatchFunction 'Clear-AlertOnce')
$detail = 'no market data for 25089s with the market open'
$exposure = '0 position(s), 0 working order(s)'
$fault = 'FEED_DEAD'

# `exit 0` would end the test host: turn each into a sentinel return.
function ConvertTo-Guard([string]$Block) {
    "function Invoke-Guard {`n" + ($Block -replace '(?m)^\s*Write-State \$state; exit 0\s*$', '    $script:stateWrites++; return "HELD"') + "`n    return 'KILL'`n}"
}

'=== the lock-screen hold'
$block = Get-OverwatchBlock '$consoleLocked = $false' '$dumpNote = $(if ($cfg.dumpBeforeKill)'
Check 'the block probes LogonUI in THIS session' ($block -match "Get-GuardSessionProcess -Name 'LogonUI'")
Invoke-Expression (ConvertTo-Guard $block)

$script:FX_LOCKED = $false
$script:FX_THROW = $false
function Get-GuardSessionProcess { param([string]$Name)
    if ($script:FX_THROW) { throw 'probe exploded' }
    if ($Name -eq 'LogonUI' -and $script:FX_LOCKED) { return @([pscustomobject]@{ Id = 1 }) }
    return @()
}
function Reset { $script:logLines = @(); $script:alerts = @(); $script:stateWrites = 0 }

Reset; $script:FX_LOCKED = $false
$script:state = [pscustomobject]@{ lockHoldAlertUtc = '' }; $state = $script:state
Check 'UNLOCKED -> reaches the kill' ((Invoke-Guard) -eq 'KILL')
Check 'no alert from the guard' ($script:alerts.Count -eq 0) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

Reset; $script:FX_LOCKED = $true
$script:state = [pscustomobject]@{ lockHoldAlertUtc = '' }; $state = $script:state
Check 'LOCKED -> does NOT kill' ((Invoke-Guard) -eq 'HELD')
Check 'logs why' ([bool]($script:logLines -match 'REMEDY HELD')) "log: $($script:logLines -join ' | ')"
Check 'says a kill would strand it until login' ([bool]($script:logLines -match 'until someone logs in')) "log: $($script:logLines -join ' | ')"
Check 'pages a human at priority 5' ([bool]($script:alerts | Where-Object { $_.prio -eq '5' -and $_.title -match 'NOT restarted' })) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"
Check 'the page names the real fault' ([bool]($script:alerts | Where-Object { $_.body -match 'no market data for 25089s' })) 'fault detail missing'
Check 'records the alert time so it can be throttled' ($script:state.lockHoldAlertUtc -ne '') "lockHoldAlertUtc=$($script:state.lockHoldAlertUtc)"

Reset
Check 'still locked next cycle -> holds again' ((Invoke-Guard) -eq 'HELD')
Check 'but no second page within 6h' ($script:alerts.Count -eq 0) "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"

Reset
$script:state.lockHoldAlertUtc = [DateTime]::UtcNow.AddHours(-7).ToString('o')
Check 'a stale hold-alert stamp (>6h) -> holds' ((Invoke-Guard) -eq 'HELD')
Check 'and pages again - a locked box all day must not go quiet' ($script:alerts.Count -eq 1) "alerts: $($script:alerts.Count)"

Reset; $script:FX_LOCKED = $false
$script:state.lockHoldAlertUtc = [DateTime]::UtcNow.ToString('o')
Check 'unlocked again -> reaches the kill' ((Invoke-Guard) -eq 'KILL')
Check 'stamp cleared so the next lock pages immediately' ($script:state.lockHoldAlertUtc -eq '') "lockHoldAlertUtc=$($script:state.lockHoldAlertUtc)"

Reset; $script:FX_LOCKED = $true; $script:FX_THROW = $true
$script:state = [pscustomobject]@{ lockHoldAlertUtc = '' }; $state = $script:state
Check 'the probe THROWS -> FAIL OPEN, the remedy still runs' ((Invoke-Guard) -eq 'KILL')
$script:FX_THROW = $false

'=== restarting a frozen NinjaTrader set to alert only, dry run, -WhatIfRemedy: never kills'
$block = Get-OverwatchBlock 'if ($WhatIfRemedy -or $cfg.dryRunRemedy' '# ! NEVER KILL INTO A LOCKED SCREEN'
Invoke-Expression (ConvertTo-Guard $block)
$state = [pscustomobject]@{}
$WhatIfRemedy = $false
$cfg = @{ dryRunRemedy = $false; restartWhenFrozen = $true }
Reset
Check 'acting -> reaches the next step' ((Invoke-Guard) -eq 'KILL')
$cfg = @{ dryRunRemedy = $false; restartWhenFrozen = $false }
Reset
Check 'alert only -> NOT killed' ((Invoke-Guard) -eq 'HELD')
Check 'and the page says why' ([bool]($script:alerts | Where-Object { $_.title -match 'NOT restarted' -and $_.body -match 'alert only in Protection' })) "alerts: $(($script:alerts | ForEach-Object { $_.body }) -join '; ')"
$null = Invoke-Guard; $null = Invoke-Guard
Check 'a fault that persists pages ONCE, not every minute' (@($script:alerts | Where-Object { $_.title -match 'NOT restarted' }).Count -eq 1) "pages: $(@($script:alerts).Count)"
Clear-AlertOnce 'fault:'
$null = Invoke-Guard
Check 'once the fault clears, the next one pages at once' (@($script:alerts | Where-Object { $_.title -match 'NOT restarted' }).Count -eq 2)
$cfg = @{ dryRunRemedy = $false; restartWhenFrozen = $true; relaunch = $false }
Reset; Clear-AlertOnce 'fault:'
Check 'restart on, but starting NinjaTrader off -> NOT killed (it would stay dead)' ((Invoke-Guard) -eq 'HELD')
Check 'and the page says why' ([bool]($script:alerts | Where-Object { $_.body -match 'Start NinjaTrader when it is down' })) "alerts: $(($script:alerts | ForEach-Object { $_.body }) -join '; ')"
$cfg = @{ dryRunRemedy = $true; restartWhenFrozen = $true }
Reset
Check 'dry run -> NOT killed' ((Invoke-Guard) -eq 'HELD')
$cfg = @{ dryRunRemedy = $false; restartWhenFrozen = $true }; $WhatIfRemedy = $true
Reset
Check '-WhatIfRemedy -> NOT killed' ((Invoke-Guard) -eq 'HELD')
$WhatIfRemedy = $false

'=== still broken inside the cooldown after a restart: one page, not one a minute'
$block = Get-OverwatchBlock '# Cooldown so a box that will not stay healthy' '# REMEDY - kill NinjaTrader'
Invoke-Expression (ConvertTo-Guard $block)
$cfg = @{ cooldownMinutes = 20 }
$state = [pscustomobject]@{ lastRemedyUtc = [DateTime]::UtcNow.AddMinutes(-3).ToString('o') }
Reset; Clear-AlertOnce 'fault:'
Check 'inside the cooldown -> no second restart' ((Invoke-Guard) -eq 'HELD')
$null = Invoke-Guard; $null = Invoke-Guard
Check 'paged once' (@($script:alerts | Where-Object { $_.title -match '\(still\)' }).Count -eq 1) "pages: $(@($script:alerts).Count)"

Complete-Test
