# A frozen Control Center (VPS 2026-10-16/17): NinjaTrader runs its windows on several UI threads, the
# Control Center on its own. Process.Responding tests one window (the topmost), the NT8 link's heartbeat
# comes from the main application thread and the feed guard's from a timer - so the Control Center's thread
# sat stuck for 6h46m with every check green, and the one probe on that thread (the strategy-list read)
# failed 407 times as log lines. Code under test is cut out of Guard-Overwatch.ps1, not copied.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
Import-Module (Join-Path $script:AppDir 'lib\Guard.Status.psm1') -Force -DisableNameChecking

function W([string]$Title, [bool]$Hung, [bool]$Visible = $true) { [pscustomobject]@{ Title = $Title; Hung = $Hung; Visible = $Visible } }

'=== reading the windows: the Control Center is found by its title, wherever it sits in the z-order'
$win = { , @((W 'Account Data - Strategies' $false), (W 'Chart - MNQ 12-26' $false), (W 'Control Center - Accounts' $true), (W 'Hidden Window' $true $false)) }
$r = Get-Nt8WindowHealth -ProcessId 1 -Windows $win
Check 'Control Center found' ($r.ControlCenterFound) ''
Check 'and seen as hung, though the topmost window is fine' ($r.ControlCenterHung) ''
Check 'hung windows are named' (@($r.HungTitles) -contains 'Control Center - Accounts') (@($r.HungTitles) -join ', ')
Check 'an invisible window does not count' (@($r.HungTitles) -notcontains 'Hidden Window') (@($r.HungTitles) -join ', ')
$r = Get-Nt8WindowHealth -ProcessId 1 -Windows { , @((W 'Control Center' $false), (W 'Chart - NQ 12-26' $true)) }
Check 'a hung chart is named, the Control Center is not hung' (-not $r.ControlCenterHung -and (@($r.HungTitles) -contains 'Chart - NQ 12-26')) ''
$r = Get-Nt8WindowHealth -ProcessId 1 -Windows { , @((W 'Kontrollzentrum' $true)) }
Check 'no window titled Control Center: not found, never guessed hung' (-not $r.ControlCenterFound -and -not $r.ControlCenterHung) ''
$r = $null; $err = ''
try { $r = Get-Nt8WindowHealth -ProcessId $PID } catch { $err = $_.Exception.Message }
Check 'the real Win32 reader runs on this PowerShell (and finds no Control Center here)' ($null -ne $r -and -not $r.ControlCenterFound -and -not $err) $err

'=== the real reader on a real frozen window: a "Control Center" whose thread stops pumping reads as hung'
$frozen = @'
Add-Type -AssemblyName System.Windows.Forms
$f = New-Object System.Windows.Forms.Form; $f.Text = 'Control Center - Accounts'; $f.ShowInTaskbar = $false
$f.StartPosition = 'Manual'; $f.Location = New-Object System.Drawing.Point(-2000, -2000)
$f.Show(); for ($i = 0; $i -lt 20; $i++) { [System.Windows.Forms.Application]::DoEvents(); [System.Threading.Thread]::Sleep(50) }
# Thread.Sleep, NOT Start-Sleep: Start-Sleep waits through COM on this STA thread, which keeps pumping
# window messages, so the window would never stop responding.
[System.Threading.Thread]::Sleep(40000)
'@
$fp = Join-Path (New-TestDir ccfrozen) 'frozen.ps1'; [IO.File]::WriteAllText($fp, $frozen)
$child = Start-Process powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$fp`"" -WindowStyle Hidden -PassThru
try {
    $r = $null; $deadline = (Get-Date).AddSeconds(20)
    do { Start-Sleep -Milliseconds 500; $r = Get-Nt8WindowHealth -ProcessId $child.Id } while (-not $r.ControlCenterFound -and (Get-Date) -lt $deadline)
    Check 'found while it pumps' ($r.ControlCenterFound) ''
    $deadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 500; $r = Get-Nt8WindowHealth -ProcessId $child.Id } while (-not $r.ControlCenterHung -and (Get-Date) -lt $deadline)
    Check 'hung once its thread stops pumping (Windows waits ~5 s)' ($r.ControlCenterHung -and (@($r.HungTitles) -contains 'Control Center - Accounts')) (@($r.HungTitles) -join ', ')
    $t0 = Get-Date; [void](Get-Nt8WindowHealth -ProcessId $child.Id); $ms = ((Get-Date) - $t0).TotalMilliseconds
    Check 'and reading a hung window does not block' ($ms -lt 2000) "${ms}ms"
} finally { try { Stop-Process -Id $child.Id -Force -ErrorAction Stop } catch { } }

'=== what a strategy-list read says: only a TIMEOUT is evidence of a frozen Control Center'
function Reply([string]$Mode) {
    switch ($Mode) {
        'ok'      { return [pscustomobject]@{ status = 'ok'; strategies = @() } }
        'timeout' { return [pscustomobject]@{ status = 'error'; gridResolved = $false; strategies = $null
                                              notes = @('the Control Center UI dispatcher did not respond within 20s (saturated)')
                                              errors = @([pscustomobject]@{ code = 'BRIDGE'; message = 'could not reach the Control Center StrategiesGrid' }) } }
        'nogrid'  { return [pscustomobject]@{ status = 'error'; gridResolved = $false; strategies = $null
                                              notes = @('StrategiesGrid not found after activating every Control Center tab')
                                              errors = @([pscustomobject]@{ code = 'BRIDGE'; message = 'could not reach the Control Center StrategiesGrid' }) } }
        default   { return $null }
    }
}
Check 'ok' ((Get-ControlCenterReadResult (Reply ok)).Result -eq 'ok') ''
Check 'the link''s own 20 s bound ran out -> timeout' ((Get-ControlCenterReadResult (Reply timeout)).Result -eq 'timeout') ''
$v = Get-ControlCenterReadResult (Reply none)
Check 'no answer from the link (busy with a long job, or down) -> unavailable, says why' ($v.Result -eq 'unavailable' -and $v.Why -match 'did not answer') $v.Why
$v = Get-ControlCenterReadResult (Reply nogrid)
Check 'the grid not found (Strategies tab closed) -> unavailable, says why' ($v.Result -eq 'unavailable' -and $v.Why -match 'StrategiesGrid not found') $v.Why
$v = Get-ControlCenterReadResult (Reply none) -WindowLongHung
Check 'no answer while the window has been hung for checks running -> timeout (froze mid-read, link stuck on it)' ($v.Result -eq 'timeout' -and $v.Why -match 'stuck') $v.Why
Check 'but a missing grid stays unavailable even then' ((Get-ControlCenterReadResult (Reply nogrid) -WindowLongHung).Result -eq 'unavailable') ''

'=== each cycle: a hung Control Center window, or a read that timed out last time, earns a read now'
$ccBlock = Get-OverwatchBlock '$ccWin = $null' '# Faults listed in $alertOnly'
Invoke-Expression ("function Invoke-CcCheck {`n" + $ccBlock + "`nreturn [pscustomobject]@{ CcHung = `$ccHung; GridFailing = `$gridFailing; HungNames = `$ccHungNames }`n}")
$script:reads = 0; $script:MODE = 'timeout'; $script:cached = 0; $script:logLines = @()
function Invoke-Bridge($kind, $extra, $t) { if ($kind -eq 'strategies') { $script:reads++; return (Reply $script:MODE) }; return $null }
function Update-StrategyCache($s, $why) { $script:cached++ }
function Log($m) { $script:logLines += $m }
$cfg = @{ bridgeTimeoutSeconds = 30; consecutiveToAct = 3 }
$nt = [pscustomobject]@{ Id = 7 }
$Report = $false; $brNotLoaded = $false; $bridgeStale = $false; $brAge = 1; $needsRestore = $false
function Set-Cc([bool]$Hung) { $script:CC_HUNG = $Hung }
function Get-Nt8WindowHealth { param($ProcessId) [pscustomobject]@{ ControlCenterFound = $true; ControlCenterHung = $script:CC_HUNG; HungTitles = @($(if ($script:CC_HUNG) { 'Control Center - Accounts' })) } }

$state = [ordered]@{ gridFailStreak = 0; ccHungStreak = 0 }
Set-Cc $false; $script:reads = 0
$x = Invoke-CcCheck
Check 'healthy: no read' ($script:reads -eq 0 -and -not $x.GridFailing -and -not $x.CcHung) "reads=$script:reads"
Set-Cc $true; $script:MODE = 'timeout'
$x = Invoke-CcCheck
Check 'window hung -> read now; it times out -> failing, read streak 1, hung streak 1' ($script:reads -eq 1 -and $x.GridFailing -and $x.CcHung -and $state.gridFailStreak -eq 1 -and $state.ccHungStreak -eq 1) "reads=$script:reads streak=$($state.gridFailStreak)/$($state.ccHungStreak)"
Check 'the hung windows are carried for the alert' ($x.HungNames -match 'Control Center') $x.HungNames
$x = Invoke-CcCheck
Check 'still hung -> hung streak 2' ($state.ccHungStreak -eq 2 -and $state.gridFailStreak -eq 2) "$($state.gridFailStreak)/$($state.ccHungStreak)"
Set-Cc $false; $script:reads = 0; $script:MODE = 'ok'; $script:cached = 0
$x = Invoke-CcCheck
Check 'window fine but the last read timed out -> read again; it answers -> both streaks cleared' ($script:reads -eq 1 -and -not $x.GridFailing -and $state.gridFailStreak -eq 0 -and $state.ccHungStreak -eq 0) "$($state.gridFailStreak)/$($state.ccHungStreak)"
Check 'but the good read is NOT recorded here: the restores later in the cycle must see the record as it was' ($script:cached -eq 0) "cached=$script:cached"
Set-Cc $true; $script:MODE = 'none'; $state.gridFailStreak = 0; $script:logLines = @()
$x = Invoke-CcCheck
Check 'window hung (first check) but the link gives no answer -> NOT failing (a busy link is not a frozen Control Center)' (-not $x.GridFailing -and $state.gridFailStreak -eq 0) "streak=$($state.gridFailStreak)"
Check 'and it says so in the log' (($script:logLines -join ' ') -match 'not available') ($script:logLines -join ' | ')
$x = Invoke-CcCheck
Check 'second hung check, still no answer -> still not failing' (-not $x.GridFailing -and $state.ccHungStreak -eq 2) "hung=$($state.ccHungStreak)"
$x = Invoke-CcCheck
Check 'third hung check, no answer -> FAILING: the link is stuck on a Control Center that froze mid-read' ($x.GridFailing -and $state.gridFailStreak -eq 1 -and $state.ccHungStreak -eq 3) "streak=$($state.gridFailStreak) hung=$($state.ccHungStreak)"
Set-Cc $false; $script:MODE = 'nogrid'; $state.gridFailStreak = 1; $script:reads = 0
$x = Invoke-CcCheck
Check 'grid not found -> not failing, and the streak stops (no tab-cycling read every minute)' (-not $x.GridFailing -and $state.gridFailStreak -eq 0 -and $script:reads -eq 1) "streak=$($state.gridFailStreak)"
$x = Invoke-CcCheck
Check 'next cycle: no read' ($script:reads -eq 1) "reads=$script:reads"
Set-Cc $true; $script:reads = 0; $script:MODE = 'timeout'; $brNotLoaded = $true
$x = Invoke-CcCheck
Check 'NT8 link not loaded: no read, so a dead link is never read as a frozen Control Center' ($script:reads -eq 0 -and -not $x.GridFailing) "reads=$script:reads"
$brNotLoaded = $false; $bridgeStale = $true
$x = Invoke-CcCheck
Check 'NT8 link stale: no read either' ($script:reads -eq 0 -and -not $x.GridFailing) "reads=$script:reads"
$bridgeStale = $false; $Report = $true
$x = Invoke-CcCheck
Check '-Report never sends the read (it writes a request file)' ($script:reads -eq 0 -and $x.CcHung) "reads=$script:reads"
$Report = $false

'=== the fault ladder'
$ladder = Get-OverwatchBlock '$alertOnly = @(' '$verdict = [ordered]@{'
Invoke-Expression ("function Get-Fault {`n" + $ladder + "`nreturn [pscustomobject]@{ Fault = `$fault; AlertOnly = (`$alertOnly -contains `$fault); Detail = `$detail }`n}")
function Set-World([bool]$UiHung, [bool]$GStale, [bool]$CcHung, [bool]$GridFailing, [bool]$FeedDead = $false) {
    $script:uiHung = $UiHung; $script:guardianStale = $GStale; $script:bridgeStale = $false
    $script:arNotLoaded = $false; $script:brNotLoaded = $false; $script:feedDead = $FeedDead; $script:feedDeferred = $false
    $script:arAge = 5; $script:brAge = 1; $script:arRung = 0; $script:feedAge = 900; $script:sourceBlamed = $false
    $script:ccHung = $CcHung; $script:gridFailing = $GridFailing; $script:ccHungNames = 'Control Center - Accounts'
}
$cfg.feedSourceOutageMaxSeconds = 1800; $cfg.feedStaleSeconds = 300
Set-World $false $false $true $true
$f = Get-Fault
Check 'THE 10-16 CASE: Control Center hung + its read fails, everything else green -> CONTROL_CENTER_HUNG' ($f.Fault -eq 'CONTROL_CENTER_HUNG') "$($f.Fault)"
Check 'not alert-only (it escalates to a restart)' (-not $f.AlertOnly) ''
Check 'the detail names the Control Center and says the rest is alive' ($f.Detail -match 'Control Center' -and $f.Detail -match 'alive') $f.Detail
Set-World $false $false $false $true
$f = Get-Fault
Check 'read fails but the window is not seen hung -> still CONTROL_CENTER_HUNG (alerts; the restart step checks the window)' ($f.Fault -eq 'CONTROL_CENTER_HUNG') "$($f.Fault)"
Set-World $false $false $true $false
Check 'window hung but the read answered -> no fault (a slow moment, not a freeze)' ($null -eq (Get-Fault).Fault) "$((Get-Fault).Fault)"
Set-World $true $false $true $true
Check 'topmost window hung too, heartbeats alive -> CONTROL_CENTER_HUNG wins over the alert-only UI_HUNG' ((Get-Fault).Fault -eq 'CONTROL_CENTER_HUNG') "$((Get-Fault).Fault)"
Set-World $true $false $false $false
Check 'topmost window hung, Control Center fine -> UI_HUNG, unchanged' ((Get-Fault).Fault -eq 'UI_HUNG') "$((Get-Fault).Fault)"
Set-World $false $true $true $true
Check 'a dead feed guard still outranks it -> GUARDIAN_DEAD' ((Get-Fault).Fault -eq 'GUARDIAN_DEAD') "$((Get-Fault).Fault)"
Set-World $false $false $true $true $true
Check 'a dead feed does not hide it' ((Get-Fault).Fault -eq 'CONTROL_CENTER_HUNG') "$((Get-Fault).Fault)"

'=== what each cycle of the fault does: count, alert at 3, wait, restart at the restart check - only with the window hung'
$st = { param($n, $hung) Get-ControlCenterStep -Consecutive $n -AlertAt 3 -RestartAt 10 -WindowHung $hung }
Check '1-2: count' ((& $st 1 $true) -eq 'count' -and (& $st 2 $true) -eq 'count') ''
Check '3: alert' ((& $st 3 $true) -eq 'alert') (& $st 3 $true)
Check '4-9: wait' ((& $st 4 $true) -eq 'wait' -and (& $st 9 $true) -eq 'wait') ''
Check '10 with the window hung: restart' ((& $st 10 $true) -eq 'restart') (& $st 10 $true)
Check '10+ with only the read failing: hold (alert, no restart)' ((& $st 10 $false) -eq 'hold' -and (& $st 15 $false) -eq 'hold') ''
Check 'restart set at or below the alert: restart when the alert is due' ((Get-ControlCenterStep -Consecutive 3 -AlertAt 3 -RestartAt 2 -WindowHung $true) -eq 'restart') ''
Check 'and still holds without the window' ((Get-ControlCenterStep -Consecutive 3 -AlertAt 3 -RestartAt 2 -WindowHung $false) -eq 'hold') ''

'=== settings'
$s = Get-OverwatchSettings (Get-GuardDefaults)
Check 'controlCenterRestartChecks defaults to 10' ($s.controlCenterRestartChecks -eq 10) "$($s.controlCenterRestartChecks)"
$old = Get-GuardDefaults; $old.thresholds.Remove('controlCenterRestartChecks')
$h = Use-TestHomes ccset
Save-GuardConfig -Config $old -GuardHome $h.Home
$s = Get-OverwatchSettings (Get-GuardConfig -GuardHome $h.Home)
Check 'a guard.json written before 0.9.2 gets the default' ($s.controlCenterRestartChecks -eq 10) "$($s.controlCenterRestartChecks)"

'=== the flow uses the step, and the restore record survives'
$src = Get-OverwatchSource
Check 'the fault flow asks Get-ControlCenterStep' ($src -match "if \(\`$fault -eq 'CONTROL_CENTER_HUNG'\) \{") ''
Check 'with the window hung on consecutiveToAct checks in a row, not one sample' ($src -match '\$windowHung = \(\[int\]\$state\.ccHungStreak -ge \[int\]\$cfg\.consecutiveToAct\)' -and $src -match '-WindowHung \$windowHung') ''
Check 'no pre-kill grid read into a frozen Control Center' ($src -match "if \(\`$responding -and \`$fault -ne 'CONTROL_CENTER_HUNG'\)") ''
Check 'a pending restore does not hide the fault (its block exits before faults are counted)' ($src -match 'if \(\$needsRestore -and \$responding -and \(Test-AddonsFresh\) -and -not \$gridFailing\)') ''
Check 'the feed-episode restore waits for the Control Center too' ($src -match 'elseif \(\$state\.feedEpisodeUtc -and \$responding -and -not \$gridFailing\)') ''
Check 'the streaks are cleared at the kill and in a new NinjaTrader''s startup grace' (([regex]::Matches($src, '\$state\.gridFailStreak = 0; \$state\.ccHungStreak = 0')).Count -eq 2) ''
Check '-Report shows the Control Center and how far the scheduled runs have got' ($src -match 'controlCenterHung = \$ccHung' -and $src -match 'controlCenterHungChecks = \$state\.ccHungStreak') ''

'=== a hold run (only the read failing) is not restarted by a few busy moments of the window'
$state = [ordered]@{ gridFailStreak = 0; ccHungStreak = 0 }
$cfg.consecutiveToAct = 3; $script:MODE = 'timeout'; $steps = @()
# hung on checks 4, 8 and 11 - three samples, never three in a row
$hungPattern = @($false, $false, $false, $true, $false, $false, $false, $true, $false, $false, $true, $false, $false)
$n = 0
foreach ($hung in $hungPattern) {
    $n++; Set-Cc $hung; $state.gridFailStreak = [Math]::Max(1, [int]$state.gridFailStreak)
    [void](Invoke-CcCheck)
    $steps += (Get-ControlCenterStep -Consecutive $n -AlertAt 3 -RestartAt 10 -WindowHung ([int]$state.ccHungStreak -ge [int]$cfg.consecutiveToAct))
}
Check 'alerted at 3, held from 10, never restarted' ($steps[2] -eq 'alert' -and $steps[9] -eq 'hold' -and $steps -notcontains 'restart') ($steps -join ',')
$state = [ordered]@{ gridFailStreak = 1; ccHungStreak = 0 }; $steps = @(); $n = 0
foreach ($i in 1..10) {
    $n++; Set-Cc $true
    [void](Invoke-CcCheck)
    $steps += (Get-ControlCenterStep -Consecutive $n -AlertAt 3 -RestartAt 10 -WindowHung ([int]$state.ccHungStreak -ge [int]$cfg.consecutiveToAct))
}
Check 'THE 10-16 CASE run through: window hung and reads timing out every check -> alert at 3, restart at 10' ($steps[2] -eq 'alert' -and $steps[9] -eq 'restart') ($steps -join ',')

'=== the hourly read'
$state = [ordered]@{ gridFailStreak = 0; ccHungStreak = 0 }
$pollBlock = Get-OverwatchBlock '$pollDue  = (' '# STRATEGY BACKUPS'
Invoke-Expression ("function Invoke-Poll {`n" + $pollBlock + "`n}")
$needsRestore = $false; $cacheAge = $null; $cfg.strategyCacheMinutes = 60
$script:MODE = 'timeout'; Invoke-Poll
Check 'a timed-out periodic read starts the streak (so the next cycle reads again and can fault)' ($state.gridFailStreak -eq 1) "$($state.gridFailStreak)"
$script:MODE = 'ok'; Invoke-Poll
Check 'a good one clears it' ($state.gridFailStreak -eq 0) "$($state.gridFailStreak)"
$script:MODE = 'none'; Invoke-Poll
Check 'no answer from the link does not start it' ($state.gridFailStreak -eq 0) "$($state.gridFailStreak)"
$script:MODE = 'nogrid'; $script:logLines = @(); Invoke-Poll
Check 'nor does a missing grid, and the log says which it was' ($state.gridFailStreak -eq 0 -and ($script:logLines -join ' ') -match 'StrategiesGrid not found') ($script:logLines -join ' | ')
$stateFile = Join-Path (New-TestDir ccstate) 'overwatch.state.json'
Invoke-Expression (Get-OverwatchFunction 'Read-State')
Check 'both streaks are part of the saved state' (@((Read-State).Keys) -contains 'gridFailStreak' -and @((Read-State).Keys) -contains 'ccHungStreak') ''

'=== the control panel no longer says Running while the Control Center is frozen'
$h = Use-TestHomes ccstatus
Save-GuardConfig (Get-GuardDefaults)
$now = [DateTime]::UtcNow
$procs = { , @([pscustomobject]@{ Id = 42; StartUtc = $now.AddMinutes(-30); Responding = $true; Title = 'Account Data - Strategies'; ControlCenterHung = $true }) }
$s = Get-GuardStatus -NowUtc $now -Processes $procs -TaskState { param($n) 'Ready' } -Nt8Version ([version]'8.1.8.3') -TimeZone ([TimeZoneInfo]::Utc)
$c = @($s.Cards | Where-Object { $_.Key -eq 'nt8' })[0]
Check 'NinjaTrader card: bad, Control Center not responding' ($c.State -eq 'bad' -and $c.Headline -eq 'Control Center not responding') "$($c.State) / $($c.Headline)"
$procs = { , @([pscustomobject]@{ Id = 42; StartUtc = $now.AddMinutes(-30); Responding = $true; Title = 'Control Center'; ControlCenterHung = $false }) }
$s = Get-GuardStatus -NowUtc $now -Processes $procs -TaskState { param($n) 'Ready' } -Nt8Version ([version]'8.1.8.3') -TimeZone ([TimeZoneInfo]::Utc)
$c = @($s.Cards | Where-Object { $_.Key -eq 'nt8' })[0]
Check 'and Running when it answers' ($c.Headline -eq 'Running') "$($c.Headline)"

Complete-Test
