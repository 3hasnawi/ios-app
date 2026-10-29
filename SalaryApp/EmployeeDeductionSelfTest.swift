#if targetEnvironment(simulator)
import Foundation
import SwiftData
import PDFKit

/// قاعدة مؤقتة وهمية فقط؛ لا تُستخدم بيانات التطبيق أو تعديلها.
@MainActor
enum EmployeeDeductionSelfTest {
    struct Failure: LocalizedError { let message: String; var errorDescription: String? { message } }
    static func run() async throws -> String {
        func require(_ condition: Bool, _ message: String) throws { if !condition { throw Failure(message: message) } }
        let schema = Schema([PayrollMonth.self, Employee.self, Deduction.self, StaffMember.self, ExecutionAccount.self])
        let config = ModelConfiguration(schema: schema, isStoredInMemoryOnly: true)
        let container = try ModelContainer(for: schema, configurations: [config])
        let context = ModelContext(container)
        let name = "أحمد اختبار وهمي"
        let jan = "كانون الثاني 2024", feb = "شباط 2024", april = "نيسان 2024"
        context.insert(PayrollMonth(name: jan, employeeCount: 1, deductionCount: 3))
        context.insert(PayrollMonth(name: feb, employeeCount: 1, deductionCount: 1))
        context.insert(PayrollMonth(name: april, employeeCount: 1, deductionCount: 0))
        context.insert(Employee(month: jan, name: name, rank: "موظف", workplace: "قسم تجريبي", netSalary: 1))
        context.insert(Deduction(month: jan, employeeName: name, title: "جهة وهمية", amount: 40, note: "1"))
        context.insert(Deduction(month: jan, employeeName: name, title: "جهة وهمية", amount: 60, note: "2"))
        context.insert(Deduction(month: jan, employeeName: name, title: "جهة وهمية", amount: nil, note: DeductionStatus.encode("موقوف", note: "مرجع")))
        context.insert(Deduction(month: feb, employeeName: "موظف آخر وهمي", title: "جهة وهمية", amount: 12, note: ""))
        try context.save()
        let engine = EmployeeDeductionEngine(modelContainer: container)
        let person = DeductionPerson(key: ArabicText.matchKey(name), name: name, detail: "")
        let statement = try await engine.statement(person: person, entity: "جهة وهمية", from: 202401, to: 202404)
        try require(statement.rows.count == 4 && statement.total == 100 && statement.incomplete, "Incorrect monthly totals or completeness")
        if case .status(let value, let statuses) = statement.rows[0].state {
            try require(value == 100 && statuses == ["موقوف"], "Amount/status not preserved")
        } else { throw Failure(message: "Missing January status") }
        if case .none = statement.rows[1].state { } else { throw Failure(message: "No deduction misclassified") }
        if case .unavailable = statement.rows[2].state { } else { throw Failure(message: "Missing month misclassified") }
        if case .unavailable = statement.rows[3].state { } else { throw Failure(message: "Empty deduction file misclassified") }
        let importer = DataImporter(modelContainer: container)
        do {
            try await importer.replaceAnnualDeductions(month: jan, with: [], expectedCount: 0, expectedDate: nil)
            throw Failure(message: "Changed month was overwritten")
        } catch AnnualDeductionImportError.changedMonth { }
        let unchanged = try await engine.statement(person: person, entity: "جهة وهمية", from: 202401, to: 202404)
        try require(unchanged.total == 100, "Changed-month guard did not preserve data")
        do { _ = try DeductionCalendar.span(from: 202405, to: 202401); throw Failure(message: "Invalid range accepted") }
        catch is EmployeeDeductionError { }
        let duplicate = Employee(month: jan, name: name, rank: "موظف", workplace: "موقع آخر", netSalary: 1)
        context.insert(duplicate)
        try context.save()
        do { _ = try await engine.statement(person: person, entity: "جهة وهمية", from: 202401, to: 202404); throw Failure(message: "Ambiguous name accepted") }
        catch EmployeeDeductionError.ambiguousName { }
        context.delete(duplicate)
        context.insert(Deduction(month: jan, employeeName: name, title: "جهة وهمية", amount: -1, note: ""))
        try context.save()
        do { _ = try await engine.statement(person: person, entity: "جهة وهمية", from: 202401, to: 202404); throw Failure(message: "Invalid amount accepted") }
        catch EmployeeDeductionError.invalidAmount { }
        let rows = try DeductionCalendar.span(from: 202401, to: 202712).map { pair in
            DeductionPeriodRow(year: pair.0, month: pair.1, state: pair.0 == 2024 && pair.1 == 1 ? .status(100, ["موقوف"]) : .amount(100))
        }
        let long = EmployeeDeductionStatement(person: person, rank: "موظف", workplace: "قسم تجريبي", entity: "جهة وهمية", rows: rows)
        let file = try await EmployeeDeductionPDF.make(long, logo: nil)
        defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
        try require(PDFDocument(url: file)?.pageCount == 2, "Four years must span two pages")
        return "employeeDeduction=ok sums=ok missing=ok duplicates=ok pdfPages=2"
    }
}
#endif
