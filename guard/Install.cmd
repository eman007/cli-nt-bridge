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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\Install-Guard.ps1" -Source "%~dp0." %*
if errorlevel 1 (
  echo.
  pause
  exit /b 1
)
