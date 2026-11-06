"""فحص ثابت فقط؛ لا يشغّل مثبّتاً ولا يثبت سلامة التشغيل على Windows."""
from pathlib import Path
import re

root = Path(__file__).resolve().parent
iss = (root / "SalaryLocal.iss").read_text(encoding="utf-8")
script = (root / "build-package.ps1").read_text(encoding="utf-8")
assert "C960A9DD-56DB-4F5D-A55B-49286DD9CC3E" in iss
assert "6E4B7C1A-2D5F-4F0B-9C3E-8A1D2B7E5F40" not in iss
assert r"DefaultDirName={localappdata}\Programs\SalaryLocalPreview" in iss
assert "PrivilegesRequired=lowest" in iss
assert "PrivilegesRequiredOverridesAllowed" not in iss
for section in ["Run", "UninstallRun", "UninstallDelete", "InstallDelete", "Registry", "Code"]:
    assert not re.search(r"^\[" + section + r"\]", iss, re.M | re.I), section
assert "RestartApplications=no" in iss
assert "InfoBeforeFile=unsigned-warning.txt" in iss
assert "$env:OS -ne 'Windows_NT'" in script
assert "--self-contained true" in script and "Get-FileHash -Algorithm SHA256" in script
assert script.index("Safety tests failed") < script.index("dotnet publish") if "dotnet publish" in script else script.index("Safety tests failed") < script.index("& $Dotnet publish")
print("Package static guard checks passed. Windows install/run/uninstall not tested.")
