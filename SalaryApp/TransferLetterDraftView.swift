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
    @Query private var months: [PayrollMonth]
    @AppStorage(ExecutionAccountStore.storageKey) private var currentAccounts = Data()
    @AppStorage(TransferRules.settingsKey) private var currentCategories = Data()
    @AppStorage("transferPayrollAccountLinks") private var currentLinks = Data()
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var currentCustomization = Data()
    @AppStorage(TransferReferencePack.settingsKey) private var currentReferences = Data()
    @AppStorage("bankStatementAccountName") private var currentPayerName = ""
    @AppStorage("bankStatementIBAN") private var currentPayerIBAN = ""
    @State private var selectedID = ""
    @State private var working = false
    @State private var error: String?
    @State private var previewURL: URL?
    @State private var showPreview = false
    @State private var generation: Task<Void, Never>?

    private var candidates: [TransferEntity] {
        entities.filter { $0.payrollRecipient == nil && $0.account != nil &&
            (kind != .anbar || $0.category == .anbar) }
    }
    private var current: Bool {
        let record = months.first { $0.name == month }
        return record != nil && figures.month == month && currentAccounts == sourceAccounts && currentCategories == sourceCategories &&
            currentLinks == sourceLinks && currentCustomization == sourceCustomization && currentReferences == references &&
            currentPayerName == payerName && currentPayerIBAN == payerIBAN &&
            "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    private var selectedEntity: TransferEntity? { candidates.first { $0.id == selectedID } }
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
                }
            }
            Section {
                if kind == .housing || kind == .water {
                    Text(tr("يُطابق رقم حساب المستفيد في الكتاب مع حساب الجهة المحفوظ قبل المعاينة.", "The beneficiary account in the letter is checked against the saved entity account before preview."))
                        .font(.footnote).foregroundStyle(.secondary)
                }
                if !current { Text(tr("تغيّرت البيانات؛ ارجع إلى ملفات التحويل لإعادة التحميل.", "Data changed; return to transfer files to reload.")).foregroundStyle(.orange) }
                Button(tr("معاينة تجريبية PDF", "Preview draft PDF"), systemImage: "doc.text.magnifyingglass") { prepare() }
                    .disabled(!current || references.isEmpty || !TransferRules.validIBAN(payerIBAN) || working || (kind.requiresEntity && selectedEntity == nil))
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
        }) {
            NavigationStack {
                if let previewURL {
                    PDFPreview(url: previewURL).navigationTitle(tr("معاينة تجريبية", "Draft preview"))
                        .navigationBarTitleDisplayMode(.inline)
                        .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("تم", "Done")) { showPreview = false } } }
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
        working = true; error = nil
        generation = Task {
            defer { working = false }
            do {
                let url: URL
                switch kind {
                case .foodTax:
                    url = try await TransferFoodTaxLetter.make(figures: figures, payerIBAN: payerIBAN, references: references, customization: customization)
                case .taxSocial:
                    url = try await TransferTaxSocialLetter.make(figures: figures, payerIBAN: payerIBAN, references: references, customization: customization)
                case .contracts:
                    url = try await TransferContractsLetter.make(figures: figures, payerIBAN: payerIBAN, references: references, customization: customization)
                case .cash:
                    let container = SharedModel.container
                    let task = Task.detached(priority: .userInitiated) { try await TransferCashListEngine(modelContainer: container).list(month: month) }
                    let list = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                    url = try await TransferCashLetter.makeWithList(figures: figures, list: list, payerIBAN: payerIBAN, references: references, customization: customization)
                case .rafidain, .otherBanks:
                    let category: TransferCategory = kind == .rafidain ? .rafidain : .otherBanks
                    url = try await TransferGroupLetter.make(allEntities: entities, category: category, month: month, payer: payerName, payerIBAN: payerIBAN, references: references, customization: customization).letter
                case .ramadi, .anbar, .housing, .water:
                    guard let entity, let account = entity.account else { throw TransferIndividualPlan.Failure.invalidSource }
                    let container = SharedModel.container
                    let task = Task.detached(priority: .userInitiated) { try await ReportEngine(modelContainer: container).rows(for: entity.group) }
                    let rows = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                    let planKind: TransferIndividualPlan.Kind = switch kind {
                    case .ramadi: .ramadi
                    case .housing: .housing
                    case .water: .water
                    default: .rafidainAnbar
                    }
                    let plan = try TransferIndividualPlan(kind: planKind, group: entity.group, rows: rows, missingAmounts: entity.missingAmounts)
                    if kind == .ramadi {
                        url = try await TransferRamadiLetter.make(plan: plan, account: account, payerIBAN: payerIBAN, references: references, customization: customization)
                    } else if kind == .anbar {
                        url = try await TransferAnbarLetter.make(plan: plan, account: account, payerIBAN: payerIBAN, references: references, customization: customization)
                    } else {
                        guard let local = TransferRules.localAccountDigits(account.iban) else { throw TransferIndividualPlan.Failure.invalidSource }
                        if kind == .housing {
                            url = try await TransferHousingLetter.make(plan: plan, account: account, beneficiaryAccountNumber: local, payerIBAN: payerIBAN, references: references, customization: customization)
                        } else {
                            url = try await TransferWaterLetter.make(plan: plan, account: account, beneficiaryAccountNumber: local, payerIBAN: payerIBAN, references: references, customization: customization)
                        }
                    }
                }
                guard !Task.isCancelled, current else { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()); return }
                previewURL = url; generation = nil; showPreview = true
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled else { return }; self.error = error.localizedDescription }
        }
    }
}
