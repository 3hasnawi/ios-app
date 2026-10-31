import Foundation

/// لقطة كتاب رواتب منفرد: الحساب من المرجع الخاص والمبالغ من ملف الشهر.
nonisolated struct TransferWordPayrollPlan: Sendable {
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? { tr("بيانات كتاب الرواتب أو مرجعه غير مكتملة.", "The payroll letter data or reference is incomplete.") }
    }
    let bookID: String
    let month: String
    let amount: Double
    let payerIBAN: String
    let beneficiaryName: String
    let beneficiaryIBAN: String
    let table: TransferWordDocument.Table?

    private init(bookID: String, month: String, amount: Double, payerIBAN: String,
                 beneficiaryName: String, beneficiaryIBAN: String, table: TransferWordDocument.Table?) {
        self.bookID = bookID; self.month = month; self.amount = amount; self.payerIBAN = payerIBAN
        self.beneficiaryName = beneficiaryName; self.beneficiaryIBAN = beneficiaryIBAN; self.table = table
    }

    static func prepare(bookID: String, figures: TransferLetterFigures, payerIBAN: String,
                        references: Data, cashList: TransferCashList? = nil,
                        customization: TransferLetterCustomization = .init(), adaptivePDF: Bool = false) throws -> Self {
        let name: String
        let table: TransferWordDocument.Table?
        switch bookID {
        case "food_company":
            name = "شركة عراق الجوال للتجارة والنقل والمقاولات والتجهيزات الغذائية المحدودة"; table = nil
        case "food_tax":
            name = "مديرية خزينة الأنبار"; table = nil
        case "tax_social":
            name = "مديرية خزينة الأنبار"; table = try TransferWordTables.treasury(figures, eastern: adaptivePDF)
        case "contracts":
            name = "قسم التقاعد والضمان الاجتماعي للعمال في محافظة الأنبار"; table = try TransferWordTables.contracts(figures, eastern: adaptivePDF)
        case "cash_salary":
            guard let cashList else { throw Failure.incomplete }
            name = "مديرية خزينة الأنبار"; table = try TransferWordTables.cash(cashList, figures: figures)
        default: throw Failure.incomplete
        }
        let amount = try TransferWordValues.payrollAmount(bookID: bookID, figures: figures)
        guard amount > 0, TransferRules.validIBAN(payerIBAN) else { throw Failure.incomplete }
        _ = try TransferLetterPeriod(figures.month)
        let original = try TransferWordReferenceAccounts.beneficiary(bookID: bookID, references: references, payerIBAN: payerIBAN)
        let beneficiary = try TransferLetterCustomValues.account(customization, bookID: bookID, source: bookID, original: original, reference: original) ?? original
        guard beneficiary != TransferLetterCustomValues.normalizedAccount(payerIBAN) else { throw Failure.incomplete }
        return Self(bookID: bookID, month: figures.month, amount: amount,
                    payerIBAN: ArabicText.latinDigits(payerIBAN).uppercased().filter { !$0.isWhitespace && $0 != "-" },
                    beneficiaryName: name, beneficiaryIBAN: beneficiary, table: table)
    }
}
