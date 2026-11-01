import SwiftUI
import SwiftData
import UniformTypeIdentifiers

/// يحفظ الدفتر كاملاً دفعة واحدة بعد الفحص، ويمنع استبداله أثناء النسخ أو الاسترجاع.
@MainActor enum SalaryTrustStore {
    static func load() throws -> SalaryTrust.Ledger { try SalaryTrust.decode(UserDefaults.standard.data(forKey: SalaryTrust.settingsKey) ?? Data()) }
    static func save(_ ledger: SalaryTrust.Ledger, expected: Data) throws {
        guard !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting,
              (UserDefaults.standard.data(forKey: SalaryTrust.settingsKey) ?? Data()) == expected else {
            throw SalaryTrust.invalid(tr("تغيّرت البيانات أو توجد عملية جارية؛ أعد المحاولة.", "The data changed or another operation is running; try again."))
        }
        let data = try SalaryTrust.encode(ledger)
        UserDefaults.standard.set(data, forKey: SalaryTrust.settingsKey)
        BackupManager.shared.markChanged()
    }
    /// تُضاف قائمة الكاش مرة واحدة؛ أي تغيير أو تكرار في الشخص والشهر يوقف الإضافة للمراجعة.
    static func addCash(month: String) async throws -> Int {
        let period = MonthName.sortKey(month)
        guard period > SalaryTrust.openingCutoff else { return 0 }
        let expected = UserDefaults.standard.data(forKey: SalaryTrust.settingsKey) ?? Data()
        var ledger = try SalaryTrust.decode(expected)
        guard ledger.openingImported else { return 0 }
        let container = SharedModel.container
        let descriptor = FetchDescriptor<PayrollMonth>(predicate: #Predicate { $0.name == month })
        let before = try ModelContext(container).fetch(descriptor)
        guard before.count == 1 else { throw SalaryTrust.invalid(tr("تعذّر تحديد شهر الرواتب بصورة فريدة.", "Could not uniquely identify the payroll month.")) }
        let importedAt = before[0].importedAt
        let list = try await Task.detached(priority: .userInitiated) { try await TransferCashListEngine(modelContainer: container).list(month: month) }.value
        let after = try ModelContext(container).fetch(descriptor)
        guard after.count == 1, after[0].importedAt == importedAt else { throw SalaryTrust.invalid(tr("تغيّرت رواتب الشهر أثناء إعداد الأمانات؛ أعد المحاولة.", "Payroll changed while preparing trust entries; try again.")) }
        guard list.repeatedNameRows == 0 else { throw SalaryTrust.invalid(tr("توجد أسماء كاش مكررة في الشهر؛ راجعها قبل إضافتها للأمانات.", "Duplicate cash names require review before adding trust entries.")) }
        let existing = Dictionary(uniqueKeysWithValues: ledger.entries.map { (SalaryTrust.key($0), $0) })
        var additions: [SalaryTrust.Entry] = []
        for person in list.employees {
            guard person.amount > 0, person.amount <= Double(SalaryTrust.maximum), person.amount.rounded() == person.amount,
                  !person.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  !person.rank.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  !person.workplace.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw SalaryTrust.invalid(tr("بيانات أحد رواتب الكاش غير مكتملة.", "A cash salary has incomplete data.")) }
            guard let caseStatus = person.caseStatus else { throw SalaryTrust.invalid(tr("عمود الملاحظات بعد صافي الراتب غير متاح؛ أعد استيراد رواتب الشهر وراجعها قبل إضافة الأمانات.", "The notes column following net salary is unavailable; reimport and review the month before adding trust entries.")) }
            let entry = SalaryTrust.Entry(id: UUID().uuidString, name: person.name, rank: person.rank, workplace: person.workplace,
                year: period / 100, month: period % 100, amount: Int64(person.amount), caseStatus: caseStatus, note: "", importedStatus: "", legacyPaid: false, reviewPending: false, payments: [])
            if let old = existing[SalaryTrust.key(entry)] {
                guard old.amount == entry.amount, old.caseStatus == entry.caseStatus else { throw SalaryTrust.invalid(tr("تغيّر مبلغ أمانة مسجلة أو حالتها؛ راجعها قبل التحديث.", "An existing trust amount or status changed; review it before updating.")) }
            } else { additions.append(entry) }
        }
        let incoming = Set(list.employees.map { "\(period / 100)-\(period % 100)|" + ArabicText.matchKey($0.name) })
        let missing = ledger.entries.contains { $0.year == period / 100 && $0.month == period % 100 && !incoming.contains(SalaryTrust.key($0)) }
        guard !missing else { throw SalaryTrust.invalid(tr("تغيّرت قائمة كاش الشهر وفيها قيود محفوظة لم تعد موجودة؛ راجعها قبل التحديث.", "The cash list no longer contains some saved entries; review before updating.")) }
        guard !additions.isEmpty else { return 0 }
        ledger.entries.append(contentsOf: additions)
        try save(ledger, expected: expected)
        return additions.count
    }
}

struct SalaryTrustView: View {
    @Query private var months: [PayrollMonth]
    @AppStorage(SalaryTrust.settingsKey) private var stored = Data()
    @State private var search = ""
    @State private var problem: String?
    @State private var preview: SalaryTrust.Ledger?
    @State private var previewBase = Data()
    @State private var showingPreview = false
    @State private var loading = false
    @State private var selected: SalaryTrust.Entry?
    private var ledger: SalaryTrust.Ledger? { try? SalaryTrust.decode(stored) }
    private var visible: [SalaryTrust.Entry] {
        (ledger?.entries ?? []).filter { search.isEmpty || ArabicText.matchKey($0.name).contains(ArabicText.matchKey(search)) }
            .sorted { ($0.year, $0.month, $0.name) > ($1.year, $1.month, $1.name) }
    }
    var body: some View {
        List {
            if let ledger {
                Section {
                    LabeledContent(tr("الأمانات غير المصروفة", "Outstanding trust"), value: Money.format(Double(ledger.entries.filter { !$0.reviewPending }.reduce(Int64(0)) { $0 + $1.remaining })))
                    LabeledContent(tr("سجلات للمراجعة", "Entries to review"), value: Num.format(ledger.entries.filter(\.reviewPending).count))
                }
                if !ledger.openingImported {
                    Section { Button(tr("استيراد ملف الأمانات الافتتاحي", "Import opening trust spreadsheet"), systemImage: "square.and.arrow.down") { pickOpening() }.disabled(loading) }
                }
                Section(tr("السجلات", "Entries")) {
                    ForEach(visible) { e in
                        Button { selected = e } label: {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(e.name).font(.headline).privacySensitive()
                                Text("\(e.month)/\(e.year) · \(Money.format(Double(e.amount)))").font(.caption).foregroundStyle(.secondary)
                                if !e.caseStatus.isEmpty { Text(tr("الحالة: \(e.caseStatus)", "Status: \(e.caseStatus)")).font(.caption).privacySensitive() }
                                Text(e.reviewPending ? tr("للمراجعة · لا تدخل في الرصيد", "Review required · excluded from balance") : e.remaining == 0 ? tr("مصروف بالكامل", "Paid in full") : tr("المتبقي: \(Money.format(Double(e.remaining)))", "Remaining: \(Money.format(Double(e.remaining)))"))
                                    .font(.caption.weight(.semibold)).foregroundStyle(e.reviewPending ? Color.orange : Color.secondary)
                            }
                        }
                    }
                }
            } else { Text(tr("تعذّر قراءة دفتر الأمانات؛ لم تتغير البيانات.", "Could not read the trust ledger; data was not changed.")).foregroundStyle(.red) }
        }
        .searchable(text: $search, prompt: tr("البحث بالاسم", "Search by name"))
        .navigationTitle(tr("أمانات الرواتب", "Salary trust"))
        .sheet(item: $selected) { entry in NavigationStack { SalaryTrustEntryView(entry: entry, stored: $stored) } }
        .alert(tr("مراجعة ملف الأمانات", "Review trust spreadsheet"), isPresented: $showingPreview) {
            Button(tr("استيراد", "Import")) {
                guard let preview else { return }
                do {
                    try SalaryTrustStore.save(preview, expected: previewBase)
                    let names = months.map(\.name).filter { MonthName.sortKey($0) > SalaryTrust.openingCutoff }
                        .sorted { MonthName.sortKey($0) < MonthName.sortKey($1) }
                    Task {
                        var errors: [String] = []
                        for name in names {
                            do { _ = try await SalaryTrustStore.addCash(month: name) }
                            catch { errors.append("\(name): \(error.localizedDescription)") }
                        }
                        if !errors.isEmpty { problem = tr("استُورد الرصيد الافتتاحي، لكن بعض الأشهر تحتاج مراجعة:\n", "Opening data imported; some months need review:\n") + errors.joined(separator: "\n") }
                    }
                } catch { problem = error.localizedDescription }
                self.preview = nil
            }
            Button(tr("إلغاء", "Cancel"), role: .cancel) { preview = nil }
        } message: {
            if let preview {
                Text(tr("\(Num.format(preview.entries.count)) سجل · \(Num.format(preview.entries.filter(\.legacyPaid).count)) مصروف بالكامل · \(Num.format(preview.entries.filter(\.reviewPending).count)) للمراجعة. استورد الملف على جهاز واحد، ثم انقل النسخة الاحتياطية إلى الجهاز الآخر. لن تتغير البيانات قبل التأكيد.", "\(preview.entries.count) entries · \(preview.entries.filter(\.legacyPaid).count) paid · \(preview.entries.filter(\.reviewPending).count) need review. Import on one device, then restore its backup on the other."))
            }
        }
        .alert(tr("أمانات الرواتب", "Salary trust"), isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) { problem = nil }
        } message: { Text(problem ?? "") }
    }
    private func pickOpening() {
        FilePicker.present(types: [UTType(filenameExtension: "xlsx") ?? .data], multiple: false) { urls in
            guard let file = urls.first else { return }
            loading = true; previewBase = stored
            Task {
                defer { loading = false; ImportCenter.removeCopy(file) }
                do {
                    guard try SalaryTrust.decode(previewBase).entries.isEmpty else { throw SalaryTrust.invalid(tr("الدفتر غير فارغ؛ لم تُستبدل البيانات.", "The ledger is not empty; data was not replaced.")) }
                    preview = try await Task.detached(priority: .userInitiated) { try SalaryTrustSpreadsheet.read(file) }.value
                    showingPreview = true
                } catch { problem = error.localizedDescription }
            }
        }
    }
}

private struct SalaryTrustEntryView: View {
    let entry: SalaryTrust.Entry
    @Binding var stored: Data
    @Environment(\.dismiss) private var dismiss
    @State private var amount = ""
    @State private var bookNumber = ""
    @State private var paymentNote = ""
    @State private var entryNote = ""
    @State private var confirmPaid = false
    @State private var caseStatus = ""
    @State private var date = Date()
    @State private var problem: String?
    var body: some View {
        Form {
            Section {
                LabeledContent(tr("الاسم", "Name"), value: entry.name).privacySensitive()
                LabeledContent(tr("الشهر", "Month"), value: "\(entry.month)/\(entry.year)")
                LabeledContent(tr("المبلغ", "Amount"), value: Money.format(Double(entry.amount)))
                if !entry.reviewPending { LabeledContent(tr("المتبقي", "Remaining"), value: Money.format(Double(entry.remaining))) }
                 if !entry.importedStatus.isEmpty { LabeledContent(tr("حالة الملف الأصلي", "Original file status"), value: entry.importedStatus) }
            }
            Section(tr("الحالة وملاحظات القيد", "Status and entry notes")) {
                TextField(tr("الحالة", "Status"), text: $caseStatus, axis: .vertical).privacySensitive()
                TextField(tr("أضف ملاحظات", "Add notes"), text: $entryNote, axis: .vertical).privacySensitive()
                Button(tr("حفظ الحالة والملاحظات", "Save status and notes")) { change { $0.caseStatus = caseStatus; $0.note = entryNote } }
            }
            if entry.reviewPending {
                Section {
                    Text(tr("هذا السجل مستبعد من الرصيد حتى تحدد حالته.", "This entry is excluded from the balance until reviewed."))
                    Button(tr("اعتمادها غير مصروفة", "Mark outstanding")) { resolve(paid: false) }
                    Button(tr("اعتمادها مصروفة بالكامل", "Mark fully paid"), role: .destructive) { confirmPaid = true }
                }
            } else {
                if !entry.payments.isEmpty {
                    Section(tr("دفعات الصرف", "Payments")) {
                        ForEach(entry.payments) { p in
                            VStack(alignment: .leading) {
                                Text("\(Money.format(Double(p.amount))) · \(p.bookNumber) · \(p.bookDate)")
                                if !p.note.isEmpty { Text(p.note).font(.caption).privacySensitive() }
                            }
                        }
                    }
                }
                if entry.remaining > 0 {
                    Section(tr("تسجيل صرف", "Record payment")) {
                        TextField(tr("المبلغ", "Amount"), text: $amount).keyboardType(.numberPad)
                        TextField(tr("رقم الكتاب", "Book number"), text: $bookNumber)
                        TextField(tr("ملاحظات الصرف (اختياري)", "Payment notes (optional)"), text: $paymentNote, axis: .vertical)
                        DatePicker(tr("تاريخ الكتاب", "Book date"), selection: $date, displayedComponents: .date)
                        Button(tr("حفظ الصرف", "Save payment")) { savePayment() }
                    }
                }
            }
        }
        .onAppear { entryNote = entry.note; caseStatus = entry.caseStatus }
        .navigationTitle(tr("تفاصيل الأمانة", "Trust details"))
        .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إغلاق", "Close")) { dismiss() } } }
        .alert(tr("تأكيد الصرف الكامل", "Confirm full payment"), isPresented: $confirmPaid) {
            Button(tr("تأكيد", "Confirm"), role: .destructive) { resolve(paid: true) }
            Button(tr("إلغاء", "Cancel"), role: .cancel) {}
        } message: { Text(tr("هل تؤكد أن مبلغ الأمانة صُرف بالكامل؟", "Confirm that the entire trust amount was paid?")) }
        .alert(tr("تحقق من البيانات", "Check data"), isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })) { Button(tr("حسناً", "OK"), role: .cancel) {} } message: { Text(problem ?? "") }
    }
    private func change(_ body: (inout SalaryTrust.Entry) throws -> Void) {
        do {
            let expected = stored; var ledger = try SalaryTrust.decode(expected)
            guard let index = ledger.entries.firstIndex(where: { $0.id == entry.id }), ledger.entries[index] == entry else { throw SalaryTrust.invalid(tr("تغيّر السجل؛ أعد فتحه.", "The entry changed; reopen it.")) }
            try body(&ledger.entries[index]); try SalaryTrustStore.save(ledger, expected: expected); dismiss()
        } catch { problem = error.localizedDescription }
    }
    private func resolve(paid: Bool) { change { $0.reviewPending = false; $0.legacyPaid = paid; $0.caseStatus = caseStatus; $0.note = entryNote } }
    private func savePayment() {
        guard let number = ArabicText.number(from: amount), number.isFinite, number > 0, number.rounded() == number,
              number <= Double(entry.remaining), !bookNumber.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            problem = tr("أدخل مبلغاً لا يتجاوز المتبقي ورقم الكتاب.", "Enter an amount no greater than the balance and the book number."); return
        }
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(identifier: "Asia/Baghdad") ?? .current
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        let stamp = String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
        change { $0.caseStatus = caseStatus; $0.note = entryNote; $0.payments.append(.init(id: UUID().uuidString, amount: Int64(number), bookNumber: bookNumber.trimmingCharacters(in: .whitespacesAndNewlines), bookDate: stamp, note: paymentNote.trimmingCharacters(in: .whitespacesAndNewlines))) }
    }
}
