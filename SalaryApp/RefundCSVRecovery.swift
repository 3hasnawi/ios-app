import Foundation

/// استرجاع دفعة صدّرها تطبيق الرواتب قبل إضافة الحفظ التلقائي.
nonisolated struct RefundCSVRecovery: Sendable {
    let date: Date
    let payer: String
    let account: String
    let source: String
    let payments: [EmployeeRefund.Payment]

    static func read(_ files: [URL]) throws -> Self {
        let invalid = EmployeeRefund.error("اختر ملفات CSV لإعادة المبالغ من الدفعة نفسها، وتأكد من عدم تكرار حساب.", "Select refund CSVs from one batch with distinct beneficiary accounts.")
        let legacyPrefix = EmployeeRefund.legacyReason + " - "
        var common: [String]?, payments: [EmployeeRefund.Payment] = []
        var seen = Set<String>(), references = Set<String>()
        for url in files {
            let access = url.startAccessingSecurityScopedResource()
            defer { if access { url.stopAccessingSecurityScopedResource() } }
            let text = try String(contentsOf: url, encoding: .utf8)
            let lines = text.split(whereSeparator: \.isNewline)
            guard lines.count > 1, lines.first == Substring(TransferRules.csvHeader) else { throw invalid }
            for line in lines.dropFirst() {
                let f = line.components(separatedBy: "|")
                guard f.count == 11, f[0].hasPrefix(f[1]), f[5] == "IQD", f[10] == "SLEV",
                      f[9] == EmployeeRefund.remittanceInformation || f[9] == EmployeeRefund.legacyReason || f[9].hasPrefix(legacyPrefix),
                      TransferRules.safeField(f[2]), TransferRules.safeField(f[7]), TransferRules.safeField(f[9]),
                      EmployeeRefund.bankCode(f[3]) != nil, let bank = EmployeeRefund.bankCode(f[8]), f[8] != f[3],
                      RefundPayrollBIC.value(f[6]).warning(bankCode: bank) == nil,
                      let amount = EmployeeRefund.amount(f[4]), seen.insert(f[8]).inserted else { throw invalid }
                let fields = [f[1], f[2], f[3], f[9]]
                if let common, fields != common { throw invalid }
                common = fields
                var payment = EmployeeRefund.Payment(name: f[7], iban: f[8], amount: amount, bic: f[6])
                let category = bank == EmployeeRefund.rafidainCode ? TransferCategory.rafidain : .otherBanks
                let prefix = f[1] + category.prefix + "0"
                guard f[0].hasPrefix(prefix), UInt64(f[0].dropFirst(prefix.count)) != nil,
                      f[0].utf8.allSatisfy({ (48...57).contains($0) }), f[0].count <= 35,
                      references.insert(f[0]).inserted else { throw invalid }
                if f[9] == EmployeeRefund.remittanceInformation {
                    let computed = EmployeeRefund.reference(payment, bank: bank, payerAccount: f[3], reason: EmployeeRefund.legacyReason, day: f[1])
                    if computed != f[0] { payment.recoveredReference = .init(computed: computed, original: f[0]) }
                }
                payments.append(payment)
            }
        }
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.calendar = Calendar(identifier: .gregorian)
        format.dateFormat = "yyyyMMdd"
        guard let common, let date = format.date(from: common[0]), format.string(from: date) == common[0] else { throw invalid }
        return Self(date: date, payer: common[1], account: common[2],
                    source: common[3].hasPrefix(legacyPrefix) ? String(common[3].dropFirst(legacyPrefix.count)) : "", payments: payments)
    }
}
