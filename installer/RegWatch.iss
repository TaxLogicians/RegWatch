; RegWatch installer (Inno Setup 6). Build with installer\build.ps1, which publishes the app first.
; The app goes in Program Files (or the user's own Programs folder); the data folder
; (%USERPROFILE%\RegWatch: PROFILE.md, digests, STATE.json) is separate and never touched here.

#ifndef AppVersion
  #define AppVersion "0.1.0-alpha"
#endif
; Windows file properties need a plain number; the label (-alpha) stays in AppVersion.
#ifndef NumericVersion
  #define NumericVersion "0.1.0"
#endif
#define AppName "RegWatch"
#define AppExe "RegWatch.App.exe"
#define TaskName "RegWatch weekly digest"

[Setup]
AppId={{68EDCDEA-5CED-4912-BA71-A722AA8A1565}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=Maubec-Deeds
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without an admin prompt; the first page offers all users instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile=..\src\RegWatch.App\Assets\RegWatch.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=Output
OutputBaseFilename=RegWatch-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Runs a command through cmd.exe and returns its output (empty if it failed).
function CommandOutput(const Command: String): String;
var
  OutFile: String;
  Output: AnsiString;
  ResultCode: Integer;
begin
  Result := '';
  OutFile := ExpandConstant('{tmp}\regwatch-cmd.txt');
  if Exec(ExpandConstant('{cmd}'), '/C ' + Command + ' > "' + OutFile + '" 2>nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     and (ResultCode = 0) and LoadStringFromFile(OutFile, Output) then
    Result := String(Output);
  DeleteFile(OutFile);
end;

function RuntimeIn(const DotnetDir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := (DotnetDir <> '') and FindFirst(AddBackslash(DotnetDir) + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec);
  if Result then FindClose(FindRec);
end;

// RegWatch is framework-dependent: it needs the .NET 10 Desktop Runtime (x64), wherever it is installed.
function DesktopRuntimeInstalled: Boolean;
begin
  Result := RuntimeIn(ExpandConstant('{commonpf64}\dotnet'))
    or RuntimeIn(GetEnv('DOTNET_ROOT'))
    or RuntimeIn(ExpandConstant('{localappdata}\Microsoft\dotnet'))
    or (Pos('Microsoft.WindowsDesktop.App 10.', CommandOutput('dotnet --list-runtimes')) > 0);
end;

// Removes the task the app's Schedule weekly button creates, but only when it runs this
// install's runner, so a task made from another copy of RegWatch is left alone.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Task: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Task := CommandOutput('schtasks /Query /TN "{#TaskName}" /V /FO LIST');
  if Pos(Lowercase(ExpandConstant('{app}\RegWatch.Runner.exe')), Lowercase(Task)) > 0 then
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "{#TaskName}" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not DesktopRuntimeInstalled then
  begin
    if MsgBox('RegWatch needs the .NET 10 Desktop Runtime (x64), which is not installed.' + #13#10#13#10 +
              'Open the Microsoft download page now? Run this setup again after installing it.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;
