import Foundation

/// مسودة للمراجعة فقط؛ لا تثبت عدد صفحات جدول Word ولا تصلح وحدها للاعتماد.
nonisolated enum TransferWordGroupDraft {
    struct LetterText: Sendable {
        let header: String
        let fixedNumber: String
        let committee: [String]
        let footer: String
    }
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? { tr("تأكد من ترويسة الكتاب والعدد وأسماء اللجنة قبل إعداد المسودة.", "Check the letter header, reference number and committee names before preparing the draft.") }
    }
    static func write(plan: TransferWordGroupPlan, month: String, payerIBAN: String,
                      text: LetterText, destination: URL) throws {
        func nonempty(_ s: String) -> Bool { !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        let normalizedPayer = ArabicText.latinDigits(payerIBAN).uppercased().filter { !$0.isWhitespace && $0 != "-" }
        guard TransferRules.validIBAN(payerIBAN), normalizedPayer == plan.payerIBAN, month == plan.month,
              nonempty(text.header), nonempty(text.fixedNumber),
              text.committee.count == 3, text.committee.allSatisfy(nonempty), nonempty(text.footer) else { throw Failure.incomplete }
        let period = try TransferLetterPeriod(month)
        let values = try TransferWordValues.make(month: month, amount: plan.total, custom: .init(),
            requested: ["AMOUNT", "AMOUNT_WORDS", "MONTH", "YEAR"])
        guard let amount = values["AMOUNT"], let words = values["AMOUNT_WORDS"],
              let monthName = values["MONTH"], let year = values["YEAR"] else { throw Failure.incomplete }
        let body = "نرجو تحويل مبلغ قدره (\(amount)) (\(words)) لا غيرها من حسابنا المرقم (\(normalizedPayer)) المفتوح لديكم إلى الحسابات المدرجة تفاصيلها في القائمة المرفقة مع قرص بصيغة (CSV) عن استقطاعات شهر (\(monthName) / \(year)) وتزويدنا بالإشعار اللازم بذلك."
        let signatures = TransferWordDocument.Table(headers: [], rows: [text.committee], halfPoints: 28,
                                                     columnWeights: [1, 1, 1], showBorders: false)
        try TransferWordDocument.write(blocks: [
            .paragraph(.init(text: text.header, halfPoints: 32, bold: true)),
            .paragraph(.init(text: "العدد: \(text.fixedNumber)\nالتاريخ: \(period.signatureDate)", halfPoints: 28)),
            .paragraph(.init(text: "إلى / مصرف الرافدين / فرع الأنبار", halfPoints: 32, bold: true)),
            .paragraph(.init(text: "م / تحويل مبلغ", halfPoints: 32, bold: true, centered: true)),
            .paragraph(.init(text: body, halfPoints: 28)),
            .table(signatures),
            .paragraph(.init(text: text.footer, halfPoints: 24)),
            .pageBreak,
            .paragraph(.init(text: "استقطاعات قيادة شرطة الأنبار لشهر (\(monthName) / \(year))", halfPoints: 28, bold: true, centered: true)),
            .table(plan.table)
        ], destination: destination)
    }
}
