# Install.cmd / Install-Guard.ps1 (spec 8): a fresh install, an update that keeps everything you set, and the
# refusals. The installer runs in its own powershell.exe, as it does for a person, against a fake release
# folder, a temp Guard home and a temp Start menu - never the real ones.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes 'install'
$guardHome = $h.Home
$menu = New-TestDir startmenu

# A release folder as the zip unpacks: Install.cmd, app\, python\, the add-ons zip, the guide.
$src = New-TestDir release
Copy-Item -LiteralPath $script:AppDir -Destination (Join-Path $src 'app') -Recurse
Copy-Item (Join-Path (Split-Path -Parent $script:AppDir) 'Install.cmd') $src
New-Item -ItemType Directory (Join-Path $src 'python') | Out-Null
[IO.File]::WriteAllText((Join-Path $src 'python\python.exe'), 'runtime')
[IO.File]::WriteAllText((Join-Path $src 'python\backup_export.py'), 'tool')
foreach ($f in 'NT8Guard-AddOns.zip', 'NT8 Guard - Setup Guide.pdf', 'NT8 Guard - Quick Start.png', 'LICENSE', 'THIRD-PARTY-NOTICES.txt') { [IO.File]::WriteAllText((Join-Path $src $f), $f) }
[IO.File]::WriteAllText((Join-Path $src 'app\VERSION'), '0.9.0')
# downloaded from the internet: Windows marks it (Mark-of-the-Web)
Set-Content -LiteralPath (Join-Path $src 'app\Guard-Panel.ps1') -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"

function Install([string[]]$More = @()) {
    $ErrorActionPreference = 'Continue'      # the installer's own error output is part of what we read, not a test crash
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'app\Install-Guard.ps1') -Source $src -GuardHome $guardHome -StartMenuDir $menu -NoLaunch @More 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = (@($out | ForEach-Object { "$_" }) -join "`n") }
}
function Marked([string]$Path) { [bool](Get-Item -LiteralPath $Path -Stream * -ErrorAction SilentlyContinue | Where-Object Stream -eq 'Zone.Identifier') }

'--- a fresh install'
Check 'the downloaded file is marked before the install' (Marked (Join-Path $src 'app\Guard-Panel.ps1')) ''
$r = Install
Check 'succeeds' ($r.Code -eq 0) $r.Out
Check 'the program, the runtime, the add-ons zip and the guide are in Guard''s folder' (
    (Test-Path (Join-Path $guardHome 'app\Guard-Panel.ps1')) -and (Test-Path (Join-Path $guardHome 'python\python.exe')) -and
    (Test-Path (Join-Path $guardHome 'NT8Guard-AddOns.zip')) -and (Test-Path (Join-Path $guardHome 'NT8 Guard - Setup Guide.pdf')) -and
    (Test-Path (Join-Path $guardHome 'NT8 Guard - Quick Start.png')) -and (Test-Path (Join-Path $guardHome 'THIRD-PARTY-NOTICES.txt'))) $r.Out
Check 'every file unblocked' (-not (Marked (Join-Path $src 'app\Guard-Panel.ps1')) -and -not (Marked (Join-Path $guardHome 'app\Guard-Panel.ps1'))) ''
$lnkPath = Join-Path $menu 'NT8 Guard.lnk'
Check 'a Start-menu shortcut' (Test-Path $lnkPath) (@(Get-ChildItem $menu).Name -join ',')
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($lnkPath)
Check 'it opens the panel without a console window' ($lnk.TargetPath -match 'wscript\.exe$' -and $lnk.Arguments -match [regex]::Escape((Join-Path $guardHome 'app\Guard-Run.vbs')) -and $lnk.Arguments -match 'Guard-Panel\.ps1') "$($lnk.TargetPath) $($lnk.Arguments)"
Check 'from Guard''s app folder' ($lnk.WorkingDirectory -eq (Join-Path $guardHome 'app')) $lnk.WorkingDirectory
Check 'and says what it did' ($r.Out -match '0\.9\.0' -and $r.Out -match [regex]::Escape($guardHome)) $r.Out
$ver = & powershell.exe -NoProfile -Command "Import-Module '$(Join-Path $guardHome 'app\lib\Guard.Paths.psm1')'; Get-GuardVersion"
Check 'the installed program knows its version' ("$ver".Trim() -eq '0.9.0') "$ver"

'--- an update keeps everything you set'
[IO.File]::WriteAllText((Join-Path $guardHome 'guard.json'), '{"python":{"exe":"C:\\mine\\python.exe"},"mine":true}')
foreach ($p in 'secrets\nt8.cred', 'state\pause.json', 'logs\relaunch.log', 'events\panel.jsonl') {
    $f = Join-Path $guardHome $p; New-Item -ItemType Directory (Split-Path $f) -Force | Out-Null; [IO.File]::WriteAllText($f, "kept $p")
}
[IO.File]::WriteAllText((Join-Path $guardHome 'app\gone-in-new-release.ps1'), 'old')
[IO.File]::WriteAllText((Join-Path $src 'app\VERSION'), '0.9.1')
New-Item -ItemType Directory (Join-Path $guardHome 'app.new') -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $guardHome 'app.new\half.ps1'), 'x')
New-Item -ItemType Directory (Join-Path $guardHome 'python.old') -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $guardHome 'python.old\x'), 'x')
$r = Install
Check 'succeeds' ($r.Code -eq 0) $r.Out
Check 'your settings untouched (your own Python too)' ((Read-AllText (Join-Path $guardHome 'guard.json')) -match 'mine') ''
Check 'your password, pause, logs and events untouched' (@('secrets\nt8.cred', 'state\pause.json', 'logs\relaunch.log', 'events\panel.jsonl' | Where-Object { (Read-AllText (Join-Path $guardHome $_)) -eq "kept $_" }).Count -eq 4) ''
Check 'the program replaced whole: the new version, and a file the release dropped is gone' (
    (Read-AllText (Join-Path $guardHome 'app\VERSION')) -eq '0.9.1' -and -not (Test-Path (Join-Path $guardHome 'app\gone-in-new-release.ps1'))) ''
Check 'no half-done copy left from an interrupted run' (-not (Test-Path (Join-Path $guardHome 'app.new')) -and -not (Test-Path (Join-Path $guardHome 'python.old')) -and
                                                        -not (Test-Path (Join-Path $guardHome 'app.old')) -and -not (Test-Path (Join-Path $guardHome 'python.new'))) (@(Get-ChildItem $guardHome).Name -join ',')

'--- refused while it would get in the way'
[IO.File]::WriteAllText((Join-Path $src 'app\VERSION'), '0.9.2')
$panel = New-Object Threading.Mutex($true, 'Local\NT8GuardPanel')
$r = Install
$panel.ReleaseMutex(); $panel.Dispose()
Check 'the control panel is open: refused, says to close it' ($r.Code -ne 0 -and $r.Out -match 'Close the NT8 Guard window') $r.Out
Check 'and nothing changed' ((Read-AllText (Join-Path $guardHome 'app\VERSION')) -eq '0.9.1') ''
$lock = Open-GuardStartLock $guardHome
$r = Install
$lock.Dispose()
Check 'NinjaTrader being started: refused' ($r.Code -ne 0 -and $r.Out -match 'being started') $r.Out
Check 'and nothing changed' ((Read-AllText (Join-Path $guardHome 'app\VERSION')) -eq '0.9.1') ''

'--- never left half-done (final review I2)'
# A run that died between its two renames left app.old and no app: the next run puts it back first,
# even when it then refuses.
[IO.Directory]::Move((Join-Path $guardHome 'app'), (Join-Path $guardHome 'app.old'))
$panel = New-Object Threading.Mutex($true, 'Local\NT8GuardPanel')
$r = Install
$panel.ReleaseMutex(); $panel.Dispose()
Check 'a previous version found as app.old is put back, even by a refused run' ($r.Code -ne 0 -and (Read-AllText (Join-Path $guardHome 'app\VERSION')) -eq '0.9.1' -and -not (Test-Path (Join-Path $guardHome 'app.old'))) $r.Out
# The second rename fails (an antivirus or another handle on the fresh copy): the previous version goes back.
$env:NT8GUARD_INSTALL_FAULT = 'app'
$r = Install
Remove-Item Env:\NT8GUARD_INSTALL_FAULT
Check 'the new copy cannot be swapped in: the previous version is back, and the message says so' (
    $r.Code -ne 0 -and (Read-AllText (Join-Path $guardHome 'app\VERSION')) -eq '0.9.1' -and $r.Out -match 'previous version' -and
    -not (Test-Path (Join-Path $guardHome 'app.new')) -and -not (Test-Path (Join-Path $guardHome 'app.old'))) $r.Out
# The old guide is open in a PDF reader: the program still updates; that one file is reported.
$pdf = [IO.File]::Open((Join-Path $guardHome 'NT8 Guard - Setup Guide.pdf'), 'Open', 'Read', 'None')
$r = Install
$pdf.Dispose()
Check 'a guide held open does not stop the update, and is named' ($r.Code -eq 0 -and (Read-AllText (Join-Path $guardHome 'app\VERSION')) -eq '0.9.2' -and $r.Out -match 'Setup Guide\.pdf') $r.Out

'--- unblocks only what it ships (final review I3)'
# Extracted with "Extract here" into Downloads: other downloads sit beside Install.cmd and must keep their mark.
$other = Join-Path $src 'someone-elses-download.exe'; [IO.File]::WriteAllText($other, 'x')
Set-Content -LiteralPath $other -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
$otherDir = Join-Path $src 'Other stuff'; New-Item -ItemType Directory $otherDir | Out-Null
$other2 = Join-Path $otherDir 'report.docx'; [IO.File]::WriteAllText($other2, 'x')
Set-Content -LiteralPath $other2 -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
Set-Content -LiteralPath (Join-Path $src 'python\backup_export.py') -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
$r = Install
Check 'its own files are unblocked' ($r.Code -eq 0 -and -not (Marked (Join-Path $src 'python\backup_export.py'))) $r.Out
Check 'files that are not part of the release keep their mark' ((Marked $other) -and (Marked $other2)) ''

'--- Install.cmd run from inside the zip (not extracted)'
$lone = New-TestDir lone
Copy-Item (Join-Path $src 'Install.cmd') $lone
$out = (& cmd.exe /c "echo.|`"$(Join-Path $lone 'Install.cmd')`"" 2>&1) -join "`n"
Check 'says to extract the zip first' ($out -match 'Extract') $out

Complete-Test
