import SwiftUI
import SwiftData

/// تُجمع الاستقطاعات أولاً للشخص ثم للشخص والجهة والإضبارة، حتى لا يُحسب توقف جهة كتوقف كامل.
nonisolated struct DeductionSnapshot: Sendable {
    let name: String
    let person: String
    let entity: String
    let note: String
    let amount: Double?

    init(name: String, entity: String, note: String = "", amount: Double?) {
        self.name = name
        person = ArabicText.matchKey(name)
        self.entity = entity
        self.note = note
        self.amount = amount
    }
}

nonisolated enum DeductionChangeKind: String, CaseIterable, Identifiable, Sendable {
    case started, stopped, increased, decreased, unchanged
    var id: String { rawValue }
    var title: String {
        switch self {
        case .started: tr("بدأ", "Started")
        case .stopped: tr("توقف", "Stopped")
        case .increased: tr("زاد", "Increased")
        case .decreased: tr("نقص", "Decreased")
        case .unchanged: tr("ثابت", "Unchanged")
        }
    }
}

nonisolated struct DeductionChange: Identifiable, Sendable {
    let id: Int
    let person: String
    let name: String
    let entity: String
    let note: String
    let before: Double
    let after: Double
    let kind: DeductionChangeKind
    var difference: Double { after - before }
}

/// حالات العرض والتصدير: توقف، جديد، تغيّر المبلغ (زاد أو نقص لنفس الجهة والإضبارة).
nonisolated enum DeductionGroupKind: String, CaseIterable, Identifiable, Sendable {
    case stopped, started, changed
    var id: String { rawValue }
    var title: String {
        switch self {
        case .stopped: tr("توقف عنهم", "Stopped")
        case .started: tr("استقطاع جديد", "New")
        case .changed: tr("تغيّر المبلغ", "Amount changed")
        }
    }
    var reportTitle: String {
        switch self {
        case .stopped: tr("الاستقطاعات المتوقفة", "Stopped deductions")
        case .started: tr("الاستقطاعات الجديدة", "New deductions")
        case .changed: tr("تغيّر مبلغ الاستقطاع (نفس الجهة والإضبارة)", "Changed deduction amounts")
        }
    }
    func contains(_ kind: DeductionChangeKind) -> Bool {
        switch self {
        case .stopped: kind == .stopped
        case .started: kind == .started
        case .changed: kind == .increased || kind == .decreased
        }
    }
}

nonisolated struct DeductionComparison: Sendable {
    let from: String
    let to: String
    let people: [DeductionChange]
    let entries: [DeductionChange]
    let emptyMonths: [String]
    var before: Double { people.reduce(0) { $0 + $1.before } }
    var after: Double { people.reduce(0) { $0 + $1.after } }
    /// لكل استقطاع (الموظف + الجهة + الإضبارة)، الأكبر فرقاً أولاً.
    func rows(_ kind: DeductionGroupKind) -> [DeductionChange] {
        entries.filter { kind.contains($0.kind) }.sorted { abs($0.difference) > abs($1.difference) }
    }
}

nonisolated enum DeductionReview {
    struct Key: Hashable {
        let person: String
        let entity: String
        var caseReference: String = ""
    }
    private struct DuplicateKey: Hashable {
        let key: Key
        let note: String
        let amount: Double
    }
    private struct Total {
        var name: String
        var entity: String
        var note: String
        var amount: Double
    }
    struct InvalidData: LocalizedError {
        var errorDescription: String? {
            tr("تعذّرت المقارنة: يوجد استقطاع بلا اسم أو جهة، أو بمبلغ فارغ أو سالب أو غير صالح. راجع ملف الاستقطاعات ثم أعد استيراده.",
               "Cannot compare: a deduction has no name or entity, or an empty, negative or invalid amount. Review and reimport the deduction file.")
        }
    }

    static func normalized(_ value: String) -> String {
        ArabicText.normalize(ArabicText.latinDigits(value))
    }

    static func duplicates(_ records: [DeductionSnapshot]) -> AuditCheck {
        var groups: [DuplicateKey: [DeductionSnapshot]] = [:]
        for r in records {
            guard !r.person.isEmpty, !normalized(r.entity).isEmpty,
                  let amount = r.amount, amount.isFinite, amount >= 0 else { continue }
            let key = DuplicateKey(key: Key(person: r.person, entity: normalized(r.entity)),
                                   note: normalized(r.note), amount: amount)
            groups[key, default: []].append(r)
        }
        let repeated = groups.values.filter { $0.count > 1 }.sorted {
            let a = $0[0], b = $1[0]
            return (a.name, a.entity, a.note, a.amount ?? 0) < (b.name, b.entity, b.note, b.amount ?? 0)
        }
        let rows = repeated.enumerated().map { i, group in
            let r = group[0]
            let file = r.note.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? tr("الإضبارة غير متوفرة", "Case reference missing") : r.note
            let detail = "\(r.entity) · \(file) · "
                + tr("\(Num.format(group.count)) مرات × \(Money.format(r.amount))", "\(group.count) copies × \(Money.format(r.amount))")
            return AuditRow(id: i, name: r.name, detail: detail, amount: (r.amount ?? 0) * Double(group.count))
        }
        return AuditCheck(id: "duplicateDeductions", title: tr("تكرار محتمل في الاستقطاعات", "Potential duplicate deductions"),
                          subtitle: rows.isEmpty ? tr("لا توجد قيود متطابقة", "No identical entries")
                          : tr("نفس الاسم والجهة والمبلغ والإضبارة؛ للمراجعة دون حذف تلقائي", "Same name, entity, amount and case reference; review without automatic deletion"),
                          ok: rows.isEmpty, rows: rows, amountTitle: tr("مجموع القيود", "Entries total"))
    }

    static func compare(from: String, to: String, old: [DeductionSnapshot], new: [DeductionSnapshot]) throws -> DeductionComparison {
        func totals(_ records: [DeductionSnapshot], byEntity: Bool) throws -> [Key: Total] {
            var result: [Key: Total] = [:]
            for (i, r) in records.enumerated() {
                if i.isMultiple(of: 256) { try Task.checkCancellation() }
                guard !r.person.isEmpty, !normalized(r.entity).isEmpty,
                      let amount = r.amount, amount.isFinite, amount >= 0 else { throw InvalidData() }
                // القيد الصفري لا يعني استقطاعاً فعلياً، والقيود المتكررة تبقى ضمن المبلغ المسجل.
                guard amount > 0 else { continue }
                let key = Key(person: r.person, entity: byEntity ? normalized(r.entity) : "",
                              caseReference: byEntity ? normalized(r.note) : "")
                var total = result[key] ?? Total(name: r.name, entity: byEntity ? r.entity : "", note: byEntity ? r.note : "", amount: 0)
                total.amount += amount
                guard total.amount.isFinite else { throw InvalidData() }
                result[key] = total
            }
            return result
        }
        func changes(byEntity: Bool) throws -> [DeductionChange] {
            let a = try totals(old, byEntity: byEntity), b = try totals(new, byEntity: byEntity)
            let keys = Set(a.keys).union(b.keys).sorted { ($0.person, $0.entity, $0.caseReference) < ($1.person, $1.entity, $1.caseReference) }
            return keys.enumerated().map { i, key in
                let before = a[key]?.amount ?? 0, after = b[key]?.amount ?? 0
                let kind: DeductionChangeKind
                if before == 0 { kind = .started }
                else if after == 0 { kind = .stopped }
                else if abs(after - before) < 0.000001 { kind = .unchanged }
                else { kind = after > before ? .increased : .decreased }
                return DeductionChange(id: i, person: key.person, name: b[key]?.name ?? a[key]!.name,
                                       entity: b[key]?.entity ?? a[key]!.entity, note: b[key]?.note ?? a[key]!.note,
                                       before: before, after: after, kind: kind)
            }
        }
        return try DeductionComparison(from: from, to: to, people: changes(byEntity: false), entries: changes(byEntity: true),
                                       emptyMonths: (old.isEmpty ? [from] : []) + (new.isEmpty ? [to] : []))
    }
}

extension ReportEngine {
    func deductionComparison(from: String, to: String) throws -> DeductionComparison {
        func load(_ month: String) throws -> [DeductionSnapshot] {
            var descriptor = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
            descriptor.propertiesToFetch = [\.employeeName, \.title, \.note, \.amount]
            return try modelContext.fetch(descriptor).map {
                DeductionSnapshot(name: $0.employeeName, entity: $0.title, note: $0.note, amount: $0.amount)
            }
        }
        return try DeductionReview.compare(from: from, to: to, old: load(from), new: load(to))
    }
}
