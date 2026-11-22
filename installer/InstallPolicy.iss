const
  QuietStartupSuffix = ' --startup';

type
  TInstallVersion = record
    Major, Minor, Patch: Integer;
  end;

function TryVersionNumber(Value: String; var Number: Integer): Boolean;
var
  Index: Integer;
begin
  Result := False;
  if (Length(Value) = 0) or (Length(Value) > 5) then
    Exit;
  for Index := 1 to Length(Value) do
    if (Value[Index] < '0') or (Value[Index] > '9') then
      Exit;
  Number := StrToInt(Value);
  Result := (Number <= 65535) and (IntToStr(Number) = Value);
end;

function ParseInstallVersion(Value: String; var Version: TInstallVersion): Boolean;
var
  Dot: Integer;
begin
  Result := False;
  Dot := Pos('.', Value);
  if (Dot = 0) or not TryVersionNumber(Copy(Value, 1, Dot - 1), Version.Major) then
    Exit;
  Delete(Value, 1, Dot);
  Dot := Pos('.', Value);
  if (Dot = 0) or not TryVersionNumber(Copy(Value, 1, Dot - 1), Version.Minor) then
    Exit;
  Result := TryVersionNumber(Copy(Value, Dot + 1, Length(Value)), Version.Patch);
end;

function CompareInstallVersions(A, B: TInstallVersion): Integer;
begin
  Result := A.Major - B.Major;
  if Result = 0 then
    Result := A.Minor - B.Minor;
  if Result = 0 then
    Result := A.Patch - B.Patch;
end;

function IsOwnedExecutableCommand(InstallRoot, Command: String): Boolean;
var
  Prefix, Suffix, Relative: String;
  Separator, Slot: Integer;
  Version: TInstallVersion;
begin
  Result := False;
  Prefix := '"' + InstallRoot + '\versions\';
  Suffix := '\Afterbreak.exe"';
  if (CompareText(Copy(Command, 1, Length(Prefix)), Prefix) <> 0) or
     (CompareText(Copy(Command, Length(Command) - Length(Suffix) + 1, Length(Suffix)), Suffix) <> 0) then
    Exit;
  Relative := Copy(Command, Length(Prefix) + 1, Length(Command) - Length(Prefix) - Length(Suffix));
  Separator := Pos('\', Relative);
  if (Separator = 0) or not ParseInstallVersion(Copy(Relative, 1, Separator - 1), Version) then
    Exit;
  Result := TryVersionNumber(Copy(Relative, Separator + 1, Length(Relative)), Slot) and
    (Slot >= 1) and (Slot <= 1000);
end;

function IsOwnedStartupCommand(InstallRoot, Command: String): Boolean;
var
  SuffixStart: Integer;
begin
  SuffixStart := Length(Command) - Length(QuietStartupSuffix) + 1;
  if (SuffixStart > 0) and
     (CompareStr(Copy(Command, SuffixStart, Length(QuietStartupSuffix)), QuietStartupSuffix) = 0) then
    Delete(Command, SuffixStart, Length(QuietStartupSuffix));
  Result := IsOwnedExecutableCommand(InstallRoot, Command);
end;

function QuietStartupCommand(ExecutablePath: String): String;
begin
  Result := '"' + ExecutablePath + '"' + QuietStartupSuffix;
end;
