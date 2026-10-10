# Pause: one file with an end time (<GuardHome>\state\pause.json). Both watchdogs obey it and it
# ALWAYS expires by itself - 24 hours at most - so nobody forgets it is on and loses protection for
# days. The log keeper is not paused by it (archiving logs never touches NinjaTrader).
#
# The old per-component switches in NinjaTrader's folder (NT8Relaunch.disabled, NT8Overwatch.disabled,
# NT8LogArchive.disabled) are still honoured during the migration.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Log.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Events.psm1')

$script:Legacy = @{ relaunch = 'NT8Relaunch.disabled'; overwatch = 'NT8Overwatch.disabled'; logarchive = 'NT8LogArchive.disabled' }
$script:MaxMinutes = 1440

function Get-GuardPausePath { [CmdletBinding()] param([string]$GuardHome) Join-Path (Get-GuardPath state $GuardHome) 'pause.json' }

function Set-GuardPause {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateRange(1, 1440)][int]$Minutes, [string]$Reason = '', [string]$GuardHome)
    $now = [DateTime]::UtcNow
    $o = [ordered]@{ setUtc = $now.ToString('o'); untilUtc = $now.AddMinutes($Minutes).ToString('o'); reason = $Reason }
    $path = Get-GuardPausePath $GuardHome
    [IO.File]::WriteAllText("$path.tmp", ($o | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Move-Item -Path "$path.tmp" -Destination $path -Force
    Write-GuardEvent -Component guard -Kind 'pause.set' -Severity warn -Text "Guard paused for $Minutes min" `
                     -Data @{ untilUtc = $o.untilUtc; reason = $Reason } -GuardHome $GuardHome
    return [pscustomobject]$o
}

function Clear-GuardPause {
    [CmdletBinding()] param([string]$GuardHome)
    $path = Get-GuardPausePath $GuardHome
    if (Test-Path $path) {
        Remove-Item $path -Force
        Write-GuardEvent -Component guard -Kind 'pause.cleared' -Severity info -Text 'Guard resumed' -GuardHome $GuardHome
    }
}

function Get-GuardPauseState {
    # @{ Paused; Source = 'pause' | 'legacy' | 'none' | 'corrupt'; UntilUtc; Reason }
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateSet('relaunch', 'overwatch', 'logarchive')][string]$Component,
          [string]$GuardHome, [string]$Nt8Dir, [Nullable[DateTime]]$NowUtc)
    $now = $(if ($null -ne $NowUtc) { [DateTime]$NowUtc } else { [DateTime]::UtcNow })   # Nullable is unwrapped: no .Value
    $legacy = Join-Path (Get-Nt8DataDir $Nt8Dir) $script:Legacy[$Component]
    if (Test-Path $legacy) {
        return [pscustomobject]@{ Paused = $true; Source = 'legacy'; UntilUtc = $null; Reason = "$($script:Legacy[$Component]) is present" }
    }
    if ($Component -eq 'logarchive') { return [pscustomobject]@{ Paused = $false; Source = 'none'; UntilUtc = $null; Reason = '' } }
    $path = Get-GuardPausePath $GuardHome
    if (-not (Test-Path $path)) { return [pscustomobject]@{ Paused = $false; Source = 'none'; UntilUtc = $null; Reason = '' } }
    try {
        $o = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        $set = ConvertFrom-GuardUtc $o.setUtc
        $until = ConvertFrom-GuardUtc $o.untilUtc
        if ($null -eq $set -or $null -eq $until) { throw 'bad timestamps' }
    } catch {
        # Fail toward PROTECTION: an unreadable pause file does not pause anything.
        Write-GuardLog -Component $Component -Message "pause.json is unreadable ($($_.Exception.Message)) - NOT paused" -GuardHome $GuardHome
        return [pscustomobject]@{ Paused = $false; Source = 'corrupt'; UntilUtc = $null; Reason = 'pause file unreadable' }
    }
    $cap = $set.AddMinutes($script:MaxMinutes)          # a hand-edited file cannot pause longer than 24 h
    if ($until -gt $cap) { $until = $cap }
    if ($now -lt $until) {
        return [pscustomobject]@{ Paused = $true; Source = 'pause'; UntilUtc = $until; Reason = [string]$o.reason }
    }
    try { Remove-Item $path -Force } catch { }
    Write-GuardEvent -Component guard -Kind 'pause.expired' -Severity info -Text 'Pause ended; Guard is watching again' -GuardHome $GuardHome
    return [pscustomobject]@{ Paused = $false; Source = 'none'; UntilUtc = $null; Reason = '' }
}

Export-ModuleMember -Function Get-GuardPausePath, Set-GuardPause, Clear-GuardPause, Get-GuardPauseState
