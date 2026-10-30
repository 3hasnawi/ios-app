import SwiftUI
import SwiftData

/// صفحات التقارير داخل التبويب.
enum ReportsRoute: Hashable {
    /// كل بنود ورقة الرواتب.
    case items
    /// كل جهات التنفيذ بمبالغها وعدد موظفيها.
    case entities
    /// التدقيق: فحوص الشهر ومقارنة الأشهر.
    case audit
    case deductionComparison
    case employeeDeductions
    case salaryAudit
    case group(ReportGroup)
}

// MARK: - تبويب التقارير

/// بطاقات للكشوف الخاصة (صندوق الشهداء، عقد الإطعام، الرعاية الصحية، بنود الرواتب) مع زر تصدير سريع في كل بطاقة،
/// ثم جهات التنفيذ مع البحث، وزر "تصدير" واحد يفتح نافذة التصدير (جهة واحدة أو مجموعة جهات).
struct ReportsView: View {
    var presentedFromHome = false
    @Environment(\.dismiss) private var dismiss
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @AppStorage("reportMonth") private var monthName = ""
    /// تبقى الجهات والفروع كما وردت في ملف الاستقطاعات.
    private let byEntity = false
    @State private var deductionGroups: [ReportGroup] = []
    @State private var loadedKey = ""
    @State private var path: [ReportsRoute] = []
    @State private var showExport = false
    @State private var exporter = QuickExporter.shared
    @State private var appliedLaunchOptions = false
    @State private var router = AppRouter.shared
    /// كشف يُعرض في نافذة المعاينة مباشرة من بطاقته.
    @State private var previewGroup: ReportGroup?
    /// عدد ملاحظات التدقيق للشهر المختار (لبطاقة التدقيق).
    @State private var auditIssues: Int?

    private var month: PayrollMonth? { months.first { $0.name == monthName } ?? months.first }

    private var payrollGroups: [ReportGroup]? {
        guard let month, let summary = PayrollItems.decodeSummary(month.itemSummaryData) else { return nil }
        return summary.map { ReportGroup(kind: .payroll, month: month.name, title: $0.key, count: $0.value.count, total: $0.value.total, byEntity: false) }
            .sorted { $0.total > $1.total }
    }

    private var loadKey: String { "\(month?.name ?? "")|\(month?.deductionCount ?? 0)|\(month?.importedAt.timeIntervalSince1970 ?? 0)|\(byEntity)" }

    var body: some View {
        NavigationStack(path: $path) {
            ScrollView {
                if months.isEmpty {
                    ContentUnavailableView(tr("لا توجد بيانات", "No data"), systemImage: "chart.bar.doc.horizontal",
                                           description: Text(tr("استورد ملفات الرواتب والاستقطاعات أولاً.", "Import payroll and deduction files first.")))
                        .padding(.top, 80)
                } else {
                    VStack(spacing: 12) {
                        summaryCard
                        auditCard
                        employeeDeductionsCard
                        entitiesCard
                        specialCards
                    }
                    .padding(.horizontal, 16)
                    .padding(.top, 4)
                    .padding(.bottom, 24)
                }
            }
            .background(Color(.systemGroupedBackground))
            .navigationTitle(tr("التقارير", "Reports"))
            // عنوان صغير وزر الشهر في شريط العنوان (مثل الرئيسية) حتى تبدأ البطاقات من أعلى الشاشة.
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                if presentedFromHome {
                    ToolbarItem(placement: .topBarLeading) {
                        Button(tr("إغلاق", "Close"), systemImage: "xmark") { dismiss() }
                            .accessibilityIdentifier("closeReports")
                    }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    if !months.isEmpty { filters }
                }
            }
            .navigationDestination(for: ReportsRoute.self) { route in
                switch route {
                case .items: PayrollItemsView(groups: payrollGroups ?? [], month: month?.name ?? "")
                case .deductionComparison: DeductionAuditView(months: months.map(\.name))
                case .employeeDeductions: EmployeeDeductionsView()
                case .salaryAudit: SalaryAuditView(months: months.map(\.name))
                case .audit: AuditView(month: month?.name ?? "", months: months.map(\.name))
                case .entities: EntitiesView(month: month?.name ?? "", groups: deductionGroups, loaded: loadedKey == loadKey, showExport: $showExport)
                case .group(let group): ReportDetailView(group: group)
                }
            }
            .sheet(isPresented: $showExport) {
                if let month {
                    DeductionExportSheet(month: month.name, byEntity: byEntity, groups: deductionGroups)
                }
            }
            .sheet(item: $previewGroup) { ReportPreviewLoader(group: $0) }
            .overlay { if exporter.isWorking { ExportingHUD() } }
            .alert(tr("التصدير", "Export"), isPresented: Binding(get: { exporter.error != nil }, set: { if !$0 { exporter.error = nil } })) {
                Button(tr("حسناً", "OK"), role: .cancel) {}
            } message: {
                Text(exporter.error ?? "")
            }
            .task(id: loadKey) { await loadDeductions() }
            .task {
                #if targetEnvironment(simulator)
                if UserDefaults.standard.bool(forKey: "annualDeductionImportTest") {
                    do { LaunchOptions.writeReport(try AnnualDeductionImportSelfTest.run()) }
                    catch { LaunchOptions.writeReport("annualDeduction=failed \(error.localizedDescription)") }
                }
                if UserDefaults.standard.bool(forKey: "employeeDeductionTest") {
                    do { LaunchOptions.writeReport(try await EmployeeDeductionSelfTest.run()) }
                    catch { LaunchOptions.writeReport("employeeDeduction=failed \(error.localizedDescription)") }
                }
                if UserDefaults.standard.bool(forKey: "deductionAuditTest") {
                    do {
                        let report = try await Task.detached(priority: .userInitiated) {
                            try await DeductionAuditSelfTest.run()
                        }.value
                        LaunchOptions.writeReport(report)
                    } catch { LaunchOptions.writeReport("deductionAudit=failed \(error.localizedDescription)") }
                }
                #endif
            }
            .onChange(of: router.pendingReport, initial: true) { _, route in
                guard let route else { return }
                path = [route]
                router.pendingReport = nil
            }
            .task(id: "\(loadKey)|\(path.count)") {
                auditIssues = nil
                guard let name = months.first?.name else { return }
                let key = loadKey
                let previous = months.count > 1 ? months[1].name : ""
                let issues = await AuditResults.shared.cachedIssues(month: name, previous: previous, container: SharedModel.container)
                guard !Task.isCancelled, key == loadKey else { return }
                auditIssues = issues
            }
        }
    }

    // MARK: البطاقات

    private var summaryCard: some View {
        let total = deductionGroups.reduce(0) { $0 + $1.total }
        let people = month?.deductionCount ?? 0
        return VStack(alignment: .leading, spacing: 2) {
            Text(tr("مجموع الاستقطاعات · \(month?.name ?? "")", "Total deductions · \(month?.name ?? "")"))
                .font(.caption).opacity(0.85)
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
        .accessibilityElement(children: .combine)
    }

    /// الكشوف الخاصة من ورقة الرواتب، ثم بطاقة كل البنود.
    private var specialCards: some View {
        let groups = payrollGroups ?? []
        let fund = groups.first { FundReport.applies(to: $0.title) }
        let food = groups.first { FoodContractReport.applies(to: $0.title) }
        let health = groups.first { HealthCareReport.applies(to: $0.title) }
        return LazyVGrid(columns: [GridItem(.flexible(), spacing: 12), GridItem(.flexible(), spacing: 12)], spacing: 12) {
            ReportCard(title: tr("صندوق الشهداء", "Martyrs fund"), subtitle: fund.map { tr("\(Num.format($0.count)) موظف", "\($0.count) employees") },
                       total: fund?.total, systemImage: "star.fill", color: .green, group: fund, path: $path, onPreview: { previewGroup = $0 })
            ReportCard(title: tr("عقد الإطعام", "Food contract"), subtitle: food.map { tr("عراق الجوال · \(Num.format($0.count))", "Iraq Mobile · \($0.count)") },
                       total: food?.total, systemImage: "fork.knife", color: .orange, group: food, path: $path, onPreview: { previewGroup = $0 })
            ReportCard(title: tr("الرعاية الصحية", "Health care"), subtitle: health.map { tr("\(Num.format($0.count)) مشترك", "\($0.count) subscribers") },
                       total: health?.total, systemImage: "cross.case.fill", color: .red, group: health, path: $path, onPreview: { previewGroup = $0 })
            Button {
                path.append(.items)
            } label: {
                VStack(alignment: .leading, spacing: 6) {
                    CardIcon(systemImage: "list.bullet.rectangle", color: .gray)
                    Text(tr("بنود الرواتب", "Payroll items")).font(.headline).foregroundStyle(.primary)
                    Text(tr("\(Num.format(groups.count)) بند", "\(groups.count) items")).font(.caption).foregroundStyle(.secondary)
                    HStack(spacing: 2) {
                        Text(tr("عرض", "View"))
                        Image(systemName: "chevron.forward").font(.caption.weight(.bold))
                    }
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(Color.accentColor)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(14)
                .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18, style: .continuous))
            }
            .buttonStyle(.plain)
            .accessibilityIdentifier("payrollItemsCard")
        }
    }

    // MARK: التدقيق

    private var auditCard: some View {
        Button {
            path.append(.audit)
        } label: {
            HStack(spacing: 12) {
                CardIcon(systemImage: auditIssues == 0 ? "checkmark.shield.fill" : "exclamationmark.shield.fill",
                         color: auditIssues == 0 ? .green : .orange)
                VStack(alignment: .leading, spacing: 4) {
                    Text(tr("التدقيق", "Audit")).font(.headline).foregroundStyle(.primary)
                    Group {
                        if let auditIssues {
                            Text(auditIssues == 0 ? tr("لا ملاحظات", "No issues") : tr("\(Num.format(auditIssues)) ملاحظات", "\(auditIssues) issues"))
                                .foregroundStyle(auditIssues == 0 ? Color.green : Color.red)
                                .fontWeight(.semibold)
                            + Text(tr(" · الرواتب والاستقطاعات", " · payroll and deductions")).foregroundStyle(.secondary)
                        } else {
                            Text(tr("الرواتب والاستقطاعات · آخر شهرين", "Payroll and deductions · last two months")).foregroundStyle(.secondary)
                        }
                    }
                    .font(.subheadline)
                }
                Spacer(minLength: 8)
                Image(systemName: "chevron.forward").font(.footnote.weight(.bold)).foregroundStyle(.tertiary)
            }
            .padding(14)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("auditCard")
        .contextMenu {
            Button(tr("فتح", "Open"), systemImage: "checkmark.shield") { path.append(.audit) }
            Button(tr("تدقيق الرواتب", "Payroll audit"), systemImage: "banknote") { path.append(.salaryAudit) }
            Button(tr("تدقيق الاستقطاعات", "Deduction audit"), systemImage: "arrow.left.arrow.right") { path.append(.deductionComparison) }
        }
    }

    // MARK: جهات التنفيذ

    private var employeeDeductionsCard: some View {
        Button { path.append(.employeeDeductions) } label: {
            HStack(spacing: 12) {
                CardIcon(systemImage: "person.text.rectangle", color: .green)
                VStack(alignment: .leading, spacing: 4) {
                    Text(tr("كشف استقطاعات موظف", "Employee deduction statement"))
                        .font(.headline).foregroundStyle(.primary)
                    Text(tr("حسب الجهة والفترة", "By entity and date range"))
                        .font(.subheadline).foregroundStyle(.secondary)
                }
                Spacer(minLength: 8)
                Image(systemName: "chevron.forward").font(.footnote.weight(.bold)).foregroundStyle(.tertiary)
            }
            .padding(14)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("employeeDeductionsCard")
    }

    /// بطاقة عريضة: عدد الجهات ومجموعها، وزر التصدير؛ الضغط يفتح قائمة الجهات.
    private var entitiesCard: some View {
        let total = deductionGroups.reduce(0) { $0 + $1.total }
        return HStack(spacing: 12) {
            Button {
                path.append(.entities)
            } label: {
                HStack(spacing: 12) {
                    CardIcon(systemImage: "building.columns.fill", color: .indigo)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(tr("جهات التنفيذ", "Execution entities")).font(.headline).foregroundStyle(.primary)
                        Text(loadedKey == loadKey
                             ? tr("\(Num.format(deductionGroups.count)) جهة", "\(deductionGroups.count) entities")
                             : tr("جارٍ الحساب…", "Calculating…"))
                            .font(.subheadline).foregroundStyle(.secondary)
                        Text(Money.format(total))
                            .font(.title3.weight(.bold).monospacedDigit())
                            .foregroundStyle(.red)
                            .lineLimit(1)
                            .minimumScaleFactor(0.7)
                    }
                    Spacer(minLength: 8)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityIdentifier("entitiesCard")
            Image(systemName: "chevron.forward").font(.footnote.weight(.bold)).foregroundStyle(.tertiary)
        }
        .padding(14)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .contentShape(.contextMenuPreview, RoundedRectangle(cornerRadius: 18, style: .continuous))
        .contextMenu {
            Button(tr("قائمة الجهات", "Entities list"), systemImage: "list.bullet") { path.append(.entities) }
            Button(tr("تصدير…", "Export…"), systemImage: "square.and.arrow.up") { showExport = true }
                .disabled(deductionGroups.isEmpty)
        }
    }

    private var filters: some View {
        Menu {
            Picker(tr("الشهر", "Month"), selection: Binding(get: { month?.name ?? "" }, set: { monthName = $0 })) {
                ForEach(months) { Text($0.name).tag($0.name) }
            }
        } label: {
            Image(systemName: "calendar")
                .accessibilityLabel(tr("اختيار الشهر: \(month?.name ?? "")", "Choose month: \(month?.name ?? "")"))
        }
        .accessibilityIdentifier("reportsMonthMenu")
        .glassButtonStyle()
        .buttonBorderShape(.circle)
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
        if defaults.bool(forKey: "reportsEntities") { path = [.entities] }
        if defaults.bool(forKey: "reportsAudit") { path = [.audit] }
        if defaults.bool(forKey: "compareDeductions") { path = [.deductionComparison] }

        if defaults.bool(forKey: "salaryAudit") { path = [.salaryAudit] }

        if defaults.bool(forKey: "auditTest"), months.count > 1 {
            let a = months[1].name, b = months[0].name
            let digest = await AuditDigest.run(month: b, previous: a, container: SharedModel.container)
            let checks = ((digest.salary?.checks ?? []) + (digest.salary?.staffChecks ?? []) + digest.deductionChecks)
                .map { "\($0.id)=\($0.ok ? "ok" : String($0.rows.count))" }.joined(separator: " ")
            let s = digest.salary
            let kinds = SalaryChangeKind.allCases.map { "\($0.rawValue)=\(s?.rows($0).count ?? -1)" }.joined(separator: " ")
            let d = digest.deductions
            let dk = DeductionGroupKind.allCases.map { "\($0.rawValue)=\(d?.rows($0).count ?? -1)" }.joined(separator: " ")
            LaunchOptions.writeReport("audit=\(s == nil || d == nil ? "failed" : "ok") issues=\(digest.issues) \(checks) salary \(kinds) deductions \(dk)")
        }
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

/// زر دائري صغير في زاوية البطاقة.
private struct CircleIcon: View {
    let systemImage: String
    var body: some View {
        Image(systemName: systemImage)
            .font(.footnote.weight(.bold))
            .foregroundStyle(Color.accentColor)
            .frame(width: 32, height: 32)
            .background(Color.accentColor.opacity(0.12), in: Circle())
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
    var onPreview: (ReportGroup) -> Void = { _ in }
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
                Text(title).font(.headline).foregroundStyle(.primary).lineLimit(1).minimumScaleFactor(0.8)
                Text(subtitle ?? tr("لا يوجد هذا الشهر", "Not this month")).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Text(total.map { Money.format($0) } ?? "—")
                    .font(.headline.monospacedDigit())
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(14)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        }
        .buttonStyle(.plain)
        .disabled(group == nil)
        // الضغط المطوّل: معاينة الجدول أو تصديره.
        .contextMenu {
            if let group {
                Button(tr("معاينة الجدول", "Preview table"), systemImage: ReportSymbols.preview) { onPreview(group) }
                Button(tr("فتح التفاصيل", "Open details"), systemImage: "list.bullet.rectangle") { path.append(.group(group)) }
                Divider()
                Button(tr("تصدير Excel", "Export Excel"), systemImage: ReportSymbols.excel) { exporter.special(group, format: .excel) }
                Button(tr("تصدير PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { exporter.special(group, format: .pdf) }
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
    @State private var previewGroup: ReportGroup?

    private var filtered: [ReportGroup] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
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
                        .contextMenu {
                            Button(tr("معاينة الجدول", "Preview table"), systemImage: ReportSymbols.preview) { previewGroup = group }
                        }
                        .swipeActions(edge: .leading) {
                            Button(tr("معاينة", "Preview"), systemImage: ReportSymbols.preview) { previewGroup = group }
                                .tint(.blue)
                        }
                    }
                }
            }
        }
        .sheet(item: $previewGroup) { ReportPreviewLoader(group: $0) }
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

// MARK: - جهات التنفيذ

/// كل جهة بمبلغها وعدد موظفيها، مع البحث وزر التصدير.
struct EntitiesView: View {
    let month: String
    let groups: [ReportGroup]
    let loaded: Bool
    @Binding var showExport: Bool
    @State private var search = UserDefaults.standard.string(forKey: "entitySearch") ?? ""
    @State private var exporter = QuickExporter.shared
    @State private var previewGroup: ReportGroup?
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()

    private var accountsByKey: [String: SavedExecutionAccount] {
        ((try? ExecutionAccountStore.decode(accountData)) ?? []).reduce(into: [:]) { result, account in
            result[account.key] = account
        }
    }

    private var filtered: [ReportGroup] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        guard !tokens.isEmpty else { return groups }
        return groups.filter { g in tokens.allSatisfy { ArabicText.normalize(g.title).contains($0) } }
    }

    var body: some View {
        List {
            Section {
                if groups.isEmpty {
                    if loaded {
                        Text(tr("لا توجد استقطاعات لهذا الشهر.", "No deductions for this month.")).foregroundStyle(.secondary)
                    } else {
                        ProgressView().frame(maxWidth: .infinity)
                    }
                } else if filtered.isEmpty {
                    Text(tr("لا توجد جهة بهذا الاسم.", "No entity matches.")).foregroundStyle(.secondary)
                } else {
                    let accounts = accountsByKey
                    ForEach(filtered) { group in
                        NavigationLink(value: ReportsRoute.group(group)) {
                            ExecutionEntityCard(group: group, account: accounts[ArabicText.normalize(group.title)])
                        }
                        .accessibilityIdentifier("reportGroup")
                        .contextMenu {
                            Button(tr("معاينة الجدول", "Preview table"), systemImage: ReportSymbols.preview) { previewGroup = group }
                            Button(tr("تصدير Excel", "Export Excel"), systemImage: ReportSymbols.excel) { exporter.single(group, format: .excel) }
                            Button(tr("تصدير PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { exporter.single(group, format: .pdf) }
                        }
                        .swipeActions(edge: .leading) {
                            Button(tr("معاينة", "Preview"), systemImage: ReportSymbols.preview) { previewGroup = group }
                                .tint(.blue)
                        }
                    }
                }
            } header: {
                Text(search.isEmpty
                     ? tr("\(Num.format(groups.count)) جهة · \(Money.format(groups.reduce(0) { $0 + $1.total }))", "\(groups.count) entities")
                     : tr("\(Num.format(filtered.count)) من \(Num.format(groups.count))", "\(filtered.count) of \(groups.count)"))
                    .textCase(nil)
            }
        }
        .adaptiveSearchable(text: $search, prompt: tr("ابحث عن جهة تنفيذ", "Search an execution entity"))
        .sheet(item: $previewGroup) { ReportPreviewLoader(group: $0) }
        .navigationTitle(tr("جهات التنفيذ", "Execution entities"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    showExport = true
                } label: {
                    ReportToolbarLabel()
                }
                .disabled(groups.isEmpty)
                .accessibilityLabel(tr("تصدير", "Export"))
                .accessibilityIdentifier("deductionExportButton")
            }
        }
        .overlay { if exporter.isWorking { ExportingHUD() } }
    }
}

/// بيانات الحساب من السجل المحفوظ المطابق للجهة.
struct ExecutionEntityCard: View {
    let group: ReportGroup
    let account: SavedExecutionAccount?

    private var bankAndBranch: String? {
        guard let account else { return nil }
        let branch = account.branch.trimmingCharacters(in: .whitespacesAndNewlines)
        return branch.isEmpty ? nil : tr("المصرف: \(branch)", "Bank: \(branch)")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "building.columns.fill")
                    .font(.body.weight(.semibold)).foregroundStyle(.white)
                    .frame(width: 40, height: 40)
                    .background(Color.teal.gradient, in: Circle())
                VStack(alignment: .leading, spacing: 3) {
                    Text(group.title).font(.body.weight(.semibold)).foregroundStyle(.primary).fixedSize(horizontal: false, vertical: true)
                    if let bankAndBranch {
                        Text(bankAndBranch).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: 8) {
                tile(title: tr("الشهر", "Month"), value: group.month)
                tile(title: tr("المبلغ", "Amount"), value: Money.format(group.total))
            }
            Text(tr("\(Num.format(group.count)) موظف", "\(Num.format(group.count)) employees"))
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(.vertical, 4)
    }

    private func tile(title: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Text(value).font(.subheadline.weight(.bold).monospacedDigit())
                .foregroundStyle(.primary).lineLimit(1).minimumScaleFactor(0.7)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(Color(.tertiarySystemFill), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}
