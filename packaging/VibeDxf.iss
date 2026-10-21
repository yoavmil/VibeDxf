#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#define AppName "VibeDxf"
#define AppExe "VibeDxf.exe"

[Setup]
AppId={{9BB5426D-489D-4706-AB47-F9AB42C33925}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Yoav Miller
AppPublisherURL=https://github.com/yoavmil/VibeDxf
AppSupportURL=https://github.com/yoavmil/VibeDxf/issues
AppUpdatesURL=https://github.com/yoavmil/VibeDxf/releases
DefaultDirName={localappdata}\Programs\VibeDxf
DefaultGroupName=VibeDxf
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=VibeDxf-Setup-{#AppVersion}-win-x64
SetupIconFile={#SourcePath}..\src\Assets\VibeDxf.ico
UninstallDisplayIcon={app}\{#AppExe}
InfoBeforeFile={#SourcePath}requirements.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\VibeDxf"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"
Name: "{userdesktop}\VibeDxf"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch VibeDxf"; Flags: nowait postinstall skipifsilent
