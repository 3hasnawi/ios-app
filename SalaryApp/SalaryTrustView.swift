import SwiftUI
import SwiftData
import UniformTypeIdentifiers
import VisionKit

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
    static func link(_ book: OfficialBook, to ids: Set<String>) throws {
        guard !ids.isEmpty, OfficialBookStore.shared.books.contains(where: { $0.id == book.id }) else {
            throw SalaryTrust.invalid(tr("اختر كتاباً وقيوداً موجودة.", "Choose an existing document and entries."))
        }
        try OfficialBookFiles.validate(book)
        let expected = UserDefaults.standard.data(forKey: SalaryTrust.settingsKey) ?? Data()
        let ledger = try SalaryTrust.decode(expected)
        let linked = try SalaryTrust.linking(ledger, bookID: book.id, entries: ids)
        try save(linked, expected: expected)
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
    @State private var filterMonth = 0
    @State private var filterStatus = SalaryTrust.PaymentFilter.all
    @State private var reportRequest: SalaryTrustReportRequest?
    @State private var problem: String?
    @State private var preview: SalaryTrust.Ledger?
    @State private var previewBase = Data()
    @State private var showingPreview = false
    @State private var loading = false
    @State private var selected: SalaryTrust.Entry?
    @State private var selectingBooks = false
    @State private var selectedIDs = Set<String>()
    @State private var showBookPicker = false
    private var ledger: SalaryTrust.Ledger? { try? SalaryTrust.decode(stored) }
    private var availableMonths: [Int] { Array(Set((ledger?.entries ?? []).map { $0.year * 100 + $0.month })).sorted(by: >) }
    private var visible: [SalaryTrust.Entry] {
        (ledger?.entries ?? []).filter { e in
            (filterMonth == 0 || e.year * 100 + e.month == filterMonth) && filterStatus.includes(e) &&
            (search.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || ArabicText.matchKey(e.name).contains(ArabicText.matchKey(search)))
        }.sorted { ($0.year, $0.month, $0.name) > ($1.year, $1.month, $1.name) }
    }
    var body: some View {
        List {
            if let ledger {
                Section {
                    LabeledContent(tr("المتبقي حسب الفلاتر", "Outstanding for current filters"), value: Money.format(Double(SalaryTrust.outstanding(visible))))
                    LabeledContent(tr("القيود الظاهرة", "Visible entries"), value: Num.format(visible.count))
                    let unresolved = visible.filter(\.reviewPending).count
                    if unresolved > 0 { Text(tr("\(Num.format(unresolved)) قيد للمراجعة مستبعد من المتبقي.", "\(unresolved) entries needing review are excluded from the balance.")).font(.caption).foregroundStyle(.orange) }
                }
                Section {
                    Picker(tr("الشهر", "Month"), selection: $filterMonth) {
                        Text(tr("كل الأشهر", "All months")).tag(0)
                        ForEach(availableMonths, id: \.self) { month in Text(SalaryTrust.monthLabel(month)).tag(month) }
                    }
                    Picker(tr("حالة الصرف", "Payment status"), selection: $filterStatus) {
                        ForEach(SalaryTrust.PaymentFilter.allCases) { status in Text(status.title).tag(status) }
                    }
                    HStack {
                        Button(tr("معاينة", "Preview"), systemImage: "doc.text.magnifyingglass") { prepareReport(preview: true) }
                        Spacer()
                        Button(tr("تصدير", "Export"), systemImage: "square.and.arrow.up") { prepareReport(preview: false) }
                    }.disabled(visible.isEmpty)
                } header: { Text(tr("الفلترة والكشف", "Filters and report")) }
                if !ledger.openingImported {
                    Section { Button(tr("استيراد ملف الأمانات الافتتاحي", "Import opening trust spreadsheet"), systemImage: "square.and.arrow.down") { pickOpening() }.disabled(loading) }
                }
                if selectingBooks {
                    Section {
                        Button(tr("ربط المحدد بكتاب صرف (\(Num.format(selectedIDs.count)))", "Link selected entries (\(selectedIDs.count))"), systemImage: "link") { showBookPicker = true }
                            .disabled(selectedIDs.isEmpty)
                        Text(tr("ربط الكتاب لا يسجّل صرفاً ولا يغيّر الرصيد.", "Linking a document does not record payment or change the balance.")).font(.caption).foregroundStyle(.secondary)
                    }
                }
                Section(tr("السجلات", "Entries")) {
                    ForEach(visible) { e in
                        Button {
                            if selectingBooks {
                                if !selectedIDs.insert(e.id).inserted { selectedIDs.remove(e.id) }
                            } else { selected = e }
                        } label: {
                            HStack(alignment: .top) {
                                if selectingBooks { Image(systemName: selectedIDs.contains(e.id) ? "checkmark.circle.fill" : "circle").foregroundStyle(.teal) }
                            VStack(alignment: .leading, spacing: 4) {
                                Text(e.name).font(.headline).privacySensitive()
                                Text("\(e.month)/\(e.year) · \(Money.format(Double(e.amount)))").font(.caption).foregroundStyle(.secondary)
                                if !e.caseStatus.isEmpty { Text(tr("الحالة: \(e.caseStatus)", "Status: \(e.caseStatus)")).font(.caption).privacySensitive() }
                                Text(e.reviewPending ? tr("للمراجعة · لا تدخل في الرصيد", "Review required · excluded from balance") : e.remaining == 0 ? tr("مصروف بالكامل", "Paid in full") : tr("المتبقي: \(Money.format(Double(e.remaining)))", "Remaining: \(Money.format(Double(e.remaining)))"))
                                    .font(.caption.weight(.semibold)).foregroundStyle(e.reviewPending ? Color.orange : Color.secondary)
                                if !(e.bookIds ?? []).isEmpty { Label(tr("كتاب مرتبط", "Linked document"), systemImage: "paperclip").font(.caption).foregroundStyle(.teal) }
                            }
                            }
                        }
                    }
                }
            } else { Text(tr("تعذّر قراءة دفتر الأمانات؛ لم تتغير البيانات.", "Could not read the trust ledger; data was not changed.")).foregroundStyle(.red) }
        }
        .searchable(text: $search, prompt: tr("البحث بالاسم", "Search by name"))
        .navigationTitle(tr("أمانات الرواتب", "Salary trust"))
        .onChange(of: filterMonth) { _, _ in selectedIDs.removeAll() }
        .onChange(of: filterStatus) { _, _ in selectedIDs.removeAll() }
        .onChange(of: search) { _, _ in selectedIDs.removeAll() }
        .sheet(item: $reportRequest) { request in
            if request.preview { SalaryTrustPreviewSheet(request: request) }
            else { SalaryTrustExportSheet(request: request) }
        }
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button(selectingBooks ? tr("إلغاء التحديد", "Cancel selection") : tr("تحديد للربط", "Select to link")) {
                    selectingBooks.toggle(); selectedIDs.removeAll()
                }.disabled(ledger?.entries.isEmpty != false)
            }
        }
        .sheet(isPresented: $showBookPicker) {
            SalaryTrustBookPicker(ids: selectedIDs, months: Array(Set(months.map(\.name) + (ledger?.entries ?? []).filter { selectedIDs.contains($0.id) }.map { "\($0.month) \($0.year)" })).sorted()) {
                selectedIDs.removeAll(); selectingBooks = false
            }
        }
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
    private func prepareReport(preview: Bool) {
        guard !visible.isEmpty else { return }
        let month = filterMonth == 0 ? tr("كل الأشهر", "All months") : SalaryTrust.monthLabel(filterMonth)
        reportRequest = SalaryTrustReportRequest(entries: visible, month: month, status: filterStatus.title,
            query: search.trimmingCharacters(in: .whitespacesAndNewlines), ledgerData: stored, preview: preview)
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
    @State private var entry: SalaryTrust.Entry
    @Binding var stored: Data
    @Environment(\.dismiss) private var dismiss
    @State private var archive = OfficialBookStore.shared
    @State private var showBookPicker = false
    @Query private var months: [PayrollMonth]
    @State private var amount = ""
    @State private var bookNumber = ""
    @State private var paymentNote = ""
    @State private var entryNote = ""
    @State private var confirmPaid = false
    @State private var caseStatus = ""
    @State private var date = Date()
    @State private var hasBookDate = false
    @State private var editPayment: SalaryTrust.Payment?
    @State private var problem: String?
    init(entry: SalaryTrust.Entry, stored: Binding<Data>) {
        _entry = State(initialValue: entry)
        _stored = stored
    }
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
            Section(tr("كتب الصرف المرتبطة", "Linked payment documents")) {
                Button(tr("ربط بكتاب من الأرشيف أو مسحه", "Link an archived document or scan one"), systemImage: "link") { showBookPicker = true }
                ForEach(entry.bookIds ?? [], id: \.self) { rawID in
                    if let uuid = UUID(uuidString: rawID), let book = archive.books.first(where: { $0.id == uuid }) {
                        NavigationLink { OfficialBookDetailView(id: uuid) } label: { Text(book.title) }
                        Button(tr("إزالة ارتباط الكتاب", "Unlink document"), role: .destructive) {
                            change { $0.bookIds = ($0.bookIds ?? []).filter { UUID(uuidString: $0) != uuid } }
                        }
                    } else { Text(tr("كتاب مرتبط غير موجود؛ راجع الأرشيف.", "A linked document is missing; check the archive.")).foregroundStyle(.red) }
                }
                Text(tr("ربط الكتاب لا يسجّل صرفاً ولا يغيّر الرصيد.", "Linking a document does not record payment or change the balance.")).font(.caption).foregroundStyle(.secondary)
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
                                Text(([Money.format(Double(p.amount)), p.bookNumber, p.bookDate].filter { !$0.isEmpty }).joined(separator: " · "))
                                Button(tr("تعديل بيانات الكتاب", "Edit document details")) { editPayment = p }.font(.caption)
                                if !p.note.isEmpty { Text(p.note).font(.caption).privacySensitive() }
                            }
                        }
                    }
                }
                if entry.remaining > 0 {
                    Section(tr("تسجيل صرف", "Record payment")) {
                        TextField(tr("المبلغ", "Amount"), text: $amount).keyboardType(.numberPad)
                        TextField(tr("رقم الكتاب (اختياري)", "Book number (optional)"), text: $bookNumber)
                        Toggle(tr("إضافة تاريخ الكتاب (اختياري)", "Add book date (optional)"), isOn: $hasBookDate)
                        if hasBookDate { DatePicker(tr("تاريخ الكتاب", "Book date"), selection: $date, displayedComponents: .date).environment(\.timeZone, TimeZone(identifier: "Asia/Baghdad") ?? .current) }
                        TextField(tr("ملاحظات الصرف (اختياري)", "Payment notes (optional)"), text: $paymentNote, axis: .vertical)
                        Button(tr("حفظ الصرف", "Save payment")) { savePayment() }
                    }
                }
            }
        }
        .onAppear { entryNote = entry.note; caseStatus = entry.caseStatus }
        .sheet(item: $editPayment) { payment in
            SalaryTrustPaymentEditor(payment: payment) { updated in
                let expected = stored
                var ledger = try SalaryTrust.decode(expected)
                guard let index = ledger.entries.firstIndex(where: { $0.id == entry.id }), ledger.entries[index] == entry,
                      let paymentIndex = ledger.entries[index].payments.firstIndex(where: { $0.id == payment.id }),
                      ledger.entries[index].payments[paymentIndex] == payment else {
                    throw SalaryTrust.invalid(tr("تغيّرت الدفعة؛ أعد فتح القيد.", "The payment changed; reopen the entry."))
                }
                ledger.entries[index].payments[paymentIndex] = updated
                try SalaryTrustStore.save(ledger, expected: expected)
                entry = ledger.entries[index]
            }
        }
        .sheet(isPresented: $showBookPicker) {
            SalaryTrustBookPicker(ids: [entry.id], months: Array(Set(months.map(\.name) + ["\(entry.month) \(entry.year)"])).sorted()) {
                if let updated = try? SalaryTrust.decode(stored).entries.first(where: { $0.id == entry.id }) { entry = updated }
            }
        }
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
              number <= Double(entry.remaining) else {
            problem = tr("أدخل مبلغاً لا يتجاوز المتبقي.", "Enter an amount no greater than the balance."); return
        }
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(identifier: "Asia/Baghdad") ?? .current
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        let stamp = String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
        change { $0.caseStatus = caseStatus; $0.note = entryNote; $0.payments.append(.init(id: UUID().uuidString, amount: Int64(number), bookNumber: bookNumber.trimmingCharacters(in: .whitespacesAndNewlines), bookDate: hasBookDate ? stamp : "", note: paymentNote.trimmingCharacters(in: .whitespacesAndNewlines))) }
    }
}


/// تعديل بيانات الكتاب والملاحظة دون تغيير مبلغ الدفعة.
private struct SalaryTrustPaymentEditor: View {
    let payment: SalaryTrust.Payment
    let save: (SalaryTrust.Payment) throws -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var number: String
    @State private var note: String
    @State private var hasDate: Bool
    @State private var date: Date
    @State private var error: String?
    init(payment: SalaryTrust.Payment, save: @escaping (SalaryTrust.Payment) throws -> Void) {
        self.payment = payment; self.save = save
        _number = State(initialValue: payment.bookNumber)
        _note = State(initialValue: payment.note)
        _hasDate = State(initialValue: !payment.bookDate.isEmpty)
        let formatter = DateFormatter(); formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.timeZone = TimeZone(identifier: "Asia/Baghdad")
        formatter.dateFormat = "yyyy-MM-dd"
        _date = State(initialValue: formatter.date(from: payment.bookDate) ?? Date())
    }
    var body: some View {
        NavigationStack {
            Form {
                LabeledContent(tr("مبلغ الصرف", "Payment"), value: Money.format(Double(payment.amount)))
                TextField(tr("رقم الكتاب (اختياري)", "Book number (optional)"), text: $number)
                Toggle(tr("إضافة تاريخ الكتاب (اختياري)", "Add book date (optional)"), isOn: $hasDate)
                if hasDate { DatePicker(tr("تاريخ الكتاب", "Book date"), selection: $date, displayedComponents: .date).environment(\.timeZone, TimeZone(identifier: "Asia/Baghdad") ?? .current) }
                TextField(tr("ملاحظات الصرف (اختياري)", "Payment notes (optional)"), text: $note, axis: .vertical)
            }
            .navigationTitle(tr("بيانات كتاب الصرف", "Payment document details"))
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(tr("إلغاء", "Cancel")) { dismiss() } }
                ToolbarItem(placement: .confirmationAction) { Button(tr("حفظ", "Save")) { persist() } }
            }
            .alert(tr("تعذّر الحفظ", "Could not save"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
                Button(tr("حسناً", "OK"), role: .cancel) { error = nil }
            } message: { Text(error ?? "") }
        }
    }
    private func persist() {
        var updated = payment
        updated.bookNumber = number.trimmingCharacters(in: .whitespacesAndNewlines)
        updated.note = note.trimmingCharacters(in: .whitespacesAndNewlines)
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(identifier: "Asia/Baghdad") ?? .current
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        updated.bookDate = hasDate ? String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0) : ""
        do { try save(updated); dismiss() } catch { self.error = error.localizedDescription }
    }
}

/// الكتاب يبقى نسخة واحدة في الأرشيف؛ الربط بالمجموعة يسجل المعرّف فقط دون أي حركة مالية.
private struct SalaryTrustBookPicker: View {
    let ids: Set<String>
    let months: [String]
    let onLinked: () -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var store = OfficialBookStore.shared
    @State private var showScanner = false
    @State private var draft: ScannedBookDraft?
    @State private var search = ""
    @State private var notice: String?
    @State private var didLink = false
    @State private var editorResult: String?
    @State private var pendingBook: OfficialBook?
    private var selectedEntries: [SalaryTrust.Entry] {
        ((try? SalaryTrustStore.load())?.entries ?? []).filter { ids.contains($0.id) }
    }
    private var visible: [OfficialBook] {
        let text = ArabicText.normalize(search)
        return store.books.filter { text.isEmpty || ArabicText.normalize($0.title + " " + $0.month + " " + ($0.entity ?? "")).contains(text) }
    }
    var body: some View {
        NavigationStack {
            List {
                Section(tr("القيود المحددة", "Selected entries")) {
                    ForEach(selectedEntries) { entry in
                        VStack(alignment: .leading) {
                            Text(entry.name).privacySensitive()
                            Text("\(entry.month)/\(entry.year)").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    if selectedEntries.count != ids.count { Text(tr("تغيّرت بعض القيود؛ أعد اختيارها.", "Some entries changed; select them again.")).foregroundStyle(.red) }
                }
                Section {
                    Button(tr("مسح كتاب جديد", "Scan a new document"), systemImage: "doc.viewfinder") {
                        if VNDocumentCameraViewController.isSupported { showScanner = true }
                        else { notice = OfficialBookError.unavailableCamera.localizedDescription }
                    }
                    .disabled(months.isEmpty)
                    Text(tr("يُحفظ الكتاب في الأرشيف ثم يُربط بالقيود المحددة. لا يُسجَّل صرف تلقائياً.", "The document is saved to the archive and linked to the selected entries. No payment is recorded."))
                        .font(.caption).foregroundStyle(.secondary)
                }
                Section(tr("اختر كتاباً من الأرشيف", "Choose an archived document")) {
                    if let error = store.loadError { Text(error).foregroundStyle(.red) }
                    else if visible.isEmpty { Text(tr("لا توجد كتب مطابقة.", "No matching documents.")) }
                    ForEach(visible) { book in
                        Button { pendingBook = book } label: {
                            VStack(alignment: .leading) {
                                Text(book.title)
                                Text(book.month).font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
            .searchable(text: $search, prompt: tr("اسم الكتاب أو الشهر", "Document name or month"))
            .navigationTitle(tr("ربط كتاب الصرف", "Link payment document"))
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إغلاق", "Close")) { dismiss() } } }
            .sheet(isPresented: $showScanner) {
                OfficialBookScanner(onComplete: { images in
                    showScanner = false
                    Task { @MainActor in try? await Task.sleep(for: .milliseconds(250)); draft = ScannedBookDraft(images: images) }
                }, onFailure: { text in showScanner = false; notice = text }, onCancel: { showScanner = false })
            }
            .sheet(item: $draft, onDismiss: {
                if let result = editorResult { notice = result; editorResult = nil }
            }) { draft in
                OfficialBookEditor(months: months, initialMonth: months.first ?? "", images: draft.images, existing: nil) { book in
                    do {
                        try SalaryTrustStore.link(book, to: ids)
                        didLink = true
                        editorResult = tr("حُفظ الكتاب في الأرشيف ورُبط بالقيود المحددة.", "The document was saved and linked to the selected entries.")
                    } catch {
                        editorResult = tr("حُفظ الكتاب في الأرشيف، لكن تعذّر ربطه: \(error.localizedDescription)", "The document was saved, but linking failed: \(error.localizedDescription)")
                    }
                }
            }
            .confirmationDialog(tr("ربط الكتاب بالقيود المحددة؟", "Link this document to the selected entries?"), isPresented: Binding(
                get: { pendingBook != nil }, set: { if !$0 { pendingBook = nil } }), titleVisibility: .visible, presenting: pendingBook) { book in
                Button(tr("ربط الكتاب", "Link document")) {
                    do { try SalaryTrustStore.link(book, to: ids); pendingBook = nil; onLinked(); dismiss() }
                    catch { pendingBook = nil; notice = error.localizedDescription }
                }
                Button(tr("إلغاء", "Cancel"), role: .cancel) { pendingBook = nil }
            } message: { _ in Text(tr("الربط لا يسجّل صرفاً ولا يغيّر الرصيد.", "Linking does not record payment or change the balance.")) }
            .alert(tr("ربط كتاب الصرف", "Link payment document"), isPresented: Binding(get: { notice != nil }, set: { if !$0 { notice = nil } })) {
                Button(tr("حسناً", "OK"), role: .cancel) {
                    notice = nil
                    if didLink { onLinked(); dismiss() }
                }
            } message: { Text(notice ?? "") }
        }
    }
}
