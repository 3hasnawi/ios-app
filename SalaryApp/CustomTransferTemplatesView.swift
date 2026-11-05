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

private struct CustomTransferTemplateEditor: View {
    @Environment(\.dismiss) private var dismiss
    @Query private var months: [PayrollMonth]
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()
    @State private var template: CustomTransferTemplate
    @State private var groups: [ReportGroup] = []
    @State private var sourceError: String?
    @State private var saveError: String?
    let monthName: String
    let save: (CustomTransferTemplate) throws -> Void

    init(template: CustomTransferTemplate, monthName: String, save: @escaping (CustomTransferTemplate) throws -> Void) {
        _template = State(initialValue: template); self.monthName = monthName; self.save = save
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
                Menu(tr("إضافة حقل إلى المتن", "Insert field into body")) {
                    ForEach(["المبلغ", "المبلغ كتابة", "الشهر", "السنة", "الحساب"], id: \.self) { key in
                        Button(key) { template.body += "{{\(key)}}" }
                    }
                }
            }
            Section(tr("مصدر المبلغ", "Amount source")) {
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
            }
            Section(tr("الحساب المرتبط اختيارياً", "Optional linked account")) {
                TextField(tr("رقم الحساب أو IBAN", "Account number or IBAN"), text: $template.account).textInputAutocapitalization(.characters).autocorrectionDisabled()
                if !accounts.isEmpty {
                    Menu(tr("اختيار من الحسابات المحفوظة", "Choose a saved account")) {
                        ForEach(accounts) { account in
                            Button(account.entity) { template.account = account.iban.isEmpty ? (account.localAccount ?? "") : account.iban }
                        }
                    }
                }
                Text(tr("تأكد من رقم الحساب عند كل إصدار للكتاب.", "Verify the account number for each letter.")).font(.footnote).foregroundStyle(.secondary)
            }
            Section(tr("التواقيع", "Signatures")) {
                TextField(tr("اسم العضو الأول", "First member"), text: $template.firstSigner)
                TextField(tr("اسم العضو الثاني", "Second member"), text: $template.secondSigner)
                TextField(tr("اسم العضو الثالث", "Third member"), text: $template.thirdSigner)
                Text(tr("تستخدم صفات اللجنة المعتمدة في كتب التطبيق.", "The existing committee titles are used.")).font(.footnote).foregroundStyle(.secondary)
            }
            Section(tr("نسخة منه إلى", "Copies to")) {
                TextField(tr("النص", "Text"), text: $template.footer, axis: .vertical).lineLimit(2...8)
            }
        }
        .navigationTitle(tr("قالب كتاب", "Letter template"))
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button(tr("إلغاء", "Cancel")) { dismiss() } }
            ToolbarItem(placement: .confirmationAction) { Button(tr("حفظ", "Save")) { do { try save(template) } catch { saveError = error.localizedDescription } } }
        }
        .alert(tr("تعذّر حفظ القالب", "Couldn't save template"), isPresented: Binding(get: { saveError != nil }, set: { if !$0 { saveError = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) { saveError = nil }
        } message: { Text(saveError ?? "") }
        .onChange(of: template.source) { _, _ in template.sourceName = "" }
        .task(id: monthName) {
            guard !monthName.isEmpty else { return }
            do {
                let container = SharedModel.container
                groups = try await TransferDataEngine(modelContainer: container).read(month: monthName).0
            } catch { sourceError = error.localizedDescription }
        }
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
                values["الشهر"] = names[period.month - 1]; values["السنة"] = String(period.year)
                if let amount {
                    let numbers = try TransferWordValues.make(month: monthName, amount: amount, custom: .init(), requested: ["AMOUNT", "AMOUNT_WORDS"])
                    values["المبلغ"] = numbers["AMOUNT"]; values["المبلغ كتابة"] = numbers["AMOUNT_WORDS"]
                    guard selected.body.contains("{{المبلغ}}") || selected.body.contains("{{المبلغ كتابة}}") else { throw CustomTransferTemplate.invalid(tr("أضف حقل المبلغ إلى متن الكتاب.", "Add the amount field to the letter body.")) }
                }
                let account = ArabicText.latinDigits(selected.account).uppercased().filter { !$0.isWhitespace && $0 != "-" }
                if !account.isEmpty {
                    guard TransferRules.validIBAN(account) || (account.count == 15 && account.allSatisfy({ $0.isASCII && $0.isNumber })) else { throw CustomTransferTemplate.invalid(tr("رقم الحساب غير صالح؛ أدخل IBAN عراقياً صحيحاً أو حساباً محلياً من ١٥ رقماً.", "Enter a valid Iraqi IBAN or a 15-digit local account.")) }
                    guard selected.body.contains("{{الحساب}}") else { throw CustomTransferTemplate.invalid(tr("أضف حقل الحساب إلى متن الكتاب أو أزل الحساب المرتبط.", "Add the account field to the body or remove the linked account.")) }
                    values["الحساب"] = account
                }
                let body = try selected.render(values)
                let required = [selected.header, selected.number, selected.recipient, selected.subject, body, selected.footer, selected.firstSigner, selected.secondSigner, selected.thirdSigner]
                guard required.allSatisfy({ !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else { throw CustomTransferTemplate.invalid(tr("أكمل الترويسة والعدد والجهة والموضوع والمتن والتواقيع والنسخة منه إلى.", "Complete the header, reference, recipient, subject, body, signatures and copies.")) }
                let date = period.signatureDate
                let signatures = TransferWordGroupDraft.signatureCells([selected.firstSigner, selected.secondSigner, selected.thirdSigner], date: date, adaptivePDF: true)
                let blocks: [TransferWordDocument.Block] = [
                    .paragraph(.init(text: selected.header, font: "Times New Roman", halfPoints: 32, bold: true)),
                    .paragraph(.init(text: TransferWordGroupDraft.referenceText(number: selected.number, date: date, adaptivePDF: true), font: "Times New Roman", halfPoints: 30, bold: true)),
                    .paragraph(.init(text: "إلى / " + selected.recipient, font: "Arial", halfPoints: 32, bold: true, centered: true)),
                    .paragraph(.init(text: "م / " + selected.subject, font: "Arial", halfPoints: 32, bold: true, centered: true)),
                    .paragraph(.init(text: body, halfPoints: 28)),
                    .table(.init(headers: [], rows: [signatures], font: "Times New Roman", halfPoints: 32, columnWeights: [1, 1, 1], showBorders: false)),
                    .paragraph(.init(text: TransferWordGroupDraft.footerWritingSpace(selected.footer, adaptivePDF: true), halfPoints: 24, bold: true))
                ]
                let dir = FileManager.default.temporaryDirectory.appendingPathComponent("custom-letter-\(UUID().uuidString)", isDirectory: true)
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                let url = dir.appendingPathComponent("كتاب مخصص.pdf")
                do {
                    try TransferAdaptivePDF.write(blocks: blocks, destination: url, sakkalBold: nil, group: false)
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
