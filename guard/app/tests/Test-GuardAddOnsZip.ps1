# guard\build\Build-AddOnsZip.ps1 makes the zip NinjaTrader's Tools > Import > NinjaScript Add-On
# accepts: Info.xml + AddOns/ at the root, forward-slash names, both halves of the NT8 link, no
# generated-code region, the feed guard stamped in the zip copy only. (Whether a REAL NinjaTrader then
# imports it cleanly over existing copies - spec section 11.4 - is checked in the fresh-install test.)
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$build = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\build\Build-AddOnsZip.ps1'))
$addon = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\addon'))
$out = New-TestDir zipout
function Invoke-Build([string[]]$BuildArgs) {
    # Continue, not Stop: a failing build writes to stderr, which Stop would turn into a throw HERE
    $ErrorActionPreference = 'Continue'
    $o = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build @BuildArgs 2>&1 | ForEach-Object { "$_" })
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($o -join ' | ') }
}

'=== build'
$b = Invoke-Build @('-OutDir', $out, '-Stamp', 'test 2026-10-09')
$zip = Join-Path $out 'NT8Guard-AddOns.zip'
Check 'the zip is built' ($b.Exit -eq 0 -and (Test-Path $zip)) $b.Text
Check 'it reports the feed guard version and stamp' ($b.Text -match 'feed guard \d+\.\d+\.\d+ \(build test 2026-10-09\)') $b.Text

$z = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($z.Entries | ForEach-Object { $_.FullName })
    Check 'exactly Info.xml + the three add-on files, at the root' (($names -join '|') -eq 'Info.xml|AddOns/AutoReconnect.cs|AddOns/NT8BridgeServer.cs|AddOns/NT8BridgeServerPlayback.cs') ($names -join ', ')
    Check 'no backslash in any entry name' (@($names | Where-Object { $_.Contains([char]92) }).Count -eq 0) ($names -join ', ')
    function Read-Entry([string]$n) {
        $s = ($z.Entries | Where-Object { $_.FullName -eq $n }).Open()
        try { $ms = New-Object IO.MemoryStream; $s.CopyTo($ms); return , $ms.ToArray() } finally { $s.Dispose() }
    }
    $info = [xml][Text.Encoding]::UTF8.GetString((Read-Entry 'Info.xml'))
    Check 'Info.xml is the import manifest NinjaTrader expects' ($info.NinjaTrader.Export.Version -and $info.NinjaTrader.Export.Agile -eq 'None') $info.OuterXml
    foreach ($f in 'NT8BridgeServer.cs', 'NT8BridgeServerPlayback.cs') {
        $same = [Convert]::ToBase64String((Read-Entry "AddOns/$f")) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $addon $f)))
        Check "$f is the source, byte for byte" $same 'differs'
    }
    $ar = [Text.Encoding]::UTF8.GetString((Read-Entry 'AddOns/AutoReconnect.cs'))
    Check 'the feed guard copy is stamped' ($ar.Contains('private const string BuildStamp = "test 2026-10-09";')) 'not stamped'
    $src = [IO.File]::ReadAllText((Join-Path $addon 'AutoReconnect.cs'))
    Check '...and otherwise identical to the source' ($ar -ceq ($src -replace 'private const string BuildStamp = "[^"]*";', 'private const string BuildStamp = "test 2026-10-09";')) 'differs'
    Check 'the source in the repo still says "dev"' ($src.Contains('private const string BuildStamp = "dev";')) 'repo source was stamped'
    Check 'no generated-code region in any entry' (-not ($ar + [Text.Encoding]::UTF8.GetString((Read-Entry 'AddOns/NT8BridgeServer.cs'))).Contains('NinjaScript generated code')) 'region found'
} finally { $z.Dispose() }

'=== a source holding a generated-code region is refused'
$fake = New-TestDir zipsrc
foreach ($f in 'AutoReconnect.cs', 'NT8BridgeServer.cs', 'NT8BridgeServerPlayback.cs') { Copy-Item (Join-Path $addon $f) (Join-Path $fake $f) }
Add-Content -Path (Join-Path $fake 'NT8BridgeServer.cs') -Value "`r`n#region NinjaScript generated code. Neither change nor remove.`r`n#endregion"
$out2 = New-TestDir zipout2
$b = Invoke-Build @('-OutDir', $out2, '-Stamp', 't', '-SourceDir', $fake)
Check 'the build fails' ($b.Exit -ne 0) "exit $($b.Exit)"
Check 'and names the problem' ($b.Text -match 'generated code') $b.Text
Check 'no zip is left' (-not (Test-Path (Join-Path $out2 'NT8Guard-AddOns.zip'))) 'zip written'
'=== the NT8 link is one partial class in two files: a missing half is refused'
Remove-Item (Join-Path $fake 'NT8BridgeServerPlayback.cs')
Copy-Item (Join-Path $addon 'NT8BridgeServer.cs') (Join-Path $fake 'NT8BridgeServer.cs') -Force
$b = Invoke-Build @('-OutDir', $out2, '-Stamp', 't', '-SourceDir', $fake)
Check 'the build fails without NT8BridgeServerPlayback.cs' ($b.Exit -ne 0 -and $b.Text -match 'missing add-on source') $b.Text

Remove-Item $out, $out2, $fake -Recurse -Force -ErrorAction SilentlyContinue
Complete-Test
