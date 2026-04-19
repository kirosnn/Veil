#define AppName        "Veil"
#define AppVersion     "1.0.0"
#define AppPublisher   "Veil"
#define AppURL         "https://github.com/kirosnn/veil"
#define VeilExe        "Veil.exe"
#define TerminalExe    "VeilTerminal.exe"

[Setup]
AppId={{B4C1D2E3-F5A6-7890-BCDE-F12345678901}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={localappdata}\Programs\Veil
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\inno
OutputBaseFilename=VeilSetup
SetupIconFile=..\apps\desktop\Veil\Assets\Logo\veil.ico
WizardStyle=modern
WizardSizePercent=100
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter={#VeilExe},{#TerminalExe}
UninstallDisplayName=Veil
UninstallDisplayIcon={app}\{#VeilExe}
; No UAC prompt — per-user install
PrivilegesRequiredOverridesAllowed=

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon_veil";     Description: "Veil shortcut on Desktop";          GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "desktopicon_terminal"; Description: "Veil Terminal shortcut on Desktop"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Dirs]
Name: "{app}"
Name: "{app}\Terminal"

[Files]
; ── Veil ─────────────────────────────────────────────────────────────────────
Source: "..\artifacts\build\Veil\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── Veil Terminal ─────────────────────────────────────────────────────────────
Source: "..\artifacts\build\VeilTerminal\*"; DestDir: "{app}\Terminal"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start menu
Name: "{userprograms}\Veil\Veil";          Filename: "{app}\{#VeilExe}";          WorkingDir: "{app}";          IconFilename: "{app}\{#VeilExe}"
Name: "{userprograms}\Veil\Veil Terminal"; Filename: "{app}\Terminal\{#TerminalExe}"; WorkingDir: "{app}\Terminal"; IconFilename: "{app}\Terminal\{#TerminalExe}"

; Desktop (optional tasks)
Name: "{userdesktop}\Veil";          Filename: "{app}\{#VeilExe}";                 Tasks: desktopicon_veil
Name: "{userdesktop}\Veil Terminal"; Filename: "{app}\Terminal\{#TerminalExe}";    Tasks: desktopicon_terminal

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Veil"; ValueData: """{app}\{#VeilExe}"" --startup"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#VeilExe}"; Description: "Launch Veil"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]

procedure StopApps();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Veil.exe',         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM VeilTerminal.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function ContainsVeilMarker(const Value: String): Boolean;
var
  LowerValue: String;
  LowerVeilDir: String;
begin
  LowerValue := LowerCase(Value);
  LowerVeilDir := LowerCase(AddBackslash(ExpandConstant('{localappdata}') + '\Programs\Veil'));
  Result :=
    (Pos('veil', LowerValue) > 0) or
    (Pos(LowerVeilDir, LowerCase(AddBackslash(Value))) > 0);
end;

function TryGetRegistryString(const RootKey: Integer; const SubKey, ValueName: String; var Value: String): Boolean;
begin
  Value := '';
  Result := RegQueryStringValue(RootKey, SubKey, ValueName, Value) and (Trim(Value) <> '');
end;

function GetCommandExecutablePath(const CommandLine: String): String;
var
  ClosingQuotePos: Integer;
  SpacePos: Integer;
  Trimmed: String;
begin
  Trimmed := Trim(CommandLine);
  if Trimmed = '' then
  begin
    Result := '';
    Exit;
  end;

  if Copy(Trimmed, 1, 1) = '"' then
  begin
    Delete(Trimmed, 1, 1);
    ClosingQuotePos := Pos('"', Trimmed);
    if ClosingQuotePos > 0 then
      Result := Copy(Trimmed, 1, ClosingQuotePos - 1)
    else
      Result := Trimmed;
    Exit;
  end;

  SpacePos := Pos(' ', Trimmed);
  if SpacePos > 0 then
    Result := Copy(Trimmed, 1, SpacePos - 1)
  else
    Result := Trimmed;
end;

function GetCommandArguments(const CommandLine: String): String;
var
  ClosingQuotePos: Integer;
  SpacePos: Integer;
  Trimmed: String;
begin
  Trimmed := Trim(CommandLine);
  if Trimmed = '' then
  begin
    Result := '';
    Exit;
  end;

  if Copy(Trimmed, 1, 1) = '"' then
  begin
    Delete(Trimmed, 1, 1);
    ClosingQuotePos := Pos('"', Trimmed);
    if ClosingQuotePos > 0 then
      Result := Trim(Copy(Trimmed, ClosingQuotePos + 1, MaxInt))
    else
      Result := '';
    Exit;
  end;

  SpacePos := Pos(' ', Trimmed);
  if SpacePos > 0 then
    Result := Trim(Copy(Trimmed, SpacePos + 1, MaxInt))
  else
    Result := '';
end;

function ShouldCleanupUninstallKey(const SubKeyName: String; const CurrentId: String): Boolean;
var
  FullSubKey: String;
  Value: String;
begin
  if SameText(SubKeyName, CurrentId) then
  begin
    Result := False;
    Exit;
  end;

  if SameText(SubKeyName, 'Veil') or SameText(SubKeyName, 'VeilTerminal') then
  begin
    Result := True;
    Exit;
  end;

  FullSubKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + SubKeyName;

  if TryGetRegistryString(HKCU, FullSubKey, 'DisplayName', Value) and ContainsVeilMarker(Value) then
  begin
    Result := True;
    Exit;
  end;

  if TryGetRegistryString(HKCU, FullSubKey, 'QuietDisplayName', Value) and ContainsVeilMarker(Value) then
  begin
    Result := True;
    Exit;
  end;

  if TryGetRegistryString(HKCU, FullSubKey, 'InstallLocation', Value) and ContainsVeilMarker(Value) then
  begin
    Result := True;
    Exit;
  end;

  if TryGetRegistryString(HKCU, FullSubKey, 'DisplayIcon', Value) and ContainsVeilMarker(Value) then
  begin
    Result := True;
    Exit;
  end;

  if TryGetRegistryString(HKCU, FullSubKey, 'UninstallString', Value) and ContainsVeilMarker(Value) then
  begin
    Result := True;
    Exit;
  end;

  Result := False;
end;

procedure CleanupUninstallKey(const AppId: String);
var
  SubKey: String;
  InstallLocation: String;
  UninstallString: String;
  ExePath: String;
  ExeArgs: String;
  ResultCode: Integer;
begin
  SubKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + AppId;

  if RegQueryStringValue(HKCU, SubKey, 'InstallLocation', InstallLocation) then
  begin
    InstallLocation := Trim(InstallLocation);
    if (InstallLocation <> '') and ContainsVeilMarker(InstallLocation) and DirExists(InstallLocation) then
      DelTree(InstallLocation, True, True, True);
  end;

  if RegQueryStringValue(HKCU, SubKey, 'UninstallString', UninstallString) then
  begin
    ExePath := GetCommandExecutablePath(UninstallString);
    ExeArgs := GetCommandArguments(UninstallString);
    if (ExePath <> '') and FileExists(ExePath) and (Pos('unins', LowerCase(ExtractFileName(ExePath))) = 1) then
    begin
      if ExeArgs <> '' then
        ExeArgs := ExeArgs + ' ';
      Exec(ExePath, ExeArgs + '/VERYSILENT /NORESTART /NOCANCEL', '',
           SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;

  RegDeleteKeyIncludingSubkeys(HKCU, SubKey);
end;

{ Remove a stale Start-Menu .lnk that points to a file that no longer exists. }
procedure RemoveOrphanedShortcut(const LinkPath: String);
begin
  if not FileExists(LinkPath) then Exit;
  { Inno Setup can't read .lnk targets, so we remove the shortcut only when
    we already know the install directory is gone. }
  if not DirExists(ExpandConstant('{localappdata}') + '\Programs\Veil') then
    DeleteFile(LinkPath);
end;

{ Enumerate every HKCU uninstall key and wipe any whose DisplayName
  contains "Veil" — catches old entries regardless of their key name.
  Skips the key that the current installer is about to write. }
procedure CleanupAllVeilUninstallKeys();
var
  BaseKey: String;
  SubKeyNames: TArrayOfString;
  KeyIndex: Integer;
  SubKeyName: String;
  CurrentId: String;
begin
  BaseKey  := 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  CurrentId := '{B4C1D2E3-F5A6-7890-BCDE-F12345678901}_is1';
  if not RegGetSubkeyNames(HKCU, BaseKey, SubKeyNames) then
    Exit;

  for KeyIndex := 0 to GetArrayLength(SubKeyNames) - 1 do
  begin
    SubKeyName := SubKeyNames[KeyIndex];
    if ShouldCleanupUninstallKey(SubKeyName, CurrentId) then
      CleanupUninstallKey(SubKeyName);
  end;
end;

procedure CleanupLegacyInstalls();
begin
  StopApps();

  { Wipe every registry entry that mentions Veil, whatever key name it used }
  CleanupAllVeilUninstallKeys();

  { Belt-and-suspenders: also hit known explicit IDs in case enumeration
    missed anything (e.g. key already partially deleted) }
  CleanupUninstallKey('Veil');
  CleanupUninstallKey('VeilTerminal');

  { Remove orphaned Start-Menu shortcuts whose target is now missing }
  RemoveOrphanedShortcut(ExpandConstant('{userprograms}') + '\Veil\Veil.lnk');
  RemoveOrphanedShortcut(ExpandConstant('{userprograms}') + '\Veil\Veil Terminal.lnk');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanupLegacyInstalls();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopApps();
end;
