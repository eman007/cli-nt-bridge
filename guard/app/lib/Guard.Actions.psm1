# The control panel's buttons that touch NinjaTrader itself (spec 5): Put back now / Bring back, Test login.
# Every one of them:
#   * asks the NT8 link first (Get-Nt8FlatState) and refuses unless NinjaTrader is closed, at its login
#     window, or flat - and refuses when the link cannot answer ("can't confirm you're flat");
#   * pauses Guard for its whole length (both watchdogs only watch) and holds the start lock, so neither
#     watchdog starts NinjaTrader - or saves the strategy backups - in the middle of it; the pause it set
#     ends with it, a pause you set yourself is left alone;
#   * closes NinjaTrader by FORCE when it must close it: a normal close drops switched-off strategies from
#     NinjaTrader's list - the very thing Put back now repairs - and every strategy is off once the flat
#     check has passed;
#   * reports each step as it goes (-Progress { param($Key, $State, $Text) }, State running | ok | failed |
#     skipped) and returns @{ Ok; Result = done | refused | failed; Message } - Message is what the panel
#     shows at the end.
# Everything outside Guard's own files is a parameter, so the tests drive it with a fake NinjaTrader.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Log.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Config.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Events.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Pause.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Start.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Status.psm1')     # Get-GuardBackupList, Get-GuardPanelPause, Format-GuardTime
Import-Module (Join-Path $PSScriptRoot 'Guard.Nt8Link.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Password.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Alerts.psm1')       # Send-GuardAlert (Stop, Start)
Import-Module (Join-Path $PSScriptRoot 'Guard.FeedGuard.psm1')    # Write-FeedGuardConfig (Stop, Start)

# The steps each action reports, in order - the panel lists them before the action starts.
$script:Steps = @{
    putback   = [ordered]@{ pause = 'Pause Guard'; check = 'Check you are flat'; close = 'Close NinjaTrader'
                            restore = 'Put the strategies back, on their charts'; resume = 'End the pause' }
    testlogin = [ordered]@{ pause = 'Pause Guard'; check = 'Check you are flat'; close = 'Close NinjaTrader'
                            login = 'Start NinjaTrader and log in'; resume = 'End the pause' }
    drill     = [ordered]@{ check = 'Check the feed guard, the market and that you are flat'; deafen = 'Silence the market data (a drill)'
                            silent = 'The feed guard notices'; recover = 'It reconnects the connection serving the watch list'
                            back = 'Market data is back' }
    uninstall = [ordered]@{ tasks = 'Remove the scheduled tasks'; shortcut = 'Remove the Start-menu shortcut'
                            files = 'Remove Guard''s folder (when this window closes)' }
    stop      = [ordered]@{ tasks = 'Switch Guard''s scheduled tasks off'; wait = 'Let a running overwatch check stand down'
                            feedguard = 'Feed guard: watch only (no reconnect, no recovery, no alert)'
                            alert = 'Tell your phone Guard is stopped' }
    start     = [ordered]@{ tasks = 'Switch Guard''s scheduled tasks on'; feedguard = 'Feed guard: back to your settings'
                            check = 'Run both watchdogs once'; alert = 'Tell your phone Guard is watching again' }
    # setup's last step (Guard.Setup, Invoke-GuardSetupFinish)
    finish    = [ordered]@{ relaunch = 'Install the relauncher (starts NinjaTrader when it is down)'
                            overwatch = 'Install the overwatch (restarts NinjaTrader when it is sick)'
                            logarchive = 'Install the log keeper'; alert = 'Send a test alert'; done = 'Mark setup complete' }
}

function Get-GuardActionSteps {
    # @( @{ Key; Text } ) for one action, in the order it runs them.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Action)
    return @($script:Steps[$Action].GetEnumerator() | ForEach-Object { [pscustomobject]@{ Key = $_.Key; Text = $_.Value } })
}

function New-GuardActionResult([bool]$Ok, [string]$Result, [string]$Message) {
    return [pscustomobject]@{ Ok = $Ok; Result = $Result; Message = $Message }
}

function Stop-Nt8Hard {
    # Force-close NinjaTrader in this Windows session and wait until it is gone. $true when it is.
    [CmdletBinding()]
    param([string]$Why, [string]$GuardHome, [int]$WaitSeconds = 30, [scriptblock]$IsRunning, [scriptblock]$Kill, [scriptblock]$Sleep)
    if (-not $IsRunning) { $IsRunning = { [bool](@(Get-GuardSessionProcess -Name NinjaTrader).Count) } }
    if (-not $Kill) { $Kill = { foreach ($p in @(Get-GuardSessionProcess -Name NinjaTrader)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } } }
    if (-not $Sleep) { $Sleep = { param($ms) Start-Sleep -Milliseconds $ms } }
    Write-GuardLog -Component panel -Message "closing NinjaTrader by force ($Why)" -GuardHome $GuardHome
    Write-GuardEvent -Component panel -Kind 'nt8.closed' -Severity warn -Text "Closed NinjaTrader to $Why" -GuardHome $GuardHome
    & $Kill
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    while (& $IsRunning) {
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        & $Sleep 500
    }
    return $true
}

function Enter-GuardActionPause {
    # Pause Guard for the action - unless you paused it yourself, which is then left as it is.
    # Returns the pause it set (to end later), or $null.
    param([string]$GuardHome, [string]$Nt8Dir, [string]$Reason, [int]$Minutes, [scriptblock]$Report)
    $mine = Get-GuardPanelPause -GuardHome $GuardHome -Nt8Dir $Nt8Dir -NowUtc ([DateTime]::UtcNow)
    & $Report pause running ''
    if ($mine.Paused) {
        & $Report pause skipped "Guard is already paused until $(Format-GuardTime -Utc $mine.UntilUtc -NowUtc ([DateTime]::UtcNow)); that pause is yours and stays."
        return $null
    }
    $p = Set-GuardPause -Minutes $Minutes -Reason $Reason -GuardHome $GuardHome
    & $Report pause ok "Both watchdogs only watch for up to $Minutes min."
    return $p
}

function Exit-GuardActionPause {
    # End the pause the action set - only that one: if pause.json now holds another, it stays.
    param($Pause, [string]$GuardHome, [scriptblock]$Report)
    if (-not $Pause) { return }
    & $Report resume running ''
    $f = Get-GuardPausePath $GuardHome
    $now = $null
    try { $now = [IO.File]::ReadAllText($f) | ConvertFrom-Json } catch { }
    if ($now -and [string]$now.setUtc -eq [string]$Pause.setUtc) { Clear-GuardPause -GuardHome $GuardHome }
    & $Report resume ok 'Guard is watching again.'
}

function Get-GuardStopRefusal {
    # Put back, Bring back, Test login and the fire drill count on Guard's watchdogs - or the feed guard's
    # recovery - to finish what they start, so none runs while Guard is stopped, or only half started again
    # (the stop file outlived a task switched on outside the panel). '' when they may run.
    param([string]$GuardHome)
    if (Test-Path -LiteralPath (Get-GuardStopPath $GuardHome)) { return 'Guard is stopped. Press Start Guard (Tools) first, then try again.' }
    return ''
}

function Get-GuardRestartNote {
    # What happens to NinjaTrader after an action closed it.
    param([hashtable]$Config, [string]$GuardHome, [string]$Nt8Dir)
    $p = Get-GuardPanelPause -GuardHome $GuardHome -Nt8Dir $Nt8Dir -NowUtc ([DateTime]::UtcNow)
    if ($p.Paused) { return "Guard is paused until $(Format-GuardTime -Utc $p.UntilUtc -NowUtc ([DateTime]::UtcNow)), so it starts NinjaTrader after that - or start it yourself." }
    if (-not (Get-GuardProtection $Config).relaunch) { return 'Start NinjaTrader yourself: Protection is Watch only, so Guard does not start it.' }
    return 'Guard starts NinjaTrader again within a minute.'
}

function Enter-GuardActionQuiet {
    # Before the flat check: nothing may switch a strategy on between the check and the action. So the
    # overwatch must not be mid-run (its run lock is taken and HELD - its next runs leave at once), no
    # other program may have a request waiting at the NT8 link (an enable queued there could land after the
    # check), and no login may be in progress (closing NinjaTrader under it). Waits up to -Seconds for the
    # first two. Returns @{ Lock = the overwatch's run lock (Dispose it at the end); Why = '' } or @{ Lock =
    # $null; Why = the refusal }.
    param([hashtable]$Config, [string]$GuardHome, [string]$Nt8Dir, [int]$Seconds = 60)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $path = Join-Path (Get-GuardPath state $GuardHome) 'overwatch.lock'
    $lock = $null
    while (-not $lock) {
        try { $lock = [IO.File]::Open($path, 'OpenOrCreate', 'ReadWrite', 'None') } catch { }
        if ($lock) { break }
        if ([DateTime]::UtcNow -ge $deadline) { return @{ Lock = $null; Why = 'The overwatch is in the middle of a check (it can switch strategies on). Try again in a minute.' } }
        Start-Sleep -Milliseconds 250
    }
    $trig = [IO.Path]::Combine((Get-Nt8DataDir $Nt8Dir), 'NT8Bridge\trigger')
    while (($n = @(Get-ChildItem -LiteralPath $trig -Filter '*.json' -ErrorAction SilentlyContinue).Count)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            $lock.Dispose()
            return @{ Lock = $null; Why = "Another program is talking to NinjaTrader right now ($n request(s) waiting at the NT8 link). Try again in a minute." }
        }
        Start-Sleep -Milliseconds 250
    }
    if (Test-LoginInFlight -Config $Config -GuardHome $GuardHome) {
        $lock.Dispose()
        return @{ Lock = $null; Why = 'A NinjaTrader login is in progress. Try again once it has finished.' }
    }
    return @{ Lock = $lock; Why = '' }
}

function Clear-OverwatchStrategyRecord {
    # After a restart or a feed recovery the overwatch switches back on the strategies its record says were
    # running (Protection: Switch strategies back on), or pages that they are off. The flat check has just
    # proved through the NT8 link that none is running, so whatever the record holds was switched off on
    # purpose. Write the record empty - the overwatch's own file, while its run lock is held - and say why in
    # its trail.
    param([string]$GuardHome, [string]$Why)
    $f = Join-Path (Get-GuardPath state $GuardHome) 'overwatch.strategies.json'
    $before = @()
    if (Test-Path -LiteralPath $f) {
        try { $before = @((Get-Content -LiteralPath $f -Raw | ConvertFrom-Json).enabled | Where-Object { $_ } | ForEach-Object { if ($_ -is [string]) { "$_@" } else { "$($_.name)@$($_.account)" } }) } catch { }
    }
    $now = [DateTime]::UtcNow.ToString('o')
    try { ([ordered]@{ utc = $now; why = "panel: $Why"; enabled = @(); emptySeen = 0 } | ConvertTo-Json -Depth 4) | Out-File -FilePath $f -Encoding utf8 } catch { }
    $line = "$now  [panel: $Why] enabled=[]" + $(if ($before.Count) { "  REMOVED=[$($before -join ', ')]" } else { '' })
    try { $line | Out-File -FilePath (Join-Path (Get-GuardPath logs $GuardHome) 'overwatch-strategies.log') -Append -Encoding utf8 } catch { }
}

function Test-GuardLoginRefused {
    # The start rule for a wrong password, read only (Test-Nt8StartAllowed itself writes files and alerts):
    # the last two logins both left NinjaTrader at its login window after the password went in, and the
    # password has not been saved since. Another try could lock or flag the account.
    param([string]$GuardHome, [string]$Nt8Dir)
    $last = @(Read-GuardEvents -GuardHome $GuardHome -Nt8Dir $Nt8Dir -Max 2 -KindPattern '^login\.(ok|failed)$')
    if ($last.Count -lt 2 -or @($last | Where-Object { $_.kind -eq 'login.failed' -and [string]$_.data.result -eq 'gave-up' }).Count -lt 2) { return $false }
    $failUtc = ConvertFrom-GuardUtc ([string]$last[0].utc)
    $saved = Get-GuardPasswordSavedUtc -GuardHome $GuardHome
    return (-not ($saved -and $failUtc -and $saved -gt $failUtc))
}

function Invoke-GuardPutBack {
    # Put back now (strategies NinjaTrader dropped across a restart) and Bring back (one you removed): the
    # 2026-10-06 procedure as one button. -Names empty = every strategy the backup list shows as missing.
    # Seams: -Flat { -> Get-Nt8FlatState result }, -IsRunning, -Kill, -Python { param($Script, $Args, $Timeout) },
    # -Sleep, -Progress.
    [CmdletBinding()]
    param([string[]]$Names = @(), [Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [string]$Nt8Dir,
          [scriptblock]$Flat, [scriptblock]$IsRunning, [scriptblock]$Kill, [scriptblock]$Python, [scriptblock]$Sleep,
          [scriptblock]$Progress, [int]$QuietSeconds = 60)
    $nt8 = Get-Nt8DataDir $(if ($Nt8Dir) { $Nt8Dir } else { [string]$Config.nt8.dataDir })
    $report = { param($k, $s, $t) if ($Progress) { & $Progress $k $s $t } }
    if (-not $Flat) { $Flat = { Get-Nt8FlatState -Config $Config -Nt8Dir $nt8 -GuardHome $GuardHome } }
    if (-not $IsRunning) { $IsRunning = { [bool](@(Get-GuardSessionProcess -Name NinjaTrader).Count) } }
    if (-not $Python) { $Python = { param($s, $a, $t) Invoke-GuardPython -Script $s -Arguments $a -TimeoutSeconds $t -Config $Config } }
    $stopped = Get-GuardStopRefusal $GuardHome
    if ($stopped) { return (New-GuardActionResult $false refused $stopped) }

    # Which ones - decided from the backup list before anything is touched, so a name that is on
    # NinjaTrader's list right now never costs a NinjaTrader restart.
    $list = Get-GuardBackupList -GuardHome $GuardHome
    $rows = @($list.Rows)
    if (-not @($Names).Count) {
        $Names = @($rows | Where-Object State -eq 'missing' | ForEach-Object Name | Select-Object -Unique)
        if (-not $Names.Count) { return (New-GuardActionResult $false refused 'Nothing is missing: every strategy Guard saved is on NinjaTrader''s list.') }
    }
    foreach ($n in $Names) {
        $mine = @($rows | Where-Object Name -eq $n)
        if (-not $mine.Count) { return (New-GuardActionResult $false refused "Guard has no backup of $n.") }
        if (@($mine | Where-Object State -ne 'live').Count -eq 0) { return (New-GuardActionResult $false refused "$n is on NinjaTrader's list right now - nothing to put back.") }
    }

    # Pause, hold the start lock, quiet the overwatch and the NT8 link - THEN check, then act: nothing may
    # switch a strategy on between the check and the force-close.
    $pause = Enter-GuardActionPause -GuardHome $GuardHome -Nt8Dir $nt8 -Reason 'putting strategies back (control panel)' -Minutes 30 -Report $report
    try {
        & $report check running ''
        $lock = Open-GuardStartLock $GuardHome
        if (-not $lock) {
            & $report check failed 'Busy'
            return (New-GuardActionResult $false refused 'NinjaTrader is being started right now, or the strategy backups are being saved. Try again in a minute.')
        }
        $quiet = $null
        try {
            $quiet = Enter-GuardActionQuiet -Config $Config -GuardHome $GuardHome -Nt8Dir $nt8 -Seconds $QuietSeconds
            if ($quiet.Why) { & $report check failed $quiet.Why; return (New-GuardActionResult $false refused $quiet.Why) }
            $f = & $Flat
            if (-not $f.Safe) { & $report check failed $f.Text; return (New-GuardActionResult $false refused $f.Text) }
            if ($f.State -eq 'flat') { Clear-OverwatchStrategyRecord -GuardHome $GuardHome -Why 'put back now (strategies switched off to be flat stay off)' }
            & $report check ok $f.Text

            & $report close running ''
            if (& $IsRunning) {
                if (-not (Stop-Nt8Hard -Why 'put strategies back' -GuardHome $GuardHome -IsRunning $IsRunning -Kill $Kill -Sleep $Sleep)) {
                    & $report close failed 'NinjaTrader did not close.'
                    return (New-GuardActionResult $false failed 'NinjaTrader did not close within 30 seconds, so nothing was put back. Close it from Task Manager and try again.')
                }
                [void](Wait-Nt8ConfigUnlocked -Nt8Dir $nt8 -Seconds 20)
                & $report close ok 'Closed.'
            } else { & $report close skipped 'NinjaTrader was not running.' }

            & $report restore running ''
            $pyArgs = @('--nt8-dir', $nt8, '--guard-home', (Get-GuardHome $GuardHome), '--apply', '--json')
            foreach ($n in $Names) { $pyArgs += @('--name', $n) }
            if (-not (Get-GuardProtection $Config).reattachCharts) { $pyArgs += '--no-relink' }
            $r = & $Python 'backup_restore.py' $pyArgs 120
            $rep = $null
            if ($r.ExitCode -ge 0 -and $r.Out) { try { $rep = $r.Out | ConvertFrom-Json } catch { } }
            if ($null -eq $rep) {
                $why = ([string]$r.Err).Trim(); if (-not $why) { $why = "exit code $($r.ExitCode)" }
                & $report restore failed $why
                Write-GuardEvent -Component panel -Kind 'backups.restore-failed' -Severity warn -Text "Put back now could not run: $why" -GuardHome $GuardHome
                return (New-GuardActionResult $false failed "Nothing was put back: $why")
            }
            $done = @($rep.restored); $refused = @($rep.refused); $relinked = @($rep.relinked); $relinkFailed = @($rep.relinkFailed)
            $parts = New-Object System.Collections.Generic.List[string]
            if ($done.Count) {
                $names_ = ($done | ForEach-Object { "$($_.name) (id $($_.id))" }) -join ', '
                $parts.Add("Put back: $names_.")
                Write-GuardEvent -Component panel -Kind 'backups.restored' -Severity info -Text "Put back by hand: $names_" -GuardHome $GuardHome
            }
            if ($relinked.Count) { $parts.Add("Charts: $($relinked -join '; ').") }
            if ($refused.Count) {
                $parts.Add('Not put back: ' + (($refused | ForEach-Object { "$($_.name): $($_.why)" }) -join '; ') + '.')
                Write-GuardEvent -Component panel -Kind 'backups.restore-refused' -Severity warn -Text ('Not put back: ' + (($refused | ForEach-Object { "$($_.name): $($_.why)" }) -join '; ')) -GuardHome $GuardHome
            }
            if ($relinkFailed.Count) { $parts.Add("Not back on a chart: $($relinkFailed -join '; ').") }
            if (-not $done.Count -and -not $refused.Count) { $parts.Add('Nothing needed putting back: NinjaTrader''s list already had them.') }
            $ok = (-not $refused.Count -and -not $relinkFailed.Count)
            & $report restore $(if ($ok) { 'ok' } else { 'failed' }) ($parts -join ' ')
            $msg = $parts -join ' '
        } finally { if ($quiet -and $quiet.Lock) { $quiet.Lock.Dispose() }; $lock.Dispose() }
    } finally { Exit-GuardActionPause -Pause $pause -GuardHome $GuardHome -Report $report }
    return (New-GuardActionResult $ok $(if ($ok) { 'done' } else { 'failed' }) "$msg $(Get-GuardRestartNote -Config $Config -GuardHome $GuardHome -Nt8Dir $nt8)")
}

function Invoke-GuardTestLogin {
    # Test login (spec 5.1 step 2): start NinjaTrader through the login helper and watch it log in - the
    # path every restart takes. With NinjaTrader running (and flat) it is force-closed first.
    # Built-in helper: judged by its own login.ok / login.failed event. Your own login program reports
    # nothing, so it is judged by NinjaTrader's window leaving 'Welcome'.
    # Seams: -Flat, -IsRunning, -Kill, -Launch { -> the started process or $null }, -Window { -> NinjaTrader's
    # window title or $null }, -Sleep, -Progress.
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [string]$Nt8Dir, [int]$WaitSeconds = 480,
          [scriptblock]$Flat, [scriptblock]$IsRunning, [scriptblock]$Kill, [scriptblock]$Launch, [scriptblock]$Window,
          [scriptblock]$Sleep, [scriptblock]$Progress, [int]$QuietSeconds = 60)
    $nt8 = Get-Nt8DataDir $(if ($Nt8Dir) { $Nt8Dir } else { [string]$Config.nt8.dataDir })
    $report = { param($k, $s, $t) if ($Progress) { & $Progress $k $s $t } }
    if (-not $Flat) { $Flat = { Get-Nt8FlatState -Config $Config -Nt8Dir $nt8 -GuardHome $GuardHome } }
    if (-not $IsRunning) { $IsRunning = { [bool](@(Get-GuardSessionProcess -Name NinjaTrader).Count) } }
    if (-not $Launch) { $Launch = { Start-GuardLogin -Config $Config -GuardHome $GuardHome -Component panel } }
    if (-not $Window) {
        $Window = { $p = @(Get-GuardSessionProcess -Name NinjaTrader) | Select-Object -First 1
                    if (-not $p) { return $null }
                    $p.Refresh(); return $p.MainWindowTitle }
    }
    if (-not $Sleep) { $Sleep = { param($ms) Start-Sleep -Milliseconds $ms } }
    $stopped = Get-GuardStopRefusal $GuardHome
    if ($stopped) { return (New-GuardActionResult $false refused $stopped) }

    $helper = [string]$Config.login.helper
    $external = $helper -and $helper -ne 'builtin'
    if ($external -and -not (Test-Path -LiteralPath $helper)) { return (New-GuardActionResult $false refused "Your login program is not there any more ($helper). Choose it again, or use Guard's own login.") }
    if (-not $external -and -not (Test-GuardPassword $GuardHome)) { return (New-GuardActionResult $false refused 'Save the password first: Guard has none to type.') }
    if (-not $external -and (Test-GuardLoginRefused -GuardHome $GuardHome -Nt8Dir $nt8)) {
        return (New-GuardActionResult $false refused ('The saved password was refused twice in a row, so Guard will not try it again: another wrong password can lock or flag ' +
                                                      'your NinjaTrader account. Save the password again first (NinjaTrader login), then test.'))
    }

    $pause = Enter-GuardActionPause -GuardHome $GuardHome -Nt8Dir $nt8 -Reason 'testing the login (control panel)' -Minutes 15 -Report $report
    try {
        & $report check running ''
        $lock = Open-GuardStartLock $GuardHome
        if (-not $lock) {
            & $report check failed 'Busy'
            return (New-GuardActionResult $false refused 'NinjaTrader is being started right now, or the strategy backups are being saved. Try again in a minute.')
        }
        $quiet = $null
        try {
            $quiet = Enter-GuardActionQuiet -Config $Config -GuardHome $GuardHome -Nt8Dir $nt8 -Seconds $QuietSeconds
            if ($quiet.Why) { & $report check failed $quiet.Why; return (New-GuardActionResult $false refused $quiet.Why) }
            $f = & $Flat
            if (-not $f.Safe) { & $report check failed $f.Text; return (New-GuardActionResult $false refused $f.Text) }
            if ($f.State -eq 'flat') { Clear-OverwatchStrategyRecord -GuardHome $GuardHome -Why 'test login (strategies switched off to be flat stay off)' }
            & $report check ok $f.Text

            & $report close running ''
            if (& $IsRunning) {
                if (-not (Stop-Nt8Hard -Why 'test the login' -GuardHome $GuardHome -IsRunning $IsRunning -Kill $Kill -Sleep $Sleep)) {
                    & $report close failed 'NinjaTrader did not close.'
                    return (New-GuardActionResult $false failed 'NinjaTrader did not close within 30 seconds. Close it from Task Manager and try again.')
                }
                [void](Wait-Nt8ConfigUnlocked -Nt8Dir $nt8 -Seconds 20)
                & $report close ok 'Closed.'
            } else { & $report close skipped 'NinjaTrader was not running.' }

            & $report login running ''
            $start = [DateTime]::UtcNow
            $proc = & $Launch
            $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
            $verdict = $null
            while (-not $verdict) {
                if ($external) {
                    $t = & $Window
                    if ($t -and $t -ne 'Welcome') { $verdict = @{ Ok = $true; Text = "NinjaTrader logged in ($(Split-Path $helper -Leaf))." } }
                } else {
                    $e = @(Read-GuardEvents -GuardHome $GuardHome -Nt8Dir $nt8 -Max 1 -Component login -KindPattern '^login\.(ok|failed)$' -SinceUtc $start)
                    if ($e.Count) {
                        $res = [string]$e[0].data.result
                        if ($e[0].kind -eq 'login.ok') { $verdict = @{ Ok = $true; Text = 'NinjaTrader logged in with the saved password.' } }
                        elseif ($res -eq 'gave-up') { $verdict = @{ Ok = $false; Text = 'NinjaTrader did not log in: the saved password was entered and refused. Save the password again (and check Remember username is ticked in NinjaTrader).' } }
                        else { $verdict = @{ Ok = $false; Text = "NinjaTrader did not log in ($res). Its log says more: Guard's logs folder, login.log." } }
                    }
                }
                if (-not $verdict -and [DateTime]::UtcNow -ge $deadline) {
                    $verdict = @{ Ok = $false; Text = $(if ($external) { "NinjaTrader is still at its login window after $WaitSeconds s: your login program did not log it in." }
                                                         else { "no answer from the login helper in $WaitSeconds s. NinjaTrader may be sitting at its login window." }) }
                }
                if (-not $verdict) { & $Sleep 1000 }
            }
            Write-GuardEvent -Component panel -Kind $(if ($verdict.Ok) { 'login.tested' } else { 'login.test-failed' }) -Severity $(if ($verdict.Ok) { 'info' } else { 'warn' }) -Text "Test login: $($verdict.Text)" -GuardHome $GuardHome
            & $report login $(if ($verdict.Ok) { 'ok' } else { 'failed' }) $verdict.Text
        } finally { if ($quiet -and $quiet.Lock) { $quiet.Lock.Dispose() }; $lock.Dispose() }
    } finally { Exit-GuardActionPause -Pause $pause -GuardHome $GuardHome -Report $report }
    return (New-GuardActionResult ([bool]$verdict.Ok) $(if ($verdict.Ok) { 'done' } else { 'failed' }) $verdict.Text)
}

function Invoke-GuardFireDrill {
    # The fire drill (spec 5.1 step 7): make the feed guard believe the market data died and watch it notice
    # and recover - the path a real dead feed takes, phone alerts included. It drops AutoReconnect.deafen in
    # NinjaTrader's folder: the add-on then ignores its own price updates for N seconds (nothing else in
    # NinjaTrader is touched), so its watchdog meets a real silence. 'Recover a silent feed' on: silent ->
    # re-subscribe -> disconnect + reconnect the connection serving the watch list -> data back. Off: its
    # alert, then the all-clear. Refused unless a feed guard 5.0.0 or later is reporting (its events are what
    # the drill follows), the market is open (it ignores a quiet closed market), no recovery is running, and
    # you are flat (the disconnect switches strategies off).
    # Seams: -Flat, -IsRunning, -Now { -> UTC }, -Sleep { param($ms) }, -Progress.
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [string]$Nt8Dir,
          [scriptblock]$Flat, [scriptblock]$IsRunning, [scriptblock]$Now, [scriptblock]$Sleep, [scriptblock]$Progress,
          [int]$QuietSeconds = 60)
    $nt8 = Get-Nt8DataDir $(if ($Nt8Dir) { $Nt8Dir } else { [string]$Config.nt8.dataDir })
    $report = { param($k, $s, $t) if ($Progress) { & $Progress $k $s $t } }
    if (-not $Flat) { $Flat = { Get-Nt8FlatState -Config $Config -Nt8Dir $nt8 -GuardHome $GuardHome } }
    if (-not $IsRunning) { $IsRunning = { [bool](@(Get-GuardSessionProcess -Name NinjaTrader).Count) } }
    if (-not $Now) { $Now = { [DateTime]::UtcNow } }
    if (-not $Sleep) { $Sleep = { param($ms) Start-Sleep -Milliseconds $ms } }
    $refuse = { param($why) & $report check failed $why; New-GuardActionResult $false refused $why }

    & $report check running ''
    $stopped = Get-GuardStopRefusal $GuardHome
    if ($stopped) { return (& $refuse $stopped) }
    if (-not (& $IsRunning)) { return (& $refuse 'Start NinjaTrader first: the drill runs inside it.') }
    $hbPath = [IO.Path]::Combine($nt8, 'AutoReconnect.heartbeat.json')
    $hb = $null
    if (Test-Path -LiteralPath $hbPath) {
        $age = ((& $Now) - (Get-Item -LiteralPath $hbPath).LastWriteTimeUtc).TotalSeconds
        if ($age -le [double]$Config.thresholds.guardianStaleSeconds) { try { $hb = [IO.File]::ReadAllText($hbPath) | ConvertFrom-Json } catch { } }
    }
    if (-not $hb) { return (& $refuse 'The feed guard add-on is not running in NinjaTrader (see its card on the Status tab).') }
    $want = Get-GuardExpectedFeedGuardVersion
    if (-not $hb.version -or [version][string]$hb.version -lt [version]$want -or $hb.events -ne $true) {
        return (& $refuse "The feed guard add-on in NinjaTrader is older than $want, and the drill follows the events only $want and later write. Import NT8Guard-AddOns.zip first.")
    }
    if ($hb.scheduledClosed -eq $true) {
        # Name the connection: a newer NinjaTrader's "Simulation" streams real market data and keeps market
        # hours, so "connect the Simulated Data Feed" alone reads as if it were already done (cold test 2026-10-10).
        $via = [string]$hb.feedProviders
        $served = $(if ($via) { " Your watch list is served by $via, which keeps market hours." } else { '' })
        $off = $(if ($via) { ", and disconnect $via" } else { '' })
        return (& $refuse ("The market is closed now, and the feed guard ignores a quiet feed while it is closed.$served Run the drill while the market is open - " +
                           "or use NinjaTrader's Simulated Data Feed, which never closes: in Connections > Configure add a Simulated Data Feed connection, connect it$off " +
                           "(feed guard 5.0.0 and later). Just switched to it? Wait a few seconds: the feed guard checks every 10 seconds."))
    }
    if ([int]$hb.rung -gt 0) { return (& $refuse 'The feed guard is already recovering the feed. Try again once the Status tab shows the market data Live.') }
    # The overwatch stays out for the whole drill: it would read the silence as a dead feed, and the drill's
    # disconnect writes the marker its strategy restore answers to.
    $quiet = Enter-GuardActionQuiet -Config $Config -GuardHome $GuardHome -Nt8Dir $nt8 -Seconds $QuietSeconds
    if ($quiet.Why) { return (& $refuse $quiet.Why) }
    try {
        $f = & $Flat
        if (-not $f.Safe) { return (& $refuse $f.Text) }
        if ($f.State -eq 'flat') { Clear-OverwatchStrategyRecord -GuardHome $GuardHome -Why 'fire drill (strategies switched off to be flat stay off)' }
        & $report check ok $f.Text

        # Long enough to need the whole ladder: noticed after the stale time, the re-subscribe settles in ~20 s,
        # then the disconnect; ending a minute after the stale time leaves room on both sides of it.
        $stale = $(if ($hb.watchdogStaleSeconds) { [int]$hb.watchdogStaleSeconds } else { [int]$Config.watch.staleSeconds })
        $recover = ($hb.autoRecover -ne $false)
        $secs = $stale + $(if ($recover) { 60 } else { 30 })
        & $report deafen running ''
        $deaf = [IO.Path]::Combine($nt8, 'AutoReconnect.deafen')
        $start = & $Now
        [IO.File]::WriteAllText($deaf, [string]$secs)
        Write-GuardEvent -Component panel -Kind 'drill.started' -Severity info -Text "Fire drill: market data silenced for $secs s" -GuardHome $GuardHome
        & $report deafen ok "The feed guard ignores market data for $secs s."

        $silentBy = $start.AddSeconds($stale + 90)
        $backBy = $start.AddSeconds($secs + 150)
        $phase = 'silent'
        & $report silent running ''
        $got = @{}
        $fail = $null; $silentAt = $null; $backAt = $null; $conn = ''
        $secsOf = { param($e) [int](((ConvertFrom-GuardUtc ([string]$e.utc)) - $start).TotalSeconds) }
        while (-not $fail -and $phase -ne 'done') {
            & $Sleep 1000
            $t = & $Now
            if ((Test-Path -LiteralPath $deaf) -and ($t - $start).TotalSeconds -ge 30) {
                try { [IO.File]::Delete($deaf) } catch { }
                $fail = 'The feed guard did not pick the drill up within 30 s - it may be stuck. Its log is AutoReconnect.log in NinjaTrader''s folder.'
                break
            }
            foreach ($e in @(Read-GuardEvents -GuardHome $GuardHome -Nt8Dir $nt8 -Max 50 -Component feedguard -SinceUtc $start | Sort-Object utc)) {
                $k = [string]$e.kind
                if ($k -eq 'feed.alert' -or $k -eq 'feed.alert-cleared') { $k = "$k/$([string]$e.data.key)" }
                if (-not $got.ContainsKey($k)) { $got[$k] = $e }
            }
            if ($got.ContainsKey('feed.source-outage')) { $fail = 'The feed guard went through every step and the market data did not come back - unusual for a drill; see AutoReconnect.log in NinjaTrader''s folder.'; break }
            if ($got.ContainsKey('feed.alert/silent-stall-exposed')) { $fail = 'The feed guard held back: it saw an open position on the connection.'; break }
            if ($phase -eq 'silent') {
                $hit = $(if ($recover) { $got['feed.stall'] } else { $got['feed.alert/silent-stall'] })
                if ($hit) {
                    $silentAt = & $secsOf $hit
                    & $report silent ok "Noticed after $silentAt s."
                    $phase = 'recover'
                    & $report recover running ''
                    if (-not $recover) { & $report recover skipped 'Recover a silent feed is set to alert only, so it does not reconnect.'; $phase = 'back'; & $report back running '' }
                } elseif ($t -ge $silentBy) { $fail = "The feed guard did not notice the silence within $($stale + 90) s. Its log is AutoReconnect.log in NinjaTrader's folder." }
            }
            if ($phase -eq 'recover' -and $got.ContainsKey('feed.disconnect')) {
                $conn = [string]$got['feed.disconnect'].data.connection
                & $report recover ok "Disconnected and reconnected $conn."
                $phase = 'back'
                & $report back running ''
            } elseif ($phase -eq 'recover' -and $got.ContainsKey('feed.recovered')) {
                & $report recover skipped 'The data came back at the re-subscribe step, before any reconnect.'
                $phase = 'back'
                & $report back running ''
            }
            if ($phase -eq 'back') {
                $hit = $(if ($recover) { $got['feed.recovered'] } else { $got['feed.alert-cleared/silent-stall'] })
                if ($hit) { $backAt = & $secsOf $hit; & $report back ok "Back after $backAt s."; $phase = 'done' }
            }
            if (-not $fail -and $phase -ne 'done' -and $t -ge $backBy) { $fail = "The market data did not come back within $($secs + 150) s of the drill's start. Its log is AutoReconnect.log in NinjaTrader's folder." }
        }
        if ($fail) {
            & $report $phase failed $fail
            Write-GuardEvent -Component panel -Kind 'drill.failed' -Severity warn -Text "Fire drill failed: $fail" -GuardHome $GuardHome
            return (New-GuardActionResult $false failed $fail)
        }
        $how = $(if (-not $recover) { 'raised its silent-feed alert' } elseif ($conn) { "disconnected and reconnected $conn" } else { 're-subscribed' })
        Write-GuardEvent -Component panel -Kind 'drill.passed' -Severity info -Text "Fire drill passed ($how)" -GuardHome $GuardHome
        return (New-GuardActionResult $true done "Passed: the feed guard noticed the silence after $silentAt s, $how, and the market data was back after $backAt s. Your phone should have shown its alerts - if it did not, check the Alerts tab.")
    } finally { $quiet.Lock.Dispose() }
}

function Test-GuardRemovable {
    # May Uninstall delete this folder? Only one that looks like a Guard home - guard.json, its last-good
    # copy, or the installed program (app\Guard-Panel.ps1) inside - and never a drive root, your profile, one
    # of Windows' own folders, NinjaTrader's own folders (its data folder, its program folder, the ones a
    # guard.json there names), a folder holding any of those, or one where NinjaTrader's files sit (its
    # database, its program, its compiled scripts - in the folder or one level down), whatever else it holds.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    try { $full = [IO.Path]::GetFullPath($Path).TrimEnd('\') } catch { return $false }
    if (-not [IO.Path]::IsPathRooted($full) -or $full.Length -le 3) { return $false }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { return $false }
    $never = @($env:USERPROFILE, $env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:TEMP) +
             @('MyDocuments', 'Desktop', 'LocalApplicationData', 'ApplicationData', 'Programs', 'StartMenu' | ForEach-Object { [Environment]::GetFolderPath($_) }) +
             @((Get-Nt8DataDir ''), (Get-Nt8ProgramDir ''))
    foreach ($f in 'guard.json', 'guard.json.lastgood') {
        $cfgPath = Join-Path $full $f
        if (Test-Path -LiteralPath $cfgPath) {
            try { $c = [IO.File]::ReadAllText($cfgPath) | ConvertFrom-Json; $never += @([string]$c.nt8.dataDir, [string]$c.nt8.programDir) | Where-Object { $_ -and $_ -ne 'auto' } } catch { }
        }
    }
    foreach ($n in $never) {
        if (-not $n) { continue }
        try { $nf = [IO.Path]::GetFullPath($n).TrimEnd('\') } catch { continue }
        # the folder itself, or one holding it (C:\Users holds every profile)
        if ($full -eq $nf -or $nf.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    }
    $markers = 'db\NinjaTrader.sqlite', 'bin\NinjaTrader.exe', 'bin\Custom'
    $dirs = @($full) + @(Get-ChildItem -LiteralPath $full -Directory -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    foreach ($d in $dirs) { foreach ($m in $markers) { if (Test-Path -LiteralPath (Join-Path $d $m)) { return $false } } }
    return ((Test-Path -LiteralPath (Join-Path $full 'guard.json')) -or (Test-Path -LiteralPath (Join-Path $full 'guard.json.lastgood')) -or
            (Test-Path -LiteralPath (Join-Path $full 'app\Guard-Panel.ps1')))
}

function Invoke-GuardUninstall {
    # Uninstall (spec 3.1): Guard's three tasks, its Start-menu shortcut and its folder. NinjaTrader is not
    # touched - so no flat check: its two add-ons stay until you delete them in NinjaTrader (the message
    # says how), and the strategy backup templates stay in NinjaTrader's templates folder. Guard's folder
    # is removed AFTER the panel closes (the panel runs from it): this writes a small cleanup script and
    # returns it (CleanupScript); Start-GuardCleanup runs it hidden when the window closes.
    # Seams: -RemoveTask { param($name) -> $true when the task is gone }, -StartMenuDir, -PanelPid, -Progress.
    [CmdletBinding()]
    param([string]$GuardHome, [string]$TaskPrefix = 'NT8Guard', [string]$StartMenuDir, [scriptblock]$RemoveTask,
          [int]$PanelPid = $PID, [scriptblock]$Progress)
    $report = { param($k, $s, $t) if ($Progress) { & $Progress $k $s $t } }
    if (-not $RemoveTask) {
        $RemoveTask = { param($n)
            if (Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $n -Confirm:$false -ErrorAction SilentlyContinue }
            -not [bool](Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue) }
    }
    if (-not $StartMenuDir) { $StartMenuDir = [Environment]::GetFolderPath('Programs') }
    $homeDir = [IO.Path]::GetFullPath((Get-GuardHome $GuardHome)).TrimEnd('\')
    $removable = Test-GuardRemovable $homeDir         # decided before anything here writes into the folder

    $lock = Open-GuardStartLock $GuardHome
    if (-not $lock) { return (New-GuardActionResult $false refused 'NinjaTrader is being started right now. Try again in a minute.') }
    try {
        & $report tasks running ''
        $left = @(foreach ($c in 'Relaunch', 'Overwatch', 'LogArchive') { $n = "$TaskPrefix-$c"; if (-not (& $RemoveTask $n)) { $n } })
        if ($left.Count) {
            $why = "Could not remove $($left -join ', '). Remove them in Task Scheduler (Task Scheduler Library), then uninstall again - Guard's folder is kept until then, or those tasks would run a missing script every minute."
            & $report tasks failed $why
            return (New-GuardActionResult $false failed $why)
        }
        & $report tasks ok 'Relaunch, Overwatch and LogArchive removed.'
    } finally { $lock.Dispose() }

    & $report shortcut running ''
    $lnk = Join-Path $StartMenuDir 'NT8 Guard.lnk'
    if (Test-Path -LiteralPath $lnk) { [IO.File]::Delete($lnk); & $report shortcut ok 'Removed.' } else { & $report shortcut skipped 'There was none.' }

    & $report files running ''
    $script = $null
    $notes = New-Object System.Collections.Generic.List[string]
    if ($removable) {
        $q = "'" + ($homeDir -replace "'", "''") + "'"
        $lines = @(
            '# NT8 Guard uninstall: remove Guard''s folder once the control panel has closed, then this script.'
            "if ($PanelPid -gt 0) { try { Wait-Process -Id $PanelPid -Timeout 600 -ErrorAction SilentlyContinue } catch { } }"
            "for (`$i = 0; `$i -lt 10 -and (Test-Path -LiteralPath $q); `$i++) { try { Remove-Item -LiteralPath $q -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 2 } }"
            'Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue'
        )
        $script = Join-Path ([IO.Path]::GetTempPath()) ("NT8Guard-uninstall-{0}.ps1" -f [guid]::NewGuid().ToString('N').Substring(0, 8))
        [IO.File]::WriteAllText($script, ($lines -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding($false)))
        & $report files ok "$homeDir is removed when this window closes."
        $app = [IO.Path]::GetFullPath((Get-GuardAppDir)).TrimEnd('\')
        if (-not $app.StartsWith($homeDir + '\', [StringComparison]::OrdinalIgnoreCase)) { $notes.Add("The program itself ($app) is outside Guard's folder and was left in place.") }
    } else {
        & $report files skipped "$homeDir does not look like Guard's folder, so it was left in place."
        $notes.Add("$homeDir was left in place: it does not look like Guard's folder.")
    }
    $msg = 'NT8 Guard is uninstalled: its tasks and shortcut are gone' + $(if ($script) { ', and its folder is removed when this window closes.' } else { '.' }) +
           ' Still in NinjaTrader, on purpose: the two add-ons - delete them in the NinjaScript Editor (AddOns folder: AutoReconnect, NT8BridgeServer and NT8BridgeServerPlayback; keep the last two if another tool uses the NT8 link), while flat, since NinjaTrader recompiles - with their settings files beside them; and your strategy backups (templates\Strategy\...\Guard backup - *.xml), yours to keep or delete.'
    if ($notes.Count) { $msg += ' ' + ($notes -join ' ') }
    return [pscustomobject]@{ Ok = $true; Result = 'done'; Message = $msg; CleanupScript = $script }
}

function Get-GuardTaskStateDefault {
    param([string]$Name)
    $t = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    if ($t) { [string]$t.State } else { $null }
}

function Write-GuardStopFeedGuard {
    # The feed guard's settings file for Stop (-Stopped) or Start, reported as the feedguard step. Never throws.
    param([hashtable]$Config, [string]$Nt8Dir, [bool]$Stopped, [scriptblock]$Report)
    $null = & $Report feedguard running ''
    $nt8 = $(if ($Nt8Dir) { $Nt8Dir } else { Get-Nt8DataDir ([string]$Config.nt8.dataDir) })
    if (-not (Test-Path -LiteralPath $nt8)) { $null = & $Report feedguard skipped "NinjaTrader's folder was not found ($nt8)."; return '' }
    try {
        $r = Write-FeedGuardConfig -Config $Config -Nt8Dir $nt8 -Stopped:$Stopped
        $when = $(if ($r.Changed) { 'It picks this up within seconds, or at NinjaTrader''s next start.' } else { 'It already had these settings.' })
        $null = & $Report feedguard ok $when
        return ''
    } catch {
        $why = "Its settings file could not be written: $($_.Exception.Message)"
        $null = & $Report feedguard failed $why
        return $why
    }
}

function Send-GuardStopAlert {
    # One alert for Stop or Start, reported as the alert step. Never throws.
    param([hashtable]$Config, [string]$GuardHome, [string]$Title, [string]$Body, [string]$Severity, [scriptblock]$Sender, [scriptblock]$Report)
    $null = & $Report alert running ''
    if (-not $Config.alerts.ntfyTopic -and -not $Config.alerts.webhookUrl) { $null = & $Report alert skipped 'No alert channel is set up.'; return }
    $a = Send-GuardAlert -Title $Title -Body $Body -Severity $Severity -Config $Config -Component panel -GuardHome $GuardHome -Sender $Sender
    $bad = @(@([string]$a.ntfy, [string]$a.webhook) | Where-Object { $_ -like 'failed*' })
    if ($bad.Count) { $null = & $Report alert failed ($bad -join '; ') } else { $null = & $Report alert ok 'Sent.' }
}

function Invoke-GuardStop {
    # Stop (Tools > Stop Guard): Guard does nothing until Start. Its three scheduled tasks are switched off -
    # so nothing restarts NinjaTrader, puts a strategy back or pages your phone - and the feed guard inside
    # NinjaTrader is told to only watch: no reconnect, no recovery, no strategy switched back on, no push.
    # Unlike a pause it never ends by itself, so it pages your phone once, and Status shows STOPPED.
    # Settings, the saved password, the backups and the logs are kept; NinjaTrader is not touched.
    # state\stopped.json is written FIRST, under the start lock: the launcher does not wait for a watchdog
    # run, so switching a task off leaves a run that already started going (an overwatch run can take
    # minutes) - it reads the file and stands down at its next step (Test-GuardStopHeld), so it can neither
    # kill nor start NinjaTrader after the stop. A task that will not switch off switches back on the ones
    # Stop switched off (never one you had switched off yourself) and removes the file: never half stopped.
    # Seams: -TaskState { param($name) -> 'Ready' | 'Running' | 'Disabled' | $null }, -SetTaskEnabled
    # { param($name, [bool]$On) }, -Sender (Send-GuardAlert's), -OverwatchWaitSeconds, -Sleep { param($ms) }, -Progress.
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [string]$Nt8Dir, [string]$TaskPrefix = 'NT8Guard',
          [scriptblock]$TaskState, [scriptblock]$SetTaskEnabled, [scriptblock]$Sender, [int]$OverwatchWaitSeconds = 90,
          [scriptblock]$Sleep, [scriptblock]$Progress)
    $report = { param($k, $s, $t) if ($Progress) { & $Progress $k $s $t } }
    if (-not $TaskState) { $TaskState = { param($n) Get-GuardTaskStateDefault $n } }
    if (-not $SetTaskEnabled) {
        $SetTaskEnabled = { param($n, $on) if ($on) { Enable-ScheduledTask -TaskName $n -ErrorAction Stop | Out-Null } else { Disable-ScheduledTask -TaskName $n -ErrorAction Stop | Out-Null } }
    }
    if (-not $Sleep) { $Sleep = { param($ms) Start-Sleep -Milliseconds $ms } }
    $names = @('Relaunch', 'Overwatch', 'LogArchive' | ForEach-Object { "$TaskPrefix-$_" })
    $short = { param($n) $n -replace "^$([regex]::Escape($TaskPrefix))-", '' }
    $path = Get-GuardStopPath $GuardHome
    $stamp = {
        [IO.File]::WriteAllText("$path.tmp", (([ordered]@{ stoppedUtc = [DateTime]::UtcNow.ToString('o'); reason = 'from the control panel' }) | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath "$path.tmp" -Destination $path -Force
    }

    $lock = Open-GuardStartLock $GuardHome
    if (-not $lock) { return (New-GuardActionResult $false refused 'Guard is busy right now (starting NinjaTrader, or saving the strategy backups). Try again in a minute.') }
    try {
        & $report tasks running ''
        $present = @($names | Where-Object { $null -ne (& $TaskState $_) })
        if (-not $present.Count) {
            $why = 'Guard''s tasks are not installed, so nothing of Guard is running. Run the setup (Tools > Run setup) to install them.'
            & $report tasks failed $why
            return (New-GuardActionResult $false refused $why)
        }
        try { & $stamp } catch {
            $why = "Nothing was switched off: Guard could not write $path ($($_.Exception.Message))."
            & $report tasks failed $why
            return (New-GuardActionResult $false failed $why)
        }
        $off = New-Object System.Collections.Generic.List[string]; $stuck = New-Object System.Collections.Generic.List[string]
        foreach ($n in $present) {
            if ((& $TaskState $n) -eq 'Disabled') { continue }      # already off: not Stop's to switch back on
            try { & $SetTaskEnabled $n $false } catch { }
            # The scheduler cmdlets can fail without throwing: ask the scheduler.
            if ((& $TaskState $n) -eq 'Disabled') { $off.Add($n) } else { $stuck.Add($n) }
        }
        if ($stuck.Count) {
            foreach ($n in $off) { try { & $SetTaskEnabled $n $true } catch { } }
            try { Remove-Item -LiteralPath $path -Force } catch { }
            $why = "Could not switch off $(@($stuck | ForEach-Object { & $short $_ }) -join ', '), so Guard was left running as it was. Switch the NT8Guard tasks off in Task Scheduler (Task Scheduler Library) instead."
            & $report tasks failed $why
            return (New-GuardActionResult $false failed $why)
        }
        # Stamped again with the tasks off: every run that began before this moment stands down. (If both tries
        # fail the first stamp stands; only a run begun in the instant before the switch-off escapes it.)
        try { & $stamp } catch { try { & $stamp } catch { } }
        Write-GuardLog -Component panel -Message "Guard stopped: $($present -join ', ') off ($($off.Count) switched off now)" -GuardHome $GuardHome
        Write-GuardEvent -Component panel -Kind 'guard.stopped' -Severity warn -Text 'Guard stopped from the control panel' -GuardHome $GuardHome
        $missing = @($names | Where-Object { $present -notcontains $_ })
        & $report tasks ok ("$(@($present | ForEach-Object { & $short $_ }) -join ', ') off." +
                            $(if ($missing.Count) { " $(@($missing | ForEach-Object { & $short $_ }) -join ', ') was not installed." } else { '' }))
    } finally { $lock.Dispose() }

    # An overwatch run still going holds its run lock. It checks the stop before every step that acts
    # (reconnect, flatten, re-enable, kill), so it ends soon: wait for it, bounded, and say if it has not.
    & $report wait running ''
    $owLock = Join-Path (Get-GuardPath state $GuardHome) 'overwatch.lock'
    $deadline = [DateTime]::UtcNow.AddSeconds($OverwatchWaitSeconds)
    $busy = $true
    do {
        try { ([IO.File]::Open($owLock, 'OpenOrCreate', 'ReadWrite', 'None')).Dispose(); $busy = $false } catch { }
        if (-not $busy -or [DateTime]::UtcNow -ge $deadline) { break }
        & $Sleep 500
    } while ($true)
    if ($busy) { & $report wait failed 'Still running (a crash report can take minutes): it stands down at its next step and does not restart NinjaTrader.' }
    else { & $report wait ok 'No overwatch check is running.' }
    $fgWhy = Write-GuardStopFeedGuard -Config $Config -Nt8Dir $Nt8Dir -Stopped $true -Report $report
    Send-GuardStopAlert -Config $Config -GuardHome $GuardHome -Title 'NT8 Guard stopped' -Severity warn -Sender $Sender -Report $report `
        -Body 'Stopped from the control panel. Nothing watches NinjaTrader on this PC - no restart, no reconnect, no alert - until Guard is started again (Tools > Start Guard).'
    $msg = 'NT8 Guard is stopped until you press Start Guard: nothing restarts NinjaTrader, reconnects it, puts a strategy back or alerts you. ' +
           'Settings, the saved password, the backups and the logs are kept.'
    if ($busy) { $msg += ' An overwatch check is still running (a crash report can take minutes): it stands down at its next step and does not restart NinjaTrader.' }
    if ($fgWhy) { $msg += " The feed guard inside NinjaTrader was NOT told to stand still and may still reconnect: $fgWhy" }
    return (New-GuardActionResult $true done $msg)
}

function Invoke-GuardStart {
    # Start (Tools > Start Guard, or Status): the three tasks on again, the feed guard's settings from
    # guard.json again, then both watchdogs run once now - so if NinjaTrader is closed, the relauncher
    # starts it within the minute (as Protection allows). Also works on tasks switched off by hand.
    # Seams: as Invoke-GuardStop, plus -RunTask { param($name) }, -WaitSeconds, -Sleep { param($ms) }.
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [string]$Nt8Dir, [string]$TaskPrefix = 'NT8Guard',
          [scriptblock]$TaskState, [scriptblock]$SetTaskEnabled, [scriptblock]$RunTask, [int]$WaitSeconds = 60,
          [scriptblock]$Sleep, [scriptblock]$Sender, [scriptblock]$Progress)
    $report = { param($k, $s, $t) if ($Progress) { & $Progress $k $s $t } }
    if (-not $TaskState) { $TaskState = { param($n) Get-GuardTaskStateDefault $n } }
    if (-not $SetTaskEnabled) {
        $SetTaskEnabled = { param($n, $on) if ($on) { Enable-ScheduledTask -TaskName $n -ErrorAction Stop | Out-Null } else { Disable-ScheduledTask -TaskName $n -ErrorAction Stop | Out-Null } }
    }
    if (-not $RunTask) { $RunTask = { param($n) Start-ScheduledTask -TaskName $n -ErrorAction Stop } }
    if (-not $Sleep) { $Sleep = { param($ms) Start-Sleep -Milliseconds $ms } }
    $names = @('Relaunch', 'Overwatch', 'LogArchive' | ForEach-Object { "$TaskPrefix-$_" })
    $short = { param($n) $n -replace "^$([regex]::Escape($TaskPrefix))-", '' }

    & $report tasks running ''
    $present = @($names | Where-Object { $null -ne (& $TaskState $_) })
    if (-not $present.Count) {
        $why = 'Guard''s tasks are not installed. Run the setup (Tools > Run setup) to install them.'
        & $report tasks failed $why
        return (New-GuardActionResult $false failed $why)
    }
    $stuck = New-Object System.Collections.Generic.List[string]
    foreach ($n in $present) {
        if ((& $TaskState $n) -eq 'Disabled') { try { & $SetTaskEnabled $n $true } catch { } }
        if ((& $TaskState $n) -eq 'Disabled') { $stuck.Add($n) }
    }
    if ($stuck.Count) {
        $why = "Could not switch on $($stuck -join ', '). Switch it on in Task Scheduler (Task Scheduler Library), or run the setup again."
        & $report tasks failed $why
        return (New-GuardActionResult $false failed $why)
    }
    Write-GuardLog -Component panel -Message "Guard started: $($present -join ', ') switched on" -GuardHome $GuardHome
    Write-GuardEvent -Component panel -Kind 'guard.started' -Severity info -Text 'Guard started again from the control panel' -GuardHome $GuardHome
    $missing = @($names | Where-Object { $present -notcontains $_ })
    $missNote = $(if ($missing.Count) { " $(@($missing | ForEach-Object { & $short $_ }) -join ', ') is not installed - run the setup to install it." } else { '' })
    & $report tasks ok ("$(@($present | ForEach-Object { & $short $_ }) -join ', ') on." + $missNote)

    # The stop ends once the feed guard has your settings back: if they could not be written, the file
    # stays and Status keeps saying so (a banner, and Start Guard in Tools to try again).
    $fgWhy = Write-GuardStopFeedGuard -Config $Config -Nt8Dir $Nt8Dir -Stopped $false -Report $report
    $path = Get-GuardStopPath $GuardHome
    if (-not $fgWhy -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Force }

    # Both watchdogs once now, then wait for their heartbeats: proof they run, not just that they are on.
    & $report check running ''
    $state = Get-GuardPath state $GuardHome
    $watch = @(@{ k = 'relaunch'; t = "$TaskPrefix-Relaunch" }, @{ k = 'overwatch'; t = "$TaskPrefix-Overwatch" } | Where-Object { $present -contains $_.t })
    $checkNote = ''
    if (-not $watch.Count) {
        $checkNote = 'Neither watchdog is installed - run the setup to install them.'
        & $report check skipped $checkNote
    } else {
        $since = [DateTime]::UtcNow.AddSeconds(-1)
        foreach ($w in $watch) { try { & $RunTask $w.t } catch { } }
        $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
        do {
            $quiet = @($watch | Where-Object { $hb = Join-Path $state "$($_.k).heartbeat"; -not ((Test-Path -LiteralPath $hb) -and [IO.File]::GetLastWriteTimeUtc($hb) -ge $since) })
            if (-not $quiet.Count -or [DateTime]::UtcNow -ge $deadline) { break }
            & $Sleep 1000
        } while ($true)
        if ($quiet.Count) {
            $checkNote = "No report yet from the $(@($quiet | ForEach-Object { $_.k -replace 'relaunch', 'relauncher' }) -join ' and ') - the Status tab shows when it reports."
            & $report check failed $checkNote
        } else { & $report check ok $(if ($watch.Count -eq 2) { 'Both reported.' } else { 'It reported.' }) }
    }

    Send-GuardStopAlert -Config $Config -GuardHome $GuardHome -Title 'NT8 Guard started' -Severity info -Sender $Sender -Report $report `
        -Body 'Started again from the control panel: Guard is watching NinjaTrader on this PC.'
    $msg = "NT8 Guard is watching again. If NinjaTrader is closed: $(Get-GuardRestartNote -Config $Config -GuardHome $GuardHome -Nt8Dir $Nt8Dir)" + $missNote
    if ($checkNote) { $msg += " $checkNote" }
    if ($fgWhy) { $msg += " The feed guard inside NinjaTrader still only watches - its settings could not be written: $fgWhy Press Start Guard again to retry." }
    return (New-GuardActionResult $true done $msg)
}

function Start-GuardCleanup {
    # Run Uninstall's cleanup script hidden and detached: it waits for the panel to exit, then removes Guard's folder.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Script)
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (ConvertTo-GuardArg $Script)) | Out-Null
}

Export-ModuleMember -Function Get-GuardActionSteps, Stop-Nt8Hard, Invoke-GuardPutBack, Invoke-GuardTestLogin, Invoke-GuardFireDrill,
                              Test-GuardRemovable, Invoke-GuardUninstall, Invoke-GuardStop, Invoke-GuardStart, Start-GuardCleanup
