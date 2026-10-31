import Foundation

/// مسودة للمراجعة فقط؛ لا تثبت عدد صفحات جدول Word ولا تصلح وحدها للاعتماد.
nonisolated enum TransferWordGroupDraft {
    struct LetterText: Sendable {
        let header: String
        let fixedNumber: String
        let committee: [String]
        let footer: String
        var bodyTemplate: String? = nil
        var signatureOffset: Int = 0
        var bodyMonth: String? = nil
        var committeeDates: [String?] = [nil, nil, nil]
    }
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? { tr("تأكد من ترويسة الكتاب والعدد وأسماء اللجنة قبل إعداد المسودة.", "Check the letter header, reference number and committee names before preparing the draft.") }
    }
    static func letterText(from custom: TransferLetterCustomization) throws -> LetterText {
        guard let header = custom.header, let fixedNumber = custom.fixedNumber,
              let first = custom.committeeFirst, let second = custom.committeeSecond,
              let third = custom.committeeThird, let footer = custom.footer,
              (-60...60).contains(custom.signatureOffset ?? 0) else { throw Failure.incomplete }
        let dates = [custom.committeeDateFirst, custom.committeeDateSecond, custom.committeeDateThird]
        for date in dates { _ = try TransferLetterCustomValues.signatureDate(date, fallback: " / 1 / 2026", eastern: false) }
        if let month = custom.bodyMonth, !month.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            _ = try TransferLetterCustomValues.bodyMonth(month, source: month)
        }
        return LetterText(header: header, fixedNumber: fixedNumber, committee: [first, second, third], footer: footer,
            bodyTemplate: custom.bodyTemplate, signatureOffset: custom.signatureOffset ?? 0, bodyMonth: custom.bodyMonth, committeeDates: dates)
    }
    /// الصفات ثابتة كما ظهرت في النماذج الأصلية، والأسماء من تخصيص المستخدم.
    static func signatureCells(_ committee: [String], date: String, adaptivePDF: Bool, dates: [String]? = nil) -> [String] {
        guard committee.count == 3 else { return [] }
        guard adaptivePDF else { return committee.map { $0 + "\n" + date } }
        let signatureDates = (dates?.count == 3 ? dates : nil) ?? [date, date, date]
        let names = committee.map { $0.replacingOccurrences(of: "ـ", with: "").split(whereSeparator: \.isWhitespace).joined(separator: " ") }
        return [
            "معاون مدير تدقيق\n\(names[0])\nالمصروفات\n\(signatureDates[0])",
            "العميد\n\(names[1])\nمدير الحسابات\n\(signatureDates[1])",
            "لواء الشرطة الحقوقي\n\(names[2])\nقائد شرطة محافظة الأنبار\n\(signatureDates[2])"
        ]
    }
    static func dates(for text: LetterText, fallback: String, adaptivePDF: Bool) throws -> [String]? {
        guard adaptivePDF else { return nil }
        guard text.committeeDates.count == 3 else { throw Failure.incomplete }
        return try text.committeeDates.map { try TransferLetterCustomValues.signatureDate($0, fallback: fallback, eastern: true) }
    }
    static func bodyValues(for text: LetterText, sourceMonth: String, amount: Double, adaptivePDF: Bool) throws -> [String: String] {
        let displayedMonth = try TransferLetterCustomValues.bodyMonth(adaptivePDF ? text.bodyMonth : nil, source: sourceMonth)
        return try TransferWordValues.make(month: displayedMonth, amount: amount, custom: .init(), requested: ["MONTH", "YEAR"])
    }
    /// عرض ١٤ مسافة غير قابلة للكسر يقارب عرض خمسة أرقام بخط ساكال المعتمد.
    static func referenceText(number: String, date: String, adaptivePDF: Bool) -> String {
        let writingSpace = adaptivePDF ? String(repeating: "\u{00A0}", count: 14) : " "
        return "العدد: \(number)\nالتاريخ:\(writingSpace)\(date)"
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
        let bodyPeriod = try Self.bodyValues(for: text, sourceMonth: month, amount: plan.total, adaptivePDF: adaptivePDF)
        guard let bodyMonth = bodyPeriod["MONTH"], let bodyYear = bodyPeriod["YEAR"] else { throw Failure.incomplete }
        let body = try TransferLetterBody.render(id: plan.category == .rafidain ? "rafidain_group" : "other_banks_group", custom: adaptivePDF ? text.bodyTemplate : nil, values: [
            "المبلغ": amount, "المبلغ كتابة": words, "حسابنا": normalizedPayer, "الشهر": bodyMonth, "السنة": bodyYear
        ])
        let dates = try Self.dates(for: text, fallback: period.signatureDate, adaptivePDF: adaptivePDF)
        let signatureCells = Self.signatureCells(text.committee, date: signatureDate, adaptivePDF: adaptivePDF, dates: dates)
        let signatures = TransferWordDocument.Table(headers: [], rows: [signatureCells], font: "Times New Roman", halfPoints: 32,
                                                     columnWeights: [1, 1, 1], showBorders: false)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: text.header, font: "Times New Roman", halfPoints: 32, bold: true)),
            .paragraph(.init(text: Self.referenceText(number: letterNumber, date: signatureDate, adaptivePDF: adaptivePDF), font: adaptivePDF ? "Sakkal Majalla" : "Times New Roman", halfPoints: adaptivePDF ? 36 : 30, bold: true)),
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
        if adaptivePDF { try TransferAdaptivePDF.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold, group: true, signatureOffset: text.signatureOffset) }
        else { try TransferWordDocument.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold) }
    }
}
