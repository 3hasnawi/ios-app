import Foundation

/// بيانات جدول مجموعة Word بعد تدقيقها مع ملف التحويل، دون ادعاء احتواء الصفحة.
nonisolated struct TransferWordGroupPlan: Sendable {
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? {
            tr("تعذّر تجهيز جدول المجموعة: راجع تصنيف الجهات والحسابات والمبالغ.", "Could not prepare the group table; review entity classification, accounts and amounts.")
        }
    }
    let category: TransferCategory
    let total: Double
    let table: TransferWordDocument.Table
    let csv: Data
    let month: String
    let payerIBAN: String

    init(allEntities: [TransferEntity], category: TransferCategory, month: String,
         payer: String, payerIBAN: String, date: Date = .now, adaptivePDF: Bool = false) throws {
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
        var rows: [[String]] = entries.enumerated().map { index, entry in
            [String(index + 1), entry.beneficiaryName ?? entry.group.title,
             ArabicText.latinDigits(entry.account?.iban ?? "").uppercased().filter { !$0.isWhitespace && $0 != "-" },
             entry.account?.branch ?? "", String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), entry.group.total)]
        }
        rows.append(["", "", "", "المجموع", String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), sum)])
        let headers = ["ت", "المديرية", "رقم الايبان", "الفرع", "المبلغ"]
        let weights = [40, 252, 322, 220, 166]
        // هامش إضافي للعناوين واختلافات عرض Word. يجب معاينة الصفحة الفعلية أيضاً.
        let widths = weights.map { CGFloat(510.3 * Double($0) / 1000) }
        let halfPoints: Int
        if adaptivePDF {
            // يقيس منشئ PDF صفوفه الفعلية؛ لا نرفض الجدول بمقاييس Word المختلفة.
            halfPoints = 22
        } else {
            let fit = try TransferSinglePageFit.fit(rows: [headers] + rows, widths: widths, availableHeight: 680,
                fontName: "Arial-BoldMT", maximumSize: 11, minimumSize: 9, verticalPadding: 5)
            halfPoints = Int((fit.fontSize * 2).rounded())
        }
        self.category = category
        self.total = sum
        self.csv = data
        self.month = month
        self.payerIBAN = ArabicText.latinDigits(payerIBAN).uppercased().filter { !$0.isWhitespace && $0 != "-" }
        self.table = TransferWordDocument.Table(headers: headers, rows: rows, halfPoints: halfPoints,
                                                columnWeights: weights, boldLastRow: true)
    }
}
