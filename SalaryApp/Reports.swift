import SwiftUI
import SwiftData
import UIKit

/// نوعا التقارير: من جدول الاستقطاعات (حسب الجهة)، ومن بنود ورقة الرواتب (صندوق الشهداء، الرعاية الصحية...).
nonisolated enum ReportKind: String, CaseIterable, Identifiable, Sendable {
    case deductions, payroll
    var id: String { rawValue }
    var title: String {
        switch self {
        case .deductions: tr("الاستقطاعات", "Deductions")
        case .payroll: tr("جدول الرواتب", "Payroll items")
        }
    }
}

/// مجموعة في التقرير: جهة استقطاع أو بند من ورقة الرواتب.
nonisolated struct ReportGroup: Hashable, Sendable, Identifiable {
    var id: String { "\(kind.rawValue)|\(month)|\(byEntity)|\(title)" }
    let kind: ReportKind
    let month: String
    let title: String
    let count: Int
    let total: Double
    /// تقرير الاستقطاعات مجمّعاً حسب الجهة الرئيسية (ما قبل "/") بدل الفرع.
    let byEntity: Bool
}

nonisolated struct ReportRow: Sendable, Identifiable, Hashable {
    let id: Int
    let name: String
    let rank: String
    let workplace: String
    let amount: Double
    let note: String
    /// جهة الاستقطاع (الفرع) لهذا السطر.
    var entity: String = ""
}

/// حسابات التقارير في الخلفية.
@ModelActor
actor ReportEngine {
    nonisolated static func entity(of title: String) -> String {
        let main = title.split(separator: "/", maxSplits: 1).first.map(String.init) ?? title
        let trimmed = main.trimmingCharacters(in: .whitespaces)
        return trimmed.isEmpty ? title : trimmed
    }

    func deductionGroups(month: String, byEntity: Bool) throws -> [ReportGroup] {
        var d = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
        d.propertiesToFetch = [\.title, \.amount, \.matchKey]
        var totals: [String: (people: Set<String>, total: Double)] = [:]
        for item in try modelContext.fetch(d) {
            let key = byEntity ? Self.entity(of: item.title) : item.title
            totals[key, default: ([], 0)].people.insert(item.matchKey)
            totals[key]!.total += item.amount ?? 0
        }
        return totals.map { ReportGroup(kind: .deductions, month: month, title: $0.key, count: $0.value.people.count,
                                        total: $0.value.total, byEntity: byEntity) }
            .sorted { $0.total > $1.total }
    }

    func rows(for group: ReportGroup) throws -> [ReportRow] {
        let month = group.month
        var e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        switch group.kind {
        case .deductions:
            let title = group.title
            let predicate: Predicate<Deduction>
            if group.byEntity { predicate = #Predicate { $0.month == month } }
            else { predicate = #Predicate { $0.month == month && $0.title == title } }
            var d = FetchDescriptor<Deduction>(predicate: predicate)
            d.propertiesToFetch = [\.title, \.amount, \.matchKey, \.employeeName, \.note, \.rank]
            let deductions = try modelContext.fetch(d).filter {
                (group.byEntity ? Self.entity(of: $0.title) : $0.title) == group.title
            }
            // نقرأ رواتب أصحاب الكشف فقط بدل كل رواتب الشهر.
            let keys = Array(Set(deductions.map(\.matchKey)))
            var info: [String: (String, String)] = [:]
            for start in stride(from: 0, to: keys.count, by: 400) {
                let batch = Array(keys[start..<min(start + 400, keys.count)])
                e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month && batch.contains($0.matchKey) })
                e.propertiesToFetch = [\.matchKey, \.rank, \.workplace]
                for emp in try modelContext.fetch(e) where info[emp.matchKey] == nil {
                    info[emp.matchKey] = (emp.rank, emp.workplace)
                }
            }
            var rows: [ReportRow] = []
            for (i, item) in deductions.enumerated() {
                let (payrollRank, workplace) = info[item.matchKey] ?? ("", "")
                let rank = item.rank.isEmpty ? payrollRank : item.rank
                rows.append(ReportRow(id: i, name: item.employeeName, rank: rank, workplace: workplace,
                                      amount: item.amount ?? 0, note: item.note, entity: item.title))
            }
            return rows.sorted { $0.name < $1.name }
        case .payroll:
            e.propertiesToFetch = [\.name, \.rank, \.workplace, \.itemsData]
            var rows: [ReportRow] = []
            for (i, emp) in try modelContext.fetch(e).enumerated() {
                guard let amount = PayrollItems.decode(emp.itemsData)[group.title] else { continue }
                rows.append(ReportRow(id: i, name: emp.name, rank: emp.rank, workplace: emp.workplace, amount: amount, note: ""))
            }
            return rows.sorted { $0.name < $1.name }
        }
    }
}

// MARK: - تفاصيل المجموعة

struct ReportDetailView: View {
    let group: ReportGroup
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()
    @State private var rows: [ReportRow]?
    @State private var loadError: String?
    @State private var showExport = false
    @State private var showPreview = UserDefaults.standard.bool(forKey: "previewReport")
    /// لبند صندوق الشهداء: الملخّص حسب الفئة.
    @State private var fundRows: [FundReport.Row]?
    /// لبند الرعاية الصحية: المشتركون وغير المشتركين.
    @State private var healthLists: HealthCareReport.Lists?
    @State private var healthWarning = false
    /// لبند شركة عراق الجوال: كشف عقد الإطعام.
    private var foodRows: [FoodContractReport.Row]? {
        guard group.kind == .payroll, FoodContractReport.applies(to: group.title), let rows else { return nil }
        return FoodContractReport.rows(from: rows)
    }

    private var executionAccount: SavedExecutionAccount? {
        guard group.kind == .deductions else { return nil }
        let exact = ArabicText.normalize(group.title)
        let main = ArabicText.normalize(ReportEngine.entity(of: group.title))
        let executionAccounts = (try? ExecutionAccountStore.decode(accountData)) ?? []
        return executionAccounts.first { $0.key == exact } ?? executionAccounts.first { $0.key == main }
    }

    var body: some View {
        List {
            Section {
                VStack(alignment: .leading, spacing: 6) {
                    Text(group.month).font(.caption).foregroundStyle(.secondary)
                    HStack(alignment: .bottom) {
                        VStack(alignment: .leading, spacing: 0) {
                            Text(tr("المجموع", "Total")).font(.caption).foregroundStyle(.secondary)
                            Text(Money.format(group.total))
                                .font(.system(size: 26, weight: .bold, design: .rounded))
                                .monospacedDigit()
                                .foregroundStyle(group.kind == .deductions ? Color.red : Color.primary)
                        }
                        Spacer()
                        VStack(alignment: .trailing, spacing: 0) {
                            Text(tr("الموظفون", "Employees")).font(.caption).foregroundStyle(.secondary)
                            Text(Num.format(group.count)).font(.title2.bold())
                        }
                    }
                }
                .padding(.vertical, 4)
            }

            if let account = executionAccount {
                Section(tr("حساب جهة التنفيذ", "Execution account")) {
                    LabeledContent("IBAN") { Text(account.iban).monospaced().textSelection(.enabled).privacySensitive() }
                    if !account.branch.isEmpty { LabeledContent(tr("الفرع", "Branch"), value: account.branch) }
                    if !account.swift.isEmpty { LabeledContent("SWIFT", value: account.swift) }
                    Button(tr("نسخ IBAN", "Copy IBAN"), systemImage: "doc.on.doc") {
                        UIPasteboard.general.string = account.iban
                    }
                }
            }

            if let fundRows { FundSummarySection(rows: fundRows) }
            if let foodRows, !foodRows.isEmpty { FoodContractSection(rows: foodRows) }
            if let healthLists { HealthCareSection(lists: healthLists) }
            if let loadError {
                Section { Text(loadError).foregroundStyle(.red) }
            }

            // بدل قائمة الأسماء: معاينة الجدول كما سيُصدَّر، وزر التصدير.
            Section {
                HStack(spacing: 10) {
                    actionButton(tr("معاينة الجدول", "Preview table"), systemImage: ReportSymbols.preview, filled: false) { showPreview = true }
                        .accessibilityIdentifier("previewReport")
                    actionButton(tr("تصدير", "Export"), systemImage: "square.and.arrow.up", filled: true) { showExport = true }
                        .accessibilityIdentifier("exportReport")
                }
                .disabled(rows?.isEmpty == true)
                .overlay { if rows == nil { ProgressView().allowsHitTesting(false) } }
                .frame(maxWidth: .infinity)
                .listRowInsets(EdgeInsets(top: 2, leading: 0, bottom: 2, trailing: 0))
                .listRowBackground(Color.clear)
            } footer: {
                if let rows {
                    Text(tr("\(Num.format(rows.count)) سطر في الجدول، بالشكل المعتمد (A4).", "\(rows.count) rows, in the approved A4 layout."))
                }
            }
        }
        .listSectionSpacing(.compact)
        .contentMargins(.top, 8, for: .scrollContent)
        .navigationTitle(group.title)
        .navigationBarTitleDisplayMode(.inline)
        .alert(tr("تنبيه: الرعاية الصحية", "Health care warning"), isPresented: $healthWarning) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: {
            Text(tr("يوجد \(Num.format(healthLists?.irregular.count ?? 0)) مبلغ لا يقبل القسمة على \(Num.format(3500))، والكشف لا يقبل الكسور. الأسماء في أعلى الصفحة.",
                    "\(healthLists?.irregular.count ?? 0) amounts are not divisible by 3,500. The names are at the top of the page."))
        }
        .sheet(isPresented: $showExport) {
            if loadError != nil { loadingState }
            else if let rows { ReportExportSheet(group: group, rows: rows, fundRows: fundRows, foodRows: foodRows, healthLists: healthLists) }
            else { loadingState }
        }
        .sheet(isPresented: $showPreview) {
            if loadError != nil { loadingState }
            else if let rows { ReportPreviewSheet(group: group, rows: rows, fundRows: fundRows, foodRows: foodRows, healthLists: healthLists) }
            else { loadingState }
        }
        .task {
            let container = SharedModel.container
            let group = group
            do {
                let loaded = try await Task.detached(priority: .userInitiated) {
                    try await ReportEngine(modelContainer: container).rows(for: group)
                }.value
                if group.kind == .payroll, HealthCareReport.applies(to: group.title) {
                    healthLists = try await Task.detached(priority: .userInitiated) {
                        try await ReportEngine(modelContainer: container).healthLists(month: group.month, title: group.title)
                    }.value
                    if let count = healthLists?.irregular.count, count > 0 { healthWarning = true }
                }
                if group.kind == .payroll, FundReport.applies(to: group.title) {
                    fundRows = try await Task.detached(priority: .userInitiated) {
                        try await ReportEngine(modelContainer: container).fundSummary(month: group.month, title: group.title)
                    }.value
                }
                guard !Task.isCancelled else { return }
                rows = loaded
                // "تصدير" من الضغط المطوّل على بطاقة في الرئيسية: يفتح نافذة التصدير مباشرة.
                if UserDefaults.standard.bool(forKey: "homeExportReport") {
                    UserDefaults.standard.removeObject(forKey: "homeExportReport")
                    showExport = true
                }
                // اختبار المحاكي: -exportReport YES يكتب ملفي Excel و PDF ويفتح نافذة التصدير.
                if UserDefaults.standard.bool(forKey: "exportReport") {
                    var ok = (try? await ReportFiles.make(group: group, rows: loaded)) != nil
                    // الكشوف الخاصة بشكلها المعتمد.
                    if let fundRows, (try? await FundReport.files(fundRows, month: group.month)) == nil { ok = false }
                    if let foodRows, (try? await FoodContractReport.files(foodRows, month: group.month)) == nil { ok = false }
                    if let healthLists, (try? await HealthCareReport.files(healthLists, month: group.month)) == nil { ok = false }
                    LaunchOptions.writeReport(ok ? "export=ok rows=\(loaded.count)" : "export=failed")
                    showExport = true
                }
            } catch {
                guard !Task.isCancelled else { return }
                loadError = error.localizedDescription
                rows = []
            }
        }
    }

    private var loadingState: some View {
        Group {
            if let loadError {
                ContentUnavailableView(tr("تعذّر تجهيز الكشف", "Couldn't prepare the report"),
                                       systemImage: "exclamationmark.triangle", description: Text(loadError))
            } else {
                ProgressView(tr("جارٍ تجهيز الكشف…", "Preparing the report…"))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
    }

    private func actionButton(_ title: String, systemImage: String, filled: Bool, action: @escaping () -> Void) -> some View {
        ReportActionButton(title: title, systemImage: systemImage, prominent: filled, action: action)
    }
}
