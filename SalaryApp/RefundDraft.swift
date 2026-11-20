import SwiftUI
import SwiftData

/// مسودة الجلسة مشتركة بين شاشة التحويل والبحث وتتبع شهر صفوف الرواتب المختارة.
@Observable @MainActor final class RefundDraft {
    static let shared = RefundDraft()
    struct Row: Identifiable {
        var candidate: RefundCandidate
        var amount: String
        var id: PersistentIdentifier { candidate.id }
    }
    var rows: [Row] = []
    var month: String? { rows.first?.candidate.month }
    var banks: [String: RefundBank] = [:]
    var source = ""
    var date = Date.now
    var payer = UserDefaults.standard.string(forKey: "bankStatementAccountName") ?? ""
    var account = UserDefaults.standard.string(forKey: "bankStatementIBAN") ?? ""
    var letters = RefundLetters()
    var working = false
    private var initialized = false

    func add(_ candidate: RefundCandidate, amount: String? = nil) throws {
        let person = candidate.person
        guard !working, month == nil || month == candidate.month else {
            throw EmployeeRefund.error("راجع الدفعة الحالية وشهر الرواتب قبل الإضافة.", "Check the active batch and its payroll month.")
        }
        guard let code = EmployeeRefund.bankCode(person.iban), EmployeeRefund.validPayrollBIC(person.payrollBIC, bankCode: code) else {
            throw EmployeeRefund.error("راجع الآيبان والسويفت في رواتب المنتسب.", "Check the employee’s payroll IBAN and SWIFT.")
        }
        if let amount, EmployeeRefund.amount(amount) == nil {
            throw EmployeeRefund.error("أدخل مبلغاً صحيحاً بالدينار.", "Enter a positive whole amount in IQD.")
        }
        if let index = rows.firstIndex(where: { $0.id == candidate.id }) {
            guard rows[index].candidate == candidate else {
                throw EmployeeRefund.error("تغيّرت بيانات المنتسب. أزله من الدفعة وأعد اختياره.", "Employee details changed. Remove and reselect the employee.")
            }
            if let amount { rows[index].amount = amount }
            return
        }
        guard !rows.contains(where: { $0.candidate.person.iban == person.iban }) else {
            throw EmployeeRefund.error("هذا الآيبان موجود لمنتسب آخر في الدفعة.", "Another batch member already has this IBAN.")
        }
        if code != EmployeeRefund.rafidainCode, banks[code] == nil { banks[code] = bankDefaults(code, person: person) }
        rows.append(Row(candidate: candidate, amount: amount ?? ""))
    }
    func remove(_ id: PersistentIdentifier) {
        guard !working else { return }
        rows.removeAll { $0.id == id }
        if rows.isEmpty { clear() }
    }
    func clear() {
        guard !working else { return }
        rows.removeAll(); banks.removeAll()
        source = ""; date = .now
        payer = UserDefaults.standard.string(forKey: "bankStatementAccountName") ?? ""
        account = UserDefaults.standard.string(forKey: "bankStatementIBAN") ?? ""
        letters = RefundLetters(); initialized = false
        loadLetterSettings()
    }

    private func bankDefaults(_ code: String, person: RefundPerson) -> RefundBank {
        let accounts = UserDefaults.standard.data(forKey: ExecutionAccountStore.storageKey) ?? Data()
        let saved = (try? ExecutionAccountStore.decode(accounts)) ?? []
        let codes = Set(saved.filter { EmployeeRefund.bankCode($0.iban) == code }
            .map { $0.swift.trimmingCharacters(in: .whitespacesAndNewlines).uppercased() }
            .filter { $0.hasPrefix(code + "IQ") && TransferRules.validBIC($0) })
        return RefundBank(name: person.bank, bic: codes.count == 1 ? codes.first ?? "" : "")
    }
    func loadLetterSettings() {
        guard !initialized else { return }
        initialized = true
        guard let data = UserDefaults.standard.data(forKey: TransferLetterCustomizationStore.settingsKey),
              let store = try? JSONDecoder().decode(TransferLetterCustomizationStore.self, from: data) else { return }
        func settings(for id: String) -> RefundLetterSettings {
            let custom = store.effective(for: id)
            let template = custom.letterTemplate
            var value = RefundLetterSettings()
            value.header = template?.header ?? custom.header ?? value.header
            value.number = template?.number ?? custom.fixedNumber ?? value.number
            value.first = template?.firstSigner ?? custom.committeeFirst ?? ""
            value.second = template?.secondSigner ?? custom.committeeSecond ?? ""
            value.third = template?.thirdSigner ?? custom.committeeThird ?? ""
            value.footer = template?.footer ?? custom.footer ?? value.footer
            value.signatureOffset = template?.signatureVerticalOffset ?? custom.signatureOffset ?? 0
            value.template = template
            return value
        }
        letters = RefundLetters(rafidain: settings(for: "rafidain_group"), otherBanks: settings(for: "other_banks_group"))
    }
}
