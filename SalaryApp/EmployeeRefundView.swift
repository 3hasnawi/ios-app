import SwiftUI
import SwiftData
import UniformTypeIdentifiers

struct EmployeeRefundView: View {
    private let initialMonth: String
    init(month: String) { initialMonth = month }
    private var month: String { draft.month ?? initialMonth }
    @Bindable private var draft = RefundDraft.shared
    @Environment(\.modelContext) private var context
    @State private var search = ""
    @State private var found: [RefundCandidate] = []
    @State private var completedSearch = ""
    @State private var confirmClear = false
    @State private var error: String?
    @State private var confirming = false
    @State private var saved = false
    @State private var importingCSV = false

    private func plan() throws -> EmployeeRefund.Batch {
        try draft.snapshot.plan()
    }

    var body: some View {
        let options = draft.letterBICOptions
        let codes = options.keys.sorted()
        return Form {
            Section {
                NavigationLink { RefundHistoryView(storage: draft.storage) } label: {
                    Label(tr("الدفعات المحفوظة", "Saved batches"), systemImage: "archivebox")
                }
                if draft.rows.isEmpty, !month.isEmpty, !draft.loadFailed {
                    Button(tr("استرجاع دفعة من ملفات CSV", "Recover batch from CSV files"), systemImage: "square.and.arrow.down") { importingCSV = true }
                }
                if let message = draft.storageError {
                    Text(message).foregroundStyle(.red)
                    Button(tr("إعادة المحاولة", "Retry")) { if draft.loadFailed { draft.reload() } else { draft.persist() } }
                }
            }
            Group {
            Section {
                LabeledContent(tr("شهر حسابات المنتسبين", "Employee account month"), value: month)
                DatePicker(tr("تاريخ التحويل", "Transfer date"), selection: $draft.date, displayedComponents: .date)
                TextField(tr("الجهة أو المصرف المعيد (اختياري)", "Refund source (optional)"), text: $draft.source)
            }
            Section(tr("اختيار المنتسبين", "Select employees")) {
                TextField(tr("ابحث بالاسم", "Search by name"), text: $search).autocorrectionDisabled()
                if search.trimmingCharacters(in: .whitespacesAndNewlines).count >= 2 {
                    if completedSearch != search { ProgressView() }
                    else if found.isEmpty { Text(tr("لا توجد نتائج لهذا الشهر", "No results for this month")).foregroundStyle(.secondary) }
                    else { ForEach(found) { person in candidateButton(person) } }
                    if found.count == 50 { Text(tr("أدخل اسماً أطول لتضييق النتائج.", "Enter more of the name to narrow the results.")).font(.caption) }
                }
            }
            if !draft.rows.isEmpty {
                Section(tr("مبلغ الإعادة لكل منتسب", "Refund amount per employee")) {
                    ForEach($draft.rows) { $row in
                        VStack(alignment: .leading, spacing: 5) {
                            HStack {
                                Text(row.candidate.person.name).bold()
                                Spacer()
                                Button(role: .destructive) { draft.remove(row.id) } label: {
                                    Image(systemName: "minus.circle").accessibilityLabel(tr("إزالة", "Remove"))
                                }.buttonStyle(.borderless)
                            }
                            Text(row.candidate.person.iban).font(.caption.monospaced()).privacySensitive()
                            RefundPayrollBICLabel(person: row.candidate.person)
                            TextField(tr("المبلغ بالدينار", "Amount in IQD"), text: $row.amount).keyboardType(.numberPad)
                        }
                    }
                }
                ForEach(codes, id: \.self) { code in
                    Section(code) {
                        if code == EmployeeRefund.rafidainCode {
                            LabeledContent(tr("المصرف", "Bank"), value: EmployeeRefund.rafidainBank.name)
                            LabeledContent(tr("سويفت الكتاب", "Letter SWIFT"), value: EmployeeRefund.rafidainBank.bic)
                        } else {
                            TextField(tr("اسم المصرف", "Bank name"), text: bankField(code, \.name))
                            bankLetterBIC(code, leaders: options[code] ?? [])
                        }
                    }
                }
            }
            Section {
                DisclosureGroup(tr("بيانات الجهة الدافعة والكتاب", "Payer and letter details")) {
                    TextField(tr("اسم الجهة الدافعة", "Payer name"), text: $draft.payer)
                    TextField(tr("آيبان الجهة الدافعة", "Payer IBAN"), text: $draft.account).autocorrectionDisabled().textInputAutocapitalization(.characters)
                    if codes.contains(EmployeeRefund.rafidainCode) {
                        letterFields(tr("كتاب الرافدين", "Rafidain letter"), $draft.letters.rafidain)
                    }
                    if codes.contains(where: { $0 != EmployeeRefund.rafidainCode }) {
                        letterFields(tr("كتاب الرشيد والمصارف الأخرى", "Rasheed and other banks letter"), $draft.letters.otherBanks)
                    }
                }
            }
            if !draft.rows.isEmpty {
                Section(tr("مراجعة الدفعة", "Review batch")) {
                    if let batch = try? plan() {
                        ForEach(batch.groups, id: \.code) { group in
                            LabeledContent(group.bank.name, value: Money.format(Double(group.total)))
                        }
                        LabeledContent(tr("المجموع الكلي", "Grand total"), value: Money.format(Double(batch.total))).bold()
                    }
                    Button(tr("حفظ الدفعة بتاريخ التحويل", "Save dated batch"), systemImage: "archivebox") { finish(share: false) }
                    Button(tr("تصدير الكتب وملفات CSV وقائمة المنتسبين", "Export letters, CSVs and employee list"), systemImage: "square.and.arrow.up") {
                        do { _ = try plan(); confirming = true } catch { self.error = error.localizedDescription }
                    }
                }
            }
            }.disabled(draft.loadFailed)
        }
        .fileImporter(isPresented: $importingCSV, allowedContentTypes: [.commaSeparatedText], allowsMultipleSelection: true) { result in
            do { recover(try result.get()) } catch { self.error = error.localizedDescription }
        }
        .toolbar {
            if !draft.rows.isEmpty {
                Button(tr("تفريغ الدفعة", "Clear batch"), systemImage: "trash") { confirmClear = true }.disabled(draft.working)
            }
        }
        .confirmationDialog(tr("تفريغ دفعة الإعادة الحالية؟", "Clear the current refund batch?"), isPresented: $confirmClear, titleVisibility: .visible) {
            Button(tr("تفريغ الدفعة", "Clear batch"), role: .destructive) { draft.clear() }
        }
        .navigationTitle(tr("إعادة المبالغ", "Employee refunds")).navigationBarTitleDisplayMode(.inline)
        .scrollDismissesKeyboard(.interactively)
        .disabled(draft.working)
        .overlay { if draft.working { ProgressView().padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12)) } }
        .task(id: [month, search]) { await find() }
        .onAppear { draft.loadLetterSettings() }
        .alert(tr("إعادة المبالغ", "Employee refunds"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK")) { error = nil }
        } message: { Text(error ?? "") }
        .alert(tr("حُفظت الدفعة ضمن الدفعات المحفوظة.", "Batch saved to history."), isPresented: $saved) {
            Button(tr("حسناً", "OK")) {}
        }
        .confirmationDialog(tr("تأكيد مبالغ الإعادة وتجهيز الملفات؟", "Confirm refund amounts and prepare files?"), isPresented: $confirming, titleVisibility: .visible) {
            Button(tr("تجهيز ومشاركة", "Prepare and share")) { finish(share: true) }
        } message: {
            Text(tr("سيُجهّز ملف مستقل لكل مصرف وكتاب لكل مجموعة ممثلة في الدفعة.", "One file per bank and one letter per represented bank group will be prepared."))
        }
    }

    private func letterFields(_ title: String, _ settings: Binding<RefundLetterSettings>) -> some View {
        DisclosureGroup(title) {
            TextField(tr("الترويسة", "Letterhead"), text: settings.header, axis: .vertical)
            TextField(tr("الجهة المخاطبة", "Addressee"), text: settings.recipient)
            TextField(tr("العدد", "Letter number"), text: settings.number)
            TextField(tr("التوقيع الأول", "First signature"), text: settings.first, axis: .vertical)
            TextField(tr("التوقيع الثاني", "Second signature"), text: settings.second, axis: .vertical)
            TextField(tr("التوقيع الثالث", "Third signature"), text: settings.third, axis: .vertical)
            Text(verbatim: RefundLetterSettings.copiesTo)
        }
    }

    private func candidateButton(_ candidate: RefundCandidate) -> some View {
        let selected = draft.rows.contains { $0.id == candidate.id }
        let person = candidate.person
        let code = EmployeeRefund.bankCode(person.iban)
        return Button {
            if selected { draft.remove(candidate.id) }
            else {
                do { try draft.add(candidate) }
                catch { self.error = error.localizedDescription }
            }
        } label: {
            HStack {
                VStack(alignment: .leading) {
                    Text(person.name)
                    Text([person.rank, person.workplace, person.bank].filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                    if code != nil, let warning = person.bicWarning {
                        Text(warning).font(.caption).foregroundStyle(.orange)
                    }
                    Text(code.map { $0 + " · …" + person.iban.suffix(4) } ?? tr("آيبان مفقود أو غير صالح", "Missing or invalid IBAN")).font(.caption)
                }
                Spacer()
                Image(systemName: selected ? "checkmark.circle.fill" : "circle")
            }
        }.disabled(code == nil && !selected)
    }

    @ViewBuilder private func bankLetterBIC(_ code: String, leaders: [String]) -> some View {
        let title = tr("سويفت الكتاب", "Letter SWIFT")
        if let selected = EmployeeRefund.letterBIC(leaders: leaders, preferred: draft.banks[code]?.bic ?? "") {
            if leaders.count > 1 {
                Picker(title, selection: Binding(get: { selected }, set: { draft.banks[code, default: RefundBank()].bic = $0 })) {
                    ForEach(leaders, id: \.self) { Text($0).tag($0) }
                }
                Text(tr("تساوت هذه الرموز بعدد المنتسبين. يمكنك اختيار أحدها للكتاب.", "These codes have the same employee count. Choose one for the letter."))
                    .font(.caption).foregroundStyle(.secondary)
            } else {
                LabeledContent(title, value: selected)
            }
        } else {
            LabeledContent(title, value: tr("راجع الرفرنس في الرواتب", "Check payroll SWIFT"))
        }
    }

    private func bankField(_ code: String, _ field: WritableKeyPath<RefundBank, String>) -> Binding<String> {
        Binding(get: { draft.banks[code, default: RefundBank()][keyPath: field] },
                set: { draft.banks[code, default: RefundBank()][keyPath: field] = $0 })
    }
    private func find() async {
        let input = search, month = month, container = context.container
        guard ArabicText.cleanQuery(input).count >= 2 else { found = []; completedSearch = input; return }
        do {
            try await Task.sleep(for: .milliseconds(200))
            let result = try await Task.detached { try await RefundPayrollSearch(modelContainer: container).search(input, month: month) }.value
            guard !Task.isCancelled, self.month == month, search == input else { return }
            found = result; completedSearch = input
        } catch {
            guard !Task.isCancelled, self.month == month, search == input else { return }
            found = []; completedSearch = input; self.error = error.localizedDescription
        }
    }
    private func recover(_ files: [URL]) {
        guard !draft.working, draft.rows.isEmpty, !files.isEmpty else { return }
        let container = context.container, month = month
        draft.working = true
        Task {
            defer { draft.working = false }
            do {
                let (old, rows) = try await Task.detached {
                    let old = try RefundCSVRecovery.read(files)
                    return (old, try await RefundPayrollSearch(modelContainer: container).recover(old.payments, month: month))
                }.value
                var snapshot = draft.snapshot
                snapshot.batchID = UUID()
                snapshot.rows = rows; snapshot.date = old.date; snapshot.source = old.source
                snapshot.payer = old.payer; snapshot.account = old.account
                snapshot.banks = [:]
                for row in rows {
                    let person = row.candidate.person
                    if let code = EmployeeRefund.bankCode(person.iban), snapshot.banks[code] == nil {
                        snapshot.banks[code] = RefundBank(name: person.bank.isEmpty ? code : person.bank)
                    }
                }
                _ = try snapshot.plan()
                try draft.storage.saveDraft(snapshot)
                draft.apply(snapshot)
            } catch { self.error = error.localizedDescription }
        }
    }

    private func finish(share: Bool) {
        guard !draft.working else { return }
        do {
            let batch = try plan(), people = draft.rows.map(\.candidate), container = context.container
            let month = month, snapshot = draft.snapshot, storage = draft.storage
            draft.working = true
            Task {
                defer { draft.working = false }
                do {
                    try await Task.detached { try await RefundPayrollSearch(modelContainer: container).verify(people, month: month) }.value
                    try await Task.detached { try storage.archive(snapshot) }.value
                    guard share else { saved = true; return }
                    let urls = try await PDFWork.run { try EmployeeRefundExport.make(batch, date: snapshot.date, letters: snapshot.letters) }
                    ActivityShare.present(urls) { _ in
                        if let folder = urls.first?.deletingLastPathComponent() { try? FileManager.default.removeItem(at: folder) }
                    }
                } catch { self.error = error.localizedDescription }
            }
        } catch { self.error = error.localizedDescription }
    }
}

struct RefundPayrollBICLabel: View {
    let person: RefundPerson
    var body: some View {
        if let warning = person.bicWarning {
            Label(warning, systemImage: "exclamationmark.triangle")
                .font(.caption).foregroundStyle(.orange)
            Text(tr("يمكن إدخال المبلغ الآن. يلزم سويفت صحيح للتصدير.", "You can enter the amount now. Export requires a valid SWIFT."))
                .font(.caption).foregroundStyle(.secondary)
        } else if case .value(let bic) = person.payrollBIC {
            LabeledContent(tr("سويفت المنتسب", "Employee SWIFT"), value: bic)
        }
    }
}
