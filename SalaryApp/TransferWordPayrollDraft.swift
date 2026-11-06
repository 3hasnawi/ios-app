import Foundation

/// مسودة للمراجعة بعد التحقق من حساب المستفيد ومصدر الراتب. لا تعني اعتماد التصدير.
nonisolated enum TransferWordPayrollDraft {
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? { tr("بيانات الكتاب أو رقم عقد الإطعام أو التوقيعات غير مكتملة.", "The letter, food contract reference, or signatures are incomplete.") }
    }
    static func write(plan: TransferWordPayrollPlan, text: TransferWordGroupDraft.LetterText,
                      foodContractReference: String? = nil, destination: URL, sakkalBold: Data? = nil, adaptivePDF: Bool = false) throws {
        func nonempty(_ s: String) -> Bool { !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        guard TransferRules.validIBAN(plan.payerIBAN), TransferRules.validIBAN(plan.beneficiaryIBAN),
              nonempty(text.header), nonempty(text.fixedNumber), text.committee.count == 3,
              (text.template == nil ? text.committee.allSatisfy(nonempty) : text.committee.contains(where: nonempty)), nonempty(plan.beneficiaryName) else { throw Failure.incomplete }
        let signatureDate = try TransferLetterCustomValues.outgoingDate(adaptivePDF ? text.outgoingMonth : nil, sourceMonth: plan.month, eastern: adaptivePDF)
        let letterNumber = adaptivePDF ? ReportSheet.easternDigits(text.fixedNumber) : text.fixedNumber
        let values = try TransferWordValues.make(month: plan.month, amount: plan.amount, custom: .init(),
            requested: ["AMOUNT", "AMOUNT_WORDS"])
        guard let amount = values["AMOUNT"], let words = values["AMOUNT_WORDS"] else { throw Failure.incomplete }
        let bodyPeriod = try TransferWordGroupDraft.bodyValues(for: text, sourceMonth: plan.month, amount: plan.amount, adaptivePDF: adaptivePDF)
        guard let bodyMonth = bodyPeriod["MONTH"], let bodyYear = bodyPeriod["YEAR"] else { throw Failure.incomplete }
        var fields = ["المبلغ": TransferWordGroupDraft.bodyNumber(amount, adaptivePDF: adaptivePDF), "المبلغ كتابة": words, "حسابنا": plan.payerIBAN,
                      "المستفيد": plan.beneficiaryName, "حساب المستفيد": plan.beneficiaryIBAN,
                      "الشهر": bodyMonth, "السنة": TransferWordGroupDraft.bodyNumber(bodyYear, adaptivePDF: adaptivePDF)]
        if let reference = foodContractReference { fields["عقد الإطعام"] = reference }
        let bodyTemplate = text.template == nil ? text.bodyTemplate : text.bodyTemplate.map(CustomTransferTemplate.formatBodyNumbers)
        let body = try TransferLetterBody.render(id: plan.bookID, custom: adaptivePDF ? bodyTemplate : nil, values: fields)
        let signatureCells = try TransferWordGroupDraft.signatures(for: text, date: signatureDate, adaptivePDF: adaptivePDF)
        let signatures = TransferWordDocument.Table(headers: [], rows: [signatureCells], font: "Times New Roman", halfPoints: text.template?.halfPoints(.signatures, default: 32) ?? 32,
                                                     columnWeights: [1, 1, 1], showBorders: false)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: text.header, font: "Times New Roman", halfPoints: text.template?.halfPoints(.header, default: adaptivePDF ? 32 : 36) ?? (adaptivePDF ? 32 : 36), bold: true)),
            .paragraph(.init(text: TransferWordGroupDraft.referenceText(number: letterNumber, date: signatureDate, adaptivePDF: adaptivePDF), font: "Times New Roman", halfPoints: text.template?.halfPoints(.reference, default: adaptivePDF ? 36 : 30) ?? (adaptivePDF ? 36 : 30), bold: true)),
            .paragraph(.init(text: "إلى / " + (text.template?.recipient ?? "مصرف الرافدين / فرع الأنبار"), font: "Arial", halfPoints: text.template?.halfPoints(.recipient, default: 32) ?? 32, bold: true, centered: adaptivePDF)),
            .paragraph(.init(text: "م / " + (text.template?.subject ?? "تحويل مبلغ"), font: "Arial", halfPoints: text.template?.halfPoints(.subject, default: 32) ?? 32, bold: true, centered: true)),
            .paragraph(TransferWordGroupDraft.bodyParagraph(body, template: text.template))
        ]
        if plan.bookID == "cash_salary" {
            var attachment = try TransferWordTables.attachment(for: plan.table)
            attachment.halfPoints = text.template?.halfPoints(.attachments, default: attachment.halfPoints) ?? attachment.halfPoints
            blocks.append(.paragraph(attachment))
            TransferWordGroupDraft.appendAttachments(text, to: &blocks)
        } else {
            TransferWordGroupDraft.appendAttachments(text, to: &blocks)
            if var table = plan.table { table.halfPoints = text.template?.halfPoints(.table, default: table.halfPoints) ?? table.halfPoints; blocks.append(.table(table)) }
        }
        blocks.append(.table(signatures))
        blocks.append(.paragraph(.init(text: text.footer, halfPoints: text.template?.halfPoints(.footer, default: 24) ?? 24, bold: adaptivePDF)))
        if adaptivePDF { try TransferAdaptivePDF.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold, group: false, signatureOffset: text.signatureOffset, signatureHorizontalOffset: text.template?.signatureHorizontalOffset ?? 0, template: text.template) }
        else { try TransferWordDocument.write(blocks: blocks, destination: destination, sakkalBold: nil) }
    }
}
