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
        context.insert(Deduction(month: "تشرين الأول 2025", employeeName: name, title: "جهة وهمية", amount: nil, note: DeductionStatus.encode("هارب", note: "")))
        context.insert(Deduction(month: "حزيران 2023", employeeName: name, title: "جهة أخرى", amount: 30, note: ""))
        context.insert(Deduction(month: "غير معروف", employeeName: name, title: "جهة وهمية", amount: 10, note: ""))
        try context.save()
        let engine = EmployeeDeductionEngine(modelContainer: container)
        let person = DeductionPerson(key: ArabicText.matchKey(name), name: name, detail: "")
        let available = try await engine.availableMonths(person: person, entity: "جهة وهمية")
        try require(available.keys == [202401, 202510] && available.unrecognizedCount == 1, "Employee/entity months not filtered or sorted")
        let otherEntity = try await engine.availableMonths(person: person, entity: "جهة أخرى")
        try require(otherEntity.keys == [202306] && otherEntity.unrecognizedCount == 0, "Entity selection mixed unrelated months")
        let statement = try await engine.statement(person: person, entity: "جهة وهمية", from: 202401, to: 202404)
        let engineReport = ReportEngine(modelContainer: container)
        let group = ReportGroup(kind: .deductions, month: jan, title: "جهة وهمية", count: 1, total: 100, byEntity: false)
        let febRows = try await engineReport.rows(for: ReportGroup(kind: .deductions, month: feb, title: group.title, count: 0, total: 0, byEntity: false))
        let febGroup = group.forMonth(feb, rows: febRows)
        try require(febGroup.month == feb && febGroup.total == 12 && febGroup.count == 1, "Changing month used stale totals or rows")
        let emptyRows = try await engineReport.rows(for: ReportGroup(kind: .deductions, month: april, title: group.title, count: 0, total: 0, byEntity: false))
        try require(emptyRows.isEmpty && group.forMonth(april, rows: emptyRows).total == 0, "Empty entity month misclassified")
        try require(statement.rows.count == 4 && statement.total == 100 && statement.incomplete && statement.hasUnrecognizedMonths, "Incorrect monthly totals or completeness")
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
        let document = PDFDocument(url: file)
        try require(document?.pageCount == 1, "Four years must fit on one landscape page")
        if let page = document?.page(at: 0) {
            let bounds = page.bounds(for: .mediaBox)
            try require(bounds.width > bounds.height, "Statement must be landscape")
        } else { throw Failure(message: "Statement page missing") }
        try require(statement.references == ["1", "2", "مرجع"], "Deduction references missing")
        let fifthYear = rows + [DeductionPeriodRow(year: 2028, month: 1, state: .amount(100))]
        let multi = EmployeeDeductionStatement(person: person, rank: "موظف", workplace: "قسم تجريبي", entity: "جهة وهمية", rows: fifthYear)
        let multiFile = try await EmployeeDeductionPDF.make(multi, logo: nil)
        defer { try? FileManager.default.removeItem(at: multiFile.deletingLastPathComponent()) }
        try require(PDFDocument(url: multiFile)?.pageCount == 2, "Five years must span two pages")
        let replacementContainer = try ModelContainer(for: schema, configurations: [ModelConfiguration(schema: schema, isStoredInMemoryOnly: true)])
        let seed = ModelContext(replacementContainer)
        for month in [jan, feb] {
            let record = PayrollMonth(name: month, employeeCount: 1, deductionCount: 1)
            record.itemSummaryData = Data([1, 2, 3])
            seed.insert(record)
            seed.insert(Employee(month: month, name: "قديم وهمي", rank: "", workplace: "", netSalary: 1))
            seed.insert(Deduction(month: month, employeeName: "قديم وهمي", title: "استقطاع وهمي", amount: 10, note: ""))
        }
        try seed.save()
        let replacement = DataImporter(modelContainer: replacementContainer)
        do {
            try await replacement.replacePayroll(month: april, with: [])
            throw Failure(message: "Empty payroll erased existing records")
        } catch PayrollReplacementError.emptyPayroll { }
        try await replacement.replacePayroll(month: april, with: [ImportedEmployee(name: "جديد وهمي", rank: "", workplace: "", netSalary: 2)])
        let verify = ModelContext(replacementContainer)
        let remaining = try verify.fetch(FetchDescriptor<Employee>())
        let deductionsAfter = try verify.fetch(FetchDescriptor<Deduction>())
        let savedMonths = try verify.fetch(FetchDescriptor<PayrollMonth>())
        try require(remaining.count == 2 && Set(remaining.map(\.month)) == Set([feb, april]) && remaining.contains { $0.month == april && $0.name == "جديد وهمي" }, "Latest two payroll months were not preserved")
        try require(deductionsAfter.count == 2 && Set(deductionsAfter.map(\.month)) == Set([jan, feb]), "Previous deductions were not preserved")
        try require(savedMonths.contains { $0.name == jan && $0.employeeCount == 0 && $0.itemSummaryData == nil && $0.deductionCount == 1 } && savedMonths.contains { $0.name == feb && $0.employeeCount == 1 && $0.deductionCount == 1 }, "Previous payroll month metadata is stale")
        do {
            try await replacement.replacePayroll(month: jan, with: [ImportedEmployee(name: "قديم جداً وهمي", rank: "", workplace: "", netSalary: 3)])
            throw Failure(message: "Older payroll displaced a recent month")
        } catch PayrollReplacementError.olderMonth { }
        var combined = ImportResult()
        combined.employees = [ImportedEmployee(name: "مشترك وهمي", rank: "", workplace: "", netSalary: 4)]
        combined.deductions = [ImportedDeduction(employeeName: "مشترك وهمي", title: "استقطاع جديد", amount: 15, note: "")]
        try await replacement.replace(month: april, with: combined)
        let afterCombined = ModelContext(replacementContainer)
        let finalEmployees = try afterCombined.fetch(FetchDescriptor<Employee>())
        let finalDeductions = try afterCombined.fetch(FetchDescriptor<Deduction>())
        try require(Set(finalEmployees.map(\.month)) == Set([feb, april]), "Combined import changed the two retained months")
        try require(finalDeductions.count == 3 && Set(finalDeductions.map(\.month)) == Set([jan, feb, april]), "Combined import lost earlier deductions")
        return "employeeDeduction=ok sums=ok missing=ok duplicates=ok pdfPages=2"
    }
}
#endif
