# The saved NinjaTrader password: a PSCredential exported with Export-Clixml, i.e. Windows DPAPI for the
# CURRENT USER. It cannot be read by another Windows user or on another machine - it must be saved on the
# box, under the account the scheduled tasks run as. Written by the control panel (or Guard-Login.ps1
# -SetPassword), read by the login helper. Never logged, never shown.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')

function Get-GuardCredentialPath { [CmdletBinding()] param([string]$GuardHome) Join-Path (Get-GuardPath secrets $GuardHome) 'nt8.cred' }

function Save-GuardPassword {
    [CmdletBinding()] param([Parameter(Mandatory)][securestring]$Password, [string]$GuardHome)
    New-Object System.Management.Automation.PSCredential('nt8', $Password) |
        Export-Clixml -Path (Get-GuardCredentialPath $GuardHome) -Force
}

function Get-GuardPasswordPlain {
    [CmdletBinding()] param([string]$GuardHome)
    return (Import-Clixml (Get-GuardCredentialPath $GuardHome)).GetNetworkCredential().Password
}

function Test-GuardPassword {
    # A password is saved AND this Windows user can read it (an empty one does not count).
    [CmdletBinding()] param([string]$GuardHome)
    try { return [bool](Get-GuardPasswordPlain $GuardHome) } catch { return $false }
}

function Get-GuardPasswordSavedUtc {
    # When the password was last saved, or $null.
    [CmdletBinding()] param([string]$GuardHome)
    $f = Get-GuardCredentialPath $GuardHome
    if (-not (Test-Path -LiteralPath $f)) { return $null }
    return (Get-Item -LiteralPath $f).LastWriteTimeUtc
}

Export-ModuleMember -Function Get-GuardCredentialPath, Save-GuardPassword, Get-GuardPasswordPlain, Test-GuardPassword, Get-GuardPasswordSavedUtc
