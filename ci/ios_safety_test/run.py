#!/usr/bin/env python3
"""Compile the production Foundation-only rules in an isolated Swift container."""
from pathlib import Path
import subprocess
import shutil
import tempfile

root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix="salary-ios-safety-") as temp:
    directory = Path(temp)
    models = (root / "SalaryApp/Models.swift").read_text()
    retention = models[models.index("nonisolated enum PayrollReplacementError"):models.index("/// شهر رواتب واحد")]
    months = models[models.index("nonisolated enum MonthName"):models.index("extension PayrollMonth {")]
    arabic = (root / "SalaryApp/ArabicText.swift").read_text().split("\nenum Money", 1)[0]
    (directory / "Rules.swift").write_text("import Foundation\n" + retention.replace("nonisolated ", "") + months.replace("nonisolated ", ""))
    (directory / "Arabic.swift").write_text(arabic.replace("nonisolated ", ""))
    (directory / "main.swift").write_text((root / "ci/ios_safety_test/main.swift").read_text())
    if shutil.which("swiftc"):
        subprocess.run(["swiftc", "Arabic.swift", "Rules.swift", "main.swift", "-o", "safety-test"], cwd=directory, check=True)
        subprocess.run([str(directory / "safety-test")], cwd=directory, check=True)
    else:
        subprocess.run(["docker", "run", "--rm", "--network", "none", "-v", f"{directory}:/tests", "-w", "/tests", "swift:6.0", "bash", "-c", "swiftc Arabic.swift Rules.swift main.swift -o safety-test && ./safety-test"], check=True)
