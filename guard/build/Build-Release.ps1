# Build-Release.ps1 - NT8Guard-v<version>.zip, the release a person downloads (spec 8):
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File guard\build\Build-Release.ps1 [-DenyList <file>]
#
# It refuses to produce a zip unless, in order:
#   1. the Guard suite (incl. the add-ons' own tests: they compile against NinjaTrader's assemblies) and
#      the Python suite pass;                                      (-SkipSuites names the zip -untested)
#   2. the bundled Python matches its pinned SHA-256 and python.exe is signed by the Python Software Foundation;
#   3. the guide's screenshots come from the real panel, all green, naming nothing of this machine;
#   4. a scan of everything that ships (but the pinned runtime) finds no personal string;
#   5. every zip entry uses '/', and the pieces a person needs are all in it;
#   6. the finished zip installs into a clean temporary profile and works from there.
# Output: guard\build\release\NT8Guard-v<version>.zip and SHA256SUMS.txt beside it.
[CmdletBinding()]
param([string]$DenyList, [string[]]$ConfigFile, [string]$OutDir, [string]$Python, [switch]$SkipSuites)
$ErrorActionPreference = 'Stop'
$build = $PSScriptRoot
$guard = Split-Path -Parent $build
$repo = Split-Path -Parent $guard                     # the NT8Bridge folder
Import-Module (Join-Path $build 'Guard.Release.psm1') -Force -DisableNameChecking

# The pinned runtime (spec 8). python.org publishes no SHA-256 on its release page; this one matches the
# CPython checksum in its signed SBOM, python-3.14.8-embed-amd64.zip.spdx.json.
$PythonVersion = '3.14.8'
$PythonSha256 = 'A93ABE456AB01BD96D7A085B3CDB6566B3063F4241360D114142FBDB07F0A310'

if (-not $DenyList) { $DenyList = Join-Path $repo 'guard-private\denylist.txt' }
if (-not (Test-Path $DenyList)) { throw "No deny list at $DenyList. Pass -DenyList <file>: one private string (regular expression) per line - your name, accounts, machine names. An empty file still runs the built-in checks." }
if (-not $ConfigFile) {
    $ConfigFile = @((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NT8Guard\guard.json'),
                    (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NinjaTrader 8\NT8Overwatch.config.json')) | Where-Object { Test-Path $_ }
}
if (-not $OutDir) { $OutDir = Join-Path $build 'release' }
if (-not $Python) { $Python = Join-Path $repo '.venv\Scripts\python.exe' }
$version = ([IO.File]::ReadAllText((Join-Path $guard 'app\VERSION'))).Trim()
$name = "NT8Guard-v$version" + $(if ($SkipSuites) { '-untested' } else { '' })
$work = Join-Path $build 'stage'
$started = Get-Date
function Step([string]$Text) { Write-Output ("[{0,4:N0}s] {1}" -f ((Get-Date) - $started).TotalSeconds, $Text) }
function Refuse([string]$Text) { Write-Output ''; Write-Output "REFUSED: $Text"; exit 1 }

Step "NT8 Guard $version -> $name.zip"
if (Test-Path $work) { [IO.Directory]::Delete($work, $true) }
New-Item -ItemType Directory -Path $work -Force | Out-Null

# 1. The suites.
if ($SkipSuites) { Step 'suites SKIPPED (-SkipSuites): the zip is named -untested' }
else {
    Step 'Guard suite (incl. the add-ons'' tests)'
    $log = Join-Path $work 'guard-suite.txt'
    & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $guard 'app\tests\Run-GuardTests.ps1') *> $log
    $code = $LASTEXITCODE
    $tail = @(Get-Content $log | Where-Object { $_ -match 'SUITE|^\s+FAIL' } | Select-Object -Last 6)
    $tail | ForEach-Object { "         $_" }
    if ($code -ne 0) { Refuse "the Guard suite failed (full output: $log)" }
    Step 'Python suite'
    if (-not (Test-Path $Python)) { Refuse "no Python with pytest at $Python (pass -Python)" }
    $out = @(& $Python -m pytest -q (Join-Path $guard 'python\tests') 2>&1 | ForEach-Object { "$_" })
    "         $(@($out | Select-Object -Last 1))"
    if ($LASTEXITCODE -ne 0) { Refuse "the Python suite failed: $(@($out | Select-Object -Last 5) -join ' | ')" }
}

# 2. The NinjaTrader add-ons zip and the bundled Python.
Step 'NinjaTrader add-ons zip'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $build 'Build-AddOnsZip.ps1') -OutDir (Join-Path $work 'addons') | ForEach-Object { "         $_" }
$addons = Join-Path $work 'addons\NT8Guard-AddOns.zip'
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $addons)) { Refuse 'the add-ons zip was not built' }
Step "Python $PythonVersion (pinned)"
try { $pyZip = Get-GuardPythonRuntime -CacheDir (Join-Path $build 'cache') -Version $PythonVersion -Sha256 $PythonSha256 } catch { Refuse $_.Exception.Message }

# 3. The guide: screenshots from the real panel, then the PDF and the Quick Start.
Step 'guide screenshots'
$shots = Join-Path $work 'shots'
$out = @(& powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $build 'Capture-GuideShots.ps1') -OutDir $shots 2>&1 | ForEach-Object { "$_" })
if ($LASTEXITCODE -ne 0) { Refuse "the screenshots: $(@($out | Where-Object { $_ -match 'LEAK|rror' }) -join ' | ')" }
$notGreen = @($out | Where-Object { $_ -match '^card ' -and $_ -notmatch ' ok$' })
if ($notGreen.Count) { Refuse "the demo Status is not all green: $($notGreen -join ', ')" }
Step 'guide PDF and Quick Start'
$guide = Join-Path $work 'guide'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $build 'Render-Guide.ps1') -OutDir $guide -ShotsDir $shots | ForEach-Object { "         $_" }
if ($LASTEXITCODE -ne 0) { Refuse 'the guide did not render' }

# 4. Stage, and scan everything that ships.
Step 'staging'
try { $stage = New-GuardStage -GuardRoot $guard -Dest (Join-Path $work $name) -PythonZip $pyZip -AddOnsZip $addons -GuideDir $guide } catch { Refuse $_.Exception.Message }
Step 'personal-strings scan'
$scanDir = New-GuardScanTree -Stage $stage.Dest -RuntimeFiles $stage.RuntimeFiles -Dest (Join-Path $work 'scan')
$scanArgs = @{ Path = $scanDir; DenyList = $DenyList }
if (@($ConfigFile).Count) { $scanArgs.ConfigFile = @($ConfigFile) }
& (Join-Path $build 'Find-PersonalStrings.ps1') @scanArgs | ForEach-Object { "         $_" }
if ($LASTEXITCODE -ne 0) { Refuse 'personal strings found (above)' }

# 5. The zip.
Step 'zip'
$zip = Join-Path $OutDir "$name.zip"
Write-GuardReleaseZip -Stage $stage.Dest -ZipPath $zip | Out-Null
$t = Test-GuardReleaseZip -ZipPath $zip
if (-not $t.Ok) { [IO.File]::Delete($zip); Refuse "the zip: $($t.Problems -join '; ')" }

# 6. Install it the way a person does.
Step 'fresh install from the zip'
$fresh = Invoke-GuardFreshInstall -ZipPath $zip -Forbidden @((Split-Path -Parent $repo), $env:USERPROFILE)
$fresh.Log | Where-Object { $_ -match 'wizard|backup_export|panel' } | ForEach-Object { "         $_" }
if (-not $fresh.Ok) { [IO.File]::Delete($zip); Refuse "the fresh install: $($fresh.Problems -join '; ')" }

$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), "$sha  $name.zip`n")
Step "DONE: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB), sha256 $sha"
exit 0
