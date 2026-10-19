# What the control panel shows: every piece of NT8 Guard, read from the files the pieces write - their
# heartbeats, state files and events - never by asking them, and never by doing their work.
#
# READ ONLY. Nothing here starts, stops, writes, deletes or alerts. That rules out the watchdogs' own
# helpers that look like reads: Test-Nt8StartAllowed stamps files and sends alerts, and
# Get-GuardPauseState deletes an expired pause file and writes an event. This module reads the same
# files they write instead. The panel calls it every 2 seconds, so it must also stay cheap: Task
# Scheduler is asked only about a watchdog whose heartbeat has gone stale.
#
# A card is @{ Key; Title; State; Headline; Detail; Remedy; Guide }:
#   State  ok | warn | bad | idle (nothing to judge, e.g. the add-ons while NinjaTrader is closed) | off
#   Guide  an anchor in the setup guide (P5); the panel links it when the guide is installed.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Config.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Events.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Alerts.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Overwatch.psm1')   # Get-BackupAdvice: one wording for missing strategies
Import-Module (Join-Path $PSScriptRoot 'Guard.Pause.psm1')       # Get-GuardStopPath (Stop's file; this module only reads it)

# The OLDEST feed guard add-on this Guard works with: the Status note, the wizard's add-on light and the
# fire drill (which follows the events 5.0.0 started writing) all gate on it, so raise it only when Guard
# needs something an older add-on lacks - not for a cosmetic change such as 5.0.1's system name in push
# titles. A test keeps addon\AutoReconnect.cs AddOnVersion at or above it. An older add-on in
# NinjaTrader is reported, with the import step as the remedy.
$script:ExpectedFeedGuardVersion = '5.0.0'
$script:Labels = @{ relaunch = 'Relauncher'; overwatch = 'Overwatch'; feedguard = 'Feed guard'; login = 'Login'
                    guard = 'Guard'; logarchive = 'Log keeper'; panel = 'Panel' }
$script:Inv = [Globalization.CultureInfo]::InvariantCulture
# Asking Task Scheduler costs ~0.4 s and NinjaTrader's file version ~0.1 s; the panel asks every 2 s.
$script:TaskCache = @{}
$script:VersionCache = $null

function Get-CachedTaskState([string]$Name, [DateTime]$NowUtc, $FreshAfterUtc) {
    # -FreshAfterUtc: an answer older than that (Guard stopped since) is asked again.
    $hit = $script:TaskCache[$Name]
    if ($hit -and ($NowUtc - $hit.At).TotalSeconds -lt 60 -and -not ($FreshAfterUtc -is [DateTime] -and $hit.At -lt $FreshAfterUtc)) { return $hit.State }
    $t = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    $st = $(if ($t) { [string]$t.State } else { $null })
    $script:TaskCache[$Name] = @{ At = $NowUtc; State = $st }
    return $st
}

function Get-CachedNt8Version([string]$ProgramDir, [DateTime]$NowUtc) {
    $c = $script:VersionCache
    if ($c -and $c.Dir -eq $ProgramDir -and ($NowUtc - $c.At).TotalMinutes -lt 5) { return $c.Version }
    $v = Get-Nt8Version $ProgramDir
    $script:VersionCache = @{ Dir = $ProgramDir; At = $NowUtc; Version = $v }
    return $v
}

function Test-GuardMarketClosedByClock {
    # The CME's regular closures by the clock alone: the weekend (Fri 17:00 - Sun 18:00 New York) and the
    # daily 17:00-18:00 break. For a feed guard older than 5.0.0, which does not report the calendar's
    # verdict (holidays). Fails OPEN: an unknown time zone never hides a silent feed.
    [CmdletBinding()] param([Parameter(Mandatory)][DateTime]$NowUtc, [string]$TimeZoneId = 'Eastern Standard Time')
    try {
        $et = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::SpecifyKind($NowUtc, 'Utc'), [TimeZoneInfo]::FindSystemTimeZoneById($TimeZoneId))
        $d = $et.DayOfWeek; $h = $et.TimeOfDay
        if ($d -eq 'Saturday') { return $true }
        if ($d -eq 'Friday' -and $h -ge [TimeSpan]'17:00') { return $true }
        if ($d -eq 'Sunday' -and $h -lt [TimeSpan]'18:00') { return $true }
        return ($h -ge [TimeSpan]'17:00' -and $h -lt [TimeSpan]'18:00')
    } catch { return $false }
}

function Get-GuardExpectedFeedGuardVersion { return $script:ExpectedFeedGuardVersion }

function Format-GuardTime {
    # A UTC moment as the person reads it: '14:57' today, 'Wed 14:57' on another day.
    [CmdletBinding()] param([Parameter(Mandatory)][DateTime]$Utc, [Parameter(Mandatory)][DateTime]$NowUtc, [TimeZoneInfo]$TimeZone)
    if (-not $TimeZone) { $TimeZone = [TimeZoneInfo]::Local }
    $t = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::SpecifyKind($Utc, 'Utc'), $TimeZone)
    $n = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::SpecifyKind($NowUtc, 'Utc'), $TimeZone)
    if ($t.Date -eq $n.Date) { return $t.ToString('HH:mm', $script:Inv) }
    return $t.ToString('ddd HH:mm', $script:Inv)
}

function Format-GuardCount {
    # '1 strategy' / '3 strategies'.
    [CmdletBinding()] param([Parameter(Mandatory)][int]$Count, [Parameter(Mandatory)][string]$One, [Parameter(Mandatory)][string]$Many)
    if ($Count -eq 1) { return "1 $One" }
    return "$Count $Many"
}

function Format-GuardAge {
    # Seconds as a short duration: '12 s', '4 min', '3 h 20 min', '2 days'.
    [CmdletBinding()] param([Parameter(Mandatory)][double]$Seconds)
    $s = [math]::Max(0, [math]::Floor($Seconds))
    if ($s -lt 120) { return "$s s" }
    $m = [math]::Floor($s / 60)
    if ($m -lt 60) { return "$m min" }
    $h = [math]::Floor($m / 60)
    if ($h -lt 48) { $r = $m % 60; return $(if ($r) { "$h h $r min" } else { "$h h" }) }
    return "$([math]::Floor($h / 24)) days"
}

function Get-FileAgeSeconds([string]$Path, [DateTime]$NowUtc) {
    # The overwatch judges every heartbeat by its file time; so does the panel. $null = no file.
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return ($NowUtc - (Get-Item -LiteralPath $Path).LastWriteTimeUtc).TotalSeconds } catch { return $null }
}

function Read-UtcFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return ConvertFrom-GuardUtc ([IO.File]::ReadAllText($Path).Trim()) } catch { return $null }
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return ([IO.File]::ReadAllText($Path) | ConvertFrom-Json) } catch { return $null }
}

function New-Card([string]$Key, [string]$Title) {
    return [pscustomobject]@{ Key = $Key; Title = $Title; State = 'ok'; Headline = ''; Detail = ''; Remedy = ''; Guide = $Key }
}

function Set-Card($Card, [string]$State, [string]$Headline, [string]$Detail = '', [string]$Remedy = '') {
    $Card.State = $State; $Card.Headline = $Headline; $Card.Detail = $Detail; $Card.Remedy = $Remedy
}

function Get-GuardPanelPause {
    # Is Guard paused, read WITHOUT the watchdogs' side effects. An expired pause reads as not paused and
    # its file is left for the watchdogs to remove (they write the 'pause ended' event). The 24-hour cap
    # is applied as they apply it. Old per-component switch files are listed separately.
    [CmdletBinding()] param([string]$GuardHome, [string]$Nt8Dir, [Parameter(Mandatory)][DateTime]$NowUtc)
    $legacy = @(@('NT8Relaunch.disabled', 'NT8Overwatch.disabled') | Where-Object { Test-Path -LiteralPath (Join-Path (Get-Nt8DataDir $Nt8Dir) $_) })
    $o = Read-JsonFile (Join-Path (Get-GuardPath state $GuardHome) 'pause.json')
    $paused = $false; $until = $null; $reason = ''
    if ($o) {
        $set = ConvertFrom-GuardUtc ([string]$o.setUtc); $until = ConvertFrom-GuardUtc ([string]$o.untilUtc)
        if ($set -and $until) {
            if ($until -gt $set.AddMinutes(1440)) { $until = $set.AddMinutes(1440) }
            $paused = ($NowUtc -lt $until); $reason = [string]$o.reason
        }
    }
    if (-not $paused) { $until = $null }
    return [pscustomobject]@{ Paused = $paused; UntilUtc = $until; Reason = $reason; LegacySwitches = $legacy }
}

function Get-GuardStatus {
    # Everything the Status tab shows, as one object. Every outside fact can be passed in (tests do):
    #   -Processes  { -> NinjaTrader processes in this session: @{ Id; StartUtc; Responding; Title; ControlCenterHung } }
    #               Responding tests only the topmost window; ControlCenterHung is the Control Center's own
    #               (it runs on a UI thread of its own - see Get-Nt8WindowHealth).
    #   -TaskState  { param($taskName) -> 'Ready' | 'Running' | 'Disabled' | $null (no such task) }
    #   -Nt8Version [version] or $null; 'auto' reads NinjaTrader.exe
    [CmdletBinding()]
    param([string]$GuardHome, [string]$Nt8Dir, [hashtable]$Config, [Nullable[DateTime]]$NowUtc,
          [scriptblock]$Processes, [scriptblock]$TaskState, $Nt8Version = 'auto', [TimeZoneInfo]$TimeZone,
          [int]$MaxEvents = 40)
    $now = $(if ($null -ne $NowUtc) { [DateTime]$NowUtc } else { [DateTime]::UtcNow })
    if (-not $TimeZone) { $TimeZone = [TimeZoneInfo]::Local }
    if (-not $Config) { $Config = Get-GuardConfig $GuardHome }
    if (-not $Nt8Dir) { $Nt8Dir = Get-Nt8DataDir ([string]$Config.nt8.dataDir) }
    if (-not $Processes) {
        $Processes = {
            $sid = (Get-Process -Id $PID).SessionId
            , @(Get-Process -Name NinjaTrader -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid } | ForEach-Object {
                $start = $null; try { $start = $_.StartTime.ToUniversalTime() } catch { }
                $ccHung = $false; try { $ccHung = [bool](Get-Nt8WindowHealth -ProcessId $_.Id).ControlCenterHung } catch { }
                [pscustomobject]@{ Id = $_.Id; StartUtc = $start; Responding = $_.Responding; Title = $_.MainWindowTitle; ControlCenterHung = $ccHung } })
        }
    }
    if (-not $TaskState) { $TaskState = { param($n) Get-CachedTaskState $n $now $taskFreshAfter } }
    if ([string]$Nt8Version -eq 'auto') { $Nt8Version = Get-CachedNt8Version ([string]$Config.nt8.programDir) $now }
    # An event's time may not parse (a damaged or hand-edited line): '?' then, never a failed status read.
    $T = { param($u) if ($u -is [DateTime]) { Format-GuardTime -Utc $u -NowUtc $now -TimeZone $TimeZone } else { '?' } }
    $mask = { param([string]$s) Hide-GuardSecret -Text $s -Config $Config }

    $state = Get-GuardPath state $GuardHome
    $prot = Get-GuardProtection $Config
    $th = $Config.thresholds
    $pause = Get-GuardPanelPause -GuardHome $GuardHome -Nt8Dir $Nt8Dir -NowUtc $now
    $nt = @(& $Processes) | Select-Object -First 1
    $ntStart = $(if ($nt) { $nt.StartUtc } else { $null })
    $events = @(Read-GuardEvents -GuardHome $GuardHome -Nt8Dir $Nt8Dir -Max 400)

    # ---- stopped (Tools > Stop Guard): its file says it was on purpose, the tasks say whether it still holds -
    # switched on again by hand or by the setup, Guard runs whatever the file says. Only then is the
    # scheduler asked every minute, and once at once after a stop.
    # An unreadable file still counts (when is then unknown): Save and the risky actions go by the file too.
    $stopFile = Get-GuardStopPath $GuardHome
    $stopDoc = $(if (Test-Path -LiteralPath $stopFile) { $d = Read-JsonFile $stopFile; if ($d) { $d } else { [pscustomobject]@{ stoppedUtc = '' } } } else { $null })
    $stopSince = $(if ($stopDoc) { ConvertFrom-GuardUtc ([string]$stopDoc.stoppedUtc) } else { $null })
    # A cached task state from before the last Stop or Start is asked again.
    $lastStart = @($events | Where-Object { $_.kind -eq 'guard.started' } | Select-Object -First 1)
    $startedUtc = $(if ($lastStart.Count) { ConvertFrom-GuardUtc ([string]$lastStart[0].utc) } else { $null })
    $taskFreshAfter = @($stopSince, $startedUtc | Where-Object { $_ -is [DateTime] } | Sort-Object -Descending) | Select-Object -First 1
    # Leftover: the file is there but the tasks run - switched on outside the panel. The feed guard was told
    # to only watch and stays so until Start (a banner says so, and Tools offers Start Guard).
    $stop = [pscustomobject]@{ Stopped = $false; SinceUtc = $null; Leftover = $false }
    if ($stopDoc) {
        $on = @(foreach ($tn in 'NT8Guard-Relaunch', 'NT8Guard-Overwatch') { $ts = $null; try { $ts = & $TaskState $tn } catch { }; if ($ts -and $ts -ne 'Disabled') { $tn } })
        $stop = [pscustomobject]@{ Stopped = (-not $on.Count); SinceUtc = $(if ($on.Count) { $null } else { $stopSince }); Leftover = [bool]$on.Count }
    }
    $stopWhen = $(if ($stop.SinceUtc) { " since $(& $T $stop.SinceUtc)" } else { '' })

    # ---- the watchdogs (relauncher + overwatch): their heartbeats, every minute, also while paused
    $wdStale = [double]$Config.overwatch.relauncherStaleSeconds
    $wd = [ordered]@{}
    foreach ($c in @(@{ k = 'relaunch'; task = 'NT8Guard-Relaunch'; label = 'relauncher' }, @{ k = 'overwatch'; task = 'NT8Guard-Overwatch'; label = 'overwatch' })) {
        $age = Get-FileAgeSeconds (Join-Path $state "$($c.k).heartbeat") $now
        $fresh = ($null -ne $age -and $age -le $wdStale)
        $why = ''
        if (-not $fresh) {
            $ts = $null; try { $ts = & $TaskState $c.task } catch { }
            $why = $(if ($null -eq $ts) { 'its task is not installed' } elseif ($ts -eq 'Disabled') { 'its task is disabled' } elseif ($null -eq $age) { 'it has never run' } else { "no sign of it for $(Format-GuardAge $age)" })
        }
        $wd[$c.k] = @{ Fresh = $fresh; Age = $age; Why = $why; Label = $c.label; Task = $c.task }
    }
    $anyWatchdog = ($wd.relaunch.Fresh -or $wd.overwatch.Fresh)
    # Start Guard is offered whenever it has something to do: stopped, a stop left over, or a watchdog task off.
    $stop | Add-Member -NotePropertyName CanStart -NotePropertyValue ($stop.Stopped -or $stop.Leftover -or [bool]@($wd.Values | Where-Object { $_.Why -eq 'its task is disabled' }).Count)

    # ---- add-on heartbeats
    $arPath = Join-Path $Nt8Dir 'AutoReconnect.heartbeat.json'
    $brPath = Join-Path $Nt8Dir 'NT8Bridge\result\heartbeat.json'
    $addon = {
        param([string]$Path, [double]$Stale)
        $age = Get-FileAgeSeconds $Path $now
        if ($null -eq $age) { return 'missing' }
        if ($age -le $Stale) { return 'fresh' }
        $written = $now.AddSeconds(-$age)
        if ($ntStart -and $written -lt $ntStart) { return 'notloaded' }
        return 'stale'
    }
    $ar = & $addon $arPath ([double]$th.guardianStaleSeconds)
    $br = & $addon $brPath ([double]$th.bridgeStaleSeconds)
    $arH = $(if ($ar -eq 'fresh') { Read-JsonFile $arPath } else { $null })

    $cards = New-Object System.Collections.Generic.List[object]

    # ---- NinjaTrader
    $c = New-Card nt8 'NinjaTrader'
    $verNote = ''; $verState = 'ok'
    if ($Nt8Version) {
        $v = [version]$Nt8Version
        if ($v -ge [version]'8.1.6.3') { $verNote = "version $v" }
        elseif ($v.Major -eq 8 -and $v.Minor -eq 1) { $verNote = "version $v is not tested with Guard (8.1.6.3 and later are)"; $verState = 'warn' }
        else { $verNote = "version $v is not supported (Guard needs NinjaTrader 8.1)"; $verState = 'bad' }
    }
    if ($nt) {
        $since = $(if ($ntStart) { "since $(& $T $ntStart)" } else { '' })
        $detail = (@($since, $verNote) | Where-Object { $_ }) -join ' - '
        if (-not $nt.Responding) {
            $act = $(if ($stop.Stopped) { 'Guard is stopped, so it will not restart it' } elseif ($prot.restartWhenFrozen) { 'Guard restarts it if it stays frozen' } else { 'Protection is set to alert only, so Guard will not restart it' })
            Set-Card $c bad 'Not responding' "$act. $detail".Trim()
        } elseif ($nt.ControlCenterHung -eq $true) {
            $act = $(if ($stop.Stopped) { 'Guard is stopped, so it will not restart it' } elseif ($prot.restartWhenFrozen) { 'Guard restarts NinjaTrader if it stays frozen' } else { 'Protection is set to alert only, so Guard will not restart it' })
            Set-Card $c bad 'Control Center not responding' "Other windows may still work. $act. $detail".Trim()
        } elseif ($nt.Title -eq 'Welcome') {
            Set-Card $c warn 'At its login window' "Waiting for the login to finish. $detail".Trim()
        } else {
            Set-Card $c $verState 'Running' $detail
        }
    } else {
        $backoff = Read-UtcFile (Join-Path $state 'start.backoff-until')
        $hold = Test-Path (Join-Path $state 'start.loginhold')
        $locked = (Test-Path (Join-Path $state 'relaunch.lockedsince')) -or (Test-Path (Join-Path $state 'overwatch.lockedsince'))
        if ($stop.Stopped) {
            Set-Card $c off 'Not running' 'Guard is stopped, so it will not start NinjaTrader.'
        } elseif ($pause.Paused) {
            Set-Card $c off 'Not running' "Guard is paused until $(& $T $pause.UntilUtc), so it will not start NinjaTrader."
        } elseif ($backoff -and $now -lt $backoff) {
            Set-Card $c bad 'Not running' "Restarts are stopped until $(& $T $backoff): NinjaTrader was restarted too often without staying up." 'Check the NinjaTrader login by hand; Guard tries again after that time.'
        } elseif ($hold) {
            Set-Card $c bad 'Not running' 'The saved password was refused twice, so Guard stopped starting NinjaTrader.' 'Save the password again (NT8 Guard: Tools > NinjaTrader login) or log in by hand.'
        } elseif (-not $prot.relaunch) {
            Set-Card $c warn 'Not running' 'Protection is Watch only, so Guard does not start NinjaTrader.'
        } elseif (-not $anyWatchdog) {
            Set-Card $c bad 'Not running' "Guard's watchdogs are not running, so nothing will start it." 'See the Watchdogs card.'
        } elseif ($locked) {
            Set-Card $c warn 'Not running' 'The screen is locked: Guard starts NinjaTrader after you unlock it (the login cannot reach a locked screen).'
        } else {
            Set-Card $c warn 'Not running' 'Guard starts it within a minute.'
        }
        if ($verState -eq 'bad') { $c.State = 'bad'; $c.Detail = "$($c.Detail) $verNote".Trim() }
    }
    $cards.Add($c)

    # ---- Market data (the feed guard's view of the watch list)
    $c = New-Card feed 'Market data'
    if (-not $nt) { Set-Card $c idle 'No reading' 'NinjaTrader is not running.' }
    elseif (-not $arH) { Set-Card $c idle 'No reading' 'Needs the feed guard add-on running in NinjaTrader (see its card).' }
    else {
        $via = $(if ($arH.feedProviders) { "via $($arH.feedProviders)" } else { '' })
        $limit = $(if ($arH.watchdogStaleSeconds) { [double]$arH.watchdogStaleSeconds } else { [double]$Config.watch.staleSeconds })
        $l1 = $arH.lastL1AgeSeconds
        $closed = $(if ($arH.PSObject.Properties['scheduledClosed']) { $arH.scheduledClosed -eq $true }
                    else { Test-GuardMarketClosedByClock -NowUtc $now -TimeZoneId ([string]$Config.watch.marketTimeZone) })
        if ($closed) { Set-Card $c idle 'Market closed (scheduled)' $via }
        elseif ([int]$arH.rung -gt 0) { Set-Card $c warn 'Recovering a silent feed' "The feed guard is on step $([int]$arH.rung) of 3 (re-subscribe, disconnect, reconnect). $via".Trim() }
        elseif ($null -eq $l1) { Set-Card $c warn 'No data yet' "No price has arrived for the watch list since the feed guard started. $via".Trim() }
        elseif ([double]$l1 -gt $limit) {
            $act = $(if ($stop.Stopped) { 'Guard is stopped, so the feed guard only watches' } elseif ($arH.autoRecover -eq $false) { 'auto-recovery is off, so it only alerts' } else { 'the feed guard steps in to recover it' })
            Set-Card $c bad "Silent for $(Format-GuardAge ([double]$l1))" "No price for longer than $(Format-GuardAge $limit); $act. $via".Trim()
        }
        else { Set-Card $c ok 'Live' (@("last price $(Format-GuardAge ([double]$l1)) ago", $via) | Where-Object { $_ }) -join ' - ' }
    }
    $cards.Add($c)

    # ---- Auto-login
    $c = New-Card login 'Auto-login'
    $helper = [string]$Config.login.helper
    $lastLogin = @($events | Where-Object { $_.kind -match '^login\.(ok|failed)$' } | Select-Object -First 1)
    if (Test-Path (Join-Path $state 'start.loginhold')) {
        Set-Card $c bad 'Stopped' 'NinjaTrader stayed at its login window after the saved password was entered, twice.' 'Save the password again (NT8 Guard: Tools > NinjaTrader login) or log in by hand; Guard resumes either way.'
    } elseif ($helper -and $helper -ne 'builtin') {
        Set-Card $c ok "Uses $(Split-Path $helper -Leaf)" 'Your own login program starts NinjaTrader.'
    } elseif (-not (Test-Path (Join-Path (Get-GuardPath secrets $GuardHome) 'nt8.cred'))) {
        Set-Card $c warn 'No password saved' 'Guard cannot log NinjaTrader in until the password is saved.' 'Save it in NT8 Guard: Tools > NinjaTrader login.'
    } elseif ($lastLogin.Count -and $lastLogin[0].kind -eq 'login.failed') {
        $when = & $T (ConvertFrom-GuardUtc ([string]$lastLogin[0].utc))
        Set-Card $c warn 'Last login failed' "At $when NinjaTrader did not log in: $(& $mask ([string]$lastLogin[0].text))" 'If the password changed, save it again in NT8 Guard: Tools > NinjaTrader login.'
    } else {
        $d = $(if ($lastLogin.Count) { "Last login $(& $T (ConvertFrom-GuardUtc ([string]$lastLogin[0].utc))) worked." } else { 'No login needed yet.' })
        Set-Card $c ok 'Password saved' $d
    }
    $cards.Add($c)

    # ---- Watchdogs
    $c = New-Card watchdogs 'Watchdogs'
    $words = { param($w) if ($w.Fresh) { "$($w.Label) checked $(Format-GuardAge $w.Age) ago" } else { "$($w.Label): $($w.Why)" } }
    $detail = (@($wd.Values | ForEach-Object { & $words $_ })) -join ' - '
    if ($stop.Stopped) {
        Set-Card $c off "Stopped$stopWhen" 'Guard''s tasks are switched off: nothing restarts NinjaTrader, reconnects it, puts a strategy back or alerts you.' 'Start Guard (at the top of this tab, or in Tools) when you want it back.'
    }
    elseif ($pause.Paused) { Set-Card $c off "Paused until $(& $T $pause.UntilUtc)" $(if ($pause.Reason) { "Reason: $($pause.Reason)" } else { 'Both watchdogs only watch while paused; the pause ends by itself.' }) }
    elseif ($wd.relaunch.Fresh -and $wd.overwatch.Fresh) { Set-Card $c ok 'Both running' $detail }
    else {
        $missing = @($wd.Values | Where-Object { -not $_.Fresh -and $_.Why -eq 'its task is not installed' })
        $remedy = $(if ($missing.Count) { 'Run the setup again to register Guard''s tasks (Register-GuardTasks.ps1).' } else { 'Open Task Scheduler and enable the NT8Guard tasks, or run the setup again.' })
        $state_ = $(if ($anyWatchdog) { 'warn' } else { 'bad' })
        $head = $(if ($anyWatchdog) { 'One is not running' } else { 'Not running' })
        Set-Card $c $state_ $head $detail $remedy
    }
    $cards.Add($c)

    # ---- the two add-ons inside NinjaTrader
    $importRemedy = 'In NinjaTrader: Tools > Import > NinjaScript Add-On, choose NT8Guard-AddOns.zip.'
    foreach ($a in @(@{ k = 'feedguard'; t = 'Feed guard add-on'; s = $ar; stale = $th.guardianStaleSeconds },
                     @{ k = 'link'; t = 'NT8 link add-on'; s = $br; stale = $th.bridgeStaleSeconds })) {
        $c = New-Card $a.k $a.t
        if (-not $nt) { Set-Card $c idle 'Waiting for NinjaTrader' '' }
        else {
            switch ($a.s) {
                'missing'   { Set-Card $c bad 'Not installed' 'It has never reported from this NinjaTrader.' $importRemedy }
                'notloaded' { Set-Card $c bad 'Not loaded' 'It has not reported since NinjaTrader started: removed, or it failed to compile after an update.' 'Open the NinjaScript Editor and press F5 to compile; if it is gone, import NT8Guard-AddOns.zip again.' }
                'stale'     { Set-Card $c bad 'Stopped reporting' "It stopped reporting more than $(Format-GuardAge ([double]$a.stale)) ago while NinjaTrader runs - it is stuck." $(if ($stop.Stopped) { 'Guard is stopped, so it will not restart NinjaTrader.' } else { 'Guard restarts NinjaTrader if it stays stuck (Protection permitting).' }) }
                default     { Set-Card $c ok 'Running' '' }
            }
        }
        if ($a.k -eq 'feedguard' -and $a.s -eq 'fresh' -and -not $arH) {
            Set-Card $c warn 'Running, with a problem' 'Its heartbeat file could not be read (caught mid-write, or damaged); the next read usually works.'
        }
        if ($a.k -eq 'feedguard' -and $arH) {
            $notes = New-Object System.Collections.Generic.List[string]
            $warn = $false; $rem = ''
            $ver = [string]$arH.version
            if (-not $ver -or ([version]$ver -lt [version]$script:ExpectedFeedGuardVersion)) {
                $notes.Add("Version $(if ($ver) { $ver } else { 'before 5.0.0' }) is older than this Guard expects ($script:ExpectedFeedGuardVersion)."); $warn = $true; $rem = $importRemedy
            } else { $notes.Add("version $ver") }
            if ($arH.watchdogConfigured -and -not $arH.watchdogResolved) {
                $notes.Add("No name in the watch list ($($arH.watchdogConfigured)) is an instrument NinjaTrader knows, so the feed is not watched."); $warn = $true
                $rem = 'Fix the Watch list: write instruments as NinjaTrader names them, e.g. NQ ##-## for the current contract.'
            }
            $sc = $arH.selfChecks
            if ($sc) {
                if ($sc.feedProvider -eq 'unavailable') { $notes.Add('On this NinjaTrader version it cannot tell which connection serves the watch list.'); $warn = $true }
                if ($sc.reEnable -eq 'unavailable') { $notes.Add('On this NinjaTrader version it cannot switch strategies back on after a recovery; it alerts instead.'); $warn = $true }
                if ($sc.rollover -eq 'rule') { $notes.Add('Contract months follow the quarterly rule (NinjaTrader''s roll dates were unreadable).') }
            }
            if (-not $arH.autoRecover) { $notes.Add('auto-recovery off') }
            $c.Detail = ($notes -join ' ')
            if ($warn) { $c.State = 'warn'; $c.Headline = 'Running, with a problem'; $c.Remedy = $rem }
        }
        if ($a.k -eq 'link' -and $c.State -eq 'ok') { $c.Detail = "answered $(Format-GuardAge (Get-FileAgeSeconds $brPath $now)) ago" }
        $cards.Add($c)
    }

    # ---- Strategy backups
    $c = New-Card backups 'Strategy backups'
    $list = Get-GuardBackupList -GuardHome $GuardHome -NowUtc $now -TimeZone $TimeZone
    if ($list.Error -eq 'none') {
        if ($nt) { Set-Card $c warn 'No backup yet' "Guard saves every strategy's settings every $($th.backupEveryMinutes) min while NinjaTrader runs." }
        else { Set-Card $c idle 'No backup yet' 'The first save happens while NinjaTrader runs.' }
    } elseif ($list.Error) {
        Set-Card $c bad 'Backup list unreadable' $list.Error 'The next save writes a new one; old templates stay in NinjaTrader''s templates folder.'
    } else {
        $rows = @($list.Rows)
        $live = @($rows | Where-Object State -eq 'live').Count
        $miss = @($rows | Where-Object State -eq 'missing')
        $onChart = @($rows | Where-Object { $_.State -eq 'live' -and $_.Chart }).Count
        $ageS = $(if ($list.SavedUtc) { ($now - $list.SavedUtc).TotalSeconds } else { $null })
        $detail = "$onChart on a chart"
        if ($null -ne $ageS) { $detail += " - last saved $(Format-GuardAge $ageS) ago" }
        $failed = @($events | Where-Object { $_.kind -eq 'backups.save-failed' -and $list.SavedUtc -and (ConvertFrom-GuardUtc ([string]$_.utc)) -gt $list.SavedUtc } | Select-Object -First 1)
        if ($miss.Count) {
            Set-Card $c warn "$(Format-GuardCount $live strategy strategies) saved, $($miss.Count) missing" "Missing from NinjaTrader's list: $(@($miss.Name) -join ', '). $detail" (Get-BackupAdvice -Config $Config -Kind missing)
        } elseif ($failed.Count) {
            Set-Card $c warn "$(Format-GuardCount $live strategy strategies) saved" "The last save failed: $(& $mask ([string]$failed[0].text)). $detail"
        } elseif ($nt -and $null -ne $ageS -and $ageS -gt 2 * 60 * [double]$th.backupEveryMinutes) {
            Set-Card $c warn "$(Format-GuardCount $live strategy strategies) saved" $detail 'Saves happen while the overwatch runs - see the Watchdogs card.'
        } else { Set-Card $c ok "$(Format-GuardCount $live strategy strategies) saved" $detail }
    }
    $cards.Add($c)

    # ---- Phone alerts
    $c = New-Card alerts 'Phone alerts'
    $al = $Config.alerts
    $last = @($events | Where-Object { $_.kind -eq 'alert.sent' } | Select-Object -First 1)
    $chan = (@($(if ($al.ntfyTopic) { 'phone' }), $(if ($al.webhookUrl) { 'webhook' })) | Where-Object { $_ }) -join ' + '
    if (-not $chan) { Set-Card $c warn 'Not set up' 'Alerts are only written to the log.' 'Set up phone alerts in the Alerts tab.' }
    elseif ($last.Count -and (([string]$last[0].data.ntfy -like 'failed*') -or ([string]$last[0].data.webhook -like 'failed*'))) {
        $why = (@([string]$last[0].data.ntfy, [string]$last[0].data.webhook) | Where-Object { $_ -like 'failed*' }) -join '; '
        Set-Card $c bad 'Last alert did not reach you' "$(& $T (ConvertFrom-GuardUtc ([string]$last[0].utc))) '$($last[0].text)': $(& $mask $why)" 'Check the address in the Alerts tab and send a test.'
    } else {
        $d = $(if ($last.Count) { "Last alert $(& $T (ConvertFrom-GuardUtc ([string]$last[0].utc))): $(& $mask ([string]$last[0].text))" } else { 'No alert sent yet - send a test from the Alerts tab.' })
        Set-Card $c ok "On: $chan" $d
    }
    $cards.Add($c)

    # ---- banners, overall, events
    $banner = New-Object System.Collections.Generic.List[string]
    if ($Config.loadError) { $banner.Add((& $mask ([string]$Config.loadError))) }
    # Stopped, yet the feed guard still reports recovery on well after the stop: it never took Stop's file.
    $arAge = Get-FileAgeSeconds $arPath $now
    if ($stop.Stopped -and $arH -and $arH.autoRecover -eq $true -and $stop.SinceUtc -and $null -ne $arAge -and $now.AddSeconds(-$arAge) -gt $stop.SinceUtc.AddSeconds(60)) {
        $banner.Add('Guard is stopped, but the feed guard inside NinjaTrader still has recovery on: it did not take the stop''s settings. Press Start Guard, then Stop Guard again.')
    }
    if ($stop.Leftover) { $banner.Add('Guard was stopped from this panel, then its tasks were switched on outside it: the watchdogs run, but the feed guard still only watches (no reconnect, no recovery, no alert). Press Start Guard in Tools to give it your settings back.') }
    foreach ($f in $pause.LegacySwitches) { $banner.Add("$f in NinjaTrader's folder keeps that watchdog paused until the file is deleted (an older way to pause; use Pause instead).") }
    $states = @($cards | ForEach-Object { $_.State })
    if ($stop.Stopped) { $overall = @{ State = 'stopped'; Text = "STOPPED$stopWhen" } }
    elseif ($pause.Paused) { $overall = @{ State = 'paused'; Text = "PAUSED until $(& $T $pause.UntilUtc)" } }
    elseif ($states -contains 'bad') { $overall = @{ State = 'bad'; Text = 'ATTENTION' } }
    elseif ($states -contains 'warn' -or $banner.Count) { $overall = @{ State = 'warn'; Text = 'CHECK' } }
    else { $overall = @{ State = 'ok'; Text = 'ALL GOOD' } }

    $ev = @($events | Select-Object -First $MaxEvents | ForEach-Object {
        $u = ConvertFrom-GuardUtc ([string]$_.utc)
        $src = $(if ($script:Labels.ContainsKey([string]$_.component)) { $script:Labels[[string]$_.component] } else { [string]$_.component })
        [pscustomobject]@{ Utc = $u; Time = $(if ($u) { & $T $u } else { '' }); Source = $src; Kind = [string]$_.kind
                           Severity = [string]$_.severity; Text = (& $mask ([string]$_.text)) }
    })
    return [pscustomobject]@{ NowUtc = $now; Overall = [pscustomobject]$overall; Pause = $pause; Stop = $stop; Banner = $banner.ToArray()
                              Cards = $cards.ToArray(); Events = $ev; Nt8Running = [bool]$nt }
}

function Get-GuardBackupList {
    # The Strategy backups tab: one row per strategy in the backup registry (state\backups\rows.json),
    # missing first, then those on NinjaTrader's list, then those removed by the user.
    # Returns @{ Error ('' | 'none' = no backup yet | a sentence); SavedUtc; Rows }.
    [CmdletBinding()] param([string]$GuardHome, [Nullable[DateTime]]$NowUtc, [TimeZoneInfo]$TimeZone)
    $now = $(if ($null -ne $NowUtc) { [DateTime]$NowUtc } else { [DateTime]::UtcNow })
    $path = Join-Path (Get-GuardPath backups $GuardHome) 'rows.json'
    if (-not (Test-Path -LiteralPath $path)) { return [pscustomobject]@{ Error = 'none'; SavedUtc = $null; Rows = @() } }
    $doc = Read-JsonFile $path
    if (-not $doc -or $doc.version -ne 2 -or $null -eq $doc.rows) {
        return [pscustomobject]@{ Error = 'rows.json is not a backup list this Guard can read.'; SavedUtc = $null; Rows = @() }
    }
    $order = @{ missing = 0; live = 1; removed = 2 }
    $words = @{ missing = 'missing'; live = 'on the list'; removed = 'removed by you' }
    $rows = foreach ($p in $doc.rows.PSObject.Properties) {
        $r = $p.Value
        $cls = [string]$r.strategies.Classname
        $short = $cls -replace '^NinjaTrader\.NinjaScript\.Strategies\.', ''
        $charts = @($r.charts | Where-Object { $_ } | ForEach-Object { "$($_.workspace) - $($_.tabName)" } | Select-Object -Unique)
        $seen = ConvertFrom-GuardUtc ([string]$r.lastSeenUtc)
        $st = [string]$r.state
        [pscustomobject]@{
            Key = $p.Name; Name = [string]$r.strategies.Name; Strategy = $short; State = $st
            StateText = $(if ($words.ContainsKey($st)) { $words[$st] } else { $st })
            Account = (@($r.labels.accounts) | Where-Object { $_ }) -join ', '
            Instrument = (@($r.labels.instruments) | Where-Object { $_ }) -join ', '
            Chart = ($charts -join '; ')
            LastSaved = $(if ($seen) { Format-GuardTime -Utc $seen -NowUtc $now -TimeZone $TimeZone } else { '' })
            Order = $(if ($order.ContainsKey($st)) { $order[$st] } else { 3 })
        }
    }
    $sorted = @($rows | Sort-Object Order, Name)
    return [pscustomobject]@{ Error = ''; SavedUtc = (ConvertFrom-GuardUtc ([string]$doc.utc)); Rows = $sorted }
}

function Get-GuardWatchInfo {
    # The Watch list tab: what guard.json asks to watch, and what the feed guard made of it - the
    # current contracts, the connection serving them, and the next scheduled closure.
    [CmdletBinding()] param([string]$GuardHome, [string]$Nt8Dir, [hashtable]$Config, [Nullable[DateTime]]$NowUtc)
    $now = $(if ($null -ne $NowUtc) { [DateTime]$NowUtc } else { [DateTime]::UtcNow })
    if (-not $Config) { $Config = Get-GuardConfig $GuardHome }
    if (-not $Nt8Dir) { $Nt8Dir = Get-Nt8DataDir ([string]$Config.nt8.dataDir) }
    $hbPath = Join-Path $Nt8Dir 'AutoReconnect.heartbeat.json'
    $age = Get-FileAgeSeconds $hbPath $now
    $h = Read-JsonFile $hbPath
    $note = ''
    if (-not $h) { $note = 'The feed guard has not reported yet, so the current contracts are not known.' }
    elseif ($age -gt [double]$Config.thresholds.guardianStaleSeconds) { $note = "The feed guard last reported $(Format-GuardAge $age) ago; this may be out of date." }
    $split = { param($s) @(([string]$s) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    return [pscustomobject]@{
        Configured   = @($Config.watch.instruments)
        Resolved     = $(if ($h) { & $split $h.watchdogResolved } else { @() })
        Providers    = $(if ($h) { [string]$h.feedProviders } else { '' })
        NextClosure  = $(if ($h) { [string]$h.nextClosure } else { '' })
        StaleSeconds = $Config.watch.staleSeconds
        ExtraDates   = @($Config.watch.extraDates)
        Note         = $note
    }
}

Export-ModuleMember -Function Get-GuardExpectedFeedGuardVersion, Format-GuardTime, Format-GuardAge, Format-GuardCount, Get-GuardPanelPause, Test-GuardMarketClosedByClock,
                              Get-GuardStatus, Get-GuardBackupList, Get-GuardWatchInfo
