import SwiftUI
import UniformTypeIdentifiers

/// حفظ الخيارات فقط؛ يجب ربطها بمواضع كل قالب قبل تفعيلها في الكتب.
struct TransferLetterSettingsView: View {
    let bookID: String?
    init(bookID: String? = nil) {
        self.bookID = bookID
        _target = State(initialValue: bookID ?? "all")
    }
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var storedData = Data()
    @AppStorage(TransferHeaderFont.boldSettingsKey) private var boldData = Data()
    @AppStorage(TransferHeaderFont.regularSettingsKey) private var regularData = Data()
    @AppStorage(TransferReferencePack.settingsKey) private var referenceData = Data()
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
    private var signatureOffsetLabel: String {
        if signatureOffset == 0 { return tr("الموضع الافتراضي", "Default position") }
        let distance = Num.format(abs(signatureOffset))
        return signatureOffset < 0 ? tr("أعلى \(distance) نقطة", "Up \(distance) points") : tr("أسفل \(distance) نقطة", "Down \(distance) points")
    }
    private var busy: Bool { importing || BackupManager.shared.isWorking || ImportCenter.shared.isBusy || StaffImportState.shared.isImporting }
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
            let offsets = target == "all" ? titles.map { store.effective(for: $0.id).signatureOffset ?? 0 } : [store.effective(for: target).signatureOffset ?? 0]
            if let firstOffset = offsets.first, offsets.allSatisfy({ $0 == firstOffset }) {
                guard (-60...60).contains(firstOffset) else { throw TransferWordGroupDraft.Failure.incomplete }
                signatureOffset = firstOffset
            } else { signatureOffset = 0; mixed.insert("signatureOffset") }
        } catch {
            header = ""; first = ""; second = ""; third = ""; footer = ""; fixedNumber = ""; foodContractReference = ""; bodyTemplate = ""; signatureOffset = 0; mixed.removeAll()
            self.error = tr("تعذّر قراءة التخصيصات المحفوظة؛ لم يتم استبدالها.", "Could not read saved customizations; they were not replaced.")
        }
    }
    private func save() {
        guard !busy, !selected.isEmpty else { return }
        let pairs = [("header", header), ("first", first), ("second", second), ("third", third), ("footer", footer),
                     ("fixedNumber", fixedNumber), ("foodContractReference", foodContractReference)]
        guard pairs.filter({ selected.contains($0.0) && mixed.contains($0.0) }).allSatisfy({ !$0.1.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else {
            error = tr("أدخل قيمة موحدة للحقل الذي يختلف بين الكتب، أو اختر كتاباً محدداً.", "Enter a value for the field that differs between letters, or choose a specific letter."); return
        }
        guard pairs.filter({ selected.contains($0.0) }).allSatisfy({ pair in
            let limit = pair.0 == "footer" ? 4000 : pair.0 == "header" ? 2000 : 200
            return pair.1.count <= limit && !pair.1.unicodeScalars.contains(where: { $0.value < 32 && $0.value != 10 })
        }) else { error = tr("أحد الحقول أطول من الحد المسموح أو يحتوي رمزاً غير صالح.", "A field is too long or contains an invalid character."); return }
        if selected.contains("body") {
            do { try TransferLetterBody.validate(bodyTemplate, for: target) }
            catch { self.error = error.localizedDescription; return }
        }
        let changes = TransferLetterCustomization(header: selected.contains("header") ? header : nil,
            committeeFirst: selected.contains("first") ? first : nil, committeeSecond: selected.contains("second") ? second : nil,
            committeeThird: selected.contains("third") ? third : nil, footer: selected.contains("footer") ? footer : nil,
            fixedNumber: selected.contains("fixedNumber") ? fixedNumber : nil,
            foodContractReference: selected.contains("foodContractReference") ? foodContractReference : nil,
            bodyTemplate: selected.contains("body") ? bodyTemplate : nil,
            signatureOffset: selected.contains("signatureOffset") ? signatureOffset : nil)
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
