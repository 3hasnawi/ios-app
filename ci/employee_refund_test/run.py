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
    files = [str(common), str(app / "ArabicAmountWords.swift"), str(app / "EmployeeRefund.swift"), str(app / "EmployeeRefundExport.swift")]
    subprocess.run(["swiftc", "-o", str(work / "test"), *files, str(tests / "main.swift")], check=True)
    subprocess.run([str(work / "test")], check=True)
    model = section("Models.swift", "@Model\nfinal class Employee", "    var items: [String: Double]")
    model = model[:model.index("    init(")] + """
    init(name: String) { self.name = name; nameKey = name; matchKey = name; rank = ""; workplace = "" }
    var details: [String:String] { [:] }
    var rawColumns: PayrollColumnSnapshot? { PayrollColumnSnapshot.decode(rawColumnsData) }
}
"""
    ios = work / "IOS.swift"
    ios.write_text("import Foundation\nimport SwiftData\n" + model + (tests / "IOS.support").read_text())
    sdk = subprocess.check_output(["xcrun", "--sdk", "iphoneos", "--show-sdk-path"], text=True).strip()
    subprocess.run(["xcrun", "swiftc", "-typecheck", "-sdk", sdk, "-target", "arm64-apple-ios26.0",
                    *files, str(ios), str(app / "EmployeeRefundView.swift"), str(app / "RefundPayrollSearch.swift")], check=True)
