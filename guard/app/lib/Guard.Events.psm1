# Structured events: one JSON object per line, one file per writer (<GuardHome>\events\<component>.jsonl;
# the feed guard add-on writes its own inside NT8's folder). Readers merge by time. This is what the
# panel's "Recent events" shows and what scripts count (e.g. relaunch backoff), instead of grepping
# log sentences - a reworded log line silently broke a reader before.
#
#   { "utc": "2026-10-09T07:14:13.1234567Z", "component": "relaunch", "kind": "nt8.launch",
#     "severity": "info" | "warn" | "alert", "text": "...", "data": { ... } }
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Log.psm1')

function Write-GuardEvent {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Component, [Parameter(Mandatory)][string]$Kind,
          [ValidateSet('info', 'warn', 'alert')][string]$Severity = 'info',
          [Parameter(Mandatory)][AllowEmptyString()][string]$Text, [hashtable]$Data = @{}, [string]$GuardHome)
    try {
        $f = Join-Path (Get-GuardPath events $GuardHome) "$Component.jsonl"
        Step-GuardFileRotation $f
        $line = [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); component = $Component; kind = $Kind
                            severity = $Severity; text = $Text; data = $Data } | ConvertTo-Json -Compress -Depth 5
        [IO.File]::AppendAllText($f, $line + "`n", (New-Object Text.UTF8Encoding($false)))
    } catch { }
}

function Read-GuardEvents {
    # Newest first. Bad lines are skipped, never fatal.
    [CmdletBinding()]
    param([string]$GuardHome, [string]$Nt8Dir, [int]$Max = 50, [string]$Component, [string]$KindPattern,
          [Nullable[DateTime]]$SinceUtc)
    $files = @(Get-ChildItem (Get-GuardPath events $GuardHome) -Filter '*.jsonl' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $fg = Get-FeedGuardEventsPath $Nt8Dir
    if (Test-Path $fg) { $files += $fg }
    $all = New-Object System.Collections.Generic.List[object]
    foreach ($f in $files) {
        # Shared read (ReadWrite + Delete): ReadAllLines denies writing while it reads, which made a writer's
        # append fail - Write-GuardEvent does not retry - and the control panel reads every 2 seconds.
        try {
            $fs = [IO.File]::Open($f, 'Open', 'Read', 'ReadWrite, Delete')
            try { $text = (New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
            $lines = $text -split "`r?`n"
        } catch { continue }
        foreach ($l in $lines) {
            if (-not $l.Trim()) { continue }
            try { $e = $l | ConvertFrom-Json } catch { continue }
            if (-not $e.utc -or -not $e.kind) { continue }
            if ($Component -and $e.component -ne $Component) { continue }
            if ($KindPattern -and $e.kind -notmatch $KindPattern) { continue }
            if ($null -ne $SinceUtc) {
                # PowerShell unwraps [Nullable[DateTime]]: $SinceUtc IS the DateTime (no .Value)
                $t = ConvertFrom-GuardUtc $e.utc
                if ($null -eq $t -or $t -lt $SinceUtc) { continue }
            }
            $all.Add($e)
        }
    }
    # A plain array (no leading comma): callers may wrap the call in @( ) safely.
    return @($all | Sort-Object -Property utc -Descending | Select-Object -First $Max)
}

Export-ModuleMember -Function Write-GuardEvent, Read-GuardEvents
