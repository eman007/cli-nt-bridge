# The release build's pieces (spec 8), without the network or NinjaTrader:
#   the bundled Python - downloaded once, refused unless it matches its pinned SHA-256 and python.exe is
#   signed by the Python Software Foundation; the backup tools copied in without tests or caches.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
Import-Module (Join-Path (Split-Path -Parent $script:AppDir) 'build\Guard.Release.psm1') -Force -DisableNameChecking
$tools = Join-Path (Split-Path -Parent $script:AppDir) 'python'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function New-FakeRuntime([string]$Path) {
    # A stand-in for python-X.Y.Z-embed-amd64.zip: python.exe and its ._pth.
    $src = New-TestDir fakepy
    [IO.File]::WriteAllText((Join-Path $src 'python.exe'), 'not really python')
    [IO.File]::WriteAllText((Join-Path $src 'python314._pth'), "python314.zip`r`n.`r`n")
    if (Test-Path $Path) { Remove-Item $Path -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($src, $Path)
    return (Get-FileHash $Path -Algorithm SHA256).Hash
}
$signed = { param($exe) [pscustomobject]@{ Status = 'Valid'; Signer = 'CN=Python Software Foundation, O=Python Software Foundation' } }

'--- the bundled Python: downloaded once, pinned'
$origin = New-TestDir origin
$fake = Join-Path $origin 'python-9.9.9-embed-amd64.zip'
$pin = New-FakeRuntime $fake
$url = ([Uri]$fake).AbsoluteUri                      # file:///... - no network in a test
$cache = New-TestDir cache
$z = Get-GuardPythonRuntime -CacheDir $cache -Version '9.9.9' -Sha256 $pin -Url $url
Check 'downloaded into the cache, matching its pin' ((Test-Path $z) -and (Get-FileHash $z -Algorithm SHA256).Hash -eq $pin) $z
$z2 = Get-GuardPythonRuntime -CacheDir $cache -Version '9.9.9' -Sha256 $pin -Url ([Uri](Join-Path $origin 'gone.zip')).AbsoluteUri
Check 'a cached copy that matches is used without downloading' ($z2 -eq $z) $z2
$cache2 = New-TestDir cache2
$err = $null
try { Get-GuardPythonRuntime -CacheDir $cache2 -Version '9.9.9' -Sha256 ('0' * 64) -Url $url | Out-Null } catch { $err = $_.Exception.Message }
Check 'a download that does not match its pin is refused' ($err -match 'SHA-256') $err
Check 'and is not left in the cache' (-not @(Get-ChildItem $cache2 -File).Count) (@(Get-ChildItem $cache2).Name -join ',')
[IO.File]::WriteAllText((Join-Path $cache 'python-9.9.9-embed-amd64.zip'), 'tampered')
$z3 = Get-GuardPythonRuntime -CacheDir $cache -Version '9.9.9' -Sha256 $pin -Url $url
Check 'a tampered cached copy is replaced by a fresh download' ((Get-FileHash $z3 -Algorithm SHA256).Hash -eq $pin) ''

'--- unpacked with the backup tools'
$dest = Join-Path (New-TestDir stage) 'python'
Expand-GuardPython -Zip $z -Dest $dest -ToolsSource $tools -Signature $signed | Out-Null
Check 'python.exe and its ._pth' ((Test-Path (Join-Path $dest 'python.exe')) -and (Test-Path (Join-Path $dest 'python314._pth'))) ''
Check 'the three backup tools and their package' ((Test-Path (Join-Path $dest 'backup_export.py')) -and (Test-Path (Join-Path $dest 'backup_restore.py')) -and
                                                  (Test-Path (Join-Path $dest 'chart_relink.py')) -and (Test-Path (Join-Path $dest 'nt8guard_backup\__init__.py'))) ''
Check 'the QR code library' (Test-Path (Join-Path $dest 'vendor\segno\__init__.py')) ''
$junk = @(Get-ChildItem $dest -Recurse -Force | Where-Object { $_.Name -in 'tests', '__pycache__', 'README.md' -or $_.Extension -eq '.pyc' })
Check 'no tests, caches or notes' (-not $junk.Count) ($junk.FullName -join ', ')
$dest2 = Join-Path (New-TestDir stage2) 'python'
$err = $null
try { Expand-GuardPython -Zip $z -Dest $dest2 -ToolsSource $tools -Signature { param($exe) [pscustomobject]@{ Status = 'NotSigned'; Signer = '' } } | Out-Null } catch { $err = $_.Exception.Message }
Check 'an unsigned python.exe is refused' ($err -match 'sign') $err
Check 'and nothing is left behind' (-not (Test-Path $dest2)) ''
$err = $null
try { Expand-GuardPython -Zip $z -Dest $dest2 -ToolsSource $tools -Signature { param($exe) [pscustomobject]@{ Status = 'Valid'; Signer = 'CN=Somebody Else' } } | Out-Null } catch { $err = $_.Exception.Message }
Check 'signed by anyone else: refused' ($err -match 'Python Software Foundation') $err

'--- the guide''s screenshots, from the real panel against a generic demo'
$capture = Join-Path (Split-Path -Parent $script:AppDir) 'build\Capture-GuideShots.ps1'
$shots = New-TestDir shots
$out = @(& powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File $capture -OutDir $shots 2>&1 | ForEach-Object { "$_" })
$code = $LASTEXITCODE
$names = @(Get-ChildItem $shots -Filter *.png | ForEach-Object BaseName | Sort-Object)
$want = @('Alerts', 'Backups', 'Protection', 'Status', 'Tools', 'Watch') + @(1..7 | ForEach-Object { "Wizard-$_" }) | Sort-Object
Check 'every tab and every setup step' ($code -eq 0 -and ($names -join ',') -eq ($want -join ',')) "exit $code; $($names -join ','); $($out -join ' | ')"
$cards = @($out | Where-Object { $_ -match '^card ' })
Check 'the Status shot is all green' ($cards.Count -ge 8 -and -not @($cards | Where-Object { $_ -notmatch ' ok$' }).Count) ($cards -join '; ')
# Positive control: a "user name" the panel does show (the demo's account) must stop the capture.
$leakDir = New-TestDir leak
$saved = $env:USERNAME; $env:USERNAME = 'Sim101'
$out = @(& powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File $capture -OutDir $leakDir 2>&1 | ForEach-Object { "$_" })
$code = $LASTEXITCODE; $env:USERNAME = $saved
Check 'a shot showing this PC''s user name is refused, and no shot is kept' ($code -ne 0 -and ($out -match '^LEAK').Count -and -not @(Get-ChildItem $leakDir -Filter *.png).Count) "exit $code"

'--- the setup guide and the Quick Start'
$guideDir = Join-Path (Split-Path -Parent $script:AppDir) 'docs\guide'
$guide = Read-AllText (Join-Path $guideDir 'NT8 Guard - Setup Guide.html')
$quick = Read-AllText (Join-Path $guideDir 'NT8 Guard - Quick Start.html')
$imgs = @([regex]::Matches($guide, '<img src="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$missing = @($imgs | Where-Object { -not (Test-Path (Join-Path $guideDir $_)) })
Check 'every picture in the guide is there' ($imgs.Count -ge 13 -and -not $missing.Count) "$($imgs.Count) pictures; missing: $($missing -join ', ')"
$tabs = @('Status', 'Protection', 'Alerts', 'Strategy backups', 'Watch list', 'Tools') | Where-Object { $guide -notmatch "<h3>$_</h3>" }
Check 'every tab of the panel has its section' (-not $tabs.Count) ($tabs -join ', ')
$ver = (Read-AllText (Join-Path $script:AppDir 'VERSION')).Trim()
Check "both name this version ($ver)" ($guide.Contains("v$ver") -and $quick.Contains("v$ver") -and $guide.Contains("NT8Guard-v$ver.zip")) ''
$leak = @(@("\b$([regex]::Escape($env:USERNAME))\b", [regex]::Escape((Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $script:AppDir))))) | Where-Object { $guide -match $_ -or $quick -match $_ })
Check 'no user name or repository path in either' (-not $leak.Count) ($leak -join ', ')
$render = New-TestDir render
$out = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path (Split-Path -Parent $script:AppDir) 'build\Render-Guide.ps1') -OutDir $render 2>&1 | ForEach-Object { "$_" })
$pdf = Join-Path $render 'NT8 Guard - Setup Guide.pdf'
Check 'renders to a PDF with its fonts, and a 1920x1080 Quick Start' ($LASTEXITCODE -eq 0 -and (Test-Path $pdf) -and (Get-Item $pdf).Length -gt 100KB -and
                                                                      (Test-Path (Join-Path $render 'NT8 Guard - Quick Start.png'))) ($out -join ' | ')

'--- built from a commit, never from someone''s half-done edits (final review I5)'
$repo = New-TestDir gitrepo
& git -C $repo init -q 2>$null; & git -C $repo config user.email t@example.invalid; & git -C $repo config user.name test
New-Item -ItemType Directory (Join-Path $repo 'guard\app') -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $repo 'guard\app\a.ps1'), 'one')
& git -C $repo add -A 2>$null; & git -C $repo commit -q -m first 2>$null
$head = (& git -C $repo rev-parse HEAD).Trim()
$b = Get-GuardBuildSource -Repo $repo -Paths @('guard', 'addon')
Check 'a clean checkout: the commit, nothing dirty' ($b.Commit -eq $head -and -not @($b.Dirty).Count) "$($b.Commit) / $(@($b.Dirty) -join ',')"
[IO.File]::WriteAllText((Join-Path $repo 'guard\app\a.ps1'), 'two'); [IO.File]::WriteAllText((Join-Path $repo 'guard\app\new.ps1'), 'x')
[IO.File]::WriteAllText((Join-Path $repo 'elsewhere.txt'), 'not ours')
$b = Get-GuardBuildSource -Repo $repo -Paths @('guard', 'addon')
Check 'an edited or new file in what ships is reported; one elsewhere is not' ((@($b.Dirty) -join ',') -match 'a\.ps1' -and (@($b.Dirty) -join ',') -match 'new\.ps1' -and (@($b.Dirty) -join ',') -notmatch 'elsewhere') (@($b.Dirty) -join ',')

'--- the staged release, its scan copy and its zip'
# A fake Guard source tree (not a git checkout: the filesystem walk), with the junk a working tree holds.
$gr = New-TestDir guardroot
foreach ($f in 'app\Guard-Panel.ps1', 'app\Install-Guard.ps1', 'app\Guard-Run.vbs', 'app\VERSION', 'app\lib\Guard.Paths.psm1', 'app\tests\Test-X.ps1', 'app\.omc\state\x.json',
               'app\lib\__pycache__\x.pyc', 'python\backup_export.py', 'python\tests\test_x.py', 'python\.omc\state\session-started.json',
               'python\.pytest_cache\v\x', 'Install.cmd', 'LICENSE', 'THIRD-PARTY-NOTICES.txt') {
    $p = Join-Path $gr $f; New-Item -ItemType Directory (Split-Path $p) -Force | Out-Null; [IO.File]::WriteAllText($p, "file $f")
}
$addons = Join-Path (New-TestDir addons) 'NT8Guard-AddOns.zip'
$za = [IO.Compression.ZipFile]::Open($addons, 'Create')
$en = $za.CreateEntry('AddOns/AutoReconnect.cs'); $w = New-Object IO.StreamWriter($en.Open()); $w.Write('// feed guard'); $w.Dispose(); $za.Dispose()
$guideOut = New-TestDir guideout
foreach ($f in 'NT8 Guard - Setup Guide.pdf', 'NT8 Guard - Quick Start.png') { [IO.File]::WriteAllText((Join-Path $guideOut $f), 'x') }
$stage = Join-Path (New-TestDir stage) 'NT8Guard'
$m = New-GuardStage -GuardRoot $gr -Dest $stage -PythonZip $z -AddOnsZip $addons -GuideDir $guideOut -Signature $signed -Commit 'abc1234def'
Check 'the release says which commit it was built from' ((Read-AllText (Join-Path $stage 'README.txt')) -match 'abc1234def') ''
$top = @(Get-ChildItem $stage | ForEach-Object Name | Sort-Object)
$wantTop = @('app', 'Install.cmd', 'LICENSE', 'NT8 Guard - Quick Start.png', 'NT8 Guard - Setup Guide.pdf', 'NT8Guard-AddOns.zip', 'python', 'README.txt', 'THIRD-PARTY-NOTICES.txt') | Sort-Object
Check 'the release layout, nothing else' (($top -join '|') -eq ($wantTop -join '|')) ($top -join ', ')
$junk = @(Get-ChildItem $stage -Recurse -Force | Where-Object { $_.Name -in 'tests', '__pycache__' -or $_.Name.StartsWith('.') -or $_.Extension -eq '.pyc' })
Check 'no tests, hook state or caches - in app\ or python\ (final review C1)' (-not $junk.Count) ($junk.FullName -join ', ')
Check 'the program, the runtime and the tools' ((Test-Path (Join-Path $stage 'app\Guard-Panel.ps1')) -and (Test-Path (Join-Path $stage 'python\python.exe')) -and (Test-Path (Join-Path $stage 'python\backup_export.py'))) ''
Check 'it knows which files are the pinned runtime' ((@($m.RuntimeFiles) -contains 'python\python.exe') -and (@($m.RuntimeFiles) -notcontains 'python\backup_export.py')) (@($m.RuntimeFiles) -join ',')
$scan = Join-Path (New-TestDir scan) 'tree'
New-GuardScanTree -Stage $stage -RuntimeFiles $m.RuntimeFiles -Dest $scan | Out-Null
Check 'the scan copy leaves out the pinned runtime but keeps Guard''s tools' (-not (Test-Path (Join-Path $scan 'python\python.exe')) -and (Test-Path (Join-Path $scan 'python\backup_export.py'))) ''
Check 'and opens the add-ons zip so its sources are read' ((Test-Path (Join-Path $scan 'NT8Guard-AddOns\AddOns\AutoReconnect.cs')) -and -not (Test-Path (Join-Path $scan 'NT8Guard-AddOns.zip'))) ''
$zip = Join-Path (New-TestDir rel) 'NT8Guard-v9.9.9.zip'
Write-GuardReleaseZip -Stage $stage -ZipPath $zip | Out-Null
$zr = [IO.Compression.ZipFile]::OpenRead($zip); $names = @($zr.Entries | ForEach-Object FullName); $zr.Dispose()
Check 'zip entries use forward slashes, one per staged file' (-not @($names | Where-Object { $_.Contains('\') }).Count -and $names.Count -eq @(Get-ChildItem $stage -Recurse -File -Force).Count) "$($names.Count) entries"
Check 'and it passes its own check' ((Test-GuardReleaseZip -ZipPath $zip).Ok) ((Test-GuardReleaseZip -ZipPath $zip).Problems -join '; ')
$bad = Join-Path (New-TestDir badzip) 'bad.zip'
$zb = [IO.Compression.ZipFile]::Open($bad, 'Create'); foreach ($n in 'app\Guard-Panel.ps1', 'python/python.exe', 'python/.omc/state/session.json') { $e = $zb.CreateEntry($n); $s = $e.Open(); $s.WriteByte(65); $s.Dispose() }; $zb.Dispose()
$t = Test-GuardReleaseZip -ZipPath $bad
Check 'a zip with a backslash entry or no Install.cmd is refused, saying which' (-not $t.Ok -and ($t.Problems -join ' ') -match 'backslash' -and ($t.Problems -join ' ') -match 'Install\.cmd') ($t.Problems -join '; ')
Check 'and one carrying a dot-folder (tool or session state) is refused (final review C1)' (($t.Problems -join ' ') -match 'python/\.omc') ($t.Problems -join '; ')
$inst = New-TestDir installed
[IO.File]::WriteAllText((Join-Path $inst 'ok.ps1'), 'nothing to see')
Check 'an installed tree that names nothing of the build machine passes' (-not @(Test-GuardInstalledTree -Root $inst -Forbidden @('C:\Fake\Repo')).Count) ''
[IO.File]::WriteAllText((Join-Path $inst 'leak.ps1'), '$x = "C:\Fake\Repo\guard\app"')
$p = @(Test-GuardInstalledTree -Root $inst -Forbidden @('C:\Fake\Repo'))
Check 'one that names the repository is reported' ($p.Count -eq 1 -and $p[0] -match 'leak\.ps1') ($p -join '; ')
Remove-Item (Join-Path $inst 'leak.ps1')
[IO.File]::WriteAllText((Join-Path $inst 'session.json'), '{ "cwd": "C:\\Fake\\Repo\\guard\\python" }')
$p = @(Test-GuardInstalledTree -Root $inst -Forbidden @('C:\Fake\Repo'))
Check 'and so is one that names it JSON-escaped (final review C1)' ($p.Count -eq 1 -and $p[0] -match 'session\.json') ($p -join '; ')

'--- the fresh-install gate''s clean machine (deferred minors)'
$w = New-TestDir cleanenv
$before = @{ Path = $env:PATH; Nt8 = $env:NT8_DIR; Home = $env:NT8GUARD_HOME }
$inside = Invoke-GuardCleanEnv -Work $w -GuardHome (Join-Path $w 'home') -Script { [pscustomobject]@{ Path = $env:PATH; Nt8 = $env:NT8_DIR; Home = $env:NT8GUARD_HOME } }
Check 'no Python and no repository on PATH' ($inside.Path -notmatch 'python|\.venv|Coding') $inside.Path
Check 'its own empty NinjaTrader folder, not this PC''s' ($inside.Nt8 -like "$w*" -and (Test-Path $inside.Nt8) -and -not @(Get-ChildItem $inside.Nt8 -Force).Count) $inside.Nt8
Check 'and everything put back afterwards' ($env:PATH -eq $before.Path -and $env:NT8_DIR -eq $before.Nt8 -and $env:NT8GUARD_HOME -eq $before.Home) ''
$q = Join-Path (New-TestDir quote) "O'Brien home"
New-Item -ItemType Directory (Join-Path $q 'app\lib') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $q 'app\lib\Guard.Setup.psm1'), "function Get-GuardSetupCheck { param(`$Nt8Version, `$AutoLogon, `$ScalePercent, `$OtherWatchdogs) [pscustomobject]@{ Key = 'python'; State = 'ok'; Text = 'Found (bundled).' } }`r`nExport-ModuleMember -Function Get-GuardSetupCheck")
$row = Get-GuardInstalledPythonRow -GuardHome $q
Check 'a Guard folder whose path holds an apostrophe still reads the wizard''s Python row' ($row -eq 'ok Found (bundled).') $row

'--- every zip in the release folder keeps its line in SHA256SUMS.txt (deferred minor)'
$rd = New-TestDir sums
foreach ($n in 'NT8Guard-v9.9.9.zip', 'NT8Guard-v9.9.9-untested.zip') { [IO.File]::WriteAllText((Join-Path $rd $n), $n) }
Write-GuardSums -Dir $rd | Out-Null
$lines = @(Get-Content (Join-Path $rd 'SHA256SUMS.txt'))
$want = @('NT8Guard-v9.9.9.zip', 'NT8Guard-v9.9.9-untested.zip' | ForEach-Object { "$((Get-FileHash (Join-Path $rd $_) -Algorithm SHA256).Hash.ToLowerInvariant())  $_" })
Check 'one line per zip, both kept' ($lines.Count -eq 2 -and -not @($want | Where-Object { $lines -notcontains $_ }).Count) ($lines -join ' | ')

'--- the committed guide PDF and Quick Start match their sources (deferred minor)'
$stampFile = Join-Path $guideDir 'rendered-from.txt'
$now = Get-GuardGuideStamp -SourceDir $guideDir
Check 'rendered from exactly the committed HTML and screenshots' ((Test-Path $stampFile) -and (Read-AllText $stampFile).Trim() -eq $now) "stamp $(Read-AllText $stampFile) vs sources $now"
Check 'the guide says what to do when a PC''s policy blocks scripts' ($guide -match 'execution policy') ''
# Cold test 2026-10-10: a newer NinjaTrader connects "Simulation" (real market data, market hours) and does not
# connect the Simulated Data Feed until you add it.
$arrow = [string][char]0x2192    # the guide's arrow; this file stays ASCII (PowerShell 5.1 reads it as ANSI)
Check 'the weekend drill says how to add the Simulated Data Feed and leave "Simulation"' ($guide.Contains("Connections $arrow Configure") -and $guide -match 'Disconnect <strong>Simulation</strong>') ''
# A clone with core.autocrlf checks the HTML out with CRLF: the same sources, so the same stamp.
$crlf = Join-Path (New-TestDir crlf) 'guide'; Copy-Item $guideDir $crlf -Recurse
foreach ($h in Get-ChildItem $crlf -Filter *.html) { $t = [IO.File]::ReadAllText($h.FullName).Replace("`r`n", "`n").Replace("`n", "`r`n"); [IO.File]::WriteAllText($h.FullName, $t, (New-Object Text.UTF8Encoding($false))) }
Check 'line endings do not make the committed guide look stale' ((Get-GuardGuideStamp -SourceDir $crlf) -eq $now) ''

Complete-Test
