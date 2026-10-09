; LogPro Installer Script
; Inno Setup Script

#define MyAppName "LogPro"
#define MyAppVersion "3.2.0"
#define MyAppPublisher "LogPro"
#define MyAppURL "https://github.com/sundarlohar007/QADeviceTool"
#define MyAppExeName "LogPro.exe"

[Setup]
; Keep this AppId stable so existing QADeviceTool installations upgrade in place.
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\\dist
OutputBaseFilename=LogPro_v{#MyAppVersion}
Compression=zip
SolidCompression=yes
WizardStyle=modern
InfoAfterFile=..\docs\windows-prerequisites.txt
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter=LogPro.exe
AppMutex=LogProRunning
UninstallDisplayIcon={app}\{#MyAppExeName}
; A clean replacement must create its own uninstaller instead of appending to
; the old install log, which the registered previous uninstaller removes.
UninstallLogMode=new

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode

[InstallDelete]
Type: filesandordirs; Name: "{app}\tools\scrcpy-win64-*"
Type: filesandordirs; Name: "{app}\tools\scrcpy"
Type: filesandordirs; Name: "{app}\tools\pymobiledevice3"
Type: filesandordirs; Name: "{app}\tools\adb"
Type: files; Name: "{app}\tools\.gitkeep"

Type: files; Name: "{app}\.logpro_transaction_adb.json"
Type: files; Name: "{app}\.logpro_backup_adb.name"
Type: files; Name: "{app}\.logpro_backup_adb.manifest.json"
Type: filesandordirs; Name: "{app}\.logpro_backup_adb"
Type: files; Name: "{app}\.logpro_transaction_scrcpy.json"
Type: files; Name: "{app}\.logpro_backup_scrcpy.name"
Type: files; Name: "{app}\.logpro_backup_scrcpy.manifest.json"
Type: filesandordirs; Name: "{app}\.logpro_backup_scrcpy"
Type: files; Name: "{app}\.logpro_transaction_pymobiledevice3.json"
Type: files; Name: "{app}\.logpro_backup_pymobiledevice3.name"
Type: files; Name: "{app}\.logpro_backup_pymobiledevice3.manifest.json"
Type: filesandordirs; Name: "{app}\.logpro_backup_pymobiledevice3"
Type: files; Name: "{app}\installation-status.txt"

[Files]
Source: "app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--installation-check"; StatusMsg: "Checking compatible application and device-tool updates..."; Flags: waituntilterminated runhidden; Check: ShouldCheckUpdates; AfterInstall: ShowUpdateOutcome
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  PreviousUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}_is1';

function HasAppleMobileDeviceService(): Boolean;
begin
  Result := RegKeyExists(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service');
end;

function HasITunes(): Boolean;
begin
  Result := RegKeyExists(HKEY_LOCAL_MACHINE_64, 'SOFTWARE\Apple Computer, Inc.\iTunes') or
    RegKeyExists(HKEY_LOCAL_MACHINE_32, 'SOFTWARE\Apple Computer, Inc.\iTunes');
end;

procedure ExplainApplePrerequisite();
var
  Notice: String;
begin
  if HasITunes() and HasAppleMobileDeviceService() then
    Notice := 'iTunes and Apple Mobile Device Service were detected. Unlock and trust each iOS device before use.'
  else if HasITunes() then
    Notice := 'iTunes was detected, but Apple Mobile Device Service is missing. Repair the classic Windows iTunes installation before using iOS USB.'
  else if HasAppleMobileDeviceService() then
    Notice := 'iTunes was not detected, but Apple Mobile Device Service is installed. Check iOS USB detection after setup.'
  else
    Notice := 'iTunes / Apple Mobile Device Service was not detected. Android features will work, but iOS USB needs the classic Windows iTunes package from https://www.apple.com/itunes/download/win64 and device trust.';
  Log(Notice);
  if not WizardSilent then MsgBox(Notice, mbInformation, MB_OK);
end;

procedure RemovePreviousInstallation();
var
  PreviousDir, UninstallCommand, UninstallExe, RecoveryDir: String;
  ExitCode: Integer;
begin
  if RegKeyExists(HKEY_LOCAL_MACHINE_64, PreviousUninstallKey) then
  begin
    if not RegQueryStringValue(HKEY_LOCAL_MACHINE_64, PreviousUninstallKey, 'InstallLocation', PreviousDir) then
      RaiseException('Existing LogPro installation has no location. Repair it before continuing.');
    if not RegQueryStringValue(HKEY_LOCAL_MACHINE_64, PreviousUninstallKey, 'UninstallString', UninstallCommand) then
      RaiseException('Existing LogPro installation has no uninstaller. Repair it before continuing.');
  end
  else if RegKeyExists(HKEY_LOCAL_MACHINE_32, PreviousUninstallKey) then
  begin
    if not RegQueryStringValue(HKEY_LOCAL_MACHINE_32, PreviousUninstallKey, 'InstallLocation', PreviousDir) then
      RaiseException('Existing LogPro installation has no location. Repair it before continuing.');
    if not RegQueryStringValue(HKEY_LOCAL_MACHINE_32, PreviousUninstallKey, 'UninstallString', UninstallCommand) then
      RaiseException('Existing LogPro installation has no uninstaller. Repair it before continuing.');
  end
  else Exit;
  PreviousDir := ExpandFileName(RemoveBackslashUnlessRoot(Trim(PreviousDir)));
  UninstallExe := RemoveQuotes(Trim(UninstallCommand));
  if (not FileExists(UninstallExe)) or
    (not PathSame(ExtractFileDir(UninstallExe), PreviousDir)) then
    RaiseException('Existing LogPro uninstall entry does not match its installation directory. No files were removed.');
  Log('Uninstalling previous LogPro installation from ' + PreviousDir);
  if (not Exec(UninstallExe, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', PreviousDir,
    SW_HIDE, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then
    RaiseException('Previous LogPro uninstall failed. Setup cannot continue until it is removed.');
  if DirExists(PreviousDir) then
  begin
    { Preserve files that the old uninstaller did not own; do not silently delete user data. }
    RecoveryDir := ExpandConstant('{localappdata}\LogPro\RecoveredInstallFiles\') +
      GetDateTimeString('yyyymmddhhnnss', '-', '-');
    if (not ForceDirectories(ExtractFileDir(RecoveryDir))) or
      (not RenameFile(PreviousDir, RecoveryDir)) then
    begin
      { A custom installation can be on another drive, where a rename to LocalAppData fails. }
      RecoveryDir := PreviousDir + '.Recovered.' + GetDateTimeString('yyyymmddhhnnss', '-', '-');
      if not RenameFile(PreviousDir, RecoveryDir) then
        RaiseException('The previous installation left files in ' + PreviousDir +
          '. Move them to a safe location and retry setup.');
    end;
    Log('Preserved leftover files at ' + RecoveryDir);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then RemovePreviousInstallation();
  if CurStep = ssPostInstall then ExplainApplePrerequisite();
end;

function ShouldCheckUpdates(): Boolean;
begin
  Result := Pos('/SKIPUPDATECHECK', Uppercase(GetCmdTail)) = 0;
end;

procedure ShowUpdateOutcome();
var
  Outcome: AnsiString;
begin
  if LoadStringFromFile(ExpandConstant('{app}\installation-status.txt'), Outcome) then
  begin
    Log(String(Outcome));
    if not WizardSilent then
      MsgBox(String(Outcome) + #13#10 + #13#10 + 'Setup can finish offline. Retry failed checks in LogPro Settings.', mbInformation, MB_OK);
  end
  else
  begin
    Log('Update check did not produce a status report.');
    if not WizardSilent then
      MsgBox('Update check did not produce a status report. The bundled installation is available; check component status in Settings.', mbInformation, MB_OK);
  end;
end;
