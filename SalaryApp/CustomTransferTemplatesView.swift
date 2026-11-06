import SwiftUI
import SwiftData
import PDFKit

struct CustomTransferTemplatesView: View {
    let monthName: String
    @AppStorage(CustomTransferTemplate.settingsKey) private var savedData = Data()
    @State private var editing: CustomTransferTemplate?
    @State private var editSnapshot = Data()

    private var templates: [CustomTransferTemplate]? { try? CustomTransferTemplate.decode(savedData) }
    var body: some View {
        List {
            Section {
                Button { editSnapshot = savedData; editing = CustomTransferTemplate() } label: {
                    Label(tr("إضافة قالب كتاب", "Add letter template"), systemImage: "doc.badge.plus")
                }.disabled(templates == nil)
                if let templates {
                    ForEach(templates) { item in
                        NavigationLink {
                            CustomTransferLetterView(template: item, monthName: monthName)
                        } label: {
                            VStack(alignment: .leading) {
                                Text(item.title).font(.headline)
                                if !item.sourceName.isEmpty { Text(item.sourceName).font(.caption).foregroundStyle(.secondary) }
                            }
                        }
                        .contextMenu {
                            Button(tr("تعديل القالب", "Edit template"), systemImage: "pencil") {
                                editSnapshot = savedData; editing = item
                            }
                        }
                    }
                } else {
                    Text(tr("تعذّرت قراءة القوالب المحفوظة؛ لم تُغيّر بياناتك. استعد نسخة احتياطية سليمة.", "Saved templates could not be read. Your data was not changed; restore a valid backup."))
                        .foregroundStyle(.red)
                }
            } footer: { Text(tr("اضغط مطولاً على القالب لتعديله. تُحفظ القوالب ضمن النسخة الاحتياطية.", "Touch and hold a template to edit it. Templates are included in backups.")) }
        }
        .navigationTitle(tr("الكتب المخصصة", "Custom letters"))
        .navigationBarTitleDisplayMode(.inline)
        .sheet(item: $editing) { item in
            NavigationStack {
                CustomTransferTemplateEditor(template: item, monthName: monthName) { updated in
                    guard savedData == editSnapshot, !BackupManager.shared.isWorking,
                              !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
                            throw CustomTransferTemplate.invalid(tr("تغيّرت البيانات أو توجد عملية جارية؛ افتح القالب مجدداً بعد انتهائها.", "Data changed or an operation is in progress. Reopen the template afterwards."))
                        }
                        var all = try CustomTransferTemplate.decode(savedData)
                        if let index = all.firstIndex(where: { $0.id == updated.id }) { all[index] = updated }
                        else { all.append(updated) }
                        savedData = try CustomTransferTemplate.encode(all)
                        BackupManager.shared.markSettingChanged(CustomTransferTemplate.settingsKey)
                    editing = nil
                }
            }
        }
    }
}

struct CustomTransferTemplateEditor: View {
    @Environment(\.dismiss) private var dismiss
    @Query private var months: [PayrollMonth]
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()
    @State private var template: CustomTransferTemplate
    @State private var groups: [ReportGroup] = []
    @State private var sourceError: String?
    @State private var saveError: String?
    @State private var choosingAccount = false
    @State private var fontPart: CustomTransferTemplate.FontPart = .body
    let monthName: String
    let bookID: String?
    let canEditAccount: Bool
    let save: (CustomTransferTemplate) throws -> Void

    init(template: CustomTransferTemplate, monthName: String, bookID: String? = nil, canEditAccount: Bool = true, save: @escaping (CustomTransferTemplate) throws -> Void) {
        _template = State(initialValue: template); self.monthName = monthName; self.bookID = bookID; self.canEditAccount = canEditAccount; self.save = save
    }
    private var payrollNames: [String] {
        Array((PayrollItems.decodeSummary(months.first { $0.name == monthName }?.itemSummaryData) ?? [:]).keys).sorted()
    }
    private var accounts: [SavedExecutionAccount] { (try? ExecutionAccountStore.decode(accountData)) ?? [] }
    var body: some View {
        Form {
            Section(tr("بيانات القالب", "Template details")) {
                TextField(tr("اسم القالب", "Template name"), text: $template.title)
                TextField(tr("الترويسة", "Header"), text: $template.header, axis: .vertical)
                TextField(tr("العدد", "Reference number"), text: $template.number)
                TextField(tr("إلى", "To"), text: $template.recipient)
                TextField(tr("الموضوع", "Subject"), text: $template.subject)
                TextField(tr("المتن", "Body"), text: $template.body, axis: .vertical).lineLimit(4...12)
                Picker(tr("الكشيدة", "Kashida"), selection: Binding(get: { template.bodyKashida ?? .none }, set: { template.bodyKashida = $0; template.bodyAlignment = .justified })) {
                    Text(tr("ضبط", "Justify")).tag(CustomTransferTemplate.Kashida.none)
                    Text(tr("كشيدة صغيرة", "Small kashida")).tag(CustomTransferTemplate.Kashida.low)
                    Text(tr("كشيدة متوسطة", "Medium kashida")).tag(CustomTransferTemplate.Kashida.medium)
                    Text(tr("كشيدة كبيرة", "Large kashida")).tag(CustomTransferTemplate.Kashida.high)
                }
                if template.bodyKashida == nil && template.bodyAlignment != nil && template.bodyAlignment != .justified {
                    Text(tr("المحاذاة السابقة محفوظة. اختر درجة الكشيدة لتطبيق ضبط الطرفين.", "The previous alignment is preserved. Choose a kashida level to justify the body."))
                        .font(.footnote).foregroundStyle(.secondary)
                }
                Picker(tr("تباعد أسطر المتن", "Body line spacing"), selection: Binding(get: { template.bodySpacing ?? .medium }, set: { template.bodySpacing = $0 })) {
                    Text(tr("صغير", "Compact")).tag(CustomTransferTemplate.BodySpacing.compact)
                    Text(tr("متوسط", "Medium")).tag(CustomTransferTemplate.BodySpacing.medium)
                    Text(tr("كبير", "Large")).tag(CustomTransferTemplate.BodySpacing.wide)
                }
                Toggle(tr("متن عريض", "Bold body"), isOn: Binding(get: { template.bodyBold ?? false }, set: { template.bodyBold = $0 }))
                Text(tr("يُطبّق التنسيق على المتن كاملاً في معاينة الكتاب.", "Formatting applies to the entire body in the letter preview.")).font(.footnote).foregroundStyle(.secondary)
                if bookID != nil { Text(tr("احتفظ بالحقول المحاسبية بين {{ }} كما هي؛ يملؤها التطبيق تلقائياً من بيانات الكتاب.", "Keep the accounting fields in {{ }} intact; the app fills them from the letter data.")).font(.footnote).foregroundStyle(.secondary) }
                Menu(tr("إضافة حقل إلى المتن", "Insert field into body")) {
                    ForEach(bookID.map { TransferLetterBody.fieldNames(for: $0) } ?? ["المبلغ", "المبلغ كتابة", "الشهر", "السنة", "الحساب"], id: \.self) { key in
                        Button(key) { template.body += "{{\(key)}}" }
                    }
                }
            }
            Section(tr("حجم الخط", "Font size")) {
                Picker(tr("جزء الكتاب", "Letter part"), selection: $fontPart) {
                    ForEach(CustomTransferTemplate.FontPart.allCases) { part in Text(part.title).tag(part) }
                }
                Picker(tr("الحجم", "Size"), selection: Binding(get: { template.fontSizes?[fontPart.rawValue] ?? 0 }, set: { value in
                    var sizes = template.fontSizes ?? [:]
                    if value == 0 { sizes.removeValue(forKey: fontPart.rawValue) } else { sizes[fontPart.rawValue] = value }
                    template.fontSizes = sizes.isEmpty ? nil : sizes
                })) {
                    Text(tr("الافتراضي", "Default")).tag(0)
                    ForEach(8...24, id: \.self) { size in Text("\(size)").tag(size) }
                }
                Text(tr("اختر الجزء ثم حجم خطه. تُراجع النتيجة في المعاينة؛ قد يحتاج النص الطويل إلى حجم أصغر.", "Choose a part and its font size. Review the preview; long text may need a smaller size."))
                    .font(.footnote).foregroundStyle(.secondary)
            }
            if bookID == nil { Section(tr("مصدر المبلغ", "Amount source")) {
                Picker(tr("المصدر", "Source"), selection: $template.source) {
                    Text(tr("بدون ربط", "None")).tag(CustomTransferTemplate.Source.none)
                    Text(tr("مبلغ يدوي", "Manual amount")).tag(CustomTransferTemplate.Source.manual)
                    Text(tr("بند رواتب", "Payroll item")).tag(CustomTransferTemplate.Source.payroll)
                    Text(tr("جهة تنفيذ", "Execution entity")).tag(CustomTransferTemplate.Source.execution)
                }
                if template.source == .payroll || template.source == .execution {
                    let names = template.source == .payroll ? payrollNames : groups.map(\.title).sorted()
                    Picker(tr("البند", "Item"), selection: $template.sourceName) {
                        Text(tr("اختر البند", "Choose item")).tag("")
                        if !template.sourceName.isEmpty && !names.contains(template.sourceName) { Text(template.sourceName + " ⚠").tag(template.sourceName) }
                        ForEach(names, id: \.self) { Text($0).tag($0) }
                    }
                    if template.source == .payroll {
                        Text(tr("يُستخدم مجموع البند المستورد كاملاً، دون حساب نسبة خاصة تلقائياً.", "The full imported item total is used; special percentages are not applied automatically.")).font(.footnote).foregroundStyle(.secondary)
                    }
                    if let sourceError { Text(sourceError).foregroundStyle(.red) }
                }
            } } else { Section(tr("مصدر المبلغ", "Amount source")) { Text(tr("يُحسب المبلغ من بيانات الكتاب والشهر المختار تلقائياً.", "The amount is calculated from the letter data and selected month.")).foregroundStyle(.secondary) } }
            if let bookID, !bookID.hasPrefix("treasury_") {
                Section(tr("الشهر والتاريخ", "Month and date")) {
                    TextField(tr("شهر المتن، أو اتركه تلقائياً", "Body month, or leave automatic"), text: Binding(get: { template.bodyMonth ?? "" }, set: { template.bodyMonth = $0 }))
                    TextField(tr("رقم شهر الصادر، أو اتركه تلقائياً", "Outgoing month number, or leave automatic"), text: Binding(get: { template.outgoingMonth ?? "" }, set: { template.outgoingMonth = $0 })).keyboardType(.numberPad)
                    Text(tr("تغيير شهر المتن لا يغيّر شهر المبالغ أو ملف التحويل.", "Changing the body month does not change the amounts or transfer file month.")).font(.footnote).foregroundStyle(.secondary)
                }
            }
            if bookID == "food_company" || bookID == "food_tax" {
                Section(tr("عقد الإطعام", "Food contract")) {
                    TextField(tr("رقم العقد وتاريخه", "Contract number and date"), text: Binding(get: { template.foodContractReference ?? "" }, set: { template.foodContractReference = $0 }))
                    Text(tr("يُستخدم هذا المرجع لكتابي الشركة وضريبة عقد الإطعام.", "This reference is shared by the company and food contract tax letters.")).font(.footnote).foregroundStyle(.secondary)
                }
            }
            if bookID == nil || canEditAccount { Section(tr("الحساب المرتبط اختيارياً", "Optional linked account")) {
                TextField(tr("رقم الحساب أو IBAN", "Account number or IBAN"), text: $template.account).textInputAutocapitalization(.characters).autocorrectionDisabled()
                if !accounts.isEmpty {
                    Button(tr("اختيار من الحسابات المحفوظة", "Choose a saved account")) { choosingAccount = true }
                }
                Text(tr("تأكد من رقم الحساب عند كل إصدار للكتاب.", "Verify the account number for each letter.")).font(.footnote).foregroundStyle(.secondary)
            } } else { Section(tr("الحساب المرتبط", "Linked account")) {
                Text(tr("يُحدّد الحساب من بيانات الكتاب أو الجهة؛ لتعديله افتح القالب من الكتاب بعد اختيار الجهة وتوفّر المراجع.", "The account comes from the letter or beneficiary. To change it, open the template from the letter after selecting the beneficiary and loading references.")).font(.footnote).foregroundStyle(.secondary)
            } }
            Section(tr("المرفقات", "Attachments")) {
                TextField(tr("وصف المرفقات، مثل: قائمة أسماء عدد ١", "Attachment description, for example: one list of names"),
                          text: Binding(get: { template.attachments ?? "" }, set: { template.attachments = $0.isEmpty ? nil : $0 }), axis: .vertical).lineLimit(2...6)
                Text(tr("اختياري؛ يظهر الوصف أسفل عبارة للتفضل بالاطلاع وقبل التوقيع.", "Optional; the description appears after the closing and before the signatures.")).font(.footnote).foregroundStyle(.secondary)
            }
            Section(tr("التواقيع", "Signatures")) {
                Text(tr("العضو الأول", "First member")).font(.headline)
                TextField(tr("العنوان الوظيفي\nالاسم\nالمنصب", "Job title\nName\nPosition"), text: $template.firstSigner, axis: .vertical).lineLimit(3...5)
                Text(tr("العضو الثاني", "Second member")).font(.headline)
                TextField(tr("العنوان الوظيفي\nالاسم\nالمنصب", "Job title\nName\nPosition"), text: $template.secondSigner, axis: .vertical).lineLimit(3...5)
                Text(tr("العضو الثالث", "Third member")).font(.headline)
                TextField(tr("العنوان الوظيفي\nالاسم\nالمنصب", "Job title\nName\nPosition"), text: $template.thirdSigner, axis: .vertical).lineLimit(3...5)
                Text(tr("أدخل توقيعاً واحداً على الأقل واترك البقية فارغة عند عدم الحاجة. يظهر التوقيع المنفرد يسار الكتاب. اكتب العنوان الوظيفي ثم الاسم ثم المنصب، كل واحد في سطر؛ ويُضاف التاريخ تلقائياً. الأسماء المحفوظة سابقاً تبقى بصفاتها المعتمدة.", "Enter at least one signature; leave the others blank if not needed. A single signature appears on the left. Enter the job title, name and position on separate lines; the date is added automatically. Previously saved names retain their existing titles.")).font(.footnote).foregroundStyle(.secondary)
                Stepper(value: Binding(get: { template.signatureVerticalOffset ?? 0 }, set: { template.signatureVerticalOffset = $0 }), in: -60...60, step: 5) {
                    LabeledContent(tr("تحريك التواقيع عمودياً", "Move signatures vertically"), value: verticalOffsetLabel(template.signatureVerticalOffset ?? 0))
                }
                Stepper(value: Binding(get: { template.signatureHorizontalOffset ?? 0 }, set: { template.signatureHorizontalOffset = $0 }), in: -30...30, step: 5) {
                    LabeledContent(tr("تحريك التواقيع أفقياً", "Move signatures horizontally"), value: horizontalOffsetLabel(template.signatureHorizontalOffset ?? 0))
                }
                Button(tr("إعادة موضع التواقيع للأصل", "Reset signature position")) {
                    template.signatureVerticalOffset = 0; template.signatureHorizontalOffset = 0
                }
                Text(tr("تتحرك التواقيع المدخلة معاً. راجع النتيجة في المعاينة؛ يمنع التطبيق تداخلها مع المتن أو الهامش.", "The entered signatures move together. Review the preview; overlap with the body or footer is prevented.")).font(.footnote).foregroundStyle(.secondary)
            }
            Section(tr("نسخة منه إلى", "Copies to")) {
                TextField(tr("النص", "Text"), text: $template.footer, axis: .vertical).lineLimit(2...8)
            }
        }
        .navigationTitle(tr("قالب الكتاب", "Letter template"))
        .sheet(isPresented: $choosingAccount) {
            SavedLetterAccountPicker(accounts: accounts) { account in
                template.account = account.iban.isEmpty ? (account.localAccount ?? "") : account.iban
            }
        }
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button(tr("إلغاء", "Cancel")) { dismiss() } }
            ToolbarItem(placement: .confirmationAction) { Button(tr("حفظ", "Save")) { do { try save(template); if bookID != nil { dismiss() } } catch { saveError = error.localizedDescription } } }
        }
        .alert(tr("تعذّر حفظ القالب", "Couldn't save template"), isPresented: Binding(get: { saveError != nil }, set: { if !$0 { saveError = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) { saveError = nil }
        } message: { Text(saveError ?? "") }
        .onChange(of: template.source) { _, _ in template.sourceName = "" }
        .task(id: monthName) {
            guard bookID == nil, !monthName.isEmpty else { return }
            do {
                let container = SharedModel.container
                groups = try await TransferDataEngine(modelContainer: container).read(month: monthName).0
            } catch { sourceError = error.localizedDescription }
        }
    }
    private func verticalOffsetLabel(_ value: Int) -> String {
        value == 0 ? tr("الموضع الأصلي", "Original position") : "\(Num.format(abs(value))) " + (value < 0 ? tr("أعلى", "up") : tr("أسفل", "down"))
    }
    private func horizontalOffsetLabel(_ value: Int) -> String {
        value == 0 ? tr("الموضع الأصلي", "Original position") : "\(Num.format(abs(value))) " + (value < 0 ? tr("يسار", "left") : tr("يمين", "right"))
    }
}

private struct CustomTransferLetterView: View {
    let template: CustomTransferTemplate
    let monthName: String
    @Query private var months: [PayrollMonth]
    @State private var manualAmount = ""
    @State private var error: String?
    @State private var previewURL: URL?
    @State private var snapshot = ""
    @State private var working = false
    @State private var bankCache = BankStatementCache.shared
    private var month: PayrollMonth? { months.first { $0.name == monthName } }
    private var sourceKey: String {
        guard let month else { return "" }
        return "\(month.name)|\(String(describing: month.persistentModelID))|\(month.importedAt.timeIntervalSince1970)|\(bankCache.revision)"
    }
    var body: some View {
        List {
            Section(tr("مراجعة الكتاب", "Review letter")) {
                LabeledContent(tr("القالب", "Template"), value: template.title)
                LabeledContent(tr("الشهر", "Month"), value: monthName.isEmpty ? "—" : monthName)
                if !template.sourceName.isEmpty { LabeledContent(tr("البند", "Item"), value: template.sourceName) }
                if !template.account.isEmpty { LabeledContent(tr("الحساب", "Account"), value: template.account).privacySensitive() }
                if template.source == .manual {
                    TextField(tr("المبلغ بالدينار", "Amount in dinars"), text: $manualAmount).keyboardType(.numberPad)
                }
            }
            Section {
                Button(tr("معاينة مسودة الكتاب", "Preview letter draft"), systemImage: "doc.text.magnifyingglass") { prepare() }
                    .disabled(working || month == nil)
                Text(tr("راجع النص والمبلغ والحساب والتواقيع في المعاينة قبل المشاركة. لا ينشئ الكتاب تحويلاً مصرفياً.", "Review the text, amount, account and signatures before sharing. The letter does not create a bank transfer."))
                    .font(.footnote).foregroundStyle(.secondary)
            }
        }
        .navigationTitle(template.title)
        .overlay { if working { ProgressView().padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16)) } }
        .alert(tr("تعذّر إعداد الكتاب", "Couldn't prepare letter"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) { error = nil }
        } message: { Text(error ?? "") }
        .sheet(isPresented: Binding(get: { previewURL != nil }, set: { if !$0 { closePreview() } })) {
            NavigationStack {
                if let url = previewURL {
                    VStack(spacing: 0) {
                        Text(tr("مسودة للمراجعة: تحقق من الشهر والمبلغ والحساب والتواقيع.", "Draft: check the month, amount, account and signatures."))
                            .font(.footnote).foregroundStyle(.orange).padding()
                        PDFPreview(url: url)
                    }
                    .navigationTitle(tr("معاينة الكتاب", "Letter preview"))
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) { Button(tr("تم", "Done")) { closePreview() } }
                        ToolbarItem(placement: .bottomBar) {
                            Button(tr("مشاركة PDF", "Share PDF"), systemImage: "square.and.arrow.up") { ActivityShare.present([url]) }
                                .disabled(sourceKey != snapshot || BackupManager.shared.isWorking || ImportCenter.shared.isBusy || StaffImportState.shared.isImporting)
                        }
                    }
                }
            }
        }
    }
    private func closePreview() {
        if let previewURL { try? FileManager.default.removeItem(at: previewURL.deletingLastPathComponent()) }
        previewURL = nil
    }
    private func prepare() {
        guard let month, !working, !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else { return }
        let key = sourceKey, summaryData = month.itemSummaryData, manual = manualAmount, selected = template
        working = true
        Task {
            defer { working = false }
            do {
                let period = try TransferLetterPeriod(monthName)
                var amount: Double?
                switch selected.source {
                case .none: break
                case .manual:
                    let clean = ArabicText.latinDigits(manual).replacingOccurrences(of: ",", with: "").trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !clean.isEmpty, clean.allSatisfy({ $0.isASCII && $0.isNumber }), let value = Double(clean) else { throw CustomTransferTemplate.invalid(tr("أدخل مبلغاً صحيحاً.", "Enter a valid amount.")) }
                    amount = value
                case .payroll:
                    guard let summary = PayrollItems.decodeSummary(summaryData), let item = summary[selected.sourceName] else { throw CustomTransferTemplate.invalid(tr("بند الرواتب غير موجود في الشهر المختار.", "The payroll item is unavailable for this month.")) }
                    amount = item.total
                case .execution:
                    let container = SharedModel.container
                    let (groups, missing) = try await TransferDataEngine(modelContainer: container).read(month: monthName)
                    let matching = groups.filter { $0.title == selected.sourceName }
                    guard matching.count == 1, (missing[selected.sourceName] ?? 0) == 0 else { throw CustomTransferTemplate.invalid(tr("الجهة غير متاحة أو لديها مبالغ ناقصة؛ راجع الاستقطاعات.", "The entity is unavailable or has incomplete amounts; review deductions.")) }
                    amount = matching[0].total
                }
                var values: [String: String] = [:]
                let names = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"]
                values["الشهر"] = names[period.month - 1]; values["السنة"] = ReportSheet.easternDigits(String(period.year))
                if let amount {
                    let numbers = try TransferWordValues.make(month: monthName, amount: amount, custom: .init(), requested: ["AMOUNT", "AMOUNT_WORDS"])
                    values["المبلغ"] = numbers["AMOUNT"].map(ReportSheet.easternDigits); values["المبلغ كتابة"] = numbers["AMOUNT_WORDS"]
                    guard selected.body.contains("{{المبلغ}}") || selected.body.contains("{{المبلغ كتابة}}") else { throw CustomTransferTemplate.invalid(tr("أضف حقل المبلغ إلى متن الكتاب.", "Add the amount field to the letter body.")) }
                }
                let account = ArabicText.latinDigits(selected.account).uppercased().filter { !$0.isWhitespace && $0 != "-" }
                if !account.isEmpty {
                    guard TransferRules.validIBAN(account) || (account.count == 15 && account.allSatisfy({ $0.isASCII && $0.isNumber })) else { throw CustomTransferTemplate.invalid(tr("رقم الحساب غير صالح؛ أدخل IBAN عراقياً صحيحاً أو حساباً محلياً من ١٥ رقماً.", "Enter a valid Iraqi IBAN or a 15-digit local account.")) }
                    guard selected.body.contains("{{الحساب}}") else { throw CustomTransferTemplate.invalid(tr("أضف حقل الحساب إلى متن الكتاب أو أزل الحساب المرتبط.", "Add the account field to the body or remove the linked account.")) }
                    values["الحساب"] = account
                }
                var formattedTemplate = selected
                formattedTemplate.body = CustomTransferTemplate.formatBodyNumbers(selected.body)
                let body = try formattedTemplate.render(values)
                let required = [selected.header, selected.number, selected.recipient, selected.subject, body, selected.footer]
                guard required.allSatisfy({ !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else { throw CustomTransferTemplate.invalid(tr("أكمل الترويسة والعدد والجهة والموضوع والمتن والنسخة منه إلى.", "Complete the header, reference, recipient, subject, body and copies.")) }
                let date = ReportSheet.easternDigits(period.signatureDate)
                let signatures = try CustomTransferTemplate.signatureCells([selected.firstSigner, selected.secondSigner, selected.thirdSigner], date: date)
                var blocks: [TransferWordDocument.Block] = [
                    .paragraph(.init(text: CustomTransferTemplate.formatBodyNumbers(selected.header), font: "Times New Roman", halfPoints: selected.halfPoints(.header, default: 32), bold: true)),
                    .paragraph(.init(text: TransferWordGroupDraft.referenceText(number: ReportSheet.easternDigits(selected.number), date: date, adaptivePDF: true), font: "Times New Roman", halfPoints: selected.halfPoints(.reference, default: 30), bold: true)),
                    .paragraph(.init(text: "إلى / " + CustomTransferTemplate.formatBodyNumbers(selected.recipient), font: "Arial", halfPoints: selected.halfPoints(.recipient, default: 32), bold: true, centered: true)),
                    .paragraph(.init(text: "م / " + CustomTransferTemplate.formatBodyNumbers(selected.subject), font: "Arial", halfPoints: selected.halfPoints(.subject, default: 32), bold: true, centered: true)),
                    .paragraph(TransferWordGroupDraft.bodyParagraph(body, template: selected))
                ]
                if let attachments = selected.attachmentParagraph {
                    blocks.append(.paragraph(.init(text: attachments, halfPoints: selected.halfPoints(.attachments, default: 22))))
                }
                blocks.append(.table(.init(headers: [], rows: [signatures], font: "Times New Roman", halfPoints: selected.halfPoints(.signatures, default: 32), columnWeights: [1, 1, 1], showBorders: false)))
                blocks.append(.paragraph(.init(text: TransferWordGroupDraft.footerWritingSpace(CustomTransferTemplate.formatBodyNumbers(selected.footer), adaptivePDF: true), halfPoints: selected.halfPoints(.footer, default: 24), bold: true)))
                let dir = FileManager.default.temporaryDirectory.appendingPathComponent("custom-letter-\(UUID().uuidString)", isDirectory: true)
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                let url = dir.appendingPathComponent("كتاب مخصص.pdf")
                do {
                    try TransferAdaptivePDF.write(blocks: blocks, destination: url, sakkalBold: nil, group: false,
                        signatureOffset: selected.signatureVerticalOffset ?? 0, signatureHorizontalOffset: selected.signatureHorizontalOffset ?? 0, template: selected)
                    var checks: [String] = []
                    if selected.body.contains("{{المبلغ}}"), let value = values["المبلغ"] { checks.append(value) }
                    if !account.isEmpty { checks.append(account) }
                    try TransferAdaptivePDF.verify(url, required: checks)
                    guard sourceKey == key, !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else { throw CustomTransferTemplate.invalid(tr("تغيّرت البيانات أثناء إعداد الكتاب؛ أعد المحاولة.", "Data changed while preparing the letter; try again.")) }
                    snapshot = key; previewURL = url
                } catch { try? FileManager.default.removeItem(at: dir); throw error }
            } catch { self.error = error.localizedDescription }
        }
    }
}
