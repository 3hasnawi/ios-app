import SwiftUI
import SwiftData

struct EmployeeRefundView: View {
    let month: String
    private struct Row: Identifiable {
        var candidate: RefundCandidate
        var amount = ""
        var id: PersistentIdentifier { candidate.id }
    }
    @Environment(\.modelContext) private var context
    @AppStorage(ExecutionAccountStore.storageKey) private var accounts = Data()
    @State private var search = ""
    @State private var found: [RefundCandidate] = []
    @State private var completedSearch = ""
    @State private var rows: [Row] = []
    @State private var banks: [String: RefundBank] = [:]
    @State private var source = ""
    @State private var date = Date.now
    @State private var payer = UserDefaults.standard.string(forKey: "bankStatementAccountName") ?? ""
    @State private var account = UserDefaults.standard.string(forKey: "bankStatementIBAN") ?? ""
    @State private var letters = RefundLetters()
    @State private var initialized = false
    @State private var error: String?
    @State private var working = false
    @State private var confirming = false

    private var codes: [String] { Set(rows.compactMap { EmployeeRefund.bankCode($0.candidate.person.iban) }).sorted() }
    private func plan() throws -> EmployeeRefund.Batch {
        try EmployeeRefund.plan(entries: rows.map { .init(person: $0.candidate.person, amount: $0.amount) },
            banks: banks, payer: payer, account: account, source: source)
    }

    var body: some View {
        Form {
            Section {
                LabeledContent(tr("شهر حسابات المنتسبين", "Employee account month"), value: month)
                DatePicker(tr("تاريخ التحويل", "Transfer date"), selection: $date, displayedComponents: .date)
                TextField(tr("الجهة أو المصرف المعيد (اختياري)", "Refund source (optional)"), text: $source)
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
            if !rows.isEmpty {
                Section(tr("مبلغ الإعادة لكل منتسب", "Refund amount per employee")) {
                    ForEach($rows) { $row in
                        VStack(alignment: .leading, spacing: 5) {
                            HStack {
                                Text(row.candidate.person.name).bold()
                                Spacer()
                                Button(role: .destructive) { let id = row.id; rows.removeAll { $0.id == id } } label: {
                                    Image(systemName: "minus.circle").accessibilityLabel(tr("إزالة", "Remove"))
                                }.buttonStyle(.borderless)
                            }
                            Text(row.candidate.person.iban).font(.caption.monospaced()).privacySensitive()
                            LabeledContent(tr("سويفت المنتسب", "Employee SWIFT"), value: row.candidate.person.payrollBIC)
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
                            TextField(tr("سويفت الكتاب", "Letter SWIFT"), text: bankField(code, \.bic)).textInputAutocapitalization(.characters).autocorrectionDisabled()
                        }
                    }
                }
            }
            Section {
                DisclosureGroup(tr("بيانات الجهة الدافعة والكتاب", "Payer and letter details")) {
                    TextField(tr("اسم الجهة الدافعة", "Payer name"), text: $payer)
                    TextField(tr("آيبان الجهة الدافعة", "Payer IBAN"), text: $account).autocorrectionDisabled().textInputAutocapitalization(.characters)
                    if codes.contains(EmployeeRefund.rafidainCode) {
                        letterFields(tr("كتاب الرافدين", "Rafidain letter"), $letters.rafidain)
                    }
                    if codes.contains(where: { $0 != EmployeeRefund.rafidainCode }) {
                        letterFields(tr("كتاب الرشيد والمصارف الأخرى", "Rasheed and other banks letter"), $letters.otherBanks)
                    }
                }
            }
            if !rows.isEmpty {
                Section(tr("مراجعة الدفعة", "Review batch")) {
                    if let batch = try? plan() {
                        ForEach(batch.groups, id: \.code) { group in
                            LabeledContent(group.bank.name, value: Money.format(Double(group.total)))
                        }
                        LabeledContent(tr("المجموع الكلي", "Grand total"), value: Money.format(Double(batch.total))).bold()
                    }
                    Button(tr("تصدير ملفات CSV والكتب", "Export CSV files and letters"), systemImage: "square.and.arrow.up") {
                        do { _ = try plan(); confirming = true } catch { self.error = error.localizedDescription }
                    }
                }
            }
        }
        .navigationTitle(tr("إعادة المبالغ", "Employee refunds")).navigationBarTitleDisplayMode(.inline)
        .scrollDismissesKeyboard(.interactively)
        .disabled(working)
        .overlay { if working { ProgressView().padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12)) } }
        .task(id: search) { await find() }
        .onAppear { loadLetterSettings() }
        .alert(tr("إعادة المبالغ", "Employee refunds"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK")) { error = nil }
        } message: { Text(error ?? "") }
        .confirmationDialog(tr("تأكيد مبالغ الإعادة وتجهيز الملفات؟", "Confirm refund amounts and prepare files?"), isPresented: $confirming, titleVisibility: .visible) {
            Button(tr("تجهيز ومشاركة", "Prepare and share")) { export() }
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
            TextField(tr("نسخة منه إلى", "Copies to"), text: settings.footer, axis: .vertical)
        }
    }

    private func candidateButton(_ candidate: RefundCandidate) -> some View {
        let selected = rows.contains { $0.id == candidate.id }
        let person = candidate.person
        let code = EmployeeRefund.bankCode(person.iban)
        let validBIC = code.map { EmployeeRefund.validPayrollBIC(person.payrollBIC, bankCode: $0) } ?? false
        return Button {
            if selected { rows.removeAll { $0.id == candidate.id } }
            else if let code {
                if code != EmployeeRefund.rafidainCode, banks[code] == nil { banks[code] = bankDefaults(code, person: person) }
                rows.append(Row(candidate: candidate))
            }
        } label: {
            HStack {
                VStack(alignment: .leading) {
                    Text(person.name)
                    Text([person.rank, person.workplace, person.bank].filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                    if code != nil && !validBIC {
                        Text(tr("راجع خانة السويفت في رواتب المنتسب", "Check SWIFT in the payroll row")).font(.caption).foregroundStyle(.orange)
                    }
                    Text(code.map { $0 + " · …" + person.iban.suffix(4) } ?? tr("آيبان مفقود أو غير صالح", "Missing or invalid IBAN")).font(.caption)
                }
                Spacer()
                Image(systemName: selected ? "checkmark.circle.fill" : "circle")
            }
        }.disabled(!validBIC && !selected)
    }

    private func bankDefaults(_ code: String, person: RefundPerson) -> RefundBank {
        let saved = (try? ExecutionAccountStore.decode(accounts)) ?? []
        let codes = Set(saved.filter { EmployeeRefund.bankCode($0.iban) == code }
            .map { $0.swift.trimmingCharacters(in: .whitespacesAndNewlines).uppercased() }
            .filter { $0.hasPrefix(code + "IQ") && TransferRules.validBIC($0) })
        return RefundBank(name: person.bank, bic: codes.count == 1 ? codes.first ?? "" : "")
    }
    private func bankField(_ code: String, _ field: WritableKeyPath<RefundBank, String>) -> Binding<String> {
        Binding(get: { banks[code, default: RefundBank()][keyPath: field] },
                set: { banks[code, default: RefundBank()][keyPath: field] = $0 })
    }
    private func find() async {
        let input = search, month = month, container = context.container
        guard ArabicText.cleanQuery(input).count >= 2 else { found = []; completedSearch = input; return }
        do {
            try await Task.sleep(for: .milliseconds(200))
            let result = try await Task.detached { try await RefundPayrollSearch(modelContainer: container).search(input, month: month) }.value
            guard !Task.isCancelled, search == input else { return }
            found = result; completedSearch = input
        } catch {
            guard !Task.isCancelled, search == input else { return }
            found = []; completedSearch = input; self.error = error.localizedDescription
        }
    }
    private func loadLetterSettings() {
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
    private func export() {
        guard !working else { return }
        do {
            let batch = try plan(), people = rows.map(\.candidate), container = context.container
            let month = month, date = date, settings = letters
            working = true
            Task {
                defer { working = false }
                do {
                    try await Task.detached { try await RefundPayrollSearch(modelContainer: container).verify(people, month: month) }.value
                    let urls = try await PDFWork.run { try EmployeeRefundExport.make(batch, date: date, letters: settings) }
                    ActivityShare.present(urls) { _ in
                        if let folder = urls.first?.deletingLastPathComponent() { try? FileManager.default.removeItem(at: folder) }
                    }
                } catch { self.error = error.localizedDescription }
            }
        } catch { self.error = error.localizedDescription }
    }
}
