import Foundation
import SwiftData

@main enum DraftTest {
    @MainActor static func main() async throws {
        let container = try ModelContainer(for: Employee.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let context = container.mainContext
        func employee(_ month: String, _ name: String, _ iban: String) throws -> Employee {
            let person = Employee(name: name)
            person.month = month
            person.detailsData = try JSONEncoder().encode(["IBAN": iban, "الرفرنس": "RAFBIQ00"])
            context.insert(person)
            try context.save()
            return person
        }
        func fakeIBAN(_ suffix: Int, code: String = "RAFB") -> String {
            let bban = code + String(format: "%015d", suffix)
            var mod = 0
            for c in (bban + "IQ00").utf8 {
                for n in (c >= 65 ? String(c - 55) : String(c - 48)).utf8 { mod = (mod * 10 + Int(n - 48)) % 97 }
            }
            return "IQ" + String(format: "%02d", 98 - mod) + bban
        }
        let first = try employee("A", "موظف افتراضي", fakeIBAN(1))
        let previous = try employee("B", first.name, fakeIBAN(2))
        let second = try employee("A", "موظف ثان", fakeIBAN(3))
        let engine = RefundPayrollSearch(modelContainer: container)
        let target = RefundSearchTarget(id: first.persistentModelID, key: first.matchKey, month: first.month)
        let one = try await engine.searchResult(target, month: "A")
        let old = try await engine.searchResult(target, month: "B")
        precondition(old.id == previous.persistentModelID && old.person.iban == fakeIBAN(2))
        let two = try await engine.searchResult(.init(id: second.persistentModelID, key: second.matchKey, month: "A"), month: "A")
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let storage = RefundStorage(root: root)
        let draft = RefundDraft(storage: storage)
        try draft.add(one)
        try draft.add(one, amount: "25000")
        try draft.add(two, amount: "400")
        precondition(target.isIn(draft) && draft.rows.count == 2 && draft.rows.map(\.amount) == ["25000", "400"])
        draft.source = "اختبار"
        draft.payer = "دافع"; draft.account = fakeIBAN(99)
        let reopened = RefundDraft(storage: storage)
        precondition(reopened.source == draft.source && reopened.rows.map(\.candidate) == draft.rows.map(\.candidate))
        precondition(reopened.rows.map(\.amount) == draft.rows.map(\.amount) && reopened.date == draft.date)
        try storage.archive(reopened.snapshot)
        try storage.archive(draft.snapshot)
        let records = try storage.archived()
        precondition(records.count == 1)
        let historical = try records[0].snapshot.plan()
        precondition(historical.total == 25400)
        let recovered = try await engine.recover(historical.groups[0].payments, month: "A")
        precondition(recovered.map(\.candidate) == draft.rows.map(\.candidate))
        precondition((try? draft.add(old, amount: "1")) == nil)
        precondition((try? draft.add(two, amount: "0")) == nil && draft.rows[1].amount == "400")
        _ = try employee("B", first.name, fakeIBAN(4))
        let fresh = RefundPayrollSearch(modelContainer: container)
        let ambiguous = try? await fresh.searchResult(target, month: "B")
        precondition(ambiguous == nil)
        draft.remove(two.id)
        draft.clear()
        precondition(draft.month == nil && draft.rows.isEmpty)
        let afterClear = try storage.archived()
        precondition(afterClear.count == 1 && afterClear[0].snapshot.rows.count == 2 && RefundDraft(storage: storage).rows.isEmpty)
        let incomplete = try employee("A", "مستفيد تجريبي", fakeIBAN(9))
        incomplete.detailsData = try JSONEncoder().encode(["IBAN": fakeIBAN(9)])
        try context.save()
        let revised = RefundPayrollSearch(modelContainer: container)
        let pending = try await revised.searchResult(.init(id: incomplete.persistentModelID, key: incomplete.matchKey, month: "A"), month: "A")
        precondition(pending.person.payrollBIC == .missingColumn)
        try draft.add(pending)
        try draft.add(pending, amount: "2500")
        precondition(draft.rows.count == 1 && draft.rows[0].amount == "2500")
        let entries = draft.rows.map { EmployeeRefund.Entry(person: $0.candidate.person, amount: $0.amount) }
        precondition((try? EmployeeRefund.plan(entries: entries, banks: [:], payer: "دافع", account: fakeIBAN(8), source: "")) == nil)
        draft.clear()
        func branchCandidate(_ id: PersistentIdentifier, _ serial: Int, _ bic: RefundPayrollBIC) -> RefundCandidate {
            .init(id: id, month: "A", person: .init(name: "تجريبي", rank: "", workplace: "", iban: fakeIBAN(serial, code: "RSHD"),
                                                  bank: "مصرف تجريبي", payrollBIC: bic))
        }
        let branchA = "RSHDIQ00001", branchB = "RSHDIQ00002"
        try draft.add(branchCandidate(one.id, 1, .value(branchB)))
        precondition(draft.letterBICOptions["RSHD"] == [branchB])
        try draft.add(branchCandidate(two.id, 2, .value(branchA)))
        precondition(draft.letterBICOptions["RSHD"] == [branchA, branchB])
        try draft.add(branchCandidate(old.id, 3, .value(branchB)))
        precondition(draft.letterBICOptions["RSHD"] == [branchB])
        draft.remove(old.id)
        draft.remove(one.id)
        precondition(draft.letterBICOptions["RSHD"] == [branchA])
        try draft.add(branchCandidate(old.id, 3, .value("OTHRIQ00")))
        draft.remove(two.id)
        precondition(draft.letterBICOptions["RSHD"] == [])
        print("Shared refund draft selection, editing, month resolution and ambiguity checks passed.")
    }
}
