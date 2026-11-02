; SSH-it installer — Inno Setup 6
; Build: "C:\Users\Moddy\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer\setup.iss

#define MyAppName "SSH-it"
#define MyAppVersion "1.0.31"
#define MyAppPublisher "SSH-it"
#define MyAppExeName "SSH-it.exe"
#define MyAppId "{{7C4E1B62-5D2A-4F0B-9E45-2B7A6C1D8F31}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=SSH-it-Setup-{#MyAppVersion}
SetupIconFile=..\src\SshIt\Assets\ssh-it.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=no
RestartApplications=no
VersionInfoVersion=1.0.29.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\src\SshIt\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\SshIt\Assets\ssh-it.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "SshIt.Desktop.1"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; AppUserModelID: "SshIt.Desktop.1"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Sessions and settings live in %APPDATA%\SSH-it and are deliberately kept.
Type: files; Name: "{app}\Assets\ssh-it.ico"
Type: dirifempty; Name: "{app}\Assets"
Type: dirifempty; Name: "{app}"
