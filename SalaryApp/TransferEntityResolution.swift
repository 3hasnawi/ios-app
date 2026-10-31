import Foundation

/// تصنيف موحّد لجهات التحويل والمبالغ المقترحة للمطابقة.
nonisolated enum TransferEntityResolution {
    static func categoryKey(_ name: String, _ account: SavedExecutionAccount?) -> String {
        "\(ArabicText.normalize(name))|\(account?.iban ?? "")|\(ArabicText.normalize(account?.branch ?? ""))"
    }
    static func make(groups: [ReportGroup], missing: [String: Int], figures: TransferLetterFigures?,
                     accounts: [SavedExecutionAccount], categories: [String: String],
                     payrollLinks: [String: String], linksValid: Bool) -> [TransferEntity] {
        let lookup = accounts.reduce(into: [String: SavedExecutionAccount]()) { $0[$1.key] = $1 }
        let deductions = groups.map { group in
            let account = linksValid ? TransferAccountLinks.resolve(title: group.title, accounts: lookup, links: payrollLinks) : nil
            let key = categoryKey(group.title, account)
            let explicit = payrollLinks[TransferAccountLinks.sourceKey(group.title)] != nil
            let directAnbar = linksValid && !explicit && account == nil && TransferRules.isAnbarBankSource(group.title)
            let category = categories[key].flatMap(TransferCategory.init(rawValue:)) ?? (directAnbar ? .anbar : TransferRules.classify(account))
            return TransferEntity(group: group, account: account, category: category, missingAmounts: missing[group.title] ?? 0,
                                  beneficiaryName: explicit ? account?.entity : nil)
        }
        guard let figures else { return deductions }
        let payroll = TransferPayrollRecipient.allCases.compactMap { recipient -> TransferEntity? in
            let amount = recipient.amount(figures)
            guard amount > 0 else { return nil }
            let account = linksValid ? payrollLinks[recipient.rawValue].flatMap { lookup[$0] } : nil
            let group = ReportGroup(kind: .deductions, month: figures.month, title: recipient.arabicTitle, count: 1, total: amount, byEntity: false)
            let category = categories[categoryKey(group.title, account)].flatMap(TransferCategory.init(rawValue:)) ?? TransferRules.classify(account)
            let duplicate = groups.contains { ArabicText.normalize($0.title) == ArabicText.normalize(recipient.arabicTitle) }
            return TransferEntity(group: group, account: account, category: category, missingAmounts: 0,
                                  beneficiaryName: account?.entity, payrollRecipient: recipient.rawValue, duplicateSource: duplicate)
        }
        return deductions + payroll
    }
}
