@echo off
rem Double-click to build/install AudioSwitcher with auto-start. Self-elevates.

rem Guard: running this from INSIDE the .zip only extracts this one file to a temp
rem folder, so the other files are missing. Tell the user to extract first.
if not exist "%~dp0AudioSwitcher.ps1" (
  echo.
  echo   Looks like you are running this from inside the .zip.
  echo   EXTRACT the whole zip first, then run Install.cmd from the
  echo   extracted folder.
  echo.
  pause
  exit /b 1
)

net session >nul 2>nul
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0AudioSwitcher.ps1" -Install
echo.
pause
