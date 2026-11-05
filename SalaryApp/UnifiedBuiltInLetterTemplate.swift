import SwiftUI
import SwiftData

/// يفتح محرر القالب نفسه للكتب الجاهزة مع الاحتفاظ بمصادرها المحاسبية والتحقق من الحسابات.
struct UnifiedBuiltInLetterTemplate: View {
    let bookID: String
    let title: String
    let month: String
    var entityTitle: String? = nil
    var entityAccount: String? = nil
    var entityAccountKey: String? = nil
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var stored = Data()
    @AppStorage(TransferReferencePack.settingsKey) private var references = Data()
    @AppStorage(ExecutionAccountStore.storageKey) private var accounts = Data()
    @AppStorage("bankStatementIBAN") private var payer = ""
    @State private var initial: CustomTransferTemplate?
    @State private var snapshot = Data()
    @State private var accountReferenceSnapshot = Data()
    @State private var accountPayerSnapshot = ""
    @State private var accountSnapshot = Data()
    @State private var referenceAccount: String?
    @State private var originalAccount: String?
    @State private var error: String?

    private var isEntity: Bool { ["ramadi", "housing", "water"].contains(bookID) }
    private var isTreasury: Bool { ["treasury_guarantee", "treasury_schools"].contains(bookID) }
    private var supportsAccount: Bool { ["food_company", "food_tax", "tax_social", "contracts", "cash_salary", "ramadi", "housing", "water"].contains(bookID) }
    var body: some View {
        Group {
            if let initial {
                CustomTransferTemplateEditor(template: initial, monthName: month, bookID: bookID, canEditAccount: isTreasury || originalAccount != nil && referenceAccount != nil, save: save)
            } else if let error {
                ContentUnavailableView(tr("تعذّر فتح القالب", "Could not open template"), systemImage: "exclamationmark.triangle", description: Text(error))
            } else { ProgressView(tr("جارٍ تحميل قالب الكتاب…", "Loading letter template…")) }
        }
        .task { await load() }
    }
    private func load() async {
        guard initial == nil, error == nil else { return }
        let settings = stored, refs = references, payerSnapshot = payer, accountData = accounts
        do {
            let store = try (settings.isEmpty ? TransferLetterCustomizationStore() : JSONDecoder().decode(TransferLetterCustomizationStore.self, from: settings))
            let custom = store.effective(for: bookID)
            var form = custom.letterTemplate ?? CustomTransferTemplate()
            if custom.letterTemplate == nil {
                let fallback = isTreasury ? store.effective(for: "food_tax") : custom
                form.title = title
                form.header = fallback.header ?? ""; form.number = fallback.fixedNumber ?? ""
                form.recipient = "مصرف الرافدين / فرع الأنبار"; form.subject = "تحويل مبلغ"
                form.body = custom.bodyTemplate ?? TransferLetterBody.template(for: bookID) ?? ""
                form.footer = isTreasury ? "نسخة منه إلى:\nمديرية خزينة الأنبار للاطلاع وإعلامنا الاستلام ... مع التقدير.\nشعبة المصروفات / شعبة الموازنة / شعبة الرواتب / إجراء اللازم والمتابعة.\nالأرشيف إجراء اللازم." : (custom.footer ?? "")
                form.firstSigner = fallback.committeeFirst ?? ""; form.secondSigner = fallback.committeeSecond ?? ""; form.thirdSigner = fallback.committeeThird ?? ""
                form.signatureVerticalOffset = isTreasury ? nil : custom.signatureOffset
            }
            if bookID == "food_company" || bookID == "food_tax" { form.foodContractReference = custom.foodContractReference }
            if !isTreasury { form.bodyMonth = custom.bodyMonth; form.outgoingMonth = custom.outgoingMonth }
            if isTreasury, form.account.isEmpty, !refs.isEmpty, TransferRules.validIBAN(payerSnapshot) {
                if let account = try? await Task.detached(priority: .userInitiated, operation: {
                    try TransferWordReferenceAccounts.beneficiary(bookID: "tax_social", references: refs, payerIBAN: payerSnapshot)
                }).value { form.account = account }
            }
            if supportsAccount, !refs.isEmpty, TransferRules.validIBAN(payerSnapshot) {
                let id = bookID, entity = isEntity
                let reference = try? await Task.detached(priority: .userInitiated) {
                    if entity { return try TransferWordReferenceAccounts.individualBeneficiary(bookID: id, references: refs, payerIBAN: payerSnapshot) }
                    return try TransferWordReferenceAccounts.beneficiary(bookID: id, references: refs, payerIBAN: payerSnapshot)
                }.value
                if let reference {
                    let original = isEntity ? entityAccount : reference
                    if let original, !original.isEmpty, (!isEntity || entityTitle != nil && entityAccountKey != nil) {
                        referenceAccount = reference; originalAccount = original
                        accountReferenceSnapshot = refs; accountPayerSnapshot = payerSnapshot; accountSnapshot = accountData
                        form.account = custom.beneficiaryAccount.flatMap { $0.isEmpty ? nil : $0 } ?? original
                    }
                }
            }
            guard stored == settings, references == refs, payer == payerSnapshot, accounts == accountData else {
                throw CustomTransferTemplate.invalid(tr("تغيّرت البيانات أثناء فتح القالب؛ أعد فتحه.", "Data changed while opening the template; reopen it."))
            }
            snapshot = settings; initial = form
        } catch { self.error = error.localizedDescription }
    }
    private func save(_ form: CustomTransferTemplate) throws {
        guard stored == snapshot, !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
            throw CustomTransferTemplate.invalid(tr("تغيّرت البيانات أو توجد عملية جارية؛ أعد فتح القالب.", "Data changed or an operation is running; reopen the template."))
        }
        try form.validate()
        if isTreasury, !form.account.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            guard TransferRules.validIBAN(form.account), TransferLetterCustomValues.normalizedAccount(form.account) != TransferLetterCustomValues.normalizedAccount(payer) else { throw TransferLetterCustomValues.Failure.account }
        }
        try TransferLetterBody.validate(form.body, for: bookID)
        if !isTreasury {
            _ = try TransferLetterCustomValues.outgoingDate(form.outgoingMonth, sourceMonth: "كانون الثاني 2026", eastern: false)
            if let bodyMonth = form.bodyMonth, !bodyMonth.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                _ = try TransferLetterCustomValues.bodyMonth(bodyMonth, source: bodyMonth)
            }
        }
        guard [form.header, form.number, form.recipient, form.subject, form.footer].allSatisfy({ !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else {
            throw CustomTransferTemplate.invalid(tr("أكمل الترويسة والعدد والجهة والموضوع ونسخة منه إلى.", "Complete the header, reference, recipient, subject and copies."))
        }
        _ = try CustomTransferTemplate.signatureCells([form.firstSigner, form.secondSigner, form.thirdSigner], date: " / ١ / ٢٠٢٦")
        var store = try (stored.isEmpty ? TransferLetterCustomizationStore() : JSONDecoder().decode(TransferLetterCustomizationStore.self, from: stored))
        var changes = TransferLetterCustomization()
        if !isTreasury { changes.bodyMonth = form.bodyMonth ?? ""; changes.outgoingMonth = form.outgoingMonth ?? "" }
        if let original = originalAccount, let reference = referenceAccount {
            guard references == accountReferenceSnapshot, payer == accountPayerSnapshot, accounts == accountSnapshot else { throw TransferLetterCustomValues.Failure.sourceChanged }
            if isEntity {
                guard let key = entityAccountKey, let account = try ExecutionAccountStore.decode(accounts).first(where: { $0.key == key }) else { throw TransferLetterCustomValues.Failure.sourceChanged }
                let now = bookID == "ramadi" ? account.iban : (TransferRules.individualLocalAccount(account) ?? "")
                guard TransferLetterCustomValues.normalizedAccount(now) == TransferLetterCustomValues.normalizedAccount(original) else { throw TransferLetterCustomValues.Failure.sourceChanged }
            }
            let source = isEntity ? (entityTitle ?? "") : bookID
            let proposed = form.account.trimmingCharacters(in: .whitespacesAndNewlines)
            if !proposed.isEmpty {
                let probe = TransferLetterCustomization(beneficiaryAccount: proposed, beneficiarySource: source,
                    beneficiaryOriginal: TransferLetterCustomValues.normalizedAccount(original), beneficiaryReference: TransferLetterCustomValues.normalizedAccount(reference))
                _ = try TransferLetterCustomValues.account(probe, bookID: bookID, source: source, original: original, reference: reference)
                guard TransferLetterCustomValues.normalizedAccount(proposed) != TransferLetterCustomValues.normalizedAccount(payer) else { throw TransferLetterCustomValues.Failure.account }
            }
            if proposed.isEmpty || TransferLetterCustomValues.normalizedAccount(proposed) == TransferLetterCustomValues.normalizedAccount(original) {
                changes.beneficiaryAccount = ""; changes.beneficiarySource = ""
                changes.beneficiaryOriginal = ""; changes.beneficiaryReference = ""
            } else {
                changes.beneficiaryAccount = proposed; changes.beneficiarySource = source
                changes.beneficiaryOriginal = TransferLetterCustomValues.normalizedAccount(original)
                changes.beneficiaryReference = TransferLetterCustomValues.normalizedAccount(reference)
            }
        }
        changes.letterTemplate = form
        store.apply(changes, to: bookID)
        if bookID == "food_company" || bookID == "food_tax" {
            guard let reference = form.foodContractReference, reference.count <= 200, !reference.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw CustomTransferTemplate.invalid(tr("أدخل رقم عقد الإطعام وتاريخه.", "Enter the food contract reference and date."))
            }
            store.common.foodContractReference = reference
        }
        stored = try JSONEncoder().encode(store)
        BackupManager.shared.markChanged()
        snapshot = stored
    }
}

struct UnifiedLetterTemplateList: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse)]) private var months: [PayrollMonth]
    var body: some View {
        List {
            Section(tr("اختر الكتاب", "Choose a letter")) {
                NavigationLink(tr("شركة عراق الجوال", "Iraq Al-Jawwal Company")) {
                    UnifiedBuiltInLetterTemplate(bookID: "food_company", title: tr("شركة عراق الجوال", "Iraq Al-Jawwal Company"), month: months.first?.name ?? "")
                }
                ForEach(TransferLetterDraftKind.allCases) { kind in
                    NavigationLink(kind.title) {
                        UnifiedBuiltInLetterTemplate(bookID: kind.rawValue, title: kind.title, month: months.first?.name ?? "")
                    }
                }
                NavigationLink(tr("كتاب التضمين", "Guarantee letter")) {
                    UnifiedBuiltInLetterTemplate(bookID: "treasury_guarantee", title: tr("كتاب التضمين", "Guarantee letter"), month: months.first?.name ?? "")
                }
                NavigationLink(tr("رسم الطابع وبناء المدارس", "Stamp duty and school construction")) {
                    UnifiedBuiltInLetterTemplate(bookID: "treasury_schools", title: tr("رسم الطابع وبناء المدارس", "Stamp duty and school construction"), month: months.first?.name ?? "")
                }
                NavigationLink(tr("الكتب المخصصة", "Custom letters")) {
                    CustomTransferTemplatesView(monthName: months.first?.name ?? "")
                }
            }
        }
        .navigationTitle(tr("قالب الكتاب", "Letter template"))
        .navigationBarTitleDisplayMode(.inline)
    }
}
