# The setup wizard's logic (spec 5.1), without its window: step 1's checks of this PC, step 3's two add-on
# lights, where the add-ons zip is, remembering the step you are on, and step 7's finish - register the
# tasks (each one proven by its heartbeat), send a test alert, mark setup complete. Plus the password
# remedies now point at the panel, not a command line.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
foreach ($m in 'Guard.Status', 'Guard.Nt8Link', 'Guard.Password', 'Guard.Actions', 'Guard.Setup') { Import-Module (Join-Path $script:AppDir "lib\$m.psm1") -Force -DisableNameChecking }
$h = Use-TestHomes 'setup'
$now = [DateTime]::UtcNow
function Row($rows, $key) { @($rows | Where-Object Key -eq $key)[0] }

'--- step 1: check this PC'
$py = Join-Path (New-TestDir py) 'python.exe'; [IO.File]::WriteAllText($py, 'x')
$cfg = Get-GuardDefaults; $cfg.python.exe = $py
$base = @{ Config = $cfg; Nt8Dir = $h.Nt8; AutoLogon = { $true }; ScalePercent = { 125 }; OtherWatchdogs = { @() } }
$r = Get-GuardSetupCheck @base -Nt8Version ([version]'8.1.8.3')
Check 'six checks, in order' ((@($r | ForEach-Object Key) -join ',') -eq 'nt8version,nt8data,python,scaling,signin,others') (@($r | ForEach-Object Key) -join ',')
Check 'no other watchdog: green' ((Row $r others).State -eq 'ok') (Row $r others).Text
Check 'a tested NinjaTrader is green' ((Row $r nt8version).State -eq 'ok' -and (Row $r nt8version).Text -match '8\.1\.8\.3') (Row $r nt8version).Text
Check 'its folder found' ((Row $r nt8data).State -eq 'ok' -and (Row $r nt8data).Text -match [regex]::Escape($h.Nt8)) (Row $r nt8data).Text
Check 'Python found' ((Row $r python).State -eq 'ok') (Row $r python).Text
Check 'display scaling: fine, and why' ((Row $r scaling).State -eq 'ok' -and (Row $r scaling).Text -match '125%') (Row $r scaling).Text
Check 'Windows signs in by itself: green' ((Row $r signin).State -eq 'ok') (Row $r signin).Text
$r = Get-GuardSetupCheck @base -Nt8Version ([version]'8.1.5.2')
Check 'an older 8.1: amber, untested' ((Row $r nt8version).State -eq 'warn' -and (Row $r nt8version).Text -match 'not tested') (Row $r nt8version).Text
$r = Get-GuardSetupCheck @base -Nt8Version ([version]'8.0.28.0')
Check 'NinjaTrader 8.0: red, unsupported' ((Row $r nt8version).State -eq 'bad' -and (Row $r nt8version).Text -match 'not supported') (Row $r nt8version).Text
$r = Get-GuardSetupCheck @base -Nt8Version $null
Check 'no NinjaTrader: red, says where it looked' ((Row $r nt8version).State -eq 'bad' -and (Row $r nt8version).Text -match 'not found') (Row $r nt8version).Text
$r = Get-GuardSetupCheck -Config $cfg -AutoLogon { $true } -ScalePercent { 125 } -Nt8Version ([version]'8.1.8.3') -Nt8Dir (Join-Path $h.Nt8 'nope') -OtherWatchdogs { @() }
Check 'no NinjaTrader folder: red' ((Row $r nt8data).State -eq 'bad' -and (Row $r nt8data).Text -match 'start NinjaTrader once') (Row $r nt8data).Text
$cfg2 = Get-GuardDefaults; $cfg2.python.exe = (Join-Path $h.Home 'missing\python.exe')
$r = Get-GuardSetupCheck -Config $cfg2 -Nt8Dir $h.Nt8 -AutoLogon { $true } -ScalePercent { 100 } -Nt8Version ([version]'8.1.8.3') -OtherWatchdogs { @() }
Check 'no Python: red, strategy backups cannot work' ((Row $r python).State -eq 'bad' -and (Row $r python).Text -match 'backups') (Row $r python).Text
$r = Get-GuardSetupCheck -Config $cfg -Nt8Dir $h.Nt8 -ScalePercent { 125 } -Nt8Version ([version]'8.1.8.3') -AutoLogon { $false } -OtherWatchdogs { @() }
Check 'a password at Windows startup: amber, with what it means' ((Row $r signin).State -eq 'warn' -and (Row $r signin).Text -match 'sign in' -and (Row $r signin).Text -match 'locked') (Row $r signin).Text

'--- another NinjaTrader watchdog already installed (final review I6)'
$tasks = { , @(
    [pscustomobject]@{ TaskName = 'Old-NT8Relaunch'; State = 'Ready'; Actions = @([pscustomobject]@{ Execute = 'wscript.exe'; Arguments = '"C:\x\NT8-Relaunch-Watchdog-Launcher.vbs"' }) }
    [pscustomobject]@{ TaskName = 'Old-NT8Overwatch'; State = 'Disabled'; Actions = @([pscustomobject]@{ Execute = 'wscript.exe'; Arguments = '"C:\x\NT8-Overwatch-Launcher.vbs"' }) }
    [pscustomobject]@{ TaskName = 'Bridge-Watch'; State = 'Ready'; Actions = @([pscustomobject]@{ Execute = 'python.exe'; Arguments = '-m nt8bridge watchdog' }) }
    [pscustomobject]@{ TaskName = 'Naked-Watch'; State = 'Ready'; Actions = @([pscustomobject]@{ Execute = 'python.exe'; Arguments = '-u C:\x\NT8Bridge\watchdog_detect_loop.py' }) }
    [pscustomobject]@{ TaskName = 'NT8Guard-Relaunch'; State = 'Ready'; Actions = @([pscustomobject]@{ Execute = 'wscript.exe'; Arguments = '"C:\g\app\Guard-Run.vbs" "Guard-Relaunch.ps1"' }) }
    [pscustomobject]@{ TaskName = 'Copy-Guard-Relaunch'; State = 'Ready'; Actions = @([pscustomobject]@{ Execute = 'wscript.exe'; Arguments = '"C:\other\app\Guard-Run.vbs" "Guard-Relaunch.ps1"' }) }) }
$found = @(Get-GuardOtherWatchdogs -Tasks $tasks -Processes { , @() })
Check 'finds the switched-on relaunchers and overwatches of another kind, or of another Guard' (($found -join ',') -eq 'Old-NT8Relaunch,Bridge-Watch,Copy-Guard-Relaunch') ($found -join ',')
$found = @(Get-GuardOtherWatchdogs -Tasks { , @() } -Processes { , @([pscustomobject]@{ ProcessId = 9; CommandLine = 'python.exe -m nt8bridge watchdog --interval 30' }) })
Check 'and a watchdog running by hand' ($found.Count -eq 1 -and $found[0] -match 'nt8bridge watchdog') ($found -join ',')
# P6 desktop 2026-10-10: Finish refused over an Interactive Brokers TWS relauncher - its launcher's name holds
# "Relaunch-Watchdog" - though it never touches NinjaTrader.
$found = @(Get-GuardOtherWatchdogs -Tasks { , @([pscustomobject]@{ TaskName = 'TWS-Relaunch'; State = 'Ready'; Actions = @([pscustomobject]@{ Execute = 'wscript.exe'; Arguments = '"C:\x\IBC\TWS-Relaunch-Watchdog-Launcher.vbs"' }) }) } `
                                   -Processes { , @([pscustomobject]@{ ProcessId = 11; CommandLine = 'powershell.exe -NoProfile -WindowStyle Hidden -File "C:\x\IBC\TWS-Relaunch-Watchdog.ps1"' }) })
Check 'a relauncher for some other program (a TWS one) is not a NinjaTrader watchdog' ($found.Count -eq 0) ($found -join ',')
$r = Get-GuardSetupCheck -Config $cfg -Nt8Dir $h.Nt8 -AutoLogon { $true } -ScalePercent { 100 } -Nt8Version ([version]'8.1.8.3') -OtherWatchdogs { @('Old-NT8Relaunch') }
Check 'step 1 names it, in red, and says what to do' ((Row $r others).State -eq 'bad' -and (Row $r others).Text -match 'Old-NT8Relaunch' -and (Row $r others).Text -match 'Disable') (Row $r others).Text

'--- step 3: the two add-on lights'
function Set-Beat([string]$Path, [int]$AgeSeconds, [string]$Text = '{}') {
    $d = Split-Path $Path -Parent; if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Text); (Get-Item $Path).LastWriteTimeUtc = $now.AddSeconds(-$AgeSeconds)
}
$up = { , @([pscustomobject]@{ Id = 7; StartUtc = $now.AddMinutes(-5); Title = 'Control Center' }) }
$l = Get-GuardAddonLights -Nt8Dir $h.Nt8 -NowUtc $now -Processes { , @() }
Check 'NinjaTrader not running: both waiting, says to start it' ($l.FeedGuard.State -eq 'wait' -and $l.Link.State -eq 'wait' -and -not $l.Ready -and $l.Note -match 'Start NinjaTrader') $l.Note
$l = Get-GuardAddonLights -Nt8Dir $h.Nt8 -NowUtc $now -Processes $up
Check 'running, nothing imported: both waiting' ($l.FeedGuard.State -eq 'wait' -and $l.Link.State -eq 'wait' -and -not $l.Ready) "$($l.FeedGuard.Text) / $($l.Link.Text)"
Set-Beat (Join-Path $h.Nt8 'AutoReconnect.heartbeat.json') 3 '{"version":"4.2.0"}'
Set-Beat (Join-Path $h.Nt8 'NT8Bridge\result\heartbeat.json') 2
$l = Get-GuardAddonLights -Nt8Dir $h.Nt8 -NowUtc $now -Processes $up
Check 'an older feed guard: not green, says to import' ($l.FeedGuard.State -eq 'old' -and $l.FeedGuard.Text -match 'import' -and $l.Link.State -eq 'ok' -and -not $l.Ready) $l.FeedGuard.Text
Set-Beat (Join-Path $h.Nt8 'AutoReconnect.heartbeat.json') 3 '{"version":"5.0.0"}'
$l = Get-GuardAddonLights -Nt8Dir $h.Nt8 -NowUtc $now -Processes $up
Check 'both reporting: both green, ready' ($l.FeedGuard.State -eq 'ok' -and $l.Link.State -eq 'ok' -and $l.Ready) "$($l.FeedGuard.Text) / $($l.Link.Text)"
Set-Beat (Join-Path $h.Nt8 'NT8Bridge\result\heartbeat.json') 900
$l = Get-GuardAddonLights -Nt8Dir $h.Nt8 -NowUtc $now -Processes $up
Check 'a silent NT8 link: not ready' ($l.Link.State -eq 'wait' -and -not $l.Ready) $l.Link.Text
$pkg = New-TestDir pkg; [IO.File]::WriteAllText((Join-Path $pkg 'NT8Guard-AddOns.zip'), 'zip')
Check 'installed: the zip beside app\' ((Get-GuardAddOnsZip -Root $pkg) -eq (Join-Path $pkg 'NT8Guard-AddOns.zip')) "$(Get-GuardAddOnsZip -Root $pkg)"
$repo = New-TestDir repo; New-Item -ItemType Directory -Path (Join-Path $repo 'build\out') | Out-Null
[IO.File]::WriteAllText((Join-Path $repo 'build\out\NT8Guard-AddOns.zip'), 'zip')
Check 'in the repository: the build output' ((Get-GuardAddOnsZip -Root $repo) -eq (Join-Path $repo 'build\out\NT8Guard-AddOns.zip')) "$(Get-GuardAddOnsZip -Root $repo)"
Check 'not built: nothing' ($null -eq (Get-GuardAddOnsZip -Root (New-TestDir nozip))) ''

'--- the step you are on is remembered'
Save-GuardConfig (Get-GuardDefaults)
Check 'a fresh install starts at step 1, not complete' ((Get-GuardSetupState).Step -eq 1 -and -not (Get-GuardSetupState).Completed) ''
Check 'moving on is saved' ((Set-GuardSetupStep -Step 4) -and (Get-GuardConfig).setup.step -eq 4) ''
$null = Set-GuardSetupStep -Step 7 -Completed
Check 'and finishing' ((Get-GuardSetupState).Completed -and (Get-GuardConfig).setup.completed -eq $true) ''

'--- step 7: finish'
Save-GuardConfig (Get-GuardDefaults)
$script:reg = @(); $script:alerted = 0; $script:steps = @()
$fin = @{ OtherWatchdogs = { @() }
          Register = { param($c) $script:reg += $c; [pscustomobject]@{ Ok = $true; Text = "$c registered, heartbeat ok" } }
          TestAlert = { $script:alerted++; [pscustomobject]@{ ok = $true; message = 'sent' } }
          Progress = { param($k, $s, $t) $script:steps += "${k}:$s" } }
$r = Invoke-GuardSetupFinish -Config (Get-GuardConfig) @fin
Check 'registers the three tasks, in order' (($script:reg -join ',') -eq 'relaunch,overwatch,logarchive') ($script:reg -join ',')
Check 'no alert channel set: the test alert is skipped, not failed' ($script:alerted -eq 0 -and ($script:steps -join ' ') -match 'alert:skipped') ($script:steps -join ' ')
Check 'setup marked complete' ($r.Ok -and (Get-GuardSetupState).Completed) $r.Message
Check 'the message says what is next' ($r.Message -match 'fire drill') $r.Message
$c = Get-GuardDefaults; $c.alerts.ntfyTopic = 'nt8guard-abcdefghijklmnopqrstuv'; Save-GuardConfig $c
$script:reg = @(); $script:alerted = 0; $script:steps = @()
$r = Invoke-GuardSetupFinish -Config (Get-GuardConfig) @fin
Check 'with a topic: the test alert is sent' ($script:alerted -eq 1 -and ($script:steps -join ' ') -match 'alert:ok') ($script:steps -join ' ')
Save-GuardConfig (Get-GuardDefaults)
$fin.Register = { param($c) $script:reg += $c; [pscustomobject]@{ Ok = ($c -ne 'overwatch'); Text = "$(if ($c -eq 'overwatch') { 'no heartbeat after 90s' } else { 'ok' })" } }
$script:reg = @(); $script:steps = @()
$r = Invoke-GuardSetupFinish -Config (Get-GuardConfig) @fin
Check 'a task that does not run: failed, named, setup NOT complete' (-not $r.Ok -and $r.Message -match 'overwatch' -and $r.Message -match 'no heartbeat' -and -not (Get-GuardSetupState).Completed) $r.Message
Check 'the others are still tried' (($script:reg -join ',') -eq 'relaunch,overwatch,logarchive') ($script:reg -join ',')
$fin.Register = { param($c) $script:reg += $c; [pscustomobject]@{ Ok = $true; Text = 'ok' } }
$fin.OtherWatchdogs = { @('Old-NT8Relaunch') }
$script:reg = @()
$r = Invoke-GuardSetupFinish -Config (Get-GuardConfig) @fin
Check 'another watchdog still switched on: nothing installed, it is named (two would restart NinjaTrader twice)' (-not $r.Ok -and $r.Message -match 'Old-NT8Relaunch' -and $script:reg.Count -eq 0 -and -not (Get-GuardSetupState).Completed) $r.Message

'--- the password remedies point at the control panel'
$s = Get-GuardStatus -GuardHome $h.Home -Nt8Dir $h.Nt8 -Processes { , @() } -TaskState { param($n) 'Ready' } -Nt8Version ([version]'8.1.8.3') -NowUtc $now
$login = @($s.Cards | Where-Object Key -eq 'login')[0]
Check 'no password saved: "NinjaTrader login" in NT8 Guard''s Tools' ($login.Remedy -match 'Tools' -and $login.Remedy -match 'NinjaTrader login' -and $login.Remedy -notmatch 'SetPassword') $login.Remedy

Complete-Test
