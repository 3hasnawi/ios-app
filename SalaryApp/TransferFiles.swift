import SwiftUI
import SwiftData
import UniformTypeIdentifiers

/// تصنيفات حسابات جهات التنفيذ؛ فرع الأنبار لا يدخل في ملف التحويل.
nonisolated enum TransferCategory: String, CaseIterable, Identifiable, Codable, Sendable {
    case rafidain, otherBanks, anbar, unresolved
    var id: String { rawValue }
    var title: String {
        switch self {
        case .rafidain: tr("الرافدين · عدا فرع الأنبار", "Rafidain · excluding Anbar branch")
        case .otherBanks: tr("مجموعة رشيد ومصارف أخرى", "Rasheed and other banks group")
        case .anbar: tr("الكتب المنفردة · الرافدين فرع الأنبار", "Individual letters · Rafidain Anbar")
        case .unresolved: tr("جهات تحتاج تصنيفاً", "Entities needing classification")
        }
    }
    var icon: String { self == .anbar ? "doc.text" : self == .unresolved ? "exclamationmark.triangle" : "building.columns" }
    var prefix: String { self == .rafidain ? "30000" : "20000" }
}

nonisolated struct TransferEntity: Identifiable, Sendable {
    let group: ReportGroup
    let account: SavedExecutionAccount?
    let category: TransferCategory
    let missingAmounts: Int
    var beneficiaryName: String? = nil
    var payrollRecipient: String? = nil
    var duplicateSource = false
    var id: String { "\(group.id)|\(payrollRecipient ?? "execution")" }
    var isDirectAnbarBank: Bool { payrollRecipient == nil && account == nil && category == .anbar && TransferRules.isAnbarBankSource(group.title) }
}

/// بنود الرواتب المستقلة عن ملف جهات التنفيذ وتحتاج ربط حساب كل مستفيد.
nonisolated enum TransferPayrollRecipient: String, CaseIterable, Identifiable {
    case health, fundFood, fundDonations, sports
    var id: String { rawValue }
    var arabicTitle: String {
        switch self {
        case .health: "مديرية شؤون الرعاية الصحية"
        case .fundFood: "صندوق شهداء الشرطة (عقد إطعام)"
        case .fundDonations: "صندوق شهداء الشرطة (تبرعات)"
        case .sports: "مديرية التدريب البدني وألعاب الشرطة"
        }
    }
    var title: String {
        switch self {
        case .health: tr(arabicTitle, "Health affairs directorate")
        case .fundFood: tr(arabicTitle, "Police martyrs fund (food contract)")
        case .fundDonations: tr(arabicTitle, "Police martyrs fund (donations)")
        case .sports: tr(arabicTitle, "Police physical training and sports")
        }
    }
    func amount(_ figures: TransferLetterFigures) -> Double {
        switch self {
        case .health: figures.healthcare
        case .fundFood: figures.foodFund
        case .fundDonations: figures.policeFundDonations
        case .sports: figures.sports
        }
    }
}

nonisolated enum TransferRules {
    static let settingsKey = "transferEntityCategories"
    /// اسم بند الفرع نفسه معتمد لكتابه فقط، لا يُنشئ حساب مستفيد أو ملف تحويل.
    static func isAnbarBankSource(_ title: String) -> Bool {
        let text = ArabicText.normalize(title).replacingOccurrences(of: "/", with: " ").replacingOccurrences(of: "-", with: " ")
        return text.split(whereSeparator: \.isWhitespace).joined(separator: " ") == ArabicText.normalize("مصرف الرافدين فرع الانبار")
    }
    static func classify(_ account: SavedExecutionAccount?) -> TransferCategory {
        guard let account else { return .unresolved }
        let branch = ArabicText.normalize(account.branch)
        if branch.contains(ArabicText.normalize("الرافدين")) {
            let clean = branch.replacingOccurrences(of: "/", with: " ").replacingOccurrences(of: "-", with: " ")
                .split(whereSeparator: \.isWhitespace).joined(separator: " ")
            if clean.hasSuffix("فرع الانبار") { return .anbar }
            if clean.contains("الانبار") { return .unresolved }
            return branch.contains(ArabicText.normalize("فرع")) ? .rafidain : .unresolved
        }
        let known = ["الرشيد", "العقاري", "الزراعي", "الاسلامي", "التجاري", "الاهلي"]
        if known.contains(where: { branch.contains(ArabicText.normalize($0)) }) { return .otherBanks }
        // اسم مصرف صريح مع SWIFT صالح يسمح بإضافة المصارف الأخرى دون قائمة أسماء مغلقة.
        let explicitBank = branch.contains(ArabicText.normalize("مصرف")) || branch.contains(ArabicText.normalize("بنك"))
        return explicitBank && validBIC(account.swift) ? .otherBanks : .unresolved
    }
    static func validIBAN(_ input: String) -> Bool {
        let s = ArabicText.latinDigits(input).uppercased().filter { !$0.isWhitespace && $0 != "-" }
        guard s.count == 23, s.hasPrefix("IQ"), s.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) }) else { return false }
        let rotated = String(s.dropFirst(4)) + String(s.prefix(4))
        var remainder = 0
        for c in rotated.utf8 {
            let value = c >= 65 ? String(Int(c) - 55) : String(Int(c) - 48)
            for d in value.utf8 { remainder = (remainder * 10 + Int(d - 48)) % 97 }
        }
        return remainder == 1
    }
    static func validBIC(_ value: String) -> Bool {
        let s = value.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        let c = Array(s.utf8)
        return (c.count == 8 || c.count == 11) && c.prefix(6).allSatisfy { (65...90).contains($0) }
            && c.dropFirst(6).allSatisfy { (65...90).contains($0) || (48...57).contains($0) }
    }
    /// الجزء المحلي المكوّن من 15 رقماً في IBAN العراقي، بعد التحقق من كامل الرقم.
    static func localAccountDigits(_ value: String) -> String? {
        guard validIBAN(value) else { return nil }
        let normalized = ArabicText.latinDigits(value).uppercased().filter { !$0.isWhitespace && $0 != "-" }
        let account = String(normalized.suffix(15))
        guard account.utf8.count == 15, account.utf8.allSatisfy({ (48...57).contains($0) }) else { return nil }
        return account
    }
    /// يقبل الحساب المحلي للكتب فقط، ويرفض التعارض مع IBAN محفوظ.
    static func individualLocalAccount(_ account: SavedExecutionAccount) -> String? {
        let local = account.localAccount.map { ArabicText.latinDigits($0).filter { !$0.isWhitespace && $0 != "-" } }
        if let local, (local.utf8.count != 15 || !local.utf8.allSatisfy({ (48...57).contains($0) })) { return nil }
        if !account.iban.isEmpty {
            guard let fromIBAN = localAccountDigits(account.iban) else { return nil }
            if let local, local != fromIBAN { return nil }
            return fromIBAN
        }
        return local
    }
    static func safeField(_ value: String) -> Bool {
        !value.isEmpty && !value.contains("|") && !value.contains("\n") && !value.contains("\r") && !value.contains("\"")
    }
    static func issues(_ entity: TransferEntity) -> [String] {
        var result: [String] = []
        if entity.account == nil && !entity.isDirectAnbarBank { return [tr("الحساب غير محفوظ", "Account is missing")] }
        if entity.duplicateSource { result.append(tr("البند موجود أيضاً في ملف الجهات؛ راجع مصدره لتجنب التكرار", "This item also exists in execution deductions; review for duplication")) }
        if let account = entity.account {
            let detected = classify(account)
            if detected != .unresolved && entity.category != .unresolved && detected != entity.category {
                result.append(tr("المجموعة لا تطابق مصرف الحساب؛ راجع الحساب والتصنيف.", "The group does not match the account bank; review the account and category."))
            }
        }
        if let account = entity.account, entity.category == .anbar {
            if individualLocalAccount(account) == nil { result.append(tr("حساب الجهة غير صالح أو غير مطابق", "Invalid or inconsistent beneficiary account")) }
        } else if let account = entity.account {
            if !validIBAN(account.iban) { result.append(tr("IBAN غير صالح", "Invalid IBAN")) }
            if !validBIC(account.swift) { result.append(tr("SWIFT غير صالح أو غير موجود", "Invalid or missing SWIFT")) }
            if account.localAccount != nil && individualLocalAccount(account) == nil {
                result.append(tr("الحساب المحلي لا يطابق IBAN؛ راجع الحساب قبل التحويل", "Local account conflicts with the IBAN; review before transfer"))
            }
        }
        if entity.missingAmounts > 0 { result.append(tr("توجد قيود بمبلغ ناقص أو غير صالح", "Some entries have missing or invalid amounts")) }
        if !entity.group.total.isFinite || entity.group.total <= 0 || entity.group.total.rounded() != entity.group.total {
            result.append(tr("المبلغ غير صالح للتحويل", "Invalid transfer amount"))
        }
        if !safeField(entity.beneficiaryName ?? entity.group.title) { result.append(tr("اسم الجهة يحتوي رمزاً غير مقبول", "Unsupported character in entity name")) }
        return result
    }
    static func csv(_ entities: [TransferEntity], category: TransferCategory, month: String, payer: String, payerIBAN: String, date: Date = .now) throws -> Data {
        guard category == .rafidain || category == .otherBanks, !entities.isEmpty,
              safeField(payer), validIBAN(payerIBAN), safeField(month),
              Set(entities.map(\.id)).count == entities.count,
              entities.allSatisfy({ $0.category == category && $0.group.month == month && issues($0).isEmpty }) else {
            throw NSError(domain: "TransferFiles", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("تحقق من الحسابات والمبالغ وبيانات الجهة الدافعة.", "Check accounts, amounts and payer details.")])
        }
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd"
        let day = formatter.string(from: date)
        var rows = ["Reference|Value Date|Payer Name|Payer Account|Amount|Currency|Receiver BIC|Beneficiary Name|Beneficiary Account|Remittance Information|Details of Charges"]
        for (index, entity) in entities.enumerated() {
            guard let account = entity.account else { throw CocoaError(.fileWriteUnknown) }
            let amount = String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), entity.group.total)
            let fields = [day + category.prefix + String(index + 1), day, payer,
                          ArabicText.latinDigits(payerIBAN).uppercased().filter { !$0.isWhitespace && $0 != "-" },
                          amount, "IQD", account.swift.trimmingCharacters(in: .whitespacesAndNewlines).uppercased(),
                          entity.beneficiaryName ?? entity.group.title, ArabicText.latinDigits(account.iban).uppercased().filter { !$0.isWhitespace && $0 != "-" },
                          "استقطاعات \(month)", "SLEV"]
            guard fields.allSatisfy(safeField) else { throw CocoaError(.fileWriteUnknown) }
            rows.append(fields.joined(separator: "|"))
        }
        return Data(rows.joined(separator: "\r\n").utf8)
    }
}

@ModelActor
actor TransferDataEngine {
    func read(month: String) async throws -> ([ReportGroup], [String: Int]) {
        let groups = try await ReportEngine(modelContainer: modelContainer).deductionGroups(month: month, byEntity: false)
        var descriptor = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
        descriptor.propertiesToFetch = [\.title, \.amount, \.note]
        var missing: [String: Int] = [:]
        for (i, item) in try modelContext.fetch(descriptor).enumerated() {
            if i.isMultiple(of: 512) { try Task.checkCancellation() }
            if DeductionStatus.read(item.note, amount: item.amount) != nil { continue }
            guard let amount = item.amount else { missing[item.title, default: 0] += 1; continue }
            if !amount.isFinite || amount < 0 || amount.rounded() != amount { missing[item.title, default: 0] += 1 }
        }
        return (groups, missing)
    }
}

struct TransferFilesView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse)]) private var months: [PayrollMonth]
    @AppStorage("reportMonth") private var selectedMonth = ""
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()
    @AppStorage(TransferRules.settingsKey) private var categoriesData = Data()
    @AppStorage("transferPayrollAccountLinks") private var payrollLinksData = Data()
    @AppStorage(TransferReferencePack.settingsKey) private var referenceData = Data()
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var customizationData = Data()
    @AppStorage(TransferHeaderFont.boldSettingsKey) private var boldFontData = Data()
    @AppStorage(TransferHeaderFont.regularSettingsKey) private var regularFontData = Data()
    @AppStorage("bankStatementAccountName") private var payerName = ""
    @AppStorage("bankStatementIBAN") private var payerIBAN = ""
    @State private var groups: [ReportGroup] = []
    @State private var missing: [String: Int] = [:]
    @State private var figures: TransferLetterFigures?
    @State private var figuresError: String?
    @State private var loadedKey = ""
    @State private var error: String?
    @State private var showSettings = false
    @State private var importingReferences = false
    @State private var referenceError: String?
    @State private var draftCoverage = TransferDraftCoverage.shared
    @State private var bankCache = BankStatementCache.shared
    @State private var loadedRevision = -1

    private var month: PayrollMonth? { months.first { $0.name == selectedMonth } ?? months.first }
    private var loadKey: String { "\(String(describing: month?.persistentModelID))|\(month?.name ?? "")|\(month?.importedAt.timeIntervalSince1970 ?? 0)" }
    private var categories: [String: String] { (try? JSONDecoder().decode([String: String].self, from: categoriesData)) ?? [:] }
    private var categoriesValid: Bool { categoriesData.isEmpty || (try? JSONDecoder().decode([String: String].self, from: categoriesData)) != nil }
    private var payrollLinks: [String: String] { (try? TransferAccountLinks.decode(payrollLinksData)) ?? [:] }
    private var linksValid: Bool { (try? TransferAccountLinks.decode(payrollLinksData)) != nil }
    private var accounts: [SavedExecutionAccount] { (try? ExecutionAccountStore.decode(accountData)) ?? [] }
    private var draftRevision: TransferDraftRevision {
        TransferDraftRevision(sourceKey: loadKey, accounts: accountData, categories: categoriesData,
            payrollLinks: payrollLinksData, customization: customizationData, references: referenceData,
            payerName: payerName, payerIBAN: payerIBAN, boldFont: boldFontData, regularFont: regularFontData)
    }
    private var entities: [TransferEntity] {
        let lookup = accounts.reduce(into: [String: SavedExecutionAccount]()) { $0[$1.key] = $1 }
        let deductions = groups.map { group in
            let account = linksValid ? TransferAccountLinks.resolve(title: group.title, accounts: lookup, links: payrollLinks) : nil
            let key = categoryKey(group.title, account)
            let explicit = payrollLinks[TransferAccountLinks.sourceKey(group.title)] != nil
            let directAnbar = linksValid && !explicit && account == nil && TransferRules.isAnbarBankSource(group.title)
            let category = categories[key].flatMap(TransferCategory.init(rawValue:)) ?? (directAnbar ? .anbar : TransferRules.classify(account))
            return TransferEntity(group: group, account: account, category: category, missingAmounts: missing[group.title] ?? 0,
                                  beneficiaryName: explicit ? account?.entity : nil)
        }
        guard let figures else { return deductions }
        let payroll = TransferPayrollRecipient.allCases.compactMap { recipient -> TransferEntity? in
            let amount = recipient.amount(figures)
            guard amount > 0 else { return nil }
            let account = linksValid ? payrollLinks[recipient.rawValue].flatMap { lookup[$0] } : nil
            let group = ReportGroup(kind: .deductions, month: figures.month, title: recipient.arabicTitle, count: 1, total: amount, byEntity: false)
            let category = categories[categoryKey(group.title, account)].flatMap(TransferCategory.init(rawValue:)) ?? TransferRules.classify(account)
            let duplicate = groups.contains { ArabicText.normalize($0.title) == ArabicText.normalize(recipient.arabicTitle) }
            return TransferEntity(group: group, account: account, category: category, missingAmounts: 0,
                                  beneficiaryName: account?.entity, payrollRecipient: recipient.rawValue, duplicateSource: duplicate)
        }
        return deductions + payroll
    }
    private func categoryKey(_ name: String, _ account: SavedExecutionAccount?) -> String {
        "\(ArabicText.normalize(name))|\(account?.iban ?? "")|\(ArabicText.normalize(account?.branch ?? ""))"
    }
    private func setCategory(_ category: TransferCategory, for entity: TransferEntity) -> Bool {
        guard categoriesValid, !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy,
              !StaffImportState.shared.isImporting else { return false }
        if let account = entity.account {
            guard let current = accounts.first(where: { $0.key == account.key }),
                  current.iban == account.iban, current.branch == account.branch,
                  current.swift == account.swift, current.localAccount == account.localAccount else { return false }
            let detected = TransferRules.classify(account)
            guard category == .unresolved || detected == .unresolved || detected == category else { return false }
        }
        var saved = categories
        let key = categoryKey(entity.group.title, entity.account)
        if category == .unresolved { saved.removeValue(forKey: key) }
        else { saved[key] = category.rawValue }
        guard let data = try? JSONEncoder().encode(saved) else { return false }
        categoriesData = data
        BackupManager.shared.markChanged()
        return true
    }
    private func setTransferAccount(_ entity: TransferEntity, key: String?) -> Bool {
        guard var saved = try? TransferAccountLinks.decode(payrollLinksData),
              !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else { return false }
        let source: String
        if let recipient = entity.payrollRecipient {
            guard TransferPayrollRecipient(rawValue: recipient) != nil else { return false }
            source = recipient
        } else { source = TransferAccountLinks.sourceKey(entity.group.title) }
        guard source != TransferAccountLinks.prefix else { return false }
        if let key {
            guard accounts.contains(where: { $0.key == key }) else { return false }
            saved[source] = key
        } else { saved.removeValue(forKey: source) }
        guard let data = try? JSONEncoder().encode(saved) else { return false }
        payrollLinksData = data
        BackupManager.shared.markChanged()
        return true
    }
    @ViewBuilder private func categoryRow(_ category: TransferCategory) -> some View {
        let items = entities.filter { $0.category == category }
        NavigationLink {
            TransferCategoryView(category: category,
                allEntities: Binding(get: { entities }, set: { _ in }),
                accounts: Binding(get: { accounts }, set: { _ in }),
                month: month?.name ?? "", sourceKey: loadKey, sourceRevision: loadedRevision,
                figures: figures, figuresError: figuresError,
                changeCategory: setCategory, setTransferAccount: setTransferAccount)
        } label: {
            categoryLabel(category, items: items)
        }
    }
    @ViewBuilder private func categoryLabel(_ category: TransferCategory, items: [TransferEntity]) -> some View {
        let total = items.reduce(0.0) { $0 + $1.group.total }
        let reviewCount = items.filter { !TransferRules.issues($0).isEmpty }.count
        VStack(alignment: .leading, spacing: 7) {
            Label(category.title, systemImage: category.icon).font(.headline)
            if linksValid && categoriesValid {
                Text(tr("\(Num.format(items.count)) جهة · \(Money.format(total))", "\(items.count) entities · \(Money.format(total))"))
                    .font(.subheadline).foregroundStyle(.secondary)
            } else {
                Text(tr("تعذّر احتساب المجموعة حتى استعادة البيانات المحفوظة.", "The group cannot be calculated until saved data is restored."))
                    .font(.caption).foregroundStyle(.orange)
            }
            if category == .anbar {
                Text(tr("كتب منفردة", "Individual documents")).font(.caption).foregroundStyle(.secondary)
            }
            if reviewCount > 0 {
                Text(tr("\(Num.format(reviewCount)) جهات تحتاج مراجعة", "\(reviewCount) entities need review"))
                    .font(.caption).foregroundStyle(.orange)
            }
            let unclassified = entities.filter { $0.category == .unresolved }.count
            if unclassified > 0 {
                Text(tr("\(Num.format(unclassified)) جهات لم يُحدد مصرفها بعد", "\(unclassified) entities have no determined bank yet"))
                    .font(.caption).foregroundStyle(.orange)
            }
        }.padding(.vertical, 8)
    }
    private func importReferences() {
        guard !importingReferences, !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
            referenceError = tr("انتظر انتهاء العملية الحالية ثم حاول مجدداً.", "Wait for the current operation, then try again.")
            return
        }
        FilePicker.present(types: [.zip], multiple: false) { urls in
            guard let url = urls.first else { return }
            importingReferences = true
            referenceError = nil
            Task {
                defer { importingReferences = false }
                do {
                    let data = try await Task.detached(priority: .userInitiated) {
                        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                        guard size > 0, size <= 10_000_000 else { throw TransferReferencePack.Failure.oversized }
                        _ = try TransferReferencePack.validate(url)
                        return try Data(contentsOf: url)
                    }.value
                    guard !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
                        referenceError = tr("انتظر انتهاء العملية الحالية ثم أعد اختيار الحزمة.", "Wait for the current operation, then select the package again.")
                        return
                    }
                    referenceData = data
                    BackupManager.shared.markChanged()
                } catch { referenceError = error.localizedDescription }
            }
        }
    }
    var body: some View {
        List {
            if let error { Section { Text(error).foregroundStyle(.red) } }
            if let month {
                Section { LabeledContent(tr("الشهر المختار", "Selected month"), value: month.name) }
            }
            if !linksValid { Section { Text(tr("تعذّرت قراءة روابط الحسابات؛ لا يمكن اعتماد التصنيف أو التصدير حتى استعادة الروابط.", "Account links could not be read; classification and export are unavailable until they are restored.")).foregroundStyle(.red) } }
            if !categoriesValid { Section { Text(tr("تعذّرت قراءة تصنيفات الجهات المحفوظة؛ لم تُستبدل البيانات، واستعدها قبل التصدير.", "Saved entity classifications could not be read; the data was not replaced. Restore it before export.")).foregroundStyle(.red) } }
            if loadedKey != loadKey && month != nil {
                ProgressView().frame(maxWidth: .infinity)
            } else if month == nil {
                ContentUnavailableView(tr("لا توجد أشهر محفوظة", "No saved months"), systemImage: "calendar")
            } else {
                Section {
                    ForEach([TransferCategory.rafidain, .otherBanks, .anbar]) { category in
                        categoryRow(category)
                    }
                } header: { Text(month?.name ?? "") }
            }
        }
        .navigationTitle(tr("ملفات التحويل", "Transfer files"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    ForEach(months) { candidate in
                        Button { selectedMonth = candidate.name } label: {
                            if candidate.name == month?.name {
                                Label(candidate.name, systemImage: "checkmark")
                            } else { Text(candidate.name) }
                        }
                    }
                } label: { Image(systemName: "calendar").accessibilityLabel(tr("اختيار الشهر", "Choose month")) }
                .disabled(months.isEmpty).glassButtonStyle().buttonBorderShape(.circle)
            }
            ToolbarItem(placement: .topBarLeading) {
                Button(tr("إعدادات التحويل", "Transfer settings"), systemImage: "slider.horizontal.3") { showSettings = true }
            }
        }
        .sheet(isPresented: $showSettings) {
            NavigationStack {
                Form {
                    Section(tr("الجهة الدافعة", "Payer")) {
                        TextField(tr("اسم الجهة الدافعة", "Payer name"), text: $payerName)
                        TextField("IBAN", text: $payerIBAN).textInputAutocapitalization(.characters).autocorrectionDisabled()
                    }
                    Section {
                        Button { importReferences() } label: {
                            if importingReferences { ProgressView() }
                            else { Label(tr("استيراد مراجع الكتب", "Import letter references"), systemImage: "doc.badge.plus") }
                        }.disabled(importingReferences)
                        if !referenceData.isEmpty { Label(tr("مراجع الكتب محفوظة", "Letter references saved"), systemImage: "checkmark.circle.fill").foregroundStyle(.green) }
                        if let referenceError { Text(referenceError).font(.footnote).foregroundStyle(.red) }
                        NavigationLink { TransferLetterSettingsView() } label: { Label(tr("تخصيص الكتب", "Customize letters"), systemImage: "slider.horizontal.3") }
                    } footer: { Text(tr("تُحفظ المراجع ضمن النسخة الاحتياطية.", "References are included in the backup.")) }
                    if let figures, error == nil {
                        Section {
                            NavigationLink {
                                TransferIssuanceView(month: month?.name ?? "", figures: figures, entities: entities, sourceKey: loadKey,
                                    accounts: accountData, categories: categoriesData, links: payrollLinksData)
                            } label: { Label(tr("تدقيق الكتب الصادرة", "Issued letter review"), systemImage: "checklist") }
                        }
                    }
                }
                .navigationTitle(tr("إعدادات التحويل", "Transfer settings"))
                .toolbar { ToolbarItem(placement: .confirmationAction) { Button(tr("تم", "Done")) { showSettings = false } } }
            }
        }
        .onChange(of: payerName) { BackupManager.shared.markChanged() }
        .onChange(of: payerIBAN) { BackupManager.shared.markChanged() }
        .onAppear { draftCoverage.discardIfDifferent(draftRevision) }
        .onChange(of: draftRevision) { draftCoverage.discardIfDifferent(draftRevision) }
        .task(id: "\(loadKey)|\(bankCache.revision)") {
            let key = loadKey
            let revision = bankCache.revision
            guard loadedKey != key || loadedRevision != revision || error != nil else { return }
            guard let name = month?.name else { groups = []; loadedKey = key; loadedRevision = revision; return }
            loadedKey = ""; error = nil; groups = []; missing = [:]; figures = nil; figuresError = nil
            let container = SharedModel.container
            let work = Task.detached(priority: .userInitiated) { try await TransferDataEngine(modelContainer: container).read(month: name) }
            do {
                let value = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
                guard !Task.isCancelled, key == loadKey, revision == bankCache.revision else { return }
                groups = value.0; missing = value.1
                do {
                    let figureWork = Task.detached(priority: .userInitiated) { try await TransferLetterFigureEngine(modelContainer: container).figures(month: name) }
                    let result = try await withTaskCancellationHandler { try await figureWork.value } onCancel: { figureWork.cancel() }
                    guard !Task.isCancelled, key == loadKey, revision == bankCache.revision else { return }
                    figures = result
                } catch is CancellationError { return }
                catch { guard !Task.isCancelled, key == loadKey, revision == bankCache.revision else { return }; figuresError = error.localizedDescription }
                loadedKey = key; loadedRevision = revision
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled, key == loadKey, revision == bankCache.revision else { return }; self.error = error.localizedDescription; loadedKey = key; loadedRevision = revision }
        }
    }
}

private struct TransferAnbarContext {
    let figures: TransferLetterFigures
    let entities: [TransferEntity]
    let sourceKey: String
    let accounts: Data
    let categories: Data
    let links: Data
    let customization: Data
    let references: Data
    let payerName: String
    let payerIBAN: String
    let boldFont: Data
    let regularFont: Data
}

private struct TransferCategoryView: View {
    let category: TransferCategory
    @Binding var allEntities: [TransferEntity]
    @Binding var accounts: [SavedExecutionAccount]
    let month: String
    let sourceKey: String
    let sourceRevision: Int
    let figures: TransferLetterFigures?
    let figuresError: String?
    let changeCategory: (TransferCategory, TransferEntity) -> Bool
    let setTransferAccount: (TransferEntity, String?) -> Bool
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse)]) private var months: [PayrollMonth]
    @AppStorage(ExecutionAccountStore.storageKey) private var currentAccounts = Data()
    @AppStorage(TransferRules.settingsKey) private var currentCategories = Data()
    @AppStorage("transferPayrollAccountLinks") private var currentPayrollLinks = Data()
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var currentCustomization = Data()
    @AppStorage(TransferReferencePack.settingsKey) private var currentReferences = Data()
    @AppStorage(TransferHeaderFont.boldSettingsKey) private var currentBoldFont = Data()
    @AppStorage(TransferHeaderFont.regularSettingsKey) private var currentRegularFont = Data()
    @AppStorage("bankStatementAccountName") private var payerName = ""
    @AppStorage("bankStatementIBAN") private var payerIBAN = ""
    @AppStorage("reportMonth") private var selectedMonth = ""
    @State private var bankCache = BankStatementCache.shared
    @State private var importCenter = ImportCenter.shared
    @State private var error: String?
    @State private var linkEntity: TransferEntity?
    @State private var linkAccountsSnapshot = Data()
    @State private var linkCategoriesSnapshot = Data()
    @State private var linkMappingsSnapshot = Data()
    private var entities: [TransferEntity] { allEntities.filter { $0.category == category } }
    private var unresolved: [TransferEntity] { allEntities.filter { $0.category == .unresolved } }
    private var unresolvedCount: Int { unresolved.count }
    private var payrollComplete: Bool { figures?.month == month && figuresError == nil }
    private var allIssues: Int { entities.filter { !TransferRules.issues($0).isEmpty }.count }
    private var anbarContext: TransferAnbarContext? {
        guard let figures, figures.month == month else { return nil }
        return TransferAnbarContext(figures: figures, entities: allEntities, sourceKey: sourceKey,
            accounts: currentAccounts, categories: currentCategories, links: currentPayrollLinks,
            customization: currentCustomization, references: currentReferences, payerName: payerName,
            payerIBAN: payerIBAN, boldFont: currentBoldFont, regularFont: currentRegularFont)
    }
    private var sourceIsCurrent: Bool {
        let record = months.first { $0.name == month }
        let active = months.first { $0.name == selectedMonth } ?? months.first
        return record != nil && active?.name == month && sourceRevision == bankCache.revision &&
            "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    private var canExport: Bool {
        payrollComplete && sourceIsCurrent && linksValidHere && categoriesValidHere &&
            !BackupManager.shared.isWorking && !ImportCenter.shared.isBusy && !StaffImportState.shared.isImporting &&
            !entities.isEmpty && unresolvedCount == 0 && allIssues == 0 && TransferRules.safeField(payerName) && TransferRules.validIBAN(payerIBAN)
    }
    var body: some View {
        List {
            Section {
                LabeledContent(tr("الشهر", "Month"), value: month)
                if sourceIsCurrent && linksValidHere && categoriesValidHere {
                    LabeledContent(category == .anbar ? tr("عدد جهات الاستقطاع", "Deduction entities") : tr("عدد الجهات", "Entities"), value: Num.format(entities.count))
                    LabeledContent(category == .anbar ? tr("مجموع جهات الاستقطاع", "Deduction entity total") : tr("المجموع", "Total"), value: Money.format(entities.reduce(0) { $0 + $1.group.total }))
                }
                if !linksValidHere || !categoriesValidHere {
                    Text(tr("تعذّر قراءة الروابط أو التصنيفات المحفوظة؛ استعدها قبل اعتماد المجموعة.", "Saved account links or categories could not be read; restore them before using this group.")).foregroundStyle(.red)
                }
                if BackupManager.shared.isWorking || importCenter.isBusy || StaffImportState.shared.isImporting {
                    Text(tr("انتظر انتهاء النسخ أو الاستيراد قبل التصدير.", "Wait for backup or import to finish before exporting.")).foregroundStyle(.orange)
                }
                if category == .rafidain || category == .otherBanks {
                    LabeledContent(tr("الجهة الدافعة", "Payer"), value: payerName.isEmpty ? tr("غير محددة", "Not set") : payerName)
                    if !payerIBAN.isEmpty { LabeledContent("IBAN", value: payerIBAN).privacySensitive() }
                    if !sourceIsCurrent { Text(tr("تغيّرت البيانات؛ ارجع إلى قائمة ملفات التحويل لإعادة تحميلها.", "Data changed; return to transfer files to reload.")).foregroundStyle(.orange) }
                    if !payrollComplete { Text(figuresError ?? tr("تعذّر تحميل استقطاعات الرواتب للشهر المختار.", "Could not load payroll deductions for the selected month.")).foregroundStyle(.orange) }
                    if !TransferRules.safeField(payerName) { Text(tr("أدخل اسم الجهة الدافعة من إعدادات ملفات التحويل.", "Enter the payer name in transfer settings.")).foregroundStyle(.orange) }
                    if !TransferRules.validIBAN(payerIBAN) { Text(tr("أدخل IBAN صالحاً للجهة الدافعة من إعدادات ملفات التحويل.", "Enter a valid payer IBAN in transfer settings.")).foregroundStyle(.orange) }
                    if unresolvedCount > 0 { Text(tr("\(Num.format(unresolvedCount)) جهة لم يُحدد مصرفها؛ اربطها من القائمة أدناه قبل التصدير.", "\(unresolvedCount) entities have no determined bank; link them below before export.")).foregroundStyle(.orange) }
                }
            }
            if category == .anbar {
                if !sourceIsCurrent {
                    Section { Text(tr("تغيّرت البيانات؛ ارجع إلى ملفات التحويل لإعادة التحميل.", "Data changed; return to transfer files to reload.")).foregroundStyle(.orange) }
                }
                if let context = anbarContext {
                    anbarBooks(context)
                        .disabled(!sourceIsCurrent)
                } else {
                    Section {
                        Label(figuresError ?? tr("تعذّر تحميل مبالغ الرواتب للشهر المختار.", "Could not load payroll amounts for the selected month."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
                        Label(tr("شركة عراق الجوال", "Iraq Al-Jawwal Company"), systemImage: "doc.text").foregroundStyle(.secondary)
                        ForEach(TransferLetterDraftKind.allCases.filter { $0 != .rafidain && $0 != .otherBanks }) { kind in
                            Label(kind.title, systemImage: "doc.text").foregroundStyle(.secondary)
                        }
                    } header: { Text(tr("الكتب المنفردة", "Individual letters")) }
                }
            }
            if category == .rafidain || category == .otherBanks {
                TransferGroupIssuanceSection(category: category, month: month, entities: entities,
                    sourceKey: sourceKey, sourceRevision: sourceRevision, accounts: currentAccounts,
                    categories: currentCategories, accountLinks: currentPayrollLinks,
                    payerName: payerName, payerIBAN: payerIBAN, canConfirm: canExport)
                groupBook
                Section {
                    Button(tr("تصدير ملف التحويل CSV", "Export transfer CSV"), systemImage: "square.and.arrow.up") { export() }
                        .disabled(!canExport)
                    if allIssues > 0 { Text(tr("عالج بيانات الجهات المعلّمة قبل التصدير.", "Resolve flagged entities before exporting.")).font(.footnote).foregroundStyle(.secondary) }
                }
            }
            if !unresolved.isEmpty {
                Section {
                    ForEach(unresolved) { entity in
                        VStack(alignment: .leading, spacing: 7) {
                            Text(entity.group.title).font(.headline)
                            Text(Money.format(entity.group.total)).font(.subheadline.weight(.semibold)).monospacedDigit()
                            if let account = entity.account {
                                Text(account.branch).font(.caption).foregroundStyle(.secondary)
                                Menu {
                                    ForEach([TransferCategory.rafidain, .otherBanks, .anbar]) { option in
                                        Button(option.title) { chooseCategory(option, for: entity) }
                                    }
                                } label: { Label(tr("تحديد مجموعة التحويل", "Set transfer category"), systemImage: "arrow.triangle.branch") }
                                .disabled(!sourceIsCurrent || !linksValidHere || !categoriesValidHere)
                            }
                            Button(tr("ربط حساب الجهة", "Link beneficiary account"), systemImage: "link") { beginLink(entity) }
                                .disabled(!sourceIsCurrent || !linksValidHere || !categoriesValidHere)
                            ForEach(TransferRules.issues(entity), id: \.self) { issue in Text(issue).font(.caption).foregroundStyle(.orange) }
                        }.padding(.vertical, 5)
                    }
                } header: { Text(tr("جهات لم يُحدد مصرفها", "Entities without a determined bank")) } footer: { Text(tr("تظهر هذه الجهات للمراجعة في المجموعات الثلاث حتى ربط حسابها أو تحديد مصرفها؛ لا تدخل في المجموع إلى أن يُحدد تصنيفها.", "These entities appear in all three groups until their account or bank is determined; they are not included in a group total until classified.")) }
            }
            if !unresolved.isEmpty || allIssues > 0 {
                Section {
                    Button(tr("استيراد ملف حسابات الجهات", "Import beneficiary accounts"), systemImage: "square.and.arrow.down") { importCenter.pickAccounts() }
                        .disabled(importCenter.isBusy || BackupManager.shared.isWorking || StaffImportState.shared.isImporting)
                    NavigationLink { ExecutionAccountsView() } label: {
                        Label(tr("الحسابات المحفوظة", "Saved accounts"), systemImage: "building.columns")
                    }
                } footer: { Text(tr("اختر ملف الحسابات الصحيح، ثم راجع نتيجة فحصه قبل الاستيراد.", "Choose the correct accounts file and review it before import.")) }
            }
            Section {
                NavigationLink {
                    entityList
                } label: {
                    HStack {
                        Label(tr("الجهات", "Entities"), systemImage: "building.2")
                        Spacer()
                        Text(Num.format(entities.count)).foregroundStyle(.secondary)
                    }
                }
            }

        }
        .navigationTitle(category.title).navigationBarTitleDisplayMode(.inline)
        .sheet(item: $linkEntity) { entity in
            NavigationStack {
                TransferAccountLinkPicker(entity: entity, accounts: accounts,
                    selectedKey: (try? TransferAccountLinks.decode(currentPayrollLinks)).map { links in
                        entity.payrollRecipient.flatMap { links[$0] } ?? (entity.payrollRecipient == nil ? TransferAccountLinks.selectedKey(title: entity.group.title, links: links) : "")
                    },
                    save: { key in
                        sourceIsCurrent && currentAccounts == linkAccountsSnapshot &&
                            currentCategories == linkCategoriesSnapshot && currentPayrollLinks == linkMappingsSnapshot &&
                            setTransferAccount(entity, key)
                    })
            }
        }
        .alert(tr("تعذّرت العملية", "Operation failed"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
    }
    private var entityList: some View {
        List {
            Section {
                if entities.isEmpty {
                    Text(tr("لا توجد جهات في هذه المجموعة.", "No entities in this group.")).foregroundStyle(.secondary)
                }
                ForEach(entities) { entity in
                    DisclosureGroup {
                        entityDetails(entity)
                    } label: {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(entity.group.title).font(.subheadline.weight(.semibold))
                            HStack {
                                Text(Money.format(entity.group.total)).font(.subheadline.weight(.semibold)).monospacedDigit()
                                Spacer(minLength: 8)
                                if let account = entity.account {
                                    Text(account.branch).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                }
                            }
                            if !TransferRules.issues(entity).isEmpty {
                                Label(tr("تحتاج مراجعة", "Needs review"), systemImage: "exclamationmark.triangle")
                                    .font(.caption).foregroundStyle(.orange)
                            }
                        }.padding(.vertical, 2)
                    }
                }
            } header: {
                Text(tr("\(Num.format(entities.count)) جهة · \(month)", "\(Num.format(entities.count)) entities · \(month)"))
            } footer: {
                Text(tr("اضغط على الجهة لعرض الحساب والتصنيف والربط.", "Open an entity to view its account, category and link."))
            }
        }
        .navigationTitle(tr("الجهات", "Entities"))
        .navigationBarTitleDisplayMode(.inline)
    }
    @ViewBuilder private func entityDetails(_ entity: TransferEntity) -> some View {
        if let account = entity.account {
            if entity.payrollRecipient == nil && ArabicText.normalize(account.entity) != ArabicText.normalize(entity.group.title) {
                Text(tr("الحساب المرتبط: \(account.entity)", "Linked account: \(account.entity)"))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Text(account.branch).font(.caption).foregroundStyle(.secondary)
            if !account.iban.isEmpty { Text(account.iban).font(.caption.monospaced()).foregroundStyle(.secondary).privacySensitive() }
            if let local = account.localAccount { Text(tr("حساب محلي: \(local)", "Local account: \(local)")).font(.caption.monospaced()).foregroundStyle(.secondary).privacySensitive() }
            if !account.swift.isEmpty { Text("SWIFT: \(account.swift)").font(.caption).foregroundStyle(.secondary) }
            Menu {
                ForEach([TransferCategory.rafidain, .otherBanks, .anbar]) { option in
                    Button(option.title) { chooseCategory(option, for: entity) }
                }
            } label: { Label(tr("تحديد مجموعة التحويل", "Set transfer category"), systemImage: "arrow.triangle.branch") }
            .disabled(!sourceIsCurrent || !linksValidHere || !categoriesValidHere)
        } else if entity.isDirectAnbarBank {
            Text(tr("يُدرج في كتاب الرافدين فرع الأنبار؛ لا يحتاج ملف CSV.", "Included in the Rafidain Anbar letter; no CSV is needed."))
                .font(.caption).foregroundStyle(.secondary)
        } else {
            Text(tr("لم يُعثر على الحساب؛ اربط جهة الاستقطاع بحساب محفوظ أو استورد حسابها.", "No account found; link this deduction entity to a saved account or import it."))
                .font(.caption).foregroundStyle(.orange)
        }
        if !entity.isDirectAnbarBank {
            Button(tr("ربط حساب الجهة", "Link beneficiary account"), systemImage: "link") { beginLink(entity) }
                .disabled(!sourceIsCurrent || !linksValidHere || !categoriesValidHere)
        }
        ForEach(TransferRules.issues(entity), id: \.self) { issue in
            Text(issue).font(.caption).foregroundStyle(.orange)
        }
    }
    private func chooseCategory(_ category: TransferCategory, for entity: TransferEntity) {
        guard sourceIsCurrent, linksValidHere, categoriesValidHere,
              allEntities.contains(where: { $0.id == entity.id && $0.category == entity.category }),
              changeCategory(category, entity) else {
            error = tr("تعذّر حفظ التصنيف؛ تحقق من مطابقة المجموعة لمصرف الحساب وسلامة البيانات، وانتظر انتهاء النسخ أو الاستيراد.", "Could not save the category; check that it matches the account bank and wait for backup or import to finish.")
            return
        }
    }
    private func beginLink(_ entity: TransferEntity) {
        guard sourceIsCurrent, linksValidHere, categoriesValidHere else { return }
        linkAccountsSnapshot = currentAccounts
        linkCategoriesSnapshot = currentCategories
        linkMappingsSnapshot = currentPayrollLinks
        linkEntity = entity
    }
    private var linksValidHere: Bool { (try? TransferAccountLinks.decode(currentPayrollLinks)) != nil }
    private var categoriesValidHere: Bool { currentCategories.isEmpty || (try? JSONDecoder().decode([String: String].self, from: currentCategories)) != nil }
    @ViewBuilder private var groupBook: some View {
        Section {
            if let context = anbarContext {
                let kind: TransferLetterDraftKind = category == .rafidain ? .rafidain : .otherBanks
                NavigationLink {
                    TransferLetterDraftView(kind: kind, month: context.figures.month, figures: context.figures,
                        entities: context.entities, sourceKey: context.sourceKey, sourceAccounts: context.accounts,
                        sourceCategories: context.categories, sourceLinks: context.links, sourceCustomization: context.customization,
                        references: context.references, payerName: context.payerName, payerIBAN: context.payerIBAN,
                        boldFont: context.boldFont, regularFont: context.regularFont)
                } label: { Label(tr("كتاب المجموعة", "Group letter"), systemImage: "doc.text.magnifyingglass") }
                .disabled(!sourceIsCurrent || !linksValidHere || !categoriesValidHere)
            } else {
                Label(figuresError ?? tr("تعذّر تحميل بيانات الكتاب للشهر المختار.", "Could not load letter data for the selected month."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
            }
        } footer: { Text(tr("راجع الكتاب ومبالغه على الجهاز، ثم صدّر PDF من المعاينة.", "Review the letter and amounts on the device, then export the PDF from the preview.")) }
    }
    @ViewBuilder private func anbarBooks(_ context: TransferAnbarContext) -> some View {
        if context.figures.missingContractNominal > 0 {
            Section {
                NavigationLink {
                    ContractNominalReviewView(month: context.figures.month, sourceKey: sourceKey, expected: context.figures.missingContractNominal)
                } label: {
                    Label(tr("\(Num.format(context.figures.missingContractNominal)) عقد بلا اسمي مقروء · مراجعة السجلات", "\(context.figures.missingContractNominal) contracts lack readable nominal pay · review records"), systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.orange)
                }
            } footer: { Text(tr("يمكن مراجعة المبلغ وتجاوز التنبيه داخل كتاب ضمان العقود.", "You can review the amount and accept the warning inside the contract letter.")) }
        }
        Section {
            NavigationLink {
                TransferCompanyLetterView(figures: context.figures, sourceKey: context.sourceKey,
                    payerIBAN: context.payerIBAN, references: context.references)
            } label: {
                VStack(alignment: .leading, spacing: 4) {
                    Label(tr("شركة عراق الجوال", "Iraq Al-Jawwal Company"), systemImage: "doc.text")
                    Text(tr("من بند شركة عراق الجوال · \(Money.format(context.figures.foodCompany))", "From company payroll item · \(Money.format(context.figures.foodCompany))"))
                        .font(.caption).foregroundStyle(.secondary)
                    if context.figures.foodCompany <= 0 { Text(tr("لا مبلغ متاح؛ راجع بيانات الشهر", "Amount unavailable; review this month")).font(.caption).foregroundStyle(.orange) }
                }
            }
            ForEach(TransferLetterDraftKind.allCases.filter { $0 != .rafidain && $0 != .otherBanks }) { kind in
                NavigationLink {
                    TransferLetterDraftView(kind: kind, month: context.figures.month, figures: context.figures,
                        entities: context.entities, sourceKey: context.sourceKey, sourceAccounts: context.accounts,
                        sourceCategories: context.categories, sourceLinks: context.links, sourceCustomization: context.customization,
                        references: context.references, payerName: context.payerName, payerIBAN: context.payerIBAN,
                        boldFont: context.boldFont, regularFont: context.regularFont)
                } label: {
                    VStack(alignment: .leading, spacing: 4) {
                        Label(kind.title, systemImage: "doc.text")
                        bookSource(kind, figures: context.figures)
                    }
                }
            }
        } header: { Text(tr("الكتب المنفردة", "Individual letters")) } footer: { Text(tr("المبالغ حسب بيانات الشهر. اختر جهة الاستقطاع للكتب المرتبطة بها، وراجع الحساب والمبلغ قبل اعتماد أي كتاب. المعاينات قيد التحقق.", "Amounts come from this month. Select the deduction entity for linked letters and review accounts and amounts. Previews are under verification.")) }
        Section {
            NavigationLink {
                TransferListsView(month: month, sourceKey: sourceKey, entities: context.entities)
            } label: { Label(tr("القوائم", "Lists"), systemImage: "list.bullet.rectangle") }
        }
    }
    @ViewBuilder private func bookSource(_ kind: TransferLetterDraftKind, figures: TransferLetterFigures) -> some View {
        if kind.requiresEntity {
            Text(tr("من استقطاعات الشهر · اختر الجهة", "From monthly deductions · choose entity")).font(.caption).foregroundStyle(.secondary)
        } else {
            let value: Double? = switch kind {
            case .foodTax: figures.foodTax
            case .taxSocial: figures.treasuryTotal
            case .contracts: figures.contractTotal
            case .cash: figures.payrollCash
            default: nil
            }
            if kind == .contracts && figures.missingContractNominal > 0 {
                Text(tr("الاسمي ناقص في \(Num.format(figures.missingContractNominal)) عقد؛ راجع المبلغ داخل الكتاب", "Nominal pay is missing for \(figures.missingContractNominal) contracts; review the amount in the letter")).font(.caption).foregroundStyle(.orange)
            } else if let value, value > 0 {
                Text(tr("من بيانات الرواتب · \(Money.format(value))", "From payroll · \(Money.format(value))")).font(.caption).foregroundStyle(.secondary)
            } else {
                Text(tr("لا مبلغ متاح؛ راجع بيانات الشهر", "Amount unavailable; review this month")).font(.caption).foregroundStyle(.orange)
            }
        }
    }
    private func export() {
        guard canExport else { return }
        do {
            let data = try TransferRules.csv(entities, category: category, month: month, payer: payerName, payerIBAN: payerIBAN)
            _ = try TransferGroupCSVCheck.validate(data, entities: entities, category: category, month: month, payer: payerName, payerIBAN: payerIBAN)
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("transfer-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let url = folder.appendingPathComponent(category == .rafidain ? "Rafidain.csv" : "OtherBanks.csv")
            do {
                try data.write(to: url, options: [.atomic, .completeFileProtection])
                guard try Data(contentsOf: url) == data else { throw TransferGroupCSVCheck.Failure.mismatch }
                ActivityShare.present([url])
            } catch {
                try? FileManager.default.removeItem(at: folder)
                throw error
            }
        } catch { self.error = error.localizedDescription }
    }
}
