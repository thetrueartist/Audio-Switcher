@echo off
rem Double-click to compile AudioSwitcher. No PowerShell / execution-policy hassle.
setlocal

if not exist "%~dp0AudioSwitcher.csproj" (
  echo.
  echo   Looks like you are running this from inside the .zip.
  echo   EXTRACT the whole zip first, then run Build.cmd from the
  echo   extracted folder. ^(A release download is already compiled -
  echo   you only need Build.cmd if you have the source.^)
  echo.
  pause
  exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo   .NET SDK not found. Install it, then run this again:
  echo     winget install Microsoft.DotNet.SDK.8
  echo   or download from https://dotnet.microsoft.com/download
  echo.
  pause
  exit /b 1
)
echo Building AudioSwitcher...
dotnet publish "%~dp0AudioSwitcher.csproj" -c Release -r win-x64 --self-contained false -o "%LOCALAPPDATA%\AudioSwitcher\bin"
if errorlevel 1 (
  echo.
  echo   Build FAILED - see the messages above.
  pause
  exit /b 1
)
echo.
echo   Built: %LOCALAPPDATA%\AudioSwitcher\bin\AudioSwitcher.exe
echo   Next: run Install.cmd for auto-start, or double-click the exe for the tray icon.
echo.
pause
