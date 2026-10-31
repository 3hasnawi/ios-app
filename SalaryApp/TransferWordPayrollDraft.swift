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
        let values = try TransferWordValues.make(month: plan.month, amount: plan.amount, custom: .init(),
            requested: ["AMOUNT", "AMOUNT_WORDS", "MONTH", "YEAR"])
        guard let amount = values["AMOUNT"], let words = values["AMOUNT_WORDS"],
              let monthName = values["MONTH"], let year = values["YEAR"] else { throw Failure.incomplete }
        let reason: String
        switch plan.bookID {
        case "food_company", "food_tax":
            guard let reference = foodContractReference, nonempty(reference) else { throw Failure.incomplete }
            let prefix = plan.bookID == "food_company" ? "عن مستحقات الشركة" : "وذلك عن أمانات ضريبة"
            reason = "\(prefix) عقد تجهيز الأرزاق الجافة والطرية المرقم (\(reference))"
        case "tax_social":
            reason = "وذلك عن مبالغ رسوم الضريبة والحماية الاجتماعية لرواتب قيادة شرطة الأنبار المبينة تفاصيلها أدناه"
        case "contracts":
            reason = "وذلك عن المساهمات التقاعدية بنسبة ١٧٪ الخاصة بالمتعاقدين بصفة رجل أمن ضمن تشكيلات قيادتنا حسب الجدول أدناه"
        case "cash_salary":
            reason = "وذلك عن أمانات رواتب منتسبي قيادتنا المبينة تفاصيلها بالقائمة المرفقة"
        default: throw Failure.incomplete
        }
        let body = "نرجو تحويل مبلغ قدره (\(amount)) (\(words)) لا غيرها من حسابنا المرقم (\(plan.payerIBAN)) المفتوح لديكم إلى حساب (\(plan.beneficiaryName)) المرقم (\(plan.beneficiaryIBAN)) (مصرف الرافدين / فرع الأنبار) \(reason) لشهر (\(monthName) / \(year)) وتزويدنا بالإشعار اللازم بذلك."
        let signatureCells = text.committee.map { $0 + "\n" + period.signatureDate }
        let signatures = TransferWordDocument.Table(headers: [], rows: [signatureCells], font: "Times New Roman", halfPoints: 32,
                                                     columnWeights: [1, 1, 1], showBorders: false)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: text.header, font: "Sakkal Majalla", halfPoints: 36, bold: true)),
            .paragraph(.init(text: "العدد: \(text.fixedNumber)\nالتاريخ: \(period.signatureDate)", halfPoints: 30, bold: true)),
            .paragraph(.init(text: "إلى / مصرف الرافدين / فرع الأنبار", font: "Arial", halfPoints: 32, bold: true)),
            .paragraph(.init(text: "م / تحويل مبلغ", font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(.init(text: body, halfPoints: 28))
        ]
        if plan.bookID != "cash_salary", let table = plan.table { blocks.append(.table(table)) }
        blocks.append(.table(signatures))
        blocks.append(.paragraph(.init(text: text.footer, halfPoints: 24)))
        if plan.bookID == "cash_salary", let table = plan.table {
            blocks.append(.pageBreak)
            blocks.append(.paragraph(.init(text: "قائمة رواتب الكاش · \(monthName) / \(year)", halfPoints: 28, bold: true, centered: true)))
            blocks.append(.table(table))
        }
        if adaptivePDF { try TransferAdaptivePDF.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold, group: false) }
        else { try TransferWordDocument.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold) }
    }
}
