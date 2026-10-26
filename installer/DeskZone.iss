; Extraction/launch only; the PowerShell wizard owns installation and user data.
#ifndef PublishSource
  #error PublishSource is required. Use Build-DeskZoneInstaller.ps1.
#endif
#ifndef InstallerSource
  #error InstallerSource is required. Use Build-DeskZoneInstaller.ps1.
#endif
#ifndef IconFile
  #error IconFile is required. Use Build-DeskZoneInstaller.ps1.
#endif

[Setup]
AppId=DeskZone.InstallerBootstrap
AppName=DeskZone
AppVersion={#GetVersionNumbersString(PublishSource + "\DeskZone.exe")}
AppPublisher=DeskZone
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
ShowLanguageDialog=no
WizardStyle=modern
SetupIconFile={#IconFile}
Compression=lzma2/fast
SolidCompression=yes
SetupLogging=yes
CloseApplications=no
RestartApplications=no

[Files]
Source: "{#PublishSource}\*"; DestDir: "{tmp}\DeskZone"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#InstallerSource}\*"; DestDir: "{tmp}\DeskZone"; Flags: ignoreversion

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  PowerShellPath: String;
  PayloadPath: String;
  Arguments: String;
begin
  if CurStep = ssPostInstall then
  begin
    PayloadPath := ExpandConstant('{tmp}\DeskZone');
    PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
    Arguments := '-NoLogo -NoProfile -NonInteractive -STA -WindowStyle Hidden ' +
      '-ExecutionPolicy Bypass -File "' + PayloadPath + '\Start-DeskZoneInstaller.ps1"';
    WizardForm.Hide;
    try
      { Wait so temporary payload files remain available throughout the wizard. }
      if not Exec(PowerShellPath, Arguments, PayloadPath, SW_HIDE,
        ewWaitUntilTerminated, ExitCode) then
        RaiseException('Cannot start DeskZone installer: ' + SysErrorMessage(ExitCode));
      if ExitCode <> 0 then
        RaiseException(Format('DeskZone installer failed (exit %d). See LOCALAPPDATA\DeskZone\logs\installer-*.log.', [ExitCode]));
    finally
      WizardForm.Show;
    end;
  end;
end;
