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
        case .anbar: tr("الرافدين · فرع الأنبار", "Rafidain · Anbar branch")
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
        guard let account = entity.account else { return [tr("الحساب غير محفوظ", "Account is missing")] }
        if entity.duplicateSource { result.append(tr("البند موجود أيضاً في ملف الجهات؛ راجع مصدره لتجنب التكرار", "This item also exists in execution deductions; review for duplication")) }
        if entity.category == .anbar {
            if individualLocalAccount(account) == nil { result.append(tr("حساب الجهة غير صالح أو غير مطابق", "Invalid or inconsistent beneficiary account")) }
        } else {
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
    @State private var bookStore = OfficialBookStore.shared
    @State private var draftCoverage = TransferDraftCoverage.shared

    private var month: PayrollMonth? { months.first { $0.name == selectedMonth } ?? months.first }
    private var loadKey: String { "\(String(describing: month?.persistentModelID))|\(month?.name ?? "")|\(month?.importedAt.timeIntervalSince1970 ?? 0)" }
    private var categories: [String: String] { (try? JSONDecoder().decode([String: String].self, from: categoriesData)) ?? [:] }
    private var payrollLinks: [String: String] { (try? TransferAccountLinks.decode(payrollLinksData)) ?? [:] }
    private var linksValid: Bool { (try? TransferAccountLinks.decode(payrollLinksData)) != nil }
    private var accounts: [SavedExecutionAccount] { (try? ExecutionAccountStore.decode(accountData)) ?? [] }
    private var draftRevision: TransferDraftRevision {
        TransferDraftRevision(sourceKey: loadKey, accounts: accountData, categories: categoriesData,
            payrollLinks: payrollLinksData, customization: customizationData, references: referenceData,
            payerName: payerName, payerIBAN: payerIBAN, boldFont: boldFontData, regularFont: regularFontData)
    }
    private var unlinkedAnbar: [TransferEntity] {
        guard let selected = month?.name, bookStore.loadError == nil else { return [] }
        let keys = Set(bookStore.books.compactMap { book -> String? in
            guard book.month == selected, FileManager.default.fileExists(atPath: OfficialBookFiles.file(book.id).path),
                  let key = book.entityKey, !key.isEmpty else { return nil }
            return key
        })
        return TransferArchiveCoverage.unlinked(entities: entities, linkedEntityKeys: keys)
    }
    private var entities: [TransferEntity] {
        let lookup = accounts.reduce(into: [String: SavedExecutionAccount]()) { $0[$1.key] = $1 }
        let deductions = groups.map { group in
            let account = linksValid ? TransferAccountLinks.resolve(title: group.title, accounts: lookup, links: payrollLinks) : nil
            let key = categoryKey(group.title, account)
            let category = categories[key].flatMap(TransferCategory.init(rawValue:)) ?? TransferRules.classify(account)
            let explicit = payrollLinks[TransferAccountLinks.sourceKey(group.title)] != nil
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
    private func setCategory(_ category: TransferCategory, for entity: TransferEntity) {
        var saved = categories
        let key = categoryKey(entity.group.title, entity.account)
        if category == .unresolved { saved.removeValue(forKey: key) }
        else { saved[key] = category.rawValue }
        if let data = try? JSONEncoder().encode(saved) {
            categoriesData = data
            BackupManager.shared.markChanged()
        }
    }
    private func setPayrollAccount(_ recipient: TransferPayrollRecipient, key: String) {
        guard var saved = try? TransferAccountLinks.decode(payrollLinksData) else { referenceError = tr("تعذّرت قراءة روابط الحسابات المحفوظة؛ لم يتغير أي ربط.", "Could not read saved account links; none were changed."); return }
        saved[recipient.rawValue] = key
        if let data = try? JSONEncoder().encode(saved) { payrollLinksData = data; BackupManager.shared.markChanged() }
    }
    private func setDeductionAccount(_ entity: TransferEntity, key: String?) -> Bool {
        guard entity.payrollRecipient == nil, var saved = try? TransferAccountLinks.decode(payrollLinksData),
              !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else { return false }
        let source = TransferAccountLinks.sourceKey(entity.group.title)
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
        let bookContext = figures.map { TransferAnbarContext(figures: $0, entities: entities, sourceKey: loadKey,
            accounts: accountData, categories: categoriesData, links: payrollLinksData,
            customization: customizationData, references: referenceData, payerName: payerName,
            payerIBAN: payerIBAN, boldFont: boldFontData, regularFont: regularFontData) }
        if category != .unresolved || !items.isEmpty {
            NavigationLink {
                TransferCategoryView(category: category, entities: items,
                    unresolvedCount: entities.filter { $0.category == .unresolved }.count,
                    month: month?.name ?? "", sourceKey: loadKey, sourceAccounts: accountData,
                    sourceCategories: categoriesData, sourcePayrollLinks: payrollLinksData,
                    payrollComplete: figures != nil, payerName: payerName, payerIBAN: payerIBAN,
                    changeCategory: setCategory, anbarContext: category == .anbar ? bookContext : nil,
                    accounts: accounts, setDeductionAccount: setDeductionAccount)
            } label: {
                categoryLabel(category, items: items)
            }
        }
    }
    @ViewBuilder private func categoryLabel(_ category: TransferCategory, items: [TransferEntity]) -> some View {
        let total = items.reduce(0.0) { $0 + $1.group.total }
        let reviewCount = items.filter { !TransferRules.issues($0).isEmpty }.count
        VStack(alignment: .leading, spacing: 7) {
            Label(category.title, systemImage: category.icon).font(.headline)
            Text(tr("\(Num.format(items.count)) جهة · \(Money.format(total))", "\(items.count) entities · \(Money.format(total))"))
                .font(.subheadline).foregroundStyle(.secondary)
            if category == .unresolved {
                Text(tr("حدّد المصرف لكل جهة قبل التصدير", "Classify each entity before export")).font(.caption).foregroundStyle(.orange)
            } else if category == .anbar {
                Text(tr("كتب منفردة", "Individual documents")).font(.caption).foregroundStyle(.secondary)
            } else if reviewCount > 0 {
                Text(tr("\(Num.format(reviewCount)) جهات تحتاج مراجعة", "\(reviewCount) entities need review"))
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
            if let figuresError { Section { Text(figuresError).foregroundStyle(.orange) } }
            if let figures, figures.missingContractNominal > 0 {
                Section {
                    NavigationLink {
                        ContractNominalReviewView(month: figures.month, sourceKey: loadKey, expected: figures.missingContractNominal)
                    } label: {
                        Label(tr("\(Num.format(figures.missingContractNominal)) عقد بلا اسمي مقروء · مراجعة السجلات", "\(Num.format(figures.missingContractNominal)) contracts without readable nominal pay · review records"), systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.orange)
                    }
                } footer: { Text(tr("يتوقف كتاب ضمان العقود حتى تُراجع بياناته؛ بقية المبالغ تُحسب من مصادرها.", "The contract guarantee letter is unavailable until its data is reviewed; other amounts use their own sources.")) }
            }
            if !linksValid { Section { Text(tr("تعذّرت قراءة روابط الحسابات؛ لا يمكن اعتماد التصنيف أو التصدير حتى استعادة الروابط.", "Account links could not be read; classification and export are unavailable until they are restored.")).foregroundStyle(.red) } }
            Section {
                Button { importReferences() } label: {
                    if importingReferences { ProgressView() }
                    else { Label(tr("استيراد مراجع الكتب", "Import letter references"), systemImage: "doc.badge.plus") }
                }.disabled(importingReferences)
                if !referenceData.isEmpty {
                    Label(tr("مراجع الكتب محفوظة", "Letter references saved"), systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                }
                if let referenceError { Text(referenceError).font(.footnote).foregroundStyle(.red) }
                NavigationLink { TransferLetterSettingsView() } label: {
                    Label(tr("تخصيص الكتب", "Customize letters"), systemImage: "slider.horizontal.3")
                }
            } footer: { Text(tr("تُحفظ المراجع الخاصة داخل النسخة الاحتياطية.", "Your private references are included in the backup.")) }
            if loadedKey != loadKey && month != nil {
                ProgressView().frame(maxWidth: .infinity)
            } else if month == nil {
                ContentUnavailableView(tr("لا توجد أشهر محفوظة", "No saved months"), systemImage: "calendar")
            } else {
                Section {
                    ForEach(TransferCategory.allCases) { category in
                        categoryRow(category)
                    }
                } header: { Text(month?.name ?? "") }
                if let figures, error == nil {
                    Section {
                        NavigationLink {
                            TransferIssuanceView(month: month?.name ?? "", figures: figures, entities: entities, sourceKey: loadKey,
                                accounts: accountData, categories: categoriesData, links: payrollLinksData)
                        } label: {
                            Label(tr("تدقيق الكتب الصادرة", "Issued letter review"), systemImage: "checklist")
                        }
                    } footer: { Text(tr("تأكيد يدوي للكتب الصادرة ومراجعة الجهات والمبالغ المسجلة.", "Manually confirm issued letters and review recorded entities and amounts.")) }
                }
                let prepared = draftCoverage.records(for: draftRevision)
                let coverageEntities = entities.filter { $0.category != .unresolved && $0.group.total.isFinite && $0.group.total > 0 }
                let missingPreviews = coverageEntities.filter { prepared[$0.id] == nil }
                let repeatedPreviews = coverageEntities.filter { (prepared[$0.id]?.count ?? 0) > 1 }
                if !coverageEntities.isEmpty {
                    Section {
                        LabeledContent(tr("جهات مصنفة لها معاينة", "Classified entities with preview"), value: "\(Num.format(coverageEntities.count - missingPreviews.count)) / \(Num.format(coverageEntities.count))")
                        if !missingPreviews.isEmpty {
                            DisclosureGroup(tr("جهات لم تُجهّز لها معاينة · \(Num.format(missingPreviews.count))", "Entities without a prepared preview · \(Num.format(missingPreviews.count))")) {
                                ForEach(missingPreviews) { entity in Text(entity.group.title).foregroundStyle(.orange) }
                            }
                        }
                        if !repeatedPreviews.isEmpty {
                            DisclosureGroup(tr("جهات ظهرت في أكثر من نوع كتاب · \(Num.format(repeatedPreviews.count))", "Entities present in more than one letter type · \(Num.format(repeatedPreviews.count))")) {
                                ForEach(repeatedPreviews) { entity in Text(entity.group.title).foregroundStyle(.red) }
                            }
                        }
                    } header: { Text(tr("تغطية معاينات الجهات", "Entity preview coverage")) } footer: { Text(tr("تشمل التغطية الجهات المصنفة ذات المبالغ الموجبة؛ تظهر غير المصنفة منفصلة. تتتبع معاينات الجلسة فقط ولا تعني إصدار كتاب، وتُصفّر عند تغيّر البيانات أو الإعدادات.", "Coverage includes classified entities with positive amounts; unclassified entities appear separately. It tracks session previews only, does not establish issuance, and resets when data or settings change.")) }
                }
                if bookStore.loadError != nil {
                    Section { Label(tr("تعذّر فحص أرشيف الكتب؛ راجعه من البيانات المحفوظة.", "Could not check the document archive; review it in saved data."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                } else if !unlinkedAnbar.isEmpty {
                    Section {
                        ForEach(unlinkedAnbar) { entity in
                            NavigationLink {
                                TransferEntityBooksView(entity: entity, month: month?.name ?? "")
                            } label: {
                                Label(entity.group.title, systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
                            }
                        }
                    } header: { Text(tr("جهات تحتاج مراجعة كتاب", "Entities needing document review")) } footer: { Text(tr("لم يُعثر على كتاب محفوظ مرتبط باسم هذه الجهات في الشهر المختار. يفحص التنبيه الأرشيف فقط ولا يثبت عدم صدور كتاب أو مطابقة مبلغه.", "No saved document linked to these entity names was found for the selected month. This checks the archive only and does not establish whether a letter was issued or its amount matched.")) }
                }
                if let figures {
                    Section {
                        ForEach(TransferPayrollRecipient.allCases) { recipient in
                            if recipient.amount(figures) > 0 {
                                Menu {
                                    ForEach(accounts) { account in
                                        Button(account.entity + (account.branch.isEmpty ? "" : " · \(account.branch)")) {
                                            setPayrollAccount(recipient, key: account.key)
                                        }
                                    }
                                } label: {
                                    VStack(alignment: .leading, spacing: 3) {
                                        Text(recipient.title).foregroundStyle(.primary)
                                        Text(accounts.first { $0.key == payrollLinks[recipient.rawValue] }?.entity ?? tr("اختر حساب الجهة", "Choose account"))
                                            .font(.caption).foregroundStyle(.secondary)
                                    }
                                }
                            }
                        }
                    } header: { Text(tr("حسابات استقطاعات الرواتب", "Payroll deduction accounts")) } footer: { Text(tr("اختر الحساب المطابق لكل بند لضمه إلى مجموعة مصرفه.", "Select the matching account for each deduction.")) }
                    Section(tr("الكتب المنفردة", "Individual letters")) {
                        NavigationLink {
                            TransferCompanyLetterView(figures: figures, sourceKey: loadKey, payerIBAN: payerIBAN, references: referenceData)
                        } label: {
                            Label(tr("كتاب شركة عراق الجوال", "Iraq Al-Jawwal letter"), systemImage: "doc.text")
                        }
                    }
                    Section {
                        ForEach(TransferLetterDraftKind.allCases) { kind in
                            NavigationLink {
                                TransferLetterDraftView(kind: kind, month: figures.month, figures: figures, entities: entities,
                                    sourceKey: loadKey, sourceAccounts: accountData, sourceCategories: categoriesData,
                                    sourceLinks: payrollLinksData, sourceCustomization: customizationData,
                                    references: referenceData, payerName: payerName, payerIBAN: payerIBAN,
                                    boldFont: boldFontData, regularFont: regularFontData)
                            } label: { Label(kind.title, systemImage: "doc.text.magnifyingglass") }
                        }
                    } header: { Text(tr("معاينات الكتب قيد التحقق", "Letter drafts under verification")) } footer: { Text(tr("المعاينات للتدقيق فقط، ولا يمكن مشاركتها حتى اكتمال الميزة.", "Previews are for review only and cannot be shared until the feature is complete.")) }
                }
                Section {
                    NavigationLink {
                        TransferCashListView(month: month?.name ?? "", sourceKey: loadKey)
                    } label: {
                        Label(tr("قائمة رواتب الكاش", "Cash salary list"), systemImage: "person.text.rectangle")
                    }
                } footer: { Text(tr("قائمة تفصيلية برواتب الدفع كاش للشهر المختار.", "A detailed list of cash salaries for the selected month.")) }
            }
        }
        .navigationTitle(tr("ملفات التحويل", "Transfer files"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    ForEach(months) { candidate in
                        Button(candidate.name) { selectedMonth = candidate.name }
                    }
                } label: { Image(systemName: "calendar").accessibilityLabel(tr("اختيار الشهر", "Choose month")) }
                .disabled(months.isEmpty).glassButtonStyle().buttonBorderShape(.circle)
            }
            ToolbarItem(placement: .topBarLeading) {
                Button(tr("بيانات الجهة الدافعة", "Payer details"), systemImage: "slider.horizontal.3") { showSettings = true }
            }
        }
        .sheet(isPresented: $showSettings) {
            NavigationStack {
                Form {
                    Section(tr("الجهة الدافعة", "Payer")) {
                        TextField(tr("اسم الجهة الدافعة", "Payer name"), text: $payerName)
                        TextField("IBAN", text: $payerIBAN).textInputAutocapitalization(.characters).autocorrectionDisabled()
                    }
                    Section { Text(tr("تُستخدم بيانات حساب كشف البنك في ملفات التحويل.", "The bank statement account is used for transfer files.")).font(.footnote).foregroundStyle(.secondary) }
                }
                .navigationTitle(tr("بيانات الجهة الدافعة", "Payer details"))
                .toolbar { ToolbarItem(placement: .confirmationAction) { Button(tr("تم", "Done")) { showSettings = false } } }
            }
        }
        .onChange(of: payerName) { BackupManager.shared.markChanged() }
        .onChange(of: payerIBAN) { BackupManager.shared.markChanged() }
        .onAppear { draftCoverage.discardIfDifferent(draftRevision) }
        .onChange(of: draftRevision) { draftCoverage.discardIfDifferent(draftRevision) }
        .task(id: loadKey) {
            let key = loadKey
            guard let name = month?.name else { groups = []; loadedKey = key; return }
            loadedKey = ""; error = nil; groups = []; missing = [:]; figures = nil; figuresError = nil
            let container = SharedModel.container
            let work = Task.detached(priority: .userInitiated) { try await TransferDataEngine(modelContainer: container).read(month: name) }
            do {
                let value = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
                guard !Task.isCancelled, key == loadKey else { return }
                groups = value.0; missing = value.1
                do {
                    let figureWork = Task.detached(priority: .userInitiated) { try await TransferLetterFigureEngine(modelContainer: container).figures(month: name) }
                    let result = try await withTaskCancellationHandler { try await figureWork.value } onCancel: { figureWork.cancel() }
                    guard !Task.isCancelled, key == loadKey else { return }
                    figures = result
                } catch is CancellationError { return }
                catch { guard !Task.isCancelled, key == loadKey else { return }; figuresError = error.localizedDescription }
                loadedKey = key
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled, key == loadKey else { return }; self.error = error.localizedDescription; loadedKey = key }
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
    let entities: [TransferEntity]
    let unresolvedCount: Int
    let month: String
    let sourceKey: String
    let sourceAccounts: Data
    let sourceCategories: Data
    let sourcePayrollLinks: Data
    let payrollComplete: Bool
    let payerName: String
    let payerIBAN: String
    let changeCategory: (TransferCategory, TransferEntity) -> Void
    let anbarContext: TransferAnbarContext?
    let accounts: [SavedExecutionAccount]
    let setDeductionAccount: (TransferEntity, String?) -> Bool
    @Query private var months: [PayrollMonth]
    @AppStorage(ExecutionAccountStore.storageKey) private var currentAccounts = Data()
    @AppStorage(TransferRules.settingsKey) private var currentCategories = Data()
    @AppStorage("transferPayrollAccountLinks") private var currentPayrollLinks = Data()
    @AppStorage("bankStatementAccountName") private var currentPayerName = ""
    @AppStorage("bankStatementIBAN") private var currentPayerIBAN = ""
    @State private var error: String?
    @State private var linkEntity: TransferEntity?
    private var allIssues: Int { entities.filter { !TransferRules.issues($0).isEmpty }.count }
    private var sourceIsCurrent: Bool {
        let record = months.first { $0.name == month }
        return record != nil && currentAccounts == sourceAccounts && currentCategories == sourceCategories && currentPayrollLinks == sourcePayrollLinks
            && currentPayerName == payerName && currentPayerIBAN == payerIBAN
            && "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    private var canExport: Bool { payrollComplete && sourceIsCurrent && !entities.isEmpty && unresolvedCount == 0 && allIssues == 0 && TransferRules.safeField(payerName) && TransferRules.validIBAN(payerIBAN) }
    var body: some View {
        List {
            Section {
                LabeledContent(tr("الشهر", "Month"), value: month)
                LabeledContent(category == .anbar ? tr("عدد جهات الاستقطاع", "Deduction entities") : tr("عدد الجهات", "Entities"), value: Num.format(entities.count))
                LabeledContent(category == .anbar ? tr("مجموع جهات الاستقطاع", "Deduction entity total") : tr("المجموع", "Total"), value: Money.format(entities.reduce(0) { $0 + $1.group.total }))
                if category == .rafidain || category == .otherBanks {
                    LabeledContent(tr("الجهة الدافعة", "Payer"), value: payerName.isEmpty ? tr("غير محددة", "Not set") : payerName)
                    if !payerIBAN.isEmpty { LabeledContent("IBAN", value: payerIBAN).privacySensitive() }
                    if !sourceIsCurrent { Text(tr("تغيّرت البيانات؛ ارجع إلى قائمة ملفات التحويل لإعادة تحميلها.", "Data changed; return to transfer files to reload.")).foregroundStyle(.orange) }
                    if !payrollComplete { Text(tr("استقطاعات الرواتب غير مكتملة؛ ارجع إلى ملفات التحويل وراجع البيانات.", "Payroll deductions are incomplete; return to transfer files and review them.")).foregroundStyle(.orange) }
                    if !TransferRules.safeField(payerName) { Text(tr("أدخل اسم الجهة الدافعة من إعدادات ملفات التحويل.", "Enter the payer name in transfer settings.")).foregroundStyle(.orange) }
                    if !TransferRules.validIBAN(payerIBAN) { Text(tr("أدخل IBAN صالحاً للجهة الدافعة من إعدادات ملفات التحويل.", "Enter a valid payer IBAN in transfer settings.")).foregroundStyle(.orange) }
                    if unresolvedCount > 0 { Text(tr("صنّف \(Num.format(unresolvedCount)) جهات قبل تصدير أي ملف.", "Classify \(unresolvedCount) entities before export.")).foregroundStyle(.orange) }
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
                        Label(tr("تعذّر تحميل مبالغ الرواتب؛ راجع بيانات الشهر في ملفات التحويل.", "Payroll amounts could not be loaded; review the month in transfer files."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
                        Label(tr("شركة عراق الجوال", "Iraq Al-Jawwal Company"), systemImage: "doc.text").foregroundStyle(.secondary)
                        ForEach(TransferLetterDraftKind.allCases.filter { $0 != .rafidain && $0 != .otherBanks }) { kind in
                            Label(kind.title, systemImage: "doc.text").foregroundStyle(.secondary)
                        }
                    } header: { Text(tr("الكتب المنفردة", "Individual letters")) }
                }
            }
            if category == .unresolved && !sourceIsCurrent {
                Section { Text(tr("تغيّر الربط؛ ارجع إلى ملفات التحويل لعرض التصنيف المحدّث.", "The link changed; return to transfer files to see the updated classification.")).foregroundStyle(.orange) }
            }
            Section(tr("الجهات", "Entities")) {
                if entities.isEmpty { Text(tr("لا توجد جهات في هذه المجموعة.", "No entities in this group.")).foregroundStyle(.secondary) }
                ForEach(entities) { entity in
                    VStack(alignment: .leading, spacing: 8) {
                        if category == .anbar {
                            NavigationLink {
                                TransferEntityBooksView(entity: entity, month: month)
                            } label: { Text(entity.group.title).font(.headline) }
                        } else { Text(entity.group.title).font(.headline) }
                        Text(Money.format(entity.group.total)).font(.subheadline.weight(.semibold)).monospacedDigit()
                        if let account = entity.account {
                            if entity.payrollRecipient == nil && ArabicText.normalize(account.entity) != ArabicText.normalize(entity.group.title) {
                                Text(tr("الحساب المرتبط: \(account.entity)", "Linked account: \(account.entity)")).font(.caption).foregroundStyle(.secondary)
                            }
                            Text(account.branch).font(.caption).foregroundStyle(.secondary)
                            if !account.iban.isEmpty { Text(account.iban).font(.caption.monospaced()).foregroundStyle(.secondary).privacySensitive() }
                            if let local = account.localAccount { Text(tr("حساب محلي: \(local)", "Local account: \(local)")).font(.caption.monospaced()).foregroundStyle(.secondary).privacySensitive() }
                            if !account.swift.isEmpty { Text("SWIFT: \(account.swift)").font(.caption).foregroundStyle(.secondary) }
                            Menu {
                                ForEach([TransferCategory.rafidain, .otherBanks, .anbar]) { option in
                                    Button(option.title) { changeCategory(option, entity) }
                                }
                            } label: { Label(tr("تحديد مجموعة التحويل", "Set transfer category"), systemImage: "arrow.triangle.branch").font(.caption) }
                        } else {
                            Text(tr("لم يُعثر على الحساب؛ اربط جهة الاستقطاع بحساب محفوظ أو استورد حسابها.", "No account found; link this deduction entity to a saved account or import it.")).font(.caption).foregroundStyle(.orange)
                        }
                        if entity.payrollRecipient == nil {
                            Button(tr("ربط حساب الجهة", "Link beneficiary account"), systemImage: "link") { linkEntity = entity }
                                .disabled(!sourceIsCurrent || !linksValidHere)
                        }
                        ForEach(TransferRules.issues(entity), id: \.self) { issue in Text(issue).font(.caption).foregroundStyle(.orange) }
                    }.padding(.vertical, 6)
                }
            }
            if category == .rafidain || category == .otherBanks {
                Section {
                    Button(tr("تصدير ملف التحويل", "Export transfer file"), systemImage: "square.and.arrow.up") { export() }
                        .disabled(!canExport)
                    if allIssues > 0 { Text(tr("عالج بيانات الجهات المعلّمة قبل التصدير.", "Resolve flagged entities before exporting.")).font(.footnote).foregroundStyle(.secondary) }
                }
            }
        }
        .navigationTitle(category.title).navigationBarTitleDisplayMode(.inline)
        .sheet(item: $linkEntity) { entity in
            NavigationStack {
                TransferAccountLinkPicker(entity: entity, accounts: accounts,
                    selectedKey: (try? TransferAccountLinks.decode(sourcePayrollLinks)).map { TransferAccountLinks.selectedKey(title: entity.group.title, links: $0) },
                    save: { key in sourceIsCurrent && setDeductionAccount(entity, key) })
            }
        }
        .alert(tr("تعذّر التصدير", "Export failed"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
    }
    private var linksValidHere: Bool { (try? TransferAccountLinks.decode(currentPayrollLinks)) != nil }
    @ViewBuilder private func anbarBooks(_ context: TransferAnbarContext) -> some View {
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
                Text(tr("الاسمي ناقص في \(Num.format(figures.missingContractNominal)) عقد؛ راجع السجلات", "Nominal pay is missing for \(figures.missingContractNominal) contracts; review records")).font(.caption).foregroundStyle(.orange)
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
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("transfer-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let url = folder.appendingPathComponent(category == .rafidain ? "Rafidain.csv" : "OtherBanks.csv")
            try data.write(to: url, options: [.atomic, .completeFileProtection])
            ActivityShare.present([url])
        } catch { self.error = error.localizedDescription }
    }
}

private struct TransferEntityBooksView: View {
    let entity: TransferEntity
    let month: String
    @State private var store = OfficialBookStore.shared
    private var books: [OfficialBook] { store.books.filter { $0.month == month && $0.entityKey == ArabicText.normalize(entity.group.title) } }
    var body: some View {
        List {
            Section(tr("بيانات الجهة", "Entity details")) {
                LabeledContent(tr("الشهر", "Month"), value: month)
                LabeledContent(tr("المبلغ", "Amount"), value: Money.format(entity.group.total))
                if let account = entity.account {
                    LabeledContent(tr("المصرف", "Bank"), value: account.branch)
                    if !account.iban.isEmpty { LabeledContent("IBAN", value: account.iban).privacySensitive() }
                    if let local = account.localAccount { LabeledContent(tr("الحساب المحلي", "Local account"), value: local).privacySensitive() }
                }
            }
            Section(tr("الكتب المرتبطة", "Linked documents")) {
                if books.isEmpty { Text(tr("لا توجد كتب مرتبطة بهذه الجهة لهذا الشهر.", "No documents linked to this entity for this month.")).foregroundStyle(.secondary) }
                ForEach(books) { book in NavigationLink(book.title) { OfficialBookDetailView(id: book.id) } }
            }
        }
        .navigationTitle(entity.group.title).navigationBarTitleDisplayMode(.inline)
    }
}
