# The feed guard add-on's settings: AutoReconnect.config.json in NinjaTrader's folder, generated from
# guard.json (the control panel calls Write-FeedGuardConfig after every save), then the
# AutoReconnect.reload signal so the running add-on re-reads it within one tick - no NinjaTrader restart.
#
# Guard owns the keys below; any OTHER key already in the file (hand tuning such as graceSeconds or
# "connections") is kept as it is. Keys nothing reads any more are dropped.
#
# ! The add-on reads this file with a deliberately tiny parser (no JSON library inside NinjaTrader): it
#   finds the first "key", takes the value after the next colon, and does NOT unescape strings. So this
#   writer puts Guard's keys first (a kept value can never shadow one), writes one value per line, and
#   refuses any text holding a quote, a backslash or a control character instead of escaping it.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Config.psm1')

$script:ManagedKeys = @('watchdogInstruments', 'watchdogStaleSeconds', 'marketTimeZoneId', 'holidays', 'useSessionGate',
                        'reconnect', 'autoRecover', 'reEnableAfterRecover', 'webhookUrl', 'webhookFormat', 'alertSource')
# canaryInstrument / pokeSettleSeconds / netProbeUrl: read only by triggers removed in add-on 5.0.0.
# watchdogInstrument (one name, before the list existed): the add-on falls back to it when the list is
# empty, which would quietly undo an emptied watch list.
$script:DroppedKeys = @('canaryInstrument', 'pokeSettleSeconds', 'netProbeUrl', 'watchdogInstrument')

function Get-FeedGuardConfigPath {
    [CmdletBinding()] param([string]$Nt8Dir)
    return [IO.Path]::Combine((Get-Nt8DataDir $Nt8Dir), 'AutoReconnect.config.json')
}

function ConvertTo-FeedGuardSettings {
    # guard.json (a loaded config hashtable) -> the add-on keys Guard owns, in the order they are written.
    # -Stopped (Guard stopped from Tools): the add-on only watches - no reconnect, no recovery, no strategy
    # switched back on, no push - until Start writes the file from guard.json again.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [switch]$Stopped)
    $p = Get-GuardProtection $Config
    $a = $Config.alerts
    $url = ''; $format = 'json'
    if ($a.ntfyTopic) { $url = ([string]$a.ntfyServer).TrimEnd('/') + '/' + $a.ntfyTopic; $format = 'text' }   # ntfy shows the bare text
    elseif ($a.webhookUrl) { $url = [string]$a.webhookUrl }
    if ($Stopped) { $p = @{ reconnect = $false; recoverSilentFeed = $false; reenableStrategies = $false }; $url = '' }
    $s = [ordered]@{}
    $s.watchdogInstruments = @($Config.watch.instruments | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    $s.watchdogStaleSeconds = $Config.watch.staleSeconds
    $s.marketTimeZoneId = [string]$Config.watch.marketTimeZone
    $s.holidays = @($Config.watch.extraDates | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    $s.useSessionGate = $true
    $s.reconnect = [bool]$p.reconnect
    $s.autoRecover = [bool]$p.recoverSilentFeed
    $s.reEnableAfterRecover = [bool]$p.reenableStrategies
    $s.webhookUrl = $url
    $s.webhookFormat = $format
    $s.alertSource = Get-GuardSystemName $Config     # starts the feed guard's push titles (add-on 5.0.1+)
    return $s
}

function ConvertTo-FeedGuardJson {
    # One key per line, managed keys first. Throws on text the add-on's reader cannot take.
    [CmdletBinding()] param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Settings)
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($k in $Settings.Keys) {
        $v = $Settings[$k]
        if ($v -is [bool]) { $txt = $(if ($v) { 'true' } else { 'false' }) }
        elseif ($v -is [int] -or $v -is [long] -or $v -is [double] -or $v -is [decimal]) { $txt = ([double]$v).ToString($inv) }
        elseif ($v -is [string]) { $txt = Format-FeedGuardString $k $v }
        elseif ($v -is [System.Collections.IEnumerable]) { $txt = '[' + ((@($v) | ForEach-Object { Format-FeedGuardString $k ([string]$_) }) -join ', ') + ']' }
        else { continue }   # nothing the add-on reads (nested objects, nulls)
        $lines.Add('  "' + $k + '": ' + $txt)
    }
    return "{`r`n" + ($lines -join ",`r`n") + "`r`n}`r`n"
}

function Format-FeedGuardString([string]$Key, [string]$Value) {
    # Guard's own keys: refused rather than escaped (the add-on would read the escapes literally).
    # A key Guard does not own was carried over from the user's file: escape it back to JSON, which
    # reproduces exactly the raw text the add-on read before - C:\\Sounds\\x.wav stays C:\\Sounds\\x.wav.
    if ($script:ManagedKeys -notcontains $Key) {
        $esc = $Value.Replace('\', '\\').Replace('"', '\"')
        $esc = [regex]::Replace($esc, '[\x00-\x1F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
        return '"' + $esc + '"'
    }
    if ($Value -match '["\\\x00-\x1F]') { throw "feed guard setting '$Key' holds a quote, a backslash or a control character, which the add-on cannot read" }
    return '"' + $Value + '"'
}

function Write-FeedGuardConfig {
    # Write AutoReconnect.config.json from guard.json and, when it changed, drop AutoReconnect.reload.
    # Returns @{ Path; Changed; Reloaded; Kept (other keys carried over); Note }. Never prints the
    # alert address (the ntfy topic is the password to the alert channel). -Stopped: see ConvertTo-FeedGuardSettings.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [string]$Nt8Dir, [switch]$NoReload, [switch]$Stopped)
    $path = Get-FeedGuardConfigPath $Nt8Dir
    $settings = ConvertTo-FeedGuardSettings $Config -Stopped:$Stopped
    $kept = @(); $note = ''
    $old = $null
    if (Test-Path $path) {
        $old = [IO.File]::ReadAllText($path)
        try {
            $parsed = $old | ConvertFrom-Json
            foreach ($prop in $parsed.PSObject.Properties) {
                if ($script:ManagedKeys -contains $prop.Name -or $script:DroppedKeys -contains $prop.Name) { continue }
                if ($prop.Value -is [System.Management.Automation.PSCustomObject]) { continue }
                $settings[$prop.Name] = $prop.Value
                $kept += $prop.Name
            }
        } catch {
            # The add-on's parser is looser than ConvertFrom-Json (a trailing comma, a raw C:\path), so this
            # file may still hold the user's connections / exclude / feedOwnerConnections. Keep a copy.
            $bak = "$path.bak"
            try { [IO.File]::Copy($path, $bak, $true) } catch { }
            $note = 'the existing file could not be read, so no other settings were carried over; it was saved as ' +
                    (Split-Path $bak -Leaf) + ' - copy any of your own settings back from it'
        }
    }
    $text = ConvertTo-FeedGuardJson $settings
    if ($null -ne $old -and $old -ceq $text) {
        return [pscustomobject]@{ Path = $path; Changed = $false; Reloaded = $false; Kept = $kept; Note = $note }
    }
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$path.tmp"
    [IO.File]::WriteAllText($tmp, $text, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $path -Force
    $reloaded = $false
    if (-not $NoReload) {
        # The add-on deletes it when it reads it (within one tick); if NinjaTrader is closed it waits for
        # the next start, where it is harmless.
        [IO.File]::WriteAllText([IO.Path]::Combine((Split-Path $path -Parent), 'AutoReconnect.reload'), 'NT8 Guard ' + [DateTime]::UtcNow.ToString('o'))
        $reloaded = $true
    }
    return [pscustomobject]@{ Path = $path; Changed = $true; Reloaded = $reloaded; Kept = $kept; Note = $note }
}

Export-ModuleMember -Function Get-FeedGuardConfigPath, ConvertTo-FeedGuardSettings, ConvertTo-FeedGuardJson, Write-FeedGuardConfig
