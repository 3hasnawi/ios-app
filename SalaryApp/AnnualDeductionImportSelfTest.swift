#if targetEnvironment(simulator)
import Foundation

/// فحص ملف Excel وهمي فقط، دون استيراد أي سجل إلى قاعدة المستخدم.
nonisolated enum AnnualDeductionImportSelfTest {
    struct Failure: LocalizedError { let message: String; var errorDescription: String? { message } }
    static func run() throws -> String {
        func require(_ condition: Bool, _ message: String) throws { if !condition { throw Failure(message: message) } }
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let result = try ExcelImporter.load(url: documents.appendingPathComponent("annual-fixture.xlsx"))
        try require(result.deductions.isEmpty && result.annualDeductions.count == 3, "Annual file not split by month")
        try require(result.annualDeductions[202401]?.count == 1 && result.annualDeductions[202402]?.count == 2 && result.annualDeductions[202412]?.count == 2, "Wrong months or year")
        let feb = result.annualDeductions[202402] ?? []
        try require(feb.contains { DeductionStatus.read($0.note, amount: $0.amount) == "موقوف" }, "Status not preserved")
        try require(feb.contains { $0.amount == 50 }, "Amount not preserved")
        try require(AnnualDeductionAmount.parse("١٢٬٣٤٥") == 12345 && AnnualDeductionAmount.parse("موقوف 2024") == nil,
                    "Unsafe amount interpretation")
        for filename in ["annual-invalid.xlsx", "annual-2025.xlsx"] {
            do {
                _ = try ExcelImporter.load(url: documents.appendingPathComponent(filename))
                throw Failure(message: "Invalid file was accepted: \(filename)")
            } catch is ImportError { }
        }
        return "annualDeduction=ok months=3 statuses=ok invalid=ok yearConflict=ok"
    }
}
#endif
