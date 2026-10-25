import SwiftUI
import SwiftData

/// صفحات التقارير داخل التبويب.
enum ReportsRoute: Hashable {
    /// كل بنود ورقة الرواتب.
    case items
    case group(ReportGroup)
}

// MARK: - تبويب التقارير

/// بطاقات للكشوف الخاصة (صندوق الشهداء، عقد الإطعام، الرعاية الصحية، بنود الرواتب) مع زر تصدير سريع في كل بطاقة،
/// ثم جهات التنفيذ مع البحث، وزر "تصدير" واحد يفتح نافذة التصدير (جهة واحدة أو مجموعة جهات).
struct ReportsView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @AppStorage("reportMonth") private var monthName = ""
    @AppStorage("reportByEntity") private var byEntity = false
    @State private var deductionGroups: [ReportGroup] = []
    @State private var loadedKey = ""
    @State private var path: [ReportsRoute] = []
    @State private var search = ""
    @State private var showExport = false
    @State private var exporter = QuickExporter.shared
    @State private var appliedLaunchOptions = false

    private var month: PayrollMonth? { months.first { $0.name == monthName } ?? months.first }

    private var payrollGroups: [ReportGroup]? {
        guard let month, let summary = PayrollItems.decodeSummary(month.itemSummaryData) else { return nil }
        return summary.map { ReportGroup(kind: .payroll, month: month.name, title: $0.key, count: $0.value.count, total: $0.value.total, byEntity: false) }
            .sorted { $0.total > $1.total }
    }

    private var loadKey: String { "\(month?.name ?? "")|\(month?.deductionCount ?? 0)|\(month?.importedAt.timeIntervalSince1970 ?? 0)|\(byEntity)" }

    private var filteredGroups: [ReportGroup] {
        let tokens = ArabicText.normalize(search).split(separator: " ").map(String.init)
        guard !tokens.isEmpty else { return deductionGroups }
        return deductionGroups.filter { group in
            let key = ArabicText.normalize(group.title)
            return tokens.allSatisfy { key.contains($0) }
        }
    }

    var body: some View {
        NavigationStack(path: $path) {
            List {
                if months.isEmpty {
                    ContentUnavailableView(tr("لا توجد بيانات", "No data"), systemImage: "chart.bar.doc.horizontal",
                                           description: Text(tr("استورد ملفات الرواتب والاستقطاعات أولاً.", "Import payroll and deduction files first.")))
                        .listRowBackground(Color.clear)
                } else {
                    if search.isEmpty {
                        Section {
                            summaryCard
                            specialCards
                        }
                        .listRowInsets(EdgeInsets(top: 0, leading: 0, bottom: 10, trailing: 0))
                        .listRowBackground(Color.clear)
                        .listRowSeparator(.hidden)
                    }
                    entitiesSection
                }
            }
            .listSectionSpacing(.compact)
            .adaptiveSearchable(text: $search, prompt: tr("ابحث عن جهة تنفيذ", "Search an execution entity"))
            .safeAreaInset(edge: .top, spacing: 0) { if !months.isEmpty { filters } }
            .navigationTitle(tr("التقارير", "Reports"))
            .navigationBarTitleDisplayMode(.large)
            .toolbar {
                if !deductionGroups.isEmpty {
                    ToolbarItem(placement: .primaryAction) {
                        Button {
                            showExport = true
                        } label: {
                            Label(tr("تصدير", "Export"), systemImage: "square.and.arrow.up")
                                .labelStyle(.titleAndIcon)
                        }
                        .accessibilityIdentifier("deductionExportButton")
                    }
                }
            }
            .navigationDestination(for: ReportsRoute.self) { route in
                switch route {
                case .items: PayrollItemsView(groups: payrollGroups ?? [], month: month?.name ?? "")
                case .group(let group): ReportDetailView(group: group)
                }
            }
            .sheet(isPresented: $showExport) {
                if let month {
                    DeductionExportSheet(month: month.name, byEntity: byEntity, groups: deductionGroups)
                }
            }
            .overlay { if exporter.isWorking { ExportingHUD() } }
            .alert(tr("التصدير", "Export"), isPresented: Binding(get: { exporter.error != nil }, set: { if !$0 { exporter.error = nil } })) {
                Button(tr("حسناً", "OK"), role: .cancel) {}
            } message: {
                Text(exporter.error ?? "")
            }
            .task(id: loadKey) { await loadDeductions() }
        }
    }

    // MARK: البطاقات

    private var summaryCard: some View {
        let total = deductionGroups.reduce(0) { $0 + $1.total }
        let people = month?.deductionCount ?? 0
        return VStack(alignment: .leading, spacing: 2) {
            Text(tr("مجموع الاستقطاعات", "Total deductions")).font(.caption).opacity(0.85)
            Text(Money.format(total))
                .font(.system(size: 28, weight: .bold, design: .rounded))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            Text(tr("\(Num.format(deductionGroups.count)) جهة · \(Num.format(people)) استقطاع",
                    "\(deductionGroups.count) entities · \(people) deductions"))
                .font(.caption).opacity(0.85)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .foregroundStyle(.white)
        .padding(16)
        .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        .padding(.horizontal, 16)
        .accessibilityElement(children: .combine)
    }

    /// الكشوف الخاصة من ورقة الرواتب، ثم بطاقة كل البنود.
    private var specialCards: some View {
        let groups = payrollGroups ?? []
        let fund = groups.first { FundReport.applies(to: $0.title) }
        let food = groups.first { FoodContractReport.applies(to: $0.title) }
        let health = groups.first { HealthCareReport.applies(to: $0.title) }
        return LazyVGrid(columns: [GridItem(.flexible(), spacing: 10), GridItem(.flexible(), spacing: 10)], spacing: 10) {
            ReportCard(title: tr("صندوق الشهداء", "Martyrs fund"), subtitle: fund.map { tr("\(Num.format($0.count)) موظف", "\($0.count) employees") },
                       total: fund?.total, systemImage: "star.fill", color: .green, group: fund, path: $path)
            ReportCard(title: tr("عقد الإطعام", "Food contract"), subtitle: food.map { tr("عراق الجوال · \(Num.format($0.count))", "Iraq Mobile · \($0.count)") },
                       total: food?.total, systemImage: "fork.knife", color: .orange, group: food, path: $path)
            ReportCard(title: tr("الرعاية الصحية", "Health care"), subtitle: health.map { tr("\(Num.format($0.count)) مشترك", "\($0.count) subscribers") },
                       total: health?.total, systemImage: "cross.case.fill", color: .red, group: health, path: $path)
            Button {
                path.append(.items)
            } label: {
                VStack(alignment: .leading, spacing: 6) {
                    CardIcon(systemImage: "list.bullet.rectangle", color: .gray)
                    Text(tr("بنود الرواتب", "Payroll items")).font(.subheadline.weight(.bold)).foregroundStyle(.primary)
                    Text(tr("\(Num.format(groups.count)) بند", "\(groups.count) items")).font(.caption).foregroundStyle(.secondary)
                    HStack(spacing: 2) {
                        Text(tr("عرض", "View"))
                        Image(systemName: "chevron.forward").font(.caption.weight(.bold))
                    }
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(Color.accentColor)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(12)
                .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
            }
            .buttonStyle(.plain)
            .accessibilityIdentifier("payrollItemsCard")
        }
        .padding(.horizontal, 16)
    }

    // MARK: جهات التنفيذ

    @ViewBuilder
    private var entitiesSection: some View {
        Section {
            if deductionGroups.isEmpty {
                if loadedKey == loadKey {
                    Text(tr("لا توجد استقطاعات لهذا الشهر.", "No deductions for this month.")).foregroundStyle(.secondary)
                } else {
                    ProgressView().frame(maxWidth: .infinity)
                }
            } else if filteredGroups.isEmpty {
                Text(tr("لا توجد جهة بهذا الاسم.", "No entity matches.")).foregroundStyle(.secondary)
            } else {
                let top = deductionGroups.map(\.total).max() ?? 1
                ForEach(filteredGroups) { group in
                    NavigationLink(value: ReportsRoute.group(group)) {
                        GroupRow(group: group, share: top > 0 ? group.total / top : 0)
                    }
                    .accessibilityIdentifier("reportGroup")
                    .contextMenu {
                        Button(tr("تصدير Excel", "Export Excel"), systemImage: "tablecells") { exporter.single(group, format: .excel) }
                        Button(tr("تصدير PDF", "Export PDF"), systemImage: "doc.richtext") { exporter.single(group, format: .pdf) }
                    }
                }
            }
        } header: {
            HStack {
                Text(search.isEmpty
                     ? tr("جهات التنفيذ (\(Num.format(deductionGroups.count)))", "Execution entities (\(deductionGroups.count))")
                     : tr("\(Num.format(filteredGroups.count)) من \(Num.format(deductionGroups.count))", "\(filteredGroups.count) of \(deductionGroups.count)"))
                Spacer()
                Menu {
                    Picker(tr("التجميع", "Group by"), selection: $byEntity) {
                        Text(tr("حسب الفرع", "By branch")).tag(false)
                        Text(tr("حسب الجهة الرئيسية", "By main entity")).tag(true)
                    }
                } label: {
                    HStack(spacing: 3) {
                        Text(byEntity ? tr("حسب الجهة", "By entity") : tr("حسب الفرع", "By branch"))
                        Image(systemName: "chevron.down").font(.caption2.weight(.bold))
                    }
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.accentColor)
                }
                .textCase(nil)
            }
        }
    }

    private var filters: some View {
        HStack {
            Menu {
                Picker(tr("الشهر", "Month"), selection: Binding(get: { month?.name ?? "" }, set: { monthName = $0 })) {
                    ForEach(months) { Text($0.name).tag($0.name) }
                }
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: "calendar")
                    Text(month?.name ?? "").lineLimit(1)
                    Image(systemName: "chevron.down").font(.caption.weight(.bold))
                }
                .font(.subheadline.weight(.semibold))
            }
            .glassButtonStyle(prominent: true)
            .buttonBorderShape(.capsule)
            Spacer()
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 6)
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
        await applyLaunchOptions()
    }

    /// اختبار المحاكي: -reportKind payroll يفتح البنود، -openReport يفتح أول مجموعة (أو -openReportTitle)،
    /// -exportSheet single|group يفتح نافذة التصدير، -bulkExport directorates يكتب ملفي التصدير المجمّع.
    private func applyLaunchOptions() async {
        guard !appliedLaunchOptions else { return }
        appliedLaunchOptions = true
        let defaults = UserDefaults.standard
        let payroll = defaults.string(forKey: "reportKind") == "payroll"
        if payroll { path = [.items] }
        if LaunchOptions.openReport, let first = testGroup(payroll ? payrollGroups : deductionGroups) {
            path.append(.group(first))
        }
        if defaults.string(forKey: "exportSheet") != nil { showExport = true }
        if let raw = defaults.string(forKey: "bulkExport"), let category = EntityCategory(rawValue: raw), let month {
            let report = await QuickExporter.selfTest(category: category, month: month.name, byEntity: byEntity, groups: deductionGroups)
            LaunchOptions.writeReport(report)
        }
    }

    private func testGroup(_ groups: [ReportGroup]?) -> ReportGroup? {
        guard let groups else { return nil }
        if let wanted = UserDefaults.standard.string(forKey: "openReportTitle"), !wanted.isEmpty {
            return groups.first { ArabicText.normalize($0.title).contains(ArabicText.normalize(wanted)) }
        }
        return groups.first
    }
}

// MARK: - بطاقة كشف خاص

private struct CardIcon: View {
    let systemImage: String
    let color: Color
    var body: some View {
        Image(systemName: systemImage)
            .font(.footnote.weight(.semibold))
            .foregroundStyle(.white)
            .frame(width: 30, height: 30)
            .background(color.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
    }
}

private struct ReportCard: View {
    let title: String
    let subtitle: String?
    let total: Double?
    let systemImage: String
    let color: Color
    let group: ReportGroup?
    @Binding var path: [ReportsRoute]
    @State private var exporter = QuickExporter.shared

    var body: some View {
        Button {
            if let group { path.append(.group(group)) }
        } label: {
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .top) {
                    CardIcon(systemImage: systemImage, color: color)
                    Spacer()
                }
                Text(title).font(.subheadline.weight(.bold)).foregroundStyle(.primary).lineLimit(1)
                Text(subtitle ?? tr("لا يوجد هذا الشهر", "Not this month")).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Text(total.map { Money.format($0) } ?? "—")
                    .font(.subheadline.weight(.bold).monospacedDigit())
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        }
        .buttonStyle(.plain)
        .disabled(group == nil)
        // زر التصدير السريع في زاوية البطاقة.
        .overlay(alignment: .topTrailing) {
            if let group {
                Menu {
                    Button(tr("تصدير Excel", "Export Excel"), systemImage: "tablecells") { exporter.special(group, format: .excel) }
                    Button(tr("تصدير PDF", "Export PDF"), systemImage: "doc.richtext") { exporter.special(group, format: .pdf) }
                } label: {
                    Image(systemName: "square.and.arrow.up")
                        .font(.footnote.weight(.bold))
                        .foregroundStyle(Color.accentColor)
                        .frame(width: 32, height: 32)
                        .background(Color.accentColor.opacity(0.12), in: Circle())
                }
                .padding(10)
                .accessibilityLabel(tr("تصدير \(title)", "Export \(title)"))
            }
        }
    }
}

struct GroupRow: View {
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

// MARK: - بنود ورقة الرواتب

struct PayrollItemsView: View {
    let groups: [ReportGroup]
    let month: String
    @State private var search = ""

    private var filtered: [ReportGroup] {
        let tokens = ArabicText.normalize(search).split(separator: " ").map(String.init)
        guard !tokens.isEmpty else { return groups }
        return groups.filter { g in tokens.allSatisfy { ArabicText.normalize(g.title).contains($0) } }
    }

    var body: some View {
        List {
            if groups.isEmpty {
                ContentUnavailableView(tr("أعد استيراد ملف رواتب هذا الشهر", "Re-import this month's payroll file"),
                                       systemImage: "arrow.clockwise",
                                       description: Text(tr("بنود ورقة الرواتب تُحفظ عند الاستيراد.", "Payroll-sheet items are saved on import.")))
                    .listRowBackground(Color.clear)
            } else {
                Section(tr("\(Num.format(filtered.count)) بند · \(month)", "\(filtered.count) items · \(month)")) {
                    let top = groups.map(\.total).max() ?? 1
                    ForEach(filtered) { group in
                        NavigationLink(value: ReportsRoute.group(group)) {
                            GroupRow(group: group, share: top > 0 ? group.total / top : 0)
                        }
                        .accessibilityIdentifier("reportGroup")
                    }
                }
            }
        }
        .adaptiveSearchable(text: $search, prompt: tr("ابحث عن بند", "Search an item"))
        .navigationTitle(tr("بنود الرواتب", "Payroll items"))
        .navigationBarTitleDisplayMode(.inline)
    }
}

// MARK: - التصدير السريع

enum ExportFormat { case excel, pdf }

/// يجهّز ملف التصدير في الخلفية ثم يفتح نافذة المشاركة.
@MainActor @Observable
final class QuickExporter {
    static let shared = QuickExporter()
    var isWorking = false
    var error: String?

    /// كشف جهة استقطاع واحدة بالشكل المعتمد.
    func single(_ group: ReportGroup, format: ExportFormat) {
        run {
            let container = SharedModel.container
            let rows = try await Task.detached(priority: .userInitiated) {
                try await ReportEngine(modelContainer: container).rows(for: group)
            }.value
            let files = try await ReportFiles.make(group: group, rows: rows)
            return format == .excel ? files.excel : files.pdf
        }
    }

    /// الكشوف الخاصة بشكلها المعتمد: صندوق الشهداء، عقد الإطعام، الرعاية الصحية.
    func special(_ group: ReportGroup, format: ExportFormat) {
        run {
            let files = try await Self.specialFiles(group)
            return format == .excel ? files.excel : files.pdf
        }
    }

    static func specialFiles(_ group: ReportGroup) async throws -> (excel: URL, pdf: URL) {
        let container = SharedModel.container
        if FundReport.applies(to: group.title) {
            let rows = try await Task.detached(priority: .userInitiated) {
                try await ReportEngine(modelContainer: container).fundSummary(month: group.month, title: group.title)
            }.value
            return try await FundReport.files(rows, month: group.month)
        }
        if HealthCareReport.applies(to: group.title) {
            let lists = try await Task.detached(priority: .userInitiated) {
                try await ReportEngine(modelContainer: container).healthLists(month: group.month, title: group.title)
            }.value
            return try await HealthCareReport.files(lists, month: group.month)
        }
        let rows = try await Task.detached(priority: .userInitiated) {
            try await ReportEngine(modelContainer: container).rows(for: group)
        }.value
        if FoodContractReport.applies(to: group.title) {
            return try await FoodContractReport.files(FoodContractReport.rows(from: rows), month: group.month)
        }
        return try await ReportFiles.make(group: group, rows: rows)
    }

    func run(_ make: @escaping () async throws -> URL) {
        guard !isWorking else { return }
        isWorking = true
        Task {
            do {
                let url = try await make()
                isWorking = false
                ActivityShare.present([url])
            } catch {
                isWorking = false
                self.error = tr("تعذّر التصدير: \(error.localizedDescription)", "Export failed: \(error.localizedDescription)")
            }
        }
    }

    /// كل جهات النوع المختار بصفوفها.
    static func entities(category: EntityCategory, month: String, byEntity: Bool, groups: [ReportGroup]) async throws -> [BulkExport.Entity] {
        let container = SharedModel.container
        let rows = try await Task.detached(priority: .userInitiated) {
            try await ReportEngine(modelContainer: container).deductionRows(month: month, byEntity: byEntity)
        }.value
        return groups.filter { category.contains($0.title) }.map { BulkExport.Entity(group: $0, rows: rows[$0.title] ?? []) }
    }

    /// اختبار المحاكي: يكتب ملفي التصدير المجمّع ويعيد سطر النتيجة.
    static func selfTest(category: EntityCategory, month: String, byEntity: Bool, groups: [ReportGroup]) async -> String {
        do {
            let list = try await entities(category: category, month: month, byEntity: byEntity, groups: groups)
            let excel = try await BulkExport.excel(list, category: category, month: month, summary: true)
            let pdf = try await BulkExport.pdf(list, category: category, month: month, summary: true)
            let counts = EntityCategory.allCases.map { c in "\(c.rawValue)=\(groups.filter { c.contains($0.title) }.count)" }.joined(separator: " ")
            return "bulk=ok sheets=\(list.count + 1) \(counts) files=\(excel.lastPathComponent),\(pdf.lastPathComponent)"
        } catch {
            return "bulk=failed \(error.localizedDescription)"
        }
    }
}

private struct ExportingHUD: View {
    var body: some View {
        VStack(spacing: 10) {
            ProgressView().controlSize(.large)
            Text(tr("جارٍ تجهيز الملف…", "Preparing the file…")).font(.subheadline.weight(.semibold))
        }
        .padding(22)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        .shadow(color: .black.opacity(0.15), radius: 16, y: 6)
        .accessibilityIdentifier("exportingHUD")
    }
}
