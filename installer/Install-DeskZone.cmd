@echo off
setlocal
powershell.exe -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0Install-DeskZone.ps1" -Interactive %*
exit /b %ERRORLEVEL%
