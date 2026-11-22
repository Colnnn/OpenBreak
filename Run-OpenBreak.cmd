@echo off
setlocal
powershell.exe -NoProfile -File "%~dp0Build-OpenBreak.ps1" -Action Launch -Configuration Debug %*
set "build_status=%errorlevel%"
if not "%build_status%"=="0" pause
exit /b %build_status%
