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
    static func letterText(from custom: TransferLetterCustomization) throws -> LetterText {
        guard let header = custom.header, let fixedNumber = custom.fixedNumber,
              let first = custom.committeeFirst, let second = custom.committeeSecond,
              let third = custom.committeeThird, let footer = custom.footer else { throw Failure.incomplete }
        return LetterText(header: header, fixedNumber: fixedNumber, committee: [first, second, third], footer: footer)
    }
    /// الصفات ثابتة كما ظهرت في النماذج الأصلية، والأسماء من تخصيص المستخدم.
    static func signatureCells(_ committee: [String], date: String, adaptivePDF: Bool) -> [String] {
        guard committee.count == 3 else { return [] }
        guard adaptivePDF else { return committee.map { $0 + "\n" + date } }
        let names = committee.map { $0.replacingOccurrences(of: "ـ", with: "").split(whereSeparator: \.isWhitespace).joined(separator: " ") }
        return [
            "معاون مدير تدقيق\n\(names[0])\nالمصروفات\n\(date)",
            "العميد\n\(names[1])\nمدير الحسابات\n\(date)",
            "لواء الشرطة الحقوقي\n\(names[2])\nقائد شرطة محافظة الأنبار\n\(date)"
        ]
    }
    static func write(plan: TransferWordGroupPlan, month: String, payerIBAN: String,
                      text: LetterText, destination: URL, sakkalBold: Data? = nil, adaptivePDF: Bool = false) throws {
        func nonempty(_ s: String) -> Bool { !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        let normalizedPayer = ArabicText.latinDigits(payerIBAN).uppercased().filter { !$0.isWhitespace && $0 != "-" }
        guard TransferRules.validIBAN(payerIBAN), normalizedPayer == plan.payerIBAN, month == plan.month,
              nonempty(text.header), nonempty(text.fixedNumber),
              text.committee.count == 3, text.committee.allSatisfy(nonempty) else { throw Failure.incomplete }
        let period = try TransferLetterPeriod(month)
        let signatureDate = adaptivePDF ? ReportSheet.easternDigits(period.signatureDate) : period.signatureDate
        let letterNumber = adaptivePDF ? ReportSheet.easternDigits(text.fixedNumber) : text.fixedNumber
        let values = try TransferWordValues.make(month: month, amount: plan.total, custom: .init(),
            requested: ["AMOUNT", "AMOUNT_WORDS", "MONTH", "YEAR"])
        guard let amount = values["AMOUNT"], let words = values["AMOUNT_WORDS"],
              let monthName = values["MONTH"], let year = values["YEAR"] else { throw Failure.incomplete }
        let body = "نرجو تحويل مبلغ قدره (\(amount)) (\(words)) لا غيرها من حسابنا المرقم (\(normalizedPayer)) المفتوح لديكم إلى الحسابات المدرجة تفاصيلها في القائمة المرفقة مع قرص بصيغة (CSV) عن استقطاعات شهر (\(monthName) / \(year)) وتزويدنا بالإشعار اللازم بذلك."
        let signatureCells = Self.signatureCells(text.committee, date: signatureDate, adaptivePDF: adaptivePDF)
        let signatures = TransferWordDocument.Table(headers: [], rows: [signatureCells], font: "Times New Roman", halfPoints: 32,
                                                     columnWeights: [1, 1, 1], showBorders: false)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: text.header, font: "Times New Roman", halfPoints: 32, bold: true)),
            .paragraph(.init(text: "العدد: \(letterNumber)\nالتاريخ: \(signatureDate)", font: adaptivePDF ? "Sakkal Majalla" : "Times New Roman", halfPoints: adaptivePDF ? 36 : 30, bold: true)),
            .paragraph(.init(text: "إلى / مصرف الرافدين / فرع الأنبار", font: "Arial", halfPoints: 32, bold: true, centered: adaptivePDF)),
            .paragraph(.init(text: "م / تحويل مبلغ", font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(.init(text: body, halfPoints: 28))
        ]
        if adaptivePDF {
            let count = plan.table.rows.count - 1
            guard count > 0 else { throw Failure.incomplete }
            blocks.append(.paragraph(.init(text: "المرفقات:\nقائمة أسماء الحسابات من تسلسل (1) لغاية تسلسل (\(count))\nقرص (CSV).", halfPoints: 24, bold: true)))
        }
        blocks += [
            .table(signatures),
            .paragraph(.init(text: text.footer, halfPoints: 24, bold: adaptivePDF)),
            .pageBreak,
            .paragraph(.init(text: "استقطاعات قيادة شرطة الأنبار لشهر (\(monthName) / \(year))", halfPoints: 28, bold: true, centered: true)),
            .table(plan.table)
        ]
        if adaptivePDF { try TransferAdaptivePDF.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold, group: true) }
        else { try TransferWordDocument.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold) }
    }
}
