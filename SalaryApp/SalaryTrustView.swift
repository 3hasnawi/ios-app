import SwiftUI
import UniformTypeIdentifiers
import VisionKit

/// يحفظ الدفتر كاملاً دفعة واحدة بعد الفحص، ويمنع استبداله أثناء النسخ أو الاسترجاع.
@MainActor enum SalaryTrustStore {
    static func load() throws -> SalaryTrust.Ledger { try SalaryTrust.decode(UserDefaults.standard.data(forKey: SalaryTrust.settingsKey) ?? Data()) }
    static func save(_ ledger: SalaryTrust.Ledger, expected: Data) throws {
        guard !ImportCenter.shared.isBusy else {
            throw SalaryTrust.invalid(tr("انتظر انتهاء الاستيراد قبل حفظ دفتر الأمانات.", "Wait for import to finish before saving the trust ledger."))
        }
        try persist(ledger, expected: expected)
    }
    /// يمتلك الاستيراد الموحّد القفل أثناء استبدال الدفتر المراجع؛ لا يؤخذ شهر من اسم الملف.
    static func replaceFromImport(_ incoming: SalaryTrust.Ledger, expected: Data) throws {
        guard ImportCenter.shared.isBusy else {
            throw SalaryTrust.invalid(tr("أعد مراجعة ملف الأمانات قبل الاستيراد.", "Review the trust spreadsheet again before importing."))
        }
        let replacement = try SalaryTrust.replacing(with: incoming, existing: SalaryTrust.decode(expected))
        try persist(replacement, expected: expected)
    }
    private static func persist(_ ledger: SalaryTrust.Ledger, expected: Data) throws {
        guard !BackupManager.shared.isWorking, !StaffImportState.shared.isImporting,
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

}

struct SalaryTrustView: View {
    @AppStorage(SalaryTrust.settingsKey) private var stored = Data()
    @State private var search = ""
    @State private var filterMonth = 0
    @State private var filterStatus: SalaryTrust.PaymentFilter
    @State private var reportRequest: SalaryTrustReportRequest?
    @State private var problem: String?
    @State private var preview: SalaryTrust.Ledger?
    @State private var previewBase = Data()
    @State private var showingPreview = false
    @State private var loading = false
    @State private var selectedPerson: SalaryTrust.Person?
    @State private var selectingBooks = false
    @State private var selectedIDs = Set<String>()
    @State private var showBookPicker = false
    init(initialStatus: SalaryTrust.PaymentFilter = .all) {
        _filterStatus = State(initialValue: initialStatus)
    }
    private var ledger: SalaryTrust.Ledger? { try? SalaryTrust.decode(stored) }
    private var availableMonths: [Int] { Array(Set((ledger?.entries ?? []).map { $0.year * 100 + $0.month })).sorted(by: >) }
    private var visible: [SalaryTrust.Entry] {
        SalaryTrust.filtered(ledger?.entries ?? [], month: filterMonth, status: filterStatus, search: search)
    }
    var body: some View {
        List {
            if let ledger {
                let visible = SalaryTrust.filtered(ledger.entries, month: filterMonth, status: filterStatus, search: search)
                let people = SalaryTrust.people(visible)
                Section {
                    LabeledContent(tr("المتبقي حسب الفلاتر", "Outstanding for current filters"), value: Money.format(Double(SalaryTrust.outstanding(visible))))
                    LabeledContent(tr("الأسماء الظاهرة", "Visible people"), value: Num.format(people.count))
                    LabeledContent(tr("قيود الأشهر الظاهرة", "Visible monthly entries"), value: Num.format(visible.count))
                    Text("\(filterMonth == 0 ? tr("كل الأشهر", "All months") : SalaryTrust.monthLabel(filterMonth)) · \(filterStatus.title)")
                        .font(.caption).foregroundStyle(.secondary)
                    let unresolved = visible.filter(\.reviewPending).count
                    if unresolved > 0 { Text(tr("\(Num.format(unresolved)) قيد للمراجعة مستبعد من المتبقي.", "\(unresolved) entries needing review are excluded from the balance.")).font(.caption).foregroundStyle(.orange) }
                }
                Section {
                    Button(tr("استيراد أو تحديث ملف الأمانات", "Import or update trust spreadsheet"), systemImage: "square.and.arrow.down") { pickOpening() }
                        .disabled(loading)
                    Text(tr("الملاحظات والحالة وتاريخ الصرف من Excel حصراً. لتعديلها حدّث الملف وأعد استيراده.", "Notes, status and payment date come only from Excel. Update and reimport the file to change them."))
                        .font(.caption).foregroundStyle(.secondary)
                    if ledger.version == 1 && !ledger.entries.isEmpty {
                        Text(tr("الدفتر الحالي محفوظ من نسخة سابقة؛ يبقى كما هو حتى تؤكد استيراد ملف بديل.", "The existing ledger is from an earlier version and stays intact until you confirm a replacement spreadsheet."))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                if selectingBooks {
                    Section {
                        Button(tr("ربط المحدد بكتاب صرف (\(Num.format(selectedIDs.count)))", "Link selected entries (\(selectedIDs.count))"), systemImage: "link") { showBookPicker = true }
                            .disabled(selectedIDs.isEmpty)
                        Text(tr("ربط الكتاب لا يسجّل صرفاً ولا يغيّر الرصيد.", "Linking a document does not record payment or change the balance.")).font(.caption).foregroundStyle(.secondary)
                    }
                }
                Section(tr("الأسماء", "People")) {
                    ForEach(people) { person in
                        Button {
                            if selectingBooks {
                                if person.entryIDs.isSubset(of: selectedIDs) { selectedIDs.subtract(person.entryIDs) }
                                else { selectedIDs.formUnion(person.entryIDs) }
                            } else { selectedPerson = person }
                        } label: {
                            HStack(alignment: .top) {
                                if selectingBooks {
                                    Image(systemName: person.entryIDs.isSubset(of: selectedIDs) ? "checkmark.circle.fill" : selectedIDs.isDisjoint(with: person.entryIDs) ? "circle" : "minus.circle.fill")
                                        .foregroundStyle(.teal)
                                }
                                VStack(alignment: .leading, spacing: 5) {
                                    Text(person.name).font(.headline).privacySensitive()
                                    Text(tr("\(Num.format(person.entries.count)) شهر · المجموع: \(Money.format(Double(person.total)))", "\(person.entries.count) months · Total: \(Money.format(Double(person.total)))"))
                                        .font(.caption).foregroundStyle(.secondary)
                                    Text(tr("المتبقي المؤكد: \(Money.format(Double(person.remaining)))", "Confirmed outstanding: \(Money.format(Double(person.remaining)))"))
                                        .font(.caption.weight(.semibold))
                                    if person.reviewTotal > 0 {
                                        Text(tr("توجد قيود للمراجعة مستبعدة من المتبقي.", "Entries needing review are excluded from outstanding."))
                                            .font(.caption).foregroundStyle(.orange)
                                    }
                                    Text(tr("افتح الاسم لعرض كل الأشهر وتفاصيلها", "Open to view every month and its details"))
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer(minLength: 0)
                                Image(systemName: "chevron.forward").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .contextMenu {
                            Button(tr("تفاصيل كل الأشهر", "All monthly details"), systemImage: "calendar") { selectedPerson = person }
                            Button(tr("تحديد الأشهر الظاهرة للربط بكتاب", "Select visible months for linking"), systemImage: "checkmark.circle") {
                                selectingBooks = true; selectedIDs.formUnion(person.entryIDs)
                            }
                            Button(tr("ربط الأشهر الظاهرة بكتاب", "Link visible months to a document"), systemImage: "link") {
                                selectedIDs = person.entryIDs; selectingBooks = true; showBookPicker = true
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
            ToolbarItemGroup(placement: .topBarTrailing) {
                Menu {
                    Picker(tr("الشهر", "Month"), selection: $filterMonth) {
                        Text(tr("كل الأشهر", "All months")).tag(0)
                        ForEach(availableMonths, id: \.self) { month in Text(SalaryTrust.monthLabel(month)).tag(month) }
                    }
                    Picker(tr("حالة الصرف", "Payment status"), selection: $filterStatus) {
                        ForEach(SalaryTrust.PaymentFilter.allCases) { status in Text(status.title).tag(status) }
                    }
                    if filterMonth != 0 || filterStatus != .all {
                        Button(tr("مسح الفلاتر", "Clear filters"), systemImage: "xmark.circle") { filterMonth = 0; filterStatus = .all }
                    }
                } label: {
                    Image(systemName: filterMonth != 0 || filterStatus != .all ? "line.3.horizontal.decrease.circle.fill" : "line.3.horizontal.decrease.circle")
                }
                .accessibilityLabel(tr("فلترة الشهر وحالة الصرف", "Filter month and payment status"))
                Menu {
                    Button(tr("معاينة الكشف", "Preview report"), systemImage: "doc.text.magnifyingglass") { prepareReport(preview: true) }.disabled(visible.isEmpty)
                    Button(tr("تصدير الكشف", "Export report"), systemImage: "square.and.arrow.up") { prepareReport(preview: false) }.disabled(visible.isEmpty)
                    Button(selectingBooks ? tr("إلغاء التحديد", "Cancel selection") : tr("تحديد للربط بكتاب", "Select for document linking"), systemImage: selectingBooks ? "xmark.circle" : "checkmark.circle") {
                        selectingBooks.toggle(); selectedIDs.removeAll()
                    }.disabled(ledger?.entries.isEmpty != false)
                } label: { Image(systemName: "ellipsis").rotationEffect(.degrees(90)) }
                .accessibilityLabel(tr("خيارات الأمانات", "Trust options"))
            }
        }
        .sheet(isPresented: $showBookPicker) {
            SalaryTrustBookPicker(ids: selectedIDs, months: Array(Set((ledger?.entries ?? []).filter { selectedIDs.contains($0.id) }.map { SalaryTrust.monthLabel($0.year * 100 + $0.month) })).sorted()) {
                selectedIDs.removeAll(); selectingBooks = false
            }
        }
        .sheet(item: $selectedPerson) { person in
            NavigationStack { SalaryTrustPersonView(personID: person.id, stored: $stored) }
        }
        .alert(tr("اعتماد ملف الأمانات", "Confirm trust spreadsheet"), isPresented: $showingPreview) {
            Button(tr("استيراد", "Import")) {
                guard let preview else { return }
                do {
                    try SalaryTrustStore.save(preview, expected: previewBase)
                    selectedIDs.removeAll(); selectingBooks = false
                } catch { problem = error.localizedDescription }
                self.preview = nil
            }
            Button(tr("إلغاء", "Cancel"), role: .cancel) { preview = nil }
        } message: {
            if let preview {
                let oldCount = (try? SalaryTrust.decode(previewBase).entries.count) ?? 0
                Text(tr("\(Num.format(preview.entries.count)) سجل · \(Num.format(preview.entries.filter(\.legacyPaid).count)) مصروف بالكامل · \(Num.format(preview.entries.filter(\.reviewPending).count)) للمراجعة. سيحل الملف محل معلومات \(Num.format(oldCount)) سجل حالي في دفتر الأمانات. تبقى الكتب في الأرشيف وروابط السجلات المطابقة محفوظة. لن يتغير الدفتر قبل التأكيد.", "\(preview.entries.count) entries · \(preview.entries.filter(\.legacyPaid).count) paid · \(preview.entries.filter(\.reviewPending).count) need review. Replaces financial information for \(oldCount) existing entries after confirmation. Archived documents and links for matching entries are kept."))
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
                    let existing = try SalaryTrust.decode(previewBase)
                    let incoming = try await Task.detached(priority: .userInitiated) { try SalaryTrustSpreadsheet.read(file) }.value
                    preview = try SalaryTrust.replacing(with: incoming, existing: existing)
                    showingPreview = true
                } catch { problem = error.localizedDescription }
            }
        }
    }
}

/// كل الأشهر تظهر عند فتح الاسم حتى لو كانت قائمة الأسماء مفلترة بشهر أو حالة.
private struct SalaryTrustPersonView: View {
    let personID: String
    @Binding var stored: Data
    @Environment(\.dismiss) private var dismiss
    private var person: SalaryTrust.Person? {
        guard let ledger = try? SalaryTrust.decode(stored) else { return nil }
        return SalaryTrust.people(ledger.entries).first { $0.id == personID }
    }
    var body: some View {
        List {
            if let person {
                Section {
                    Text(person.name).font(.headline).privacySensitive()
                    LabeledContent(tr("عدد الأشهر", "Months"), value: Num.format(person.entries.count))
                    LabeledContent(tr("مجموع كل الأشهر", "Total for all months"), value: Money.format(Double(person.total)))
                    LabeledContent(tr("المصروف المؤكد", "Confirmed paid"), value: Money.format(Double(person.paid)))
                    LabeledContent(tr("المتبقي المؤكد", "Confirmed outstanding"), value: Money.format(Double(person.remaining)))
                    if person.reviewTotal > 0 {
                        LabeledContent(tr("مبالغ للمراجعة", "Amounts needing review"), value: Money.format(Double(person.reviewTotal)))
                            .foregroundStyle(.orange)
                        Text(tr("مبالغ المراجعة ضمن المجموع، ولا تدخل في المصروف أو المتبقي المؤكد.", "Review amounts are included in the total but excluded from confirmed paid and outstanding."))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Text(tr("كل أشهر هذا الاسم، بغض النظر عن فلاتر القائمة السابقة.", "Every month for this name, regardless of the previous list filters."))
                        .font(.caption).foregroundStyle(.secondary)
                }
                Section(tr("تفاصيل الأشهر", "Monthly details")) {
                    ForEach(person.entries) { entry in
                        NavigationLink {
                            SalaryTrustEntryView(entry: entry, stored: $stored)
                        } label: {
                            VStack(alignment: .leading, spacing: 6) {
                                HStack {
                                    Text(SalaryTrust.monthLabel(entry.year * 100 + entry.month)).font(.headline)
                                    Spacer()
                                    Text(Money.format(Double(entry.amount))).font(.headline).monospacedDigit()
                                }
                                Text(SalaryTrust.paymentStatus(entry)).font(.caption.weight(.semibold))
                                    .foregroundStyle(entry.reviewPending ? Color.orange : Color.secondary)
                                Text(tr("الحالة بالملف: \(entry.importedStatus.orDash)", "File status: \(entry.importedStatus.orDash)"))
                                    .font(.caption).privacySensitive()
                                Text(tr("تاريخ الصرف: \(entry.displayPaymentDate.orDash)", "Payment date: \(entry.displayPaymentDate.orDash)"))
                                    .font(.caption).foregroundStyle(.secondary).privacySensitive()
                                if !entry.displayNotes.isEmpty {
                                    Text(tr("الملاحظات: \(entry.displayNotes)", "Notes: \(entry.displayNotes)"))
                                        .font(.caption).foregroundStyle(.secondary).privacySensitive()
                                }
                                if !(entry.bookIds ?? []).isEmpty {
                                    Label(tr("كتاب مرتبط", "Linked document"), systemImage: "paperclip").font(.caption).foregroundStyle(.teal)
                                }
                            }
                        }
                    }
                }
            } else {
                Text(tr("تعذّر العثور على قيود هذا الاسم؛ أعد فتح القائمة.", "This person's entries are unavailable; reopen the list."))
                    .foregroundStyle(.secondary)
            }
        }
        .navigationTitle(tr("أمانات المنتسب", "Person’s salary trust"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إغلاق", "Close")) { dismiss() } } }
    }
}

private struct SalaryTrustEntryView: View {
    @State private var entry: SalaryTrust.Entry
    @Binding var stored: Data
    @Environment(\.dismiss) private var dismiss
    @State private var archive = OfficialBookStore.shared
    @State private var showBookPicker = false
    @State private var problem: String?
    init(entry: SalaryTrust.Entry, stored: Binding<Data>) {
        _entry = State(initialValue: entry); _stored = stored
    }
    var body: some View {
        Form {
            Section {
                LabeledContent(tr("الاسم", "Name"), value: entry.name).privacySensitive()
                LabeledContent(tr("الرتبة", "Rank"), value: entry.rank.orDash)
                LabeledContent(tr("الفوج أو المديرية", "Workplace"), value: entry.workplace.orDash).privacySensitive()
                LabeledContent(tr("الشهر", "Month"), value: SalaryTrust.monthLabel(entry.year * 100 + entry.month))
                LabeledContent(tr("المبلغ", "Amount"), value: Money.format(Double(entry.amount)))
                LabeledContent(tr("حالة الصرف", "Payment status"), value: SalaryTrust.paymentStatus(entry))
                if !entry.reviewPending {
                    LabeledContent(tr("المصروف", "Paid"), value: Money.format(Double(entry.paid)))
                    LabeledContent(tr("المتبقي", "Remaining"), value: Money.format(Double(entry.remaining)))
                }
            }
            Section(tr("المعلومات المستوردة", "Imported information")) {
                LabeledContent(tr("الحالة بالملف", "File status"), value: entry.importedStatus.orDash).privacySensitive()
                LabeledContent(tr("تاريخ الصرف", "Payment date"), value: entry.displayPaymentDate.orDash).privacySensitive()
                VStack(alignment: .leading, spacing: 6) {
                    Text(tr("الملاحظات", "Notes")).font(.caption).foregroundStyle(.secondary)
                    Text(entry.displayNotes.orDash).privacySensitive().textSelection(.enabled)
                }
                Text(tr("تحديث هذه المعلومات يكون باستيراد ملف Excel جديد فقط.", "Update this information by importing a new Excel spreadsheet."))
                    .font(.caption).foregroundStyle(.secondary)
                if entry.reviewPending {
                    Text(tr("الحالة تحتاج مراجعة في ملف Excel؛ هذا القيد مستبعد من المتبقي حتى تحديث الملف.", "Review the status in Excel. This entry is excluded from the balance until the spreadsheet is updated."))
                        .font(.caption).foregroundStyle(.orange)
                }
            }
            if !entry.payments.isEmpty {
                Section(tr("دفعات محفوظة من نسخة سابقة", "Payments saved by an earlier version")) {
                    ForEach(entry.payments) { p in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(Money.format(Double(p.amount)))
                            if !p.bookNumber.isEmpty { Text(p.bookNumber).font(.caption) }
                            if !p.bookDate.isEmpty { Text(p.bookDate).font(.caption) }
                            if !p.note.isEmpty { Text(p.note).font(.caption).privacySensitive() }
                        }
                    }
                }
            }
            Section(tr("الكتب المرتبطة", "Linked documents")) {
                Button(tr("ربط بكتاب من الأرشيف أو مسحه", "Link an archived document or scan one"), systemImage: "link") { showBookPicker = true }
                ForEach(entry.bookIds ?? [], id: \.self) { rawID in
                    if let uuid = UUID(uuidString: rawID), let book = archive.books.first(where: { $0.id == uuid }) {
                        NavigationLink { OfficialBookDetailView(id: uuid) } label: { Text(book.title) }
                        Button(tr("إزالة ارتباط الكتاب", "Unlink document"), role: .destructive) { unlink(uuid) }
                    } else { Text(tr("كتاب مرتبط غير موجود؛ راجع الأرشيف.", "A linked document is missing; check the archive.")).foregroundStyle(.red) }
                }
                Text(tr("ربط الكتاب لا يغيّر الحالة أو تاريخ الصرف أو الرصيد.", "Linking a document does not change status, payment date or balance."))
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .sheet(isPresented: $showBookPicker) {
            SalaryTrustBookPicker(ids: [entry.id], months: [SalaryTrust.monthLabel(entry.year * 100 + entry.month)]) {
                if let updated = try? SalaryTrust.decode(stored).entries.first(where: { $0.id == entry.id }) { entry = updated }
            }
        }
        .navigationTitle(tr("تفاصيل الأمانة", "Trust details"))
        .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إغلاق", "Close")) { dismiss() } } }
        .alert(tr("تحقق من البيانات", "Check data"), isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(problem ?? "") }
    }
    private func unlink(_ uuid: UUID) {
        do {
            let expected = stored
            var ledger = try SalaryTrust.decode(expected)
            guard let index = ledger.entries.firstIndex(where: { $0.id == entry.id }), ledger.entries[index] == entry else {
                throw SalaryTrust.invalid(tr("تغيّر السجل؛ أعد فتحه.", "The entry changed; reopen it."))
            }
            ledger.entries[index].bookIds = (entry.bookIds ?? []).filter { UUID(uuidString: $0) != uuid }
            try SalaryTrustStore.save(ledger, expected: expected)
            entry = ledger.entries[index]
        } catch { problem = error.localizedDescription }
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
