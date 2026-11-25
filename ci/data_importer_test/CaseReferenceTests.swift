import Foundation
import SwiftData

@MainActor func testCaseReferences(container: ModelContainer) async throws {
    let importer = DataImporter(modelContainer: container)
    let person = DeductionPerson(key: ArabicText.matchKey("منتسب تجريبي"), name: "منتسب تجريبي", detail: "")
    let entity = "جهة تجريبية"
    func row(_ ref: String, _ amount: Double?, name: String = "منتسب تجريبي", title: String = "جهة تجريبية") -> ImportedDeduction {
        .init(employeeName: name, title: title, amount: amount, note: ref)
    }
    try await importer.replaceDeductions(month: "أيلول 2026", with: [
        row("١٢ / ٢٠٢٥", 40), row("12/2025", 10), row("12/2026", 60), row("", 7),
        row("99", 1000, name: "منتسب آخر"), row("88", 900, title: "جهة أخرى")
    ])
    try await importer.replaceDeductions(month: "تشرين الأول 2026", with: [
        row(DeductionStatus.encode("موقوف", note: "12/2025"), nil), row(DeductionStatus.encode("هارب", note: ""), nil)
    ])
    let engine = EmployeeDeductionEngine(modelContainer: container)
    let options = try await engine.availableMonths(person: person, entity: entity)
    precondition(Set(options.references.map(\.id)) == ["12/2025", "12/2026"])
    let selected = options.references.first { $0.id == "12/2025" }!
    precondition(options.byReference[selected.id]?.keys == [202609, 202610])
    precondition(options.byReference["12/2026"]?.keys == [202609])
    let filtered = try await engine.statement(person: person, entity: entity, from: 202609, to: 202610, reference: selected)
    precondition(filtered.total == 50 && filtered.references == [selected.title])
    if case .status(nil, ["موقوف"]) = filtered.rows[1].state {} else { fatalError("Wrong case status") }
    let all = try await engine.statement(person: person, entity: entity, from: 202609, to: 202610)
    precondition(all.total == 117)
    try await importer.replaceDeductions(month: "تشرين الثاني 2026", with: [row("", 9, title: "جهة بلا إضبارة")])
    let noCases = try await engine.availableMonths(person: person, entity: "جهة بلا إضبارة")
    precondition(noCases.references.isEmpty && noCases.all.keys == [202611])
    print("Employee case selection, dates, repeated references, statuses and combined totals passed")
}
