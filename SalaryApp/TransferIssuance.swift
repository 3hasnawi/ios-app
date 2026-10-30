import SwiftUI
import SwiftData

/// إقرار المستخدم فقط؛ لا يثبت هذا السجل حصول تحويل مصرفي أو مطابقة PDF.
nonisolated struct TransferIssuedLine: Codable, Equatable, Identifiable {
    let id: String
    let title: String
    let amount: Double
    let count: Int
    let iban: String
    let branch: String
    let swift: String
    let category: String

    init(_ entity: TransferEntity) {
        id = entity.id
        title = entity.group.title
        amount = entity.group.total
        count = entity.group.count
        iban = entity.account?.iban ?? ""
        branch = entity.account?.branch ?? ""
        swift = entity.account?.swift ?? ""
        category = entity.category.rawValue
    }

    init(payrollID: String, title: String, month: String, amount: Double) {
        id = "payroll|\(month)|\(payrollID)"
        self.title = title
        self.amount = amount
        count = 0
        iban = ""; branch = ""; swift = ""; category = "payroll"
    }
}

nonisolated struct TransferIssuedRecord: Codable, Identifiable {
    let id: UUID
    let month: String
    let book: String
    let confirmedAt: Date
    let lines: [TransferIssuedLine]
}

nonisolated enum TransferIssuance {
    static let settingsKey = "transferIssuedConfirmations"
    struct Ledger: Codable { var version = 1; var records: [TransferIssuedRecord] = [] }
    enum Failure: Error { case invalid }
    static func decode(_ data: Data) throws -> Ledger {
        if data.isEmpty { return Ledger() }
        let ledger = try JSONDecoder().decode(Ledger.self, from: data)
        guard ledger.version == 1, Set(ledger.records.map(\.id)).count == ledger.records.count,
              ledger.records.allSatisfy({ !$0.month.isEmpty && !$0.book.isEmpty && !$0.lines.isEmpty &&
                  Set($0.lines.map(\.id)).count == $0.lines.count &&
                  $0.lines.allSatisfy { !$0.id.isEmpty && $0.amount.isFinite && $0.amount > 0 } }) else { throw Failure.invalid }
        return ledger
    }
    struct Audit {
        let missing: [TransferIssuedLine]
        let changed: [TransferIssuedLine]
        let absent: [TransferIssuedLine]
        let repeated: [TransferIssuedLine]
    }
    /// البنود مستقلة حتى يظهر تغير جزء من جدول حتى لو بقي مجموعه ثابتاً.
    static func payrollLines(_ figures: TransferLetterFigures) -> [TransferIssuedLine] {
        let values: [(String, String, Double?)] = [
            ("foodTax", "ضريبة عقد الإطعام", figures.foodTax),
            ("foodCompany", "شركة عراق الجوال", figures.foodCompany),
            ("incomeTax", "الضريبة", figures.incomeTax),
            ("socialProtection", "الحماية الاجتماعية", figures.socialProtection),
            ("contractNominal", "حصة اسمي العقود", figures.contractNominalShare),
            ("contractRetirement", "توقيفات العقود التقاعدية", figures.contractRetirement),
            ("cashSalary", "رواتب الكاش", figures.payrollCash)
        ]
        return values.compactMap { id, title, amount in
            guard let amount, amount.isFinite, amount > 0 else { return nil }
            return TransferIssuedLine(payrollID: id, title: title, month: figures.month, amount: amount)
        }
    }
    static func audit(current: [TransferIssuedLine], records: [TransferIssuedRecord], month: String) -> Audit {
        let recorded = records.filter { $0.month == month }.flatMap(\.lines)
        let byCurrent = Dictionary(current.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let byRecorded = Dictionary(grouping: recorded, by: \.id)
        return Audit(
            missing: current.filter { byRecorded[$0.id] == nil },
            changed: current.filter { line in byRecorded[line.id]?.contains(where: { $0 != line }) == true },
            absent: recorded.filter { byCurrent[$0.id] == nil },
            repeated: current.filter { (byRecorded[$0.id]?.count ?? 0) > 1 })
    }
}

/// تسجيل كتاب صدر خارج التطبيق، بتأكيد صريح من المستخدم؛ لا تسجّل المعاينة كإصدار.
struct TransferIssuanceView: View {
    let month: String
    let figures: TransferLetterFigures
    let entities: [TransferEntity]
    let sourceKey: String
    let accounts: Data
    let categories: Data
    let links: Data
    @Query private var months: [PayrollMonth]
    @AppStorage(TransferIssuance.settingsKey) private var stored = Data()
    @AppStorage(ExecutionAccountStore.storageKey) private var currentAccounts = Data()
    @AppStorage(TransferRules.settingsKey) private var currentCategories = Data()
    @AppStorage("transferPayrollAccountLinks") private var currentLinks = Data()
    @State private var selected: Set<String> = []
    @State private var book = ""
    @State private var confirm = false
    @State private var retract: TransferIssuedRecord?
    @State private var error: String?

    private var current: Bool {
        let record = months.first { $0.name == month }
        return record != nil && currentAccounts == accounts && currentCategories == categories && currentLinks == links &&
            "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    private var lines: [TransferIssuedLine] {
        entities.filter { $0.group.total.isFinite && $0.group.total > 0 }.map(TransferIssuedLine.init) + TransferIssuance.payrollLines(figures)
    }
    private var ledger: TransferIssuance.Ledger? { try? TransferIssuance.decode(stored) }
    @discardableResult private func save(_ records: [TransferIssuedRecord]) -> Bool {
        guard current, ledger != nil, !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
            error = tr("تغيرت البيانات أو توجد عملية استيراد أو نسخ جارية. أعد فتح الصفحة وحاول مجدداً.", "Data changed or another operation is in progress. Reopen this page and try again.")
            return false
        }
        do {
            stored = try JSONEncoder().encode(TransferIssuance.Ledger(records: records))
            BackupManager.shared.markChanged()
            return true
        } catch { self.error = error.localizedDescription; return false }
    }
    var body: some View {
        List {
            if !current { Text(tr("تغيرت بيانات الشهر أو الحسابات؛ أعد فتح الصفحة.", "Month data or accounts changed; reopen this page.")).foregroundStyle(.orange) }
            if let error { Text(error).foregroundStyle(.red) }
            if let ledger {
                let audit = TransferIssuance.audit(current: lines, records: ledger.records, month: month)
                Section(tr("مراجعة الجهات والبنود · \(month)", "Entity and item review · \(month)")) {
                    issueGroup(tr("مصادر بلا تأكيد إصدار", "Items without issuance confirmation"), audit.missing, .orange)
                    issueGroup(tr("تغيّر المبلغ أو العدد أو الحساب بعد التأكيد", "Amount, count or account changed after confirmation"), audit.changed, .red)
                    issueGroup(tr("مصادر مؤكدة غير متاحة في بيانات الشهر", "Confirmed items unavailable in current data"), audit.absent, .orange)
                    issueGroup(tr("جهات مؤكدة في أكثر من كتاب", "Entities confirmed in multiple letters"), audit.repeated, .red)
                    if audit.missing.isEmpty && audit.changed.isEmpty && audit.absent.isEmpty && audit.repeated.isEmpty {
                        Text(tr("لا توجد اختلافات في الإقرارات المسجلة.", "No differences in recorded confirmations.")).foregroundStyle(.secondary)
                    }
                } footer: { Text(tr("يقارن التدقيق الجهات وبنود الكتب بإقراراتك اليدوية. لا يقرأ الكتب الصادرة أو يثبت التحويل. قد يظهر بند الكاش غير متاح إذا تعذر تحديد طريقة الدفع.", "This compares entities and letter items with manual confirmations. It does not read issued letters or prove a transfer. Cash may be unavailable if payment methods cannot be determined.")) }
                Section(tr("تسجيل كتاب صادر يدوياً", "Manually record an issued letter")) {
                    TextField(tr("اسم الكتاب الصادر", "Issued letter name"), text: $book)
                    ForEach(lines) { line in
                        Toggle(isOn: Binding(get: { selected.contains(line.id) }, set: { if $0 { selected.insert(line.id) } else { selected.remove(line.id) } })) {
                            VStack(alignment: .leading) {
                                Text(line.title)
                                Text(Money.format(line.amount)).font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                    Button(tr("تأكيد تسجيل الإصدار", "Confirm issued letter")) { confirm = true }
                        .disabled(!current || selected.isEmpty || book.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                } footer: { Text(tr("بعد إصدار الكتاب فعلياً، اختر الجهات أو بنود الرواتب التي يتضمنها. المعاينة وحدها لا تُعد إصداراً.", "After issuing the letter, select the entities or payroll items it contains. A preview is not issuance.")) }
                let records = ledger.records.filter { $0.month == month }.sorted { $0.confirmedAt > $1.confirmedAt }
                if !records.isEmpty {
                    Section(tr("الإصدارات المؤكدة يدوياً", "Manually confirmed letters")) {
                        ForEach(records) { record in
                            VStack(alignment: .leading) {
                                Text(record.book).font(.headline)
                                Text(tr("\(Num.format(record.lines.count)) مصدر", "\(record.lines.count) items"))
                                    .font(.caption).foregroundStyle(.secondary)
                                Text(record.confirmedAt, style: .date).font(.caption).foregroundStyle(.secondary)
                                Button(tr("إلغاء التأكيد", "Retract confirmation"), role: .destructive) { retract = record }
                            }
                        }
                    }
                }
            } else {
                Label(tr("تعذرت قراءة سجل الإصدارات. لم يُغيّر السجل؛ راجع النسخة الاحتياطية.", "Could not read the issuance ledger. It was not changed; check the backup."), systemImage: "exclamationmark.triangle").foregroundStyle(.red)
            }
        }
        .navigationTitle(tr("تدقيق الكتب الصادرة", "Issued letter review"))
        .alert(tr("تأكيد إصدار الكتاب؟", "Confirm letter issued?"), isPresented: $confirm) {
            Button(tr("تأكيد الإصدار", "Confirm issuance")) {
                guard let ledger, current else { return }
                let chosen = lines.filter { selected.contains($0.id) }
                guard !chosen.isEmpty, Set(chosen.map(\.id)).count == chosen.count else { return }
                let record = TransferIssuedRecord(id: UUID(), month: month, book: book.trimmingCharacters(in: .whitespacesAndNewlines), confirmedAt: .now, lines: chosen)
                if save(ledger.records + [record]) { selected = []; book = "" }
            }
            Button(tr("إلغاء", "Cancel"), role: .cancel) { }
        } message: { Text(tr("أؤكد أنني أصدرت الكتاب فعلياً وأن الجهات والبنود المختارة وردت فيه بالمبالغ المعروضة. لا يتحقق التطبيق من الكتاب أو المصرف بهذا الإقرار.", "I confirm that I issued this letter and it contains the selected entities and items with their amounts. This confirmation does not verify the document or the bank.")) }
        .alert(tr("إلغاء تأكيد الإصدار؟", "Retract issuance confirmation?"), isPresented: Binding(get: { retract != nil }, set: { if !$0 { retract = nil } })) {
            Button(tr("إلغاء التأكيد", "Retract"), role: .destructive) {
                if let id = retract?.id, let ledger { save(ledger.records.filter { $0.id != id }) }
                retract = nil
            }
            Button(tr("رجوع", "Back"), role: .cancel) { retract = nil }
        } message: { Text(tr("يُحذف الإقرار فقط، ولا يُحذف الكتاب أو بيانات الشهر.", "Only the confirmation is removed; the letter and monthly data are unchanged.")) }
    }
    @ViewBuilder private func issueGroup(_ title: String, _ values: [TransferIssuedLine], _ color: Color) -> some View {
        if !values.isEmpty {
            DisclosureGroup("\(title) · \(Num.format(values.count))") {
                ForEach(Array(values.enumerated()), id: \.offset) { _, line in
                    Text(line.title).foregroundStyle(color)
                }
            }
        }
    }
}
