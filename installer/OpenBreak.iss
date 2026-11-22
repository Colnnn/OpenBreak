; Keep the original installation, single-instance, setup and startup identities for upgrades.
#define ProductId "Afterbreak.5572F77B-412F-48B5-AD89-247ECE6B356E"
#define ProductMutex "Local\Afterbreak.NativeStudy"
#define SetupMutexName "Local\Afterbreak.UserSetup"
#define InstallPath "{localappdata}\Programs\Afterbreak"
#define UninstallRegistryPath "Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductId + "_is1"
#define FreshLogValue "OpenBreakOnlyUninstallLog"
#define StartLink "{userprograms}\OpenBreak"
#define LegacyStartLink "{userprograms}\Afterbreak.lnk"
#define AppExecutable "OpenBreak.exe"

[Setup]
AppId={#ProductId}
AppName=OpenBreak
AppVersion={#AppVersion}
VersionInfoDescription=OpenBreak Setup
VersionInfoProductName=OpenBreak
VersionInfoVersion={#AppVersion}.0
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
PrivilegesRequired=lowest
DefaultDirName={#InstallPath}
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DefaultGroupName=OpenBreak
UninstallDisplayName=OpenBreak
UninstallDisplayIcon={code:PayloadDirectory}\{#AppExecutable}
CreateUninstallRegKey=ConfirmActivation
UninstallLogMode=append
AppMutex={#ProductMutex}
SetupMutex={#SetupMutexName}
CloseApplications=no
RestartApplications=no
OutputBaseFilename=OpenBreak-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes

[Files]
Source: "{#PayloadRoot}\*"; DestDir: "{code:PayloadDirectory}"; Excludes: "\files.sha256"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadRoot}\files.sha256"; DestDir: "{code:PayloadDirectory}"; Flags: ignoreversion; AfterInstall: VerifyInstalledPayload

[Icons]
Name: "{#StartLink}"; Filename: "{code:PayloadDirectory}\{#AppExecutable}"; WorkingDir: "{code:PayloadDirectory}"; Check: IsPayloadVerified

[Code]
#include "InstallPolicy.iss"

const
  AppMutexName = '{#ProductMutex}';
  UninstallKey = '{#UninstallRegistryPath}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunName = '{#ProductId}';
  NotFound = $FFFFFFFF;
  DirectoryAttribute = $10;
  ReparseAttribute = $400;

var
  AppMutexHandle: NativeUInt;
  ActiveSlot, ShortcutBackup, PayloadError: String;
  StartupPage: TInputOptionWizardPage;
  Committed, HadInstall, OpenBreakOnlyLog, PayloadVerified: Boolean;
  StartupChanged, StartupFailed, LegacyShortcutCleanupFailed: Boolean;

function FileAttributes(Path: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function CreateAppMutex(Attributes: NativeUInt; InitialOwner: Integer; Name: String): NativeUInt;
  external 'CreateMutexW@kernel32.dll stdcall';
function CloseAppMutex(Handle: NativeUInt): Integer;
  external 'CloseHandle@kernel32.dll stdcall';

function RootDirectory: String;
begin
  Result := ExpandConstant('{#InstallPath}');
end;

function StartMenuLink: String;
begin
  Result := ExpandConstant('{#StartLink}.lnk');
end;

function LegacyStartMenuLink: String;
begin
  Result := ExpandConstant('{#LegacyStartLink}');
end;

function PayloadDirectory(Param: String): String;
begin
  Result := ActiveSlot;
end;

function AcquireAppMutex: Boolean;
var
  Handle: NativeUInt;
  LastError: Integer;
begin
  if AppMutexHandle <> 0 then begin
    Result := True;
    Exit;
  end;
  Handle := CreateAppMutex(0, 0, AppMutexName);
  LastError := DLLGetLastError;
  Result := (Handle <> 0) and (LastError <> 183);
  if Result then
    AppMutexHandle := Handle
  else if Handle <> 0 then
    CloseAppMutex(Handle);
end;

function PathExistsChecked(Path: String): Boolean;
var
  Attributes: LongWord;
  LastError: Integer;
begin
  Attributes := FileAttributes(Path);
  LastError := DLLGetLastError;
  Result := Attributes <> NotFound;
  if Result and ((Attributes and ReparseAttribute) <> 0) then
    RaiseException('OpenBreak setup cannot use a linked path: ' + Path);
  if not Result and (LastError <> 2) and (LastError <> 3) then
    RaiseException('OpenBreak setup could not inspect: ' + Path);
end;

procedure CheckPathAncestors(Path: String);
var
  Parent: String;
begin
  while Path <> '' do begin
    PathExistsChecked(Path);
    Parent := ExtractFileDir(Path);
    if CompareText(Parent, Path) = 0 then
      Exit;
    Path := Parent;
  end;
end;

function CheckTree(Path: String): Boolean;
var
  Item: TFindRec;
begin
  Result := False;
  if not PathExistsChecked(Path) then
    Exit;
  if not DirExists(Path) then
    RaiseException('Expected an OpenBreak installation directory: ' + Path);
  if not FindFirst(AddBackslash(Path) + '*', Item) then
    RaiseException('OpenBreak setup could not read: ' + Path);
  try
    repeat
      if (Item.Name <> '.') and (Item.Name <> '..') then begin
        Result := True;
        if (Item.Attributes and ReparseAttribute) <> 0 then
          RaiseException('OpenBreak setup cannot use a linked path: ' + AddBackslash(Path) + Item.Name);
        if (Item.Attributes and DirectoryAttribute) <> 0 then
          CheckTree(AddBackslash(Path) + Item.Name);
      end;
    until not FindNext(Item);
  finally
    FindClose(Item);
  end;
end;

function ReadNormalShortcutTarget(Link: String): String;
var
  Shell, Shortcut: Variant;
  Arguments: String;
begin
  Shell := CreateOleObject('WScript.Shell');
  Shortcut := Shell.CreateShortcut(Link);
  Arguments := Shortcut.Arguments;
  Result := '';
  if Arguments = '' then
    Result := Shortcut.TargetPath;
end;

function IsOwnedLegacyShortcut(Link: String): Boolean;
var
  Attributes: LongWord;
begin
  Result := False;
  Attributes := FileAttributes(Link);
  if (Attributes = NotFound) or ((Attributes and (DirectoryAttribute or ReparseAttribute)) <> 0) then
    Exit;
  try
    Result := IsOwnedVersionedExecutable(RootDirectory, '"' + ReadNormalShortcutTarget(Link) + '"',
      LegacyAppExecutable);
  except
    Log('OpenBreak setup could not read the previous Start menu shortcut.');
  end;
end;

function OldVersion: String;
var
  Version: TInstallVersion;
  Path: String;
begin
  Result := '';
  if RegKeyExists(HKCU, UninstallKey) and
     (not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Result) or
      not ParseInstallVersion(Result, Version) or
      not RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', Path) or
      (CompareText(Path, RootDirectory) <> 0)) then
    RaiseException('OpenBreak could not verify the installed version. Repair the installation before continuing.');
end;

function HasOpenBreakOnlyLog: Boolean;
var
  Marker: Cardinal;
begin
  Result := RegQueryDWordValue(HKCU, UninstallKey, '{#FreshLogValue}', Marker) and (Marker = 1);
end;

function CheckInstallPaths: Boolean;
begin
  CheckPathAncestors(RootDirectory);
  CheckPathAncestors(StartMenuLink);
  if PathExistsChecked(StartMenuLink) and
     (not FileExists(StartMenuLink) or
      not IsOwnedVersionedExecutable(RootDirectory, '"' + ReadNormalShortcutTarget(StartMenuLink) + '"',
        '{#AppExecutable}')) then
    RaiseException('The OpenBreak Start menu shortcut is in use. Move it and retry.');
  Result := CheckTree(RootDirectory);
end;

function OwnedStartup(var Command: String): Boolean;
begin
  Result := RegQueryStringValue(HKCU, RunKey, RunName, Command) and
    IsOwnedStartupCommand(RootDirectory, Command);
end;

function SelectUnusedSlot: String;
var
  Index: Integer;
begin
  for Index := 1 to 1000 do begin
    Result := RootDirectory + '\versions\{#AppVersion}\' + IntToStr(Index);
    if not PathExistsChecked(Result) then
      Exit;
  end;
  RaiseException('No unused OpenBreak installation slot is available. Uninstall OpenBreak and retry.');
end;

procedure CheckInstallVersion(RootHasFiles: Boolean);
var
  Existing: String;
  Current, Candidate: TInstallVersion;
begin
  Existing := OldVersion;
  if Existing <> '' then begin
    ParseInstallVersion(Existing, Current);
    ParseInstallVersion('{#AppVersion}', Candidate);
    if CompareInstallVersions(Current, Candidate) > 0 then
      RaiseException('A newer version of OpenBreak is already installed.');
  end else if RootHasFiles or FileExists(StartMenuLink) then
    RaiseException('Setup could not identify an existing OpenBreak installation. Move its files before retrying.');
end;

function InitializeSetup: Boolean;
begin
  CheckInstallVersion(CheckInstallPaths);
  ActiveSlot := SelectUnusedSlot;
  Result := True;
end;

procedure StartupOptionChanged(Sender: TObject);
begin
  StartupChanged := True;
end;

procedure InitializeWizard;
var
  Command, DisplayName: String;
begin
  StartupPage := CreateInputOptionPage(wpSelectDir, 'Start at sign-in',
    'Choose whether to register OpenBreak for Windows startup.',
    'Windows Startup settings can still keep OpenBreak disabled.', False, False);
  StartupPage.Add('Add OpenBreak to Windows startup');
  HadInstall := OldVersion <> '';
  { Inno recreates the uninstall key. Carry forward the marker only from OpenBreak. }
  OpenBreakOnlyLog := not HadInstall or
    (HasOpenBreakOnlyLog and RegQueryStringValue(HKCU, UninstallKey, 'DisplayName', DisplayName) and
      (CompareText(DisplayName, 'OpenBreak') = 0));
  StartupPage.Values[0] := HadInstall and OwnedStartup(Command);
  StartupPage.CheckListBox.OnClickCheck := @StartupOptionChanged;
end;

procedure BackUpShortcut;
var
  Backup: String;
begin
  if (ShortcutBackup = '') and FileExists(StartMenuLink) then begin
    Backup := ExpandConstant('{tmp}\previous-openbreak.lnk');
    if not CopyFile(StartMenuLink, Backup, True) then
      RaiseException('OpenBreak setup could not preserve the previous Start menu shortcut.');
    ShortcutBackup := Backup;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  try
    if CompareText(WizardDirValue, RootDirectory) <> 0 then
      RaiseException('OpenBreak installs in the current user profile. Remove the custom destination and retry.');
    CheckInstallVersion(CheckInstallPaths);
    if PathExistsChecked(ActiveSlot) then
      RaiseException('The OpenBreak installation slot changed. Cancel Setup and retry.');
    if not AcquireAppMutex then
      RaiseException('Quit OpenBreak from its tray icon, then run Setup again.');
    BackUpShortcut;
  except
    Result := GetExceptionMessage;
  end;
end;

procedure CheckInstalledPayload;
var
  Lines: TArrayOfString;
  Manifest, RelativePath, FullPath: String;
  Index: Integer;
begin
  Manifest := ActiveSlot + '\files.sha256';
  if not FileExists(Manifest) or
     (CompareText(GetSHA256OfFile(Manifest), '{#ManifestHash}') <> 0) or
     not LoadStringsFromFile(Manifest, Lines) then
    RaiseException('OpenBreak setup could not verify the installed files.');
  for Index := 0 to GetArrayLength(Lines) - 1 do begin
    RelativePath := Copy(Lines[Index], 67, Length(Lines[Index]));
    StringChangeEx(RelativePath, '/', '\', True);
    FullPath := AddBackslash(ActiveSlot) + RelativePath;
    if not PathExistsChecked(FullPath) or
       (CompareText(GetSHA256OfFile(FullPath), Copy(Lines[Index], 1, 64)) <> 0) then
      RaiseException('OpenBreak setup could not verify: ' + RelativePath);
  end;
end;

procedure VerifyInstalledPayload;
begin
  { Inno reports and suppresses exceptions from AfterInstall callbacks. }
  try
    CheckInstalledPayload;
    PayloadVerified := True;
  except
    PayloadError := GetExceptionMessage;
  end;
end;

function IsPayloadVerified: Boolean;
begin
  Result := PayloadVerified;
end;

function ConfirmActivation: Boolean;
begin
  { The uninstall registration directive runs before Inno replaces the previous
    Apps entry. An exception at this boundary enters Inno's installation rollback. }
  if not PayloadVerified then begin
    if PayloadError = '' then
      PayloadError := 'OpenBreak setup did not finish verifying the new files.';
    RaiseException(PayloadError);
  end;
  CheckPathAncestors(StartMenuLink);
  if not FileExists(StartMenuLink) or
     (CompareText(ReadNormalShortcutTarget(StartMenuLink), ActiveSlot + '\{#AppExecutable}') <> 0) then
    RaiseException('OpenBreak setup could not create its Start menu shortcut.');
  Result := True;
end;

procedure RetireLegacyShortcut;
var
  Link: String;
begin
  Link := LegacyStartMenuLink;
  if OpenBreakOnlyLog then
    Exit;
  try
    CheckPathAncestors(ExtractFileDir(Link));
    if IsOwnedLegacyShortcut(Link) then
      LegacyShortcutCleanupFailed := not DeleteFile(Link) and IsOwnedLegacyShortcut(Link);
  except
    LegacyShortcutCleanupFailed := True;
    Log(GetExceptionMessage);
  end;
  if LegacyShortcutCleanupFailed then
    SuppressibleMsgBox('OpenBreak was installed. Setup could not remove the previous Start menu shortcut.',
      mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(Step: TSetupStep);
var
  PreviousCommand: String;
  Owned, Present, RegisterStartup: Boolean;
  Marker: Cardinal;
begin
  if Step <> ssPostInstall then
    Exit;
  Committed := True;
  { Create no uninstall registration before activation. An absent marker is conservative. }
  Marker := 0;
  if OpenBreakOnlyLog then
    Marker := 1;
  if RegKeyExists(HKCU, UninstallKey) and
     not RegWriteDWordValue(HKCU, UninstallKey, '{#FreshLogValue}', Marker) then
    Log('OpenBreak setup could not record the uninstall log origin.');
  RetireLegacyShortcut;
  Present := RegValueExists(HKCU, RunKey, RunName);
  Owned := OwnedStartup(PreviousCommand);
  RegisterStartup := HadInstall and Owned;
  if StartupChanged then
    RegisterStartup := StartupPage.Values[0];
  if RegisterStartup then begin
    if (not Present) or Owned then
      StartupFailed := not RegWriteStringValue(HKCU, RunKey, RunName, QuietStartupCommand(ActiveSlot + '\{#AppExecutable}'))
    else
      StartupFailed := True;
  end else if Owned then
    StartupFailed := not RegDeleteValue(HKCU, RunKey, RunName);
  if StartupFailed then
    SuppressibleMsgBox('OpenBreak was installed. Setup could not update the Windows startup entry.',
      mbError, MB_OK, IDOK);
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if StartupFailed then
    Result := 10
  else if LegacyShortcutCleanupFailed then
    Result := 11;
end;

procedure DeinitializeSetup;
begin
  if not Committed and (ShortcutBackup <> '') then begin
    CheckPathAncestors(StartMenuLink);
    if not PathExistsChecked(StartMenuLink) or
       (CompareText(ReadNormalShortcutTarget(StartMenuLink), ActiveSlot + '\{#AppExecutable}') = 0) then begin
      if not CopyFile(ShortcutBackup, StartMenuLink, False) then
        MsgBox('Setup could not restore the previous OpenBreak Start menu shortcut.', mbError, MB_OK);
    end;
  end;
  if AppMutexHandle <> 0 then
    CloseAppMutex(AppMutexHandle);
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  Command, LegacyLink: String;
begin
  if Step <> usUninstall then
    Exit;
  if CompareText(ExpandConstant('{app}'), RootDirectory) <> 0 then
    RaiseException('OpenBreak could not verify the uninstall location.');
  CheckInstallPaths;
  { An inherited uninstall log can still delete the old link by name. }
  LegacyLink := LegacyStartMenuLink;
  if not HasOpenBreakOnlyLog and PathExistsChecked(LegacyLink) and not IsOwnedLegacyShortcut(LegacyLink) then
    RaiseException('The previous Start menu shortcut name is in use. Move the shortcut and retry.');
  if not AcquireAppMutex then
    RaiseException('Quit OpenBreak from its tray icon, then uninstall again.');
  if OwnedStartup(Command) and not RegDeleteValue(HKCU, RunKey, RunName) then
    RaiseException('OpenBreak could not remove its Windows startup entry.');
end;

procedure DeinitializeUninstall;
begin
  if AppMutexHandle <> 0 then
    CloseAppMutex(AppMutexHandle);
end;
