param([string]$Version = '0.1.0', [string]$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (-not (Test-Path -LiteralPath $Iscc)) { throw "Inno Setup 6 not found: $Iscc" }
$publish = Join-Path $projectRoot 'Payroll.Desktop\publish\win-x64'
$output = Join-Path $projectRoot 'installer\output'
dotnet publish (Join-Path $projectRoot 'Payroll.Desktop\Payroll.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $publish "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& $Iscc /Q "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$output" (Join-Path $projectRoot 'installer\PayrollSetup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer creation failed.' }
Write-Host "Installer: $(Join-Path $output 'PayrollWindows-Setup.exe')"
