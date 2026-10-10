# The buttons that close NinjaTrader (spec 5): Put back now / Bring back, and Test login. Each asks the NT8
# link first and refuses unless you are flat, pauses Guard for its whole length, holds the start lock so no
# watchdog starts NinjaTrader under it, force-closes NinjaTrader (a normal close drops switched-off
# strategies), and ends the pause it set. Driven here with a fake NinjaTrader, a fake NT8 link and a fake
# backup tool - nothing real is closed or started.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
foreach ($m in 'Guard.Status', 'Guard.Nt8Link', 'Guard.Password', 'Guard.Actions') { Import-Module (Join-Path $script:AppDir "lib\$m.psm1") -Force -DisableNameChecking }
$h = Use-TestHomes 'actions'
$now = [DateTime]::UtcNow

# ---- the backup list: Alpha on NinjaTrader's list, Beta missing (dropped across a restart), Gamma removed by you
$rows = @{}
foreach ($r in @(@{ n = 'Alpha'; s = 'live' }, @{ n = 'Beta'; s = 'missing' }, @{ n = 'Gamma'; s = 'removed' })) {
    $rows["$($r.n)@NinjaTrader.NinjaScript.Strategies.DemoStrategy"] = @{ strategies = @{ Id = 1; Name = $r.n; Classname = 'NinjaTrader.NinjaScript.Strategies.DemoStrategy' }
                                                                       state = $r.s; lastSeenUtc = $now.ToString('o'); charts = @() }
}
[IO.File]::WriteAllText((Join-Path (Get-GuardPath backups) 'rows.json'), (@{ version = 2; utc = $now.ToString('o'); rows = $rows } | ConvertTo-Json -Depth 8))

$script:flat = [pscustomobject]@{ State = 'flat'; Safe = $true; Reasons = @(); Text = 'Flat: no position, no working order, no strategy running.' }
$busy = [pscustomobject]@{ State = 'busy'; Safe = $false; Reasons = @('Sim101: Long 2 NQ 12-26'); Text = 'You are not flat: Sim101: Long 2 NQ 12-26.' }
$unknown = [pscustomobject]@{ State = 'unknown'; Safe = $false; Reasons = @(); Text = "Guard can't confirm you're flat: the NT8 link add-on is not installed in NinjaTrader." }

function Reset {
    $script:running = $true; $script:dies = $true; $script:killed = @(); $script:py = @(); $script:steps = @()
    $script:pausedDuringRestore = $null; $script:launched = 0
    $script:report = '{"planned":[],"restored":[{"name":"Beta","id":812,"originalId":746}],"refused":[],"notes":[],"relinked":["Main - NQ 1 min: Beta re-attached"],"relinkFailed":[],"backup":"x"}'
    $script:exit = 0
    Clear-GuardPause
}
$seams = @{
    Flat      = { $script:flat }
    IsRunning = { $script:running }
    Kill      = { param($ids) $script:killed += 'kill'; if ($script:dies) { $script:running = $false } }
    Python    = { param($s, $a, $t)
                  $script:py += , (@($s) + $a)
                  $script:pausedDuringRestore = (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused
                  [pscustomobject]@{ ExitCode = $script:exit; Out = $script:report; Err = ''; TimedOut = $false } }
    Progress  = { param($k, $s, $t) $script:steps += "${k}:$s" }
    Sleep     = { param($ms) }
}
function PutBack([string[]]$Names = @(), $Config = (Get-GuardConfig)) { Invoke-GuardPutBack -Names $Names -Config $Config @seams }

'--- refused before anything happens'
Reset; $script:flat = $busy
$r = PutBack @('Beta')
Check 'not flat: refused, in words' (-not $r.Ok -and $r.Result -eq 'refused' -and $r.Message -match 'Long 2 NQ 12-26') $r.Message
Check 'nothing paused, closed or run' (-not (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused -and $script:killed.Count -eq 0 -and $script:py.Count -eq 0) ''
$script:flat = $unknown
$r = PutBack @('Beta')
Check 'the NT8 link cannot answer: refused' ($r.Result -eq 'refused' -and $r.Message -match "can't confirm you're flat") $r.Message
$script:flat = [pscustomobject]@{ State = 'flat'; Safe = $true; Reasons = @(); Text = 'Flat' }
$r = PutBack @('Alpha')
Check 'a strategy on NinjaTrader''s list right now: refused' ($r.Result -eq 'refused' -and $r.Message -match 'Alpha is on NinjaTrader''s list' -and $script:killed.Count -eq 0) $r.Message
$r = PutBack @('Nobody')
Check 'a name Guard never saved: refused' ($r.Result -eq 'refused' -and $r.Message -match 'Nobody') $r.Message

'--- Put back now: pause, close NinjaTrader hard, put back, resume'
Reset
$r = PutBack @('Beta')
Check 'done' ($r.Ok -and $r.Result -eq 'done') $r.Message
Check 'NinjaTrader force-closed' ($script:killed.Count -eq 1) ''
$a = $script:py[0]
Check 'the backup tool ran with --apply, --json and the name' ($a[0] -eq 'backup_restore.py' -and $a -contains '--apply' -and $a -contains '--json' -and ($a -join ' ') -match '--name Beta' -and $a -notcontains '--no-relink') ($a -join ' ')
Check 'Guard was paused while it ran' ($script:pausedDuringRestore -eq $true) ''
Check 'and is not paused now' (-not (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused) ''
Check 'the start lock is free again' ($null -ne ($l = Open-GuardStartLock)) ''; if ($l) { $l.Dispose() }
Check 'every step reported, in order - paused BEFORE the check' (($script:steps -join ' ') -eq 'pause:running pause:ok check:running check:ok close:running close:ok restore:running restore:ok resume:running resume:ok') ($script:steps -join ' ')
Check 'the message names it, its chart, and what happens next' ($r.Message -match 'Put back: Beta' -and $r.Message -match 're-attached' -and $r.Message -match 'within a minute') $r.Message
Check 'recorded as an event' ([bool](Read-GuardEvents -KindPattern '^backups\.restored$' | Where-Object { $_.text -match 'Beta' })) ''

'--- which strategies'
Reset
$null = PutBack
Check 'no names: every missing one, not the removed one' ((($script:py[0] -join ' ') -match '--name Beta') -and (($script:py[0] -join ' ') -notmatch 'Gamma')) ($script:py[0] -join ' ')
Reset
$null = PutBack @('Gamma')
Check 'Bring back: the one you removed, by name' (($script:py[0] -join ' ') -match '--name Gamma') ($script:py[0] -join ' ')
Reset
$c = Get-GuardConfig; $c.protection.reattachCharts = $false
$null = PutBack @('Beta') $c
Check 'charts left alone when Protection says so' ($script:py[0] -contains '--no-relink') ($script:py[0] -join ' ')

'--- NinjaTrader already closed'
Reset; $script:running = $false
$r = PutBack @('Beta')
Check 'nothing to close' ($r.Ok -and $script:killed.Count -eq 0 -and ($script:steps -join ' ') -match 'close:skipped') ($script:steps -join ' ')

'--- your own pause is yours'
Reset
$null = Set-GuardPause -Minutes 480 -Reason 'my maintenance'
$r = PutBack @('Beta')
$p = Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)
Check 'still paused afterwards, with your reason' ($p.Paused -and $p.Reason -eq 'my maintenance') "$($p.Paused) $($p.Reason)"
Check 'and the message says NinjaTrader waits for it' ($r.Message -match 'paused until') $r.Message

'--- Watch only: nothing starts NinjaTrader again'
Reset
$c = Get-GuardConfig; $c.protection.preset = 'watch'
$r = PutBack @('Beta') $c
Check 'says to start it yourself' ($r.Ok -and $r.Message -match 'Start NinjaTrader yourself') $r.Message

'--- a start already running holds the start lock'
Reset
$held = Open-GuardStartLock
$r = PutBack @('Beta')
$held.Dispose()
Check 'refused, NinjaTrader untouched' ($r.Result -eq 'refused' -and $r.Message -match 'being started right now' -and $script:killed.Count -eq 0) $r.Message
Check 'the pause it set is ended' (-not (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused) ''

'--- NinjaTrader will not close'
Reset; $script:dies = $false
$r = PutBack @('Beta')
Check 'failed, nothing written' ($r.Result -eq 'failed' -and $r.Message -match 'did not close' -and $script:py.Count -eq 0) $r.Message
Check 'pause ended, lock free' (-not (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused -and ($null -ne ($l = Open-GuardStartLock))) ''; if ($l) { $l.Dispose() }

'--- the backup tool refuses one'
Reset
$script:report = '{"planned":[],"restored":[],"refused":[{"name":"Beta","why":"account id 3 no longer exists"}],"notes":[],"relinked":[],"relinkFailed":[],"backup":null}'
$script:exit = 1
$r = PutBack @('Beta')
Check 'not ok, with the reason' (-not $r.Ok -and $r.Message -match 'Beta: account id 3 no longer exists') $r.Message
Check 'the pause still ends' (-not (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused) ''
Reset
$seams.Python = { param($s, $a, $t) [pscustomobject]@{ ExitCode = -1; Out = ''; Err = 'no Python runtime: python\python.exe is missing'; TimedOut = $false } }
$r = PutBack @('Beta')
Check 'the backup tool cannot run: failed, in words' ($r.Result -eq 'failed' -and $r.Message -match 'no Python runtime') $r.Message

'--- the overwatch must not switch back on what you switched off (final review C1)'
$rec = Join-Path (Get-GuardPath state) 'overwatch.strategies.json'
function Set-Record { [IO.File]::WriteAllText($rec, (@{ utc = $now.ToString('o'); why = 'periodic'; enabled = @(@{ name = 'Alpha'; account = 'Sim101'; instrument = 'NQ 12-26' }); emptySeen = 0 } | ConvertTo-Json -Depth 4)) }
Reset; Set-Record
$null = PutBack @('Beta')
$r = Get-Content $rec -Raw | ConvertFrom-Json
Check 'flat through the NT8 link: the overwatch''s record of running strategies is emptied' (@($r.enabled).Count -eq 0 -and $r.why -match 'panel') ($r | ConvertTo-Json -Compress)
Check 'and the trail says why' ((Read-AllText (Join-Path (Get-GuardPath logs) 'overwatch-strategies.log')) -match 'panel.*REMOVED=\[Alpha@Sim101\]') ''
Reset; Set-Record; $script:running = $false
$script:flat = [pscustomobject]@{ State = 'closed'; Safe = $true; Reasons = @(); Text = 'NinjaTrader is not running.' }
$null = PutBack @('Beta')
Check 'NinjaTrader already closed (a crash, say): the record is left for the restart to use' (@((Get-Content $rec -Raw | ConvertFrom-Json).enabled).Count -eq 1) ''
$script:flat = [pscustomobject]@{ State = 'flat'; Safe = $true; Reasons = @(); Text = 'Flat' }

'--- quiet first: no overwatch run, no other request to the NT8 link, no login (final review I1, I4)'
Reset
$seams.Flat = { $script:pausedAtCheck = (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused; $script:flat }
$null = PutBack @('Beta')
Check 'the flat check runs with Guard already paused' ($script:pausedAtCheck -eq $true) ''
$seams.Flat = { $script:flat }
Reset
$ow = [IO.File]::Open((Join-Path (Get-GuardPath state) 'overwatch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$r = Invoke-GuardPutBack -Names @('Beta') -Config (Get-GuardConfig) @seams -QuietSeconds 1
$ow.Dispose()
Check 'an overwatch run still working: refused, nothing closed, the pause ended' ($r.Result -eq 'refused' -and $r.Message -match 'overwatch' -and $script:killed.Count -eq 0 -and -not (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused) $r.Message
Reset
$trig = Join-Path $h.Nt8 'NT8Bridge\trigger'; New-Item -ItemType Directory -Path $trig -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $trig 'strategies_0123.json'), '{"kind":"strategies","enable":"Alpha"}')
$r = Invoke-GuardPutBack -Names @('Beta') -Config (Get-GuardConfig) @seams -QuietSeconds 1
Remove-Item (Join-Path $trig 'strategies_0123.json')
Check 'another program''s request waiting at the NT8 link: refused, nothing closed' ($r.Result -eq 'refused' -and $r.Message -match 'talking to NinjaTrader' -and $script:killed.Count -eq 0) $r.Message
Reset
[IO.File]::WriteAllText((Get-LoginInflightPath), (@{ pid = $PID; startedUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Compress))
$r = PutBack @('Beta')
Check 'a login in progress: refused, NinjaTrader not closed under it' ($r.Result -eq 'refused' -and $r.Message -match 'login is in progress' -and $script:killed.Count -eq 0) $r.Message
Remove-Item (Get-LoginInflightPath)

'--- Test login'
Reset
$script:launchResult = 'ok'
$tl = @{
    Flat      = { $script:flat }
    IsRunning = { $script:running }
    Kill      = { param($ids) $script:killed += 'kill'; $script:running = $false }
    Launch    = { $script:launched++
                  if ($script:launchResult) { Write-GuardEvent -Component login -Kind "login.$(if ($script:launchResult -eq 'ok') { 'ok' } else { 'failed' })" -Severity info -Text "NinjaTrader login: $script:launchResult" -Data @{ result = $script:launchResult } }
                  $null }
    Progress  = { param($k, $s, $t) $script:steps += "${k}:$s" }
    Sleep     = { param($ms) }
}
$r = Invoke-GuardTestLogin -Config (Get-GuardConfig) @tl -WaitSeconds 5
Check 'no password saved: refused before anything' ($r.Result -eq 'refused' -and $r.Message -match 'Save the password first' -and $script:killed.Count -eq 0 -and $script:launched -eq 0) $r.Message
Save-GuardPassword -Password (ConvertTo-SecureString 'not-a-real-password' -AsPlainText -Force)
Reset; $script:running = $false; $script:launchResult = 'ok'
$r = Invoke-GuardTestLogin -Config (Get-GuardConfig) @tl -WaitSeconds 5
Check 'NinjaTrader closed: logs in, ok' ($r.Ok -and $r.Message -match 'logged in' -and $script:launched -eq 1 -and $script:killed.Count -eq 0) $r.Message
Check 'pause ended' (-not (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused) ''
Reset; $script:launchResult = 'gave-up'
$r = Invoke-GuardTestLogin -Config (Get-GuardConfig) @tl -WaitSeconds 5
Check 'running and flat: closed first; a refused password says so' ($script:killed.Count -eq 1 -and -not $r.Ok -and $r.Message -match 'did not log in' -and $r.Message -match 'password') $r.Message
Reset; $script:running = $false; $script:launchResult = $null
$r = Invoke-GuardTestLogin -Config (Get-GuardConfig) @tl -WaitSeconds 1
Check 'no word from the login helper: failed, bounded' (-not $r.Ok -and $r.Message -match 'no answer') $r.Message
Reset; $script:flat = $busy
$r = Invoke-GuardTestLogin -Config (Get-GuardConfig) @tl -WaitSeconds 5
Check 'not flat: refused' ($r.Result -eq 'refused' -and $script:launched -eq 0) $r.Message
$script:flat = [pscustomobject]@{ State = 'flat'; Safe = $true; Reasons = @(); Text = 'Flat' }

'--- the password was refused twice: Test login waits for a new one (final review, re-graded M2)'
Reset; $script:running = $false; $script:launchResult = 'ok'
(Get-Item (Get-GuardCredentialPath)).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(-30)
Write-GuardEvent -Component login -Kind 'login.failed' -Severity alert -Text 'NinjaTrader login failed: gave-up' -Data @{ result = 'gave-up' }
Write-GuardEvent -Component login -Kind 'login.failed' -Severity alert -Text 'NinjaTrader login failed: gave-up' -Data @{ result = 'gave-up' }
$r = Invoke-GuardTestLogin -Config (Get-GuardConfig) @tl -WaitSeconds 5
Check 'refused, nothing started - another wrong password could lock the account' ($r.Result -eq 'refused' -and $r.Message -match 'refused twice' -and $script:launched -eq 0) $r.Message
Save-GuardPassword -Password (ConvertTo-SecureString 'not-a-real-password-2' -AsPlainText -Force)
$r = Invoke-GuardTestLogin -Config (Get-GuardConfig) @tl -WaitSeconds 5
Check 'saved again: allowed' ($r.Ok -and $script:launched -eq 1) $r.Message

'--- Test login with your own login program'
Reset; $script:running = $false; $script:launchResult = $null
$exe = Join-Path (New-TestDir helper) 'NTLogin.exe'; [IO.File]::WriteAllText($exe, 'x')
$c = Get-GuardConfig; $c.login.helper = $exe
$script:title = 'Control Center'
$r = Invoke-GuardTestLogin -Config $c @tl -WaitSeconds 5 -Window { $script:title }
Check 'judged by NinjaTrader''s window leaving Welcome' ($r.Ok -and $r.Message -match 'logged in') $r.Message
Reset; $script:running = $false; $script:launchResult = $null; $script:title = 'Welcome'
$r = Invoke-GuardTestLogin -Config $c @tl -WaitSeconds 1 -Window { $script:title }
Check 'still at Welcome: failed' (-not $r.Ok -and $r.Message -match 'login window') $r.Message

Complete-Test
