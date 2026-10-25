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

// MARK: - تبويب التقارير

struct ReportsView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @AppStorage("reportKind") private var kindRaw = ReportKind.deductions.rawValue
    @AppStorage("reportMonth") private var monthName = ""
    @AppStorage("reportByEntity") private var byEntity = false
    @State private var deductionGroups: [ReportGroup] = []
    @State private var loadedKey = ""
    @State private var path: [ReportGroup] = []
    @State private var importState = StaffImportState.shared

    private var kind: ReportKind { ReportKind(rawValue: kindRaw) ?? .deductions }
    private var month: PayrollMonth? { months.first { $0.name == monthName } ?? months.first }

    private var payrollGroups: [ReportGroup]? {
        guard let month, let summary = PayrollItems.decodeSummary(month.itemSummaryData) else { return nil }
        return summary.map { ReportGroup(kind: .payroll, month: month.name, title: $0.key, count: $0.value.count, total: $0.value.total, byEntity: false) }
            .sorted { $0.total > $1.total }
    }

    private var groups: [ReportGroup]? { kind == .deductions ? deductionGroups : payrollGroups }
    private var loadKey: String { "\(month?.name ?? "")|\(month?.deductionCount ?? 0)|\(month?.importedAt.timeIntervalSince1970 ?? 0)|\(byEntity)" }

    var body: some View {
        NavigationStack(path: $path) {
            List {
                if months.isEmpty {
                    ContentUnavailableView(tr("لا توجد بيانات", "No data"), systemImage: "chart.bar.doc.horizontal",
                                           description: Text(tr("استورد ملفات الرواتب والاستقطاعات أولاً.", "Import payroll and deduction files first.")))
                        .listRowBackground(Color.clear)
                } else {
                    content
                }
            }
            .listSectionSpacing(.compact)
            .safeAreaInset(edge: .top, spacing: 0) { if !months.isEmpty { filters } }
            .navigationTitle(tr("التقارير", "Reports"))
            .navigationBarTitleDisplayMode(.large)
            .navigationDestination(for: ReportGroup.self) { ReportDetailView(group: $0) }
            .task(id: loadKey) { await loadDeductions() }
            .onChange(of: deductionGroups) {
                // اختبار المحاكي: -openReport YES يفتح أول مجموعة.
                if LaunchOptions.openReport, path.isEmpty, let first = testGroup(groups) { path = [first] }
            }
            .onAppear {
                if LaunchOptions.openReport, kind == .payroll, path.isEmpty, let first = testGroup(payrollGroups) { path = [first] }
            }
        }
    }

    /// اختبار المحاكي: المجموعة التي يحتوي عنوانها -openReportTitle، وإلا الأولى.
    private func testGroup(_ groups: [ReportGroup]?) -> ReportGroup? {
        guard let groups else { return nil }
        if let wanted = UserDefaults.standard.string(forKey: "openReportTitle"), !wanted.isEmpty {
            return groups.first { ArabicText.normalize($0.title).contains(ArabicText.normalize(wanted)) }
        }
        return groups.first
    }

    @ViewBuilder
    private var content: some View {
        Section {
            Picker(tr("النوع", "Type"), selection: $kindRaw) {
                ForEach(ReportKind.allCases) { Text($0.title).tag($0.rawValue) }
            }
            .pickerStyle(.segmented)
            .listRowBackground(Color.clear)
            .listRowInsets(EdgeInsets(top: 4, leading: 0, bottom: 4, trailing: 0))
        }

        if let groups {
            if groups.isEmpty {
                if loadedKey == loadKey || kind == .payroll {
                    ContentUnavailableView(tr("لا توجد بنود لهذا الشهر", "Nothing for this month"), systemImage: "tray")
                        .listRowBackground(Color.clear)
                } else {
                    ProgressView().frame(maxWidth: .infinity).listRowBackground(Color.clear)
                }
            } else {
                Section {
                    summaryCard(groups)
                        .listRowInsets(EdgeInsets())
                        .listRowBackground(Color.clear)
                }
                Section(kind == .deductions ? (byEntity ? tr("حسب الجهة", "By entity") : tr("حسب الفرع", "By branch")) : tr("البنود", "Items")) {
                    let top = groups.map(\.total).max() ?? 1
                    ForEach(groups) { group in
                        NavigationLink(value: group) {
                            GroupRow(group: group, share: top > 0 ? group.total / top : 0)
                        }
                        .accessibilityIdentifier("reportGroup")
                    }
                }
            }
        } else {
            ContentUnavailableView(tr("أعد استيراد ملف رواتب هذا الشهر", "Re-import this month's payroll file"),
                                   systemImage: "arrow.clockwise",
                                   description: Text(tr("بنود ورقة الرواتب (صندوق الشهداء، الرعاية الصحية...) تُحفظ عند الاستيراد من هذا الإصدار فصاعداً.",
                                                        "Payroll-sheet items are saved on import from this version on.")))
                .listRowBackground(Color.clear)
        }
    }

    private var filters: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                Menu {
                    Picker(tr("الشهر", "Month"), selection: Binding(get: { month?.name ?? "" }, set: { monthName = $0 })) {
                        ForEach(months) { Text($0.name).tag($0.name) }
                    }
                } label: {
                    pill(month?.name ?? "", systemImage: "calendar")
                }
                .glassButtonStyle(prominent: true)
                .buttonBorderShape(.capsule)
                if kind == .deductions {
                    Menu {
                        Picker(tr("التجميع", "Group by"), selection: $byEntity) {
                            Text(tr("حسب الفرع", "By branch")).tag(false)
                            Text(tr("حسب الجهة الرئيسية", "By main entity")).tag(true)
                        }
                    } label: {
                        pill(byEntity ? tr("حسب الجهة", "By entity") : tr("حسب الفرع", "By branch"), systemImage: "square.stack.3d.up")
                    }
                    .glassButtonStyle()
                    .buttonBorderShape(.capsule)
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 6)
        }
        .scrollClipDisabled()
    }

    private func pill(_ title: String, systemImage: String) -> some View {
        HStack(spacing: 6) {
            Image(systemName: systemImage)
            Text(title).lineLimit(1)
            Image(systemName: "chevron.down").font(.caption.weight(.bold))
        }
        .font(.subheadline.weight(.semibold))
    }

    private func summaryCard(_ groups: [ReportGroup]) -> some View {
        let total = groups.reduce(0) { $0 + $1.total }
        return HStack(alignment: .bottom) {
            VStack(alignment: .leading, spacing: 2) {
                Text(tr("المجموع", "Total")).font(.caption).opacity(0.85)
                Text(Money.format(total))
                    .font(.system(size: 26, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 2) {
                Text(kind == .deductions ? tr("جهات", "Entities") : tr("بنود", "Items")).font(.caption).opacity(0.85)
                Text(Num.format(groups.count)).font(.title2.bold())
            }
        }
        .foregroundStyle(.white)
        .padding(16)
        .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        .padding(.horizontal, 16)
    }

    private func loadDeductions() async {
        guard let month else { return }
        let key = loadKey
        let name = month.name
        let entity = byEntity
        let container = SharedModel.container
        let result = (try? await Task.detached(priority: .userInitiated) {
            try await ReportEngine(modelContainer: container).deductionGroups(month: name, byEntity: entity)
        }.value) ?? []
        guard !Task.isCancelled else { return }
        deductionGroups = result
        loadedKey = key
    }
}

private struct GroupRow: View {
    let group: ReportGroup
    let share: Double

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(group.title).font(.body.weight(.semibold)).lineLimit(2)
                Spacer(minLength: 8)
                Text(Money.format(group.total))
                    .font(.subheadline.weight(.bold).monospacedDigit())
                    .foregroundStyle(group.kind == .deductions ? Color.red : Color.primary)
            }
            HStack(spacing: 8) {
                Text(tr("\(Num.format(group.count)) موظف", "\(Num.format(group.count)) employees"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                GeometryReader { proxy in
                    Capsule().fill(Color(.tertiarySystemFill))
                        .overlay(alignment: .leading) {
                            Capsule().fill(Color.accentColor.opacity(0.8)).frame(width: max(4, proxy.size.width * share))
                        }
                }
                .frame(height: 6)
            }
        }
        .padding(.vertical, 4)
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
