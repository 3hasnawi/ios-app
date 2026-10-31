import SwiftUI
import SwiftData

/// المطابقة تتبع شهر التمويل السابق، وليس أقرب شهر متوفر بالضرورة.
struct BankReconciliationPicker: View {
    let currentMonth: String
    let months: [String]
    @AppStorage("bankStatementManualAmounts") private var manualData = Data()
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()
    @AppStorage(TransferRules.settingsKey) private var categoriesData = Data()
    @AppStorage("transferPayrollAccountLinks") private var linksData = Data()
    @State private var selected = ""
    @State private var summary: BankStatementSummary?
    @State private var figures: TransferLetterFigures?
    @State private var groups: [ReportGroup]?
    @State private var missing: [String: Int] = [:]
    @State private var groupFailure: String?
    @State private var failure: String?
    @State private var figuresFailure: String?
    @State private var loading = false
    @State private var cache = BankStatementCache.shared
    private var expectedPrevious: Int? { BankReconciliation.previousMonthKey(currentMonth) }
    private var storedSourcesValid: Bool {
        (manualData.isEmpty || (try? JSONDecoder().decode([String: BankManualAmounts].self, from: manualData)) != nil) &&
        (try? ExecutionAccountStore.decode(accountData)) != nil &&
        (categoriesData.isEmpty || (try? JSONDecoder().decode([String: String].self, from: categoriesData)) != nil) &&
        (try? TransferAccountLinks.decode(linksData)) != nil
    }
    private var entities: [TransferEntity]? {
        guard storedSourcesValid, let groups, let figures,
              let accounts = try? ExecutionAccountStore.decode(accountData), let links = try? TransferAccountLinks.decode(linksData) else { return nil }
        let categories = (try? JSONDecoder().decode([String: String].self, from: categoriesData)) ?? [:]
        return TransferEntityResolution.make(groups: groups, missing: missing, figures: figures, accounts: accounts, categories: categories, payrollLinks: links, linksValid: true)
    }
    private var manual: BankManualAmounts {
        ((try? JSONDecoder().decode([String: BankManualAmounts].self, from: manualData)) ?? [:])[selected] ?? .init()
    }
    var body: some View {
        Group {
            if selected.isEmpty {
                ContentUnavailableView(tr("الشهر السابق غير محفوظ", "Previous month is not saved"), systemImage: "calendar.badge.exclamationmark",
                    description: Text(tr("اختر شهراً محفوظاً للمراجعة أو استورد بيانات الشهر السابق أولاً.", "Choose a saved month or import the previous month's data first.")))
            } else if loading {
                ProgressView(tr("جارٍ تحميل بيانات الشهر", "Loading month data"))
            } else if let failure {
                ContentUnavailableView(tr("تعذّر تحميل بيانات الشهر", "Could not load month data"), systemImage: "exclamationmark.triangle", description: Text(failure))
            } else if !storedSourcesValid {
                ContentUnavailableView(tr("تعذّرت قراءة البيانات المحفوظة", "Could not read saved data"), systemImage: "exclamationmark.triangle", description: Text(tr("راجع الحسابات والروابط والمبالغ اليدوية قبل المطابقة.", "Review accounts, links and manual amounts before reconciliation.")))
            } else if let summary, summary.month == selected {
                BankReconciliationView(month: selected, summary: summary, manual: manual, figures: figures, figuresFailure: figuresFailure ?? groupFailure, entities: entities).id(selected)
            }
        }
        .navigationTitle(tr("مطابقة حساب المصرف", "Bank account reconciliation"))
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    ForEach(months, id: \.self) { value in
                        Button { selected = value } label: {
                            if value == selected { Label(value, systemImage: "checkmark") } else { Text(value) }
                        }
                    }
                } label: { Image(systemName: "calendar").accessibilityLabel(tr("اختيار شهر التمويل", "Choose funding month")) }
            }
        }
        .onAppear {
            if selected.isEmpty, let previous = expectedPrevious, let value = months.first(where: { MonthName.sortKey($0) == previous }) { selected = value }
        }
        .task(id: "\(selected)|\(cache.revision)") {
            guard !selected.isEmpty else { return }
            let name = selected
            summary = nil; figures = nil; groups = nil; missing = [:]; failure = nil; figuresFailure = nil; groupFailure = nil; loading = true
            defer { if !Task.isCancelled && selected == name { loading = false } }
            do {
                let container = SharedModel.container
                let job = Task.detached(priority: .userInitiated) { try await BankStatementEngine(modelContainer: container).summary(month: name) }
                let value = try await withTaskCancellationHandler { try await job.value } onCancel: { job.cancel() }
                guard !Task.isCancelled, selected == name else { return }
                summary = value
                do {
                    let job = Task.detached(priority: .userInitiated) { try await TransferLetterFigureEngine(modelContainer: container).figures(month: name) }
                    let result = try await withTaskCancellationHandler { try await job.value } onCancel: { job.cancel() }
                    guard !Task.isCancelled, selected == name else { return }
                    figures = result
                } catch is CancellationError { return }
                catch { guard !Task.isCancelled, selected == name else { return }; figuresFailure = error.localizedDescription }
                do {
                    let job = Task.detached(priority: .userInitiated) { try await TransferDataEngine(modelContainer: container).read(month: name) }
                    let result = try await withTaskCancellationHandler { try await job.value } onCancel: { job.cancel() }
                    guard !Task.isCancelled, selected == name else { return }
                    groups = result.0; missing = result.1
                } catch is CancellationError { return }
                catch { guard !Task.isCancelled, selected == name else { return }; groupFailure = error.localizedDescription }
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled, selected == name else { return }; failure = error.localizedDescription }
        }
    }
}
