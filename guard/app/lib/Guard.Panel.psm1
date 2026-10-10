# What the control panel's buttons do, kept out of the window so it can be tested without one: presets
# and toggles, saving (guard.json and the feed guard's own settings file, which is generated from it),
# the watch list's text boxes, the phone-alert topic and its QR code, the diagnostics zip and Forget.
#
# The panel is the ONE writer of guard.json (spec 3.3). Every save goes through Save-GuardPanelSettings,
# which refuses a change the watchdogs or the add-on could not read - before writing anything.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Log.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Config.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Events.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Alerts.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Start.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Overwatch.psm1')   # Invoke-BackupSave (Forget is a full save)
Import-Module (Join-Path $PSScriptRoot 'Guard.FeedGuard.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Status.psm1')

$script:Toggles = @('relaunch', 'restartWhenFrozen', 'reconnect', 'recoverSilentFeed', 'restoreStrategies',
                    'reattachCharts', 'reenableStrategies', 'flattenUnprotected')

function Set-GuardPreset {
    # Choosing a preset drops every toggle set on top of the old one, so the preset reads as itself.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [Parameter(Mandatory)][ValidateSet('watch', 'recover', 'handsoff')][string]$Preset)
    $Config.protection.preset = $Preset
    foreach ($t in $script:Toggles) { [void]$Config.protection.Remove($t) }
    return $Config
}

function Set-GuardToggle {
    # One switch. Leaving the preset's value makes the effective preset 'custom' (Get-GuardProtection).
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Value)
    if (($script:Toggles + 'crashDumps') -notcontains $Name) { throw "unknown protection setting '$Name'" }
    $Config.protection[$Name] = $Value
    return $Config
}

function Save-GuardPanelSettings {
    # Validate, write guard.json, then regenerate the feed guard's file from it and signal a reload.
    # Returns @{ Ok; Message } - the message is shown under the Save button, so it says what happened in
    # words and never shows the alert address.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [string]$Section = 'Settings', [string]$GuardHome, [string]$Nt8Dir)
    $mask = { param($s) Hide-GuardSecret -Text ([string]$s) -Config $Config }
    $problems = @(Test-GuardConfig $Config)
    if ($problems.Count) { return [pscustomobject]@{ Ok = $false; Message = (& $mask ('Not saved: ' + ($problems -join '; '))) } }
    try { ConvertTo-FeedGuardJson (ConvertTo-FeedGuardSettings $Config) | Out-Null }   # the add-on's reader is stricter than guard.json's
    catch { return [pscustomobject]@{ Ok = $false; Message = (& $mask "Not saved: $($_.Exception.Message)") } }
    try { Save-GuardConfig -Config $Config -GuardHome $GuardHome }
    catch { return [pscustomobject]@{ Ok = $false; Message = (& $mask "Not saved: $($_.Exception.Message)") } }

    $nt8 = $(if ($Nt8Dir) { $Nt8Dir } else { Get-Nt8DataDir ([string]$Config.nt8.dataDir) })
    $msg = "$Section saved."
    if (-not (Test-Path -LiteralPath $nt8)) {
        $msg += " NinjaTrader's folder was not found ($nt8), so the feed guard's settings are written the next time you save once it exists."
    } else {
        try {
            $fg = Write-FeedGuardConfig -Config $Config -Nt8Dir $nt8
            if ($fg.Changed) { $msg += ' The feed guard picks the change up within seconds (or at NinjaTrader''s next start).' }
            if ($fg.Note) { $msg += " Note: $($fg.Note)" }
        } catch { $msg += " The feed guard's settings could not be written: $($_.Exception.Message)" }
    }
    Write-GuardEvent -Component panel -Kind 'settings.saved' -Severity info -Text "$Section saved" -GuardHome $GuardHome
    return [pscustomobject]@{ Ok = $true; Message = (& $mask $msg) }
}

function ConvertFrom-GuardListText {
    # A multi-line text box as a list: one entry per line, trimmed, blanks and repeats dropped.
    # Wrap the call in @( ): no entries returns nothing, one returns a single string.
    [CmdletBinding()] param([AllowEmptyString()][string]$Text)
    $seen = @{}
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($l in ([string]$Text -split "`r?`n")) {
        $t = $l.Trim()
        if ($t -and -not $seen.ContainsKey($t)) { $seen[$t] = 1; $out.Add($t) }
    }
    return $out.ToArray()
}

function Test-GuardWatchEntries {
    # Problems with the Watch list tab's entries, as sentences. Instruments go into the feed guard's file,
    # whose reader takes no quote or backslash; extra dates are what the add-on parses: 'yyyy-MM-dd' (closed
    # all day) or 'yyyy-MM-dd HH:mm' (closed from that time, New York time). Wrap the call in @( ).
    [CmdletBinding()] param([string[]]$Instruments = @(), [string[]]$ExtraDates = @())
    $bad = New-Object System.Collections.Generic.List[string]
    foreach ($i in $Instruments) { if ($i -match '["\\]') { $bad.Add("'$i' cannot hold a quote or a backslash") } }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    foreach ($d in $ExtraDates) {
        $x = [DateTime]::MinValue
        $ok = [DateTime]::TryParseExact($d, 'yyyy-MM-dd', $inv, 'None', [ref]$x) -or [DateTime]::TryParseExact($d, 'yyyy-MM-dd HH:mm', $inv, 'None', [ref]$x)
        if (-not $ok) { $bad.Add("'$d' is not a date like 2026-11-27 or 2026-11-27 13:15") }
    }
    return $bad.ToArray()
}

function New-GuardNtfyTopic {
    # A private ntfy topic. ntfy topics are PUBLIC: anyone who knows the name can read and send to it, so
    # it is long and random (22 characters from 36 = 113 bits).
    $chars = 'abcdefghijklmnopqrstuvwxyz0123456789'
    $bytes = New-Object byte[] 22
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return 'nt8guard-' + (-join ($bytes | ForEach-Object { $chars[$_ % 36] }))   # 256 % 36 = 4: a tiny bias, harmless
}

function Get-GuardNtfyAddress {
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config)
    return ([string]$Config.alerts.ntfyServer).TrimEnd('/') + '/' + [string]$Config.alerts.ntfyTopic
}

function Get-GuardQrPng {
    # PNG bytes of a QR code for $Text (the phone-alert address), drawn by the bundled Python (segno).
    # The text travels in an environment variable of the child process, not its command line: other
    # programs can read a command line, and this address is the password to the alert channel.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][hashtable]$Config, [string]$ToolsDir)
    $env:NT8GUARD_QR_TEXT = $Text
    try { $r = Invoke-GuardPython -Script 'qr_png.py' -TimeoutSeconds 30 -Config $Config -ToolsDir $ToolsDir }
    finally { Remove-Item Env:\NT8GUARD_QR_TEXT -ErrorAction SilentlyContinue }
    if ($r.ExitCode -ne 0) { throw ("could not draw the QR code: " + (Hide-GuardSecret -Text ("$($r.Err)".Trim()) -Config $Config)) }
    return [Convert]::FromBase64String(([string]$r.Out).Trim())
}

function Hide-GuardPersonal {
    # For files leaving this machine: the alert address, the Windows user name in paths, the PC name.
    param([string]$Text, [hashtable]$Config)
    $t = Hide-GuardSecret -Text $Text -Config $Config
    # guard.json is written by ConvertTo-Json, which escapes '&' as \u0026 (and ' < >): mask that form too.
    foreach ($s in @($Config.alerts.ntfyTopic, $Config.alerts.webhookUrl)) {
        if (-not $s) { continue }
        $esc = (ConvertTo-Json ([string]$s) -Compress).Trim('"')
        if ($esc -ne [string]$s) { $t = $t.Replace($esc, '***') }
    }
    # A path to the profile in any spelling: C:\Users\someone\, C:\\Users\\someone\\ (inside JSON), C:/Users/someone/.
    $names = @($env:USERNAME, (Split-Path ([Environment]::GetFolderPath('UserProfile')) -Leaf)) | Where-Object { $_ } | Select-Object -Unique
    foreach ($n in $names) { $t = [regex]::Replace($t, '(?i)([\\/]+Users[\\/]+)' + [regex]::Escape($n) + '(?=[\\/"]|$)', '$1<user>') }
    if ($env:COMPUTERNAME) { $t = [regex]::Replace($t, '(?i)\b' + [regex]::Escape($env:COMPUTERNAME) + '\b', '<pc>') }
    return $t
}

function New-GuardDiagnosticsZip {
    # A zip to attach to a GitHub issue: Guard's settings, logs, events, small state files and a status
    # summary, plus the feed guard's files from NinjaTrader's folder (long logs cut to their last 2000
    # lines). Never the secrets folder or the strategy backups (your strategies' settings). The alert
    # address, your Windows user name and the PC name are masked in every file.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OutPath, [string]$GuardHome, [string]$Nt8Dir, [scriptblock]$Processes, [scriptblock]$TaskState)
    $cfg = Get-GuardConfig $GuardHome
    if (-not $Nt8Dir) { $Nt8Dir = Get-Nt8DataDir ([string]$cfg.nt8.dataDir) }
    $gh = Get-GuardHome $GuardHome
    $files = [ordered]@{}   # entry name -> source path
    $add = { param($entry, $path) if (Test-Path -LiteralPath $path -PathType Leaf) { $files[$entry] = $path } }
    & $add 'guard.json' (Join-Path $gh 'guard.json')
    foreach ($f in @(Get-ChildItem (Get-GuardPath logs $GuardHome) -File -ErrorAction SilentlyContinue)) { & $add "logs/$($f.Name)" $f.FullName }
    foreach ($f in @(Get-ChildItem (Get-GuardPath events $GuardHome) -File -ErrorAction SilentlyContinue)) { & $add "events/$($f.Name)" $f.FullName }
    foreach ($f in @(Get-ChildItem (Get-GuardPath state $GuardHome) -File -ErrorAction SilentlyContinue | Where-Object { $_.Length -lt 1MB })) { & $add "state/$($f.Name)" $f.FullName }
    foreach ($n in 'AutoReconnect.config.json', 'AutoReconnect.heartbeat.json', 'AutoReconnect.log') { & $add "nt8/$n" (Join-Path $Nt8Dir $n) }
    & $add 'nt8/feedguard-events.jsonl' (Join-Path $Nt8Dir 'NT8Guard\feedguard-events.jsonl')
    & $add 'nt8/link-heartbeat.json' (Join-Path $Nt8Dir 'NT8Bridge\result\heartbeat.json')

    $sa = @{ GuardHome = $GuardHome; Nt8Dir = $Nt8Dir; Config = $cfg }
    if ($Processes) { $sa.Processes = $Processes }
    if ($TaskState) { $sa.TaskState = $TaskState }
    $st = Get-GuardStatus @sa
    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine("NT8 Guard diagnostics, $([DateTime]::UtcNow.ToString('u'))")
    [void]$sb.AppendLine("Windows $([Environment]::OSVersion.Version), PowerShell $($PSVersionTable.PSVersion), culture $([Globalization.CultureInfo]::CurrentCulture.Name), time zone $([TimeZoneInfo]::Local.Id)")
    [void]$sb.AppendLine("NT8 Guard $(Get-GuardVersion); NinjaTrader $(Get-Nt8Version ([string]$cfg.nt8.programDir)); feed guard expected $(Get-GuardExpectedFeedGuardVersion); protection $((Get-GuardProtection $cfg).preset)")
    [void]$sb.AppendLine("Overall: $($st.Overall.Text)")
    foreach ($b in $st.Banner) { [void]$sb.AppendLine("! $b") }
    foreach ($c in $st.Cards) { [void]$sb.AppendLine(("[{0,-5}] {1}: {2} - {3} {4}" -f $c.State, $c.Title, $c.Headline, $c.Detail, $c.Remedy).TrimEnd()) }
    [void]$sb.AppendLine('Recent events:')
    foreach ($e in $st.Events) { [void]$sb.AppendLine("  $($e.Time)  $($e.Source)  $($e.Severity)  $($e.Text)") }

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $dir = Split-Path $OutPath -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    if (Test-Path $OutPath) { Remove-Item $OutPath -Force }
    $utf8 = New-Object Text.UTF8Encoding($false)
    $zip = [IO.Compression.ZipFile]::Open($OutPath, 'Create')
    try {
        $put = {
            param($entry, $text)
            $w = New-Object IO.StreamWriter(($zip.CreateEntry($entry)).Open(), $utf8)
            try { $w.Write((Hide-GuardPersonal -Text $text -Config $cfg)) } finally { $w.Dispose() }
        }
        & $put 'status.txt' $sb.ToString()
        foreach ($entry in $files.Keys) {
            $text = ''
            # FileShare ReadWrite: the watchdogs and the add-on may be writing these right now.
            try {
                $fs = [IO.File]::Open($files[$entry], 'Open', 'Read', 'ReadWrite')
                try { $text = (New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
            } catch { $text = "(could not be read: $($_.Exception.Message))" }
            $lines = $text -split "`r?`n"
            if ($lines.Count -gt 2000) { $text = ($lines[($lines.Count - 2000)..($lines.Count - 1)]) -join "`r`n" }
            & $put $entry $text
        }
    } finally { $zip.Dispose() }
    return [pscustomobject]@{ Path = $OutPath; Files = $files.Count + 1
                              Note = 'Look through it before you post it: your alert address, Windows user name and PC name are masked, but account and connection names are still in the logs.' }
}

function Invoke-GuardBackupForget {
    # Drop one strategy's backup (only one NinjaTrader no longer lists - the exporter refuses a live one).
    # It is a full save too, so it goes through Invoke-BackupSave: a strategy that left NinjaTrader's list
    # since the last save is reported here exactly as the overwatch would have - the exporter reports each
    # change once. Under the start lock like every save. Returns @{ Ok; Message }.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [string]$Nt8Dir, [scriptblock]$Python)
    $sa = @{ Config = $Config; GuardHome = $GuardHome; Nt8Dir = $Nt8Dir; Component = 'panel'; Forget = @($Name) }
    if ($Python) { $sa.Python = $Python }
    $s = Invoke-BackupSave @sa
    if ($s.Result -eq 'busy') { return [pscustomobject]@{ Ok = $false; Message = 'NinjaTrader is being started right now; try again in a minute.' } }
    $rep = $s.Report
    if ($null -eq $rep) { return [pscustomobject]@{ Ok = $false; Message = "Not forgotten: $($s.Summary)" } }
    $kept = @($rep.kept | Where-Object { $_.name -eq $Name })
    if ($kept.Count) { return [pscustomobject]@{ Ok = $false; Message = "$Name was not forgotten: it $($kept[0].why)." } }
    if (@($rep.forgotten) -contains $Name) {
        Write-GuardEvent -Component panel -Kind 'backups.forgotten' -Severity info -Text "Forgot the backup of $Name" -GuardHome $GuardHome
        return [pscustomobject]@{ Ok = $true; Message = "Guard forgot $Name. Its template file stays in NinjaTrader's templates folder." }
    }
    return [pscustomobject]@{ Ok = $false; Message = "$Name is not in the backups." }
}

Export-ModuleMember -Function Set-GuardPreset, Set-GuardToggle, Save-GuardPanelSettings, ConvertFrom-GuardListText, Test-GuardWatchEntries,
                              New-GuardNtfyTopic, Get-GuardNtfyAddress, Get-GuardQrPng, Hide-GuardPersonal, New-GuardDiagnosticsZip,
                              Invoke-GuardBackupForget
