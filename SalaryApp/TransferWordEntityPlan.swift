import Foundation

/// بيانات كتاب منفرد من قيود الجهة المراجَعة وحسابها المرتبط صراحةً.
nonisolated struct TransferWordEntityPlan: Sendable {
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? { tr("بيانات جهة الكتاب أو حسابها أو أسماء منتسبيها غير مكتملة.", "The entity, beneficiary account, or employee data is incomplete.") }
    }
    let bookID: String
    let month: String
    let amount: Double
    let payerIBAN: String
    let beneficiaryName: String
    let beneficiaryAccount: String?
    let bank: String?
    let personName: String?
    let personRank: String?
    let table: TransferWordDocument.Table?

    private init(bookID: String, month: String, amount: Double, payerIBAN: String,
                 beneficiaryName: String, beneficiaryAccount: String?, bank: String?,
                 personName: String?, personRank: String?, table: TransferWordDocument.Table?) {
        self.bookID = bookID; self.month = month; self.amount = amount; self.payerIBAN = payerIBAN
        self.beneficiaryName = beneficiaryName; self.beneficiaryAccount = beneficiaryAccount; self.bank = bank
        self.personName = personName; self.personRank = personRank; self.table = table
    }
    static func prepare(plan: TransferIndividualPlan, account: SavedExecutionAccount?, payerIBAN: String,
                        references: Data, accountLinks: Data) throws -> Self {
        guard TransferRules.validIBAN(payerIBAN), plan.group.total.isFinite, plan.group.total > 0,
              plan.group.total.rounded() == plan.group.total, plan.group.total <= 9_007_199_254_740_991 else { throw Failure.incomplete }
        _ = try TransferLetterPeriod(plan.group.month)
        let id: String
        let number: String?
        let personName: String?
        let personRank: String?
        let table: TransferWordDocument.Table?
        switch plan.kind {
        case .ramadi:
            guard let account, TransferAccountLinks.authorizes(title: plan.group.title, account: account, data: accountLinks),
                  TransferRules.validIBAN(account.iban) else { throw Failure.incomplete }
            id = "ramadi"; number = ArabicText.latinDigits(account.iban).uppercased().filter { !$0.isWhitespace && $0 != "-" }
            personName = nil; personRank = nil; table = try TransferWordTables.individual(plan)
        case .housing:
            guard let account, TransferAccountLinks.authorizes(title: plan.group.title, account: account, data: accountLinks),
                  let local = TransferRules.individualLocalAccount(account) else { throw Failure.incomplete }
            id = "housing"; number = local; personName = nil; personRank = nil; table = try TransferWordTables.individual(plan)
        case .water:
            guard let account, TransferAccountLinks.authorizes(title: plan.group.title, account: account, data: accountLinks),
                  let local = TransferRules.individualLocalAccount(account), let employee = plan.rows.first,
                  !employee.rank.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  plan.rows.allSatisfy({ ArabicText.matchKey($0.name) == ArabicText.matchKey(employee.name) &&
                      ArabicText.normalize($0.rank) == ArabicText.normalize(employee.rank) }) else { throw Failure.incomplete }
            id = "water"; number = local; personName = employee.name; personRank = employee.rank; table = nil
        case .rafidainAnbar:
            // هذا المتن موجّه لحساب مصرف الفرع نفسه؛ الجهات الأخرى تحتاج كتبها المنفردة.
            guard TransferRules.isAnbarBankSource(plan.group.title) else { throw Failure.incomplete }
            if let account {
                guard TransferAccountLinks.authorizes(title: plan.group.title, account: account, data: accountLinks),
                      TransferRules.validIBAN(account.iban), TransferRules.classify(account) == .anbar else { throw Failure.incomplete }
            } else if !TransferAccountLinks.allowsDirectAnbar(title: plan.group.title, data: accountLinks) { throw Failure.incomplete }
            id = "rafidain_anbar"; number = nil; personName = nil; personRank = nil
            table = try TransferWordTables.anbar(plan)
        }
        let recipient = account?.entity ?? plan.group.title
        guard !recipient.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw Failure.incomplete }
        try TransferWordReferenceAccounts.verifyIndividual(bookID: id, references: references, payerIBAN: payerIBAN, beneficiary: number)
        return Self(bookID: id, month: plan.group.month, amount: plan.group.total,
                    payerIBAN: ArabicText.latinDigits(payerIBAN).uppercased().filter { !$0.isWhitespace && $0 != "-" },
                    beneficiaryName: recipient, beneficiaryAccount: number, bank: account?.branch,
                    personName: personName, personRank: personRank, table: table)
    }
}
