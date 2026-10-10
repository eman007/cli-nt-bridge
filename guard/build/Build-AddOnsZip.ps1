# NT8Guard-AddOns.zip: NT8 Guard's two NinjaTrader add-ons - the feed guard (AutoReconnect.cs) and the
# NT8 link (NT8BridgeServer.cs + NT8BridgeServerPlayback.cs, ONE partial class: always both) - in the
# form NinjaTrader's own Tools > Import > NinjaScript Add-On accepts:
#   - Info.xml and AddOns/ at the zip ROOT (not bin/Custom/...);
#   - FORWARD-slash entry names. The .NET Framework zip helpers write backslashes, and NinjaTrader's
#     importer then fails with its catch-all "Import failed" dialog;
#   - no "NinjaScript generated code" region in any source: NinjaTrader writes its own on import, and a
#     second copy fails the whole import (the same catch-all dialog).
# The feed guard's BuildStamp is stamped in the zip copy only, so its heartbeat names the build.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File guard\build\Build-AddOnsZip.ps1 [-OutDir <dir>] [-Stamp <text>]
[CmdletBinding()]
param([string]$OutDir, [string]$Stamp, [string]$SourceDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))   # the NT8Bridge folder
if (-not $SourceDir) { $SourceDir = Join-Path $root 'addon' }
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'out' }
if (-not $Stamp) {
    $sha = ''
    try { $sha = (& git -C $root rev-parse --short HEAD 2>$null) } catch { }
    $Stamp = $(if ($sha) { "$sha $([DateTime]::UtcNow.ToString('yyyy-MM-dd'))" } else { "release $([DateTime]::UtcNow.ToString('yyyy-MM-dd'))" })
}
if ($Stamp -match '["\\]') { throw "the build stamp may not contain a quote or a backslash: $Stamp" }

$entries = [ordered]@{
    'AddOns/AutoReconnect.cs'           = Join-Path $SourceDir 'AutoReconnect.cs'
    'AddOns/NT8BridgeServer.cs'         = Join-Path $SourceDir 'NT8BridgeServer.cs'
    'AddOns/NT8BridgeServerPlayback.cs' = Join-Path $SourceDir 'NT8BridgeServerPlayback.cs'
}
$utf8 = New-Object Text.UTF8Encoding($false)
$bytes = @{}
foreach ($name in $entries.Keys) {
    $src = $entries[$name]
    if (-not (Test-Path $src)) { throw "missing add-on source: $src" }
    $text = [IO.File]::ReadAllText($src)
    if ($text.Contains('NinjaScript generated code')) {
        throw "$src holds a 'NinjaScript generated code' region - NinjaTrader's import would fail on it. Strip it first."
    }
    if ($name -eq 'AddOns/AutoReconnect.cs') {
        $pattern = 'private const string BuildStamp = "[^"]*";'
        if ($text -notmatch $pattern) { throw "AutoReconnect.cs has no BuildStamp line to stamp" }
        $text = [regex]::Replace($text, $pattern, ("private const string BuildStamp = `"$Stamp`";").Replace('$', '$$'))
        $bytes[$name] = $utf8.GetBytes($text)
    } else {
        $bytes[$name] = [IO.File]::ReadAllBytes($src)
    }
}
$version = ''
if ([Text.Encoding]::UTF8.GetString($bytes['AddOns/AutoReconnect.cs']) -match 'internal const string AddOnVersion = "([^"]+)";') { $version = $matches[1] }

$info = @'
<?xml version="1.0" encoding="utf-8"?>
<NinjaTrader>
  <Export>
    <Version>8.1.6.0</Version>
    <Agile>None</Agile>
  </Export>
</NinjaTrader>
'@

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$zip = Join-Path $OutDir 'NT8Guard-AddOns.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
$fs = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
try {
    $arch = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $all = [ordered]@{ 'Info.xml' = $utf8.GetBytes($info.Replace("`r`n", "`n").Replace("`n", "`r`n")) }
        foreach ($k in $entries.Keys) { $all[$k] = $bytes[$k] }   # $entries is ordered; $bytes is not
        foreach ($k in $all.Keys) {
            $e = $arch.CreateEntry($k, [IO.Compression.CompressionLevel]::Optimal)   # explicit "/" names
            $s = $e.Open()
            try { $s.Write($all[$k], 0, $all[$k].Length) } finally { $s.Dispose() }
        }
    } finally { $arch.Dispose() }
} finally { $fs.Dispose() }

# Read it back the way an importer would: the exact entry list, no backslash anywhere.
$check = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($check.Entries | ForEach-Object { $_.FullName })
    $bad = @($names | Where-Object { $_.Contains([char]92) })
    if ($bad.Count) { throw "zip entry names with a backslash: $($bad -join ', ')" }
    $want = @('Info.xml') + @($entries.Keys)
    if (($names -join '|') -ne ($want -join '|')) { throw "unexpected zip entries: $($names -join ', ')" }
} finally { $check.Dispose() }
"NT8Guard-AddOns.zip: feed guard $version (build $Stamp) + NT8 link -> $zip"
