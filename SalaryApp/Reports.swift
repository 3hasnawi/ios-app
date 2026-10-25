import SwiftUI
import SwiftData

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
    static func entity(of title: String) -> String {
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
            e.propertiesToFetch = [\.matchKey, \.rank, \.workplace]
            var info: [String: (String, String)] = [:]
            for emp in try modelContext.fetch(e) where info[emp.matchKey] == nil { info[emp.matchKey] = (emp.rank, emp.workplace) }
            var d = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
            d.propertiesToFetch = [\.title, \.amount, \.matchKey, \.employeeName, \.note, \.rank]
            var rows: [ReportRow] = []
            for (i, item) in try modelContext.fetch(d).enumerated() {
                let key = group.byEntity ? Self.entity(of: item.title) : item.title
                guard key == group.title else { continue }
                let (payrollRank, workplace) = info[item.matchKey] ?? ("", "")
                // الرتبة من ملف الاستقطاعات أولاً، وإلا من الرواتب.
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
    @State private var rows: [ReportRow]?
    @State private var search = ""
    @State private var showExport = false
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

    private var filtered: [ReportRow] {
        guard let rows else { return [] }
        let tokens = ArabicText.normalize(search).split(separator: " ").map(String.init)
        guard !tokens.isEmpty else { return rows }
        return rows.filter { row in
            let key = ArabicText.normalize(row.name + " " + row.workplace + " " + row.rank)
            return tokens.allSatisfy { key.contains($0) }
        }
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

            if let fundRows { FundSummarySection(rows: fundRows) }
            if let foodRows, !foodRows.isEmpty { FoodContractSection(rows: foodRows) }
            if let healthLists { HealthCareSection(lists: healthLists) }

            Section {
                if rows == nil {
                    ProgressView().frame(maxWidth: .infinity)
                } else {
                    ForEach(filtered) { row in
                        HStack(alignment: .top, spacing: 10) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(row.name).font(.subheadline.weight(.semibold))
                                let line = [row.rank, row.workplace].filter { !$0.isEmpty }.joined(separator: " · ")
                                if !line.isEmpty { Text(line).font(.caption).foregroundStyle(.secondary) }
                                if group.byEntity, !row.entity.isEmpty, row.entity != group.title {
                                    Text(row.entity).font(.caption2).foregroundStyle(.secondary)
                                }
                                if !row.note.isEmpty { Text(Num.digits(row.note)).font(.caption2).foregroundStyle(.secondary) }
                            }
                            Spacer(minLength: 8)
                            Text(Money.format(row.amount)).font(.subheadline.weight(.semibold).monospacedDigit())
                        }
                    }
                }
            } header: {
                if let rows, !search.isEmpty {
                    Text(tr("\(Num.format(filtered.count)) من \(Num.format(rows.count))", "\(filtered.count) of \(rows.count)"))
                }
            }
        }
        .searchable(text: $search, placement: .navigationBarDrawer(displayMode: .automatic), prompt: tr("ابحث بالاسم أو القسم", "Search name or unit"))
        .navigationTitle(group.title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button(tr("تصدير", "Export"), systemImage: "square.and.arrow.up") { showExport = true }
                    .disabled(rows?.isEmpty ?? true)
                    .accessibilityIdentifier("exportReport")
            }
        }
        .alert(tr("تنبيه: الرعاية الصحية", "Health care warning"), isPresented: $healthWarning) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: {
            Text(tr("يوجد \(Num.format(healthLists?.irregular.count ?? 0)) مبلغ لا يقبل القسمة على \(Num.format(3500))، والكشف لا يقبل الكسور. الأسماء في أعلى الصفحة.",
                    "\(healthLists?.irregular.count ?? 0) amounts are not divisible by 3,500. The names are at the top of the page."))
        }
        .sheet(isPresented: $showExport) {
            if let rows { ReportExportSheet(group: group, rows: rows, fundRows: fundRows, foodRows: foodRows, healthLists: healthLists) }
        }
        .task {
            let container = SharedModel.container
            let group = group
            let loaded = (try? await Task.detached(priority: .userInitiated) {
                try await ReportEngine(modelContainer: container).rows(for: group)
            }.value) ?? []
            rows = loaded
            if group.kind == .payroll, HealthCareReport.applies(to: group.title) {
                healthLists = try? await Task.detached(priority: .userInitiated) {
                    try await ReportEngine(modelContainer: container).healthLists(month: group.month, title: group.title)
                }.value
                if let count = healthLists?.irregular.count, count > 0 { healthWarning = true }
            }
            if group.kind == .payroll, FundReport.applies(to: group.title) {
                fundRows = try? await Task.detached(priority: .userInitiated) {
                    try await ReportEngine(modelContainer: container).fundSummary(month: group.month, title: group.title)
                }.value
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
        }
    }
}
