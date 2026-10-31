import Foundation

/// ربط صريح مستقل عن الشهر؛ يعاد استخدامه فقط إن لم تتغير هوية المستفيد وحسابه.
nonisolated enum TransferLetterEntityLinks {
    static let settingsKey = "transferLetterEntityLinks"
    struct Link: Codable, Equatable, Sendable {
        let title: String
        let accountKey: String?
        let iban: String?
        let localAccount: String?
        init(_ entity: TransferEntity) {
            title = ArabicText.normalize(entity.group.title)
            accountKey = entity.account?.key
            iban = entity.account.map { ArabicText.latinDigits($0.iban).uppercased().filter { !$0.isWhitespace && $0 != "-" } }
            if let local = entity.account?.localAccount { localAccount = ArabicText.latinDigits(local).filter(\.isNumber) }
            else { localAccount = nil }
        }
    }
    static func decode(_ data: Data) throws -> [String: Link] {
        if data.isEmpty { return [:] }
        return try JSONDecoder().decode([String: Link].self, from: data)
    }
    static func resolve(_ link: Link, candidates: [TransferEntity], accountLinks: Data) -> TransferEntity? {
        let matches = candidates.filter { entity in
            guard Link(entity) == link else { return false }
            if let account = entity.account { return TransferAccountLinks.authorizes(title: entity.group.title, account: account, data: accountLinks) }
            return entity.isDirectAnbarBank && TransferAccountLinks.allowsDirectAnbar(title: entity.group.title, data: accountLinks)
        }
        return matches.count == 1 ? matches[0] : nil
    }
    /// بعد تحديث قائمة الحسابات، يقبل الربط السابق إذا أصبح الحساب المحدث هو الحساب الذي اختاره المستخدم للكتاب.
    static func resolveUpdated(_ link: Link, candidates: [TransferEntity], accountLinks: Data,
                               bookID: String, customization: TransferLetterCustomization) -> TransferEntity? {
        guard ["ramadi", "housing", "water"].contains(bookID),
              let override = customization.beneficiaryAccount, !override.isEmpty else { return nil }
        let previous: String?
        if bookID == "ramadi" { previous = link.iban }
        else { previous = link.localAccount ?? link.iban.flatMap { TransferRules.localAccountDigits($0) } }
        guard let previous, let pinned = customization.beneficiaryOriginal,
              pinned == TransferLetterCustomValues.normalizedAccount(previous) || pinned == TransferLetterCustomValues.normalizedAccount(override) else { return nil }
        let matches = candidates.filter { entity in
            guard ArabicText.normalize(entity.group.title) == link.title, let account = entity.account,
                  account.key == link.accountKey, TransferAccountLinks.authorizes(title: entity.group.title, account: account, data: accountLinks) else { return false }
            let original = bookID == "ramadi" ? account.iban : (TransferRules.individualLocalAccount(account) ?? "")
            guard TransferLetterCustomValues.normalizedAccount(original) == TransferLetterCustomValues.normalizedAccount(override) else { return false }
            return (try? TransferLetterCustomValues.account(customization, bookID: bookID, source: entity.group.title, original: original, reference: customization.beneficiaryReference ?? "")) != nil
        }
        return matches.count == 1 ? matches[0] : nil
    }
    static func uniqueReferenceMatch(bookID: String, candidates: [TransferEntity], references: Data,
                                     payerIBAN: String, accountLinks: Data) throws -> TransferEntity? {
        let reference = try TransferWordReferenceAccounts.individualBeneficiary(bookID: bookID, references: references, payerIBAN: payerIBAN)
        let matches = candidates.filter { entity in
            if bookID == "rafidain_anbar" {
                guard reference == nil, entity.category == .anbar, TransferRules.isAnbarBankSource(entity.group.title) else { return false }
                if let account = entity.account { return TransferRules.classify(account) == .anbar && TransferAccountLinks.authorizes(title: entity.group.title, account: account, data: accountLinks) }
                return entity.isDirectAnbarBank && TransferAccountLinks.allowsDirectAnbar(title: entity.group.title, data: accountLinks)
            }
            guard let account = entity.account, TransferAccountLinks.authorizes(title: entity.group.title, account: account, data: accountLinks), let reference else { return false }
            let number: String?
            if bookID == "ramadi" { number = TransferRules.validIBAN(account.iban) ? account.iban : nil }
            else if bookID == "housing" || bookID == "water" { number = TransferRules.individualLocalAccount(account) }
            else { return false }
            guard let number else { return false }
            return ArabicText.latinDigits(number).uppercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) } == reference
        }
        return matches.count == 1 ? matches[0] : nil
    }
}
