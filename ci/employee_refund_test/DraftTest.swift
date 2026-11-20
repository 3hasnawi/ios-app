import Foundation
import SwiftData

@main enum DraftTest {
    @MainActor static func main() async throws {
        let container = try ModelContainer(for: Employee.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let context = container.mainContext
        func employee(_ month: String, _ name: String, _ iban: String) throws -> Employee {
            let person = Employee(name: name)
            person.month = month
            person.detailsData = try JSONEncoder().encode(["IBAN": iban, "Receiver BIC": "RAFBIQ00"])
            context.insert(person)
            try context.save()
            return person
        }
        func fakeIBAN(_ suffix: Int) -> String {
            let bban = "RAFB" + String(format: "%015d", suffix)
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
        let draft = RefundDraft.shared
        try draft.add(one)
        try draft.add(one, amount: "25000")
        try draft.add(two, amount: "400")
        precondition(target.isIn(draft) && draft.rows.count == 2 && draft.rows.map(\.amount) == ["25000", "400"])
        precondition((try? draft.add(old, amount: "1")) == nil)
        precondition((try? draft.add(two, amount: "0")) == nil && draft.rows[1].amount == "400")
        _ = try employee("B", first.name, fakeIBAN(4))
        let fresh = RefundPayrollSearch(modelContainer: container)
        let ambiguous = try? await fresh.searchResult(target, month: "B")
        precondition(ambiguous == nil)
        draft.remove(two.id)
        draft.clear()
        precondition(draft.month == nil && draft.rows.isEmpty)
        print("Shared refund draft selection, editing, month resolution and ambiguity checks passed.")
    }
}
