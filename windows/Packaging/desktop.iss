[Setup]
AppId=AnbarPayroll.Desktop
AppName=Anbar Payroll
AppVersion={#DesktopVersion}
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
Source: "{#WebBootstrapper}"; DestName: "MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
function HasRuntime(Root: Integer): Boolean;
var Version: String;
begin
  Result := RegQueryStringValue(Root, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function RuntimeInstalled(): Boolean;
begin
  Result := HasRuntime(HKLM32) or HasRuntime(HKCU32);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ExitCode: Integer;
begin
  Result := '';
  if RuntimeInstalled() then
  begin
    Log('WebView2 already present; skipping bootstrapper.');
    Exit;
  end;

  Log('WebView2 missing; running Microsoft bootstrapper.');
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '',
              SW_HIDE, ewWaitUntilTerminated, ExitCode) or not RuntimeInstalled() then
  begin
    Log(Format('WebView2 prerequisite failed (%d).', [ExitCode]));
    Result := 'تعذّر تثبيت Microsoft Edge WebView2 اللازم لمعاينة الكشوف. تحقق من اتصال الإنترنت ثم أعد المحاولة.';
  end;
end;

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Icons]
Name: "{userdesktop}\Anbar Payroll"; Filename: "{app}\AnbarPayroll.exe"; Tasks: desktopicon
Name: "{userprograms}\Anbar Payroll"; Filename: "{app}\AnbarPayroll.exe"
