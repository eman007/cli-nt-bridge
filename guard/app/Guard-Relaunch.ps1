# Guard-Relaunch.ps1 - keep a HEALTHY NinjaTrader 8 running. Scheduled task, every minute.
#
# Owns "NinjaTrader is DOWN" (crash, closed by hand, killed by the overwatch) and "NinjaTrader is up
# but stuck at its login window". The overwatch owns "up but sick".
#
#   * PAUSE, LOCKED SCREEN, BACKOFF, REFUSED PASSWORD: the shared gate (Test-Nt8StartAllowed in
#     Guard.Start) - the same rules hold every starter. A locked screen holds starts until the unlock
#     (logged once); the probe FAILS OPEN.
#   * STUCK AT LOGIN: NinjaTrader still titled 'Welcome' after stuckMinutes -> kill it so the next start
#     can log in. Sooner (loginDeadSeconds) only for a NinjaTrader GUARD launched whose login helper has
#     gone: one the user opened by hand gets the full stuckMinutes.
#   * DISCONNECTED REMOTE SESSION: reattach it to the console (tscon) so the login can type.
#   * Starting NinjaTrader always goes through Invoke-Nt8Start (put strategies back, unlock wait, log in).
# Only NinjaTrader and the sign-in screen of THIS Windows session count.
# Protection 'relaunch' off: alert once per outage, never act.
[CmdletBinding()]
param([string]$GuardHome, [switch]$ImportOnly)

$ErrorActionPreference = 'SilentlyContinue'
foreach ($m in 'Guard.Paths', 'Guard.Log', 'Guard.Config', 'Guard.Events', 'Guard.Alerts', 'Guard.Pause', 'Guard.Start') {
    Import-Module (Join-Path $PSScriptRoot "lib\$m.psm1")
}

function Get-StateFile { param([string]$Name, [string]$GuardHome) Join-Path (Get-GuardPath state $GuardHome) $Name }

# ------------------------------------------------------------------------------------------------
# One cycle. $Ops (scriptblocks, all replaceable by tests):
#   IsLocked() -> bool            Nt8() -> @{ Id; Title; UpMinutes; StartUtc } | $null      Kill($id)
#   LoginInFlight() -> bool       Session() -> @{ Id; State }                                 Tscon($id)
#   Start() -> result object      Now() -> [DateTime] UTC
# Returns the outcome as a word, for the log and for tests.
# ------------------------------------------------------------------------------------------------
function Invoke-RelaunchCycle {
    param([Parameter(Mandatory)][hashtable]$Config, [Parameter(Mandatory)][hashtable]$Ops, [string]$GuardHome, [string]$Nt8Dir)
    $log = { param($m) Write-GuardLog -Component relaunch -Message $m -GuardHome $GuardHome }
    $now = & $Ops.Now

    # proof of life, every run and before anything that can fail - absence is the alarm
    try { [IO.File]::WriteAllText((Get-StateFile 'relaunch.heartbeat' $GuardHome), $now.ToString('o')) } catch { }

    $rl = $Config.relaunch
    $prot = Get-GuardProtection $Config
    $gate = Test-Nt8StartAllowed -Component relaunch -Config $Config -GuardHome $GuardHome -Nt8Dir $Nt8Dir -IsLocked $Ops.IsLocked -Now $Ops.Now
    if ($gate -ne 'ok') { return $gate }

    $downAlerted = Get-StateFile 'relaunch.downalerted' $GuardHome
    $nt = & $Ops.Nt8
    if ($nt) {
        if ($nt.Title -ne 'Welcome') {
            if (Test-Path $downAlerted) { Remove-Item $downAlerted -Force }
            Set-Nt8SeenHealthy $GuardHome
            return 'healthy'
        }
        # Only a NinjaTrader Guard itself started (process start within moments of our launch) can be
        # judged by its login helper; one the user opened by hand gets the full stuckMinutes.
        $last = Get-Nt8LastLaunchUtc $GuardHome
        $ours = $last -and $nt.StartUtc -and ($nt.StartUtc -ge $last.AddSeconds(-10)) -and ($nt.StartUtc -le $last.AddMinutes(5))
        $loginDead = $ours -and (-not (& $Ops.LoginInFlight)) -and (($now - $last).TotalSeconds -ge [int]$rl.loginDeadSeconds)
        if (-not $loginDead -and $nt.UpMinutes -lt [int]$rl.stuckMinutes) { return 'starting' }
        if (-not $prot.relaunch) {
            if (-not (Test-Path $downAlerted)) {
                [IO.File]::WriteAllText($downAlerted, $now.ToString('o'))
                Send-GuardAlert -Title 'NT8 stuck at its login window' -Body 'NinjaTrader is sitting at its login window. Restarting it is switched off in Protection, so Guard is leaving it alone.' `
                                -Severity alert -Config $Config -Component relaunch -GuardHome $GuardHome | Out-Null
            }
            return 'stuck-relaunch-off'
        }
        $why = $(if ($loginDead) { "the login helper finished without logging in ($($rl.loginDeadSeconds)s after the launch)" } else { 'the Control Center never loaded' })
        & $log "NinjaTrader STUCK at its login window for $([math]::Round($nt.UpMinutes))m ($why) -> stopping it so the next start can log in"
        Write-GuardEvent -Component relaunch -Kind 'nt8.stuck' -Severity warn -Text "NinjaTrader stuck at its login window ($why)" -GuardHome $GuardHome
        & $Ops.Kill $nt.Id
    }

    if (& $Ops.LoginInFlight) { return 'in-flight' }

    $last = Get-Nt8LastLaunchUtc $GuardHome
    if ($last -and ($now - $last).TotalMinutes -lt [double]$rl.graceMinutes) { return 'grace' }

    if (-not $prot.relaunch) {
        if (-not (Test-Path $downAlerted)) {
            [IO.File]::WriteAllText($downAlerted, $now.ToString('o'))
            Send-GuardAlert -Title 'NT8 is DOWN' -Body 'NinjaTrader is not running. Starting it is switched off in Protection, so Guard is leaving it down.' `
                            -Severity alert -Config $Config -Component relaunch -GuardHome $GuardHome | Out-Null
        }
        return 'down-relaunch-off'
    }

    # the action budget and the refused-password hold, just before a start
    $gate = Test-Nt8StartAllowed -Component relaunch -Config $Config -GuardHome $GuardHome -Nt8Dir $Nt8Dir -IsLocked $Ops.IsLocked -Now $Ops.Now -ForLaunch
    if ($gate -ne 'ok') { return $gate }

    # A DISCONNECTED remote session has no desktop the login can type into; reattach it to the console.
    $s = & $Ops.Session
    if ($s -and $s.Id -ne 0 -and "$($s.State)" -like 'Disc*') {
        & $log "session $($s.Id) is disconnected -> attaching it to the console so the login can type"
        & $Ops.Tscon $s.Id
    }

    $r = & $Ops.Start
    return $(if ($r -and $r.Result) { [string]$r.Result } else { 'launched' })
}

# ------------------------------------------------------------------------------------------------
# The real operations.
# ------------------------------------------------------------------------------------------------
function Get-SessionState {
    $sid = (Get-Process -Id $PID).SessionId
    $state = ''
    try {
        foreach ($line in @(qwinsta 2>$null)) {
            $tok = ($line.Trim() -replace '^>', '') -split '\s+'
            for ($i = 0; $i -lt ($tok.Count - 1); $i++) {
                if ($tok[$i] -eq "$sid" -and $tok[$i + 1] -match '^(Active|Conn|Disc|Listen)$') { $state = $tok[$i + 1]; break }
            }
            if ($state) { break }
        }
    } catch { }
    return @{ Id = $sid; State = $state }
}

if ($ImportOnly) { return }

$cfg = Get-GuardConfig -GuardHome $GuardHome
$nt8 = Get-Nt8DataDir $cfg.nt8.dataDir
Publish-GuardConfigProblem -Config $cfg -Component relaunch -GuardHome $GuardHome
$ops = @{
    IsLocked      = { [bool](@(Get-GuardSessionProcess -Name LogonUI).Count) }
    Nt8           = {
        $p = @(Get-GuardSessionProcess -Name NinjaTrader) | Select-Object -First 1
        if (-not $p) { return $null }
        $start = $null; $up = 999
        try { $start = $p.StartTime.ToUniversalTime(); $up = ([DateTime]::UtcNow - $start).TotalMinutes } catch { }
        return @{ Id = $p.Id; Title = $p.MainWindowTitle; UpMinutes = $up; StartUtc = $start }
    }
    Kill          = { param($id) try { Stop-Process -Id $id -Force } catch { }; Start-Sleep -Seconds 3 }
    LoginInFlight = { Test-LoginInFlight -Config $cfg -GuardHome $GuardHome }
    Session       = { Get-SessionState }
    Tscon         = { param($id) try { & tscon.exe $id /dest:console 2>&1 | Out-Null; Start-Sleep -Seconds 2 } catch { } }
    Start         = { Invoke-Nt8Start -Component relaunch -Config $cfg -GuardHome $GuardHome -Nt8Dir $nt8 }
    Now           = { [DateTime]::UtcNow }
}
try {
    $outcome = Invoke-RelaunchCycle -Config $cfg -Ops $ops -GuardHome $GuardHome -Nt8Dir $nt8
    if ($outcome -notin 'healthy', 'paused', 'held-locked', 'backing-off', 'starting', 'grace', 'in-flight') {
        Write-GuardLog -Component relaunch -Message "cycle: $outcome" -GuardHome $GuardHome
    }
} catch {
    Write-GuardLog -Component relaunch -Message "cycle FAILED: $($_.Exception.GetType().Name): $($_.Exception.Message)" -GuardHome $GuardHome
}
