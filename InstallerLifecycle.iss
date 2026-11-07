{ This pure policy is included by both Setup and the disposable Pascal test.
  Never try a new command on a predecessor that may treat unknown flags as GUI launch. }
{ The pinned Inno Setup 6.7.3 Setup runtime is 32-bit, including x64 installs.
  Cardinal is its pointer-width ABI here; update this binding before changing
  compiler/runtime architecture. version.dll is the Windows system metadata API. }
function InstallerVersionInfoSize(const Filename: String; var Handle: Cardinal): Cardinal;
  external 'GetFileVersionInfoSizeW@version.dll stdcall';
function InstallerVersionInfo(const Filename: String; Handle, Size, Buffer: Cardinal): Boolean;
  external 'GetFileVersionInfoW@version.dll stdcall';
function InstallerVersionValue(Buffer: Cardinal; const SubBlock: String; var Value: Cardinal; var ValueLength: Cardinal): Boolean;
  external 'VerQueryValueW@version.dll stdcall';
function InstallerAllocate(Flags, Bytes: Cardinal): Cardinal;
  external 'LocalAlloc@kernel32.dll stdcall';
function InstallerFree(Buffer: Cardinal): Cardinal;
  external 'LocalFree@kernel32.dll stdcall';
procedure InstallerCopyWord(var Destination: Word; Source, Count: Cardinal);
  external 'RtlMoveMemory@kernel32.dll stdcall';
procedure InstallerCopyString(Destination: String; Source, Count: Cardinal);
  external 'RtlMoveMemory@kernel32.dll stdcall';

function InstallerValueWithinBuffer(Buffer, Size, Value, Bytes: Cardinal): Boolean;
var
  Offset: Cardinal;
begin
  Result := False;
  if (Value = 0) or (Value < Buffer) then
    Exit;
  Offset := Value - Buffer;
  if Offset > Size then
    Exit;
  Result := Bytes <= Size - Offset;
end;

function GetInstallerProductVersion(const Filename: String; var Version: String): Boolean;
var
  Handle, Size, ValueLength, Buffer: Cardinal;
  Value: Cardinal;
  Language, CodePage, Terminator: Word;
  SubBlock: String;
begin
  Result := False;
  Version := '';
  Handle := 0;
  Size := InstallerVersionInfoSize(Filename, Handle);
  if (Size = 0) or (Size > 1048576) then
    Exit;
  Buffer := InstallerAllocate($0040, Size); { LMEM_FIXED | LMEM_ZEROINIT }
  if Buffer = 0 then
    Exit;
  try
    if not InstallerVersionInfo(Filename, 0, Size, Buffer) then
      Exit;
    if not InstallerVersionValue(Buffer, '\VarFileInfo\Translation', Value, ValueLength) then
      Exit;
    { Our .NET compiler emits exactly one language/codepage. Ambiguous metadata
      is refused rather than selecting a different ProductVersion block. }
    if (ValueLength <> 4) or not InstallerValueWithinBuffer(Buffer, Size, Value, 4) then
      Exit;
    InstallerCopyWord(Language, Value, 2);
    InstallerCopyWord(CodePage, Value + 2, 2);
    SubBlock := Format('\StringFileInfo\%.4x%.4x\ProductVersion', [Language, CodePage]);
    if not InstallerVersionValue(Buffer, SubBlock, Value, ValueLength) then
      Exit;
    if (ValueLength < 2) or (ValueLength > 128) then
      Exit;
    if not InstallerValueWithinBuffer(Buffer, Size, Value, ValueLength * 2) then
      Exit;
    InstallerCopyWord(Terminator, Value + (ValueLength - 1) * 2, 2);
    if Terminator <> 0 then
      Exit;
    SetLength(Version, ValueLength - 1);
    InstallerCopyString(Version, Value, (ValueLength - 1) * 2);
    Result := True;
  finally
    if InstallerFree(Buffer) <> 0 then
    begin
      Result := False;
      Version := '';
    end;
  end;
end;

function TryGetInstallerBetaRevision(const Version: String; var Revision: Integer): Boolean;
var
  Suffix: String;
begin
  Result := False;
  Revision := 0;
  if Copy(Version, 1, 11) <> '1.0.0-beta.' then
    Exit;
  Suffix := Copy(Version, 12, Length(Version));
  Revision := StrToIntDef(Suffix, 0);
  Result := (Revision > 0) and (IntToStr(Revision) = Suffix);
end;

function GetInstalledUpgradeArguments(const Version, CandidateVersion: String; var Arguments: String): Boolean;
var
  Revision, CandidateRevision: Integer;
begin
  Result := False;
  Arguments := '';
  if not TryGetInstallerBetaRevision(Version, Revision) then
    Exit;
  if not TryGetInstallerBetaRevision(CandidateVersion, CandidateRevision) then
    Exit;
  { The retained release history starts at beta.2 with the restore command. }
  if Revision < 2 then
    Exit;
  if Revision > CandidateRevision then
    Exit;
  if Revision <= 11 then
    Arguments := '--restore'
  else
    Arguments := '--prepare-upgrade';
  Result := True;
end;

function GetInstalledUninstallArguments(const Version, CandidateVersion: String; var Arguments: String): Boolean;
var
  Revision, CandidateRevision: Integer;
begin
  Result := False;
  Arguments := '';
  if not TryGetInstallerBetaRevision(Version, Revision) then
    Exit;
  if not TryGetInstallerBetaRevision(CandidateVersion, CandidateRevision) then
    Exit;
  if (Revision < 12) or (CandidateRevision < 12) or (Version <> CandidateVersion) then
    Exit;
  Arguments := '--uninstall';
  Result := True;
end;
