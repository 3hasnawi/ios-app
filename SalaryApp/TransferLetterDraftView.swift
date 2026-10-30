import SwiftUI
import SwiftData

/// معاينات قيد التحقق؛ لا تعرض مشاركة أو تصديراً قبل اعتماد القوالب والتدقيق.
nonisolated enum TransferLetterDraftKind: String, CaseIterable, Identifiable {
    case foodTax = "food_tax", taxSocial = "tax_social", contracts = "contracts", cash = "cash_salary"
    case ramadi = "ramadi", housing = "housing", water = "water", anbar = "rafidain_anbar"
    case rafidain = "rafidain_group", otherBanks = "other_banks_group"
    var id: String { rawValue }
    var title: String {
        switch self {
        case .foodTax: tr("ضريبة عقد الإطعام", "Food contract tax")
        case .taxSocial: tr("الضريبة والحماية الاجتماعية", "Tax and social protection")
        case .contracts: tr("ضمان العقود", "Contract social security")
        case .cash: tr("أمانات رواتب", "Cash salaries")
        case .ramadi: tr("تنفيذ الرمادي", "Ramadi execution")
        case .housing: tr("صندوق الإسكان", "Housing fund")
        case .water: tr("الموارد المائية", "Water resources")
        case .anbar: tr("الرافدين فرع الأنبار", "Rafidain Anbar")
        case .rafidain: tr("مجموعة الرافدين", "Rafidain group")
        case .otherBanks: tr("مجموعة رشيد ومصارف أخرى", "Rasheed and other banks group")
        }
    }
    var requiresEntity: Bool { self == .ramadi || self == .housing || self == .water || self == .anbar }
}

struct TransferLetterDraftView: View {
    let kind: TransferLetterDraftKind
    let month: String
    let figures: TransferLetterFigures
    let entities: [TransferEntity]
    let sourceKey: String
    let sourceAccounts: Data
    let sourceCategories: Data
    let sourceLinks: Data
    let sourceCustomization: Data
    let references: Data
    let payerName: String
    let payerIBAN: String
    let boldFont: Data
    let regularFont: Data
    @State private var coverage = TransferDraftCoverage.shared
    @Query private var months: [PayrollMonth]
    @AppStorage(ExecutionAccountStore.storageKey) private var currentAccounts = Data()
    @AppStorage(TransferRules.settingsKey) private var currentCategories = Data()
    @AppStorage("transferPayrollAccountLinks") private var currentLinks = Data()
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var currentCustomization = Data()
    @AppStorage(TransferReferencePack.settingsKey) private var currentReferences = Data()
    @AppStorage("bankStatementAccountName") private var currentPayerName = ""
    @AppStorage("bankStatementIBAN") private var currentPayerIBAN = ""
    @AppStorage(TransferHeaderFont.boldSettingsKey) private var currentBoldFont = Data()
    @AppStorage(TransferHeaderFont.regularSettingsKey) private var currentRegularFont = Data()
    @State private var selectedID = ""
    @State private var working = false
    @State private var error: String?
    @State private var previewURL: URL?
    @State private var countNotice: String?
    @State private var groupAuditPassed = false
    @State private var csvPreview: String?
    @State private var showCSV = false
    @State private var anbarAuditPassed = false
    @State private var cashAuditPassed = false
    @State private var showPreview = false
    @State private var generation: Task<Void, Never>?

    private var candidates: [TransferEntity] {
        entities.filter { $0.payrollRecipient == nil && ($0.account != nil || (kind == .anbar && $0.isDirectAnbarBank)) &&
            (kind != .anbar || $0.category == .anbar) }
    }
    private var current: Bool {
        let record = months.first { $0.name == month }
        return record != nil && figures.month == month && currentAccounts == sourceAccounts && currentCategories == sourceCategories &&
            currentLinks == sourceLinks && currentCustomization == sourceCustomization && currentReferences == references &&
            currentPayerName == payerName && currentPayerIBAN == payerIBAN && currentBoldFont == boldFont && currentRegularFont == regularFont &&
            "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    private var selectedEntity: TransferEntity? { candidates.first { $0.id == selectedID } }
    private var revision: TransferDraftRevision {
        TransferDraftRevision(sourceKey: sourceKey, accounts: sourceAccounts, categories: sourceCategories,
            payrollLinks: sourceLinks, customization: sourceCustomization, references: references,
            payerName: payerName, payerIBAN: payerIBAN, boldFont: boldFont, regularFont: regularFont)
    }
    var body: some View {
        List {
            Section(tr("بيانات المعاينة", "Preview details")) {
                LabeledContent(tr("الكتاب", "Letter"), value: kind.title)
                LabeledContent(tr("الشهر", "Month"), value: month)
                if kind.requiresEntity {
                    Picker(tr("جهة الاستقطاع", "Deduction entity"), selection: $selectedID) {
                        Text(tr("اختر الجهة", "Choose entity")).tag("")
                        ForEach(candidates) { entity in Text(entity.group.title).tag(entity.id) }
                    }
                    if let entity = selectedEntity { LabeledContent(tr("المبلغ", "Amount"), value: Money.format(entity.group.total)) }
                    if let entity = selectedEntity {
                        ForEach(TransferRules.issues(entity), id: \.self) { issue in Text(issue).font(.footnote).foregroundStyle(.orange) }
                        if (kind == .housing || kind == .water), let account = entity.account,
                           TransferRules.individualLocalAccount(account) == nil {
                            Text(tr("الحساب المحلي مفقود أو لا يطابق IBAN المحفوظ؛ راجع ملف الحسابات.", "The local account is missing or conflicts with the saved IBAN; review the account file.")).font(.footnote).foregroundStyle(.orange)
                        }
                    }
                }
            }
            Section {
                if kind == .contracts && figures.missingContractNominal > 0 {
                    Text(tr("يتعذر حساب هذا الكتاب: يوجد \(Num.format(figures.missingContractNominal)) عقد بلا اسمي مقروء. راجع السجلات من صفحة ملفات التحويل.", "This letter cannot be calculated: \(figures.missingContractNominal) contracts lack readable nominal pay. Review the records in transfer files.")).font(.footnote).foregroundStyle(.orange)
                }
                if kind == .housing || kind == .water {
                    Text(tr("يُطابق رقم حساب المستفيد في الكتاب مع حساب الجهة المحفوظ قبل المعاينة.", "The beneficiary account in the letter is checked against the saved entity account before preview."))
                        .font(.footnote).foregroundStyle(.secondary)
                }
                if !current { Text(tr("تغيّرت البيانات؛ ارجع إلى ملفات التحويل لإعادة التحميل.", "Data changed; return to transfer files to reload.")).foregroundStyle(.orange) }
                Button(tr("معاينة تجريبية PDF", "Preview draft PDF"), systemImage: "doc.text.magnifyingglass") { prepare() }
                    .disabled(!current || references.isEmpty || !TransferRules.validIBAN(payerIBAN) || working || (kind.requiresEntity && selectedEntity == nil) || (kind == .contracts && figures.contractTotal == nil))
            } footer: { Text(tr("معاينة قيد التحقق؛ مشاركة الكتب معطّلة حتى اعتماد التنسيق ومطابقة المبالغ.", "Preview under verification; sharing is disabled until the layout and amounts are verified.")) }
        }
        .navigationTitle(kind.title).navigationBarTitleDisplayMode(.inline)
        .overlay { if working { ProgressView(tr("إعداد المعاينة", "Preparing preview")).padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16)) } }
        .alert(tr("تعذّر إعداد المعاينة", "Couldn't prepare preview"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
        .sheet(isPresented: $showPreview, onDismiss: {
            if let previewURL { try? FileManager.default.removeItem(at: previewURL.deletingLastPathComponent()) }
            previewURL = nil
            countNotice = nil
            groupAuditPassed = false
            csvPreview = nil
            showCSV = false
            anbarAuditPassed = false
            cashAuditPassed = false
        }) {
            NavigationStack {
                if let previewURL {
                    VStack(spacing: 0) {
                        if !current {
                            Label(tr("تغيّرت البيانات بعد إعداد هذه المعاينة؛ أغلقها وأعد تحميل الشهر.", "Data changed after this preview was prepared; close it and reload the month."), systemImage: "exclamationmark.triangle")
                                .font(.footnote).foregroundStyle(.orange).padding(10).frame(maxWidth: .infinity)
                                .background(.thinMaterial)
                        }
                        if groupAuditPassed && current {
                            Label(tr("فُحصت أسماء الجدول وحساباته ومبالغه وملف CSV؛ التنسيق ما زال قيد المراجعة.", "Table names, accounts, amounts and CSV were checked; the layout remains under review."), systemImage: "checkmark.shield")
                                .font(.footnote).foregroundStyle(.green).padding(10).frame(maxWidth: .infinity)
                                .background(.thinMaterial)
                        }
                        if anbarAuditPassed && current {
                            Label(tr("فُحصت أسماء جدول الأنبار ورتبهم ومبالغهم؛ التنسيق ما زال قيد المراجعة.", "Anbar table names, ranks and amounts were checked; the layout remains under review."), systemImage: "checkmark.shield")
                                .font(.footnote).foregroundStyle(.green).padding(10).frame(maxWidth: .infinity)
                                .background(.thinMaterial)
                        }
                        if cashAuditPassed && current {
                            Label(tr("فُحصت أسماء ورتب ومحال عمل ورواتب قائمة الكاش ومجموعها مع المصدر.", "Cash list names, ranks, workplaces, salaries and total were checked against the source."), systemImage: "checkmark.shield")
                                .font(.footnote).foregroundStyle(.green).padding(10).frame(maxWidth: .infinity)
                                .background(.thinMaterial)
                        }
                        if let countNotice {
                            Label(countNotice, systemImage: "exclamationmark.triangle.fill")
                                .font(.footnote).foregroundStyle(.orange).padding(10).frame(maxWidth: .infinity)
                                .background(.thinMaterial)
                        }
                        PDFPreview(url: previewURL)
                    }.navigationTitle(tr("معاينة تجريبية", "Draft preview"))
                        .navigationBarTitleDisplayMode(.inline)
                        .toolbar {
                            ToolbarItem(placement: .cancellationAction) { Button(tr("تم", "Done")) { showPreview = false } }
                            if csvPreview != nil {
                                ToolbarItem(placement: .primaryAction) {
                                    Button(tr("معاينة CSV", "Preview CSV")) { showCSV = true }.disabled(!current)
                                }
                            }
                        }
                        .sheet(isPresented: $showCSV) {
                            NavigationStack {
                                Group {
                                    if current, let csvPreview {
                                        ScrollView([.horizontal, .vertical]) {
                                            Text(csvPreview).font(.system(.caption, design: .monospaced))
                                                .fixedSize(horizontal: true, vertical: false).padding().privacySensitive()
                                        }.environment(\.layoutDirection, .leftToRight)
                                    } else {
                                        ContentUnavailableView(tr("تغيّرت البيانات", "Data changed"), systemImage: "exclamationmark.triangle", description: Text(tr("أغلق المعاينة وأعد تحميل الشهر.", "Close the preview and reload the month.")))
                                    }
                                }
                                .navigationTitle(tr("معاينة CSV", "CSV preview"))
                                .navigationBarTitleDisplayMode(.inline)
                                .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("تم", "Done")) { showCSV = false } } }
                            }
                        }
                }
            }
        }
        .onDisappear { generation?.cancel() }
    }
    private func prepare() {
        guard current, !working else { return }
        let customization: TransferLetterCustomization
        do {
            let store = try (sourceCustomization.isEmpty ? TransferLetterCustomizationStore() : JSONDecoder().decode(TransferLetterCustomizationStore.self, from: sourceCustomization))
            customization = store.effective(for: kind.rawValue)
        } catch { self.error = tr("تعذّر قراءة التخصيصات المحفوظة.", "Could not read saved customizations."); return }
        let entity = selectedEntity
        working = true; error = nil; countNotice = nil; groupAuditPassed = false; anbarAuditPassed = false; cashAuditPassed = false; csvPreview = nil
        generation = Task {
            defer { working = false }
            do {
                let url: URL
                switch kind {
                case .foodTax:
                    url = try await TransferFoodTaxLetter.make(figures: figures, payerIBAN: payerIBAN, references: references, customization: customization, monthFontData: boldFont.isEmpty ? nil : boldFont)
                case .taxSocial:
                    url = try await TransferTaxSocialLetter.make(figures: figures, payerIBAN: payerIBAN, references: references, customization: customization, monthFontData: boldFont.isEmpty ? nil : boldFont)
                case .contracts:
                    url = try await TransferContractsLetter.make(figures: figures, payerIBAN: payerIBAN, references: references, customization: customization, monthFontData: boldFont.isEmpty ? nil : boldFont)
                case .cash:
                    let container = SharedModel.container
                    let task = Task.detached(priority: .userInitiated) { try await TransferCashListEngine(modelContainer: container).list(month: month) }
                    let list = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                    url = try await TransferCashLetter.makeWithList(figures: figures, list: list, payerIBAN: payerIBAN, references: references, customization: customization, monthFontData: boldFont.isEmpty ? nil : boldFont)
                    cashAuditPassed = true
                case .rafidain, .otherBanks:
                    let category: TransferCategory = kind == .rafidain ? .rafidain : .otherBanks
                    let result = try await TransferGroupLetter.make(allEntities: entities, category: category, month: month, payer: payerName, payerIBAN: payerIBAN, references: references, customization: customization)
                    do {
                        let csvWork = Task.detached(priority: .userInitiated) {
                            let size = try result.csv.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                            guard size > 0, size <= 1_000_000 else { throw TransferGroupCSVCheck.Failure.mismatch }
                            let data = try Data(contentsOf: result.csv)
                            guard data.count <= 1_000_000, let text = String(data: data, encoding: .utf8) else { throw TransferGroupCSVCheck.Failure.mismatch }
                            return text
                        }
                        csvPreview = try await withTaskCancellationHandler { try await csvWork.value } onCancel: { csvWork.cancel() }
                    } catch {
                        try? FileManager.default.removeItem(at: result.letter.deletingLastPathComponent())
                        throw error
                    }
                    url = result.letter
                    groupAuditPassed = true
                case .ramadi, .anbar, .housing, .water:
                    guard let entity, entity.account != nil || (kind == .anbar && entity.isDirectAnbarBank) else { throw TransferIndividualPlan.Failure.invalidSource }
                    let container = SharedModel.container
                    let key = MonthName.sortKey(month)
                    let year = key / 100, number = key % 100
                    let previousKey = number == 1 ? (year - 1) * 100 + 12 : year * 100 + number - 1
                    let prior = months.first { $0.sortKey == previousKey && $0.deductionCount > 0 }
                    let priorName = prior?.name
                    let priorRevision = prior?.importedAt
                    let task = Task.detached(priority: .userInitiated) {
                        let engine = ReportEngine(modelContainer: container)
                        let rows = try await engine.rows(for: entity.group)
                        guard let priorName else { return (rows, Optional<Int>.none) }
                        let previous = try await engine.deductionGroups(month: priorName, byEntity: false)
                        try Task.checkCancellation()
                        return (rows, Optional(previous.first { $0.title == entity.group.title }?.count ?? 0))
                    }
                    let (rows, previousCount) = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                    if let priorName, let priorRevision,
                       months.first(where: { $0.name == priorName })?.importedAt != priorRevision {
                        throw NSError(domain: "TransferLetterDraft", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("تغيّرت بيانات الشهر السابق؛ أعد فتح المعاينة.", "The previous month's data changed; reopen the preview.")])
                    }
                    let planKind: TransferIndividualPlan.Kind = switch kind {
                    case .ramadi: .ramadi
                    case .housing: .housing
                    case .water: .water
                    default: .rafidainAnbar
                    }
                    let plan = try TransferIndividualPlan(kind: planKind, group: entity.group, rows: rows, missingAmounts: entity.missingAmounts)
                    if let previousCount, previousCount != entity.group.count {
                        countNotice = tr("تغيّر عدد المنتسبين: \(Num.format(previousCount)) في الشهر السابق و\(Num.format(entity.group.count)) في الشهر المختار.", "Employee count changed: \(Num.format(previousCount)) in the previous month and \(Num.format(entity.group.count)) in the selected month.")
                    }
                    if kind == .ramadi {
                        guard let account = entity.account else { throw TransferIndividualPlan.Failure.invalidSource }
                        url = try await TransferRamadiLetter.make(plan: plan, account: account, payerIBAN: payerIBAN, references: references, customization: customization, monthFontData: boldFont.isEmpty ? nil : boldFont, accountLinks: sourceLinks)
                    } else if kind == .anbar {
                        url = try await TransferAnbarLetter.make(plan: plan, account: entity.account, payerIBAN: payerIBAN, references: references, customization: customization, monthFontData: boldFont.isEmpty ? nil : boldFont, accountLinks: sourceLinks)
                        anbarAuditPassed = true
                    } else {
                        guard let account = entity.account else { throw TransferIndividualPlan.Failure.invalidSource }
                        guard let local = TransferRules.individualLocalAccount(account) else { throw TransferIndividualPlan.Failure.invalidSource }
                        if kind == .housing {
                            url = try await TransferHousingLetter.make(plan: plan, account: account, beneficiaryAccountNumber: local, payerIBAN: payerIBAN, references: references, customization: customization, monthFontData: boldFont.isEmpty ? nil : boldFont, accountLinks: sourceLinks)
                        } else {
                            url = try await TransferWaterLetter.make(plan: plan, account: account, beneficiaryAccountNumber: local, payerIBAN: payerIBAN, references: references, customization: customization, monthFontData: boldFont.isEmpty ? nil : boldFont, accountLinks: sourceLinks)
                        }
                    }
                }
                guard !Task.isCancelled, current else { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()); return }
                switch kind {
                case .rafidain, .otherBanks:
                    let category: TransferCategory = kind == .rafidain ? .rafidain : .otherBanks
                    coverage.record(revision: revision, entityIDs: entities.filter { $0.category == category }.map(\.id), bookID: kind.rawValue)
                case .ramadi, .anbar, .housing, .water:
                    if let entity { coverage.record(revision: revision, entityIDs: [entity.id], bookID: kind.rawValue) }
                default: break
                }
                previewURL = url; generation = nil; showPreview = true
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled else { return }; self.error = error.localizedDescription }
        }
    }
}
