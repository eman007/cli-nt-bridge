# Real Task Scheduler, throwaway task names, temp Guard home, FAKE NinjaTrader folder, preset Watch
# (nothing acts) and no alert channel. Proves: tasks register without admin, run hidden through
# Guard-Run.vbs, write their heartbeats, and -Remove deletes them. Then Stop and Start switch those real tasks
# off and on (no admin either), and installing them clears an earlier stop.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
$h = Use-TestHomes integration
Import-GuardModules
$cfg = Get-GuardDefaults
$cfg.nt8.dataDir = $h.Nt8                        # never the real NinjaTrader folder (the log keeper prunes)
$cfg.protection.preset = 'watch'                 # nothing acts
Save-GuardConfig -Config $cfg -GuardHome $h.Home
$reg = Join-Path $script:AppDir 'Register-GuardTasks.ps1'
$prefix = 'NT8GuardTest' + (Get-Random -Maximum 99999)

'=== the default install includes the overwatch'
$pe = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($reg, [ref]$null, [ref]$pe)
$cp = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Components' })[0]
Check 'the overwatch is installed by default' ([string]$cp.DefaultValue.Extent.Text -match 'overwatch') ([string]$cp.DefaultValue.Extent.Text)

'=== register (no admin) and prove each task runs'
$stopFile = Join-Path $h.Home 'state\stopped.json'
New-Item -ItemType Directory -Path (Split-Path $stopFile) -Force | Out-Null
[IO.File]::WriteAllText($stopFile, '{ "stoppedUtc": "2026-10-01T00:00:00Z" }')     # a stop from before: installing ends it
# One component per run, as the setup's Finish does - the stop ends only once both watchdogs are installed.
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reg -GuardHome $h.Home -TaskPrefix $prefix -Components 'relaunch' -VerifySeconds 90 | Out-Null
$code1 = $LASTEXITCODE
$stillStopped = Test-Path $stopFile
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reg -GuardHome $h.Home -TaskPrefix $prefix -Components 'overwatch,logarchive' -VerifySeconds 90 | Out-Null
$code = $LASTEXITCODE
try {
    Check 'registration reported success' ($code1 -eq 0 -and $code -eq 0) "exit $code1, $code"
    Check 'the relauncher alone does not end a stop' $stillStopped
    foreach ($t in "$prefix-Relaunch", "$prefix-Overwatch", "$prefix-LogArchive") {
        $task = Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
        Check "$t exists" ([bool]$task)
        if (-not $task) { continue }
        Check "$t runs as the current user, Limited" ($task.Principal.RunLevel -eq 'Limited' -and $task.Principal.UserId -like "*$env:USERNAME")
        Check "$t starts wscript Guard-Run.vbs" ($task.Actions[0].Execute -eq 'wscript.exe' -and $task.Actions[0].Arguments -like '*Guard-Run.vbs*')
    }
    Check 'relaunch heartbeat' (Test-Path (Join-Path $h.Home 'state\relaunch.heartbeat'))
    Check 'overwatch heartbeat' (Test-Path (Join-Path $h.Home 'state\overwatch.heartbeat'))
    Check 'log keeper heartbeat' (Test-Path (Join-Path $h.Home 'state\logarchive.heartbeat'))
    Check 'installing the tasks ended the earlier stop' (-not (Test-Path $stopFile))

    '=== Stop and Start, through the real scheduler'
    foreach ($m in 'Guard.Status', 'Guard.Nt8Link', 'Guard.Password', 'Guard.Actions') { Import-Module (Join-Path $script:AppDir "lib\$m.psm1") -Force -DisableNameChecking }
    $c = Get-GuardConfig $h.Home
    $r = Invoke-GuardStop -Config $c -GuardHome $h.Home -Nt8Dir $h.Nt8 -TaskPrefix $prefix
    $on = @(Get-ScheduledTask -TaskName "$prefix-*" | Where-Object { $_.State -ne 'Disabled' } | ForEach-Object TaskName)
    Check 'Stop switched the three real tasks off, without admin' ($r.Ok -and -not $on.Count -and (Test-Path $stopFile)) "$($r.Message) | still on: $($on -join ', ')"
    $r = Invoke-GuardStart -Config $c -GuardHome $h.Home -Nt8Dir $h.Nt8 -TaskPrefix $prefix -WaitSeconds 90
    $off = @(Get-ScheduledTask -TaskName "$prefix-*" | Where-Object { $_.State -eq 'Disabled' } | ForEach-Object TaskName)
    Check 'Start switched them on, and both watchdogs reported' ($r.Ok -and -not $off.Count -and $r.Message -notmatch 'No report yet' -and -not (Test-Path $stopFile)) "$($r.Message) | still off: $($off -join ', ')"
} finally {
    '=== remove'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reg -TaskPrefix $prefix -Remove | Out-Null
    Check 'tasks removed' (-not (Get-ScheduledTask -TaskName "$prefix-*" -ErrorAction SilentlyContinue))
}
Complete-Test
