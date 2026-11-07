[Setup]
AppId=TaskbarSelectOwnedLifecyclePolicyFixture
AppName=Taskbar Select owned lifecycle policy fixture
AppVersion=1
DefaultDirName={tmp}\TaskbarSelectOwnedLifecyclePolicyFixture
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputBaseFilename=InstallerLifecyclePolicyFixture

[Code]
#include "..\..\InstallerLifecycle.iss"

procedure ExitProcess(Code: Integer);
  external 'ExitProcess@kernel32.dll stdcall';

var
  Checks, UninstallChecks: Integer;

procedure CheckPolicy(const Version, CandidateVersion, Expected: String; Accepted: Boolean);
var
  Arguments: String;
  Actual: Boolean;
begin
  Arguments := 'uninitialized';
  Actual := GetInstalledUpgradeArguments(Version, CandidateVersion, Arguments);
  if (Actual <> Accepted) or (Arguments <> Expected) then
  begin
    Log('FAIL: actual installer lifecycle policy [' + Version + '] [' + CandidateVersion + ']');
    ExitProcess(20);
  end;
  Checks := Checks + 1;
end;

procedure CheckUninstallPolicy(const Version, CandidateVersion, Expected: String; Accepted: Boolean);
var
  Arguments: String;
begin
  if (GetInstalledUninstallArguments(Version, CandidateVersion, Arguments) <> Accepted) or (Arguments <> Expected) then
  begin
    Log('FAIL: actual uninstaller lifecycle policy [' + Version + '] [' + CandidateVersion + ']');
    ExitProcess(23);
  end;
  UninstallChecks := UninstallChecks + 1;
end;

procedure CheckMetadata(const Filename, Expected: String; Accepted: Boolean);
var
  Version: String;
  Actual: Boolean;
begin
  Actual := GetInstallerProductVersion(Filename, Version);
  if (Actual <> Accepted) or (Version <> Expected) then
  begin
    Log('FAIL: installed ProductVersion metadata [' + Filename + ']');
    if Actual then
      Log('Owned metadata read succeeded=1; actual length=' + IntToStr(Length(Version)) + '; expected length=' + IntToStr(Length(Expected)))
    else
      Log('Owned metadata read succeeded=0; actual length=' + IntToStr(Length(Version)) + '; expected length=' + IntToStr(Length(Expected)));
    if (Length(Version) > 0) and (Length(Expected) > 0) then
      Log('Owned metadata last code: actual=' + IntToStr(Ord(Version[Length(Version)])) + '; expected=' + IntToStr(Ord(Expected[Length(Expected)])));
    ExitProcess(21);
  end;
end;

function InitializeSetup(): Boolean;
var
  Revision: Integer;
  MetadataDirectory: String;
begin
  Checks := 0;
  for Revision := 2 to 11 do
    CheckPolicy('1.0.0-beta.' + IntToStr(Revision), '1.0.0-beta.12', '--restore', True);
  CheckPolicy('1.0.0-beta.1', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.12', '1.0.0-beta.12', '--prepare-upgrade', True);
  CheckPolicy('1.0.0-beta.12', '1.0.0-beta.13', '--prepare-upgrade', True);
  CheckPolicy('1.0.0-beta.13', '1.0.0-beta.13', '--prepare-upgrade', True);
  CheckPolicy('1.0.0-beta.13', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.13', '1.0.0-beta.14', '--prepare-upgrade', True);
  CheckPolicy('1.0.0-beta.14', '1.0.0-beta.14', '--prepare-upgrade', True);
  CheckPolicy('1.0.0-beta.14', '1.0.0-beta.13', '', False);
  CheckPolicy('1.0.0-beta.14-test', '1.0.0-beta.14', '', False);
  CheckPolicy('1.0.0-beta.12', '1.0.0-beta.11', '', False);
  CheckPolicy('', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0', '1.0.0-beta.12', '', False);
  CheckPolicy('2.0.0-beta.1', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.0', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.-1', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.012', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.+12', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.12 ', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.12-persistent-test', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.7-fullscreen-test', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.2147483648', '1.0.0-beta.12', '', False);
  CheckPolicy('1.0.0-beta.12', '1.0.0-beta.12-persistent-test', '', False);
  CheckPolicy('1.0.0-beta.11', '2.0.0-beta.12', '', False);
  Log('PASS: ' + IntToStr(Checks) + ' actual Pascal installer lifecycle policy cases. No app, profile, task or installation was accessed.');
  UninstallChecks := 0;
  CheckUninstallPolicy('1.0.0-beta.12', '1.0.0-beta.12', '--uninstall', True);
  CheckUninstallPolicy('1.0.0-beta.13', '1.0.0-beta.13', '--uninstall', True);
  CheckUninstallPolicy('1.0.0-beta.13-persistent-test', '1.0.0-beta.13', '', False);
  CheckUninstallPolicy('1.0.0-beta.13', '1.0.0-beta.13-persistent-test', '', False);
  CheckUninstallPolicy('1.0.0-beta.12', '1.0.0-beta.13', '', False);
  CheckUninstallPolicy('1.0.0-beta.13', '1.0.0-beta.12', '', False);
  CheckUninstallPolicy('1.0.0-beta.14', '1.0.0-beta.14', '--uninstall', True);
  CheckUninstallPolicy('1.0.0-beta.13', '1.0.0-beta.14', '', False);
  CheckUninstallPolicy('1.0.0-beta.14', '1.0.0-beta.13', '', False);
  CheckUninstallPolicy('1.0.0-beta.14-test', '1.0.0-beta.14', '', False);
  CheckUninstallPolicy('1.0.0-beta.11', '1.0.0-beta.12', '', False);
  CheckUninstallPolicy('1.0.0-beta.12', '1.0.0-beta.11', '', False);
  CheckUninstallPolicy('1.0.0-beta.12-persistent-test', '1.0.0-beta.12', '', False);
  CheckUninstallPolicy('1.0.0-beta.12', '1.0.0-beta.12-persistent-test', '', False);
  CheckUninstallPolicy('1.0.0-beta.012', '1.0.0-beta.12', '', False);
  CheckUninstallPolicy('', '1.0.0-beta.12', '', False);
  CheckUninstallPolicy('1.0.0-beta.12', '', '', False);
  Log('PASS: ' + IntToStr(UninstallChecks) + ' actual Pascal uninstaller lifecycle policy cases. Downgraded and mismatched executables cannot certify cleanup.');
  if not InstallerValueWithinBuffer(100, 10, 100, 10) or
    not InstallerValueWithinBuffer(100, 10, 108, 2) or
    InstallerValueWithinBuffer(100, 10, 99, 1) or
    InstallerValueWithinBuffer(100, 10, 108, 3) or
    InstallerValueWithinBuffer(100, 10, 111, 0) or
    InstallerValueWithinBuffer(100, 10, 0, 1) or
    InstallerValueWithinBuffer(100, 10, $FFFFFFFF, 2) then
    ExitProcess(24);
  Log('PASS: 7 actual returned-pointer bounds cases. Out-of-range and wraparound pointers are refused.');
  MetadataDirectory := ExpandConstant('{param:MetadataDir|}');
  if MetadataDirectory = '' then
    ExitProcess(22);
  { The pinned Inno stub reserves a 50-character ProductVersion string. Keep
    every padding byte in this expectation; production parsing stays strict. }
  CheckMetadata(ExpandConstant('{srcexe}'), '1' + StringOfChar(' ', 49), True);
  CheckMetadata(MetadataDirectory + '\legacy.dll', '1.0.0-beta.11', True);
  CheckMetadata(MetadataDirectory + '\production.dll', '1.0.0-beta.12', True);
  CheckMetadata(MetadataDirectory + '\persistent-test.dll', '1.0.0-beta.12-persistent-test', True);
  CheckMetadata(MetadataDirectory + '\malformed.dll', '1.0.0-beta.012', True);
  { Chr casts to AnsiChar in this pinned Pascal Script runtime. A Unicode
    character constant preserves U+96EA for the independent expected value. }
  CheckMetadata(MetadataDirectory + '\unicode.dll', '1.0.0-beta.12-' + #38634, True);
  CheckMetadata(MetadataDirectory + '\oversize.dll', '', False);
  CheckMetadata(MetadataDirectory + '\non-pe.txt', '', False);
  CheckMetadata(MetadataDirectory + '\missing.dll', '', False);
  Log('PASS: 9 actual ProductVersion metadata cases. No numeric-version fallback or predecessor execution.');
  { Exit before any wizard, installation registration or application action. }
  ExitProcess(0);
  Result := False;
end;
