@echo off
rem NT8 Guard - install, or update an existing install. Double-click me in the folder you extracted.
rem Settings, the saved password, logs and strategy backups are kept on an update.
if not exist "%~dp0app\Install-Guard.ps1" (
  echo.
  echo  NT8 Guard: Install.cmd cannot find the app folder next to it.
  echo  Extract the zip first: right-click NT8Guard-v*.zip, choose "Extract All...",
  echo  then double-click Install.cmd in the extracted folder.
  echo.
  pause
  exit /b 1
)
rem Unblock the release's own files first, with a command rather than a script: a script execution
rem policy set by Group Policy overrides -ExecutionPolicy Bypass and would refuse a marked Install-Guard.ps1.
set "NT8G_SRC=%~dp0."
powershell.exe -NoProfile -Command "$s = $env:NT8G_SRC; foreach ($d in 'app', 'python') { $p = Join-Path $s $d; if (Test-Path -LiteralPath $p) { Get-ChildItem -LiteralPath $p -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue } }; foreach ($f in 'Install.cmd', 'NT8Guard-AddOns.zip', 'NT8 Guard - Setup Guide.pdf', 'NT8 Guard - Quick Start.png', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'README.txt') { $p = Join-Path $s $f; if (Test-Path -LiteralPath $p) { Unblock-File -LiteralPath $p -ErrorAction SilentlyContinue } }"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\Install-Guard.ps1" -Source "%~dp0." %*
if errorlevel 1 (
  echo.
  pause
  exit /b 1
)
