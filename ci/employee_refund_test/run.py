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
        output.write((app / "CustomTransferTemplate.swift").read_text() + "\n")
        output.write("extension TransferWordGroupDraft {\n" + section("TransferWordGroupDraft.swift", "    static func signatureCells(", "    static func bodyValues(")
                     + section("TransferWordGroupDraft.swift", "    static func referenceText(", "    /// تبقى مساحة") + "}\n")
    renderer = work / "Renderer.swift"
    # The macOS CLI has no iOS file-protection class. Keep the real PDFKit checks.
    render_source = (app / "TransferAdaptivePDF.swift").read_text().replace("import UIKit", "import AppKit")
    renderer.write_text(render_source.replace("try fm.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: destination.path)", ""))
    report = work / "ReportPDF.swift"
    report_code = ("import UIKit\n" + section("ReportExport.swift", "nonisolated enum PDFReportWriter", "// MARK: - نافذة التصدير")
                   + (app / "ReportFont.swift").read_text() + (app / "EntityDeductionPDF.swift").read_text()
                   + (app / "PDFText.swift").read_text().split("/// تجهيز ملفات التصدير")[0])
    report.write_text(report_code.replace("import UIKit", "#if os(macOS)\nimport AppKit\n#else\nimport UIKit\n#endif"))
    mac = [str(renderer), str(tests / "MacPDF.swift"), str(root / "ci/report_layout_test/UIKitPreviewStubs.swift")]
    files = [str(report), str(app / "BoundedFile.swift"), str(app / "RefundCSVRecovery.swift"), str(app / "TransferHeaderFont.swift"), str(app / "ReportTableStyle.swift"), str(common), str(app / "ArabicAmountWords.swift"), str(app / "EmployeeRefund.swift"), str(app / "EmployeeRefundExport.swift")]
    treasury = work / "TreasuryTable.swift"
    treasury.write_text("import Foundation\n" +
        section("TransferLetterFigures.swift", "nonisolated struct TransferLetterFigures:", "    var effectiveContractNominalShare") + "}\n" +
        section("TransferWordTables.swift", "nonisolated enum TransferWordTables", "    static func contracts(") + "}\n")
    subprocess.run(["swiftc", "-o", str(work / "test"), *mac, *files, str(treasury), str(app / "TransferLetterBody.swift"),
                    str(tests / "main.swift")], check=True)
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
    shared = [str(app / "RefundBackup.swift"), str(app / "BackupLineReader.swift"), str(app / "RefundStorage.swift"), str(ios), str(app / "RefundPayrollSearch.swift"), str(app / "RefundDraft.swift")]
    subprocess.run(["swiftc", "-parse-as-library", "-o", str(work / "draft-test"), *mac, *files, *shared,
                    str(target), str(tests / "DraftTest.swift")], check=True)
    subprocess.run([str(work / "draft-test")], check=True)
    preview = work / "StatementPreview.swift"
    preview.write_text("import SwiftUI\nimport PDFKit\n"
                       + section("EmployeeDeductions.swift", "struct StatementPDF:", "/// كشف أفقي")
                       + section("ReportPreview.swift", "struct PDFPreview:", "/// يحمّل صفوف الكشف")
                       + "enum AppSettings { enum Numerals { case arabicIndic, latin }; static let numerals = Numerals.latin; static var isEnglish: Bool { false } }\n"
                       + "enum PerfLog { static func record(_ name: String, since: Date) {} }\n")
    sdk = subprocess.check_output(["xcrun", "--sdk", "iphoneos", "--show-sdk-path"], text=True).strip()
    subprocess.run(["xcrun", "swiftc", "-typecheck", "-sdk", sdk, "-target", "arm64-apple-ios26.0",
                    *files, str(preview), str(app / "OneDriveAuthorizationReply.swift"), str(app / "OneDriveTrialView.swift"), str(app / "TransferAdaptivePDF.swift"), str(app / "DocumentPicker.swift"), *shared, str(app / "RefundAmountField.swift"), str(app / "RefundHistoryView.swift"), str(app / "EmployeeRefundView.swift"), str(app / "RefundSearchEntrySheet.swift")], check=True)
