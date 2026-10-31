import Foundation

/// مسودة الكتب المنفردة المرتبطة بجهة استقطاع؛ لا تُعد اعتماداً نهائياً.
nonisolated enum TransferWordEntityDraft {
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? { tr("بيانات الجهة أو التوقيعات غير مكتملة.", "The entity or signature details are incomplete.") }
    }
    static func write(plan: TransferWordEntityPlan, text: TransferWordGroupDraft.LetterText,
                      destination: URL, sakkalBold: Data? = nil, adaptivePDF: Bool = false) throws {
        func nonempty(_ s: String) -> Bool { !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        guard TransferRules.validIBAN(plan.payerIBAN), nonempty(text.header), nonempty(text.fixedNumber),
              text.committee.count == 3, text.committee.allSatisfy(nonempty),
              nonempty(plan.beneficiaryName),
              plan.bookID != "rafidain_anbar" || plan.table != nil else { throw Failure.incomplete }
        let period = try TransferLetterPeriod(plan.month)
        let signatureDate = adaptivePDF ? ReportSheet.easternDigits(period.signatureDate) : period.signatureDate
        let letterNumber = adaptivePDF ? ReportSheet.easternDigits(text.fixedNumber) : text.fixedNumber
        let values = try TransferWordValues.make(month: plan.month, amount: plan.amount, custom: .init(),
            requested: ["AMOUNT", "AMOUNT_WORDS", "MONTH", "YEAR"])
        guard let amount = values["AMOUNT"], let words = values["AMOUNT_WORDS"],
              let monthName = values["MONTH"], let year = values["YEAR"] else { throw Failure.incomplete }
        var fields = ["المبلغ": amount, "المبلغ كتابة": words, "حسابنا": plan.payerIBAN,
                      "المستفيد": plan.beneficiaryName, "الشهر": monthName, "السنة": year]
        if let value = plan.beneficiaryAccount { fields["حساب المستفيد"] = value }
        if let value = plan.bank { fields["المصرف"] = value }
        if let value = plan.personRank { fields["الرتبة"] = value }
        if let value = plan.personName { fields["الاسم"] = value }
        let body = try TransferLetterBody.render(id: plan.bookID, custom: adaptivePDF ? text.bodyTemplate : nil, values: fields)
        let signatures = TransferWordDocument.Table(headers: [], rows: [TransferWordGroupDraft.signatureCells(text.committee, date: signatureDate, adaptivePDF: adaptivePDF)],
            font: "Times New Roman", halfPoints: 32, columnWeights: [1, 1, 1], showBorders: false)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: text.header, font: adaptivePDF ? "Times New Roman" : "Sakkal Majalla", halfPoints: adaptivePDF ? 32 : 36, bold: true)),
            .paragraph(.init(text: "العدد: \(letterNumber)\nالتاريخ: \(signatureDate)", font: adaptivePDF ? "Sakkal Majalla" : "Times New Roman", halfPoints: adaptivePDF ? 36 : 30, bold: true)),
            .paragraph(.init(text: "إلى / مصرف الرافدين / فرع الأنبار", font: "Arial", halfPoints: 32, bold: true, centered: adaptivePDF)),
            .paragraph(.init(text: "م / تحويل مبلغ", font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(.init(text: body, halfPoints: 28)),
            .table(signatures),
            .paragraph(.init(text: text.footer, halfPoints: 24, bold: adaptivePDF))
        ]
        if let table = plan.table {
            blocks.append(.pageBreak)
            blocks.append(.paragraph(.init(text: "قائمة استقطاعات \(plan.beneficiaryName) · \(monthName) / \(year)", halfPoints: 28, bold: true, centered: true)))
            blocks.append(.table(table))
        }
        if adaptivePDF { try TransferAdaptivePDF.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold, group: false) }
        else { try TransferWordDocument.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold) }
    }
}
