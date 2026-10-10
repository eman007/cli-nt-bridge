. (Join-Path $PSScriptRoot 'TestKit.ps1')
$scanner = Join-Path (Split-Path $script:AppDir -Parent) 'build\Find-PersonalStrings.ps1'
# Planted strings are assembled at run time, so this file never trips the real scan itself.
$token = 'ZQXJ' + '-PRIVATE'
$topic = 'zqxj_' + 'topic_77'

function Invoke-Scan([string]$Dir, [string[]]$More = @()) {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scanner -Path $Dir -DenyList $deny @More 2>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Out = $out }
}

$root = New-TestDir scan
$deny = Join-Path $root 'deny.txt'
[IO.File]::WriteAllText($deny, "# test list`nZQXJ-PRIV`n")
$tree = Join-Path $root 'tree'
New-Item -ItemType Directory -Path $tree -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $tree 'clean.ps1'), "'nothing personal here'")

'=== a clean tree passes'
$r = Invoke-Scan $tree
Check 'clean -> exit 0' ($r.Code -eq 0) $r.Out

'=== every file is scanned, whatever its extension (what ships is what counts)'
$bin = Join-Path $tree '__pycache__'
New-Item -ItemType Directory -Path $bin -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $bin 'mod.cpython-311.pyc'), [byte[]](@(0, 13, 10, 0, 227) + [Text.Encoding]::ASCII.GetBytes("x$token y") + @(0, 255)))
$r = Invoke-Scan $tree
Check 'a compiled .pyc with the string -> found' ($r.Code -eq 1 -and $r.Out -match 'mod\.cpython-311\.pyc') $r.Out
Remove-Item $bin -Recurse -Force
[IO.File]::WriteAllText((Join-Path $tree 'LICENSE'), "Copyright $token")
$r = Invoke-Scan $tree
Check 'a file with no extension -> found' ($r.Code -eq 1 -and $r.Out -match 'LICENSE') $r.Out
Remove-Item (Join-Path $tree 'LICENSE') -Force
[IO.File]::WriteAllText((Join-Path $tree 'Install.cmd'), "@echo $token", [Text.Encoding]::Unicode)
$r = Invoke-Scan $tree
Check 'a UTF-16 file -> found' ($r.Code -eq 1 -and $r.Out -match 'Install\.cmd') $r.Out
Remove-Item (Join-Path $tree 'Install.cmd') -Force

'=== the live alert channel is caught but never printed'
$cfgFile = Join-Path $root 'guard.json'
[IO.File]::WriteAllText($cfgFile, "{ ""alerts"": { ""ntfyTopic"": ""$topic"" } }")
[IO.File]::WriteAllText((Join-Path $tree 'notes.md'), "subscribe to https://ntfy.sh/$topic")
$r = Invoke-Scan $tree @('-ConfigFile', $cfgFile)
Check 'topic in a file -> found' ($r.Code -eq 1 -and $r.Out -match '<private alert channel>') $r.Out
Check 'the topic itself is never printed' (-not $r.Out.Contains($topic))

Complete-Test
