; تثبيت مستقل لكل مستخدم؛ لا يلمس SalaryDesktop أو مساحات البيانات.
#ifndef AppVersion
#define AppVersion "0.6.0"
#endif
#ifndef SourceDir
#error SourceDir is required
#endif
#ifndef OutputDir
#define OutputDir "output"
#endif
[Setup]
AppId={{C960A9DD-56DB-4F5D-A55B-49286DD9CC3E}
AppName=Salary Local Preview
AppVersion={#AppVersion}
AppVerName=Salary Local Preview {#AppVersion}
DefaultDirName={localappdata}\Programs\SalaryLocalPreview
DefaultGroupName=Salary Local Preview
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=SalaryLocal-Preview-{#AppVersion}-Setup
UninstallDisplayIcon={app}\SalaryLocal.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
InfoBeforeFile=unsigned-warning.txt
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Salary Local Preview"; Filename: "{app}\SalaryLocal.exe"
; لا تشغيل تلقائياً ولا مفاتيح Run ولا أوامر حذف بيانات عند الإزالة.
