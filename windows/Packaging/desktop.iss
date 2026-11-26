[Setup]
AppId=AnbarPayroll.Desktop
AppName=Anbar Payroll
AppVersion=0.1.0
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

[Icons]
Name: "{userprograms}\Anbar Payroll"; Filename: "{app}\AnbarPayroll.exe"
