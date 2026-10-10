# The overwatch's newer paths: who starts a NinjaTrader that is down, the duplicate-instance watch, and
# -Report reading without writing. Functions are cut out of Guard-Overwatch.ps1, not copied.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes owflow
$GuardHome = $h.Home
$base = $h.Nt8
$stateDir = Get-GuardPath state
$guard = Get-GuardDefaults
$cfg = @{ relauncherStaleSeconds = 180 }

$script:logLines = @(); $script:alerts = @(); $script:starts = @()
function Log($m) { $script:logLines += $m }
function Send-Alert($title, $body, $prio) { $script:alerts += [pscustomobject]@{ title = $title; body = $body; prio = $prio } }
$script:FX_RESULT = 'launched'
function Invoke-Nt8Start { param($Component, $Config, $GuardHome, $Nt8Dir) $script:starts += $Component; [pscustomobject]@{ Result = $script:FX_RESULT; Restore = '' } }

Invoke-Expression (Get-OverwatchFunction 'Get-AgeSeconds')
Invoke-Expression (Get-OverwatchFunction 'Invoke-OverwatchDown')
function Reset { $script:logLines = @(); $script:alerts = @(); $script:starts = @()
                 $script:state = [pscustomobject]@{ consecutive = 2; fault = 'X'; startNote = ''; downAlerted = '' }; $state = $script:state }
$hb = Join-Path $stateDir 'relaunch.heartbeat'

'=== NinjaTrader down, relauncher running: the relauncher owns it'
Reset; [IO.File]::WriteAllText($hb, [DateTime]::UtcNow.ToString('o'))
Invoke-OverwatchDown
Check 'no start from the overwatch' ($script:starts.Count -eq 0)
Check 'fault counter cleared' ($script:state.consecutive -eq 0)
Invoke-OverwatchDown
Check 'said once, not every minute' (@($script:logLines | Where-Object { $_ -match 'relauncher is running' }).Count -eq 1)

'=== NinjaTrader down, relauncher NOT running (stale heartbeat): the overwatch starts it, through the shared routine'
Reset; [IO.File]::WriteAllText($hb, [DateTime]::UtcNow.AddMinutes(-10).ToString('o'))
(Get-Item $hb).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(-10)
Invoke-OverwatchDown
Check 'started once, as the overwatch' (($script:starts -join ',') -eq 'overwatch')
$a = @($script:alerts | Where-Object { $_.title -match 'relauncher is not running' })
Check 'priority-5 alert says the relauncher stopped and what was done' ($a.Count -eq 1 -and $a[0].prio -eq '5' -and $a[0].body -match 'started NinjaTrader instead') "alerts: $(($script:alerts | ForEach-Object { $_.body }) -join '; ')"
Invoke-OverwatchDown
Check 'tries again next minute' ($script:starts.Count -eq 2)
Check 'but alerts once per outage' (@($script:alerts | Where-Object { $_.title -match 'relauncher is not running' }).Count -eq 1)

'=== relauncher never ran, and starting is set to alert only'
Reset; Remove-Item $hb -Force; $script:FX_RESULT = 'relaunch-off'
Invoke-OverwatchDown
$a = @($script:alerts | Where-Object { $_.title -match 'relauncher is not running' })
Check 'says it stays down and why' ($a.Count -eq 1 -and $a[0].body -match 'never run' -and $a[0].body -match 'stays down') "body: $($a[0].body)"
$script:FX_RESULT = 'launched'

'=== the duplicate-instance watch reads NinjaTrader''s current log and alerts once per pair'
New-Item -ItemType Directory -Path (Join-Path $h.Nt8 'log') -Force | Out-Null
$t0 = (Get-Date).AddMinutes(-2)
$fmt = 'yyyy-MM-dd HH:mm:ss:fff'
[IO.File]::WriteAllLines((Join-Path $h.Nt8 'log\log.20261009.00000.txt'), @(
    "$($t0.ToString($fmt))|1|4|Enabling NinjaScript strategy 'Alpha/101' : On starting",
    "$($t0.AddMilliseconds(30).ToString($fmt))|1|4|Enabling NinjaScript strategy 'Alpha/101' : On starting"))
Invoke-Expression (Get-OverwatchFunction 'Invoke-DuplicateWatch')
Reset; $script:state | Add-Member dupScanLocal '' -Force
Invoke-DuplicateWatch
$a = @($script:alerts | Where-Object { $_.title -match 'TWO copies' })
Check 'two copies -> one priority-5 alert naming it' ($a.Count -eq 1 -and $a[0].prio -eq '5' -and $a[0].body -match 'Alpha/101') "alerts: $(($script:alerts | ForEach-Object { $_.title }) -join '; ')"
Check 'and an event' ([bool](Read-GuardEvents -KindPattern '^nt8\.duplicate-instances$'))
Invoke-DuplicateWatch
Check 'the same pair is not alerted again' (@($script:alerts | Where-Object { $_.title -match 'TWO copies' }).Count -eq 1)
Reset; $script:state | Add-Member dupScanLocal '' -Force
[IO.File]::WriteAllLines((Join-Path $h.Nt8 'log\log.20261009.00000.txt'), @(
    "$((Get-Date).AddHours(-3).ToString($fmt))|1|4|Enabling NinjaScript strategy 'Old/1' : On starting",
    "$((Get-Date).AddHours(-3).AddMilliseconds(10).ToString($fmt))|1|4|Enabling NinjaScript strategy 'Old/1' : On starting"))
Invoke-DuplicateWatch
Check 'first look ignores old history' ($script:alerts.Count -eq 0)

'=== the add-ons count as loaded on EITHER heartbeat (a box without the feed guard still restores and backs up)'
Invoke-Expression (Get-OverwatchFunction 'Test-AddonsFresh')
$arHeart = Join-Path $h.Nt8 'AutoReconnect.heartbeat.json'
$brHeart = Join-Path $h.Nt8 'NT8Bridge\result\heartbeat.json'
New-Item -ItemType Directory -Path (Split-Path $brHeart -Parent) -Force | Out-Null
$cfg = @{ relauncherStaleSeconds = 180; guardianStaleSeconds = 90; bridgeStaleSeconds = 180 }
Check 'neither heartbeat -> not loaded' (-not (Test-AddonsFresh))
[IO.File]::WriteAllText($brHeart, '{}')
Check 'only the NT8 link -> loaded' (Test-AddonsFresh)
(Get-Item $brHeart).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(-10)
Check 'a stale NT8 link -> not loaded' (-not (Test-AddonsFresh))
[IO.File]::WriteAllText($arHeart, '{}')
Check 'only the feed guard -> loaded' (Test-AddonsFresh)
$src = Get-OverwatchSource
Check 'both restore gates use it' (([regex]::Matches($src, '\$needsRestore -and (\$addonsUp|\$responding -and \(Test-AddonsFresh\))')).Count -eq 2)

'=== an add-on whose heartbeat predates this NinjaTrader is NOT RUNNING in it - alert, never a restart loop'
Invoke-Expression (Get-OverwatchFunction 'Test-HeartbeatPredatesStart')
$hbf = Join-Path $h.Nt8 'probe.heartbeat.json'
$start = [DateTime]::UtcNow.AddMinutes(-30)
Check 'no file -> no' (-not (Test-HeartbeatPredatesStart $hbf 90 $start))
[IO.File]::WriteAllText($hbf, '{}'); (Get-Item $hbf).LastWriteTimeUtc = $start.AddHours(-2)
Check 'stale and older than this NinjaTrader -> not running in it' (Test-HeartbeatPredatesStart $hbf 90 $start)
(Get-Item $hbf).LastWriteTimeUtc = $start.AddMinutes(10)
Check 'stale but written by this NinjaTrader -> it ran, then stopped (a wedge)' (-not (Test-HeartbeatPredatesStart $hbf 90 $start))
(Get-Item $hbf).LastWriteTimeUtc = [DateTime]::UtcNow
Check 'fresh -> no' (-not (Test-HeartbeatPredatesStart $hbf 90 $start))
Check 'start time unknown -> no' (-not (Test-HeartbeatPredatesStart $hbf 90 $null))

$ladder = Get-OverwatchBlock '$alertOnly = @(' '$verdict = [ordered]@{'
Invoke-Expression ("function Get-Fault {`n" + $ladder + "`nreturn [pscustomobject]@{ Fault = `$fault; AlertOnly = (`$alertOnly -contains `$fault); Detail = `$detail }`n}")
function Set-World([bool]$UiHung, [bool]$GStale, [bool]$BStale, [bool]$ArNL, [bool]$BrNL, [bool]$FeedDead) {
    $script:uiHung = $UiHung; $script:guardianStale = $GStale; $script:bridgeStale = $BStale
    $script:arNotLoaded = $ArNL; $script:brNotLoaded = $BrNL; $script:feedDead = $FeedDead; $script:feedDeferred = $false
    $script:arAge = 400; $script:brAge = 400; $script:arRung = 0; $script:feedAge = 900; $script:sourceBlamed = $false
}
$cfg.feedSourceOutageMaxSeconds = 1800
Set-World $false $true $false $false $false $false
Check 'a guardian that ran and stopped -> GUARDIAN_DEAD (restart)' ((Get-Fault).Fault -eq 'GUARDIAN_DEAD' -and -not (Get-Fault).AlertOnly)
Set-World $false $false $false $true $false $false
$f = Get-Fault
Check 'feed guard not running in this NinjaTrader -> ADDON_NOT_LOADED, alert only' ($f.Fault -eq 'ADDON_NOT_LOADED' -and $f.AlertOnly) "$($f.Fault)"
Check 'the alert says what to do' ($f.Detail -match 'feed guard' -and $f.Detail -match 'compile') $f.Detail
Set-World $false $false $false $false $true $false
Check 'NT8 link not running -> ADDON_NOT_LOADED too' ((Get-Fault).Fault -eq 'ADDON_NOT_LOADED')
$staleBlock = Get-OverwatchBlock '$uiHung        = (-not $responding)' '$feedDead      ='
Invoke-Expression ("function Get-Stale {`n" + $staleBlock + "`nreturn [pscustomobject]@{ G = `$guardianStale; B = `$bridgeStale }`n}")
$responding = $true; $arAge = 400; $brAge = 400; $cfg.guardianStaleSeconds = 90; $cfg.bridgeStaleSeconds = 180
$arNotLoaded = $true; $brNotLoaded = $true
$st = Get-Stale
Check 'a stale heartbeat from an add-on that is not running is NOT a wedge' (-not $st.G -and -not $st.B) "G=$($st.G) B=$($st.B)"
$arNotLoaded = $false; $brNotLoaded = $false
$st = Get-Stale
Check 'a stale heartbeat from an add-on that ran IS one' ($st.G -and $st.B) "G=$($st.G) B=$($st.B)"
$readBlock = Get-OverwatchBlock '$feedAge = $null; $arRung = $null' '$uiHung        ='
Invoke-Expression ("function Read-FeedFromHeartbeat {`n" + $readBlock + "`nreturn `$feedAge`n}")
$arHeart = $hbf
[IO.File]::WriteAllText($hbf, '{ "lastL1AgeSeconds": 999, "rung": 0, "build": "x" }')
$arNotLoaded = $true
Check 'a dead add-on''s old heartbeat is not read as a dead feed' ($null -eq (Read-FeedFromHeartbeat))
$arNotLoaded = $false
Check 'a running add-on''s heartbeat is read' ((Read-FeedFromHeartbeat) -eq 999)

'=== one overwatch run at a time: a run that finds the last one still working leaves at once'
$lockHome = New-TestDir owlock
$lc = Get-GuardDefaults; $lc.protection.preset = 'watch'; $lc.nt8.dataDir = $h.Nt8
Save-GuardConfig -Config $lc -GuardHome $lockHome
$held = [IO.File]::Open((Join-Path (Get-GuardPath state $lockHome) 'overwatch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:AppDir 'Guard-Overwatch.ps1') -GuardHome $lockHome 2>&1 | Out-String
    $code = $LASTEXITCODE
} finally { $held.Close() }
Check 'exits 0' ($code -eq 0) $out
Check 'did nothing: no heartbeat, no state' (-not (Test-Path (Join-Path $lockHome 'state\overwatch.heartbeat')) -and -not (Test-Path (Join-Path $lockHome 'state\overwatch.state.json')))
Check 'and said why' ((Read-AllText (Join-Path $lockHome 'logs\overwatch.log')) -match 'previous run is still working')

'=== -Report reads only: answers, never writes, never acts'
$reportHome = New-TestDir owreport
$out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:AppDir 'Guard-Overwatch.ps1') -Report -GuardHome $reportHome 2>&1 | Out-String
$code = $LASTEXITCODE
$v = $null; try { $v = $out | ConvertFrom-Json } catch { }
Check 'exits 0 with a JSON verdict' ($code -eq 0 -and $null -ne $v) $out
Check 'reports the NinjaTrader folder it watches' ($v.base -eq $h.Nt8) "base=$($v.base)"
$written = @(Get-ChildItem $reportHome -Recurse -File -ErrorAction SilentlyContinue)
Check 'nothing written (no state, no log, no heartbeat)' ($written.Count -eq 0) (($written | ForEach-Object { $_.Name }) -join ', ')

Complete-Test
