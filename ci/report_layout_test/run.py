#!/usr/bin/env python3
"""Test the production CoreText layout algorithm on macOS without an iOS build or simulator."""
from pathlib import Path
import subprocess
import argparse
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument("--preview-dir", type=Path)
options = parser.parse_args()
root = Path(__file__).resolve().parents[2]
entity = (root / "SalaryApp/EntityDeductionPDF.swift").read_text()
if not options.preview_dir:
    entity = entity[:entity.index("    static func render(")] + "}\n"
entity = entity.replace("import UIKit", "import AppKit").replace("nonisolated ", "")
report = (root / "SalaryApp/ReportExport.swift").read_text()
start = report.index("    static func columnWidths(")
end = report.index("        return widths\n    }", start) + len("        return widths\n    }")
widths = "import AppKit\nimport Foundation\nenum PDFReportWriter {\n" + report[start:end] + "\n}\n"
pdf_text = (root / "SalaryApp/PDFText.swift").read_text()
start = pdf_text.index("    static let rtl:")
end = pdf_text.index("    /// يرسم", start)
rtl = pdf_text[start:end]
fitted = report[report.index("    func fitted() -> ReportSheet"):report.index('    /// "ايلول 2026"')]
digits = report[report.index("    static func easternDigits("):report.index("    static var defaultHeader")]
stubs = '''import AppKit
import CoreText
import Foundation
func tr(_ ar: String, _ en: String) -> String { ar }
typealias UIFont = NSFont
extension NSFont { var lineHeight: CGFloat { ascender - descender + leading } }
enum ReportFont {
    static func regular(_ size: CGFloat) -> NSFont { NSFont(name: "ArialMT", size: size) ?? NSFont.systemFont(ofSize: size) }
    static func bold(_ size: CGFloat) -> NSFont { NSFont(name: "Arial-BoldMT", size: size) ?? NSFont.boldSystemFont(ofSize: size) }
}
struct ReportSheet {
    enum Kind { case serial, text, amount }
    struct Column { let title: String; let width: Double; let kind: Kind }
    let header: String
    let title: String
    let columns: [Column]
    var rows: [[String]]
    let amounts: [Double]
    var showTotal = true
    var entityDeduction = false
    var deductionList = false
    var total: Double { amounts.reduce(0, +) }
    var amountColumn: Int { columns.firstIndex { $0.kind == .amount } ?? 0 }
    var landscape: Bool { !entityDeduction && columns.reduce(0) { $0 + $1.width } > 96 }
''' + fitted + digits + "}\n"
if options.preview_dir:
    stubs += pdf_text[:pdf_text.index("/// تجهيز ملفات التصدير")].replace("import UIKit", "import AppKit").replace("nonisolated ", "")
else:
    stubs += "enum PDFText {\n" + rtl + "}\n"
with tempfile.TemporaryDirectory(prefix="salary-report-layout-") as temp:
    directory = Path(temp)
    (directory / "Entity.swift").write_text(entity)
    (directory / "TableStyle.swift").write_text((root / "SalaryApp/ReportTableStyle.swift").read_text().replace("nonisolated ", ""))
    health = (root / "SalaryApp/HealthCarePDFLayout.swift").read_text().replace("import UIKit", "import AppKit").replace("nonisolated ", "")
    (directory / "Health.swift").write_text(health)
    (directory / "Widths.swift").write_text(widths)
    (directory / "Stubs.swift").write_text(stubs)
    main = (root / "ci/report_layout_test/main.swift").read_text()
    extra = []
    arguments = []
    if options.preview_dir:
        main += "\n" + (root / "ci/report_layout_test/preview.swift").read_text()
        (directory / "UIKitPreviewStubs.swift").write_text((root / "ci/report_layout_test/UIKitPreviewStubs.swift").read_text())
        decor = (root / "SalaryApp/PDFDecor.swift").read_text().replace("import UIKit", "import AppKit").replace("nonisolated ", "")
        (directory / "Decor.swift").write_text(decor)
        extra = ["UIKitPreviewStubs.swift", "Decor.swift"]
        arguments = [str(options.preview_dir.resolve())]
    (directory / "main.swift").write_text(main)
    subprocess.run(["swiftc", "Stubs.swift", "Widths.swift", "TableStyle.swift", "Entity.swift", "Health.swift", *extra, "main.swift", "-o", "layout-test"], cwd=directory, check=True)
    subprocess.run([str(directory / "layout-test"), *arguments], cwd=directory, check=True)
