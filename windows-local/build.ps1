param([switch]$Portable)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build windows-local/SalaryLocal.App/SalaryLocal.App.csproj -c Release --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    dotnet run --project windows-local/SalaryLocal.Tests/SalaryLocal.Tests.csproj -c Release -p:BuildInParallel=false
    if ($LASTEXITCODE -ne 0) { throw 'Safety tests failed.' }
    if ($Portable) {
        dotnet publish windows-local/SalaryLocal.App/SalaryLocal.App.csproj -c Release -r win-x64 --self-contained true --nologo -m:1 -o windows-local/artifacts/SalaryLocal-win-x64-0.6-test
        if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
        Copy-Item windows-local/README.ar.md windows-local/artifacts/SalaryLocal-win-x64-0.6-test/اقرأني.md
        Copy-Item windows-local/verification/RESULTS.ar.md windows-local/artifacts/SalaryLocal-win-x64-0.6-test/نتائج-الاختبار.md
    }
} finally { Pop-Location }
