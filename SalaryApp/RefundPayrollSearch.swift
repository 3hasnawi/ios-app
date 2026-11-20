import SwiftData
import Foundation

nonisolated struct RefundCandidate: Identifiable, Hashable, Sendable {
    let id: PersistentIdentifier
    let person: RefundPerson
}

@ModelActor
actor RefundPayrollSearch {
    private func candidate(_ employee: Employee) -> RefundCandidate {
        let details = employee.details
        return RefundCandidate(id: employee.persistentModelID, person: RefundPerson(name: employee.name, rank: employee.rank,
            workplace: employee.workplace, iban: EmployeeRefund.normalizeAccount(details["IBAN"] ?? ""), bank: details["توطين المصرف"] ?? ""))
    }

    func search(_ input: String, month: String) throws -> [RefundCandidate] {
        let key = ArabicText.matchKey(ArabicText.cleanQuery(input))
        guard key.count >= 2 else { return [] }
        var query = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month && $0.matchKey.contains(key) },
                                              sortBy: [SortDescriptor(\.name), SortDescriptor(\.workplace)])
        query.fetchLimit = 50
        return try modelContext.fetch(query).map(candidate)
    }

    func verify(_ selected: [RefundCandidate], month: String) throws {
        for record in selected {
            let key = ArabicText.matchKey(record.person.name)
            let query = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month && $0.matchKey == key })
            guard try modelContext.fetch(query).contains(where: { candidate($0) == record }) else {
                throw EmployeeRefund.error("تغيّرت بيانات المنتسب؛ أعد اختياره: " + record.person.name, "Employee data changed. Select again: " + record.person.name)
            }
        }
    }
}
