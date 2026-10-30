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
        return known.contains(where: { branch.contains(ArabicText.normalize($0)) }) ? .otherBanks : .unresolved
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
    static func safeField(_ value: String) -> Bool {
        !value.isEmpty && !value.contains("|") && !value.contains("\n") && !value.contains("\r") && !value.contains("\"")
    }
    static func issues(_ entity: TransferEntity) -> [String] {
        var result: [String] = []
        guard let account = entity.account else { return [tr("الحساب غير محفوظ", "Account is missing")] }
        if entity.duplicateSource { result.append(tr("البند موجود أيضاً في ملف الجهات؛ راجع مصدره لتجنب التكرار", "This item also exists in execution deductions; review for duplication")) }
        if !validIBAN(account.iban) { result.append(tr("IBAN غير صالح", "Invalid IBAN")) }
        if !validBIC(account.swift) { result.append(tr("SWIFT غير صالح أو غير موجود", "Invalid or missing SWIFT")) }
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

    private var month: PayrollMonth? { months.first { $0.name == selectedMonth } ?? months.first }
    private var loadKey: String { "\(String(describing: month?.persistentModelID))|\(month?.name ?? "")|\(month?.importedAt.timeIntervalSince1970 ?? 0)" }
    private var categories: [String: String] { (try? JSONDecoder().decode([String: String].self, from: categoriesData)) ?? [:] }
    private var payrollLinks: [String: String] { (try? JSONDecoder().decode([String: String].self, from: payrollLinksData)) ?? [:] }
    private var accounts: [SavedExecutionAccount] { (try? ExecutionAccountStore.decode(accountData)) ?? [] }
    private var entities: [TransferEntity] {
        let lookup = accounts.reduce(into: [String: SavedExecutionAccount]()) { $0[$1.key] = $1 }
        let deductions = groups.map { group in
            let account = lookup[ArabicText.normalize(group.title)]
            let key = categoryKey(group.title, account)
            let category = categories[key].flatMap(TransferCategory.init(rawValue:)) ?? TransferRules.classify(account)
            return TransferEntity(group: group, account: account, category: category, missingAmounts: missing[group.title] ?? 0)
        }
        guard let figures else { return deductions }
        let payroll = TransferPayrollRecipient.allCases.compactMap { recipient -> TransferEntity? in
            let amount = recipient.amount(figures)
            guard amount > 0 else { return nil }
            let account = payrollLinks[recipient.rawValue].flatMap { lookup[$0] }
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
        var saved = payrollLinks
        saved[recipient.rawValue] = key
        if let data = try? JSONEncoder().encode(saved) { payrollLinksData = data; BackupManager.shared.markChanged() }
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
                        let items = entities.filter { $0.category == category }
                        if category != .unresolved || !items.isEmpty {
                            NavigationLink {
                                TransferCategoryView(category: category, entities: items, unresolvedCount: entities.filter { $0.category == .unresolved }.count,
                                    month: month?.name ?? "", sourceKey: loadKey, sourceAccounts: accountData, sourceCategories: categoriesData,
                                    sourcePayrollLinks: payrollLinksData, payrollComplete: figures != nil,
                                    payerName: payerName, payerIBAN: payerIBAN,
                                    changeCategory: setCategory)
                            } label: {
                                VStack(alignment: .leading, spacing: 7) {
                                    Label(category.title, systemImage: category.icon).font(.headline)
                                    Text(tr("\(Num.format(items.count)) جهة · \(Money.format(items.reduce(0) { $0 + $1.group.total }))", "\(items.count) entities · \(Money.format(items.reduce(0) { $0 + $1.group.total }))"))
                                        .font(.subheadline).foregroundStyle(.secondary)
                                    if category == .unresolved { Text(tr("حدّد المصرف لكل جهة قبل التصدير", "Classify each entity before export")).font(.caption).foregroundStyle(.orange) }
                                    else if category == .anbar { Text(tr("كتب منفردة", "Individual documents")).font(.caption).foregroundStyle(.secondary) }
                                    else {
                                        let n = items.filter { !TransferRules.issues($0).isEmpty }.count
                                        if n > 0 { Text(tr("\(Num.format(n)) جهات تحتاج مراجعة", "\(n) entities need review")).font(.caption).foregroundStyle(.orange) }
                                    }
                                }.padding(.vertical, 8)
                            }
                        }
                    }
                } header: { Text(month?.name ?? "") }
                if let figures {
                    Section(tr("حسابات استقطاعات الرواتب", "Payroll deduction accounts")) {
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
                    } footer: { Text(tr("اختر الحساب المطابق لكل بند لضمه إلى مجموعة مصرفه.", "Select the matching account for each deduction.")) }
                    Section(tr("الكتب المنفردة", "Individual letters")) {
                        NavigationLink {
                            TransferCompanyLetterView(figures: figures, sourceKey: loadKey, payerIBAN: payerIBAN, references: referenceData)
                        } label: {
                            Label(tr("كتاب شركة عراق الجوال", "Iraq Al-Jawwal letter"), systemImage: "doc.text")
                        }
                    }
                    Section(tr("معاينات الكتب قيد التحقق", "Letter drafts under verification")) {
                        ForEach(TransferLetterDraftKind.allCases) { kind in
                            NavigationLink {
                                TransferLetterDraftView(kind: kind, month: figures.month, figures: figures, entities: entities,
                                    sourceKey: loadKey, sourceAccounts: accountData, sourceCategories: categoriesData,
                                    sourceLinks: payrollLinksData, sourceCustomization: customizationData,
                                    references: referenceData, payerName: payerName, payerIBAN: payerIBAN)
                            } label: { Label(kind.title, systemImage: "doc.text.magnifyingglass") }
                        }
                    } footer: { Text(tr("المعاينات للتدقيق فقط، ولا يمكن مشاركتها حتى اكتمال الميزة.", "Previews are for review only and cannot be shared until the feature is complete.")) }
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
            catch { guard !Task.isCancelled, key == loadKey else { return }; error = error.localizedDescription; loadedKey = key }
        }
    }
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
    @Query private var months: [PayrollMonth]
    @AppStorage(ExecutionAccountStore.storageKey) private var currentAccounts = Data()
    @AppStorage(TransferRules.settingsKey) private var currentCategories = Data()
    @AppStorage("transferPayrollAccountLinks") private var currentPayrollLinks = Data()
    @AppStorage("bankStatementAccountName") private var currentPayerName = ""
    @AppStorage("bankStatementIBAN") private var currentPayerIBAN = ""
    @State private var error: String?
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
                LabeledContent(tr("عدد الجهات", "Entities"), value: Num.format(entities.count))
                LabeledContent(tr("المجموع", "Total"), value: Money.format(entities.reduce(0) { $0 + $1.group.total }))
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
                            Text(account.branch).font(.caption).foregroundStyle(.secondary)
                            Text(account.iban).font(.caption.monospaced()).foregroundStyle(.secondary).privacySensitive()
                            if !account.swift.isEmpty { Text("SWIFT: \(account.swift)").font(.caption).foregroundStyle(.secondary) }
                            Menu {
                                ForEach([TransferCategory.rafidain, .otherBanks, .anbar]) { option in
                                    Button(option.title) { changeCategory(option, entity) }
                                }
                            } label: { Label(tr("تحديد مجموعة التحويل", "Set transfer category"), systemImage: "arrow.triangle.branch").font(.caption) }
                        } else {
                            Text(tr("استورد حساب الجهة من الإعدادات لإضافة IBAN وSWIFT.", "Import this entity's account in Settings to add IBAN and SWIFT.")).font(.caption).foregroundStyle(.orange)
                        }
                        if category != .anbar {
                            ForEach(TransferRules.issues(entity), id: \.self) { issue in Text(issue).font(.caption).foregroundStyle(.orange) }
                        }
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
        .alert(tr("تعذّر التصدير", "Export failed"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
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
                    LabeledContent("IBAN", value: account.iban).privacySensitive()
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
