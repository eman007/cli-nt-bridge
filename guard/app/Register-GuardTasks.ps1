# Register-GuardTasks.ps1 - install (or -Remove) NT8 Guard's scheduled tasks for the CURRENT user.
#
# No administrator rights needed: the tasks run as you, in your logged-on session, at the normal
# (Limited) run level - verified on Windows 11 with UAC on. They only run while you are logged on,
# which is also the only time NinjaTrader can be logged in.
#
# Installing is not working: after registering, each task is run once and must write its heartbeat,
# or this reports failure.
#
#   Register-GuardTasks.ps1 [-GuardHome DIR] [-Components relaunch,overwatch,logarchive] [-TaskPrefix NT8Guard]
#   Register-GuardTasks.ps1 -Remove
[CmdletBinding()]
param([string]$GuardHome, [string]$TaskPrefix = 'NT8Guard',
      # A comma-separated list ("relaunch,overwatch,logarchive"): powershell.exe -File cannot pass an array.
      [string[]]$Components = @('relaunch', 'overwatch', 'logarchive'),
      [switch]$Remove, [ValidateSet('Limited', 'Highest')][string]$RunLevel = 'Limited', [int]$VerifySeconds = 90)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Guard.Paths.psm1')

$all = [ordered]@{
    relaunch   = @{ Task = "$TaskPrefix-Relaunch";   Script = 'Guard-Relaunch.ps1';   Every = 1;  Heartbeat = 'relaunch.heartbeat' }
    overwatch  = @{ Task = "$TaskPrefix-Overwatch";  Script = 'Guard-Overwatch.ps1';  Every = 1;  Heartbeat = 'overwatch.heartbeat' }
    logarchive = @{ Task = "$TaskPrefix-LogArchive"; Script = 'Guard-LogArchive.ps1'; Every = 10; Heartbeat = 'logarchive.heartbeat' }
}
$gh = Get-GuardHome $GuardHome
$results = @()
$Components = @($Components | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($c in $Components) { if (-not $all.Contains($c)) { throw "unknown component '$c' (relaunch, overwatch, logarchive)" } }

if ($Remove) {
    foreach ($c in $all.Keys) {
        $t = $all[$c].Task
        if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $t -Confirm:$false }
        $results += [pscustomobject]@{ Component = $c; Task = $t; Removed = -not [bool](Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) }
    }
    $results | Format-Table -AutoSize | Out-String | Write-Host
    if (@($results | Where-Object { -not $_.Removed }).Count) { exit 1 } else { exit 0 }
}

$vbs = Join-Path $PSScriptRoot 'Guard-Run.vbs'
if (-not (Test-Path $vbs)) { throw "Guard-Run.vbs not found next to this script ($vbs)" }

foreach ($c in $Components) {
    $d = $all[$c]
    $ps1 = Join-Path $PSScriptRoot $d.Script
    $row = [ordered]@{ Component = $c; Task = $d.Task; Registered = $false; Heartbeat = 'not checked' }
    if (-not (Test-Path $ps1)) { $row.Heartbeat = "skipped: $($d.Script) is not installed"; $results += [pscustomobject]$row; continue }
    $errs = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($ps1, [ref]$null, [ref]$errs)
    if ($errs) { $row.Heartbeat = "refused: $($d.Script) has parse errors"; $results += [pscustomobject]$row; continue }

    $action    = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ('"{0}" "{1}" -GuardHome "{2}"' -f $vbs, $d.Script, $gh)
    # No -RepetitionDuration: omitted means "repeat indefinitely" ([TimeSpan]::MaxValue is rejected).
    $trigger   = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(30) -RepetitionInterval (New-TimeSpan -Minutes $d.Every)
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel $RunLevel
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
                     -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $d.Task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    # The scheduler cmdlets can fail without throwing: ask the scheduler.
    $row.Registered = [bool](Get-ScheduledTask -TaskName $d.Task -ErrorAction SilentlyContinue)
    if ($row.Registered) {
        $hb = Join-Path (Get-GuardPath state $gh) $d.Heartbeat
        if (Test-Path $hb) { Remove-Item $hb -Force }
        Start-ScheduledTask -TaskName $d.Task
        $deadline = (Get-Date).AddSeconds($VerifySeconds)
        while ((Get-Date) -lt $deadline -and -not (Test-Path $hb)) { Start-Sleep -Seconds 2 }
        $row.Heartbeat = $(if (Test-Path $hb) { 'ok' } else { "none after ${VerifySeconds}s (LastTaskResult $((Get-ScheduledTaskInfo -TaskName $d.Task).LastTaskResult))" })
    }
    $results += [pscustomobject]$row
}
$results | Format-Table -AutoSize | Out-String | Write-Host
# Installing tasks while Guard is stopped (Tools > Stop Guard) ends the stop once BOTH watchdog tasks are
# on - asked of the scheduler, because the setup installs one task per run. The feed guard gets guard.json's
# settings back first - Stop left it only watching - and only then does the stop's file go; if they cannot
# be written the file stays, and Status says so and offers Start Guard.
$stopFile = Join-Path (Get-GuardPath state $gh) 'stopped.json'
$taskOn = { param($n) $t = Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue; [bool]$t -and [string]$t.State -ne 'Disabled' }
if (@($results | Where-Object { $_.Registered }).Count -and (Test-Path -LiteralPath $stopFile) -and
    (& $taskOn "$TaskPrefix-Relaunch") -and (& $taskOn "$TaskPrefix-Overwatch")) {
    try {
        Import-Module (Join-Path $PSScriptRoot 'lib\Guard.Config.psm1')
        Import-Module (Join-Path $PSScriptRoot 'lib\Guard.Events.psm1')
        Import-Module (Join-Path $PSScriptRoot 'lib\Guard.FeedGuard.psm1')
        $cfg = Get-GuardConfig $gh
        $nt8Dir = Get-Nt8DataDir ([string]$cfg.nt8.dataDir)
        if (Test-Path -LiteralPath $nt8Dir) { Write-FeedGuardConfig -Config $cfg -Nt8Dir $nt8Dir | Out-Null }
        Remove-Item -LiteralPath $stopFile -Force
        Write-GuardEvent -Component guard -Kind 'guard.started' -Severity info -Text 'Guard running again: its tasks were installed' -GuardHome $gh
        Write-Host 'Guard was stopped; it is running again (the feed guard has your settings back).'
    } catch { Write-Host "Guard was stopped; its tasks run again, but the feed guard still only watches - its settings could not be written: $($_.Exception.Message). Press Start Guard in the control panel." }
}
if (@($results | Where-Object { $_.Heartbeat -ne 'ok' -and $_.Heartbeat -notlike 'skipped*' }).Count) { exit 1 } else { exit 0 }
