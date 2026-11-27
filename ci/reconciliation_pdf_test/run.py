"""Render the iPhone bank statement with Core Text and verify real PDF pagination on macOS."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
app = root / "SalaryApp"

def source(file):
    return (app / file).read_text()

def section(file, start, end):
    code = source(file).split(start, 1)[1].split(end, 1)[0]
    return start + code

with tempfile.TemporaryDirectory() as tmp:
    folder = Path(tmp)
    bank = source("BankReconciliation.swift")
    bank = bank[:bank.index("    static func groupSuggestions")] + bank[bank.index("    struct Line:"):]
    pdf = source("BankReconciliationPDF.swift").replace("try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: destination.path)", "")
    support = """
import AppKit
func tr(_ ar: String, _ en: String) -> String { ar }
enum Num { static func digits(_ text: String) -> String { text } }
"""
    support += source("ArabicText.swift").split("enum Money")[0]
    support += section("Models.swift", "nonisolated enum MonthName {", "\n}") + "\n}\n"
    support += "enum ReportSheet {\n" + section("ReportExport.swift", "    static func easternDigits(", "    static var defaultHeader") + "}\n"
    support += "enum TransferWordValues { enum Failure: Error { case invalidAmount }\n" + section("TransferWordValues.swift", "    static func groupedAmount(", "    /// تُطلب") + "}\n"
    support += "enum TransferAnbarTablePDF {\n" + section("TransferAnbarTablePDF.swift", "    static func matchesExtractedCell", "    static func make(") + "}\n"
    renderer = folder / "Renderer.swift"
    renderer.write_text("\n".join([support, bank, pdf, source("ReportFont.swift"), source("PDFText.swift"), source("ReportTableStyle.swift")]).replace("import UIKit", "import AppKit"))
    binary = folder / "check"
    subprocess.run(["swiftc", "-parse-as-library", str(renderer), str(root / "ci/report_layout_test/UIKitPreviewStubs.swift"),
                    str(root / "ci/employee_refund_test/MacPDF.swift"), str(Path(__file__).with_name("Check.swift")), "-o", str(binary)], check=True)
    subprocess.run([str(binary)], check=True)
