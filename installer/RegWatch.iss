; RegWatch installer (Inno Setup 6). Build with installer\build.ps1, which publishes the app first.
; The app goes in Program Files (or the user's own Programs folder); the data folder
; (%USERPROFILE%\RegWatch: PROFILE.md, digests, STATE.json) is separate and never touched here.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "RegWatch"
#define AppExe "RegWatch.App.exe"
#define TaskName "RegWatch weekly digest"

[Setup]
AppId={{68EDCDEA-5CED-4912-BA71-A722AA8A1565}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
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

[UninstallRun]
; Removes the task the app's Schedule weekly button creates; harmless if it was never created.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#TaskName}"" /F"; Flags: runhidden; RunOnceId: "DeleteWeeklyTask"

[Code]
// RegWatch is framework-dependent: it needs the .NET 10 Desktop Runtime (x64).
function DesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then FindClose(FindRec);
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
