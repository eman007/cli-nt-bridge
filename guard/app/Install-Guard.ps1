# Install-Guard.ps1 - install NT8 Guard, or update an install (spec 8). Install.cmd runs it from the folder
# the release zip was extracted to:
#
#   powershell -ExecutionPolicy Bypass -File app\Install-Guard.ps1 -Source <extracted folder> [-NoLaunch]
#
# What it does, in order:
#   1. unblocks every file it ships (a zip downloaded from the internet marks each one, and PowerShell
#      can then refuse the modules);
#   2. refuses while the control panel is open (its fonts and modules are in use) or while NinjaTrader is
#      being started (a start reads the program it would be replacing);
#   3. replaces app\ and python\ WHOLE: copied beside the live folder as <name>.new, then swapped in by
#      two renames, so no task ever loads a half-copied program; a leftover .new/.old from an
#      interrupted run is cleared first;
#   4. copies the add-ons zip and the guide beside them;
#   5. adds the Start-menu shortcut "NT8 Guard";
#   6. opens the setup wizard on a first install (the panel on an update), unless -NoLaunch.
# Never touched: guard.json, secrets\ (the saved password), state\, events\, logs\, the strategy backups,
# NinjaTrader, and Guard's scheduled tasks (an update keeps the same paths, so the tasks need nothing).
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source, [string]$GuardHome, [string]$StartMenuDir, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'

function Say([string]$Text) { Write-Output "  $Text" }
function Fail([string]$Text) { Write-Output ''; Write-Output "  NT8 Guard was NOT installed: $Text"; exit 1 }

$Source = [IO.Path]::GetFullPath($Source).TrimEnd('\')
$srcApp = Join-Path $Source 'app'
if (-not (Test-Path (Join-Path $srcApp 'Guard-Panel.ps1'))) { Fail "$Source does not hold NT8 Guard's app folder. Extract the whole zip, then run Install.cmd from it." }

# 1. Mark-of-the-Web: before anything imports a module from the source.
Get-ChildItem -LiteralPath $Source -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue

foreach ($m in 'Guard.Paths', 'Guard.Log', 'Guard.Config', 'Guard.Events', 'Guard.Alerts', 'Guard.Pause', 'Guard.Start') {
    Import-Module (Join-Path $srcApp "lib\$m.psm1") -DisableNameChecking
}
$guardDir = [IO.Path]::GetFullPath((Get-GuardHome $GuardHome)).TrimEnd('\')
$version = ([IO.File]::ReadAllText((Join-Path $srcApp 'VERSION'))).Trim()
Write-Output ''
Write-Output "  NT8 Guard $version"

# 2. Refusals.
$m = $null
if ([Threading.Mutex]::TryOpenExisting('Local\NT8GuardPanel', [ref]$m)) {
    $m.Dispose()
    Fail 'Close the NT8 Guard window first (its program files are in use), then run Install.cmd again.'
}
if (-not (Test-Path $guardDir)) { New-Item -ItemType Directory -Path $guardDir -Force | Out-Null }
$lock = Open-GuardStartLock $guardDir
if (-not $lock) { Fail 'NinjaTrader is being started right now (or a strategy backup is being saved). Try again in a minute.' }

function Install-GuardFolder([string]$Name) {
    $from = Join-Path $Source $Name
    if (-not (Test-Path $from)) { return "no $Name\ in this release - skipped" }
    $live = Join-Path $guardDir $Name
    $new = "$live.new"; $old = "$live.old"
    foreach ($leftover in $new, $old) { if (Test-Path $leftover) { [IO.Directory]::Delete($leftover, $true) } }
    Copy-Item -LiteralPath $from -Destination $new -Recurse -Force
    if (Test-Path $live) {
        # A file open in the live folder (a backup tool still running) blocks the rename: wait it out.
        $moved = $false
        for ($i = 0; $i -lt 30 -and -not $moved; $i++) {
            try { [IO.Directory]::Move($live, $old); $moved = $true } catch { Start-Sleep -Seconds 1 }
        }
        if (-not $moved) {
            [IO.Directory]::Delete($new, $true)
            throw "$live is in use (a Guard task or a backup still running). Try again in a minute."
        }
    }
    [IO.Directory]::Move($new, $live)
    if (Test-Path $old) { try { [IO.Directory]::Delete($old, $true) } catch { } }
    return "$Name\ installed"
}

# 3 and 4, holding the start lock so no start runs on a half-swapped program.
try {
    foreach ($n in 'app', 'python') {
        try { Say (Install-GuardFolder $n) } catch { Fail $_.Exception.Message }
    }
    foreach ($f in 'NT8Guard-AddOns.zip', 'NT8 Guard - Setup Guide.pdf', 'NT8 Guard - Quick Start.png', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'README.txt') {
        $p = Join-Path $Source $f
        if (Test-Path $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $guardDir $f) -Force }
    }
    Say "the NinjaTrader add-ons zip and the guide copied"
} finally { $lock.Dispose() }

# 5. The Start-menu shortcut: the panel through Guard-Run.vbs, so no console window flashes.
if (-not $StartMenuDir) { $StartMenuDir = [Environment]::GetFolderPath('Programs') }
if (-not (Test-Path $StartMenuDir)) { New-Item -ItemType Directory -Path $StartMenuDir -Force | Out-Null }
$app = Join-Path $guardDir 'app'
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $StartMenuDir 'NT8 Guard.lnk'))
$lnk.TargetPath = Join-Path $env:SystemRoot 'System32\wscript.exe'
$lnk.Arguments = "`"$(Join-Path $app 'Guard-Run.vbs')`" `"Guard-Panel.ps1`""
$lnk.WorkingDirectory = $app
$lnk.Description = 'NT8 Guard control panel'
$lnk.Save()
Say 'Start menu: NT8 Guard'
Write-Output ''
Write-Output "  Installed in $guardDir"

# 6. Open the wizard (first install) or the panel (update).
if (-not $NoLaunch) {
    Import-Module (Join-Path $app 'lib\Guard.Setup.psm1') -DisableNameChecking
    $done = $false
    try { $done = (Get-GuardSetupState -GuardHome $guardDir).Completed } catch { }
    $launch = @("`"$(Join-Path $app 'Guard-Run.vbs')`"", '"Guard-Panel.ps1"')
    if (-not $done) { $launch += '"-Wizard"' }
    Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\wscript.exe') -ArgumentList $launch
    Write-Output $(if ($done) { '  Opening NT8 Guard.' } else { '  Opening the setup wizard.' })
}
exit 0
