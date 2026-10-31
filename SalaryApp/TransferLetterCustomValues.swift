import Foundation

/// قيم اختيارية للكتاب، دون تغيير بيانات الشهر أو حسابات التحويل المحفوظة.
nonisolated enum TransferLetterCustomValues {
    enum Failure: LocalizedError {
        case date, month, account, sourceChanged, bankChanged
        var errorDescription: String? {
            switch self {
            case .date: tr("أدخل تاريخ التوقيع بصيغة يوم/شهر/سنة أو /شهر/سنة، مع شهر وسنة صالحين.", "Enter a valid signature date as day/month/year or /month/year.")
            case .month: tr("اختر شهراً وسنة صالحين لمتن الكتاب.", "Choose a valid body month and year.")
            case .account: tr("رقم حساب المستفيد غير صالح لهذا الكتاب؛ تحقق من نوعه وأرقامه.", "The beneficiary account is invalid for this letter; check its type and digits.")
            case .sourceChanged: tr("تغيّرت الجهة أو حسابها الأصلي؛ راجع تخصيص حساب الكتاب واحفظه مجدداً.", "The source entity or account changed; review and save the letter account again.")
            case .bankChanged: tr("الحساب البديل يتبع مصرفاً مختلفاً عن المصرف المذكور في الكتاب؛ راجع بيانات المصرف أولاً.", "The alternate account belongs to a different bank than the letter states; review the bank details first.")
            }
        }
    }
    static func bodyMonth(_ value: String?, source: String) throws -> String {
        let custom = value?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !custom.isEmpty else { return source }
        guard MonthName.sortKey(custom) > 0 else { throw Failure.month }
        return custom
    }
    static func signatureDate(_ value: String?, fallback: String, eastern: Bool) throws -> String {
        let raw = ArabicText.latinDigits(value ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !raw.isEmpty else { return eastern ? ReportSheet.easternDigits(fallback) : fallback }
        var parts = raw.components(separatedBy: "/").map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        if parts.count == 2 { parts.insert("", at: 0) }
        guard parts.count == 3, parts[0].utf8.count <= 2, parts[1].utf8.count <= 2, parts[2].utf8.count == 4,
              parts.allSatisfy({ $0.utf8.allSatisfy { (48...57).contains($0) } }),
              let month = Int(parts[1]), (1...12).contains(month), let year = Int(parts[2]), (1900...2200).contains(year) else { throw Failure.date }
        var dayText = ""
        if !parts[0].isEmpty {
            guard let day = Int(parts[0]), (1...31).contains(day) else { throw Failure.date }
            var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(secondsFromGMT: 0)!
            guard let value = calendar.date(from: DateComponents(year: year, month: month, day: day)),
                  calendar.component(.year, from: value) == year, calendar.component(.month, from: value) == month,
                  calendar.component(.day, from: value) == day else { throw Failure.date }
            dayText = String(day)
        }
        let formatted = "\(dayText) / \(month) / \(year)"
        return eastern ? ReportSheet.easternDigits(formatted) : formatted
    }
    static func normalizedAccount(_ value: String) -> String {
        ArabicText.latinDigits(value).uppercased().filter { !$0.isWhitespace && $0 != "-" }
    }
    static func account(_ custom: TransferLetterCustomization, bookID: String, source: String, original: String, reference: String) throws -> String? {
        guard let value = custom.beneficiaryAccount?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else { return nil }
        let normalized = normalizedAccount(value), current = normalizedAccount(original), checkedReference = normalizedAccount(reference)
        guard custom.beneficiarySource == source, let prior = custom.beneficiaryOriginal, !prior.isEmpty,
              let pinnedReference = custom.beneficiaryReference, !pinnedReference.isEmpty, pinnedReference == checkedReference,
              prior == current || normalized == current else { throw Failure.sourceChanged }
        switch bookID {
        case "housing", "water":
            guard [normalized, checkedReference, prior].allSatisfy({ $0.utf8.count == 15 && $0.utf8.allSatisfy { (48...57).contains($0) } }) else { throw Failure.account }
        case "ramadi", "food_company", "food_tax", "tax_social", "contracts", "cash_salary":
            guard TransferRules.validIBAN(normalized), TransferRules.validIBAN(checkedReference) else { throw Failure.account }
            guard normalized.dropFirst(4).prefix(4) == prior.dropFirst(4).prefix(4) else { throw Failure.bankChanged }
        default: throw Failure.account
        }
        return normalized
    }
}
