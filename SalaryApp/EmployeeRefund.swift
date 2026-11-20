import Foundation
import CryptoKit

/// لقطة المستفيد من صف الرواتب. اسم الجهة المعيدة لا يُستعمل مطلقاً لاختيار حساب المستفيد.
nonisolated struct RefundPerson: Codable, Hashable, Sendable {
    let name: String
    let rank: String
    let workplace: String
    let iban: String
    let bank: String
    let payrollBIC: RefundPayrollBIC

    var bicWarning: String? { payrollBIC.warning(bankCode: EmployeeRefund.bankCode(iban)) }
}

nonisolated enum RefundPayrollBIC: Codable, Hashable, Sendable {
    case missingColumn, empty, conflicting, value(String)

    func warning(bankCode: String?) -> String? {
        switch self {
        case .missingColumn: return tr("لم يُتعرّف على عمود السويفت في صف الراتب المحفوظ.", "No recognized SWIFT column in the saved payroll row.")
        case .empty: return tr("خانة السويفت فارغة في رواتب هذا المنتسب.", "The employee’s payroll SWIFT field is empty.")
        case .conflicting: return tr("صف الراتب يحتوي أكثر من رمز سويفت مختلف.", "The payroll row contains conflicting SWIFT codes.")
        case .value(let bic):
            guard TransferRules.validBIC(bic) else { return tr("صيغة سويفت المنتسب غير صالحة.", "The employee’s SWIFT format is invalid.") }
            if let bankCode, !bic.hasPrefix(bankCode + "IQ") { return tr("سويفت المنتسب لا يطابق مصرف الآيبان.", "The employee’s SWIFT does not match the IBAN bank.") }
            return nil
        }
    }
}

nonisolated struct RefundBank: Codable, Equatable, Sendable {
    var name = ""
    var bic = ""
}

nonisolated enum EmployeeRefund {
    static let remittanceInformation = "اعادة مبالغ تنفيذ"
    static let legacyReason = "إعادة مبالغ زائدة في التسديد"
    static let rafidainCode = "RAFB"
    static let rafidainBank = RefundBank(name: "مصرف الرافدين / فرع المستنصر", bic: "RAFBIQB1098")

    // الملف الجديد لا يحتوي اسم الجهة الذي دخل في توليد المرجع الأصلي.
    struct RecoveredReference: Codable, Sendable {
        let computed: String
        let original: String
    }
    struct Entry: Sendable {
        let person: RefundPerson
        let amount: String
        var recoveredReference: RecoveredReference? = nil
    }
    struct Payment: Sendable {
        let name: String
        let iban: String
        let amount: Int64
        let bic: String
        var recoveredReference: RecoveredReference? = nil
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
    private static func headerKey(_ title: String) -> String {
        ArabicText.normalize(title).filter { $0.isLetter || $0.isNumber }
    }
    private static let bicHeaders = Set([
        "الرفرنس", "رفرنس", "SWIFT", "BIC", "SWIFT Code", "BIC Code", "SWIFT/BIC", "BIC/SWIFT", "Receiver BIC", "Bank BIC",
        "Bank Swift Code", "Branch BIC", "Branch Swift Code", "Beneficiary BIC",
        "سويفت", "سوفت", "سويفت كود", "سوفت كود", "رمز السويفت", "رمز السوفت",
        "رمز سويفت", "رمز سوفت", "كود السويفت", "كود السوفت", "السويفت كود", "السوفت كود",
        "السويفت", "السوفت", "سويفت المصرف", "سوفت المصرف", "سويفت الفرع", "سوفت الفرع",
        "رمز السويفت للمصرف", "رمز السويفت للفرع", "رمز SWIFT", "رمز BIC"
    ].map(headerKey))

    // الأعمدة الأصلية تشمل الحقول غير المعروفة وقت الاستيراد وتحتفظ بالعناوين المكررة.
    static func payrollBIC(details: [String: String], columns: PayrollColumnSnapshot?) -> RefundPayrollBIC {
        var foundColumn = false
        var codes = Set<String>()
        func collect(_ header: String, _ value: String) {
            guard bicHeaders.contains(headerKey(header)) else { return }
            foundColumn = true
            let clean = value.unicodeScalars.filter { $0.properties.generalCategory != .format }
            let code = ArabicText.latinDigits(String(String.UnicodeScalarView(clean)).trimmingCharacters(in: .whitespacesAndNewlines)).uppercased()
            if !code.isEmpty && code != "0" { codes.insert(code) }
        }
        for (header, value) in details { collect(header, value) }
        if let columns {
            for (header, value) in zip(columns.headers, columns.values) { collect(header, value) }
        }
        guard let first = codes.first else { return foundColumn ? .empty : .missingColumn }
        return codes.count == 1 ? .value(first) : .conflicting
    }
    // كل منتسب له صوت واحد لرمز فرعه، بصرف النظر عن مبلغ الإعادة.
    static func mostFrequentBICs(_ codes: [String]) -> [String] {
        var counts: [String: Int] = [:]
        for code in codes { counts[code, default: 0] += 1 }
        guard let maximum = counts.values.max() else { return [] }
        return counts.keys.filter { counts[$0] == maximum }.sorted()
    }

    static func letterBIC(leaders: [String], preferred: String) -> String? {
        leaders.contains(preferred) ? preferred : leaders.first
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
        let reason = source.isEmpty ? legacyReason : legacyReason + " - " + source
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
            guard case .value(let bic) = entry.person.payrollBIC, entry.person.bicWarning == nil else {
                let reason = entry.person.bicWarning ?? tr("راجع السويفت.", "Check SWIFT.")
                throw error(name + " — " + reason, name + " — " + reason)
            }
            guard seen.insert(iban).inserted else {
                throw error("الآيبان مكرر بين المستفيدين: " + name, "Repeated beneficiary IBAN: " + name)
            }
            guard let value = amount(entry.amount), total <= maximum - value else {
                throw error("راجع المبلغ بالدينار للمنتسب: " + name, "Check the IQD amount for " + name)
            }
            total += value
            groups[code, default: []].append(Payment(name: name, iban: iban, amount: value, bic: bic, recoveredReference: entry.recoveredReference))
        }
        let result: [Group] = try groups.keys.sorted().map { code in
            guard let bank = (code == rafidainCode ? rafidainBank : banks[code]), let payments = groups[code] else { throw error("بيانات المصرف ناقصة: " + code, "Missing bank: " + code) }
            let name = bank.name.trimmingCharacters(in: .whitespacesAndNewlines)
            let bic = code == rafidainCode ? rafidainBank.bic
                : letterBIC(leaders: mostFrequentBICs(payments.map(\.bic)), preferred: bank.bic)
            guard let bic, !name.isEmpty, TransferRules.safeField(name) else {
                throw error("راجع اسم المصرف: " + code, "Check bank name for " + code)
            }
            return Group(code: code, bank: RefundBank(name: name, bic: bic), payments: payments, total: payments.reduce(0) { $0 + $1.amount })
        }
        return Batch(groups: result, total: total, payer: payer, account: account, source: source, reason: reason)
    }

    // مرجع رقمي ثابت عند إعادة تجهيز الحركة، ومستقل عن ترتيب الأسماء أو ملفات استقطاعات اليوم نفسه.
    static func reference(_ payment: Payment, bank: String, payerAccount: String, reason: String, day: String) -> String {
        let components = [day, payerAccount, payment.iban, String(payment.amount), reason, payment.bic]
        let digest = SHA256.hash(data: Data(components.joined(separator: "|").utf8))
        let serial = digest.prefix(8).reduce(UInt64.zero) { ($0 << 8) | UInt64($1) }
        let prefix = bank == rafidainCode ? TransferCategory.rafidain.prefix : TransferCategory.otherBanks.prefix
        return day + prefix + "0" + String(serial)
    }

    /// نفس ترتيب أعمدة التحويل المصرفي والفاصل والترميز المعتمدين.
    static func csv(_ group: Group, batch: Batch, day: String) -> Data {
        var rows = [TransferRules.csvHeader]
        for item in group.payments {
            let computed = reference(item, bank: group.code, payerAccount: batch.account, reason: batch.reason, day: day)
            let issuedReference = item.recoveredReference.flatMap { $0.computed == computed ? $0.original : nil } ?? computed
            let fields = [issuedReference, day, batch.payer, batch.account, String(item.amount),
                          "IQD", item.bic, item.name, item.iban, remittanceInformation, "SLEV"]
            rows.append(fields.joined(separator: "|"))
        }
        return Data(rows.joined(separator: "\r\n").utf8)
    }
}
