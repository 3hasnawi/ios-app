import Foundation

/// ملخص خفيف للعرض؛ لا تنتقل قيود الدفتر كلها إلى بطاقة البحث أو مسار التنقل.
nonisolated struct SalaryTrustSearchSummary: Hashable, Sendable {
    let key: String
    let name: String
    let months: Int
    let total: Int64
    let paid: Int64
    let remaining: Int64
    let review: Int64
    var allPaid: Bool { total > 0 && remaining == 0 && review == 0 }
    init(_ person: SalaryTrust.Person) {
        key = person.id; name = person.name
        months = Set(person.entries.map { $0.year * 100 + $0.month }).count
        total = person.total; paid = person.paid; remaining = person.remaining; review = person.reviewTotal
    }
}

actor SalaryTrustSearch {
    static let shared = SalaryTrustSearch()
    private var snapshot: Data?
    private var people: [SalaryTrust.Person] = []
    private var byKey: [String: SalaryTrustSearchSummary] = [:]

    private func prepare(_ data: Data) throws {
        guard snapshot != data else { return }
        let ledger = try SalaryTrust.decode(data)
        let grouped = SalaryTrust.people(ledger.entries)
        let summaries = grouped.map(SalaryTrustSearchSummary.init)
        people = grouped
        byKey = Dictionary(uniqueKeysWithValues: summaries.map { ($0.key, $0) })
        snapshot = data
    }
    func summaries(keys: [String], data: Data) throws -> [String: SalaryTrustSearchSummary] {
        try prepare(data)
        return keys.reduce(into: [:]) { if let value = byKey[$1] { $0[$1] = value } }
    }
    struct Match: Sendable { let summary: SalaryTrustSearchSummary; let field: String }
    func find(_ input: String, data: Data) throws -> [Match] {
        try prepare(data)
        let query = AutomaticSearchQuery(input)
        guard !query.isEmpty else { return [] }
        var matches: [Match] = []
        for (index, person) in people.enumerated() {
            if index.isMultiple(of: 128) { try Task.checkCancellation() }
            for entry in person.entries {
                let fields = [
                    AutomaticSearchQuery.Field(label: tr("الاسم", "Name"), value: entry.name),
                    .init(label: tr("الرتبة", "Rank"), value: entry.rank),
                    .init(label: tr("محل العمل", "Workplace"), value: entry.workplace),
                    .init(label: tr("الشهر", "Month"), value: SalaryTrust.monthLabel(entry.year * 100 + entry.month)),
                    .init(label: tr("الحالة", "Status"), value: entry.importedStatus + " " + SalaryTrust.paymentStatus(entry)),
                    .init(label: tr("الملاحظات", "Notes"), value: entry.displayNotes),
                    .init(label: tr("تاريخ الصرف", "Payment date"), value: entry.displayPaymentDate),
                    .init(label: tr("المبلغ", "Amount"), value: String(entry.amount))]
                if let field = query.match(fields), let summary = byKey[person.id] {
                    matches.append(Match(summary: summary, field: field)); break
                }
            }
            if matches.count >= 100 { break }
        }
        return matches
    }
}
