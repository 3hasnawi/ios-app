[Setup]
AppId=AnbarPayroll.Desktop
AppName=Anbar Payroll
AppVersion=0.2.4
SetupIconFile=..\Desktop\AppIcon.ico
DefaultDirName={localappdata}\Programs\AnbarPayroll
DefaultGroupName=Anbar Payroll
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/fast
SolidCompression=yes
OutputDir={#InstallerDir}
OutputBaseFilename=AnbarPayroll-Setup
UninstallDisplayIcon={app}\AnbarPayroll.exe
WizardStyle=modern

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

Source: "{#WebRuntime}"; DestName: "WebView2.exe"; DestDir: "{tmp}"; Flags: nocompression deleteafterinstall; Check: NeedsRuntime

[Code]
function HasRuntime(Root: Integer): Boolean;
var Version: String;
begin
  Result := RegQueryStringValue(Root, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function NeedsRuntime(): Boolean;
begin
  Result := not (HasRuntime(HKLM32) or HasRuntime(HKCU));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var ExitCode: Integer;
begin
  if (CurStep = ssPostInstall) and NeedsRuntime() then
    if not Exec(ExpandConstant('{tmp}\WebView2.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or NeedsRuntime() then
      RaiseException('Microsoft Edge WebView2 Runtime installation failed.');
end;

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Icons]
Name: "{userdesktop}\Anbar Payroll"; Filename: "{app}\AnbarPayroll.exe"; Tasks: desktopicon
Name: "{userprograms}\Anbar Payroll"; Filename: "{app}\AnbarPayroll.exe"
