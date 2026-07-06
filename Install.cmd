@echo off
rem Double-click to build + install AudioSwitcher with auto-start. Self-elevates.
net session >nul 2>nul
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0AudioSwitcher.ps1" -Install
echo.
pause
