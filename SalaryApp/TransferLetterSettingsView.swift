import SwiftUI
import SwiftData
import UniformTypeIdentifiers

/// حفظ الخيارات فقط؛ يجب ربطها بمواضع كل قالب قبل تفعيلها في الكتب.
struct TransferLetterSettingsView: View {
    let bookID: String?
    let currentMonth: String?
    let entityTitle: String?
    let entityAccount: String?
    let entityAccountKey: String?
    init(bookID: String? = nil, currentMonth: String? = nil, entityTitle: String? = nil,
         entityAccount: String? = nil, entityAccountKey: String? = nil) {
        self.bookID = bookID; self.currentMonth = currentMonth; self.entityTitle = entityTitle
        self.entityAccount = entityAccount; self.entityAccountKey = entityAccountKey
        _target = State(initialValue: bookID ?? "all")
    }
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse)]) private var months: [PayrollMonth]
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var storedData = Data()
    @AppStorage(TransferHeaderFont.boldSettingsKey) private var boldData = Data()
    @AppStorage(TransferHeaderFont.regularSettingsKey) private var regularData = Data()
    @AppStorage(TransferReferencePack.settingsKey) private var referenceData = Data()
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()
    @AppStorage("bankStatementIBAN") private var payerIBAN = ""
    @State private var target = "all"
    @State private var selected = Set<String>()
    @State private var header = ""
    @State private var first = ""
    @State private var second = ""
    @State private var third = ""
    @State private var footer = ""
    @State private var fixedNumber = ""
    @State private var foodContractReference = ""
    @State private var bodyTemplate = ""
    @State private var signatureOffset = 0
    @State private var bodyMonth = ""
    @State private var manualBodyMonth = ""
    @State private var firstDate = ""
    @State private var secondDate = ""
    @State private var thirdDate = ""
    @State private var beneficiary = ""
    @State private var referenceBeneficiary: String?
    @State private var accountReferenceSnapshot = Data()
    @State private var accountPayerSnapshot = ""
    @State private var accountSourceError: String?
    @State private var loadingAccount = false
    @State private var mixed = Set<String>()
    @State private var importing = false
    @State private var error: String?
    @State private var saved = false

    private struct Choice: Identifiable { let id: String; let title: String }
    private var titles: [Choice] { [
        ("food_company", tr("شركة عراق الجوال", "Iraq Al-Jawwal")),
        ("food_tax", tr("ضريبة عقد الإطعام", "Food contract tax")),
        ("tax_social", tr("الضريبة والحماية الاجتماعية", "Tax and social protection")),
        ("contracts", tr("ضمان العقود", "Contract social security")),
        ("cash_salary", tr("أمانات رواتب", "Cash salaries")),
        ("ramadi", tr("تنفيذ الرمادي", "Ramadi execution")),
        ("housing", tr("صندوق الإسكان", "Housing fund")),
        ("water", tr("الموارد المائية", "Water resources")),
        ("rafidain_anbar", tr("الرافدين فرع الأنبار", "Rafidain Anbar")),
        ("rafidain_group", tr("مجموعة الرافدين", "Rafidain group")),
        ("other_banks_group", tr("مجموعة رشيد ومصارف أخرى", "Rasheed and other banks group"))
    ].map { Choice(id: $0.0, title: $0.1) } }
    private var supportsBeneficiary: Bool {
        guard let bookID else { return false }
        return ["food_company", "food_tax", "tax_social", "contracts", "cash_salary", "ramadi", "housing", "water"].contains(bookID)
    }
    private var isEntityAccount: Bool { ["ramadi", "housing", "water"].contains(bookID ?? "") }
    private var originalAccount: String? { isEntityAccount ? entityAccount : referenceBeneficiary }
    private var beneficiarySource: String? { isEntityAccount ? entityTitle : bookID }
    private var availableMonths: [String] {
        var values = Set(months.map(\.name).filter { MonthName.sortKey($0) > 0 })
        if let currentMonth, MonthName.sortKey(currentMonth) > 0 { values.insert(currentMonth) }
        if !bodyMonth.isEmpty, MonthName.sortKey(bodyMonth) > 0 { values.insert(bodyMonth) }
        return values.sorted { MonthName.sortKey($0) > MonthName.sortKey($1) }
    }
    private func shownSignatureDate(_ value: String) -> String {
        let fallback = currentMonth.flatMap { try? TransferLetterPeriod($0).signatureDate } ?? ""
        guard !fallback.isEmpty || !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return tr("شهر البيانات", "Data month") }
        return (try? TransferLetterCustomValues.signatureDate(value, fallback: fallback, eastern: true)) ?? tr("تاريخ غير صالح", "Invalid date")
    }
    private var signatureOffsetLabel: String {
        if signatureOffset == 0 { return tr("الموضع الافتراضي", "Default position") }
        let distance = Num.format(abs(signatureOffset))
        return signatureOffset < 0 ? tr("أعلى \(distance) نقطة", "Up \(distance) points") : tr("أسفل \(distance) نقطة", "Down \(distance) points")
    }
    private var busy: Bool { importing || loadingAccount || BackupManager.shared.isWorking || ImportCenter.shared.isBusy || StaffImportState.shared.isImporting }
    private func field(_ key: String, _ title: String, text: Binding<String>, lines: Bool = false) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Toggle(title, isOn: Binding(get: { selected.contains(key) }, set: { if $0 { selected.insert(key) } else { selected.remove(key) } }))
            if selected.contains(key) {
                TextField(title, text: text, axis: lines ? .vertical : .horizontal)
                    .lineLimit(lines ? (key == "body" ? 5...14 : 1...5) : 1...1)
                    .textFieldStyle(.roundedBorder)
                if mixed.contains(key) {
                    Text(tr("تختلف القيمة بين الكتب؛ اختر كتاباً لرؤية قيمته، أو أدخل قيمة موحدة للجميع.", "Values differ between letters; choose a letter to view its value, or enter one value for all."))
                        .font(.footnote).foregroundStyle(.secondary)
                }
            }
        }.padding(.vertical, 3)
    }
    @ViewBuilder private var datesAndBodyMonth: some View {
                field("firstDate", tr("تاريخ العضو الأول", "First member date"), text: $firstDate)
                field("secondDate", tr("تاريخ العضو الثاني", "Second member date"), text: $secondDate)
                field("thirdDate", tr("تاريخ العضو الثالث", "Third member date"), text: $thirdDate)
                if selected.contains("firstDate") { LabeledContent(tr("تاريخ العضو الأول المعتمد", "Effective first date"), value: shownSignatureDate(firstDate)) }
                if selected.contains("secondDate") { LabeledContent(tr("تاريخ العضو الثاني المعتمد", "Effective second date"), value: shownSignatureDate(secondDate)) }
                if selected.contains("thirdDate") { LabeledContent(tr("تاريخ العضو الثالث المعتمد", "Effective third date"), value: shownSignatureDate(thirdDate)) }
                if selected.contains("firstDate") || selected.contains("secondDate") || selected.contains("thirdDate") {
                    Text(tr("أدخل يوم/شهر/سنة، أو /شهر/سنة لترك اليوم فارغاً. الحقل الفارغ يستخدم شهر البيانات؛ التاريخ المخصص يبقى ثابتاً حتى تغييره.", "Enter day/month/year, or /month/year to leave the day blank. An empty field uses the letter's data month."))
                        .font(.footnote).foregroundStyle(.secondary)
                }
                VStack(alignment: .leading, spacing: 8) {
                    Toggle(tr("الشهر في متن الكتاب", "Month in letter body"), isOn: Binding(get: { selected.contains("bodyMonth") }, set: { if $0 { selected.insert("bodyMonth") } else { selected.remove("bodyMonth") } }))
                    if selected.contains("bodyMonth") {
                        Picker(tr("الشهر المكتوب", "Displayed month"), selection: $bodyMonth) {
                            Text(tr("تلقائي: شهر البيانات", "Automatic: data month")).tag("")
                            ForEach(availableMonths, id: \.self) { value in Text(value).tag(value) }
                            Text(tr("شهر آخر", "Another month")).tag("__manual__")
                        }
                        if bodyMonth == "__manual__" {
                            TextField(tr("مثال: تشرين الأول ٢٠٢٦", "Example: October 2026"), text: $manualBodyMonth)
                                .textFieldStyle(.roundedBorder)
                        }
                        Text(tr("يتغير الشهر في المتن فقط؛ المبالغ والقوائم وملف التحويل تبقى لشهر البيانات المختار. يبقى هذا الاختيار محفوظاً حتى تغييره.", "Only the body month changes; amounts, lists, and transfer files remain tied to the selected data month. This choice stays saved until changed."))
                            .font(.footnote).foregroundStyle(.orange)
                        if mixed.contains("bodyMonth") { Text(tr("تختلف إعدادات الشهر بين الكتب؛ اختيارك سيطبق على الجميع.", "Body month settings differ; your choice will apply to all letters.")).font(.footnote).foregroundStyle(.secondary) }
                    }
                }.padding(.vertical, 3)
    }
    @ViewBuilder private var beneficiaryField: some View {
                if target != "all" && supportsBeneficiary {
                    if let originalAccount, referenceBeneficiary != nil, beneficiarySource != nil {
                        field("beneficiary", tr("حساب المستفيد في الكتاب", "Beneficiary account in letter"), text: $beneficiary)
                        if selected.contains("beneficiary") {
                            Text(tr("التغيير لهذا الكتاب فقط. عند وجود ملف تحويل، حدّث قائمة IBAN ليطابقه، وراجع اسم المصرف والفرع. أدخل حساباً من المصرف نفسه، أو اترك الحقل فارغاً للعودة للحساب الأصلي.", "This changes this letter only. If there is a transfer file, update the IBAN list to match and review the bank and branch. Enter an account at the same bank, or leave blank to use the original."))
                                .font(.footnote).foregroundStyle(.orange)
                            LabeledContent(tr("الحساب الأصلي", "Original account"), value: originalAccount).privacySensitive()
                        }
                    } else if loadingAccount { ProgressView(tr("قراءة حساب المستفيد", "Reading beneficiary account")) }
                    else { Text(accountSourceError ?? tr("اختر جهة الاستقطاع أولاً لعرض حساب الكتاب.", "Choose the deduction entity first to show its account."))
                            .font(.footnote).foregroundStyle(.secondary) }
                }
    }
    var body: some View {
        Form {
            Section {
                Button(tr("استيراد القيم من النماذج", "Import values from letter templates"), systemImage: "square.and.arrow.down") { importDefaults() }
                    .disabled(busy)
                if importing { ProgressView() }
            } footer: { Text(tr("يملأ العدد والتواقيع والهامش من ملف الإعدادات الخاص، مع الاحتفاظ بتخصيصاتك السابقة. راجع العدد وأسماء اللجنة بعد الاستيراد.", "Fills the reference number, signatures and footer from the private settings file while preserving existing customizations. Review the reference number and committee names after import.")) }
            Section {
                if let bookID {
                    Picker(tr("تطبيق التغييرات على", "Apply changes to"), selection: $target) {
                        Text(tr("هذا الكتاب", "This letter")).tag(bookID)
                        Text(tr("جميع الكتب", "All letters")).tag("all")
                    }
                    if let title = titles.first(where: { $0.id == bookID })?.title {
                        LabeledContent(tr("الكتاب الحالي", "Current letter"), value: title)
                    }
                } else {
                    LabeledContent(tr("تطبيق التغييرات على", "Apply changes to"), value: tr("جميع الكتب", "All letters"))
                }
            } header: { Text(tr("نطاق التخصيص", "Customization scope")) } footer: { Text(tr("اختر الحقول التي تريد تعديلها فقط؛ تطبيق حقل على جميع الكتب يستبدل تخصيصه السابق في كل كتاب.", "Select only the fields to change. Applying a field to all letters replaces its previous individual overrides.")) }
            Section {
                field("header", tr("الترويسة", "Header"), text: $header, lines: true)
                field("first", tr("عضو اللجنة الأول", "First committee member"), text: $first)
                field("second", tr("عضو اللجنة الثاني", "Second committee member"), text: $second)
                field("third", tr("عضو اللجنة الثالث", "Third committee member"), text: $third)
                datesAndBodyMonth
                VStack(alignment: .leading, spacing: 10) {
                    Toggle(tr("موضع تواقيع اللجنة", "Committee signature position"), isOn: Binding(get: { selected.contains("signatureOffset") }, set: { if $0 { selected.insert("signatureOffset") } else { selected.remove("signatureOffset") } }))
                    if selected.contains("signatureOffset") {
                        HStack {
                            Button(tr("رفع", "Move up"), systemImage: "arrow.up") { signatureOffset = max(-60, signatureOffset - 5) }
                                .disabled(signatureOffset <= -60)
                            Spacer()
                            Text(signatureOffsetLabel)
                                .font(.footnote).foregroundStyle(.secondary)
                            Spacer()
                            Button(tr("خفض", "Move down"), systemImage: "arrow.down") { signatureOffset = min(60, signatureOffset + 5) }
                                .disabled(signatureOffset >= 60)
                        }.buttonStyle(.borderless)
                        if mixed.contains("signatureOffset") {
                            Text(tr("تختلف مواضع اللجنة بين الكتب؛ اختر موضعاً موحداً أو عدّل الكتاب الحالي.", "Committee positions differ between letters; choose a shared position or edit the current letter."))
                                .font(.footnote).foregroundStyle(.secondary)
                        }
                        Text(tr("تتحرك التواقيع الثلاثة معاً. راجع المعاينة بعد الحفظ.", "All three signatures move together. Review the preview after saving."))
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                }.padding(.vertical, 3)
                field("footer", tr("الهامش السفلي", "Footer"), text: $footer, lines: true)
                if target != "all" {
                    field("body", tr("متن الكتاب", "Letter body"), text: $bodyTemplate, lines: true)
                    if selected.contains("body") {
                        Text(tr("عدّل الصياغة واترك الحقول بين {{ }} كما هي؛ يملؤها التطبيق من بيانات الشهر تلقائياً.", "Edit the wording and keep the fields in {{ }} unchanged; the app fills them from the selected month."))
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                }
                beneficiaryField
                if target == "all" {
                    field("fixedNumber", tr("العدد الموحد", "Fixed letter number"), text: $fixedNumber)
                    field("foodContractReference", tr("رقم وتاريخ عقد الإطعام", "Food contract number and date"), text: $foodContractReference)
                }
                Button(tr("حفظ التخصيص", "Save customization"), systemImage: "checkmark.circle") { save() }
                    .disabled(selected.isEmpty || busy)
                if target != "all" {
                    Button(tr("إزالة تخصيص هذا الكتاب", "Remove this letter's overrides"), role: .destructive) { removeIndividual() }
                        .disabled(busy)
                }
            } header: { Text(tr("الحقول", "Fields")) } footer: { Text(tr("لتعديل متن كتاب محدد، افتح التخصيص من داخل الكتاب. تظهر القيم المشتركة عندما تتطابق في جميع الكتب.", "To edit a letter body, open settings from that letter. Shared values appear when they match across all letters.")) }
            Section {
                Button { importFont(.bold) } label: {
                    Label(boldData.isEmpty ? tr("استيراد الخط العريض", "Import bold font") : tr("استبدال الخط العريض", "Replace bold font"), systemImage: "textformat")
                }.disabled(busy)
                Button { importFont(.regular) } label: {
                    Label(regularData.isEmpty ? tr("استيراد الخط العادي", "Import regular font") : tr("استبدال الخط العادي", "Replace regular font"), systemImage: "textformat")
                }.disabled(busy)
                if importing { ProgressView() }
            } header: { Text(tr("خط الترويسة الخاص", "Private header font")) } footer: { Text(tr("يُحفظ الخط ضمن نسختك الاحتياطية، وقد يُضمّن العريض في ملف Word إذا سمح الخط بذلك.", "The font is kept in your backup, and the bold font may be embedded in the Word file when permitted.")) }
        }
        .navigationTitle(tr("تخصيص الكتب", "Customize letters"))
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { loadTarget() }
        .task(id: "\(referenceData.hashValue)|\(payerIBAN)") { await loadReferenceAccount() }
        .onChange(of: target) { loadTarget() }
        .alert(tr("تعذّر الحفظ", "Couldn't save"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
        .alert(tr("حُفظ التخصيص", "Customization saved"), isPresented: $saved) { Button(tr("حسناً", "OK"), role: .cancel) {} }
    }
    private func readStore() throws -> TransferLetterCustomizationStore {
        if storedData.isEmpty { return TransferLetterCustomizationStore() }
        return try JSONDecoder().decode(TransferLetterCustomizationStore.self, from: storedData)
    }
    private func loadTarget() {
        selected.removeAll()
        manualBodyMonth = ""
        do {
            let store = try readStore()
            mixed.removeAll()
            func value(_ key: String, _ path: KeyPath<TransferLetterCustomization, String?>) -> String {
                if target != "all" { return store.effective(for: target)[keyPath: path] ?? "" }
                let values = titles.map { store.effective(for: $0.id)[keyPath: path] }
                guard let first = values.first, values.allSatisfy({ $0 == first }) else {
                    mixed.insert(key); return ""
                }
                return first ?? ""
            }
            header = value("header", \.header); first = value("first", \.committeeFirst)
            second = value("second", \.committeeSecond); third = value("third", \.committeeThird)
            footer = value("footer", \.footer); fixedNumber = store.common.fixedNumber ?? ""
            foodContractReference = store.common.foodContractReference ?? ""
            bodyTemplate = target == "all" ? "" : (store.effective(for: target).bodyTemplate ?? TransferLetterBody.template(for: target) ?? "")
            firstDate = value("firstDate", \.committeeDateFirst); secondDate = value("secondDate", \.committeeDateSecond)
            thirdDate = value("thirdDate", \.committeeDateThird); bodyMonth = value("bodyMonth", \.bodyMonth)
            let override = target == "all" ? nil : store.effective(for: target).beneficiaryAccount
            beneficiary = (override?.isEmpty == false ? override : nil) ?? originalAccount ?? ""
            let offsets = target == "all" ? titles.map { store.effective(for: $0.id).signatureOffset ?? 0 } : [store.effective(for: target).signatureOffset ?? 0]
            if let firstOffset = offsets.first, offsets.allSatisfy({ $0 == firstOffset }) {
                guard (-60...60).contains(firstOffset) else { throw TransferWordGroupDraft.Failure.incomplete }
                signatureOffset = firstOffset
            } else { signatureOffset = 0; mixed.insert("signatureOffset") }
        } catch {
            header = ""; first = ""; second = ""; third = ""; footer = ""; fixedNumber = ""; foodContractReference = ""; bodyTemplate = ""; signatureOffset = 0; bodyMonth = ""; manualBodyMonth = ""; firstDate = ""; secondDate = ""; thirdDate = ""; beneficiary = ""; mixed.removeAll()
            self.error = tr("تعذّر قراءة التخصيصات المحفوظة؛ لم يتم استبدالها.", "Could not read saved customizations; they were not replaced.")
        }
    }
    private func save() {
        guard !busy, !selected.isEmpty else { return }
        let pairs = [("header", header), ("first", first), ("second", second), ("third", third), ("footer", footer),
                     ("fixedNumber", fixedNumber), ("foodContractReference", foodContractReference), ("firstDate", firstDate), ("secondDate", secondDate), ("thirdDate", thirdDate), ("beneficiary", beneficiary)]
        guard pairs.filter({ selected.contains($0.0) && mixed.contains($0.0) && !["firstDate", "secondDate", "thirdDate"].contains($0.0) }).allSatisfy({ !$0.1.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else {
            error = tr("أدخل قيمة موحدة للحقل الذي يختلف بين الكتب، أو اختر كتاباً محدداً.", "Enter a value for the field that differs between letters, or choose a specific letter."); return
        }
        guard pairs.filter({ selected.contains($0.0) }).allSatisfy({ pair in
            let limit = pair.0 == "footer" ? 4000 : pair.0 == "header" ? 2000 : 200
            return pair.1.count <= limit && !pair.1.unicodeScalars.contains(where: { $0.value < 32 && $0.value != 10 })
        }) else { error = tr("أحد الحقول أطول من الحد المسموح أو يحتوي رمزاً غير صالح.", "A field is too long or contains an invalid character."); return }
        let chosenBodyMonth = bodyMonth == "__manual__" ? manualBodyMonth.trimmingCharacters(in: .whitespacesAndNewlines) : bodyMonth
        do {
            for (key, value) in [("firstDate", firstDate), ("secondDate", secondDate), ("thirdDate", thirdDate)] where selected.contains(key) {
                _ = try TransferLetterCustomValues.signatureDate(value, fallback: " / 1 / 2026", eastern: false)
            }
            if selected.contains("bodyMonth") {
                if bodyMonth == "__manual__" && chosenBodyMonth.isEmpty { throw TransferLetterCustomValues.Failure.month }
                if !chosenBodyMonth.isEmpty { _ = try TransferLetterCustomValues.bodyMonth(chosenBodyMonth, source: chosenBodyMonth) }
            }
            if selected.contains("beneficiary"), !beneficiary.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                guard target == bookID, let id = bookID, let source = beneficiarySource, let original = originalAccount, let reference = referenceBeneficiary else { throw TransferLetterCustomValues.Failure.account }
                guard accountReferenceSnapshot == referenceData, accountPayerSnapshot == payerIBAN else { throw TransferLetterCustomValues.Failure.sourceChanged }
                if isEntityAccount {
                    guard let key = entityAccountKey, let saved = try ExecutionAccountStore.decode(accountData).first(where: { $0.key == key }) else { throw TransferLetterCustomValues.Failure.sourceChanged }
                    let now = id == "ramadi" ? saved.iban : (TransferRules.individualLocalAccount(saved) ?? "")
                    guard TransferLetterCustomValues.normalizedAccount(now) == TransferLetterCustomValues.normalizedAccount(original) else { throw TransferLetterCustomValues.Failure.sourceChanged }
                }
                let probe = TransferLetterCustomization(beneficiaryAccount: beneficiary, beneficiarySource: source,
                    beneficiaryOriginal: TransferLetterCustomValues.normalizedAccount(original), beneficiaryReference: TransferLetterCustomValues.normalizedAccount(reference))
                _ = try TransferLetterCustomValues.account(probe, bookID: id, source: source, original: original, reference: reference)
                if TransferLetterCustomValues.normalizedAccount(beneficiary) == TransferLetterCustomValues.normalizedAccount(payerIBAN) { throw TransferLetterCustomValues.Failure.account }
            }
        } catch { self.error = error.localizedDescription; return }
        if selected.contains("body") {
            do { try TransferLetterBody.validate(bodyTemplate, for: target) }
            catch { self.error = error.localizedDescription; return }
        }
        var changes = TransferLetterCustomization()
        if selected.contains("header") { changes.header = header }
        if selected.contains("first") { changes.committeeFirst = first }
        if selected.contains("second") { changes.committeeSecond = second }
        if selected.contains("third") { changes.committeeThird = third }
        if selected.contains("footer") { changes.footer = footer }
        if selected.contains("fixedNumber") { changes.fixedNumber = fixedNumber }
        if selected.contains("foodContractReference") { changes.foodContractReference = foodContractReference }
        if selected.contains("body") { changes.bodyTemplate = bodyTemplate }
        if selected.contains("signatureOffset") { changes.signatureOffset = signatureOffset }
        if selected.contains("bodyMonth") { changes.bodyMonth = chosenBodyMonth }
        if selected.contains("firstDate") { changes.committeeDateFirst = firstDate }
        if selected.contains("secondDate") { changes.committeeDateSecond = secondDate }
        if selected.contains("thirdDate") { changes.committeeDateThird = thirdDate }
        if selected.contains("beneficiary") {
            changes.beneficiaryAccount = beneficiary
            changes.beneficiarySource = beneficiarySource ?? ""
            changes.beneficiaryOriginal = TransferLetterCustomValues.normalizedAccount(originalAccount ?? "")
            changes.beneficiaryReference = TransferLetterCustomValues.normalizedAccount(referenceBeneficiary ?? "")
        }
        do {
            var store = try readStore()
            if target == "all" { store.applyToAll(changes) } else { store.apply(changes, to: target) }
            storedData = try JSONEncoder().encode(store)
            BackupManager.shared.markChanged(); loadTarget(); saved = true
        } catch { self.error = tr("تعذّر قراءة التخصيصات المحفوظة؛ لم يتم استبدالها.", "Could not read saved customizations; they were not replaced.") }
    }
    private func removeIndividual() {
        guard !busy, target != "all" else { return }
        do {
            var store = try readStore(); store.individual.removeValue(forKey: target)
            storedData = try JSONEncoder().encode(store); BackupManager.shared.markChanged(); loadTarget(); saved = true
        } catch { self.error = tr("تعذّر قراءة التخصيصات المحفوظة؛ لم يتم استبدالها.", "Could not read saved customizations; they were not replaced.") }
    }
    private func loadReferenceAccount() async {
        guard supportsBeneficiary, let bookID else { return }
        let references = referenceData, payer = payerIBAN
        guard !references.isEmpty, TransferRules.validIBAN(payer) else { referenceBeneficiary = nil; accountReferenceSnapshot = Data(); accountPayerSnapshot = ""; return }
        referenceBeneficiary = nil; accountReferenceSnapshot = Data(); accountPayerSnapshot = ""
        loadingAccount = true; accountSourceError = nil
        defer { loadingAccount = false }
        do {
            let task = Task.detached(priority: .userInitiated) {
                if ["ramadi", "housing", "water"].contains(bookID) {
                    guard let value = try TransferWordReferenceAccounts.individualBeneficiary(bookID: bookID, references: references, payerIBAN: payer) else { throw TransferLetterCustomValues.Failure.account }
                    return value
                }
                return try TransferWordReferenceAccounts.beneficiary(bookID: bookID, references: references, payerIBAN: payer)
            }
            let value = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            guard !Task.isCancelled, referenceData == references, payerIBAN == payer else { return }
            referenceBeneficiary = value; accountReferenceSnapshot = references; accountPayerSnapshot = payer
            if !isEntityAccount, !selected.contains("beneficiary"), target == bookID {
                let stored = try readStore().effective(for: bookID).beneficiaryAccount
                if stored?.isEmpty != false { beneficiary = value }
            }
        } catch is CancellationError { return }
        catch { guard !Task.isCancelled else { return }; referenceBeneficiary = nil; accountSourceError = tr("تعذّرت قراءة الحساب الأصلي من المرجع؛ راجع مراجع الكتاب وحساب الدفع.", "Could not read the original account from the reference; review the reference and payer account.") }
    }
    private func importDefaults() {
        guard !busy else { return }
        guard !referenceData.isEmpty else {
            error = tr("استورد مراجع الكتب أولاً من صفحة ملفات التحويل.", "Import the letter references from transfer files first.")
            return
        }
        let referenceSnapshot = referenceData
        FilePicker.present(types: [.json], multiple: false) { urls in
            guard let url = urls.first else { return }
            importing = true; error = nil
            Task {
                defer { importing = false }
                do {
                    let imported = try await Task.detached(priority: .userInitiated) {
                        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                        guard size > 0, size <= 1_000_000 else { throw TransferWordDefaults.Failure.invalid }
                        let package = try TransferWordDefaults.decode(Data(contentsOf: url))
                        try TransferWordDefaults.verifyReferences(package, data: referenceSnapshot)
                        return package
                    }.value
                    await FilePicker.waitUntilSettled()
                    guard referenceData == referenceSnapshot else {
                        throw NSError(domain: "WordDefaults", code: 2, userInfo: [NSLocalizedDescriptionKey: tr("تغيّرت مراجع الكتب؛ أعد اختيار ملف الإعدادات.", "The letter references changed; select the settings file again.")])
                    }
                    guard !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
                        throw NSError(domain: "WordDefaults", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("انتظر انتهاء العملية الحالية ثم أعد الاستيراد.", "Wait for the current operation and import again.")])
                    }
                    var merged = TransferWordDefaults.merging(imported.defaults, preserving: try readStore())
                    merged.wordReferenceHashes = imported.referenceHashes
                    storedData = try JSONEncoder().encode(merged)
                    BackupManager.shared.markChanged(); loadTarget(); saved = true
                } catch { self.error = error.localizedDescription }
            }
        }
    }
    private func importFont(_ style: TransferHeaderFont.Style) {
        guard !busy else { return }
        FilePicker.present(types: [UTType(filenameExtension: "ttf") ?? .data], multiple: false) { urls in
            guard let url = urls.first else { return }
            importing = true
            Task {
                defer { importing = false }
                do {
                    let bytes = try await Task.detached(priority: .userInitiated) {
                        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                        guard size > 0, size <= 3_000_000 else { throw TransferHeaderFont.Failure.invalidFont }
                        let data = try Data(contentsOf: url)
                        _ = try TransferHeaderFont.make(data: data, style: style, size: 18)
                        return data
                    }.value
                    guard !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
                        throw NSError(domain: "TransferHeaderFont", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("انتظر انتهاء العملية الحالية ثم أعد اختيار الخط.", "Wait for the current operation, then select the font again.")])
                    }
                    if style == .bold { boldData = bytes } else { regularData = bytes }
                    BackupManager.shared.markChanged()
                } catch { self.error = error.localizedDescription }
            }
        }
    }
}
