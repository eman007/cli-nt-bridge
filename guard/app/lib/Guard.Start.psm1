# The ONE way NinjaTrader is started (relauncher and overwatch both use it):
#   1. put back strategies NinjaTrader dropped from its list, and back on their charts (NT8 closed)
#   2. wait for Config.xml to be released by the NinjaTrader that just died
#   3. run the login helper (built-in Guard-Login.ps1, or a configured program such as NTLogin.exe)
# Plus the shared probes: is a login already in flight, and the bundled Python.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Log.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Config.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Events.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Alerts.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Pause.psm1')

function ConvertTo-GuardArg {
    # One argument quoted for a Windows command line (CommandLineToArgvW rules).
    [CmdletBinding()] param([AllowEmptyString()][string]$Arg)
    if ($Arg -and $Arg -notmatch '[\s"]') { return $Arg }
    $q = $Arg -replace '(\\*)"', '$1$1\"'
    $q = $q -replace '(\\+)$', '$1$1'
    return '"' + $q + '"'
}

function Get-GuardPythonExe {
    # guard.json python.exe, else the bundled runtime next to the backup tools. Never python.exe from
    # PATH: on a stock Windows that is the Microsoft Store stub, which opens the Store instead of running.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config)
    $c = [string]$Config.python.exe
    if ($c -and $c -ne 'auto') { return $c }
    $bundled = Join-Path (Get-GuardToolsDir) 'python.exe'
    if (Test-Path $bundled) { return $bundled }
    return $null
}

function Invoke-GuardPython {
    # Run one backup tool, bounded. @{ ExitCode; Out; Err; TimedOut } - ExitCode -1 = could not run.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Script, [string[]]$Arguments = @(), [int]$TimeoutSeconds = 60,
          [Parameter(Mandatory)][hashtable]$Config, [string]$ToolsDir)
    $exe = Get-GuardPythonExe $Config
    if (-not $exe) { return [pscustomobject]@{ ExitCode = -1; Out = ''; TimedOut = $false
                                               Err = 'no Python runtime: python\python.exe is missing and guard.json python.exe is not set' } }
    if (-not $ToolsDir) { $ToolsDir = Get-GuardToolsDir }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ((@((Join-Path $ToolsDir $Script)) + $Arguments) | ForEach-Object { ConvertTo-GuardArg $_ }) -join ' '
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    try { $p = [System.Diagnostics.Process]::Start($psi) } catch {
        return [pscustomobject]@{ ExitCode = -1; Out = ''; Err = $_.Exception.Message; TimedOut = $false }
    }
    # Read both pipes asynchronously: a child that fills one pipe's buffer while we wait on exit deadlocks.
    $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        try { $p.Kill() } catch { }
        return [pscustomobject]@{ ExitCode = -1; Out = ''; Err = "did not finish in ${TimeoutSeconds}s - stopped"; TimedOut = $true }
    }
    $p.WaitForExit()
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Out = $o.Result; Err = $e.Result; TimedOut = $false }
}

function Get-GuardSessionProcess {
    # Processes of this name in THIS Windows session only. Another user's NinjaTrader, or the sign-in
    # screen (LogonUI) of another session - fast user switching, a remote session - is not ours.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Name)
    $sid = (Get-Process -Id $PID).SessionId
    return @(Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid })
}

function Get-LoginInflightPath { [CmdletBinding()] param([string]$GuardHome) Join-Path (Get-GuardPath state $GuardHome) 'login.inflight' }

function Set-LoginInflight {
    # Written by the STARTER the moment it launches the login helper (still under the start lock), so
    # there is no gap in which a second starter sees no login in flight. Atomic: temp file, then move.
    [CmdletBinding()] param([Parameter(Mandatory)][int]$ProcessId, [string]$GuardHome)
    $f = Get-LoginInflightPath $GuardHome
    $tmp = "$f.$PID.tmp"
    try {
        [IO.File]::WriteAllText($tmp, ([ordered]@{ pid = $ProcessId; startedUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Compress))
        # Over an existing marker (the starter wrote it, the helper writes it again; or a dead helper left one):
        # File.Replace with NO backup. Not $null - PowerShell hands a .NET string parameter "" for $null, and
        # "" is "not of a legal form" (cold test 2026-10-10: every start through the built-in login died here).
        if (Test-Path -LiteralPath $f) { [IO.File]::Replace($tmp, $f, [NullString]::Value) }
        else { try { [IO.File]::Move($tmp, $f) } catch { [IO.File]::Replace($tmp, $f, [NullString]::Value) } }
    } finally {
        if (Test-Path -LiteralPath $tmp) { try { [IO.File]::Delete($tmp) } catch { } }
    }
}

function Test-LoginInFlight {
    # A login is in flight if the built-in helper's marker names a live process started < 10 min ago
    # (other than -ExceptPid: the login helper itself), or a configured login program started < 10 min
    # ago is running in this session. A login program running longer than that is hung, not in flight.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [int]$ExceptPid = -1,
                            [scriptblock]$GetProcess)
    if (-not $GetProcess) { $GetProcess = { param($n) Get-GuardSessionProcess -Name $n } }
    $f = Get-LoginInflightPath $GuardHome
    if (Test-Path $f) {
        try {
            $o = [IO.File]::ReadAllText($f) | ConvertFrom-Json
            $age = ([DateTime]::UtcNow - (ConvertFrom-GuardUtc $o.startedUtc)).TotalMinutes
            if ([int]$o.pid -ne $ExceptPid -and $age -lt 10 -and (Get-Process -Id ([int]$o.pid) -ErrorAction Stop)) { return $true }
        } catch { }
    }
    $h = [string]$Config.login.helper
    if ($h -and $h -ne 'builtin') {
        foreach ($p in @(& $GetProcess ([IO.Path]::GetFileNameWithoutExtension($h)))) {
            try { if (([DateTime]::UtcNow - $p.StartTime.ToUniversalTime()).TotalMinutes -lt 10) { return $true } } catch { }
        }
    }
    return $false
}

function Stop-StaleLoginHelper {
    # A configured login program still running 10 minutes after it started is hung (an error dialog,
    # a lost window): stop it, or no start would ever happen again.
    [CmdletBinding()] param([string]$Component = 'relaunch', [Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome,
                            [scriptblock]$GetProcess, [scriptblock]$Kill)
    $h = [string]$Config.login.helper
    if (-not $h -or $h -eq 'builtin') { return }
    if (-not $GetProcess) { $GetProcess = { param($n) Get-GuardSessionProcess -Name $n } }
    if (-not $Kill) { $Kill = { param($id) Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }
    $name = [IO.Path]::GetFileNameWithoutExtension($h)
    foreach ($p in @(& $GetProcess $name)) {
        try {
            $mins = ([DateTime]::UtcNow - $p.StartTime.ToUniversalTime()).TotalMinutes
            if ($mins -ge 10) {
                Write-GuardLog -Component $Component -Message "login program $name (pid $($p.Id)) hung for $([math]::Round($mins)) min -> stopping it" -GuardHome $GuardHome
                & $Kill $p.Id
            }
        } catch { }
    }
}

function Get-GuardStartLockPath { [CmdletBinding()] param([string]$GuardHome) Join-Path (Get-GuardPath state $GuardHome) 'start.lock' }

function Open-GuardStartLock {
    # The start lock: an exclusive open of state\start.lock. Returns the open file (Dispose() releases
    # it; Windows releases it if the process dies) or $null when someone else holds it. Held by every
    # start AND by every strategy-backup save, so a save never runs while a start is putting strategies
    # back - both write the backup registry.
    [CmdletBinding()] param([string]$GuardHome)
    try { return [IO.File]::Open((Get-GuardStartLockPath $GuardHome), 'OpenOrCreate', 'ReadWrite', 'None') } catch { return $null }
}

function Set-Nt8SeenHealthy {
    # Stamped whenever a watchdog sees NinjaTrader logged in; lifts a refused-password hold.
    [CmdletBinding()] param([string]$GuardHome)
    try { [IO.File]::WriteAllText((Join-Path (Get-GuardPath state $GuardHome) 'nt8.healthy'), [DateTime]::UtcNow.ToString('o')) } catch { }
}

function Test-Nt8StartAllowed {
    # Spec 3.4 step 1, shared by every starter (the relauncher, and the overwatch when the relauncher is
    # not running). Returns 'ok' or why not:
    #   paused | backing-off | held-locked                       (always checked)
    #   relaunch-off | login-refused | backoff                   (with -ForLaunch, just before a start)
    # LOCKED SCREEN: the login cannot reach NinjaTrader's window, so starting only burns the budget and
    # then nothing starts for an hour after the unlock. Logged once per lock. The probe FAILS OPEN.
    # BACKOFF: maxActions starts/stops in windowMinutes -> no starts for backoffMinutes, doubling for
    # each backoff in the last 24 h (24 h at most). A retry storm against a failing login is what gets
    # a broker account flagged.
    # LOGIN REFUSED (built-in login): the last two logins both left NinjaTrader at its login window
    # after the password was entered -> no starts until the password is saved again or NinjaTrader is
    # seen logged in. A wrong saved password must not be retried forever.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Component, [Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome,
          [string]$Nt8Dir, [scriptblock]$IsLocked, [scriptblock]$Now, [switch]$ForLaunch)
    if (-not $IsLocked) { $IsLocked = { [bool](@(Get-GuardSessionProcess -Name LogonUI).Count) } }
    if (-not $Now) { $Now = { [DateTime]::UtcNow } }
    $t = & $Now
    $state = Get-GuardPath state $GuardHome
    $log = { param($m) Write-GuardLog -Component $Component -Message $m -GuardHome $GuardHome }
    $rl = $Config.relaunch

    if ((Get-GuardPauseState -Component $Component -GuardHome $GuardHome -Nt8Dir $Nt8Dir -NowUtc $t).Paused) { return 'paused' }

    $backoff = Join-Path $state 'start.backoff-until'
    if (Test-Path $backoff) {
        $until = ConvertFrom-GuardUtc ([IO.File]::ReadAllText($backoff).Trim())
        if ($until -and $t -lt $until) { return 'backing-off' }
        Remove-Item $backoff -Force -ErrorAction SilentlyContinue
        & $log 'backoff window elapsed -> starting NinjaTrader is allowed again'
    }

    $lockStamp = Join-Path $state "$Component.lockedsince"
    $locked = $false
    try { $locked = [bool](& $IsLocked) } catch { $locked = $false }
    if ($locked) {
        if (-not (Test-Path $lockStamp)) {
            [IO.File]::WriteAllText($lockStamp, $t.ToString('o'))
            & $log "screen is LOCKED: the login cannot reach NinjaTrader's window, so NinjaTrader is not started until the screen is unlocked"
        }
        return 'held-locked'
    }
    if (Test-Path $lockStamp) {
        $since = ConvertFrom-GuardUtc ([IO.File]::ReadAllText($lockStamp).Trim())
        $mins = $(if ($since) { [math]::Round(($t - $since).TotalMinutes) } else { '?' })
        & $log "screen UNLOCKED after $mins min -> starting NinjaTrader is allowed again"
        Remove-Item $lockStamp -Force -ErrorAction SilentlyContinue
    }
    if (-not $ForLaunch) { return 'ok' }

    # Protection 'Start NinjaTrader when it is down' off (Watch only): no starter starts it.
    if (-not (Get-GuardProtection $Config).relaunch) { return 'relaunch-off' }

    $helper = [string]$Config.login.helper
    $hold = Join-Path $state 'start.loginhold'
    if (-not $helper -or $helper -eq 'builtin') {
        $last = @(Read-GuardEvents -GuardHome $GuardHome -Nt8Dir $Nt8Dir -Max 2 -KindPattern '^login\.(ok|failed)$')
        $refused = @($last | Where-Object { $_.kind -eq 'login.failed' -and $_.data -and $_.data.result -eq 'gave-up' })
        if ($last.Count -eq 2 -and $refused.Count -eq 2) {
            $failUtc = ConvertFrom-GuardUtc ([string]$last[0].utc)
            $cred = Join-Path (Get-GuardPath secrets $GuardHome) 'nt8.cred'
            $savedUtc = $(if (Test-Path $cred) { (Get-Item $cred).LastWriteTimeUtc } else { $null })
            $seen = Join-Path $state 'nt8.healthy'
            $seenUtc = $(if (Test-Path $seen) { ConvertFrom-GuardUtc ([IO.File]::ReadAllText($seen).Trim()) } else { $null })
            if (-not (($savedUtc -and $savedUtc -gt $failUtc) -or ($seenUtc -and $seenUtc -gt $failUtc))) {
                if (-not (Test-Path $hold)) {
                    [IO.File]::WriteAllText($hold, $t.ToString('o'))
                    $msg = 'NinjaTrader stayed at its login window after the saved password was entered, twice in a row. Guard stopped starting it so a wrong password is not retried until the account is locked or flagged. Save the password again (NT8 Guard: Tools > NinjaTrader login) or log in by hand; Guard resumes either way.'
                    & $log "LOGIN REFUSED twice -> not starting NinjaTrader. $msg"
                    Write-GuardEvent -Component $Component -Kind 'nt8.loginhold' -Severity alert -Text 'Stopped starting NinjaTrader: the saved password was refused twice' -GuardHome $GuardHome
                    Send-GuardAlert -Title 'NT8 login refused - restarts stopped' -Body $msg -Severity alert -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
                }
                return 'login-refused'
            }
        }
    }
    if (Test-Path $hold) { Remove-Item $hold -Force -ErrorAction SilentlyContinue }

    $cut = $t.AddMinutes(-[double]$rl.windowMinutes)
    $recent = @(Read-GuardEvents -GuardHome $GuardHome -Nt8Dir $Nt8Dir -Max 1000 -KindPattern '^nt8\.(launch|stuck)$' -SinceUtc $cut).Count
    if ($recent -ge [int]$rl.maxActions) {
        $trips = @(Read-GuardEvents -GuardHome $GuardHome -Nt8Dir $Nt8Dir -Max 100 -KindPattern '^nt8\.backoff$' -SinceUtc $t.AddHours(-24)).Count
        $mins = [math]::Min(1440, [double]$rl.backoffMinutes * [math]::Pow(2, $trips))
        [IO.File]::WriteAllText($backoff, $t.AddMinutes($mins).ToString('o'))
        & $log "BACKOFF: $recent starts/stops in $($rl.windowMinutes) min -> not starting NinjaTrader again for $mins min"
        Write-GuardEvent -Component $Component -Kind 'nt8.backoff' -Severity alert -Text "Stopped restarting NinjaTrader for $mins min" -GuardHome $GuardHome
        Send-GuardAlert -Title 'NT8 restarts PAUSED (backoff)' `
                        -Body "NinjaTrader was started or stopped $recent times in $($rl.windowMinutes) minutes and is still not staying up. Guard stopped trying for $mins minutes (each further backoff today doubles it) so it does not hammer a failing login - a retry storm can get a broker account flagged. Check the NinjaTrader login by hand." `
                        -Severity alert -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
        return 'backoff'
    }
    return 'ok'
}

function Wait-Nt8ConfigUnlocked {
    # NinjaTrader holds Config.xml open; a login program that reads it while the dying NT8 still holds
    # it can crash before typing the password. A dying NT8 frees it within about a second.
    [CmdletBinding()] param([string]$Nt8Dir, [int]$Seconds = 20)
    $cfgXml = Join-Path (Get-Nt8DataDir $Nt8Dir) 'Config.xml'
    if (-not (Test-Path $cfgXml)) { return $true }
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ($true) {
        try { $fs = [IO.File]::Open($cfgXml, 'Open', 'ReadWrite', 'None'); $fs.Close(); return $true } catch { }
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        Start-Sleep -Seconds 1
    }
}

function Start-GuardLogin {
    # Launch the login helper hidden; returns the process (or $null). The built-in helper's in-flight
    # marker is written HERE, with the child's process id, before returning.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [string]$Component = 'relaunch')
    $h = [string]$Config.login.helper
    if ($h -and $h -ne 'builtin') {
        Stop-StaleLoginHelper -Component $Component -Config $Config -GuardHome $GuardHome
        return (Start-Process -FilePath $h -WorkingDirectory (Split-Path $h -Parent) -WindowStyle Hidden -PassThru)
    }
    $ps1 = Join-Path (Get-GuardAppDir) 'Guard-Login.ps1'
    $args_ = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-STA', '-WindowStyle', 'Hidden',
               '-File', (ConvertTo-GuardArg $ps1), '-GuardHome', (ConvertTo-GuardArg (Get-GuardHome $GuardHome)))
    $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $args_ -WindowStyle Hidden -PassThru
    if ($p) { Set-LoginInflight -ProcessId $p.Id -GuardHome $GuardHome }
    return $p
}

function Get-Nt8LastLaunchUtc {
    [CmdletBinding()] param([string]$GuardHome)
    $f = Join-Path (Get-GuardPath state $GuardHome) 'nt8.lastlaunch'
    if (-not (Test-Path $f)) { return $null }
    return (ConvertFrom-GuardUtc ([IO.File]::ReadAllText($f).Trim()))
}

function Invoke-Nt8Start {
    # Returns @{ Result; Restore = '<summary>' }. Result: 'launched' | 'already-running' | 'busy' (another
    # starter holds the start lock) | a Test-Nt8StartAllowed reason (paused, held-locked, backoff, ...).
    # -IsRunning / -IsLocked / -Python / -Launch are test seams; the defaults are the real thing.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Component, [Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome,
          [string]$Nt8Dir, [scriptblock]$IsRunning, [scriptblock]$IsLocked, [scriptblock]$Python, [scriptblock]$Launch,
          [int]$LoginWaitSeconds = 25)
    if (-not $IsRunning) { $IsRunning = { [bool](@(Get-GuardSessionProcess -Name NinjaTrader).Count) } }
    if (-not $Python) { $Python = { param($s, $a, $t) Invoke-GuardPython -Script $s -Arguments $a -TimeoutSeconds $t -Config $Config } }
    if (-not $Launch) { $Launch = { Start-GuardLogin -Config $Config -GuardHome $GuardHome -Component $Component } }
    $log = { param($m) Write-GuardLog -Component $Component -Message $m -GuardHome $GuardHome }

    if (& $IsRunning) { return [pscustomobject]@{ Result = 'already-running'; Restore = '' } }

    # One start at a time across every starter, and never during a strategy-backup save.
    $lock = Open-GuardStartLock $GuardHome
    if (-not $lock) { return [pscustomobject]@{ Result = 'busy'; Restore = '' } }
    try {
        $nt8 = Get-Nt8DataDir $(if ($Nt8Dir) { $Nt8Dir } else { $Config.nt8.dataDir })
        $gate = Test-Nt8StartAllowed -Component $Component -Config $Config -GuardHome $GuardHome -Nt8Dir $nt8 -IsLocked $IsLocked -ForLaunch
        if ($gate -ne 'ok') { return [pscustomobject]@{ Result = $gate; Restore = '' } }
        return (Invoke-Nt8StartLocked -Component $Component -Config $Config -GuardHome $GuardHome -Nt8 $nt8 `
                                      -Python $Python -Launch $Launch -Log $log -LoginWaitSeconds $LoginWaitSeconds)
    } finally { $lock.Dispose() }
}

function Invoke-Nt8StartLocked {
    # The start itself, under the start lock: put strategies back, Config.xml wait, log in.
    param([string]$Component, [hashtable]$Config, [string]$GuardHome, [string]$Nt8, [scriptblock]$Python,
          [scriptblock]$Launch, [scriptblock]$Log, [int]$LoginWaitSeconds)
    $nt8 = $Nt8
    $log = $Log
    $prot = Get-GuardProtection $Config

    # 1. Put back strategies NinjaTrader dropped, then back on their charts. Bounded; a failure here
    #    is reported and the launch goes ahead - never launching is strictly worse.
    $rargs = @('--nt8-dir', $nt8, '--guard-home', (Get-GuardHome $GuardHome), '--json')
    if ($prot.restoreStrategies) { $rargs += '--apply' }
    if (-not $prot.reattachCharts) { $rargs += '--no-relink' }
    $r = & $Python 'backup_restore.py' $rargs 60
    $summary = ''
    $rep = $null
    if ($r.ExitCode -ge 0 -and $r.Out) { try { $rep = $r.Out | ConvertFrom-Json } catch { } }
    if ($null -eq $rep) {
        $summary = "strategy backups: could not run ($(([string]$r.Err).Trim()))"
        & $log $summary
        Write-GuardEvent -Component $Component -Kind 'backups.restore-failed' -Severity warn -Text $summary -GuardHome $GuardHome
    } elseif ($prot.restoreStrategies) {
        $done = @($rep.restored); $refused = @($rep.refused); $relinked = @($rep.relinked); $relinkFailed = @($rep.relinkFailed)
        $summary = "strategy backups: put back $($done.Count), refused $($refused.Count), charts fixed $($relinked.Count)"
        & $log $summary
        foreach ($n in @($rep.notes)) { & $log "  note: $n" }
        if ($done.Count) {
            $names = ($done | ForEach-Object { "$($_.name) (id $($_.id))" }) -join ', '
            Write-GuardEvent -Component $Component -Kind 'backups.restored' -Severity info -Text "Put back: $names" -GuardHome $GuardHome
            Send-GuardAlert -Title 'NT8 strategies put back' -Body "NinjaTrader had dropped these from its Strategies list; Guard put them back before starting it: $names" `
                            -Severity info -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
        }
        if ($relinked.Count) {
            Write-GuardEvent -Component $Component -Kind 'backups.relinked' -Severity info -Text ($relinked -join '; ') -GuardHome $GuardHome
        }
        if ($refused.Count -or $relinkFailed.Count) {
            $why = @($refused | ForEach-Object { "$($_.name): $($_.why)" }) + @($relinkFailed)
            Send-GuardAlert -Title 'NT8 strategy restore needs attention' -Body ($why -join "`n") -Severity alert `
                            -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
        }
    } elseif ($r.ExitCode -eq 2) {
        $missing = @($rep.planned | ForEach-Object { $_.name })
        $summary = "strategy backups: $($missing.Count) missing, putting them back is switched off"
        & $log $summary
        Send-GuardAlert -Title 'NT8 strategies missing' -Body ("NinjaTrader dropped these from its Strategies list. Putting them back is switched off in Protection, so they stay missing:`n" + ($missing -join "`n")) `
                        -Severity warn -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
    }

    # 2. Config.xml released by the NinjaTrader that just died.
    if (-not (Wait-Nt8ConfigUnlocked -Nt8Dir $nt8 -Seconds ([int]$Config.relaunch.configUnlockWaitSeconds))) {
        & $log "Config.xml still locked after $($Config.relaunch.configUnlockWaitSeconds)s - launching anyway"
    }

    # 3. Log in.
    [IO.File]::WriteAllText((Join-Path (Get-GuardPath state $GuardHome) 'nt8.lastlaunch'), [DateTime]::UtcNow.ToString('o'))
    $helper = $(if ($Config.login.helper -and $Config.login.helper -ne 'builtin') { [IO.Path]::GetFileName($Config.login.helper) } else { 'built-in login helper' })
    & $log "NinjaTrader is down -> starting it ($helper)"
    Write-GuardEvent -Component $Component -Kind 'nt8.launch' -Severity warn -Text "Starting NinjaTrader ($helper)" -GuardHome $GuardHome
    $p = & $Launch
    # A configured login program reports nothing itself: a fast failure (it crashed, NinjaTrader's
    # Config.xml was locked, ...) shows only as its exit code, so wait briefly and record it.
    $external = $Config.login.helper -and $Config.login.helper -ne 'builtin'
    if ($external -and $p -is [System.Diagnostics.Process]) {
        try {
            if ($p.WaitForExit($LoginWaitSeconds * 1000)) {
                $code = $p.ExitCode
                if ($code -ne 0) {
                    $msg = "The login program $helper ended with exit code $code (0x$($code.ToString('X8'))); NinjaTrader may be sitting at its login window."
                    & $log "ERROR: $msg"
                    Write-GuardEvent -Component $Component -Kind 'login.failed' -Severity alert -Text $msg -Data @{ result = "exit $code" } -GuardHome $GuardHome
                    Send-GuardAlert -Title 'NT8 login program failed' -Body $msg -Severity alert -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
                } else { & $log "login program $helper finished (exit code 0)" }
            } else { & $log "login program $helper still running after ${LoginWaitSeconds}s (normal while it waits for the login window)" }
        } catch { }
    }
    return [pscustomobject]@{ Result = 'launched'; Restore = $summary }
}

Export-ModuleMember -Function ConvertTo-GuardArg, Get-GuardPythonExe, Invoke-GuardPython, Get-GuardSessionProcess,
                              Get-LoginInflightPath, Set-LoginInflight, Test-LoginInFlight, Stop-StaleLoginHelper,
                              Get-GuardStartLockPath, Open-GuardStartLock, Set-Nt8SeenHealthy, Test-Nt8StartAllowed, Wait-Nt8ConfigUnlocked,
                              Start-GuardLogin, Get-Nt8LastLaunchUtc, Invoke-Nt8Start
