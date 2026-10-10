# Real Task Scheduler, throwaway task names, temp Guard home, FAKE NinjaTrader folder, preset Watch
# (nothing acts) and no alert channel. Proves: tasks register without admin, run hidden through
# Guard-Run.vbs, write their heartbeats, and -Remove deletes them.
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
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reg -GuardHome $h.Home -TaskPrefix $prefix -Components 'relaunch,overwatch,logarchive' -VerifySeconds 90 | Out-Null
$code = $LASTEXITCODE
try {
    Check 'registration reported success' ($code -eq 0) "exit $code"
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
} finally {
    '=== remove'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reg -TaskPrefix $prefix -Remove | Out-Null
    Check 'tasks removed' (-not (Get-ScheduledTask -TaskName "$prefix-*" -ErrorAction SilentlyContinue))
}
Complete-Test
