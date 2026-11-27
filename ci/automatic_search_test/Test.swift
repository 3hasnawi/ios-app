import Foundation
import SwiftData

@main struct Test {
    static let first = "منتسب ألف وهمي"
    static let second = "منتسب باء وهمي"
    static let trustOnly = "أمانة اختبارية فقط"
    static func entry(_ name: String, month: Int, amount: Int64, status: String = "", note: String = "") -> SalaryTrust.Entry {
        SalaryTrust.Entry(id: UUID().uuidString, name: name, rank: "موظف", workplace: "قسم اختباري", year: 2026, month: month,
            amount: amount, caseStatus: "", note: note, importedStatus: status, legacyPaid: status == "صرف",
            reviewPending: status == "مراجعة", payments: [])
    }
    @MainActor static func main() async throws {
        let fields = [AutomaticSearchQuery.Field(label: "name", value: "أَحمد التجريبي"), .init(label: "number", value: "001234"),
                      .init(label: "mother", value: "مريم اختبارية"), .init(label: "amount", value: "100000.0")]
        precondition(AutomaticSearchQuery("احمد ٠٠١٢٣٤").match(fields) != nil)
        precondition(AutomaticSearchQuery("١٢ ٣٤").match(fields) == "number")
        precondition(AutomaticSearchQuery("احمد رقم غير موجود").match(fields) == nil)
        precondition(AutomaticSearchQuery("1000000").match(fields) == nil)
        precondition(AutomaticSearchQuery("احمد 123").digits == nil)
        precondition(AutomaticSearchQuery(" ").match(fields) == nil)
        let schema = Schema([PayrollMonth.self, Employee.self, Deduction.self, StaffMember.self])
        let container = try ModelContainer(for: schema, configurations: [ModelConfiguration(isStoredInMemoryOnly: true)])
        SharedModel.container = container
        let context = ModelContext(container)
        let newer = "تشرين الثاني 2026", older = "تشرين الأول 2026"
        let monthA = PayrollMonth(name: newer, employeeCount: 2, deductionCount: 1)
        let monthB = PayrollMonth(name: older, employeeCount: 1, deductionCount: 2)
        context.insert(monthA); context.insert(monthB)
        context.insert(StaffMember(name: first, mother: "مريم اختبارية", statNumber: "001234", unifiedNumber: "987654",
            rank: "موظف", status: "مستمر", workplace: "قسم وهمي", sections: [.init(title: "اختبار", fields: [.init(label: "بيان إضافي", value: "كلمة مشتركة")])]))
        context.insert(StaffMember(name: second, mother: "فاطمة اختبارية", statNumber: "008888", unifiedNumber: "111222",
            rank: "موظف", status: "مستمر", workplace: "قسم وهمي", sections: []))
        context.insert(Employee(month: newer, name: first, rank: "موظف", workplace: "قسم وهمي", netSalary: 700))
        context.insert(Employee(month: older, name: first, rank: "موظف", workplace: "قسم وهمي", netSalary: 600))
        let raw = PayrollColumnSnapshot.encode(headers: ["حقل غير معروف"], values: ["معلومة نادرة 777"])
        context.insert(Employee(month: newer, name: second, rank: "موظف", workplace: "قسم وهمي", netSalary: 800,
            details: ["ملاحظة خاصة": "كلمة مشتركة", "بيان إضافي": "مريم اختبارية"], rawColumnsData: raw))
        context.insert(Deduction(month: newer, employeeName: first, title: "جهة وهمية مستقلة", amount: 71, note: "إضبارة ١٢٣/٢٠٢٦"))
        let pastName = "منتسب قديم اختباري"
        for title in ["جهة ألف قديمة","جهة باء قديمة"] {
            context.insert(Deduction(month: older, employeeName: pastName, title: title, amount: 50, note: ""))
        }
        // مطابقة لاحقة تتجاوز حجم الدفعات ولا تسقط عند وجود نتائج سريعة للشخص الأول.
        for i in 0..<270 {
            context.insert(StaffMember(name: "موظف تجربة \(i)", mother: "أم وهمية", statNumber: "5\(i)", unifiedNumber: "6\(i)",
                rank: "اختبار", status: "مستمر", workplace: "قسم", sections: i == 269 ? [.init(title: "فحص", fields: [.init(label: "حقل متأخر", value: "معلومة متأخرة وحيدة")])] : []))
        }
        try context.save()
        let ledger = SalaryTrust.Ledger(version: 2, entries: [
            entry(first, month: 8, amount: 300, note: "مرجع مشترك وهمي"),
            entry(first, month: 9, amount: 200, status: "صرف"),
            entry(first, month: 7, amount: 100, status: "مراجعة"),
            entry(trustOnly, month: 6, amount: 500, note: "مرجع مشترك وهمي")], openingImported: true)
        let trustData = try SalaryTrust.encode(ledger)
        let started = Date()
        func find(_ query: String, data: Data? = nil) async throws -> SmartSearchResults {
            try await SmartSearch.find(query, months: [monthA, monthB], accountData: Data(), trustData: data ?? trustData)
        }
        let name = try await find(first)
        precondition(name.people.count == 1 && name.people[0].hasPayroll && name.people[0].hasStaff && name.people[0].hasTrust)
        precondition(name.people[0].hit.month == newer && name.people[0].hit.netSalary == 700)
        let summary = name.people[0].hit.trust!
        precondition(summary.months == 3 && summary.total == 600 && summary.paid == 200 && summary.remaining == 300 && summary.review == 100 && !summary.allPaid)
        let past = try await find(pastName)
        precondition(past.people.count == 1 && past.people[0].hit.hasDeduction && !past.people[0].hasPayroll && past.groups.isEmpty)
        let mother = try await find("مريم اختبارية")
        precondition(Set(mother.people.map(\.name)) == Set([first, second]))
        precondition(mother.people.contains { $0.name == first && $0.hasTrust && $0.hasPayroll && $0.hit.matchLabel?.contains("الأم") == true })
        let stat = try await find("٠٠١ ٢٣٤")
        precondition(stat.people.contains { $0.name == first && $0.hasTrust })
        let unified = try await find("٩٨٧٦٥٤")
        precondition(unified.people.contains { $0.name == first })
        let mixed = try await find("ألف 001234")
        precondition(mixed.people.count == 1 && mixed.people[0].name == first)
        let invalidMixed = try await find("باء 001234")
        precondition(invalidMixed.people.isEmpty)
        let shared = try await find("كلمة مشتركة")
        precondition(Set(shared.people.map(\.name)).isSuperset(of: [first, second]))
        let rare = try await find("معلومة نادرة 777")
        precondition(rare.people.contains { $0.name == second && $0.hasPayroll })
        let late = try await find("معلومة متأخرة وحيدة")
        precondition(late.people.contains { $0.name == "موظف تجربة 269" })
        let orphan = try await find(trustOnly)
        precondition(orphan.people.count == 1 && orphan.people[0].hasTrust && !orphan.people[0].hasPayroll && !orphan.people[0].hasStaff)
        let byNote = try await find("مرجع مشترك وهمي")
        precondition(Set(byNote.people.map(\.name)) == Set([first, trustOnly]))
        precondition(byNote.people.first { $0.name == first }!.hasPayroll)
        let deduction = try await find("١٢٣/٢٠٢٦")
        precondition(deduction.groups.contains { $0.title == "جهة وهمية مستقلة" })
        let paidData = try SalaryTrust.encode(SalaryTrust.Ledger(version: 2, entries: [entry(first, month: 9, amount: 200, status: "صرف")], openingImported: true))
        let paid = try await find(first, data: paidData)
        precondition(paid.people[0].hit.trust!.allPaid && paid.people[0].hit.trust!.remaining == 0)
        let empty = try await find(first, data: Data())
        precondition(!empty.people[0].hasTrust)
        let damaged = try await find(first, data: Data("corrupt".utf8))
        precondition(damaged.trustError != nil && damaged.people.contains { $0.name == first } && !damaged.people[0].hasTrust)
        var partialSeen = false
        let complete = try await SmartSearch.find(first, months: [monthA, monthB], accountData: Data(), trustData: trustData) { partial in
            partialSeen = partialSeen || partial.people.contains { $0.name == first }
        }
        precondition(partialSeen && complete.people.count == 1)
        let cancellation = Task { try await find("معلومة متأخرة وحيدة") }; cancellation.cancel()
        do { _ = try await cancellation.value; fatalError("Search cancellation ignored") } catch is CancellationError { }
        let unchanged = try context.fetchCount(FetchDescriptor<Employee>())
        let trustedCount = try SalaryTrust.decode(trustData).entries.count
        precondition(unchanged == 3 && trustedCount == 4)
        print("Automatic search passed: names/mothers/IDs, mixed fields, deep results beside fast hits, unknown payroll columns, late batches, trust-only people, unique cards and latest salary, paid/review separation, replacement/corrupt ledgers, progressive results and cancellation; \(Date().timeIntervalSince(started)) seconds")
    }
}
