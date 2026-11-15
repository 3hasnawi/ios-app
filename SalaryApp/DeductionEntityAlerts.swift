import Foundation

nonisolated enum DeductionEntityAlertKind: String, CaseIterable, Identifiable, Sendable {
    case added, missing
    var id: String { rawValue }
    var title: String {
        switch self {
        case .added: tr("جهات جديدة", "New entities")
        case .missing: tr("جهات غائبة عن الشهر", "Entities absent this month")
        }
    }
}

nonisolated struct DeductionEntityAlert: Identifiable, Sendable {
    let key: String
    let title: String
    let kind: DeductionEntityAlertKind
    let people: Int
    let amount: Double
    var id: String { kind.rawValue + "|" + key }
}

nonisolated struct DeductionEntityAlertSummary: Sendable {
    let alerts: [DeductionEntityAlert]
    func rows(_ kind: DeductionEntityAlertKind) -> [DeductionEntityAlert] { alerts.filter { $0.kind == kind } }
}

nonisolated struct DeductionAlertMonth: Sendable {
    let name: String
    let order: Int
    let deductions: Int
}

/// المقارنة حسب الجهة مستقلة عن تغيّر الموظف أو الإضبارة داخل الجهة المستمرة.
nonisolated enum DeductionEntityAlerts {
    private struct Total {
        var titles = Set<String>()
        var beforePeople = Set<String>()
        var afterPeople = Set<String>()
        var before: Double = 0
        var after: Double = 0
    }
    struct Incomplete: LocalizedError {
        var errorDescription: String? {
            tr("بيانات المقارنة غير مكتملة؛ راجع استيراد ملفي الاستقطاعات قبل اعتماد تنبيهات الجهات.",
               "Comparison data is incomplete. Check both deduction imports before using entity alerts.")
        }
    }
    static func key(_ title: String) -> String { DeductionReview.normalized(title) }

    /// لا نعتمد شهر رواتب بلا ملف استقطاعات، ولا نتنبأ بتاريخ اسم شهر غير مفهوم أو ملتبس.
    static func previousMonth(selected: String, months: [DeductionAlertMonth]) -> String? {
        guard let current = months.first(where: { $0.name == selected }), current.order > 0,
              current.deductions > 0 else { return nil }
        let candidates = months.filter { $0.deductions > 0 && $0.order > 0 && $0.order < current.order }
        guard let order = candidates.map(\.order).max(), candidates.filter({ $0.order == order }).count == 1,
              months.filter({ $0.order == current.order && $0.deductions > 0 }).count == 1 else { return nil }
        return candidates.first { $0.order == order }?.name
    }

    static func summarize(_ comparison: DeductionComparison) throws -> DeductionEntityAlertSummary {
        guard comparison.emptyMonths.isEmpty else { throw Incomplete() }
        var totals: [String: Total] = [:]
        for (index, row) in comparison.entries.enumerated() {
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            let key = key(row.entity)
            guard !key.isEmpty, !row.person.isEmpty,
                  row.before.isFinite, row.after.isFinite, row.before >= 0, row.after >= 0 else { throw Incomplete() }
            var total = totals[key] ?? Total()
            total.titles.insert(row.entity)
            if row.before > 0 { total.beforePeople.insert(row.person) }
            if row.after > 0 { total.afterPeople.insert(row.person) }
            total.before += row.before
            total.after += row.after
            guard total.before.isFinite, total.after.isFinite else { throw Incomplete() }
            totals[key] = total
        }
        let alerts = totals.compactMap { key, total -> DeductionEntityAlert? in
            let kind: DeductionEntityAlertKind
            if total.before == 0 && total.after > 0 { kind = .added }
            else if total.before > 0 && total.after == 0 { kind = .missing }
            else { return nil }
            let title = total.titles.sorted { ArabicText.alphabeticalOrder($0, $1) == .orderedAscending }.first ?? key
            return DeductionEntityAlert(key: key, title: title, kind: kind,
                people: kind == .added ? total.afterPeople.count : total.beforePeople.count,
                amount: kind == .added ? total.after : total.before)
        }.sorted { ArabicText.alphabeticalOrder($0.title, $1.title) == .orderedAscending }
        return DeductionEntityAlertSummary(alerts: alerts)
    }
}
