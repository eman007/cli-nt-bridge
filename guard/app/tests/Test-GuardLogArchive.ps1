. (Join-Path $PSScriptRoot 'TestKit.ps1')
$h = Use-TestHomes logarchive
$script = Join-Path $script:AppDir 'Guard-LogArchive.ps1'
foreach ($d in 'log', 'trace') { New-Item -ItemType Directory -Path (Join-Path $h.Nt8 $d) -Force | Out-Null }
$old   = Join-Path $h.Nt8 'log\log.20261001.00000.txt'
$fresh = Join-Path $h.Nt8 'log\log.20261009.00000.txt'
$held  = Join-Path $h.Nt8 'trace\trace.20261001.00000.txt'
[IO.File]::WriteAllText($old, "2026-10-01 first line`nmore")
[IO.File]::WriteAllText($fresh, "2026-10-09 first line")
[IO.File]::WriteAllText($held, "2026-10-01 trace")
foreach ($f in $old, $held) { (Get-Item $f).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-5) }
$arch = Join-Path $h.Home 'logs\nt8'

'=== archives every log, keyed per session, and prunes only verified old live files'
$lock = [IO.File]::Open($held, 'Open', 'Read', 'Read')     # held open WITHOUT delete sharing: archivable, not deletable
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script | Out-Null
$lock.Close()
$copies = @(Get-ChildItem $arch -File)
Check 'three archives' ($copies.Count -eq 3) ($copies.Name -join ', ')
Check 'named <file>__<created>_<hash>' ([bool]($copies.Name -match '^log\.20261001\.00000__\d{8}-\d{6}_[0-9a-f]{8}\.txt$'))
Check 'old archived live log pruned' (-not (Test-Path $old))
Check 'fresh live log kept' (Test-Path $fresh)
Check 'a file held open is archived but not deleted' (Test-Path $held)
Check 'heartbeat written' (Test-Path (Join-Path $h.Home 'state\logarchive.heartbeat'))

'=== a new session under the same name gets its OWN archive'
[IO.File]::WriteAllText($fresh, "2026-10-09 SECOND session first line")
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script | Out-Null
Check 'four archives now' (@(Get-ChildItem $arch -File).Count -eq 4)

'=== retention by the archive''s own age'
$a = Get-ChildItem $arch -File | Select-Object -First 1
$a.LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-8)
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script | Out-Null
Check 'an 8-day-old archive is purged (retention 7)' (-not (Test-Path $a.FullName))

'=== the old NT8LogArchive.disabled switch still pauses it'
New-Item -ItemType File -Path (Join-Path $h.Nt8 'NT8LogArchive.disabled') | Out-Null
Remove-Item (Join-Path $h.Home 'state\logarchive.heartbeat') -Force
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script | Out-Null
Check 'paused: no heartbeat written' (-not (Test-Path (Join-Path $h.Home 'state\logarchive.heartbeat')))

Complete-Test
