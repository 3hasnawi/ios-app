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
              nonempty(plan.beneficiaryName) else { throw Failure.incomplete }
        let period = try TransferLetterPeriod(plan.month)
        let values = try TransferWordValues.make(month: plan.month, amount: plan.amount, custom: .init(),
            requested: ["AMOUNT", "AMOUNT_WORDS", "MONTH", "YEAR"])
        guard let amount = values["AMOUNT"], let words = values["AMOUNT_WORDS"],
              let monthName = values["MONTH"], let year = values["YEAR"] else { throw Failure.incomplete }
        let target: String
        let reason: String
        switch plan.bookID {
        case "ramadi", "housing", "water":
            guard let account = plan.beneficiaryAccount, nonempty(account), let bank = plan.bank, nonempty(bank) else { throw Failure.incomplete }
            target = "إلى حساب (\(plan.beneficiaryName)) المرقم (\(account)) والمفتوح لدى (\(bank))"
            switch plan.bookID {
            case "ramadi": reason = "وذلك عن المبالغ المستقطعة والمحسومة من منتسبي قيادة شرطة الأنبار وحسب القوائم المرفقة طياً"
            case "housing": reason = "وذلك عن مبالغ السلف والقروض المستقطعة والمحسومة من منتسبي قيادة شرطة الأنبار المدرجة أسماؤهم في القائمة المرفقة طياً"
            default:
                guard let rank = plan.personRank, let name = plan.personName, nonempty(rank), nonempty(name) else { throw Failure.incomplete }
                reason = "وذلك عن مبالغ استقطاع منسوبنا \(rank) (\(name))"
            }
        case "rafidain_anbar":
            guard plan.table != nil else { throw Failure.incomplete }
            target = "إلى حساب مصرفكم (مصرف الرافدين / فرع الأنبار)"
            reason = "وذلك عن مبالغ السلف والقروض المستقطعة والمحسومة من منتسبينا المدرجة أسماؤهم في القائمة المرفقة"
        default: throw Failure.incomplete
        }
        let body = "نرجو تحويل مبلغ قدره (\(amount)) (\(words)) لا غيرها من حسابنا المرقم (\(plan.payerIBAN)) المفتوح لديكم \(target) \(reason) لشهر (\(monthName) / \(year)) وتزويدنا بالإشعار اللازم بذلك."
        let signatures = TransferWordDocument.Table(headers: [], rows: [TransferWordGroupDraft.signatureCells(text.committee, date: period.signatureDate, adaptivePDF: adaptivePDF)],
            font: "Times New Roman", halfPoints: 32, columnWeights: [1, 1, 1], showBorders: false)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: text.header, font: adaptivePDF ? "Times New Roman" : "Sakkal Majalla", halfPoints: adaptivePDF ? 32 : 36, bold: true)),
            .paragraph(.init(text: "العدد: \(text.fixedNumber)\nالتاريخ: \(period.signatureDate)", font: adaptivePDF ? "Sakkal Majalla" : "Times New Roman", halfPoints: adaptivePDF ? 36 : 30, bold: true)),
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
