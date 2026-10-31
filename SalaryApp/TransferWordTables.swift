import Foundation

/// صفوف الجداول مأخوذة من مصادر الشهر بعد مطابقة مبالغها؛ لا تؤكد توزيع صفحات Word.
nonisolated enum TransferWordTables {
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? { tr("بيانات الجدول أو مجموعه غير مكتملة أو غير متطابقة.", "The table data or total is incomplete or inconsistent.") }
    }
    private static func valid(_ amount: Double) -> Bool {
        amount.isFinite && amount >= 0 && amount.rounded() == amount && amount <= 9_007_199_254_740_991
    }
    private static func number(_ amount: Double) -> String {
        String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), amount)
    }
    /// يطابق العدد صفوف قائمة الأسماء نفسها دون صف المجموع.
    static func attachment(for table: TransferWordDocument.Table?) throws -> TransferWordDocument.Paragraph {
        guard let table, table.boldLastRow, table.rows.count > 1 else { throw Failure.incomplete }
        let count = ReportSheet.easternDigits(String(table.rows.count - 1))
        return .init(text: "المرفقات:\nقائمة أسماء عدد (\(count))", halfPoints: 24, bold: true)
    }
    static func treasury(_ figures: TransferLetterFigures) throws -> TransferWordDocument.Table {
        guard valid(figures.incomeTax), valid(figures.socialProtection), valid(figures.treasuryTotal) else { throw Failure.incomplete }
        return .init(headers: ["ت", "البيان", "المبلغ"], rows: [
            ["1", "الحماية الاجتماعية", number(figures.socialProtection)],
            ["2", "ضريبة الدخل", number(figures.incomeTax)],
            ["", "المجموع", number(figures.treasuryTotal)]
        ], columnWeights: [1, 6, 3], boldLastRow: true, widthFraction: 0.56, alignRight: true)
    }
    static func contracts(_ figures: TransferLetterFigures) throws -> TransferWordDocument.Table {
        guard figures.missingContractNominal == 0, let nominal = figures.contractNominalShare,
              let total = figures.contractTotal, valid(nominal), valid(figures.contractRetirement), valid(total) else { throw Failure.incomplete }
        return .init(headers: ["مبلغ نسبة ٥٪", "مبلغ نسبة ١٢٪", "المجموع"], rows: [
            [number(figures.contractRetirement), number(nominal), number(total)]
        ])
    }
    static func cash(_ list: TransferCashList, figures: TransferLetterFigures) throws -> TransferWordDocument.Table {
        guard list.month == figures.month, let expected = figures.payrollCash, valid(expected), expected > 0,
              !list.employees.isEmpty, list.employees.count < 50_000,
              list.employees.allSatisfy({ !$0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
                  !$0.rank.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
                  !$0.workplace.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && valid($0.amount) && $0.amount > 0 }),
              valid(list.total), list.total == expected else { throw Failure.incomplete }
        var rows: [[String]] = []
        rows.reserveCapacity(list.employees.count + 1)
        for (index, employee) in list.employees.enumerated() {
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            rows.append([employee.name, employee.rank, employee.workplace, number(employee.amount)])
        }
        rows.append(["", "", "المجموع", number(expected)])
        return .init(headers: ["الاسم", "الرتبة", "محل العمل", "صافي الراتب"], rows: rows, columnWeights: [150, 80, 190, 119], boldLastRow: true)
    }
    /// قائمة مرفقة للجهات التي يذكر كتابها وجود قائمة أسماء.
    static func individual(_ plan: TransferIndividualPlan) throws -> TransferWordDocument.Table {
        guard !plan.rows.isEmpty, plan.rows.count < 50_000, valid(plan.group.total),
              plan.rows.allSatisfy({ !$0.rank.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && valid($0.amount) }) else { throw Failure.incomplete }
        var rows: [[String]] = []
        rows.reserveCapacity(plan.rows.count + 1)
        for (index, row) in plan.rows.enumerated() {
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            rows.append([String(index + 1), row.rank, row.name, number(row.amount)])
        }
        rows.append(["", "", "المجموع", number(plan.group.total)])
        return .init(headers: ["ت", "الرتبة", "الاسم الكامل", "المبلغ"], rows: rows,
                     columnWeights: [40, 90, 260, 120], boldLastRow: true)
    }
    static func anbar(_ plan: TransferIndividualPlan) throws -> TransferWordDocument.Table {
        guard case .rafidainAnbar = plan.kind, !plan.rows.isEmpty, plan.rows.count < 50_000,
              valid(plan.group.total), plan.rows.allSatisfy({ !$0.rank.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && valid($0.amount) }) else { throw Failure.incomplete }
        var rows: [[String]] = []
        rows.reserveCapacity(plan.rows.count + 1)
        for (index, row) in plan.rows.enumerated() {
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            rows.append([String(index + 1), row.rank, row.name, number(row.amount), row.entity])
        }
        rows.append(["", "", "المجموع", number(plan.group.total), ""])
        return .init(headers: ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "جهة التنفيذ"], rows: rows, columnWeights: [40, 65, 149, 74, 180], boldLastRow: true)
    }
}
