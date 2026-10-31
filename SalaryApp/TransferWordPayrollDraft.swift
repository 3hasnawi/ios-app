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
              text.committee.allSatisfy(nonempty), nonempty(plan.beneficiaryName) else { throw Failure.incomplete }
        let period = try TransferLetterPeriod(plan.month)
        let signatureDate = adaptivePDF ? ReportSheet.easternDigits(period.signatureDate) : period.signatureDate
        let letterNumber = adaptivePDF ? ReportSheet.easternDigits(text.fixedNumber) : text.fixedNumber
        let values = try TransferWordValues.make(month: plan.month, amount: plan.amount, custom: .init(),
            requested: ["AMOUNT", "AMOUNT_WORDS", "MONTH", "YEAR"])
        guard let amount = values["AMOUNT"], let words = values["AMOUNT_WORDS"],
              let monthName = values["MONTH"], let year = values["YEAR"] else { throw Failure.incomplete }
        var fields = ["المبلغ": amount, "المبلغ كتابة": words, "حسابنا": plan.payerIBAN,
                      "المستفيد": plan.beneficiaryName, "حساب المستفيد": plan.beneficiaryIBAN,
                      "الشهر": monthName, "السنة": year]
        if let reference = foodContractReference { fields["عقد الإطعام"] = reference }
        let body = try TransferLetterBody.render(id: plan.bookID, custom: adaptivePDF ? text.bodyTemplate : nil, values: fields)
        let signatureCells = TransferWordGroupDraft.signatureCells(text.committee, date: signatureDate, adaptivePDF: adaptivePDF)
        let signatures = TransferWordDocument.Table(headers: [], rows: [signatureCells], font: "Times New Roman", halfPoints: 32,
                                                     columnWeights: [1, 1, 1], showBorders: false)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: text.header, font: adaptivePDF ? "Times New Roman" : "Sakkal Majalla", halfPoints: adaptivePDF ? 32 : 36, bold: true)),
            .paragraph(.init(text: TransferWordGroupDraft.referenceText(number: letterNumber, date: signatureDate, adaptivePDF: adaptivePDF), font: adaptivePDF ? "Sakkal Majalla" : "Times New Roman", halfPoints: adaptivePDF ? 36 : 30, bold: true)),
            .paragraph(.init(text: "إلى / مصرف الرافدين / فرع الأنبار", font: "Arial", halfPoints: 32, bold: true, centered: adaptivePDF)),
            .paragraph(.init(text: "م / تحويل مبلغ", font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(.init(text: body, halfPoints: 28))
        ]
        if plan.bookID == "cash_salary" { blocks.append(.paragraph(try TransferWordTables.attachment(for: plan.table))) }
        else if let table = plan.table { blocks.append(.table(table)) }
        blocks.append(.table(signatures))
        blocks.append(.paragraph(.init(text: text.footer, halfPoints: 24, bold: adaptivePDF)))
        if adaptivePDF { try TransferAdaptivePDF.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold, group: false) }
        else { try TransferWordDocument.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold) }
    }
}
