; ملف تثبيت واحد لنسخة ويندوز من تطبيق الرواتب (Inno Setup 6.5+).
; يُبنى في windows-check.yml:  ISCC /DAppVersion=1.0.N /DSourceDir=<مجلد النشر> /DOutputDir=<مجلد الإخراج> SalaryDesktop.iss
; التثبيت لحساب المستخدم الحالي دون صلاحيات مسؤول (يمكن اختيار كل المستخدمين)، وإلغاء التثبيت لا يحذف البيانات المحلية.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\SalaryDesktop\bin\Release\publish"
#endif
#ifndef OutputDir
  #define OutputDir "output"
#endif

[Setup]
AppId={{6E4B7C1A-2D5F-4F0B-9C3E-8A1D2B7E5F40}
AppName=تطبيق الرواتب
AppVersion={#AppVersion}
AppVerName=تطبيق الرواتب {#AppVersion}
AppPublisher=قيادة شرطة محافظة الانبار - قسم الحسابات
AppPublisherURL=https://github.com/3hasnawi/ios-app
AppSupportURL=https://github.com/3hasnawi/ios-app/releases/tag/windows-latest
AppUpdatesURL=https://github.com/3hasnawi/ios-app/releases/tag/windows-latest
VersionInfoVersion={#AppVersion}
VersionInfoProductName=تطبيق الرواتب
DefaultDirName={autopf}\SalaryDesktop
DefaultGroupName=تطبيق الرواتب
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=SalaryDesktop-Setup
SetupIconFile=..\SalaryDesktop\Assets\AppIcon.ico
UninstallDisplayIcon={app}\SalaryDesktop.exe
UninstallDisplayName=تطبيق الرواتب
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "arabic"; MessagesFile: "Arabic.isl"

[CustomMessages]
arabic.DesktopIcon=إنشاء اختصار على سطح المكتب
arabic.LaunchApp=تشغيل تطبيق الرواتب الآن
arabic.DataKept=بيانات البرنامج المحلية (النسخ والتعديلات والإعدادات) محفوظة في مجلد حسابك ولا تُحذف عند إلغاء التثبيت.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\تطبيق الرواتب"; Filename: "{app}\SalaryDesktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\تطبيق الرواتب"; Filename: "{app}\SalaryDesktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\SalaryDesktop.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox(CustomMessage('DataKept'), mbInformation, MB_OK);
end;
