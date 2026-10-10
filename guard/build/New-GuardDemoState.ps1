# New-GuardDemoState.ps1 - a generic, healthy Guard home and NinjaTrader folder for the guide's screenshots
# (spec 8: screenshots captured from the real panel). Every name in it is made up: sample strategies, the
# Sim101 account, a "Data Feed" connection, a placeholder alert topic. Dot-source it, then:
#
#   $demo = New-GuardDemoState -Root <empty folder>
#   -> @{ Home; Nt8; NowUtc; StatusArgs (seams for Get-GuardStatus); SetupCheckArgs; AddonArgs }
#
# Needs Guard's modules imported (Guard.Paths, Guard.Config, Guard.Events).
function New-GuardDemoState {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Root)
    $now = [DateTime]::UtcNow
    $homeDir = Join-Path $Root 'NT8Guard'; $nt8 = Join-Path $Root 'NinjaTrader 8'
    foreach ($d in $homeDir, $nt8) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    $env:NT8GUARD_HOME = $homeDir; $env:NT8_DIR = $nt8
    $beat = { param([string]$Path, [int]$AgeSeconds, [string]$Text = 'x')
        $d = Split-Path $Path -Parent; if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
        [IO.File]::WriteAllText($Path, $Text); (Get-Item $Path).LastWriteTimeUtc = $now.AddSeconds(-$AgeSeconds) }

    $c = Get-GuardDefaults
    $c.alerts.ntfyTopic = 'nt8guard-your-own-private-topic'
    $c.setup = @{ step = 7; completed = $true }
    # an installed layout: the bundled runtime beside app\ (a placeholder - nothing runs it here)
    $py = Join-Path $homeDir 'python\python.exe'; New-Item -ItemType Directory -Path (Split-Path $py) -Force | Out-Null; [IO.File]::WriteAllText($py, '')
    $c.python = @{ exe = $py }
    Save-GuardConfig -Config $c -GuardHome $homeDir

    $state = Get-GuardPath state $homeDir
    foreach ($k in 'relaunch', 'overwatch', 'logarchive') { & $beat (Join-Path $state "$k.heartbeat") 25 }
    & $beat (Join-Path (Get-GuardPath secrets $homeDir) 'nt8.cred') 86400
    $hb = [ordered]@{ component = 'AutoReconnect'; build = 'release'; version = '5.0.0'; rung = 0; autoRecover = $true; lastL1AgeSeconds = 1
                      watchdogStaleSeconds = 75; watchdogResolved = 'ES 12-26,NQ 12-26'; watchdogConfigured = 'ES ##-##,NQ ##-##'
                      events = $true; scheduledClosed = $false; simulatedFeed = $false; nextClosure = 'Thanksgiving, Thu 26 Nov: closed all day'
                      feedProviders = 'Data Feed'; selfChecks = [ordered]@{ feedProvider = 'ok'; rollover = 'nt8'; reEnable = 'ok'; calendar = 'rules' } }
    & $beat (Join-Path $nt8 'AutoReconnect.heartbeat.json') 4 ($hb | ConvertTo-Json -Depth 4)
    & $beat (Join-Path $nt8 'NT8Bridge\result\heartbeat.json') 3 '{"status":"ok"}'
    New-Item -ItemType Directory -Path (Join-Path $nt8 'NT8Bridge\trigger') -Force | Out-Null

    $rows = @{}
    foreach ($r in @(@{ n = 'ES Trend'; i = 'ES 12-26'; t = 'ES 5 min' }, @{ n = 'NQ Breakout'; i = 'NQ 12-26'; t = 'NQ 2 min' }, @{ n = 'NQ Pullback'; i = 'NQ 12-26'; t = 'NQ 2 min' })) {
        $rows["$($r.n)@NinjaTrader.NinjaScript.Strategies.SampleMACrossOver"] = @{
            strategies = @{ Id = 1; Name = $r.n; Classname = 'NinjaTrader.NinjaScript.Strategies.SampleMACrossOver' }; state = 'live'
            lastSeenUtc = $now.AddMinutes(-12).ToString('o'); labels = @{ accounts = @('Sim101'); instruments = @($r.i) }
            charts = @(@{ workspace = 'Trading'; tabName = $r.t }) }
    }
    [IO.File]::WriteAllText((Join-Path (Get-GuardPath backups $homeDir) 'rows.json'),
                            (@{ version = 2; utc = $now.AddMinutes(-12).ToString('o'); rows = $rows } | ConvertTo-Json -Depth 8))

    Write-GuardEvent -Component relaunch -Kind 'nt8.launch' -Severity info -Text 'Started NinjaTrader and logged in' -GuardHome $homeDir
    Write-GuardEvent -Component overwatch -Kind 'connections.restored' -Severity info -Text 'Reconnected Data Feed after the restart' -GuardHome $homeDir
    Write-GuardEvent -Component overwatch -Kind 'backups.saved' -Severity info -Text 'Strategy backups saved: 3 strategies' -GuardHome $homeDir
    Write-GuardEvent -Component panel -Kind 'alert.test' -Severity info -Text 'Test alert sent to your phone' -GuardHome $homeDir

    $procs = { , @([pscustomobject]@{ Id = 4242; StartUtc = [DateTime]::UtcNow.AddHours(-6); Responding = $true; Title = 'Control Center' }) }
    return [pscustomobject]@{
        Home = $homeDir; Nt8 = $nt8; NowUtc = $now
        StatusArgs = @{ Processes = $procs; TaskState = { param($n) 'Ready' }; Nt8Version = [version]'8.1.8.3' }
        SetupCheckArgs = @{ Nt8Version = [version]'8.1.8.3'; AutoLogon = { $true }; ScalePercent = { 125 }; OtherWatchdogs = { @() } }
        AddonArgs = @{ Processes = $procs }
    }
}
