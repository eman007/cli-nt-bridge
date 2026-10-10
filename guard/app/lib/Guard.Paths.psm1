# Where NinjaTrader and NT8 Guard keep their files.
#
# Windows' own folder lookups only - never a user name - so a Documents folder redirected to OneDrive
# or another drive is followed. NT8_DIR / NT8GUARD_HOME override for tests and unusual setups, and
# guard.json can name NinjaTrader's folders explicitly ('auto' = find them).
#
# ! Never name a variable $home in these scripts: PowerShell variables are case-insensitive and
#   $HOME is a read-only automatic variable.

function Get-GuardHome {
    [CmdletBinding()] param([string]$Override)
    if ($Override) { return $Override }
    if ($env:NT8GUARD_HOME) { return $env:NT8GUARD_HOME }
    return [IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'NT8Guard')
}

function Get-Nt8DataDir {
    [CmdletBinding()] param([string]$Override)
    if ($Override -and $Override -ne 'auto') { return $Override }
    if ($env:NT8_DIR) { return $env:NT8_DIR }
    return [IO.Path]::Combine([Environment]::GetFolderPath('MyDocuments'), 'NinjaTrader 8')
}

function Get-Nt8ProgramDir {
    # NinjaTrader's uninstall entry records no InstallLocation, so: the running process if there is
    # one, then the standard Program Files locations.
    [CmdletBinding()] param([string]$Override)
    if ($Override -and $Override -ne 'auto') { return $Override }
    try {
        $p = Get-Process -Name NinjaTrader -ErrorAction Stop | Select-Object -First 1
        if ($p.Path) { return (Split-Path (Split-Path $p.Path -Parent) -Parent) }   # <dir>\bin\NinjaTrader.exe
    } catch { }
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }) {
        $d = Join-Path $root 'NinjaTrader 8'
        if (Test-Path (Join-Path $d 'bin\NinjaTrader.exe')) { return $d }
    }
    return (Join-Path $env:ProgramFiles 'NinjaTrader 8')
}

function Get-Nt8Exe {
    [CmdletBinding()] param([string]$ProgramDir)
    # [IO.Path]::Combine throughout: Windows PowerShell 5.1's Join-Path throws for an unmounted drive.
    return [IO.Path]::Combine((Get-Nt8ProgramDir $ProgramDir), 'bin\NinjaTrader.exe')
}

function Get-Nt8Version {
    [CmdletBinding()] param([string]$ProgramDir)
    $exe = Get-Nt8Exe $ProgramDir
    if (-not (Test-Path $exe)) { return $null }
    try { return [version](Get-Item $exe).VersionInfo.FileVersion } catch { return $null }
}

function Get-GuardPath {
    # A folder inside Guard's home, created on first use.
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateSet('state', 'logs', 'events', 'secrets', 'dumps', 'backups', 'nt8logs')][string]$Kind,
          [string]$GuardHome)
    $rel = @{ state = 'state'; logs = 'logs'; events = 'events'; secrets = 'secrets'; dumps = 'dumps';
              backups = 'state\backups'; nt8logs = 'logs\nt8' }[$Kind]
    $p = [IO.Path]::Combine((Get-GuardHome $GuardHome), $rel)
    if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
    return $p
}

# app\lib -> app ; app -> <root>\python (installed: %LOCALAPPDATA%\NT8Guard\python, repo: guard\python)
function Get-GuardAppDir { return (Split-Path -Parent $PSScriptRoot) }
function Get-GuardVersion {
    # app\VERSION, written by the release; 'dev' when run from a checkout without it.
    $f = Join-Path (Get-GuardAppDir) 'VERSION'
    if (Test-Path $f) { return ([IO.File]::ReadAllText($f)).Trim() }
    return 'dev'
}
function Get-GuardToolsDir { return (Join-Path (Split-Path -Parent (Get-GuardAppDir)) 'python') }

function Get-FeedGuardEventsPath {
    # Written by the feed guard add-on inside NT8, which only knows NT8's own folder.
    [CmdletBinding()] param([string]$Nt8Dir)
    return [IO.Path]::Combine((Get-Nt8DataDir $Nt8Dir), 'NT8Guard\feedguard-events.jsonl')
}

function ConvertFrom-GuardUtc {
    # Parse a stored 'o'-format UTC stamp the same way on every machine (no culture involved).
    [CmdletBinding()] param([string]$Text)
    if (-not $Text) { return $null }
    try {
        return [DateTime]::Parse($Text, [Globalization.CultureInfo]::InvariantCulture,
                                 [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    } catch { return $null }
}

Export-ModuleMember -Function Get-GuardHome, Get-GuardVersion, Get-Nt8DataDir, Get-Nt8ProgramDir, Get-Nt8Exe, Get-Nt8Version,
                              Get-GuardPath, Get-GuardAppDir, Get-GuardToolsDir, Get-FeedGuardEventsPath,
                              ConvertFrom-GuardUtc
