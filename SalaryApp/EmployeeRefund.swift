import Foundation
import CryptoKit

/// لقطة المستفيد من صف الرواتب. اسم الجهة المعيدة لا يُستعمل مطلقاً لاختيار حساب المستفيد.
nonisolated struct RefundPerson: Hashable, Sendable {
    let name: String
    let rank: String
    let workplace: String
    let iban: String
    let bank: String
}

nonisolated struct RefundBank: Equatable, Sendable {
    var name = ""
    var bic = ""
}

nonisolated enum EmployeeRefund {
    static let rafidainCode = "RAFB"
    static let rafidainBank = RefundBank(name: "مصرف الرافدين / فرع المستنصر", bic: "RAFBIQB1098")

    struct Entry: Sendable {
        let person: RefundPerson
        let amount: String
    }
    struct Payment: Sendable {
        let name: String
        let iban: String
        let amount: Int64
    }
    struct Group: Sendable {
        let code: String
        let bank: RefundBank
        let payments: [Payment]
        let total: Int64
        var isRafidain: Bool { code == EmployeeRefund.rafidainCode }
    }
    struct Batch: Sendable {
        let groups: [Group]
        let total: Int64
        let payer: String
        let account: String
        let source: String
        let reason: String
    }
    static func error(_ arabic: String, _ english: String) -> NSError {
        NSError(domain: "EmployeeRefund", code: 1, userInfo: [NSLocalizedDescriptionKey: tr(arabic, english)])
    }
    static func normalizeAccount(_ value: String) -> String {
        ArabicText.latinDigits(value).uppercased().filter { !$0.isWhitespace && $0 != "-" }
    }
    static func bankCode(_ value: String) -> String? {
        let clean = normalizeAccount(value)
        guard clean.range(of: #"^IQ[0-9]{2}[A-Z]{4}[0-9]{15}$"#, options: .regularExpression) != nil,
              TransferRules.validIBAN(clean) else { return nil }
        return String(clean.dropFirst(4).prefix(4))
    }
    static func amount(_ value: String) -> Int64? {
        let clean = ArabicText.latinDigits(value.trimmingCharacters(in: .whitespacesAndNewlines)).replacingOccurrences(of: "٬", with: ",")
        guard clean.range(of: #"^(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)$"#, options: .regularExpression) != nil,
              let number = Int64(clean.replacingOccurrences(of: ",", with: "")), number > 0, number <= maximum else { return nil }
        return number
    }
    private static let maximum: Int64 = 999_999_999_999_999
    static func plan(entries: [Entry], banks: [String: RefundBank], payer: String, account: String, source: String) throws -> Batch {
        let payer = payer.trimmingCharacters(in: .whitespacesAndNewlines)
        let account = normalizeAccount(account)
        let source = source.trimmingCharacters(in: .whitespacesAndNewlines)
        let reason = source.isEmpty ? "إعادة مبالغ زائدة في التسديد" : "إعادة مبالغ زائدة في التسديد - " + source
        guard !entries.isEmpty, bankCode(account) != nil, TransferRules.safeField(payer), TransferRules.safeField(reason) else {
            throw error("اختر المنتسبين وراجع اسم الجهة الدافعة وآيبانها ومصدر الإعادة.", "Select employees and check payer details and refund source.")
        }
        var seen = Set<String>(), groups: [String: [Payment]] = [:]
        var total: Int64 = 0
        for entry in entries {
            let name = entry.person.name
            let iban = normalizeAccount(entry.person.iban)
            guard let code = bankCode(iban), iban != account, TransferRules.safeField(name) else {
                throw error("راجع اسم المنتسب وآيبانه: " + name, "Check employee name and IBAN: " + name)
            }
            guard seen.insert(iban).inserted else {
                throw error("الآيبان مكرر بين المستفيدين: " + name, "Repeated beneficiary IBAN: " + name)
            }
            guard let value = amount(entry.amount), total <= maximum - value else {
                throw error("راجع المبلغ بالدينار للمنتسب: " + name, "Check the IQD amount for " + name)
            }
            total += value
            groups[code, default: []].append(Payment(name: name, iban: iban, amount: value))
        }
        let result: [Group] = try groups.keys.sorted().map { code in
            guard let bank = (code == rafidainCode ? rafidainBank : banks[code]), let payments = groups[code] else { throw error("بيانات المصرف ناقصة: " + code, "Missing bank: " + code) }
            let name = bank.name.trimmingCharacters(in: .whitespacesAndNewlines)
            let bic = bank.bic.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
            guard !name.isEmpty, TransferRules.safeField(name), TransferRules.validBIC(bic), bic.hasPrefix(code + "IQ") else {
                throw error("أكمل اسم المصرف ورمز SWIFT المطابق للآيبان: " + code, "Check bank name and matching SWIFT for " + code)
            }
            return Group(code: code, bank: RefundBank(name: name, bic: bic), payments: payments, total: payments.reduce(0) { $0 + $1.amount })
        }
        return Batch(groups: result, total: total, payer: payer, account: account, source: source, reason: reason)
    }

    // مرجع رقمي ثابت عند إعادة تجهيز الحركة، ومستقل عن ترتيب الأسماء أو ملفات استقطاعات اليوم نفسه.
    private static func reference(_ payment: Payment, group: Group, batch: Batch, day: String) -> String {
        let components = [day, batch.account, payment.iban, String(payment.amount), batch.reason, group.bank.bic]
        let digest = SHA256.hash(data: Data(components.joined(separator: "|").utf8))
        let serial = digest.prefix(8).reduce(UInt64.zero) { ($0 << 8) | UInt64($1) }
        let prefix = group.isRafidain ? TransferCategory.rafidain.prefix : TransferCategory.otherBanks.prefix
        return day + prefix + "0" + String(serial)
    }

    /// نفس ترتيب أعمدة التحويل المصرفي والفاصل والترميز المعتمدين.
    static func csv(_ group: Group, batch: Batch, day: String) -> Data {
        var rows = [TransferRules.csvHeader]
        for item in group.payments {
            let fields = [reference(item, group: group, batch: batch, day: day), day, batch.payer, batch.account, String(item.amount),
                          "IQD", group.bank.bic, item.name, item.iban, batch.reason, "SLEV"]
            rows.append(fields.joined(separator: "|"))
        }
        return Data(rows.joined(separator: "\r\n").utf8)
    }
}
