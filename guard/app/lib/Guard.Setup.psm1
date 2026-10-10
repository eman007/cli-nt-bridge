# The setup wizard's logic (spec 5.1), kept out of the window so it can be tested without one:
#   step 1  Get-GuardSetupCheck     NinjaTrader's version and folders, Guard's Python, display scaling,
#                                   whether Windows signs in by itself, another watchdog already on
#           Get-GuardOtherWatchdogs that other watchdog (Finish refuses while one is switched on)
#   step 3  Get-GuardAddonLights    the two add-ons' first reports (Next waits for both)
#           Get-GuardAddOnsZip      the file NinjaTrader imports
#   step 7  Invoke-GuardSetupFinish register the tasks (each proven by its heartbeat), a test alert, done
#   always  Get-GuardSetupState / Set-GuardSetupStep - the step you are on, kept in guard.json
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Config.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Alerts.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Start.psm1')      # Get-GuardPythonExe, Get-GuardSessionProcess
Import-Module (Join-Path $PSScriptRoot 'Guard.Status.psm1')     # Get-GuardExpectedFeedGuardVersion

function New-SetupRow([string]$Key, [string]$Title, [string]$State, [string]$Text) {
    return [pscustomobject]@{ Key = $Key; Title = $Title; State = $State; Text = $Text }
}

# What another watchdog runs, in its task's action or its command line: this PC's older relaunch and
# overwatch scripts, cli-nt-bridge's watchdog, another copy of Guard. (nt8bridge watch / watchdog_detect_loop
# only flatten naked positions - they never restart NinjaTrader, so they may stay.)
# Each names NinjaTrader's own watchdogs: a bare 'Relaunch-Watchdog' also matched a TWS relauncher (2026-10-10).
$script:WatchdogPatterns = @('NT8-Relaunch', 'NT8-Overwatch', 'nt8bridge\s+watchdog', 'Guard-Relaunch\.ps1', 'Guard-Overwatch\.ps1')

function Get-GuardOtherWatchdogs {
    # Something else that restarts NinjaTrader, switched on: two of them would each restart it and fight over
    # it. Names, in the order found: each scheduled task that is not Disabled and runs one (Guard's own tasks,
    # named <TaskPrefix>-..., are re-registered by Finish and do not count), then each process running one
    # that is not this Guard's. Read-only. Seams: -Tasks { -> , @(task objects) }, -Processes { -> , @(Win32_Process) }.
    [CmdletBinding()]
    param([scriptblock]$Tasks, [scriptblock]$Processes, [string]$TaskPrefix = 'NT8Guard')
    if (-not $Tasks) { $Tasks = { , @(Get-ScheduledTask -ErrorAction SilentlyContinue) } }
    if (-not $Processes) {
        $Processes = { , @(Get-CimInstance Win32_Process -Filter "Name='python.exe' OR Name='pythonw.exe' OR Name='powershell.exe' OR Name='pwsh.exe' OR Name='wscript.exe'" -ErrorAction SilentlyContinue |
                           Select-Object ProcessId, CommandLine) }
    }
    $isWatchdog = { param($text) foreach ($p in $script:WatchdogPatterns) { if ($text -match $p) { return $true } }; return $false }
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($t in @(@(& $Tasks) | ForEach-Object { $_ })) {
        if (-not $t -or [string]$t.State -eq 'Disabled' -or [string]$t.TaskName -like "$TaskPrefix-*") { continue }
        $runs = (@($t.Actions | Where-Object { $_ } | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join ' ')
        if (& $isWatchdog $runs) { $found.Add([string]$t.TaskName) }
    }
    $app = [IO.Path]::GetFullPath((Get-GuardAppDir)).TrimEnd('\')
    foreach ($p in @(@(& $Processes) | ForEach-Object { $_ })) {
        $cmd = [string]$p.CommandLine
        if (-not $cmd -or [int]$p.ProcessId -eq $PID -or $cmd.IndexOf($app, [StringComparison]::OrdinalIgnoreCase) -ge 0) { continue }
        if (& $isWatchdog $cmd) {
            $short = $(if ($cmd.Length -gt 90) { $cmd.Substring(0, 87) + '...' } else { $cmd })
            $found.Add("'$short' (process $($p.ProcessId))")
        }
    }
    return $found.ToArray()
}

function Format-GuardOtherWatchdogs([string[]]$Names) {
    return ("Something else already restarts NinjaTrader here: $($Names -join ', '). With Guard too, two would restart it and fight over it. " +
            'Disable it in Task Scheduler (right-click it, Disable) - or close it, if it runs in a window - then press Check again. You can delete it once Guard has run a while.')
}

function Get-GuardSetupCheck {
    # Step 1: is this PC ready? Rows @{ Key; Title; State = ok | warn | bad; Text }, in the order shown.
    # -Nt8Version 'auto' reads NinjaTrader.exe ($null = not found); -AutoLogon { -> $true when Windows signs
    # in by itself }; -ScalePercent { -> display scaling, 100 = none }; -OtherWatchdogs { -> names }. All read-only.
    [CmdletBinding()]
    param([hashtable]$Config, [string]$Nt8Dir, $Nt8Version = 'auto', [scriptblock]$AutoLogon, [scriptblock]$ScalePercent, [scriptblock]$OtherWatchdogs)
    if (-not $Config) { $Config = Get-GuardConfig }
    if (-not $OtherWatchdogs) { $OtherWatchdogs = { Get-GuardOtherWatchdogs } }
    if (-not $AutoLogon) {
        $AutoLogon = { try { [string](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name AutoAdminLogon -ErrorAction Stop).AutoAdminLogon -eq '1' } catch { $false } }
    }
    if (-not $ScalePercent) {
        $ScalePercent = { try { [int][math]::Round([double](Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction Stop).AppliedDPI * 100 / 96) } catch { 100 } }
    }
    $rows = New-Object System.Collections.Generic.List[object]

    $prog = Get-Nt8ProgramDir ([string]$Config.nt8.programDir)
    if ([string]$Nt8Version -eq 'auto') { $Nt8Version = Get-Nt8Version ([string]$Config.nt8.programDir) }
    if (-not $Nt8Version) {
        $rows.Add((New-SetupRow nt8version 'NinjaTrader' bad "NinjaTrader 8 was not found in $prog. Install it, or set nt8.programDir in guard.json to where it is."))
    } else {
        $v = [version]$Nt8Version
        if ($v -ge [version]'8.1.6.3') { $rows.Add((New-SetupRow nt8version 'NinjaTrader' ok "NinjaTrader $v - tested with Guard.")) }
        elseif ($v.Major -eq 8 -and $v.Minor -eq 1) { $rows.Add((New-SetupRow nt8version 'NinjaTrader' warn "NinjaTrader $v is not tested with Guard yet (8.1.6.3 and later are). It should work: watch the Status tab after its first restart.")) }
        else { $rows.Add((New-SetupRow nt8version 'NinjaTrader' bad "NinjaTrader $v is not supported: Guard needs NinjaTrader 8.1.")) }
    }

    $data = Get-Nt8DataDir $(if ($Nt8Dir) { $Nt8Dir } else { [string]$Config.nt8.dataDir })
    if (Test-Path -LiteralPath $data) {
        $rows.Add((New-SetupRow nt8data "NinjaTrader's folder" ok ("$data" + $(if ($data -match 'OneDrive') { ' - in OneDrive, which is fine.' } else { '' }))))
    } else {
        $rows.Add((New-SetupRow nt8data "NinjaTrader's folder" bad "Not found at $data. If NinjaTrader has never run on this Windows account, start NinjaTrader once (it creates the folder); otherwise set nt8.dataDir in guard.json."))
    }

    $py = Get-GuardPythonExe $Config
    if ($py -and (Test-Path -LiteralPath $py)) { $rows.Add((New-SetupRow python "Guard's Python" ok "Found ($py).")) }
    else { $rows.Add((New-SetupRow python "Guard's Python" bad 'Missing, so the strategy backups can be neither saved nor put back. Install Guard again (Install.cmd).')) }

    $pct = [int](& $ScalePercent)
    $rows.Add((New-SetupRow scaling 'Display scaling' ok "$pct% - fine: Guard finds NinjaTrader's password box by what it is, not where it sits on the screen."))

    if (& $AutoLogon) { $rows.Add((New-SetupRow signin 'Windows sign-in' ok 'Windows signs in by itself, so after a restart Guard starts NinjaTrader on its own.')) }
    else {
        $rows.Add((New-SetupRow signin 'Windows sign-in' warn ('Windows asks for a password at startup: after a restart (an update, a power cut) NinjaTrader is started only once you sign in. ' +
                                                               'Guard also cannot log NinjaTrader in while the screen is locked - it waits for the unlock.')))
    }

    $others = @(& $OtherWatchdogs)
    if ($others.Count) { $rows.Add((New-SetupRow others 'Other watchdogs' bad (Format-GuardOtherWatchdogs $others))) }
    else { $rows.Add((New-SetupRow others 'Other watchdogs' ok 'None: nothing else restarts NinjaTrader on this PC.')) }
    return $rows.ToArray()
}

function Get-GuardAddonLights {
    # Step 3: has each add-on reported from this NinjaTrader? @{ FeedGuard; Link } each @{ State = ok | old |
    # wait; Text }, plus Ready (both ok - the wizard's Next waits for it), Note and Nt8Running.
    [CmdletBinding()]
    param([hashtable]$Config, [string]$Nt8Dir, [Nullable[DateTime]]$NowUtc, [scriptblock]$Processes)
    $now = $(if ($null -ne $NowUtc) { [DateTime]$NowUtc } else { [DateTime]::UtcNow })
    if (-not $Config) { $Config = Get-GuardConfig }
    $nt8 = Get-Nt8DataDir $(if ($Nt8Dir) { $Nt8Dir } else { [string]$Config.nt8.dataDir })
    if (-not $Processes) { $Processes = { , @(Get-GuardSessionProcess -Name NinjaTrader) } }
    $running = [bool](@(& $Processes) | Select-Object -First 1)     # the processes come back as ONE array (, @(...))
    $age = { param($p) if (Test-Path -LiteralPath $p) { ($now - (Get-Item -LiteralPath $p).LastWriteTimeUtc).TotalSeconds } else { $null } }

    $want = Get-GuardExpectedFeedGuardVersion
    $fg = @{ State = 'wait'; Text = 'Feed guard: not reporting yet.' }
    $arPath = [IO.Path]::Combine($nt8, 'AutoReconnect.heartbeat.json')
    $a = & $age $arPath
    if ($running -and $null -ne $a -and $a -le [double]$Config.thresholds.guardianStaleSeconds) {
        $ver = ''
        try { $ver = [string]([IO.File]::ReadAllText($arPath) | ConvertFrom-Json).version } catch { }
        if ($ver -and [version]$ver -ge [version]$want) { $fg = @{ State = 'ok'; Text = "Feed guard $ver is reporting." } }
        else { $fg = @{ State = 'old'; Text = "The feed guard in NinjaTrader is version $(if ($ver) { $ver } else { 'before 5.0.0' }), older than this Guard ($want): import the new NT8Guard-AddOns.zip." } }
    }
    $lk = @{ State = 'wait'; Text = 'NT8 link: not reporting yet.' }
    $b = & $age ([IO.Path]::Combine($nt8, 'NT8Bridge\result\heartbeat.json'))
    if ($running -and $null -ne $b -and $b -le [double]$Config.thresholds.bridgeStaleSeconds) { $lk = @{ State = 'ok'; Text = 'NT8 link is reporting.' } }

    $ready = ($fg.State -eq 'ok' -and $lk.State -eq 'ok')
    $note = $(if (-not $running) { "Start NinjaTrader: the lights turn green on the add-ons' first report, a few seconds after it opens." }
              elseif ($ready) { 'Both add-ons are running in NinjaTrader.' }
              else { 'After the import NinjaTrader compiles them, and they report within a minute. A light that stays grey: open the NinjaScript Editor and press F5.' })
    return [pscustomobject]@{ FeedGuard = [pscustomobject]$fg; Link = [pscustomobject]$lk; Ready = $ready; Note = $note; Nt8Running = $running }
}

function Get-GuardAddOnsZip {
    # The file NinjaTrader imports: installed, beside app\; in the repository, the build output. $null when neither.
    [CmdletBinding()] param([string]$Root = (Split-Path -Parent (Get-GuardAppDir)))
    foreach ($p in @((Join-Path $Root 'NT8Guard-AddOns.zip'), (Join-Path $Root 'build\out\NT8Guard-AddOns.zip'))) {
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

function Get-GuardSetupState {
    [CmdletBinding()] param([string]$GuardHome)
    $c = Get-GuardConfig $GuardHome
    $step = [int]$c.setup.step
    if ($step -lt 1 -or $step -gt 7) { $step = 1 }
    return [pscustomobject]@{ Completed = ($c.setup.completed -eq $true); Step = $step }
}

function Set-GuardSetupStep {
    # Remember the wizard's step (and, at the end, that setup is complete). $true when saved; $false when
    # guard.json could not be written (the wizard carries on regardless - this is a bookmark).
    [CmdletBinding()] param([Parameter(Mandatory)][ValidateRange(1, 7)][int]$Step, [switch]$Completed, [string]$GuardHome)
    $c = Get-GuardConfig $GuardHome
    $c.setup = @{ step = $Step; completed = ([bool]$Completed -or $c.setup.completed -eq $true) }
    try { Save-GuardConfig -Config $c -GuardHome $GuardHome; return $true } catch { return $false }
}

function Register-GuardTask {
    # One task, through Register-GuardTasks.ps1 in its own process: registered, run once, its heartbeat
    # seen - or why not. @{ Ok; Text }.
    [CmdletBinding()] param([Parameter(Mandatory)][ValidateSet('relaunch', 'overwatch', 'logarchive')][string]$Component, [string]$GuardHome)
    $ps1 = Join-Path (Get-GuardAppDir) 'Register-GuardTasks.ps1'
    $out = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ps1 -GuardHome (Get-GuardHome $GuardHome) -Components $Component 2>&1 |
             ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    if ($LASTEXITCODE -eq 0) { return [pscustomobject]@{ Ok = $true; Text = 'registered; it ran and reported' } }
    return [pscustomobject]@{ Ok = $false; Text = "not working: $(($out | Select-Object -Last 1))" }
}

function Invoke-GuardSetupFinish {
    # Step 7: register the three tasks (each run once and proven by its heartbeat), send a test alert when
    # alerts are set up, and mark setup complete. A task that does not run leaves setup unfinished, named.
    # Refused, with nothing registered, while another watchdog is switched on (step 1 names it).
    # Seams: -Register { param($component) -> @{ Ok; Text } }, -TestAlert { -> @{ ok; message } },
    # -OtherWatchdogs { -> names }, -Progress.
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [scriptblock]$Register, [scriptblock]$TestAlert,
          [scriptblock]$OtherWatchdogs, [scriptblock]$Progress)
    $report = { param($k, $s, $t) if ($Progress) { & $Progress $k $s $t } }
    if (-not $Register) { $Register = { param($c) Register-GuardTask -Component $c -GuardHome $GuardHome } }
    if (-not $TestAlert) { $TestAlert = { Test-GuardAlert -Config $Config -GuardHome $GuardHome } }
    if (-not $OtherWatchdogs) { $OtherWatchdogs = { Get-GuardOtherWatchdogs } }
    $others = @(& $OtherWatchdogs)
    if ($others.Count) {
        $why = 'Guard was not switched on. ' + (Format-GuardOtherWatchdogs $others)
        & $report relaunch failed $why
        return [pscustomobject]@{ Ok = $false; Result = 'refused'; Message = $why }
    }
    $failed = New-Object System.Collections.Generic.List[string]
    foreach ($c in 'relaunch', 'overwatch', 'logarchive') {
        & $report $c running ''
        $r = & $Register $c
        if ($r.Ok) { & $report $c ok $r.Text } else { & $report $c failed $r.Text; $failed.Add("$($c): $($r.Text)") }
    }
    & $report alert running ''
    $alertNote = ''
    if (-not $Config.alerts.ntfyTopic -and -not $Config.alerts.webhookUrl) { & $report alert skipped 'No phone alerts set up - add them in the Alerts tab whenever you like.' }
    else {
        $a = & $TestAlert
        if ($a.ok) { & $report alert ok ([string]$a.message) }
        else { & $report alert failed ([string]$a.message); $alertNote = " The test alert did not get through ($($a.message)) - check the Alerts tab." }
    }
    & $report done running ''
    if ($failed.Count) {
        $why = "Setup is not finished: $($failed -join '; '). Check Task Scheduler (the NT8Guard tasks), then press Finish setup again."
        & $report done failed $why
        return [pscustomobject]@{ Ok = $false; Result = 'failed'; Message = $why + $alertNote }
    }
    [void](Set-GuardSetupStep -Step 7 -Completed -GuardHome $GuardHome)
    & $report done ok 'Setup is complete.'
    return [pscustomobject]@{ Ok = $true; Result = 'done'
                              Message = "Setup is finished: Guard is watching NinjaTrader.$alertNote Next, while the market is open and you are flat: the fire drill (Tools) - it proves the whole chain, your phone included." }
}

Export-ModuleMember -Function Get-GuardSetupCheck, Get-GuardOtherWatchdogs, Get-GuardAddonLights, Get-GuardAddOnsZip, Get-GuardSetupState,
                              Set-GuardSetupStep, Register-GuardTask, Invoke-GuardSetupFinish
