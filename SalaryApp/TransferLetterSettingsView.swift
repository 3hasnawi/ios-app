import SwiftUI
import UniformTypeIdentifiers

/// حفظ الخيارات فقط؛ يجب ربطها بمواضع كل قالب قبل تفعيلها في الكتب.
struct TransferLetterSettingsView: View {
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var storedData = Data()
    @AppStorage(TransferHeaderFont.boldSettingsKey) private var boldData = Data()
    @AppStorage(TransferHeaderFont.regularSettingsKey) private var regularData = Data()
    @State private var target = "all"
    @State private var selected = Set<String>()
    @State private var header = ""
    @State private var first = ""
    @State private var second = ""
    @State private var third = ""
    @State private var footer = ""
    @State private var fixedNumber = ""
    @State private var foodContractReference = ""
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
    private var busy: Bool { importing || BackupManager.shared.isWorking || ImportCenter.shared.isBusy || StaffImportState.shared.isImporting }
    private func field(_ key: String, _ title: String, text: Binding<String>, lines: Bool = false) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Toggle(title, isOn: Binding(get: { selected.contains(key) }, set: { if $0 { selected.insert(key) } else { selected.remove(key) } }))
            if selected.contains(key) {
                TextField(title, text: text, axis: lines ? .vertical : .horizontal)
                    .lineLimit(lines ? 1...5 : 1...1)
                    .textFieldStyle(.roundedBorder)
            }
        }.padding(.vertical, 3)
    }
    var body: some View {
        Form {
            Section {
                Button(tr("استيراد إعدادات النماذج", "Import letter settings"), systemImage: "square.and.arrow.down") { importDefaults() }
                    .disabled(busy)
                if importing { ProgressView() }
            } footer: { Text(tr("يملأ العدد والتواقيع والهامش من ملف الإعدادات الخاص، مع الاحتفاظ بتخصيصاتك السابقة. راجع العدد وأسماء اللجنة بعد الاستيراد.", "Fills the reference number, signatures and footer from the private settings file while preserving existing customizations. Review the reference number and committee names after import.")) }
            Section {
                Picker(tr("تطبيق التغييرات على", "Apply changes to"), selection: $target) {
                    Text(tr("جميع الكتب", "All letters")).tag("all")
                    ForEach(titles) { item in Text(item.title).tag(item.id) }
                }
            } header: { Text(tr("نطاق التخصيص", "Customization scope")) } footer: { Text(tr("اختر الحقول التي تريد تعديلها فقط؛ تطبيق حقل على جميع الكتب يستبدل تخصيصه السابق في كل كتاب.", "Select only the fields to change. Applying a field to all letters replaces its previous individual overrides.")) }
            Section {
                field("header", tr("الترويسة", "Header"), text: $header, lines: true)
                field("first", tr("عضو اللجنة الأول", "First committee member"), text: $first)
                field("second", tr("عضو اللجنة الثاني", "Second committee member"), text: $second)
                field("third", tr("عضو اللجنة الثالث", "Third committee member"), text: $third)
                field("footer", tr("الهامش السفلي", "Footer"), text: $footer, lines: true)
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
            } header: { Text(tr("الحقول", "Fields")) } footer: { Text(tr("تُحفظ الخيارات، وما زال تطبيقها على ملفات الكتب قيد الإكمال.", "Options are saved; applying them to letter PDFs is still in progress.")) }
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
            let value = target == "all" ? store.common : store.effective(for: target)
            header = value.header ?? ""; first = value.committeeFirst ?? ""; second = value.committeeSecond ?? ""
            third = value.committeeThird ?? ""; footer = value.footer ?? ""
            fixedNumber = value.fixedNumber ?? ""; foodContractReference = value.foodContractReference ?? ""
        } catch {
            header = ""; first = ""; second = ""; third = ""; footer = ""; fixedNumber = ""; foodContractReference = ""
            self.error = tr("تعذّر قراءة التخصيصات المحفوظة؛ لم يتم استبدالها.", "Could not read saved customizations; they were not replaced.")
        }
    }
    private func save() {
        guard !busy, !selected.isEmpty else { return }
        let pairs = [("header", header), ("first", first), ("second", second), ("third", third), ("footer", footer),
                     ("fixedNumber", fixedNumber), ("foodContractReference", foodContractReference)]
        guard pairs.filter({ selected.contains($0.0) }).allSatisfy({ pair in
            let limit = pair.0 == "footer" ? 4000 : pair.0 == "header" ? 2000 : 200
            return pair.1.count <= limit && !pair.1.unicodeScalars.contains(where: { $0.value < 32 && $0.value != 10 })
        }) else { error = tr("أحد الحقول أطول من الحد المسموح أو يحتوي رمزاً غير صالح.", "A field is too long or contains an invalid character."); return }
        let changes = TransferLetterCustomization(header: selected.contains("header") ? header : nil,
            committeeFirst: selected.contains("first") ? first : nil, committeeSecond: selected.contains("second") ? second : nil,
            committeeThird: selected.contains("third") ? third : nil, footer: selected.contains("footer") ? footer : nil,
            fixedNumber: selected.contains("fixedNumber") ? fixedNumber : nil,
            foodContractReference: selected.contains("foodContractReference") ? foodContractReference : nil)
        do {
            var store = try readStore()
            if target == "all" { store.applyToAll(changes) } else { store.apply(changes, to: target) }
            storedData = try JSONEncoder().encode(store)
            BackupManager.shared.markChanged(); selected.removeAll(); saved = true
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
        FilePicker.present(types: [.json], multiple: false) { urls in
            guard let url = urls.first else { return }
            importing = true; error = nil
            Task {
                defer { importing = false }
                do {
                    let imported = try await Task.detached(priority: .userInitiated) {
                        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                        guard size > 0, size <= 1_000_000 else { throw TransferWordDefaults.Failure.invalid }
                        return try TransferWordDefaults.decode(Data(contentsOf: url))
                    }.value
                    await FilePicker.waitUntilSettled()
                    guard !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
                        throw NSError(domain: "WordDefaults", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("انتظر انتهاء العملية الحالية ثم أعد الاستيراد.", "Wait for the current operation and import again.")])
                    }
                    let merged = TransferWordDefaults.merging(imported, preserving: try readStore())
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
