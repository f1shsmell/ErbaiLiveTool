@echo off
rem Build ErbaiLiveTool installer (Setup.exe + green zip). Double-click to run.
rem Output: installer\Output\ErbaiLiveTool_Setup_v<ver>.exe and ErbaiLiveTool-<ver>.zip
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-installer.ps1"
pause