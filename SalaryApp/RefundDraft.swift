import SwiftUI
import SwiftData

/// مسودة الجلسة مشتركة بين شاشة التحويل والبحث وتتبع شهر صفوف الرواتب المختارة.
@Observable @MainActor final class RefundDraft {
    static let shared = RefundDraft()
    private(set) var batchID = UUID()
    let storage: RefundStorage
    private var updating = false
    private(set) var loadFailed = false
    private(set) var storageError: String?
    var rows: [RefundDraftRow] = [] { didSet { persist() } }
    var month: String? { rows.first?.candidate.month }
    var banks: [String: RefundBank] = [:] { didSet { persist() } }
    var source = "" { didSet { persist() } }
    var date = Date.now { didSet { persist() } }
    var payer = UserDefaults.standard.string(forKey: "bankStatementAccountName") ?? "" { didSet { persist() } }
    var account = UserDefaults.standard.string(forKey: "bankStatementIBAN") ?? "" { didSet { persist() } }
    var letters = RefundLetters() { didSet { persist() } }
    var working = false
    private var initialized = false

    init(storage: RefundStorage = .local) {
        self.storage = storage
        reload()
    }

    func reload() {
        do {
            if let saved = try storage.loadDraft() { apply(saved) }
            loadFailed = false
            storageError = nil
        } catch {
            loadFailed = true
            storageError = tr("تعذّرت قراءة المسودة المحفوظة. افتح قفل الجهاز وأعد المحاولة.", "Could not read the saved draft. Unlock the device and retry.")
        }
    }

    var snapshot: RefundSnapshot {
        .init(batchID: batchID, rows: rows, banks: banks, source: source, date: date, payer: payer, account: account, letters: letters)
    }

    func apply(_ saved: RefundSnapshot) {
        updating = true
        batchID = saved.batchID; rows = saved.rows; banks = saved.banks
        source = saved.source; date = saved.date; payer = saved.payer; account = saved.account
        letters = saved.letters; initialized = true
        updating = false
    }

    func persist() {
        guard !updating, !loadFailed else { return }
        do { try storage.saveDraft(snapshot); storageError = nil }
        catch { storageError = tr("تعذّر حفظ المسودة على الجهاز.", "Could not save the draft on this device.") }
    }

    func add(_ candidate: RefundCandidate, amount: String? = nil) throws {
        let person = candidate.person
        guard !working, !loadFailed, month == nil || month == candidate.month else {
            throw EmployeeRefund.error("راجع الدفعة الحالية وشهر الرواتب قبل الإضافة.", "Check the active batch and its payroll month.")
        }
        guard let code = EmployeeRefund.bankCode(person.iban) else {
            throw EmployeeRefund.error("راجع الآيبان في رواتب المنتسب.", "Check the employee’s payroll IBAN.")
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
        if code != EmployeeRefund.rafidainCode, banks[code] == nil { banks[code] = RefundBank(name: person.bank) }
        rows.append(RefundDraftRow(candidate: candidate, amount: amount ?? ""))
    }
    func remove(_ id: PersistentIdentifier) {
        guard !working else { return }
        rows.removeAll { $0.id == id }
        if rows.isEmpty { clear() }
    }
    func clear() {
        guard !working else { return }
        guard !loadFailed else { return }
        updating = true
        defer { updating = false; persist() }
        batchID = UUID()
        rows.removeAll(); banks.removeAll()
        source = ""; date = .now
        payer = UserDefaults.standard.string(forKey: "bankStatementAccountName") ?? ""
        account = UserDefaults.standard.string(forKey: "bankStatementIBAN") ?? ""
        letters = RefundLetters(); initialized = false
        loadLetterSettings()
    }

    var letterBICOptions: [String: [String]] {
        var codesByBank: [String: [String]] = [:]
        for row in rows {
            let person = row.candidate.person
            guard let bank = EmployeeRefund.bankCode(person.iban) else { continue }
            if case .value(let bic) = person.payrollBIC, person.payrollBIC.warning(bankCode: bank) == nil {
                codesByBank[bank, default: []].append(bic)
            } else if codesByBank[bank] == nil {
                codesByBank[bank] = []
            }
        }
        return codesByBank.mapValues(EmployeeRefund.mostFrequentBICs)
    }

    func loadLetterSettings() {
        guard !initialized, !loadFailed else { return }
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
