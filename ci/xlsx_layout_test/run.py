#!/usr/bin/env python3
"""Exercise the production spreadsheet XML and validation without building the iOS app."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = (root / "SalaryApp/ReportExport.swift").read_text()
validation_start = source.index("    static func validate(_ sheet:")
validation_end = source.index("    /// ملف بعدة أوراق", validation_start)
methods_start = source.index("    static func letter(_ index:")
methods_end = source.index("    private static let contentTypes", methods_start)
production = "import Foundation\nenum XLSXWriter {\n" + source[validation_start:validation_end] + source[methods_start:methods_end] + "\n}\n"
fitted_start = source.index("    func fitted() -> ReportSheet")
fitted_end = source.index('    /// "ايلول 2026"', fitted_start)
fitted = source[fitted_start:fitted_end]
stubs = '''import Foundation
func tr(_ ar: String, _ en: String) -> String { ar }
struct ReportSheet {
    enum Kind { case serial, text, amount }
    struct Column { let title: String; let width: Double; let kind: Kind }
    let header: String
    let title: String
    let columns: [Column]
    var rows: [[String]]
    var amounts: [Double]
    var showTotal = true
    var entityDeduction = false
    var deductionList = false
    var total: Double { amounts.reduce(0, +) }
    var amountColumn: Int { columns.firstIndex { $0.kind == .amount } ?? 0 }
    var landscape: Bool { !entityDeduction && columns.reduce(0) { $0 + $1.width } > 96 }
''' + fitted + "}\n"
with tempfile.TemporaryDirectory(prefix="salary-xlsx-layout-") as temp:
    directory = Path(temp)
    (directory / "Production.swift").write_text(production)
    health = (root / "SalaryApp/HealthCareReport.swift").read_text()
    start = health.index("    private static func sheet(")
    end = health.index("    // MARK: - PDF", start)
    health_production = health[start:end].replace("private static", "static")
    health_stubs = '''import Foundation
struct ReportRow { let name: String; let rank: String; let amount: Double }
enum HealthCareReport {
    static func isExact(_ value: Double) -> Bool { value.truncatingRemainder(dividingBy: 3500) == 0 }
    static func family(_ value: Double) -> Int { max(1, Int((value / 3500).rounded())) }
    static func familyText(_ value: Double) -> String { isExact(value) ? String(family(value)) : String(format: "%.2f", value / 3500) }
    static func title(month: String, others: Bool) -> String { "رعاية " + month }
'''
    (directory / "Health.swift").write_text(health_stubs + health_production + "}\n")
    (directory / "Stubs.swift").write_text(stubs)
    (directory / "main.swift").write_text((root / "ci/xlsx_layout_test/main.swift").read_text())
    subprocess.run(["swiftc", "Stubs.swift", "Production.swift", "Health.swift", "main.swift", "-o", "xlsx-test"], cwd=directory, check=True)
    subprocess.run([str(directory / "xlsx-test")], cwd=directory, check=True)
