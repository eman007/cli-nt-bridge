# The fire drill (spec 5.1 step 7) and Uninstall (spec 3.1).
# The drill drops AutoReconnect.deafen in NinjaTrader's folder and follows the feed guard's own events: silent,
# recovered, data back. Here a fake feed guard writes those events as the clock moves. Uninstall removes the
# three tasks, the shortcut and Guard's folder (after the panel closes) - never NinjaTrader's files.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
foreach ($m in 'Guard.Status', 'Guard.Nt8Link', 'Guard.Password', 'Guard.Actions') { Import-Module (Join-Path $script:AppDir "lib\$m.psm1") -Force -DisableNameChecking }
$h = Use-TestHomes 'drill'
$hbPath = Join-Path $h.Nt8 'AutoReconnect.heartbeat.json'
$deafPath = Join-Path $h.Nt8 'AutoReconnect.deafen'
$evPath = Get-FeedGuardEventsPath $h.Nt8
New-Item -ItemType Directory -Path (Split-Path $evPath) -Force | Out-Null

function Set-Heartbeat([hashtable]$Over = @{}) {
    $hb = [ordered]@{ component = 'AutoReconnect'; version = '5.0.0'; events = $true; rung = 0; autoRecover = $true
                      scheduledClosed = $false; watchdogStaleSeconds = 75; lastL1AgeSeconds = 1 }
    foreach ($k in $Over.Keys) { $hb[$k] = $Over[$k] }
    [IO.File]::WriteAllText($hbPath, ($hb | ConvertTo-Json))
}
# The fake clock and the fake feed guard: each Sleep moves the clock; at the given seconds after the drill
# starts, the feed guard "writes" an event (and takes the deafen file, as the add-on does on its next tick).
function Reset([object[]]$Script) {
    $script:t0 = [DateTime]::UtcNow
    $script:clock = $script:t0
    $script:plan = [System.Collections.Generic.List[object]]@($Script)
    $script:steps = @()
    $script:running = $true
    $script:flat = [pscustomobject]@{ State = 'flat'; Safe = $true; Reasons = @(); Text = 'Flat' }
    if (Test-Path $evPath) { Remove-Item $evPath -Force }
    if (Test-Path $deafPath) { Remove-Item $deafPath -Force }
    Set-Heartbeat
}
function Emit([string]$Kind, [hashtable]$Data, [DateTime]$Utc) {
    $line = [ordered]@{ utc = $Utc.ToString('o'); component = 'feedguard'; kind = $Kind; severity = 'warn'; text = $Kind; data = $Data } | ConvertTo-Json -Compress
    [IO.File]::AppendAllText($evPath, $line + "`n")
}
$seams = @{
    Flat      = { $script:flat }
    IsRunning = { $script:running }
    Now       = { $script:clock }
    Sleep     = { param($ms)
                  $script:clock = $script:clock.AddMilliseconds($ms)
                  $el = ($script:clock - $script:t0).TotalSeconds
                  if ($el -ge 10 -and (Test-Path $deafPath)) { $script:deafened = [IO.File]::ReadAllText($deafPath); Remove-Item $deafPath -Force }
                  while ($script:plan.Count -and $script:plan[0].At -le $el) {
                      $e = $script:plan[0]; $script:plan.RemoveAt(0)
                      Emit $e.Kind $e.Data $script:t0.AddSeconds($e.At)
                  } }
    Progress  = { param($k, $s, $t) $script:steps += "${k}:$s" }
}
function Drill([hashtable]$Config = (Get-GuardConfig)) { Invoke-GuardFireDrill -Config $Config @seams }

'--- refused before anything happens'
Reset @()
$script:running = $false
$r = Drill
Check 'NinjaTrader not running' ($r.Result -eq 'refused' -and $r.Message -match 'Start NinjaTrader' -and -not (Test-Path $deafPath)) $r.Message
Reset @(); Remove-Item $hbPath
$r = Drill
Check 'no feed guard heartbeat' ($r.Result -eq 'refused' -and $r.Message -match 'feed guard add-on is not running') $r.Message
Reset @(); (Get-Item $hbPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(-10)
$r = Drill
Check 'a stale feed guard' ($r.Result -eq 'refused' -and $r.Message -match 'not running') $r.Message
Reset @(); Set-Heartbeat @{ version = '4.2.0'; events = $false }
$r = Drill
Check 'a feed guard older than 5.0.0 (no events to follow)' ($r.Result -eq 'refused' -and $r.Message -match '5\.0\.0') $r.Message
Reset @(); Set-Heartbeat @{ scheduledClosed = $true }
$r = Drill
Check 'the market is closed' ($r.Result -eq 'refused' -and $r.Message -match 'market is closed' -and -not (Test-Path $deafPath)) $r.Message
Check 'and it names the weekend way: the Simulated Data Feed never closes' ($r.Message -match 'Simulated Data Feed') $r.Message
Check 'and says to wait a few seconds after switching to it (deferred minor)' ($r.Message -match 'few seconds') $r.Message
# Cold test 2026-10-10: a newer NinjaTrader's "Simulation" connection streams real market data and keeps market
# hours; the refusal must say which connection serves the watch list, so the person knows to switch.
Reset @(); Set-Heartbeat @{ scheduledClosed = $true; feedProviders = 'Simulation'; simulatedFeed = $false }
$r = Drill
Check 'it names the connection serving the watch list, and that it keeps market hours' ($r.Result -eq 'refused' -and $r.Message -match 'served by Simulation' -and $r.Message -match 'market hours') $r.Message
Reset @(); Set-Heartbeat @{ rung = 2 }
$r = Drill
Check 'a recovery is already running' ($r.Result -eq 'refused' -and $r.Message -match 'already recovering') $r.Message
Reset @(); $script:flat = [pscustomobject]@{ State = 'busy'; Safe = $false; Reasons = @('x'); Text = 'You are not flat: Sim101: Long 1 NQ 12-26.' }
$r = Drill
Check 'not flat (the disconnect switches strategies off)' ($r.Result -eq 'refused' -and $r.Message -match 'not flat' -and -not (Test-Path $deafPath)) $r.Message

'--- a drill that passes: silent, re-subscribe, disconnect + reconnect, data back'
Reset @(
    @{ At = 85; Kind = 'feed.stall'; Data = @{ seconds = '85' } }
    @{ At = 108; Kind = 'feed.disconnect'; Data = @{ connection = 'Sim Feed' } }
    @{ At = 140; Kind = 'feed.recovered'; Data = @{ rung = '3'; seconds = '55' } }
)
$r = Drill
Check 'passed' ($r.Ok -and $r.Result -eq 'done') $r.Message
Check 'the deafen file asked for the stale time plus a minute' ($script:deafened -eq '135') $script:deafened
Check 'every step reported' (($script:steps -join ' ') -eq 'check:running check:ok deafen:running deafen:ok silent:running silent:ok recover:running recover:ok back:running back:ok') ($script:steps -join ' ')
Check 'the message tells the story' ($r.Message -match 'Passed' -and $r.Message -match '85 s' -and $r.Message -match 'Sim Feed' -and $r.Message -match '140 s' -and $r.Message -match 'phone') $r.Message
Check 'recorded' ([bool](Read-GuardEvents -KindPattern '^drill\.passed$')) ''

'--- the overwatch stays out of the drill (final review C1, I1)'
$rec = Join-Path (Get-GuardPath state) 'overwatch.strategies.json'
[IO.File]::WriteAllText($rec, (@{ utc = [DateTime]::UtcNow.ToString('o'); why = 'periodic'; enabled = @(@{ name = 'Alpha'; account = 'Sim101'; instrument = 'NQ 12-26' }); emptySeen = 0 } | ConvertTo-Json -Depth 4))
Reset @(
    @{ At = 85; Kind = 'feed.stall'; Data = @{} }
    @{ At = 108; Kind = 'feed.disconnect'; Data = @{ connection = 'Sim Feed' } }
    @{ At = 140; Kind = 'feed.recovered'; Data = @{ rung = '3' } }
)
$script:owFree = $null
$plain = $seams.Sleep
$seams.Sleep = { param($ms) if ($null -eq $script:owFree) { try { $f = [IO.File]::Open((Join-Path (Get-GuardPath state) 'overwatch.lock'), 'OpenOrCreate', 'ReadWrite', 'None'); $f.Dispose(); $script:owFree = $true } catch { $script:owFree = $false } }; & $plain $ms }
$r = Drill
$seams.Sleep = $plain
Check 'the overwatch cannot run while the drill does (it would read the silence as a dead feed)' ($r.Ok -and $script:owFree -eq $false) "$($r.Message) owFree=$script:owFree"
Check 'its record of running strategies is emptied - the drill''s disconnect must not switch back on what you switched off' (@((Get-Content $rec -Raw | ConvertFrom-Json).enabled).Count -eq 0) ''
Check 'and the lock is free afterwards' ($null -ne ($l = [IO.File]::Open((Join-Path (Get-GuardPath state) 'overwatch.lock'), 'OpenOrCreate', 'ReadWrite', 'None'))) ''; if ($l) { $l.Dispose() }

'--- a drill that fails'
Reset @()
$r = Drill
Check 'no silence noticed in time: failed, bounded' ($r.Result -eq 'failed' -and $r.Message -match 'did not notice' -and ($script:clock - $script:t0).TotalSeconds -lt 400) "$($r.Message) after $(($script:clock - $script:t0).TotalSeconds)s"
Check 'recorded' ([bool](Read-GuardEvents -KindPattern '^drill\.failed$')) ''
Reset @(
    @{ At = 85; Kind = 'feed.stall'; Data = @{} }
    @{ At = 108; Kind = 'feed.disconnect'; Data = @{ connection = 'Sim Feed' } }
    @{ At = 160; Kind = 'feed.source-outage'; Data = @{} }
)
$r = Drill
Check 'the feed guard gave up: failed, says so' ($r.Result -eq 'failed' -and $r.Message -match 'data did not come back') $r.Message
Reset @(); $seams.Sleep = { param($ms) $script:clock = $script:clock.AddMilliseconds($ms) }   # a feed guard that never reads the file
$r = Drill
Check 'the feed guard never picked the drill up: failed, file taken back' ($r.Result -eq 'failed' -and $r.Message -match 'did not pick' -and -not (Test-Path $deafPath)) $r.Message
$seams.Remove('Sleep')
$seams.Sleep = { param($ms)
                 $script:clock = $script:clock.AddMilliseconds($ms)
                 $el = ($script:clock - $script:t0).TotalSeconds
                 if ($el -ge 10 -and (Test-Path $deafPath)) { $script:deafened = [IO.File]::ReadAllText($deafPath); Remove-Item $deafPath -Force }
                 while ($script:plan.Count -and $script:plan[0].At -le $el) {
                     $e = $script:plan[0]; $script:plan.RemoveAt(0)
                     Emit $e.Kind $e.Data $script:t0.AddSeconds($e.At)
                 } }

'--- Recover a silent feed set to alert only: the alert, then the all-clear'
Reset @(
    @{ At = 80; Kind = 'feed.alert'; Data = @{ key = 'silent-stall' } }
    @{ At = 110; Kind = 'feed.alert-cleared'; Data = @{ key = 'silent-stall' } }
)
Set-Heartbeat @{ autoRecover = $false }
$r = Drill
Check 'passed' ($r.Ok) $r.Message
Check 'a shorter silence is enough' ($script:deafened -eq '105') $script:deafened
Check 'recovery shown as not attempted' (($script:steps -join ' ') -match 'recover:skipped') ($script:steps -join ' ')

'--- Uninstall'
$menu = New-TestDir startmenu
$lnk = Join-Path $menu 'NT8 Guard.lnk'; [IO.File]::WriteAllText($lnk, 'x')
$script:removed = @(); $script:taskGone = $true
$un = @{ StartMenuDir = $menu; RemoveTask = { param($n) $script:removed += $n; $script:taskGone }; Progress = { param($k, $s, $t) $script:steps += "${k}:$s" } }
[IO.File]::WriteAllText((Join-Path $h.Home 'guard.json'), '{}')
$held = Open-GuardStartLock
$r = Invoke-GuardUninstall @un
$held.Dispose()
Check 'a start in progress: refused, nothing removed' ($r.Result -eq 'refused' -and $script:removed.Count -eq 0 -and (Test-Path $lnk)) $r.Message
$script:steps = @()
$r = Invoke-GuardUninstall @un -PanelPid 0
Check 'done' ($r.Ok -and $r.Result -eq 'done') $r.Message
Check 'the three tasks removed' (($script:removed -join ',') -eq 'NT8Guard-Relaunch,NT8Guard-Overwatch,NT8Guard-LogArchive') ($script:removed -join ',')
Check 'the shortcut removed' (-not (Test-Path $lnk)) ''
Check 'a cleanup script for Guard''s folder, not the program files outside it' ($r.CleanupScript -and (Read-AllText $r.CleanupScript).Contains($h.Home) -and -not (Read-AllText $r.CleanupScript).Contains($script:AppDir)) $r.CleanupScript
Check 'the message says what stays in NinjaTrader' ($r.Message -match 'AutoReconnect' -and $r.Message -match 'NT8BridgeServer' -and $r.Message -match 'Guard backup') $r.Message
$out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $r.CleanupScript 2>&1
Check 'the cleanup removes Guard''s folder' (-not (Test-Path $h.Home)) ($out | Out-String)
Check 'and itself' (-not (Test-Path $r.CleanupScript)) ''
Check 'NinjaTrader''s folder untouched' ((Test-Path $hbPath) -and (Test-Path $evPath)) ''

$odd = New-TestDir notguard; [IO.File]::WriteAllText((Join-Path $odd 'my-notes.txt'), 'keep me')
$r = Invoke-GuardUninstall @un -GuardHome $odd -PanelPid 0
Check 'a folder that does not look like Guard''s is not scheduled for removal' ($r.Ok -and -not $r.CleanupScript -and $r.Message -match 'left in place') $r.Message
Check 'never your profile, Documents or a drive root' (-not (Test-GuardRemovable $env:USERPROFILE) -and -not (Test-GuardRemovable ([Environment]::GetFolderPath('MyDocuments'))) -and -not (Test-GuardRemovable 'C:\') -and -not (Test-GuardRemovable ([Environment]::GetFolderPath('LocalApplicationData')))) ''
# The same, able to fail: each protected folder made to look like a Guard home (final review I5).
$fakeProfile = New-TestDir profile; [IO.File]::WriteAllText((Join-Path $fakeProfile 'guard.json'), '{}')
$realProfile = $env:USERPROFILE; $env:USERPROFILE = $fakeProfile
$p1 = Test-GuardRemovable $fakeProfile
$env:USERPROFILE = $realProfile
Check 'the profile, even holding guard.json' (-not $p1) ''
[IO.File]::WriteAllText((Join-Path $h.Nt8 'guard.json'), '{}')
Check 'NinjaTrader''s own folder, even holding guard.json' (-not (Test-GuardRemovable $h.Nt8)) ''
$parent = New-TestDir parent; [IO.File]::WriteAllText((Join-Path $parent 'guard.json'), '{}')
$inside = Join-Path $parent 'NinjaTrader 8'; New-Item -ItemType Directory -Path $inside | Out-Null
$savedNt8 = $env:NT8_DIR; $env:NT8_DIR = $inside
$p2 = Test-GuardRemovable $parent
$env:NT8_DIR = $savedNt8
Check 'a folder that holds NinjaTrader''s folder' (-not $p2) ''
$lookalike = New-TestDir lookalike; [IO.File]::WriteAllText((Join-Path $lookalike 'guard.json'), '{}')
New-Item -ItemType Directory -Path (Join-Path $lookalike 'db') | Out-Null; [IO.File]::WriteAllText((Join-Path $lookalike 'db\NinjaTrader.sqlite'), 'x')
Check 'a folder holding a NinjaTrader database, wherever it is' (-not (Test-GuardRemovable $lookalike)) ''
$script:taskGone = $false
$h2 = New-TestDir home2; [IO.File]::WriteAllText((Join-Path $h2 'guard.json'), '{}')
$r = Invoke-GuardUninstall @un -GuardHome $h2 -PanelPid 0
Check 'a task that will not go: failed, and the folder is kept (the task would run a missing script every minute)' ($r.Result -eq 'failed' -and -not $r.CleanupScript -and $r.Message -match 'Task Scheduler') $r.Message

Complete-Test
