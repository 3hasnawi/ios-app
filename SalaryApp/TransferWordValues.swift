import Foundation

/// قيم مشتركة لقوالب Word المُجهزة، دون الاعتماد على أرقام النموذج القديم.
nonisolated enum TransferWordValues {
    enum Failure: LocalizedError {
        case invalidAmount, unknownField(String)
        var errorDescription: String? {
            switch self {
            case .invalidAmount: tr("مبلغ الكتاب غير صالح.", "The letter amount is invalid.")
            case .unknownField(let field): tr("حقل الكتاب غير متاح: \(field).", "Letter field is unavailable: \(field).")
            }
        }
    }

    /// تُطلب فقط الحقول الموجودة في القالب؛ لا تُستبدل التخصيصات غير المحددة بنص فارغ.
    static func make(month: String, amount: Double, custom: TransferLetterCustomization,
                     requested: Set<String>) throws -> [String: String] {
        guard amount.isFinite, amount >= 0, amount.rounded() == amount,
              amount <= 9_007_199_254_740_991 else { throw Failure.invalidAmount }
        let period = try TransferLetterPeriod(month)
        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.numberStyle = .decimal
        formatter.maximumFractionDigits = 0
        guard let numeric = formatter.string(from: NSNumber(value: amount)) else { throw Failure.invalidAmount }
        let names = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"]
        let available: [String: String?] = [
            "MONTH": names[period.month - 1],
            "MONTH_LABEL": period.name,
            "MONTH_NUMBER": String(period.month),
            "YEAR": String(period.year),
            "SIGNATURE_DATE": period.signatureDate,
            "AMOUNT": numeric,
            "AMOUNT_WORDS": try ArabicAmountWords.dinars(Int64(amount)),
            "HEADER": custom.header,
            "COMMITTEE_FIRST": custom.committeeFirst,
            "COMMITTEE_SECOND": custom.committeeSecond,
            "COMMITTEE_THIRD": custom.committeeThird,
            "FOOTER": custom.footer
        ]
        var result: [String: String] = [:]
        for key in requested {
            guard let value = available[key] ?? nil else { throw Failure.unknownField(key) }
            result[key] = value
        }
        return result
    }
}
