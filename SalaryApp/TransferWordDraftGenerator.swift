import Foundation

/// إعداد مسودة Word وملف المجموعة عند الحاجة. لا يشارك الملفات أو يعتمدها.
nonisolated enum TransferWordDraftGenerator {
    struct Result: Sendable { let letter: URL; let csv: URL? }
    enum Failure: LocalizedError {
        case incomplete, changed, sourceChanged
        var errorDescription: String? {
            switch self {
            case .incomplete: tr("بيانات الكتاب غير مكتملة؛ راجع الجهة والتخصيصات والمراجع.", "The letter data is incomplete; review its beneficiary, customization and references.")
            case .changed: tr("تعذّر التحقق من ملف التحويل بعد إنشائه.", "The transfer file could not be verified after creation.")
            case .sourceChanged: tr("تغيّرت بيانات الشهر السابق؛ أعد فتح المعاينة.", "The previous month's data changed; reopen the preview.")
            }
        }
    }
    static func make(bookID: String, figures: TransferLetterFigures, entities: [TransferEntity],
                     entity: TransferEntity?, individualPlan: TransferIndividualPlan?, cashList: TransferCashList?,
                     references: Data, accountLinks: Data, payerName: String, payerIBAN: String,
                     customization: TransferLetterCustomization, sakkalBold: Data? = nil, adaptivePDF: Bool = false) throws -> Result {
        let text = try TransferWordGroupDraft.letterText(from: customization)
        if bookID != "rafidain_group", bookID != "other_banks_group", let sakkalBold {
            let font = try TransferHeaderFont.make(data: sakkalBold, style: .bold, size: 18)
            let header = text.header.filter { !$0.isNewline }
            try TransferHeaderFont.requireSupported(header, font: font)
        }
        let fm = FileManager.default
        let root = fm.temporaryDirectory.appendingPathComponent("word-draft-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.protectionKey: FileProtectionType.complete])
        var complete = false
        defer { if !complete { try? fm.removeItem(at: root) } }
        let letter = root.appendingPathComponent(adaptivePDF ? "مسودة الكتاب.pdf" : "مسودة الكتاب.docx")
        var csvURL: URL?
        switch bookID {
        case "rafidain_group", "other_banks_group":
            let category: TransferCategory = bookID == "rafidain_group" ? .rafidain : .otherBanks
            try TransferWordReferenceAccounts.verifyGroup(bookID: bookID, references: references, payerIBAN: payerIBAN)
            let plan = try TransferWordGroupPlan(allEntities: entities, category: category, month: figures.month,
                payer: payerName, payerIBAN: payerIBAN)
            try TransferWordGroupDraft.write(plan: plan, month: figures.month, payerIBAN: payerIBAN, text: text, destination: letter, sakkalBold: sakkalBold, adaptivePDF: adaptivePDF)
            if adaptivePDF {
                var checks = [plan.payerIBAN, String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), plan.total)]
                for row in plan.table.rows.dropLast() { checks.append(row[2]); checks.append(row[4]) }
                try TransferAdaptivePDF.verify(letter, required: checks)
            } else { try TransferWordVerification.verify(url: letter, month: plan.month, amount: plan.total, payerIBAN: plan.payerIBAN,
                text: text, table: plan.table) }
            let csv = root.appendingPathComponent(category == .rafidain ? "Rafidain.csv" : "OtherBanks.csv")
            try plan.csv.write(to: csv, options: [.atomic, .completeFileProtection])
            guard try Data(contentsOf: csv) == plan.csv else { throw Failure.changed }
            let entries = entities.filter { $0.category == category }
            _ = try TransferGroupCSVCheck.validate(Data(contentsOf: csv), entities: entries, category: category,
                month: figures.month, payer: payerName, payerIBAN: payerIBAN)
            csvURL = csv
        case "food_company", "food_tax", "tax_social", "contracts", "cash_salary":
            let plan = try TransferWordPayrollPlan.prepare(bookID: bookID, figures: figures,
                payerIBAN: payerIBAN, references: references, cashList: cashList)
            try TransferWordPayrollDraft.write(plan: plan, text: text,
                foodContractReference: customization.foodContractReference, destination: letter, sakkalBold: sakkalBold, adaptivePDF: adaptivePDF)
            if adaptivePDF {
                var checks = [plan.payerIBAN, plan.beneficiaryIBAN, String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), plan.amount)]
                if let table = plan.table { for row in table.rows { checks.append(contentsOf: row.filter { !$0.isEmpty && $0.allSatisfy(\.isNumber) }) } }
                try TransferAdaptivePDF.verify(letter, required: checks)
            } else { try TransferWordVerification.verify(url: letter, month: plan.month, amount: plan.amount, payerIBAN: plan.payerIBAN,
                text: text, table: plan.table, beneficiaryName: plan.beneficiaryName, beneficiaryAccount: plan.beneficiaryIBAN,
                bank: "مصرف الرافدين / فرع الأنبار",
                contractReference: bookID == "food_company" || bookID == "food_tax" ? customization.foodContractReference : nil) }
        case "ramadi", "housing", "water", "rafidain_anbar":
            guard let entity, let individualPlan, entity.group.id == individualPlan.group.id,
                  entity.group.month == figures.month, entity.group.total == individualPlan.group.total,
                  entity.missingAmounts == 0 else { throw Failure.incomplete }
            let plan = try TransferWordEntityPlan.prepare(plan: individualPlan, account: entity.account,
                payerIBAN: payerIBAN, references: references, accountLinks: accountLinks)
            try TransferWordEntityDraft.write(plan: plan, text: text, destination: letter, sakkalBold: sakkalBold, adaptivePDF: adaptivePDF)
            if adaptivePDF {
                var checks = [plan.payerIBAN, String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), plan.amount)]
                if let account = plan.beneficiaryAccount { checks.append(account) }
                if let table = plan.table { for row in table.rows { checks.append(contentsOf: row.filter { !$0.isEmpty && $0.allSatisfy(\.isNumber) }) } }
                try TransferAdaptivePDF.verify(letter, required: checks)
            } else { try TransferWordVerification.verify(url: letter, month: plan.month, amount: plan.amount, payerIBAN: plan.payerIBAN,
                text: text, table: plan.table, beneficiaryName: bookID == "rafidain_anbar" ? nil : plan.beneficiaryName,
                beneficiaryAccount: plan.beneficiaryAccount, bank: bookID == "rafidain_anbar" ? nil : plan.bank,
                personName: plan.personName, personRank: plan.personRank) }
        default: throw Failure.incomplete
        }
        try Task.checkCancellation()
        complete = true
        return Result(letter: letter, csv: csvURL)
    }
}
