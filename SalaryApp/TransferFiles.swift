import SwiftUI
import SwiftData

/// تصنيفات حسابات جهات التنفيذ؛ فرع الأنبار لا يدخل في ملف التحويل.
nonisolated enum TransferCategory: String, CaseIterable, Identifiable, Codable {
    case rafidain, otherBanks, anbar, unresolved
    var id: String { rawValue }
    var title: String {
        switch self {
        case .rafidain: tr("الرافدين · عدا فرع الأنبار", "Rafidain · excluding Anbar branch")
        case .otherBanks: tr("بقية المصارف", "Other banks")
        case .anbar: tr("الرافدين · فرع الأنبار", "Rafidain · Anbar branch")
        case .unresolved: tr("جهات تحتاج تصنيفاً", "Entities needing classification")
        }
    }
    var icon: String { self == .anbar ? "doc.text" : self == .unresolved ? "exclamationmark.triangle" : "building.columns" }
    var prefix: String { self == .rafidain ? "30000" : "20000" }
}

nonisolated struct TransferEntity: Identifiable {
    let group: ReportGroup
    let account: SavedExecutionAccount?
    let category: TransferCategory
    let missingAmounts: Int
    var id: String { group.id }
}

nonisolated enum TransferRules {
    static let settingsKey = "transferEntityCategories"
    static func classify(_ account: SavedExecutionAccount?) -> TransferCategory {
        guard let account else { return .unresolved }
        let branch = ArabicText.normalize(account.branch)
        if branch.contains(ArabicText.normalize("الرافدين")) {
            if branch.contains(ArabicText.normalize("الانبار")) { return .anbar }
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
    static func safeField(_ value: String) -> Bool {
        !value.isEmpty && !value.contains("|") && !value.contains("\n") && !value.contains("\r") && !value.contains("\"")
    }
    static func issues(_ entity: TransferEntity) -> [String] {
        var result: [String] = []
        guard let account = entity.account else { return [tr("الحساب غير محفوظ", "Account is missing")] }
        if !validIBAN(account.iban) { result.append(tr("IBAN غير صالح", "Invalid IBAN")) }
        if !validBIC(account.swift) { result.append(tr("SWIFT غير صالح أو غير موجود", "Invalid or missing SWIFT")) }
        if entity.missingAmounts > 0 { result.append(tr("توجد قيود بمبلغ ناقص أو غير صالح", "Some entries have missing or invalid amounts")) }
        if !entity.group.total.isFinite || entity.group.total <= 0 || entity.group.total.rounded() != entity.group.total {
            result.append(tr("المبلغ غير صالح للتحويل", "Invalid transfer amount"))
        }
        if !safeField(entity.group.title) { result.append(tr("اسم الجهة يحتوي رمزاً غير مقبول", "Unsupported character in entity name")) }
        return result
    }
    static func csv(_ entities: [TransferEntity], category: TransferCategory, month: String, payer: String, payerIBAN: String, date: Date = .now) throws -> Data {
        guard category == .rafidain || category == .otherBanks, !entities.isEmpty,
              safeField(payer), validIBAN(payerIBAN), safeField(month),
              entities.allSatisfy({ $0.category == category && issues($0).isEmpty }) else {
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
                          entity.group.title, ArabicText.latinDigits(account.iban).uppercased().filter { !$0.isWhitespace && $0 != "-" },
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
    @AppStorage("bankStatementAccountName") private var payerName = ""
    @AppStorage("bankStatementIBAN") private var payerIBAN = ""
    @State private var groups: [ReportGroup] = []
    @State private var missing: [String: Int] = [:]
    @State private var loadedKey = ""
    @State private var error: String?
    @State private var showSettings = false

    private var month: PayrollMonth? { months.first { $0.name == selectedMonth } ?? months.first }
    private var loadKey: String { "\(String(describing: month?.persistentModelID))|\(month?.name ?? "")|\(month?.importedAt.timeIntervalSince1970 ?? 0)" }
    private var categories: [String: String] { (try? JSONDecoder().decode([String: String].self, from: categoriesData)) ?? [:] }
    private var entities: [TransferEntity] {
        let accounts = ((try? ExecutionAccountStore.decode(accountData)) ?? []).reduce(into: [String: SavedExecutionAccount]()) { $0[$1.key] = $1 }
        return groups.map { group in
            let account = accounts[ArabicText.normalize(group.title)]
            let key = categoryKey(group.title, account)
            let category = categories[key].flatMap(TransferCategory.init(rawValue:)) ?? TransferRules.classify(account)
            return TransferEntity(group: group, account: account, category: category, missingAmounts: missing[group.title] ?? 0)
        }
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
    var body: some View {
        List {
            if let error { Section { Text(error).foregroundStyle(.red) } }
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
            loadedKey = ""; error = nil; groups = []; missing = [:]
            let container = SharedModel.container
            let work = Task.detached(priority: .userInitiated) { try await TransferDataEngine(modelContainer: container).read(month: name) }
            do {
                let value = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
                guard !Task.isCancelled, key == loadKey else { return }
                groups = value.0; missing = value.1; loadedKey = key
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
    let payerName: String
    let payerIBAN: String
    let changeCategory: (TransferCategory, TransferEntity) -> Void
    @Query private var months: [PayrollMonth]
    @AppStorage(ExecutionAccountStore.storageKey) private var currentAccounts = Data()
    @AppStorage(TransferRules.settingsKey) private var currentCategories = Data()
    @State private var error: String?
    private var allIssues: Int { entities.filter { !TransferRules.issues($0).isEmpty }.count }
    private var sourceIsCurrent: Bool {
        let record = months.first { $0.name == month }
        return record != nil && currentAccounts == sourceAccounts && currentCategories == sourceCategories
            && "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    private var canExport: Bool { sourceIsCurrent && !entities.isEmpty && unresolvedCount == 0 && allIssues == 0 && TransferRules.safeField(payerName) && TransferRules.validIBAN(payerIBAN) }
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
