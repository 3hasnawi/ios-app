import Foundation

/// بيانات جدول مجموعة Word بعد تدقيقها مع ملف التحويل، دون ادعاء احتواء الصفحة.
nonisolated struct TransferWordGroupPlan: Sendable {
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? {
            tr("تعذّر تجهيز جدول المجموعة: راجع تصنيف الجهات والحسابات والمبالغ.", "Could not prepare the group table; review entity classification, accounts and amounts.")
        }
    }
    let total: Double
    let table: TransferWordDocument.Table
    let csv: Data

    init(allEntities: [TransferEntity], category: TransferCategory, month: String,
         payer: String, payerIBAN: String, date: Date = .now) throws {
        guard category == .rafidain || category == .otherBanks, !allEntities.isEmpty,
              Set(allEntities.map(\.id)).count == allEntities.count,
              allEntities.allSatisfy({ $0.group.month == month && $0.category != .unresolved }) else { throw Failure.incomplete }
        let entries = allEntities.filter { $0.category == category }
        guard !entries.isEmpty, entries.allSatisfy({ TransferRules.issues($0).isEmpty }) else { throw Failure.incomplete }
        let sum = entries.reduce(0.0) { $0 + $1.group.total }
        guard sum.isFinite, sum > 0, sum.rounded() == sum, sum <= 9_007_199_254_740_991 else { throw Failure.incomplete }
        let data = try TransferRules.csv(entries, category: category, month: month, payer: payer, payerIBAN: payerIBAN, date: date)
        let verified = try TransferGroupCSVCheck.validate(data, entities: entries, category: category, month: month, payer: payer, payerIBAN: payerIBAN)
        let expected = entries.map { TransferReconcileLine(sourceID: $0.id, title: $0.group.title, amount: Int64($0.group.total)) }
        guard TransferReconciliation.check(expected: expected, book: expected, file: verified, target: .grouped).isEmpty else { throw Failure.incomplete }
        let rows: [[String]] = entries.enumerated().map { index, entry in
            [String(index + 1), entry.beneficiaryName ?? entry.group.title,
             ArabicText.latinDigits(entry.account?.iban ?? "").uppercased().filter { !$0.isWhitespace && $0 != "-" },
             entry.account?.branch ?? "", String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), entry.group.total)]
        }
        self.total = sum
        self.csv = data
        self.table = TransferWordDocument.Table(headers: ["ت", "المديرية", "رقم الايبان", "الفرع", "المبلغ"], rows: rows)
    }
}
