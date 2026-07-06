@echo off
rem Double-click to remove AudioSwitcher. Standalone - works even from inside the zip.
rem Learned settings in %LOCALAPPDATA%\AudioSwitcher\ are kept.

net session >nul 2>nul
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
  exit /b
)

echo Stopping AudioSwitcher...
taskkill /f /im AudioSwitcher.exe >nul 2>nul
echo Removing auto-start task...
schtasks /delete /tn AudioSwitcherDaemon /f >nul 2>nul
echo Deleting program files...
rmdir /s /q "%LOCALAPPDATA%\AudioSwitcher\bin" >nul 2>nul

echo.
echo Done. Learned settings kept in %LOCALAPPDATA%\AudioSwitcher
echo (delete that folder yourself for a clean slate).
echo.
pause
