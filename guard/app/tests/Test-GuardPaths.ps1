. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules

'=== folders: explicit override, then environment, then Windows'' own lookup'
$env:NT8GUARD_HOME = 'X:\from-env'; $env:NT8_DIR = 'Y:\nt8-env'
Check 'Guard home: override wins' ((Get-GuardHome 'Z:\explicit') -eq 'Z:\explicit')
Check 'Guard home: environment next' ((Get-GuardHome) -eq 'X:\from-env')
Check 'NT8 folder: guard.json value wins' ((Get-Nt8DataDir 'Q:\set') -eq 'Q:\set')
Check "NT8 folder: 'auto' falls through to the environment" ((Get-Nt8DataDir 'auto') -eq 'Y:\nt8-env')
Remove-Item Env:\NT8GUARD_HOME; Remove-Item Env:\NT8_DIR
Check 'Guard home default: LocalAppData\NT8Guard' ((Get-GuardHome) -eq (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NT8Guard'))
Check 'NT8 folder default: Documents (as Windows reports it)\NinjaTrader 8' ((Get-Nt8DataDir) -eq (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NinjaTrader 8'))
Check 'NT8 program folder override' ((Get-Nt8ProgramDir 'P:\NT') -eq 'P:\NT')
Check 'NinjaTrader.exe under bin' ((Get-Nt8Exe 'P:\NT') -eq 'P:\NT\bin\NinjaTrader.exe')

'=== Guard folders are created on first use'
$h = Use-TestHomes paths
$s = Get-GuardPath state
Check 'state folder created inside the Guard home' ((Test-Path $s) -and $s -eq (Join-Path $h.Home 'state'))
Check 'backups under state\backups' ((Get-GuardPath backups) -eq (Join-Path $h.Home 'state\backups'))
Check 'tools folder is <root>\python next to app' ((Get-GuardToolsDir) -eq (Join-Path (Split-Path $script:AppDir -Parent) 'python'))
Check "feed guard events live in NT8's folder" ((Get-FeedGuardEventsPath) -eq (Join-Path $h.Nt8 'NT8Guard\feedguard-events.jsonl'))

'=== stamps parse the same in any culture'
$stamp = '2026-10-09T07:14:13.1234567Z'
$was = [Threading.Thread]::CurrentThread.CurrentCulture
foreach ($c in 'en-US', 'de-DE', 'ar-SA', 'th-TH') {
    [Threading.Thread]::CurrentThread.CurrentCulture = $c
    $t = ConvertFrom-GuardUtc $stamp
    Check "parsed under $c" ($t -and $t.Kind -eq 'Utc' -and $t.Year -eq 2026 -and $t.Hour -eq 7) "$t"
}
[Threading.Thread]::CurrentThread.CurrentCulture = $was
Check 'garbage -> $null' ($null -eq (ConvertFrom-GuardUtc 'not a time'))
Check 'empty -> $null' ($null -eq (ConvertFrom-GuardUtc ''))

'=== logs rotate'
$f = Join-Path (Get-GuardPath logs) 'x.log'
[IO.File]::WriteAllText($f, ('a' * 64))
Step-GuardFileRotation -Path $f -MaxBytes 10
Check 'over the limit -> moved to .1' ((Test-Path "$f.1") -and -not (Test-Path $f))
Write-GuardLog -Component x -Message 'hello'
Check 'logging continues in a fresh file' ((Read-AllText $f) -match 'hello')

Complete-Test
