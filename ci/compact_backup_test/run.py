"""Exercise the iPhone backup actor with real SwiftData, ZIPFoundation and fictional local stores on macOS."""
from pathlib import Path
import re
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
app = root / "SalaryApp"
tests = Path(__file__).resolve().parent

def read(name):
    return (app / (name + ".swift")).read_text()

def section(text, start, end):
    return text[text.index(start):text.index(end, text.index(start))]

with tempfile.TemporaryDirectory(prefix="payroll-backup-test-") as folder:
    work = Path(folder)
    src = work / "Sources" / "BackupTests"
    src.mkdir(parents=True)
    revision = re.search(r'revision: "([a-f0-9]+)"', (root / "project.yml").read_text()).group(1)
    (work / "Package.swift").write_text('// swift-tools-version:6.2\nimport PackageDescription\n' +
        'let package = Package(name:"BackupTests", platforms:[.macOS(.v26)], dependencies:[.package(url:"https://github.com/weichsel/ZIPFoundation.git",revision:"' +
        revision + '")], targets:[.executableTarget(name:"BackupTests",dependencies:[.product(name:"ZIPFoundation",package:"ZIPFoundation")])], swiftLanguageModes:[.v5])')
    for name in ["CompactPayrollBackup","BackupArchiveVerification","CheckedArchive","BoundedFile","BackupLineReader","SharedPayrollColumns","SalaryTrust","ContractOverride","ContractBatchPayload","ContractTransferBatch","ContractTransferEngine","TransferIndividualPlan","TransferGroupCSVCheck","TransferReconciliation"]:
        (src / (name + ".swift")).write_text(read(name))
    backup = read("Backup")
    models = read("Models")
    accounts = read("ExecutionAccounts")
    books = section(read("OfficialBooks"), "nonisolated struct OfficialBook:", "/// يُنشأ PDF")
    # Isolate the book store; the PDF and index validation methods are the application's.
    books = section(books, "nonisolated struct OfficialBook:", "    static var root: URL") + '    static var root: URL { URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("test-books") }\n' + books[books.index("    static func file("):]
    imports = 'import Foundation\nimport SwiftData\nimport ZIPFoundation\nimport CryptoKit\nimport CoreGraphics\n'
    code = [
        section(read("Reports"), "nonisolated enum ReportKind", "/// حسابات التقارير"),
        section(read("TransferFiles"), "nonisolated enum TransferCategory", "@ModelActor"),
        section(read("TransferLetterFigures"), "nonisolated struct TransferLetterFigures", "nonisolated enum TransferLetterFigureRules"),
        section(read("HealthCareReport"), "nonisolated enum RankOrder", "/// اشتراكات"),
        section(read("FundReport"), "nonisolated enum FundReport", "    static func summary(") + "}",

        backup[backup.index("nonisolated enum BackupFormat"):backup.index("/// القراءة والكتابة")],
        section(backup, "@ModelActor\nactor BackupActor", "\n}",) + "\n}",
        "enum Restore {\n" + section(backup, "    nonisolated static func replaceVerifiedArchive", "    /// يسجل خط الأساس") + section(backup, "    nonisolated static func extractForRestore", "    private func open(") + "\n}",
        section(models, "/// شهر رواتب واحد", "/// يحفظ نتيجة"), section(models, "nonisolated enum MonthName", "extension PayrollMonth"),
        read("StaffModel").split("/// معلومات آخر استيراد")[0], read("ArabicText").split("enum Money")[0],
        section(accounts, "nonisolated struct SavedExecutionAccount", "    static func merge(") + "}\n",
        section(read("BankStatement"), "nonisolated struct BankStatementSummary", "/// مبالغ تُدخل"),
        section(read("BankStatement"), "nonisolated enum ContractNominalValue", "/// ملخصات مؤقتة"),
        section(read("BankStatement"), "@ModelActor", "struct BankStatementView"),
        section(read("ExcelImporter"), "nonisolated enum DeductionStatus", "nonisolated enum AnnualDeductionAmount"),
        section(read("FoodContractReport"), "nonisolated enum FoodContractReport", "    static func rows(") + "}", books
    ]
    (src / "Production.swift").write_text(imports + "\n".join(code))
    for name in ["Support.swift","Test.swift","ContractBatchTests.swift"]:
        (src / name).write_text((tests / name).read_text())
    subprocess.run(["swift","run","-c","release","BackupTests"],cwd=work,check=True)
