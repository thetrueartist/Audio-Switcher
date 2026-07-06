@echo off
rem Double-click to remove AudioSwitcher (stops it, removes auto-start, deletes the exe).
rem Learned state in %LOCALAPPDATA%\AudioSwitcher\ is kept - delete that folder for a clean slate.
net session >nul 2>nul
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0AudioSwitcher.ps1" -Uninstall
echo.
pause
