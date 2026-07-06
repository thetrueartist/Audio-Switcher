@echo off
rem Front door: double-click for the menu, or pass args (e.g. AudioSwitcher.cmd -Status).
rem Uses -ExecutionPolicy Bypass so you never hit the "not digitally signed" wall.

if not exist "%~dp0AudioSwitcher.ps1" (
  echo.
  echo   Looks like you are running this from inside the .zip.
  echo   EXTRACT the whole zip first, then run from the extracted folder.
  echo.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0AudioSwitcher.ps1" %*
if "%~1"=="" pause
