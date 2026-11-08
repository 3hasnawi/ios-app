#!/usr/bin/env python3
"""Test the production CoreText layout algorithm on macOS without an iOS build or simulator."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
entity = (root / "SalaryApp/EntityDeductionPDF.swift").read_text()
entity = entity[:entity.index("    static func render(")].replace("import UIKit", "import AppKit").replace("nonisolated ", "") + "}\n"
report = (root / "SalaryApp/ReportExport.swift").read_text()
start = report.index("    static func columnWidths(")
end = report.index("        return widths\n    }", start) + len("        return widths\n    }")
widths = "import AppKit\nimport Foundation\nenum PDFReportWriter {\n" + report[start:end] + "\n}\n"
pdf_text = (root / "SalaryApp/PDFText.swift").read_text()
start = pdf_text.index("    static let rtl:")
end = pdf_text.index("    /// يرسم", start)
rtl = pdf_text[start:end]
stubs = '''import AppKit
import CoreText
import Foundation
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
    var total: Double { amounts.reduce(0, +) }
    var amountColumn: Int { columns.firstIndex { $0.kind == .amount } ?? 0 }
    var landscape: Bool { !entityDeduction && columns.reduce(0) { $0 + $1.width } > 96 }
}
enum PDFText {
''' + rtl + "}\n"
with tempfile.TemporaryDirectory(prefix="salary-report-layout-") as temp:
    directory = Path(temp)
    (directory / "Entity.swift").write_text(entity)
    health = (root / "SalaryApp/HealthCarePDFLayout.swift").read_text().replace("import UIKit", "import AppKit").replace("nonisolated ", "")
    (directory / "Health.swift").write_text(health)
    (directory / "Widths.swift").write_text(widths)
    (directory / "Stubs.swift").write_text(stubs)
    (directory / "main.swift").write_text((root / "ci/report_layout_test/main.swift").read_text())
    subprocess.run(["swiftc", "Stubs.swift", "Widths.swift", "Entity.swift", "Health.swift", "main.swift", "-o", "layout-test"], cwd=directory, check=True)
    subprocess.run([str(directory / "layout-test")], cwd=directory, check=True)
