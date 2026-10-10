# One log file per component under <GuardHome>\logs, rotated at 5 MB (one previous copy kept).
# Logging must never throw into a watchdog: every failure here is swallowed.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')

function Step-GuardFileRotation {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path, [long]$MaxBytes = 5MB)
    try {
        if ((Test-Path $Path) -and (Get-Item $Path).Length -ge $MaxBytes) { Move-Item -Path $Path -Destination "$Path.1" -Force }
    } catch { }
}

function Write-GuardLog {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Component, [Parameter(Mandatory)][AllowEmptyString()][string]$Message,
          [string]$GuardHome)
    try {
        $f = Join-Path (Get-GuardPath logs $GuardHome) "$Component.log"
        Step-GuardFileRotation $f
        "$([DateTime]::UtcNow.ToString('o'))  $Message" | Out-File -FilePath $f -Append -Encoding utf8
    } catch { }
}

Export-ModuleMember -Function Step-GuardFileRotation, Write-GuardLog
