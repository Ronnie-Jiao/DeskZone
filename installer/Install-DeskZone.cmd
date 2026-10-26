@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0Start-DeskZoneInstaller.ps1" %*
set "DeskZoneInstallerExit=%ERRORLEVEL%"
if not "%DeskZoneInstallerExit%"=="0" (
    echo DeskZone installer failed. See %%LOCALAPPDATA%%\DeskZone\logs\installer-*.log.
    pause
)
exit /b %DeskZoneInstallerExit%
