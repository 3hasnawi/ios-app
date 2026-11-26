import SwiftData
import Foundation

nonisolated struct RefundCandidate: Codable, Identifiable, Hashable, Sendable {
    let id: PersistentIdentifier
    let month: String
    let person: RefundPerson
}

@ModelActor
actor RefundPayrollSearch {
    private static func candidate(_ employee: Employee) -> RefundCandidate {
        let details = employee.details
        return RefundCandidate(id: employee.persistentModelID, month: employee.month, person: RefundPerson(name: employee.name, rank: employee.rank,
            workplace: employee.workplace, iban: EmployeeRefund.normalizeAccount(details["IBAN"] ?? ""), bank: details["توطين المصرف"] ?? "",
            payrollBIC: EmployeeRefund.payrollBIC(details: details, columns: employee.rawColumns)))
    }

    func search(_ input: String, month: String) throws -> [RefundCandidate] {
        let key = ArabicText.matchKey(ArabicText.cleanQuery(input))
        guard key.count >= 2 else { return [] }
        var query = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month && $0.matchKey.contains(key) },
                                              sortBy: [SortDescriptor(\.name), SortDescriptor(\.workplace)])
        query.fetchLimit = 50
        return try modelContext.fetch(query).map(Self.candidate)
    }

    func searchResult(_ target: RefundSearchTarget, month: String) throws -> RefundCandidate {
        let key = target.key
        var query = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month && $0.matchKey == key })
        if target.month != month { query.fetchLimit = 2 }
        let matches = try modelContext.fetch(query)
        if target.month == month, let exact = matches.first(where: { $0.persistentModelID == target.id }) {
            return Self.candidate(exact)
        }
        if target.month != month, matches.count == 1 { return Self.candidate(matches[0]) }
        throw EmployeeRefund.error("تعذّر تحديد سجل هذا المنتسب في شهر الدفعة. راجع الأسماء من شاشة إعادة المبالغ.", "Could not identify this employee in the batch month. Review names on the refunds screen.")
    }

    func recover(_ payments: [EmployeeRefund.Payment], month: String) throws -> [RefundDraftRow] {
        try payments.map { payment in
            let key = ArabicText.matchKey(payment.name)
            let query = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month && $0.matchKey == key })
            let matches = try modelContext.fetch(query).map(Self.candidate).filter {
                $0.person.iban == payment.iban && $0.person.payrollBIC == .value(payment.bic)
            }
            guard matches.count == 1, let match = matches.first else {
                throw EmployeeRefund.error("تعذّرت مطابقة حساب المنتسب «" + payment.name + "» في رواتب شهر " + month + ". اختر شهر الرواتب الذي أُعدّت منه الدفعة.", "Could not match " + payment.name + " in " + month + ". Select the original payroll month.")
            }
            return RefundDraftRow(candidate: match, amount: String(payment.amount), recoveredReference: payment.recoveredReference)
        }
    }

    /// Payroll rows receive new persistent IDs when a backup is restored. Keep the captured
    /// payment details and relink only an unambiguous exact match from the same month.
    static func relinkDraft(in storage: RefundStorage, context: ModelContext) throws {
        guard var draft = try storage.loadDraft() else { return }
        var changed = false
        for index in draft.rows.indices {
            let original = draft.rows[index].candidate
            let month = original.month, key = ArabicText.matchKey(original.person.name)
            let query = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month && $0.matchKey == key })
            let matches = try context.fetch(query).map(Self.candidate).filter { $0.person == original.person }
            if matches.count == 1, let match = matches.first, match.id != original.id {
                draft.rows[index].candidate = match
                changed = true
            }
        }
        if changed { try storage.saveDraft(draft) }
    }

    func verify(_ selected: [RefundCandidate], month: String) throws {
        for record in selected {
            let key = ArabicText.matchKey(record.person.name)
            let query = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month && $0.matchKey == key })
            guard try modelContext.fetch(query).contains(where: { Self.candidate($0) == record }) else {
                throw EmployeeRefund.error("تغيّرت بيانات المنتسب؛ أعد اختياره: " + record.person.name, "Employee data changed. Select again: " + record.person.name)
            }
        }
    }
}
