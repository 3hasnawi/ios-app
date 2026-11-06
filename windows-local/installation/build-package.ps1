param([switch]$Installer, [string]$IsccPath = 'ISCC.exe', [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$localRoot = Split-Path -Parent $PSScriptRoot
if ($env:OS -ne 'Windows_NT') { throw 'Run packaging locally on Windows. No remote builds are started.' }
[xml]$project = Get-Content (Join-Path $localRoot 'SalaryLocal.App/SalaryLocal.App.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Project Version must be a three-part numeric value.' }
$release = Join-Path $localRoot ('artifacts/package-' + $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$publish = Join-Path $release 'SalaryLocalPreview'
New-Item -ItemType Directory -Path $release | Out-Null
try {
    & $Dotnet run --project (Join-Path $localRoot 'SalaryLocal.Tests/SalaryLocal.Tests.csproj') -c Release -p:BuildInParallel=false
    if ($LASTEXITCODE -ne 0) { throw 'Safety tests failed; no package produced.' }
    & $Dotnet publish (Join-Path $localRoot 'SalaryLocal.App/SalaryLocal.App.csproj') -c Release -r win-x64 --self-contained true --nologo -m:1 -p:PublishSingleFile=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed; no package produced.' }
    if (!(Test-Path (Join-Path $publish 'SalaryLocal.exe'))) { throw 'Missing application executable.' }
    Copy-Item (Join-Path $PSScriptRoot 'unsigned-warning.txt') $publish
    Copy-Item (Join-Path $PSScriptRoot 'README.ar.md') (Join-Path $publish 'دليل-التثبيت-والترحيل.md')
    Copy-Item (Join-Path $localRoot 'README.ar.md') (Join-Path $publish 'اقرأني.md')
    Copy-Item (Join-Path $localRoot 'verification/RESULTS.ar.md') (Join-Path $publish 'نتائج-الاختبار.md')
    Copy-Item (Join-Path $localRoot 'SalaryLocal.App/INTEGRATION.md') (Join-Path $publish 'فحص-Windows.md')
    $zip = Join-Path $release ('SalaryLocal-Preview-' + $version + '-win-x64.zip')
    Compress-Archive -Path $publish -DestinationPath $zip -CompressionLevel Optimal
    if ($Installer) {
        & $IsccPath "/DAppVersion=$version" "/DSourceDir=$publish" "/DOutputDir=$release" (Join-Path $PSScriptRoot 'SalaryLocal.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
        if (!(Test-Path (Join-Path $release "SalaryLocal-Preview-$version-Setup.exe"))) { throw 'Installer output missing.' }
    }
    Get-ChildItem $release -File | Where-Object { $_.Extension -in '.zip','.exe' } | Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + (Split-Path $_.Path -Leaf) } | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Unsigned test package: $release"
    Write-Host 'Windows installation, launch, upgrade, uninstallation, and two-computer restore still require manual verification.'
} catch { Write-Error "Package incomplete in $release. Do not distribute: $_"; throw }
