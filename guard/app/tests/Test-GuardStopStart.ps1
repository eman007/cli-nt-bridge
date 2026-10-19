# Stop and Start (Tools > Stop Guard / Start Guard): Guard's three tasks switched off and on again, the feed
# guard told to only watch and then given its settings back, one alert each way, and what Status makes of
# it. No real tasks: the scheduler is a fake that keeps each task's state in a hashtable.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
foreach ($m in 'Guard.Status', 'Guard.Nt8Link', 'Guard.Password', 'Guard.Panel', 'Guard.Actions') { Import-Module (Join-Path $script:AppDir "lib\$m.psm1") -Force -DisableNameChecking }
$h = Use-TestHomes 'stopstart'
$topic = 'stop-secret-topic-9'
$cfg = Get-GuardDefaults
$cfg.alerts.ntfyTopic = $topic
Save-GuardConfig $cfg
$cfg = Get-GuardConfig
$fgPath = Get-FeedGuardConfigPath $h.Nt8
$reload = Join-Path $h.Nt8 'AutoReconnect.reload'
$stopFile = Get-GuardStopPath
$state = Get-GuardPath state

function Reset-Tasks([string]$State = 'Ready', [string[]]$Missing = @()) {
    $script:tasks = @{}
    foreach ($c in 'Relaunch', 'Overwatch', 'LogArchive') { if ($Missing -notcontains $c) { $script:tasks["NT8Guard-$c"] = $State } }
    $script:stuck = ''; $script:ran = @(); $script:steps = @()
    $script:sent = New-Object System.Collections.Generic.List[string]
}
$seams = @{
    TaskState      = { param($n) $script:tasks[$n] }          # $null: no such task
    SetTaskEnabled = { param($n, $on) if ($script:stuck -ne $n) { $script:tasks[$n] = $(if ($on) { 'Ready' } else { 'Disabled' }) } }
    Sender         = { param($uri, $headers, $body, $ct) $script:sent.Add("$($headers.Title) | $($headers.Priority) | $([Text.Encoding]::UTF8.GetString($body))") }
    Progress       = { param($k, $s, $t) $script:steps += "${k}:$s" }
}
# Start runs both watchdogs once and waits for their heartbeats: the fake task writes one, like the real.
$beat = { param($n) $script:ran += $n; $k = $(if ($n -like '*Relaunch') { 'relaunch' } else { 'overwatch' }); [IO.File]::WriteAllText((Join-Path $state "$k.heartbeat"), 'x') }
$startSeams = $seams + @{ RunTask = $beat; Sleep = { param($ms) }; WaitSeconds = 5 }
function Read-Fg { return (Read-AllText $fgPath) }
function Status($Tasks) {
    return Get-GuardStatus -Processes { , @() } -TaskState $Tasks -Nt8Version ([version]'8.1.8.3') -TimeZone ([TimeZoneInfo]::Utc)
}

'--- Stop'
Write-FeedGuardConfig -Config $cfg -Nt8Dir $h.Nt8 | Out-Null
Remove-Item $reload -Force
Check 'before: the feed guard reconnects, recovers and pushes' ((Read-Fg) -match '"reconnect": true' -and (Read-Fg) -match '"autoRecover": true' -and (Read-Fg).Contains($topic)) ''
Reset-Tasks
$r = Invoke-GuardStop -Config $cfg @seams
Check 'done' ($r.Ok -and $r.Result -eq 'done') $r.Message
Check 'the three tasks switched off' (@($script:tasks.Values | Where-Object { $_ -ne 'Disabled' }).Count -eq 0 -and $script:tasks.Count -eq 3) (($script:tasks.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')
Check 'its file says when' ((Test-Path $stopFile) -and (ConvertFrom-GuardUtc ([string](Read-AllText $stopFile | ConvertFrom-Json).stoppedUtc))) (Read-AllText $stopFile)
Check 'the feed guard only watches: no reconnect, no recovery, no re-enable' ((Read-Fg) -match '"reconnect": false' -and (Read-Fg) -match '"autoRecover": false' -and (Read-Fg) -match '"reEnableAfterRecover": false') ''
Check 'and pushes nothing' ((Read-Fg) -match '"webhookUrl": ""' -and -not (Read-Fg).Contains($topic)) ''
Check 'the running add-on is told to re-read it' (Test-Path $reload) ''
Check 'one warning alert, saying how to start it again' ($script:sent.Count -eq 1 -and $script:sent[0] -match 'NT8 Guard stopped \| 4 \|' -and $script:sent[0] -match 'Start Guard') (@($script:sent) -join ' // ')
Check 'every step reported' (($script:steps -join ' ') -match 'tasks:ok' -and ($script:steps -join ' ') -match 'feedguard:ok' -and ($script:steps -join ' ') -match 'alert:ok') ($script:steps -join ' ')
Check 'an event says it was stopped' (@(Read-GuardEvents -KindPattern '^guard\.stopped$').Count -eq 1) ''
Check 'the message says what stops and what is kept' ($r.Message -match 'Start Guard' -and $r.Message -match 'kept') $r.Message

'--- Status while stopped'
$s = Status $seams.TaskState
Check 'STOPPED, since when' ($s.Overall.State -eq 'stopped' -and $s.Overall.Text -match '^STOPPED since ' -and $s.Stop.Stopped) $s.Overall.Text
$wd = @($s.Cards | Where-Object Key -eq 'watchdogs')[0]
Check 'the watchdogs card says so, and how to start it' ($wd.State -eq 'off' -and $wd.Headline -match '^Stopped' -and $wd.Remedy -match 'Start Guard') "$($wd.State) $($wd.Headline) / $($wd.Remedy)"
$nt = @($s.Cards | Where-Object Key -eq 'nt8')[0]
Check 'NinjaTrader down: Guard will not start it' ($nt.State -eq 'off' -and $nt.Detail -match 'stopped, so it will not start') $nt.Detail

'--- saving a setting while stopped keeps the feed guard still'
$cfg2 = Get-GuardConfig
$cfg2.watch.staleSeconds = 90
$sv = Save-GuardPanelSettings -Config $cfg2 -Section 'Watch list'
Check 'saved, and it says the feed guard gets it at Start' ($sv.Ok -and $sv.Message -match 'stopped') $sv.Message
Check 'the feed guard file has the new value but still only watches' ((Read-Fg) -match '"watchdogStaleSeconds": 90' -and (Read-Fg) -match '"reconnect": false' -and -not (Read-Fg).Contains($topic)) ''
$cfg = Get-GuardConfig

'--- Start'
$script:steps = @(); $script:sent.Clear(); $script:ran = @()
Remove-Item (Join-Path $state '*.heartbeat') -Force -ErrorAction SilentlyContinue
$r = Invoke-GuardStart -Config $cfg @startSeams
Check 'done' ($r.Ok -and $r.Result -eq 'done') $r.Message
Check 'the three tasks on again' (@($script:tasks.Values | Where-Object { $_ -ne 'Ready' }).Count -eq 0) (($script:tasks.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')
Check 'the stop file is gone' (-not (Test-Path $stopFile)) ''
Check 'the feed guard has your settings back' ((Read-Fg) -match '"reconnect": true' -and (Read-Fg) -match '"autoRecover": true' -and (Read-Fg).Contains($topic) -and (Read-Fg) -match '"watchdogStaleSeconds": 90') ''
Check 'both watchdogs run once, the log keeper not' (($script:ran -join ',') -eq 'NT8Guard-Relaunch,NT8Guard-Overwatch') ($script:ran -join ',')
Check 'and both reported' (($script:steps -join ' ') -match 'check:ok') ($script:steps -join ' ')
Check 'one information alert' ($script:sent.Count -eq 1 -and $script:sent[0] -match 'NT8 Guard started \| 3 \|') (@($script:sent) -join ' // ')
Check 'an event says it was started' (@(Read-GuardEvents -KindPattern '^guard\.started$').Count -eq 1) ''
$s = Status $seams.TaskState
Check 'Status: watching again, nothing left over' (-not $s.Stop.Stopped -and -not $s.Stop.Leftover -and -not @($s.Banner | Where-Object { $_ -match 'Start Guard' }).Count) (@($s.Banner) -join ' | ')

'--- Start: no heartbeat in time is said, not hidden'
Reset-Tasks Disabled
Remove-Item (Join-Path $state '*.heartbeat') -Force -ErrorAction SilentlyContinue
$r = Invoke-GuardStart -Config $cfg @seams -RunTask { param($n) } -Sleep { param($ms) } -WaitSeconds 0
Check 'still done (the tasks are on), the step failed, the message says which' ($r.Ok -and ($script:steps -join ' ') -match 'check:failed' -and $r.Message -match 'relauncher and overwatch') $r.Message
Check 'tasks switched off by hand, with no stop file, are switched on too' (@($script:tasks.Values | Where-Object { $_ -ne 'Ready' }).Count -eq 0) ''

'--- refusals and failures'
Reset-Tasks
$held = Open-GuardStartLock
$r = Invoke-GuardStop -Config $cfg @seams
$held.Dispose()
Check 'a start in progress: refused, nothing switched off' ($r.Result -eq 'refused' -and @($script:tasks.Values | Where-Object { $_ -ne 'Ready' }).Count -eq 0 -and -not (Test-Path $stopFile)) $r.Message

Reset-Tasks
$script:stuck = 'NT8Guard-Overwatch'
$fgBefore = Read-Fg
$r = Invoke-GuardStop -Config $cfg @seams
Check 'a task that will not switch off: failed, the others on again, never half stopped' ($r.Result -eq 'failed' -and @($script:tasks.Values | Where-Object { $_ -ne 'Ready' }).Count -eq 0 -and $r.Message -match 'Overwatch' -and $r.Message -match 'Task Scheduler') $r.Message
Check 'and nothing else changed: no file, no alert, the feed guard as it was' (-not (Test-Path $stopFile) -and $script:sent.Count -eq 0 -and (Read-Fg) -ceq $fgBefore) ''

Reset-Tasks -Missing 'Relaunch', 'Overwatch', 'LogArchive'
$r = Invoke-GuardStop -Config $cfg @seams
Check 'no tasks installed: refused, says to run the setup' ($r.Result -eq 'refused' -and $r.Message -match 'setup' -and -not (Test-Path $stopFile)) $r.Message
$r = Invoke-GuardStart -Config $cfg @startSeams
Check 'Start with no tasks: failed, says to run the setup' ($r.Result -eq 'failed' -and $r.Message -match 'setup') $r.Message

Reset-Tasks -Missing 'LogArchive'
$r = Invoke-GuardStop -Config $cfg @seams
Check 'a missing log keeper does not block a stop, and is named' ($r.Ok -and ($script:steps -join ' ') -match 'tasks:ok' -and (Test-Path $stopFile)) ($script:steps -join ' ')
$r = Invoke-GuardStart -Config $cfg @startSeams
Check 'nor a start, which says to install it' ($r.Ok -and $r.Message -match 'LogArchive is not installed') $r.Message

$quiet = Get-GuardConfig; $quiet.alerts.ntfyTopic = ''
Reset-Tasks
$r = Invoke-GuardStop -Config $quiet @seams
Check 'no alert channel: the alert step is skipped, the stop is done' ($r.Ok -and ($script:steps -join ' ') -match 'alert:skipped' -and $script:sent.Count -eq 0) ($script:steps -join ' ')
$r = Invoke-GuardStart -Config $cfg @startSeams

'--- a watchdog run already going when Stop is pressed stands down'
Reset-Tasks
$r = Invoke-GuardStop -Config $cfg @seams
$since = ConvertFrom-GuardUtc ([string](Read-AllText $stopFile | ConvertFrom-Json).stoppedUtc)
Check 'a run that began before the stop is held' (Test-GuardStopHeld -ProcessStartUtc $since.AddMinutes(-3)) ''
Check 'a run that began after it (a task switched on again) is not' (-not (Test-GuardStopHeld -ProcessStartUtc $since.AddSeconds(5))) ''
Check 'the start gate says stopped to the relauncher and the overwatch (this process began before the stop)' ((Test-Nt8StartAllowed -Component relaunch -Config $cfg) -eq 'stopped' -and (Test-Nt8StartAllowed -Component overwatch -Config $cfg) -eq 'stopped') ''
$ow = Get-Content -Raw (Join-Path $script:AppDir 'Guard-Overwatch.ps1')
$killAt = $ow.IndexOf('Stop-Process -Id $nt.Id -Force')
Check 'the overwatch checks the stop at the top of a run, before a down start, a restore, and twice before a kill' (([regex]::Matches($ow, 'Test-GuardStopHeld')).Count -ge 5 -and $ow.LastIndexOf('Test-GuardStopHeld', $killAt) -gt $ow.IndexOf('Save-NT8Dump $nt.Id')) ''
[IO.File]::WriteAllText($stopFile, '{ not json')
Check 'an unreadable file holds no run (toward protection)' (-not (Test-GuardStopHeld -ProcessStartUtc ([DateTime]::UtcNow.AddHours(-1)))) ''
Remove-Item $stopFile
Check 'no file holds no run' (-not (Test-GuardStopHeld -ProcessStartUtc ([DateTime]::UtcNow.AddHours(-1)))) ''

'--- while stopped, the actions that need Guard running refuse'
[IO.File]::WriteAllText($stopFile, ('{ "stoppedUtc": "' + [DateTime]::UtcNow.ToString('o') + '" }'))
$script:steps = @()
$a = Invoke-GuardPutBack -Config $cfg -Names @('Alpha') -Progress $seams.Progress
$b = Invoke-GuardTestLogin -Config $cfg -Progress $seams.Progress
$c = Invoke-GuardFireDrill -Config $cfg -Progress $seams.Progress
Check 'Put back, Test login and the fire drill: refused, saying to start Guard first' (@($a, $b, $c | Where-Object { $_.Result -eq 'refused' -and $_.Message -match 'Start Guard' }).Count -eq 3) "$($a.Message) | $($b.Message) | $($c.Message)"
Remove-Item $stopFile

'--- Stop: the stop file first, and a rollback only of what Stop switched off'
Reset-Tasks
$script:tasks['NT8Guard-LogArchive'] = 'Disabled'      # switched off by hand before
$script:stuck = 'NT8Guard-Overwatch'
$r = Invoke-GuardStop -Config $cfg @seams
Check 'failed; the relauncher on again, the log keeper still off (it was off before), no stop file' ($r.Result -eq 'failed' -and $script:tasks['NT8Guard-Relaunch'] -eq 'Ready' -and $script:tasks['NT8Guard-LogArchive'] -eq 'Disabled' -and -not (Test-Path $stopFile)) (($script:tasks.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')
Reset-Tasks Disabled
$r = Invoke-GuardStop -Config $cfg @seams
Check 'tasks already off by hand: a stop is still recorded' ($r.Ok -and (Test-Path $stopFile)) $r.Message
$r = Invoke-GuardStart -Config $cfg @startSeams
Reset-Tasks
New-Item -ItemType Directory -Path "$stopFile.tmp" -Force | Out-Null
$r = Invoke-GuardStop -Config $cfg @seams
Check 'the stop file cannot be written: failed, nothing switched off' ($r.Result -eq 'failed' -and $r.Message -match 'Nothing was switched off' -and @($script:tasks.Values | Where-Object { $_ -ne 'Ready' }).Count -eq 0) $r.Message
Remove-Item "$stopFile.tmp" -Force
$script:owLock = [IO.File]::Open((Join-Path $state 'overwatch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$script:steps = @()
$r = Invoke-GuardStop -Config $cfg @seams -OverwatchWaitSeconds 30 -Sleep { param($ms) if ($script:owLock) { $script:owLock.Dispose(); $script:owLock = $null } }
Check 'an overwatch check running: Stop waits for it to stand down, then goes on' ($r.Ok -and ($script:steps -join ' ') -match 'wait:ok' -and $r.Message -notmatch 'still running') "$($script:steps -join ' ') | $($r.Message)"
$r = Invoke-GuardStart -Config $cfg @startSeams
$script:owLock = [IO.File]::Open((Join-Path $state 'overwatch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$script:steps = @()
$r = Invoke-GuardStop -Config $cfg @seams -OverwatchWaitSeconds 0 -Sleep { param($ms) }
$script:owLock.Dispose(); $script:owLock = $null
Check 'still running at the limit: the stop holds, and the result says the check stands down' ($r.Ok -and ($script:steps -join ' ') -match 'wait:failed' -and $r.Message -match 'stands down at its next step') "$($script:steps -join ' ') | $($r.Message)"
$r = Invoke-GuardStart -Config $cfg @startSeams

'--- Start: the stop ends only once the feed guard has your settings back'
Reset-Tasks
$r = Invoke-GuardStop -Config $cfg @seams
$bad = Get-GuardConfig; $bad.watch.instruments = @('NQ "12-26"')
$r = Invoke-GuardStart -Config $bad @startSeams
Check 'the feed guard file cannot be written: the tasks are on, the stop file stays, the message says so' ($r.Ok -and (Test-Path $stopFile) -and @($script:tasks.Values | Where-Object { $_ -ne 'Ready' }).Count -eq 0 -and $r.Message -match 'still only watches') $r.Message
Check 'and Status shows it (a stop left over, Start Guard offered)' ((Status $seams.TaskState).Stop.Leftover -and (Status $seams.TaskState).Stop.CanStart) ''
$r = Invoke-GuardStart -Config $cfg @startSeams
Check 'Start again with good settings ends it' ($r.Ok -and -not (Test-Path $stopFile)) $r.Message
Reset-Tasks -Missing 'Relaunch', 'Overwatch'
$r = Invoke-GuardStart -Config $cfg @startSeams
Check 'only the log keeper installed: the check is skipped, not "both reported"' (($script:steps -join ' ') -match 'check:skipped' -and $r.Message -match 'Neither watchdog') ($script:steps -join ' ')

Check 'the alert address never reaches a log or an event' (-not ((Get-ChildItem (Get-GuardPath logs), (Get-GuardPath events) -File -Recurse | ForEach-Object { Read-AllText $_.FullName }) -join '').Contains($topic)) ''

Complete-Test
