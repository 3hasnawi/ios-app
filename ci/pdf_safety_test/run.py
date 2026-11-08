#!/usr/bin/env python3
"""Exercise the production PDF replacement flow with CoreGraphics on macOS."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = (root / "SalaryApp/PDFDecor.swift").read_text()
start = source.index("    static let footerText")
end = source.index("    /// \"٢٠٢٦/", start)
production = source[start:end]
stubs = '''import CoreGraphics
import Foundation
func tr(_ ar: String, _ en: String) -> String { ar }
enum PDFDecor {
''' + production + '''
    static func stampText(_ date: Date) -> String { "" }
    static func drawFooter(in box: CGRect, page: Int, of pages: Int, stamp: String, footer: String, context: CGContext) { }
}
'''
with tempfile.TemporaryDirectory(prefix="salary-pdf-check-") as temp:
    directory = Path(temp)
    (directory / "PDFDecor.swift").write_text(stubs)
    (directory / "main.swift").write_text((root / "ci/pdf_safety_test/main.swift").read_text())
    subprocess.run(["swiftc", "PDFDecor.swift", "main.swift", "-o", "pdf-test"], cwd=directory, check=True)
    subprocess.run([str(directory / "pdf-test")], cwd=directory, check=True)
