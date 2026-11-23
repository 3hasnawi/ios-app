import SwiftUI
import SwiftData

/// المطابقة تتبع شهر التمويل السابق، وليس أقرب شهر متوفر بالضرورة.
struct BankReconciliationPicker: View {
    let currentMonth: String
    let months: [PayrollMonth]
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
    private var calculationKey: BankStatementCache.Key? {
        months.first { $0.name == selected }.map { cache.key(for: $0) }
    }
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
            if calculationKey == nil {
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
                    ForEach(months) { record in
                        let value = record.name
                        Button { selected = value } label: {
                            if value == selected { Label(value, systemImage: "checkmark") } else { Text(value) }
                        }
                    }
                } label: { Image(systemName: "calendar").accessibilityLabel(tr("اختيار شهر التمويل", "Choose funding month")) }
            }
        }
        .onAppear {
            if selected.isEmpty, let previous = expectedPrevious, let value = months.first(where: { MonthName.sortKey($0.name) == previous }) { selected = value.name }
        }
        .task(id: calculationKey) {
            summary = nil; figures = nil; groups = nil; missing = [:]; failure = nil; figuresFailure = nil; groupFailure = nil
            guard let key = calculationKey else { loading = false; return }
            loading = true
            defer { if !Task.isCancelled && calculationKey == key { loading = false } }
            do {
                let value = try await cache.summary(for: key)
                guard !Task.isCancelled, calculationKey == key else { return }
                summary = value
                do {
                    let result = try await cache.figures(for: key)
                    guard !Task.isCancelled, calculationKey == key else { return }
                    figures = result
                } catch is CancellationError { return }
                catch { guard !Task.isCancelled, calculationKey == key else { return }; figuresFailure = error.localizedDescription }
                do {
                    let result = try await cache.groups(for: key)
                    guard !Task.isCancelled, calculationKey == key else { return }
                    groups = result.0; missing = result.1
                } catch is CancellationError { return }
                catch { guard !Task.isCancelled, calculationKey == key else { return }; groupFailure = error.localizedDescription }
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled, calculationKey == key else { return }; failure = error.localizedDescription }
        }
    }
}
