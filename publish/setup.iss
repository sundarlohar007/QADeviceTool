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

[Files]
Source: "app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--installation-check"; StatusMsg: "Checking compatible application and device-tool updates..."; Flags: waituntilterminated runhidden; Check: ShouldCheckUpdates
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function ShouldCheckUpdates(): Boolean;
begin
  Result := Pos('/SKIPUPDATECHECK', Uppercase(GetCmdTail)) = 0;
end;
