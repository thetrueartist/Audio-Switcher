@echo off
rem Front door: double-click for the menu, or pass args (e.g. AudioSwitcher.cmd -Status).
rem Uses -ExecutionPolicy Bypass so you never hit the "not digitally signed" wall.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0AudioSwitcher.ps1" %*
if "%~1"=="" pause
