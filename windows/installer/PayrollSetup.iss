#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#ifndef SourceDir
#define SourceDir "..\Payroll.Desktop\publish\win-x64"
#endif
#ifndef OutputDir
#define OutputDir "output"
#endif
[Setup]
AppId={{DD426E4E-701B-48CE-B1FB-76B0D3B3B4D9}
AppName=Payroll Desktop
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\IraqiPayrollNew
DefaultGroupName=Payroll Desktop
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=PayrollWindows-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Payroll.Desktop.exe
CloseApplications=yes
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{autoprograms}\Payroll Desktop"; Filename: "{app}\Payroll.Desktop.exe"
