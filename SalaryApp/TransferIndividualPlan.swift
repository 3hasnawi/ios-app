import Foundation

/// لقطة مستند جهة منفردة؛ اختيار الجهة صريح لمنع تخمين المستفيد من تشابه الأسماء.
nonisolated struct TransferIndividualPlan: Sendable {
    enum Kind: Sendable { case water, housing, ramadi, rafidainAnbar }
    enum Failure: LocalizedError {
        case invalidSource, mismatchedRows, waterRequiresOnePerson
        var errorDescription: String? {
            switch self {
            case .invalidSource: tr("بيانات جهة الكتاب غير مكتملة أو غير صالحة.", "The letter entity data is incomplete or invalid.")
            case .mismatchedRows: tr("أسماء الجهة أو مبالغها لا تطابق مجموع الكشف للشهر المختار.", "The entity rows or amounts do not match the selected month's report.")
            case .waterRequiresOnePerson: tr("كتاب الموارد المائية مخصص لمنتسب واحد؛ راجع قيود الجهة قبل الإصدار.", "The water resources letter requires one employee; review the entity entries.")
            }
        }
    }
    let kind: Kind
    let group: ReportGroup
    let rows: [ReportRow]

    init(kind: Kind, group: ReportGroup, rows: [ReportRow], missingAmounts: Int) throws {
        guard group.kind == .deductions, !group.byEntity, !group.month.isEmpty,
              !group.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              missingAmounts == 0, !rows.isEmpty,
              group.total.isFinite, group.total > 0, group.total.rounded() == group.total,
              group.total <= 9_007_199_254_740_991,
              rows.allSatisfy({ !$0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
                  $0.entity == group.title && $0.amount.isFinite && $0.amount >= 0 && $0.amount.rounded() == $0.amount }) else { throw Failure.invalidSource }
        let people = Set(rows.map { ArabicText.matchKey($0.name) })
        guard !people.contains(""), people.count == group.count,
              rows.reduce(0.0, { $0 + $1.amount }) == group.total else { throw Failure.mismatchedRows }
        if case .water = kind, people.count != 1 { throw Failure.waterRequiresOnePerson }
        self.kind = kind
        self.group = group
        self.rows = rows
    }
}
