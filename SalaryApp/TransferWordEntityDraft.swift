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
              text.committee.count == 3, (text.template == nil ? text.committee.allSatisfy(nonempty) : text.committee.contains(where: nonempty)),
              nonempty(plan.beneficiaryName),
              plan.bookID != "rafidain_anbar" || plan.table != nil else { throw Failure.incomplete }
        let signatureDate = try TransferLetterCustomValues.outgoingDate(adaptivePDF ? text.outgoingMonth : nil, sourceMonth: plan.month, eastern: adaptivePDF)
        let letterNumber = adaptivePDF ? ReportSheet.easternDigits(text.fixedNumber) : text.fixedNumber
        let values = try TransferWordValues.make(month: plan.month, amount: plan.amount, custom: .init(),
            requested: ["AMOUNT", "AMOUNT_WORDS"])
        guard let amount = values["AMOUNT"], let words = values["AMOUNT_WORDS"] else { throw Failure.incomplete }
        let bodyPeriod = try TransferWordGroupDraft.bodyValues(for: text, sourceMonth: plan.month, amount: plan.amount, adaptivePDF: adaptivePDF)
        guard let bodyMonth = bodyPeriod["MONTH"], let bodyYear = bodyPeriod["YEAR"] else { throw Failure.incomplete }
        var fields = ["المبلغ": TransferWordGroupDraft.bodyNumber(amount, adaptivePDF: adaptivePDF), "المبلغ كتابة": words, "حسابنا": plan.payerIBAN,
                      "المستفيد": plan.beneficiaryName, "الشهر": bodyMonth, "السنة": TransferWordGroupDraft.bodyNumber(bodyYear, adaptivePDF: adaptivePDF)]
        if let value = plan.beneficiaryAccount { fields["حساب المستفيد"] = value }
        if let value = plan.bank { fields["المصرف"] = value }
        if let value = plan.personRank { fields["الرتبة"] = value }
        if let value = plan.personName { fields["الاسم"] = value }
        let bodyTemplate = text.template == nil ? text.bodyTemplate : text.bodyTemplate.map(CustomTransferTemplate.formatBodyNumbers)
        let body = try TransferLetterBody.render(id: plan.bookID, custom: adaptivePDF ? bodyTemplate : nil, values: fields)
        let signatures = TransferWordDocument.Table(headers: [], rows: [try TransferWordGroupDraft.signatures(for: text, date: signatureDate, adaptivePDF: adaptivePDF)],
            font: "Times New Roman", halfPoints: 32, columnWeights: [1, 1, 1], showBorders: false)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: text.header, font: adaptivePDF ? (text.template != nil && sakkalBold != nil ? "Sakkal Majalla" : "Times New Roman") : "Sakkal Majalla", halfPoints: adaptivePDF ? 32 : 36, bold: true)),
            .paragraph(.init(text: TransferWordGroupDraft.referenceText(number: letterNumber, date: signatureDate, adaptivePDF: adaptivePDF), font: adaptivePDF ? "Sakkal Majalla" : "Times New Roman", halfPoints: adaptivePDF ? 36 : 30, bold: true)),
            .paragraph(.init(text: "إلى / " + (text.template?.recipient ?? "مصرف الرافدين / فرع الأنبار"), font: "Arial", halfPoints: 32, bold: true, centered: adaptivePDF)),
            .paragraph(.init(text: "م / " + (text.template?.subject ?? "تحويل مبلغ"), font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(TransferWordGroupDraft.bodyParagraph(body, template: text.template))
        ]
        if plan.table != nil { blocks.append(.paragraph(try TransferWordTables.attachment(for: plan.table))) }
        TransferWordGroupDraft.appendAttachments(text, to: &blocks)
        blocks.append(.table(signatures))
        blocks.append(.paragraph(.init(text: text.footer, halfPoints: 24, bold: adaptivePDF)))
        if adaptivePDF { try TransferAdaptivePDF.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold, group: false, signatureOffset: text.signatureOffset, signatureHorizontalOffset: text.template?.signatureHorizontalOffset ?? 0) }
        else { try TransferWordDocument.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold) }
    }
}
