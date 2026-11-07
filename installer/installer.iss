#define AppName "Taskbar Select"
#define AppVersion "1.0.0-beta.18"
#define AppFileVersion "1.0.0.18"
#define AppPublisher "Taskbar Select contributors"
#define AppExeName "TaskbarSelect.exe"
#define RepoRoot AddBackslash(SourcePath) + ".."

[Setup]
SourceDir={#RepoRoot}
AppId={{A446A470-529B-42E0-AD2D-D24B31864DFC}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppFileVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppFileVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription=Taskbar Select Setup
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=dist
OutputBaseFilename=TaskbarSelect-{#AppVersion}-Setup
SetupIconFile=assets\TaskbarSelect.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no

[Files]
Source: "artifacts\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "artifacts\{#AppExeName}"; DestName: "TaskbarSelect.Lifecycle.exe"; Flags: dontcopy
Source: "docs\package\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "RELEASE_NOTES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "TESTING.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "SECURITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "assets\ARTWORK.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; AppUserModelID: "TaskbarSelect.App"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "TaskbarSelect.App"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
#include "InstallerLifecycle.iss"

function StopInstalledApp(): Boolean;
var
  Executable, Version, CheckedArguments: String;
  ResultCode: Integer;
begin
  Result := False;
  Executable := ExpandConstant('{app}\{#AppExeName}');
  if FileExists(Executable) then
  begin
    Result := False;
    if not GetInstallerProductVersion(Executable, Version) then
      Exit;
    { Uninstall cannot extract a trusted current helper. A downgraded, older,
      test or mismatched executable cannot certify removal of saved approval. }
    if not GetInstalledUninstallArguments(Version, '{#AppVersion}', CheckedArguments) then
      Exit;
    Result := Exec(Executable, CheckedArguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  end;
end;

function PrepareInstalledApp(): Boolean;
var
  Executable, Version, Arguments, TrustedCandidate: String;
  ResultCode: Integer;
  HadInstalledApp: Boolean;
begin
  Result := False;
  Executable := ExpandConstant('{app}\{#AppExeName}');
  HadInstalledApp := FileExists(Executable);
  if not HadInstalledApp then
  begin
    { A missing executable is a clean install only when no installer record
      remains here. Never replace a broken installation's recovery files. }
    if FileExists(ExpandConstant('{app}\unins000.exe')) or
      FileExists(ExpandConstant('{app}\unins000.dat')) then
      Exit;
  end
  else
  begin
    if not GetInstallerProductVersion(Executable, Version) then
      Exit;
    if not GetInstalledUpgradeArguments(Version, '{#AppVersion}', Arguments) then
      Exit;
  end;
  { A portable production build may own saved approval even when the installed
    version is beta.11 or absent. Always check using this trusted package's
    current lifecycle implementation before calling legacy recovery or copying. }
  ExtractTemporaryFile('TaskbarSelect.Lifecycle.exe');
  TrustedCandidate := ExpandConstant('{tmp}\TaskbarSelect.Lifecycle.exe');
  if not Exec(TrustedCandidate, '--prepare-upgrade', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Exit;
  if FileExists(Executable) <> HadInstalledApp then
    Exit;
  if not HadInstalledApp then
  begin
    Result := True;
    Exit;
  end;
  { Beta.11 and earlier have no remembered registration and support --restore.
    Beta.12+ must confirm elevated cleanup before Setup replaces any file.
    Declined UAC, pending recovery, unknown versions and nonzero exits block. }
  Result := Exec(Executable, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if PrepareInstalledApp() then
    Result := ''
  else
    Result := 'Taskbar Select could not safely prepare the installed version for upgrade. Its files and settings have not been replaced. Open the installed app''s Settings, turn Administrator support off, approve Windows permission there if requested, and wait until it is fully off before retrying. If recovery is pending, finish recovery first. A newer or unrecognized installed version cannot be replaced by this installer.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopInstalledApp();
  if not Result then
  begin
    if not FileExists(ExpandConstant('{app}\{#AppExeName}')) then
      SuppressibleMsgBox('Taskbar Select cannot be uninstalled because its recovery executable is missing. Restore the verified matching TaskbarSelect.exe from that version''s official portable archive to ' + ExpandConstant('{app}') + ', then retry uninstalling. Do not delete settings or administrator recovery files manually.', mbError, MB_OK, IDOK)
    else
      SuppressibleMsgBox('Taskbar Select could not confirm recovery and administrator-support removal. Its files and settings have been retained. Open the app''s Settings, turn Administrator support off, approve Windows permission there if requested, and wait until it is fully off before retrying uninstall. If recovery is pending, finish recovery first. If the executable was replaced with another version, restore the verified matching executable from this version''s official portable archive.', mbError, MB_OK, IDOK);
  end;
end;
