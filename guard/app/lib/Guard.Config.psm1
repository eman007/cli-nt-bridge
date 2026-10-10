# guard.json: the one settings file. Owned by the control panel; every script reads it through here.
#
# Defaults are complete, so a missing or partial file still yields every key. Protection is a preset
# ('watch' | 'recover' | 'handsoff') plus optional per-toggle overrides; the effective preset reads
# 'custom' once any toggle differs from the named one.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')

$script:ToggleNames = @('relaunch', 'restartWhenFrozen', 'reconnect', 'recoverSilentFeed', 'restoreStrategies',
                        'reattachCharts', 'reenableStrategies', 'flattenUnprotected')
$script:Presets = @{
    watch    = @{ relaunch = $false; restartWhenFrozen = $false; reconnect = $false; recoverSilentFeed = $false
                  restoreStrategies = $false; reattachCharts = $false; reenableStrategies = $false; flattenUnprotected = $false }
    recover  = @{ relaunch = $true; restartWhenFrozen = $true; reconnect = $true; recoverSilentFeed = $true
                  restoreStrategies = $true; reattachCharts = $true; reenableStrategies = $false; flattenUnprotected = $false }
    handsoff = @{ relaunch = $true; restartWhenFrozen = $true; reconnect = $true; recoverSilentFeed = $true
                  restoreStrategies = $true; reattachCharts = $true; reenableStrategies = $true; flattenUnprotected = $true }
}

function Get-GuardDefaults {
    return @{
        schemaVersion = 1
        nt8           = @{ dataDir = 'auto'; programDir = 'auto' }
        login         = @{ helper = 'builtin'; clearCacheBeforeLaunch = $false }
        python        = @{ exe = 'auto' }
        protection    = @{ preset = 'recover'; crashDumps = $false }
        relaunch      = @{ graceMinutes = 3; stuckMinutes = 6; loginDeadSeconds = 90; maxActions = 4
                           windowMinutes = 30; backoffMinutes = 60; configUnlockWaitSeconds = 20 }
        thresholds    = @{ guardianStaleSeconds = 90; bridgeStaleSeconds = 180; feedStaleSeconds = 300
                           consecutiveToAct = 3; cooldownMinutes = 20; backupEveryMinutes = 30 }
        # The overwatch's finer timings (each explained where Guard-Overwatch.ps1 uses it).
        overwatch     = @{ startupGraceSeconds = 300; feedDeferMaxSeconds = 900; feedSourceOutageMaxSeconds = 1800
                           sourceVerdictMaxAgeMinutes = 20; restoreAfterAnyRestart = $true; restoreMaxAgeMinutes = 240
                           strategyCacheMinutes = 60; strategyDisableMarkerWindowMinutes = 60; connSettleSeconds = 30
                           connRetryCooldownMinutes = 10; connRestoreMinUptimeSeconds = 180; restoreDeferMaxMinutes = 12
                           bridgeTimeoutSeconds = 30; relauncherStaleSeconds = 180; dryRunRemedy = $false
                           dumpFullMemory = $true; dumpTimeoutSeconds = 180; dumpKeep = 2 }
        logKeeper     = @{ retentionDays = 7; livePruneDays = 3 }
        alerts        = @{ ntfyServer = 'https://ntfy.sh'; ntfyTopic = ''; webhookUrl = ''
                           priorities = @{ info = 3; warn = 4; alert = 5 } }
        watch         = @{ instruments = @('ES ##-##', 'NQ ##-##'); staleSeconds = 75
                           marketTimeZone = 'Eastern Standard Time'; extraDates = @() }
        setup         = @{ completed = $false; step = 1 }
    }
}

function ConvertTo-GuardHashtable {
    # ConvertFrom-Json gives PSCustomObjects (PowerShell 5.1 has no -AsHashtable); merge needs hashtables.
    [CmdletBinding()] param($InputObject)
    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [System.Management.Automation.PSCustomObject]) {
        $h = @{}
        foreach ($p in $InputObject.PSObject.Properties) { $h[$p.Name] = ConvertTo-GuardHashtable $p.Value }
        return $h
    }
    if ($InputObject -is [System.Collections.IEnumerable] -and $InputObject -isnot [string] -and $InputObject -isnot [hashtable]) {
        return , @($InputObject | ForEach-Object { ConvertTo-GuardHashtable $_ })
    }
    return $InputObject
}

function Merge-GuardHashtable {
    # Recursively lay $Over on top of a COPY of $Base. Nested hashtables merge; anything else replaces.
    [CmdletBinding()] param([hashtable]$Base, [hashtable]$Over)
    $out = @{}
    foreach ($k in $Base.Keys) { $out[$k] = $Base[$k] }
    if ($Over) {
        foreach ($k in $Over.Keys) {
            if ($out[$k] -is [hashtable] -and $Over[$k] -is [hashtable]) { $out[$k] = Merge-GuardHashtable $out[$k] $Over[$k] }
            else { $out[$k] = $Over[$k] }
        }
    }
    return $out
}

function Get-GuardConfigPath { [CmdletBinding()] param([string]$GuardHome) Join-Path (Get-GuardHome $GuardHome) 'guard.json' }

function Repair-GuardValue {
    # $Value checked against the shape of $Default; anything unusable is replaced by $Fallback and its
    # dotted path recorded in $Problems. Numbers must be numbers (a decimal counts) and stay above zero
    # where the default is; text must be text; a list may be one string. Extra keys are kept.
    param($Value, $Default, $Fallback, [string]$Path, [System.Collections.Generic.List[string]]$Problems)
    if ($Default -is [hashtable]) {
        # an unusable section is replaced whole; a missing key simply takes its default (partial files)
        if ($Value -isnot [hashtable]) { $Problems.Add($Path); return $(if ($Fallback -is [hashtable]) { $Fallback } else { $Default }) }
        $out = @{}
        foreach ($k in $Value.Keys) { $out[$k] = $Value[$k] }
        foreach ($k in $Default.Keys) {
            if (-not $Value.ContainsKey($k)) { $out[$k] = $Default[$k]; continue }
            $fb = $(if ($Fallback -is [hashtable] -and $Fallback.ContainsKey($k)) { $Fallback[$k] } else { $Default[$k] })
            $out[$k] = Repair-GuardValue $Value[$k] $Default[$k] $fb "$Path.$k" $Problems
        }
        return $out
    }
    if ($Default -is [array]) {
        if ($Value -is [string]) { return , @($Value) }
        if ($null -ne $Value -and $Value -is [System.Collections.IEnumerable] -and $Value -isnot [hashtable]) { return , @($Value) }
        $Problems.Add($Path); return , @($Fallback)
    }
    if ($Default -is [bool]) {
        if ($Value -is [bool]) { return $Value }
        $Problems.Add($Path); return $Fallback
    }
    if ($Default -is [int] -or $Default -is [long] -or $Default -is [double]) {
        if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) {
            # Windows PowerShell 5.1's ConvertFrom-Json reads 1.5 as [decimal]
            $n = $(if ($Value -is [decimal]) { [double]$Value } else { $Value })
            if ($Default -gt 0 -and $n -le 0) { $Problems.Add($Path); return $Fallback }
            return $n
        }
        $Problems.Add($Path); return $Fallback
    }
    if ($Default -is [string]) {
        if ($Value -is [string]) { return $Value }
        $Problems.Add($Path); return $Fallback
    }
    return $Value
}

function Repair-GuardConfig {
    # Returns @{ Config; Problems } - $Config laid over the defaults, every unusable piece replaced by
    # the matching piece of $Fallback (the last good settings, else the defaults).
    param([hashtable]$Config, [hashtable]$Fallback)
    $problems = New-Object System.Collections.Generic.List[string]
    $defaults = Get-GuardDefaults
    if (-not $Fallback) { $Fallback = $defaults }
    $fixed = Repair-GuardValue $Config $defaults $Fallback 'guard' $problems
    $pr = $fixed.protection
    if (@('watch', 'recover', 'handsoff', 'custom') -notcontains [string]$pr.preset) {
        $problems.Add('guard.protection.preset'); $pr.preset = $Fallback.protection.preset
    }
    foreach ($t in $script:ToggleNames) {
        if ($pr.ContainsKey($t) -and $null -ne $pr[$t] -and $pr[$t] -isnot [bool]) {
            $problems.Add("guard.protection.$t")
            if ($Fallback.protection.ContainsKey($t) -and $Fallback.protection[$t] -is [bool]) { $pr[$t] = $Fallback.protection[$t] } else { $pr.Remove($t) }
        }
    }
    return @{ Config = $fixed; Problems = @($problems | ForEach-Object { $_ -replace '^guard\.', '' }) }
}

function Read-GuardConfigFile {
    # @{ Config; Problems; Error } for one file. Error = it could not be used at all.
    param([string]$Path, [hashtable]$Fallback)
    try {
        $raw = [IO.File]::ReadAllText($Path)
        if (-not $raw.Trim()) { return @{ Error = 'it is empty' } }
        $parsed = ConvertTo-GuardHashtable ($raw | ConvertFrom-Json)
        if ($parsed -isnot [hashtable]) { return @{ Error = 'it does not hold a settings object' } }
        $r = Repair-GuardConfig -Config $parsed -Fallback $Fallback
        return @{ Config = $r.Config; Problems = $r.Problems; Error = $null }
    } catch { return @{ Error = "it could not be read ($($_.Exception.Message))" } }
}

function Get-GuardConfig {
    # Defaults overlaid with guard.json; never throws. Anything unusable falls back to the LAST GOOD
    # settings (guard.json.lastgood, refreshed after every clean load and every save), else the
    # defaults - a truncated file must not silently switch a Watch-only user to Recover NT8. The reason
    # is in $cfg.loadError; the caller logs and alerts (Publish-GuardConfigProblem).
    [CmdletBinding()] param([string]$GuardHome)
    $path = Get-GuardConfigPath $GuardHome
    $lastGood = "$path.lastgood"
    $fallback = Get-GuardDefaults
    $from = 'the default settings'
    if (Test-Path $lastGood) {
        $lg = Read-GuardConfigFile -Path $lastGood -Fallback $fallback
        if (-not $lg.Error -and -not $lg.Problems.Count) {
            $fallback = $lg.Config
            $from = "the last good copy ($((Get-Item $lastGood).LastWriteTime.ToString('yyyy-MM-dd HH:mm')))"
        }
    }
    if (-not (Test-Path $path)) {
        if ($from -eq 'the default settings') { return (Get-GuardDefaults) }
        $fallback.loadError = "guard.json is missing; using $from"
        return $fallback
    }
    $r = Read-GuardConfigFile -Path $path -Fallback $fallback
    if ($r.Error) {
        $fallback.loadError = "guard.json is not usable because $($r.Error); using $from"
        return $fallback
    }
    $cfg = $r.Config
    if ($r.Problems.Count) {
        $cfg.loadError = "guard.json: these settings were not usable, so they use $from instead: $($r.Problems -join ', ')"
    } else {
        try {
            $same = (Test-Path $lastGood) -and ([IO.File]::ReadAllText($lastGood) -ceq [IO.File]::ReadAllText($path))
            if (-not $same) { [IO.File]::Copy($path, $lastGood, $true) }
        } catch { }   # another reader copying at the same moment: harmless
    }
    return $cfg
}

function Get-GuardProtection {
    # The effective toggles: the named preset, then any explicit toggle. Plus crashDumps and the
    # effective preset name ('custom' when a toggle departs from the named preset).
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config)
    $p = $Config.protection
    $name = [string]$p.preset
    if (-not $script:Presets.ContainsKey($name)) { $name = 'recover' }
    $eff = @{}
    foreach ($t in $script:ToggleNames) {
        $eff[$t] = $script:Presets[$name][$t]
        if ($p.ContainsKey($t) -and $null -ne $p[$t]) { $eff[$t] = [bool]$p[$t] }
    }
    $eff.crashDumps = [bool]$p.crashDumps
    $eff.preset = $name
    foreach ($t in $script:ToggleNames) { if ($eff[$t] -ne $script:Presets[$name][$t]) { $eff.preset = 'custom'; break } }
    return $eff
}

function Get-GuardPreset {
    [CmdletBinding()] param([Parameter(Mandatory)][ValidateSet('watch', 'recover', 'handsoff')][string]$Name)
    $h = @{}
    foreach ($k in $script:Presets[$Name].Keys) { $h[$k] = $script:Presets[$Name][$k] }
    return $h
}

function Test-GuardConfig {
    # Every problem with a settings object, as plain sentences. Empty = valid.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config)
    $bad = New-Object System.Collections.Generic.List[string]
    if ($Config.schemaVersion -ne 1) { $bad.Add("schemaVersion must be 1 (found '$($Config.schemaVersion)')") }
    $preset = [string]$Config.protection.preset
    if (@('watch', 'recover', 'handsoff', 'custom') -notcontains $preset) { $bad.Add("protection.preset must be watch, recover, handsoff or custom (found '$preset')") }
    foreach ($t in $script:ToggleNames + @('crashDumps')) {
        if ($Config.protection.ContainsKey($t) -and $Config.protection[$t] -isnot [bool]) { $bad.Add("protection.$t must be true or false") }
    }
    $helper = [string]$Config.login.helper
    if ($helper -ne 'builtin' -and -not ($helper -like '*.exe' -and (Test-Path $helper))) {
        $bad.Add("login.helper must be 'builtin' or the full path of an existing .exe (found '$helper')")
    }
    if ([string]$Config.alerts.ntfyServer -notmatch '^https?://[^\s/]+') { $bad.Add('alerts.ntfyServer must be an http(s) address') }
    if ([string]$Config.alerts.ntfyTopic -notmatch '^[A-Za-z0-9_-]{0,64}$') { $bad.Add('alerts.ntfyTopic may only use letters, digits, - and _ (64 at most)') }
    if ($Config.alerts.webhookUrl -and [string]$Config.alerts.webhookUrl -notmatch '^https?://\S+$') { $bad.Add('alerts.webhookUrl must be empty or an http(s) address') }
    $inst = @($Config.watch.instruments | Where-Object { $_ -and ([string]$_).Trim() })
    if ($inst.Count -lt 1) { $bad.Add('watch.instruments needs at least one instrument') }
    if ([int]$Config.watch.staleSeconds -lt 30) { $bad.Add('watch.staleSeconds must be at least 30') }
    $defaults = Get-GuardDefaults
    foreach ($sec in 'relaunch', 'thresholds', 'logKeeper', 'overwatch') {
        foreach ($k in $Config[$sec].Keys) {
            $v = $Config[$sec][$k]
            if ($defaults[$sec][$k] -is [bool]) {
                if ($v -isnot [bool]) { $bad.Add("$sec.$k must be true or false") }
            } elseif (-not ($v -is [int] -or $v -is [long] -or $v -is [double]) -or $v -le 0) { $bad.Add("$sec.$k must be a positive number") }
        }
    }
    return $bad.ToArray()
}

function Save-GuardConfig {
    # Validate, then write atomically (temp file + rename). Refuses an invalid settings object.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome)
    $problems = Test-GuardConfig $Config
    if ($problems.Count) { throw ("guard.json not saved: " + ($problems -join '; ')) }
    $clean = @{}
    foreach ($k in $Config.Keys) { if ($k -ne 'loadError') { $clean[$k] = $Config[$k] } }
    $path = Get-GuardConfigPath $GuardHome
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$path.tmp"
    [IO.File]::WriteAllText($tmp, ($clean | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Move-Item -Path $tmp -Destination $path -Force
    try { [IO.File]::Copy($path, "$path.lastgood", $true) } catch { }
}

Export-ModuleMember -Function Get-GuardDefaults, ConvertTo-GuardHashtable, Merge-GuardHashtable, Get-GuardConfigPath,
                              Repair-GuardConfig, Get-GuardConfig, Get-GuardProtection, Get-GuardPreset, Test-GuardConfig,
                              Save-GuardConfig
