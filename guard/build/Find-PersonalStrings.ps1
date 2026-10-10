# Find-PersonalStrings.ps1 - refuse to publish anything that names the author's machines, accounts
# or private alert channel.
#
# Scans EVERY file under -Path, binaries included. Point it at exactly what will ship (the release
# build packs from `git ls-files`): a working tree also holds generated files such as __pycache__\*.pyc,
# which embed the build machine's paths and are rightly reported.
#
# The deny-list is NOT part of the package (it would itself be personal): pass -DenyList with one
# pattern per line (regular expressions, case-insensitive; blank lines and '#' comments ignored).
# -ConfigFile adds the live alert topic / webhook from a guard.json or NT8Overwatch.config.json
# WITHOUT printing them: a hit on one is reported as '<private alert channel>'.
#
#   Find-PersonalStrings.ps1 -Path guard -DenyList <private list> [-ConfigFile <guard.json>]
# Exit 0 = clean, 1 = hits found (each printed as file:line: pattern).
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$DenyList, [string[]]$ConfigFile = @())

$ErrorActionPreference = 'Stop'
$patterns = @(Get-Content $DenyList | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
$secrets = @()
foreach ($cf in $ConfigFile) {
    if (-not (Test-Path $cf)) { continue }
    $j = Get-Content $cf -Raw | ConvertFrom-Json
    foreach ($v in @($j.alerts.ntfyTopic, $j.alerts.webhookUrl, $j.ntfyUrl)) { if ($v) { $secrets += [string]$v } }
}
function Get-ScanText {
    # EVERY file is scanned, whatever its extension: a compiled .pyc embeds the build machine's paths, and
    # a LICENSE, .cmd or .ini ships like any script. Text by its byte-order mark; anything else as
    # Latin-1 (every byte maps to one character, so ASCII patterns match inside binaries); plus a UTF-16
    # reading when the file holds NUL bytes (UTF-16 text without a byte-order mark).
    param([string]$File)
    $b = [IO.File]::ReadAllBytes($File)
    $texts = @()
    if ($b.Length -ge 2 -and $b[0] -eq 0xFF -and $b[1] -eq 0xFE) { $texts += [Text.Encoding]::Unicode.GetString($b, 2, $b.Length - 2) }
    elseif ($b.Length -ge 2 -and $b[0] -eq 0xFE -and $b[1] -eq 0xFF) { $texts += [Text.Encoding]::BigEndianUnicode.GetString($b, 2, $b.Length - 2) }
    elseif ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { $texts += [Text.Encoding]::UTF8.GetString($b, 3, $b.Length - 3) }
    else {
        $texts += [Text.Encoding]::GetEncoding(28591).GetString($b)
        if ([Array]::IndexOf($b, [byte]0) -ge 0) { $texts += [Text.Encoding]::Unicode.GetString($b) }
    }
    return $texts
}

$hits = 0
$files = @(Get-ChildItem -Path $Path -Recurse -File -Force)
foreach ($f in $files) {
    $lines = @(Get-ScanText $f.FullName | ForEach-Object { $_ -split "`r?`n" })
    $n = 0
    foreach ($line in $lines) {
        $n++
        foreach ($p in $patterns) {
            if ($line -match $p) { "{0}:{1}: matches '{2}'" -f $f.FullName, $n, $p; $hits++ }
        }
        foreach ($s in $secrets) {
            if ($line.Contains($s) -or ($s -match '/([^/]+)$' -and $line.Contains($matches[1]))) {
                "{0}:{1}: <private alert channel>" -f $f.FullName, $n; $hits++
            }
        }
    }
}
if ($hits) { "FOUND $hits personal string(s) in $Path"; exit 1 }
"clean: $($files.Count) files, $($patterns.Count) patterns, $($secrets.Count) private channel(s) checked"
exit 0
