import Foundation

/// تدقيق الحسابات قبل إعداد مسودة كتاب الخزينة؛ لا يغيّر البيانات المستوردة.
nonisolated enum TreasuryLetterRules {
    enum Failure: LocalizedError {
        case incomplete, amounts, sourceChanged, pageCount
        var errorDescription: String? {
            switch self {
            case .incomplete: tr("أكمل بيانات الكتاب والحسابات والتواقيع قبل المعاينة.", "Complete the letter details, accounts and signatures before preview.")
            case .amounts: tr("راجع الأسماء والرتب ومبالغ البنود؛ يجب أن تتطابق مع تفاصيل الرواتب وتكون المبالغ صحيحة وموجبة.", "Check names, ranks and amounts against payroll; amounts must be positive whole numbers.")
            case .sourceChanged: tr("تغيّرت بيانات الشهر أثناء إعداد الكتاب؛ أعد المحاولة.", "The month's data changed while preparing the letter. Try again.")
            case .pageCount: tr("لا يتسع الكتاب وجدوله في صفحة واحدة؛ راجع طول البيانات أو استخدم كشفاً منفصلاً.", "The letter and table do not fit on one page; review the content or use a separate report.")
            }
        }
    }
    static func validAmount(_ n: Double) -> Bool {
        n.isFinite && n > 0 && n.rounded() == n && n <= 9_007_199_254_740_991
    }
    static func total(_ values: [Double]) throws -> Double {
        guard !values.isEmpty, values.allSatisfy(validAmount) else { throw Failure.amounts }
        let sum = values.reduce(0, +)
        guard validAmount(sum) else { throw Failure.amounts }
        return sum
    }
    static func validateRows(_ rows: [ReportRow], summary: PayrollItems.Summary, requireRank: Bool = true) throws -> Double {
        guard rows.count == summary.count,
              rows.allSatisfy({ !$0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
                  (!requireRank || !$0.rank.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty) }) else { throw Failure.amounts }
        let sum = try total(rows.map(\.amount))
        guard sum == summary.total else { throw Failure.amounts }
        return sum
    }
}
