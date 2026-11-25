"""Exercise production monthly totals, and render the printed form on macOS when requested."""
from pathlib import Path
from tempfile import TemporaryDirectory
import argparse
import subprocess

root = Path(__file__).resolve().parents[2]
app = root / "SalaryApp"
parser = argparse.ArgumentParser()
parser.add_argument("--preview-dir", type=Path)
options = parser.parse_args()

def section(file, start, end):
    text = (app / file).read_text()
    return text[text.index(start):text.index(end, text.index(start))]

with TemporaryDirectory() as tmp:
    work = Path(tmp)
    shared = '''import Foundation
func tr(_ a: String, _ e: String) -> String { a }
enum AppSettings { static let locale = Locale(identifier: "ar_IQ") }
enum Num { static func format(_ n: Int) -> String { String(n) } }
'''
    shared += section("Models.swift", "nonisolated struct PayrollColumnSnapshot", "nonisolated enum PayrollItems")
    shared += section("TransferLetterFigures.swift", "nonisolated enum TransferLetterFigureError", "nonisolated enum TransferLetterFigureRules")
    files = [app / "CommandDeductions.swift", app / "ArabicText.swift"]
    main = (Path(__file__).parent / "main.swift").read_text()
    if options.preview_dir:
        shared += "\nimport AppKit\nimport CoreText\ntypealias UIFont = NSFont\n"
        shared += "extension NSFont { var lineHeight: CGFloat { ascender - descender + leading } }\n"
        shared += '''enum ReportFont {
            static func bold(_ s: CGFloat) -> NSFont { NSFont(name: "Arial-BoldMT", size: s)! }
        }
'''
        shared += section("PDFText.swift", "nonisolated enum PDFText {", "/// تجهيز ملفات التصدير")
        shared += "enum ReportSheet {\n" + section("ReportExport.swift", "    static func easternDigits(", "    static var defaultHeader") + "}\n"
        shared += section("FundReport.swift", "nonisolated enum FundReport", "    struct Row:") + "}\n"
        shared += section("HealthCareReport.swift", "nonisolated enum HealthCareReport", "    struct Lists:") + "}\n"
        (work / "CommandPDF.swift").write_text((app / "CommandDeductionPDF.swift").read_text().replace("import UIKit", "import AppKit"))
        files += [work / "CommandPDF.swift", app / "ReportTableStyle.swift", root / "ci/report_layout_test/UIKitPreviewStubs.swift"]
        main += (Path(__file__).parent / "preview.swift").read_text()
        options.preview_dir.mkdir(parents=True, exist_ok=True)
    (work / "Support.swift").write_text(shared)
    (work / "main.swift").write_text(main)
    binary = work / "verify"
    subprocess.run(["swiftc", *files, work / "Support.swift", work / "main.swift", "-o", binary], check=True)
    subprocess.run([binary] + ([options.preview_dir.resolve()] if options.preview_dir else []), check=True)
