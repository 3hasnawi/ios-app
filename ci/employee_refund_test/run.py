"""Exercise the production batch/CSV/letter plan and type-check the iPhone UI without an app build."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
app = root / "SalaryApp"
tests = Path(__file__).parent

def section(file, start, end):
    text = (app / file).read_text()
    return text[text.index(start):text.index(end, text.index(start))]

with tempfile.TemporaryDirectory() as tmp:
    work = Path(tmp)
    types = section("TransferWordDocument.swift", "nonisolated enum TransferWordDocument", "    static func")
    # Only the three document value types are needed.
    types = types[:types.index("case pageBreak") + len("case pageBreak")] + "\n    }\n}\n"
    arabic = (app / "ArabicText.swift").read_text().split("enum Money")[0]
    rules = section("TransferFiles.swift", "    static func validIBAN", "    /// الجزء المحلي")
    rules += section("TransferFiles.swift", "    static func safeField", "    static func issues")
    rules += section("TransferFiles.swift", "    static let csvHeader", "    static func csv")
    category = section("TransferFiles.swift", "nonisolated enum TransferCategory", "nonisolated struct TransferEntity")
    common = work / "Shared.swift"
    snapshot = section("Models.swift", "nonisolated struct PayrollColumnSnapshot:", "/// ترميز سريع")
    common.write_text(arabic + "\n" + snapshot + "\n" + types + "\n" + category + "\nenum TransferRules {\n" + rules + "\n}\n" +
        (tests / "Support.swift").read_text())
    with common.open("a") as output:
        output.write(section("ReportExport.swift", "nonisolated struct ReportSheet:", "    /// عرض كل عمود") + "}\n")
        output.write("extension ReportSheet {\n" + section("ReportExport.swift", "    static func easternDigits(", "    static var defaultHeader") + "}\n")
        output.write("extension TransferWordValues {\n" + section("TransferWordValues.swift", "    static func groupedAmount(", "    /// تُطلب") + "}\n")
        output.write("extension CustomTransferTemplate {\n" + section("CustomTransferTemplate.swift", "    static func invalid(", "    func halfPoints(") + "}\n")
        output.write("extension TransferWordGroupDraft {\n" + section("TransferWordGroupDraft.swift", "    static func signatureCells(", "    static func bodyValues(")
                     + section("TransferWordGroupDraft.swift", "    static func referenceText(", "    /// تبقى مساحة") + "}\n")
    renderer = work / "Renderer.swift"
    renderer.write_text((app / "TransferAdaptivePDF.swift").read_text().replace("import UIKit", "import AppKit"))
    report = work / "ReportPDF.swift"
    report_code = ("import UIKit\n" + section("ReportExport.swift", "nonisolated enum PDFReportWriter", "// MARK: - نافذة التصدير")
                   + (app / "ReportFont.swift").read_text() + (app / "EntityDeductionPDF.swift").read_text()
                   + (app / "PDFText.swift").read_text().split("/// تجهيز ملفات التصدير")[0])
    report.write_text(report_code.replace("import UIKit", "#if os(macOS)\nimport AppKit\n#else\nimport UIKit\n#endif"))
    mac = [str(renderer), str(tests / "MacPDF.swift"), str(root / "ci/report_layout_test/UIKitPreviewStubs.swift")]
    files = [str(report), str(app / "RefundCSVRecovery.swift"), str(app / "TransferHeaderFont.swift"), str(app / "ReportTableStyle.swift"), str(common), str(app / "ArabicAmountWords.swift"), str(app / "EmployeeRefund.swift"), str(app / "EmployeeRefundExport.swift")]
    subprocess.run(["swiftc", "-o", str(work / "test"), *mac, *files, str(tests / "main.swift")], check=True)
    subprocess.run([str(work / "test")], cwd=root, check=True)
    model = section("Models.swift", "@Model\nfinal class Employee", "    var items: [String: Double]")
    model = model[:model.index("    init(")] + """
    init(name: String) { self.name = name; nameKey = ArabicText.normalize(name); matchKey = ArabicText.matchKey(name); rank = ""; workplace = "" }
    var details: [String:String] { detailsData.flatMap { try? JSONDecoder().decode([String:String].self, from: $0) } ?? [:] }
    var rawColumns: PayrollColumnSnapshot? { PayrollColumnSnapshot.decode(rawColumnsData) }
}
"""
    ios = work / "IOS.swift"
    ios.write_text("import Foundation\nimport SwiftData\n" + model + (tests / "IOS.support").read_text())
    target = work / "Target.swift"
    target.write_text("import SwiftData\n" + section("RefundSearchEntrySheet.swift", "struct RefundSearchTarget:", "struct RefundSearchEntrySheet:"))
    shared = [str(app / "RefundStorage.swift"), str(ios), str(app / "RefundPayrollSearch.swift"), str(app / "RefundDraft.swift")]
    subprocess.run(["swiftc", "-parse-as-library", "-o", str(work / "draft-test"), *mac, *files, *shared,
                    str(target), str(tests / "DraftTest.swift")], check=True)
    subprocess.run([str(work / "draft-test")], check=True)
    sdk = subprocess.check_output(["xcrun", "--sdk", "iphoneos", "--show-sdk-path"], text=True).strip()
    subprocess.run(["xcrun", "swiftc", "-typecheck", "-sdk", sdk, "-target", "arm64-apple-ios26.0",
                    *files, str(app / "TransferAdaptivePDF.swift"), *shared, str(app / "RefundHistoryView.swift"), str(app / "EmployeeRefundView.swift"), str(app / "RefundSearchEntrySheet.swift")], check=True)
