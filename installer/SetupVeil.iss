#define AppName "Veil"
#define AppVersion "1.0"
#define AppExeName "Veil.exe"
#define PublishDir "..\artifacts\publish"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=kirosnn
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=SetupVeil
SetupIconFile={#PublishDir}\Assets\Logo\veil.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Veil"; ValueData: """{app}\{#AppExeName}"" --startup"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Lancer {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/f /im {#AppExeName}"; Flags: runhidden; RunOnceId: "KillVeil"
