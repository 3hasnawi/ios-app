import SwiftUI
import SwiftData
import Charts

// MARK: - البيانات

/// أرقام الشهر في الرئيسية (تُحسب في الخلفية وتُحفظ حتى تظهر فوراً في المرة القادمة).
nonisolated struct HomeOverview: Sendable, Equatable, Codable {
    struct Point: Sendable, Equatable, Codable { var month: String; var net: Double }
    struct Share: Sendable, Equatable, Codable { var title: String; var count: Int; var total: Double }

    var netTotal: Double = 0
    var employees = 0
    var previousNetTotal: Double?
    /// صافي آخر ستة أشهر حتى هذا الشهر (الأقدم أولاً).
    var trend: [Point] = []
    /// ملف الاستقطاعات: المجموع وعدد الجهات وأكبرها.
    var entityTotal: Double = 0
    var entityCount = 0
    var topEntities: [Share] = []
    /// ضباط، موظفون، مراتب، عقود: العدد والصافي.
    var categories: [Share] = []
}

/// خلاصة التدقيق للرئيسية (أرقام فقط).
nonisolated struct HomeAudit: Sendable, Equatable, Codable {
    struct Issue: Sendable, Equatable, Codable, Identifiable {
        var id: String
        var title: String
        var detail: String
        var count: Int
        /// salary | deductions
        var route: String
    }
    var issues: [Issue] = []
    var compared = false
    var added = 0, removed = 0, rank = 0, workplace = 0, net = 0, bank = 0
    var hasDeductions = false
    var stopped = 0, started = 0, changed = 0

    init() {}

    init(_ d: AuditDigest) {
        if let s = d.salary {
            compared = s.compared
            added = s.rows(.added).count
            removed = s.rows(.removed).count
            rank = s.rows(.rank).count
            workplace = s.rows(.workplace).count
            net = s.rows(.net).count
            bank = s.rows(.bank).count
            if bank > 0 {
                issues.append(Issue(id: "bank", title: tr("تغيّر الحساب المصرفي", "Bank account changed"),
                                    detail: tr("مقارنة بـ\(d.previous)", "vs \(d.previous)"), count: bank, route: "salary"))
            }
            for c in s.checks where !c.ok {
                issues.append(Issue(id: c.id, title: c.title, detail: tr("فحص الشهر", "Monthly check"), count: c.rows.count, route: "salary"))
            }
        }
        for c in d.deductionChecks where !c.ok {
            issues.append(Issue(id: c.id, title: c.title, detail: tr("الاستقطاعات", "Deductions"), count: c.rows.count, route: "deductions"))
        }
        if let c = d.deductions, c.emptyMonths.isEmpty {
            hasDeductions = true
            stopped = c.rows(.stopped).count
            started = c.rows(.started).count
            changed = c.rows(.changed).count
        }
    }
}

extension ReportEngine {
    func homeOverview(month: String, previous: String?, trendMonths: [String]) throws -> HomeOverview {
        var result = HomeOverview()
        var e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        e.propertiesToFetch = [\.netSalary, \.rank]
        let current = try modelContext.fetch(e)
        result.employees = current.count
        result.netTotal = current.reduce(0) { $0 + ($1.netSalary ?? 0) }

        // التوزيع حسب الفئة (بنفس تصنيف كشف صندوق الشهداء).
        var shares = FundReport.categories.map { HomeOverview.Share(title: $0.name, count: 0, total: 0) }
        for emp in current {
            let i = FundReport.category(of: emp.rank)
            shares[i].count += 1
            shares[i].total += emp.netSalary ?? 0
        }
        result.categories = shares

        func net(_ name: String) throws -> Double {
            var d = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == name })
            d.propertiesToFetch = [\.netSalary]
            return try modelContext.fetch(d).reduce(0) { $0 + ($1.netSalary ?? 0) }
        }
        if let previous { result.previousNetTotal = try net(previous) }
        var trend: [HomeOverview.Point] = []
        for name in trendMonths {
            let value: Double
            if name == month { value = result.netTotal }
            else if name == previous, let p = result.previousNetTotal { value = p }
            else { value = try net(name) }
            trend.append(HomeOverview.Point(month: name, net: value))
        }
        result.trend = trend

        let groups = try deductionGroups(month: month, byEntity: false)
        result.entityTotal = groups.reduce(0) { $0 + $1.total }
        result.entityCount = groups.count
        result.topEntities = groups.sorted { $0.total > $1.total }.prefix(4)
            .map { HomeOverview.Share(title: $0.title, count: $0.count, total: $0.total) }
        return result
    }
}

// MARK: - البطاقات (إظهار وإخفاء من الإعدادات)

enum HomeCard: String, CaseIterable, Identifiable {
    case attention, changes, entities, special, recents, categories, dataStatus
    var id: String { rawValue }
    static let hiddenKey = "homeHiddenCards"

    var title: String {
        switch self {
        case .attention: tr("يحتاج انتباهك", "Needs attention")
        case .changes: tr("متغيرات الشهر", "This month's changes")
        case .entities: tr("أكبر جهات الاستقطاع", "Top deduction entities")
        case .special: tr("الكشوف الخاصة", "Special reports")
        case .recents: tr("آخر من بحثت عنهم", "Recently viewed")
        case .categories: tr("التوزيع حسب الفئة", "By category")
        case .dataStatus: tr("حالة البيانات", "Data status")
        }
    }
    var subtitle: String {
        switch self {
        case .attention: tr("أهم ملاحظات التدقيق", "Top audit issues")
        case .changes: tr("جدد، خارجون، ترفيع، نقل، الاستقطاعات", "New, removed, promotions, transfers, deductions")
        case .entities: tr("أعلى أربع جهات بمبالغها", "Top four entities")
        case .special: tr("صندوق الشهداء، الرعاية الصحية، عقد الإطعام", "Martyrs fund, health care, food contract")
        case .recents: tr("صف أفقي بالأسماء", "A row of names")
        case .categories: tr("ضباط، موظفون، مراتب، عقود", "Officers, staff, ranks, contracts")
        case .dataStatus: tr("الملفات المستوردة والنسخة الاحتياطية", "Imported files and backup")
        }
    }
    var systemImage: String {
        switch self {
        case .attention: "exclamationmark.triangle.fill"
        case .changes: "arrow.left.arrow.right"
        case .entities: "building.columns.fill"
        case .special: "star.fill"
        case .recents: "clock.arrow.circlepath"
        case .categories: "chart.bar.fill"
        case .dataStatus: "checkmark.icloud.fill"
        }
    }
    var color: Color {
        switch self {
        case .attention: .orange
        case .changes: .blue
        case .entities: .indigo
        case .special: .green
        case .recents: .gray
        case .categories: .teal
        case .dataStatus: .cyan
        }
    }

    static func hidden(_ raw: String) -> Set<HomeCard> {
        Set(raw.split(separator: ",").compactMap { HomeCard(rawValue: String($0)) })
    }
    static func raw(_ set: Set<HomeCard>) -> String {
        allCases.filter(set.contains).map(\.rawValue).joined(separator: ",")
    }
}

// MARK: - الرئيسية

/// صفحات تُفتح من بطاقات الرئيسية (بالضغط أو من قائمة الضغط المطوّل).
enum HomeRoute: Hashable, Identifiable {
    case audit, salary, deductions, backup
    var id: Self { self }
}

/// الرئيسية «لوحة المحاسب»: الصافي ومعادلته، يحتاج انتباهك، متغيرات الشهر، أكبر الجهات، الكشوف الخاصة،
/// آخر البحث، التوزيع حسب الفئة، حالة البيانات. كل بطاقة تفتح صفحتها، والضغط المطوّل للخيارات والإخفاء.
struct HomeDashboard: View {
    let months: [PayrollMonth]
    let current: PayrollMonth?

    @State private var overview: HomeOverview?
    @State private var audit: HomeAudit?
    @State private var route: HomeRoute?
    @State private var showCustomize = false
    @State private var backup = BackupManager.shared
    @AppStorage(HomeTileLayout.key) private var layoutRaw = ""
    @AppStorage(HomeCard.hiddenKey) private var hiddenRaw = ""
    /// يتحدّث تلقائياً عند أي إضافة أو مسح (من أي شاشة).
    @AppStorage(RecentSearches.key) private var recentsData = Data()
    private var recents: [RecentSearches.Item] { RecentSearches.decode(recentsData) }
    private var hidden: Set<HomeCard> { HomeCard.hidden(hiddenRaw) }

    private var previous: PayrollMonth? {
        guard let current, let i = months.firstIndex(where: { $0.name == current.name }), i + 1 < months.count else { return nil }
        return months[i + 1]
    }

    /// آخر ستة أشهر حتى الشهر المعروض (الأقدم أولاً).
    private var trendMonths: [String] {
        guard let current, let i = months.firstIndex(where: { $0.name == current.name }) else { return [] }
        return Array(months[i...].prefix(6).map(\.name).reversed())
    }

    // وقت الاستيراد يتغير حتى لو بقي عدد السجلات نفسه.
    private var statsKey: String {
        ([current, previous].map { month in
            guard let month else { return "none" }
            return "\(month.name)|\(month.importedAt.timeIntervalSince1970)|\(month.employeeCount)|\(month.deductionCount)"
        } + trendMonths).joined(separator: ";")
    }

    private var auditKey: String { "\(statsKey)|\(AppSettings.locale.identifier)" }

    private var summary: [String: PayrollItems.Summary] { PayrollItems.decodeSummary(current?.itemSummaryData) ?? [:] }

    var body: some View {
        ScrollViewReader { proxy in
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                if let current {
                    homeTile(0, compact: false, month: current)
                    HStack(alignment: .top, spacing: 12) {
                        homeTile(1, compact: true, month: current)
                        homeTile(2, compact: true, month: current)
                    }
                    ForEach(3..<HomeTileLayout.defaults.count, id: \.self) { index in
                        homeTile(index, compact: false, month: current)
                    }
                    Button {
                        showCustomize = true
                    } label: {
                        Label(tr("تخصيص البطاقات", "Customize cards"), systemImage: "slider.horizontal.3")
                            .font(.footnote.weight(.medium))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 6)
                    }
                    .accessibilityIdentifier("homeCustomize")
                } else { welcome }
            }
            .padding(.horizontal, 16)
            .padding(.top, 4)
            .padding(.bottom, 24)
            .animation(.snappy, value: layoutRaw)
            Color.clear.frame(height: 1).id("homeBottom")
        }
        .task(id: audit == nil) {
            // اختبار المحاكي: -homeScroll YES ينزل إلى آخر الرئيسية بعد اكتمال البطاقات.
            guard audit != nil, UserDefaults.standard.bool(forKey: "homeScroll") else { return }
            try? await Task.sleep(for: .milliseconds(400))
            withAnimation { proxy.scrollTo("homeBottom", anchor: .bottom) }
        }
        }
        .background {
            // شعار وزارة الداخلية خلفية شفافة ثابتة.
            Image("HomeLogo")
                .resizable()
                .scaledToFit()
                .frame(maxWidth: 320)
                .opacity(0.07)
                .accessibilityHidden(true)
                .allowsHitTesting(false)
        }
        .navigationDestination(item: $route) { route in
            switch route {
            case .audit: AuditView(months: months.map(\.name))
            case .salary: SalaryAuditView(months: months.map(\.name))
            case .deductions: DeductionAuditView(months: months.map(\.name))
            case .backup: BackupView()
            }
        }
        .sheet(isPresented: $showCustomize) {
            NavigationStack {
                HomeLayoutSettingsView()
                    .toolbar {
                        ToolbarItem(placement: .confirmationAction) {
                            Button(tr("تم", "Done")) { showCustomize = false }
                        }
                    }
            }
            .presentationDetents([.medium, .large])
        }
        .task {
            if UserDefaults.standard.bool(forKey: "openHomeCustomize") { showCustomize = true }
        }
        .task(id: statsKey) { await loadOverview() }
        .task(id: "\(auditKey)|\(layoutRaw)") { await loadAudit() }
    }

    // MARK: التحميل

    /// كل موضع يقبل أي بطاقة؛ اختيار بطاقة معروضة في موضع آخر يبدّل موقعيهما.
    @ViewBuilder
    private func homeTile(_ index: Int, compact: Bool, month: PayrollMonth) -> some View {
        let kind = HomeTileLayout.decode(layoutRaw)[index]
        if kind != .hidden {
            HomeTileCard(kind: kind, spec: tileSpec(kind, month: month), compact: compact) {
                openTile(kind, month: month)
            }
            .contextMenu {
                Menu {
                    Section(tr("الرواتب", "Payroll")) {
                        tileChoices(HomeTileKind.payroll, index: index, selected: kind)
                    }
                    Section(tr("الاستقطاعات والتدقيق", "Deductions and audit")) {
                        tileChoices(HomeTileKind.deductions, index: index, selected: kind)
                    }
                    Section(tr("الأدوات", "Tools")) {
                        tileChoices(HomeTileKind.navigation, index: index, selected: kind)
                    }
                } label: {
                    Label(tr("تخصيص بطاقة البيانات", "Customize data card"), systemImage: "slider.horizontal.3")
                }
                Button(tr("إخفاء البطاقة", "Hide card"), systemImage: "eye.slash") {
                    layoutRaw = HomeTileLayout.select(.hidden, at: index, in: layoutRaw)
                }
            }
            .accessibilityIdentifier(kind == .reports ? "homeReportsEntry" : kind == .net ? "homeNetCard" : "homeSlot\(index)")
        }
    }

    @ViewBuilder
    private func tileChoices(_ kinds: [HomeTileKind], index: Int, selected: HomeTileKind) -> some View {
        ForEach(kinds) { kind in
            Button {
                layoutRaw = HomeTileLayout.select(kind, at: index, in: layoutRaw)
            } label: {
                if kind == selected { Label(kind.title, systemImage: "checkmark") }
                else { Text(kind.title) }
            }
        }
    }

    private func tileSpec(_ kind: HomeTileKind, month: PayrollMonth) -> HomeTileSpec {
        let payrollRecents = recents.filter(\.inPayroll)
        switch kind {
        case .net:
            var change = ""
            if let now = overview?.netTotal, let before = overview?.previousNetTotal, before > 0 {
                let percent = (now - before) / before * 100
                change = tr("\(percent >= 0 ? "+" : "")\(percent.formatted(.number.precision(.fractionLength(1)).locale(AppSettings.locale)))% عن الشهر السابق",
                            "\(percent >= 0 ? "+" : "")\(percent.formatted(.number.precision(.fractionLength(1)).locale(AppSettings.locale)))% vs previous month")
            }
            return HomeTileSpec(title: tr("صافي رواتب \(month.name)", "Net payroll · \(month.name)"),
                                value: Money.format(overview?.netTotal), subtitle: change)
        case .employees:
            return HomeTileSpec(title: kind.title, value: Num.format(overview?.employees ?? month.employeeCount),
                                subtitle: tr("من ملف رواتب \(month.name)", "From \(month.name) payroll"))
        case .deductionCount:
            return HomeTileSpec(title: kind.title, value: Num.format(month.deductionCount),
                                subtitle: tr("\(Num.format(overview?.entityCount ?? 0)) جهة تنفيذ", "\(overview?.entityCount ?? 0) entities"))
        case .deductionTotal:
            return HomeTileSpec(title: kind.title, value: Money.format(overview?.entityTotal),
                                subtitle: tr("ملف الاستقطاعات · \(month.name)", "Deduction file · \(month.name)"))
        case .attention:
            return HomeTileSpec(title: kind.title, value: audit.map { Num.format($0.issues.count) } ?? "—",
                                subtitle: audit?.issues.isEmpty == true ? tr("لا ملاحظات هذا الشهر", "No issues this month") : tr("ملاحظات الرواتب والاستقطاعات", "Payroll and deduction issues"),
                                rows: audit?.issues.prefix(2).map(\.title) ?? [])
        case .reports:
            return HomeTileSpec(title: kind.title, value: "", subtitle: tr("التدقيق والكشوفات وجهات التنفيذ", "Audits, statements and entities"))
        case .recents:
            return HomeTileSpec(title: kind.title, value: "", subtitle: payrollRecents.isEmpty ? tr("لا يوجد بحث رواتب بعد", "No payroll searches yet") : "",
                                rows: Array(payrollRecents.prefix(2).map(\.text)))
        case .changes:
            let count = audit.map { $0.added + $0.removed + $0.stopped + $0.started + $0.changed }
            return HomeTileSpec(title: kind.title, value: count.map(Num.format) ?? "—",
                                subtitle: previous.map { tr("مقارنة بـ\($0.name)", "Compared with \($0.name)") } ?? "",
                                rows: audit.map { [tr("\(Num.format($0.added)) موظفون جدد", "\($0.added) new employees"),
                                                    tr("\(Num.format($0.stopped)) استقطاعات توقفت", "\($0.stopped) stopped deductions")] } ?? [])
        case .entities:
            return HomeTileSpec(title: kind.title, value: overview.map { Num.format($0.entityCount) } ?? "—",
                                subtitle: Money.format(overview?.entityTotal),
                                rows: overview?.topEntities.prefix(2).map { "\($0.title) · \(Money.format($0.total))" } ?? [])
        case .categories:
            return HomeTileSpec(title: kind.title, value: "", subtitle: tr("توزيع الرواتب حسب الرتبة", "Payroll by rank"),
                                rows: overview?.categories.prefix(3).map { "\($0.title) · \(Num.format($0.count))" } ?? [])
        case .special:
            return HomeTileSpec(title: kind.title, value: "", subtitle: tr("من بنود الرواتب", "From payroll items"),
                                rows: [tr("صندوق الشهداء", "Martyrs fund"), tr("الرعاية الصحية", "Health care"), tr("عقد الإطعام", "Food contract")])
        case .fileStatus:
            return HomeTileSpec(title: kind.title, value: "", subtitle: month.name,
                                rows: [tr("الرواتب: \(Num.format(month.employeeCount)) موظف", "Payroll: \(month.employeeCount) employees"),
                                       tr("الاستقطاعات: \(Num.format(month.deductionCount)) قيد", "Deductions: \(month.deductionCount) entries")])
        case .hidden:
            return HomeTileSpec(title: "", value: "", subtitle: "")
        }
    }

    private func openTile(_ kind: HomeTileKind, month: PayrollMonth) {
        switch kind {
        case .reports, .special: AppRouter.shared.showReports = true
        case .attention: route = .audit
        case .changes: route = .salary
        case .deductionCount, .deductionTotal, .entities: openReports(.entities, month: month)
        case .employees: AppRouter.shared.tab = .search
        case .recents:
            if let item = recents.first(where: \.inPayroll) {
                ShortcutRouter.shared.pendingSearch = item.text
                AppRouter.shared.tab = .search
            }
        case .net, .categories, .fileStatus, .hidden: break
        }
    }

    /// مدخل ثابت للتقارير بعد نقلها من الشريط السفلي.
    private var reportsEntry: some View {
        Button {
            AppRouter.shared.showReports = true
        } label: {
            HStack(spacing: 12) {
                Image(systemName: "chart.bar.doc.horizontal")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(.blue)
                    .frame(width: 40, height: 40)
                    .background(Color.blue.opacity(0.12), in: RoundedRectangle(cornerRadius: 12))
                VStack(alignment: .leading, spacing: 3) {
                    Text(tr("التقارير", "Reports")).font(.headline)
                    Text(tr("التدقيق والكشوفات وجهات التنفيذ", "Audits, statements and execution entities"))
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer(minLength: 8)
                Image(systemName: "chevron.forward").font(.footnote.weight(.semibold)).foregroundStyle(.tertiary)
            }
            .padding(14)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("homeReportsEntry")
    }

    private static let overviewCacheKey = "homeOverviewCache"
    private static let auditCacheKey = "homeAuditCache"

    private func loadOverview() async {
        guard let current else { overview = nil; return }
        let cacheKey = statsKey
        if let data = UserDefaults.standard.data(forKey: Self.overviewCacheKey),
           let cached = try? JSONDecoder().decode([String: HomeOverview].self, from: data)[cacheKey] {
            overview = cached
            return
        }
        overview = nil
        let container = SharedModel.container
        let month = current.name, previousName = previous?.name, trend = trendMonths
        let started = Date()
        let loaded = try? await Task.detached(priority: .userInitiated) {
            try await ReportEngine(modelContainer: container).homeOverview(month: month, previous: previousName, trendMonths: trend)
        }.value
        guard !Task.isCancelled else { return }
        overview = loaded
        PerfLog.record("homeStats", since: started)
        if let loaded, let data = try? JSONEncoder().encode([cacheKey: loaded]) {
            UserDefaults.standard.set(data, forKey: Self.overviewCacheKey)
        }
    }

    /// التدقيق أثقل: يُعرض المحفوظ فوراً، ويُحسب في الخلفية بعد ظهور الرئيسية (أو يؤخذ جاهزاً بعد الاستيراد).
    private func loadAudit() async {
        guard let current else { return }
        let visible = HomeTileLayout.decode(layoutRaw)
        guard visible.contains(.attention) || visible.contains(.changes) else { return }
        let cacheKey = auditKey
        if let data = UserDefaults.standard.data(forKey: Self.auditCacheKey),
           let cached = try? JSONDecoder().decode([String: HomeAudit].self, from: data)[cacheKey] {
            audit = cached
            return
        }
        audit = nil
        try? await Task.sleep(for: .milliseconds(600))
        guard !Task.isCancelled else { return }
        let digest = await AuditDigest.run(month: current.name, previous: previous?.name ?? "", container: SharedModel.container)
        guard !Task.isCancelled else { return }
        let result = HomeAudit(digest)
        audit = result
        if let data = try? JSONEncoder().encode([cacheKey: result]) {
            UserDefaults.standard.set(data, forKey: Self.auditCacheKey)
        }
    }

    // MARK: الصافي

    private func netCard(_ month: PayrollMonth) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(tr("صافي رواتب \(month.name)", "Net salaries, \(month.name)"))
                .font(.subheadline.weight(.medium))
                .opacity(0.85)
            if let overview {
                Text(Money.format(overview.netTotal))
                    .font(.system(size: 32, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                HStack {
                    if let prev = overview.previousNetTotal, prev > 0, let previous {
                        let change = (overview.netTotal - prev) / prev * 100
                        Text(tr("\(change >= 0 ? "+" : "")\(change.formatted(.number.precision(.fractionLength(1)).locale(AppSettings.locale)))% عن \(previous.name)",
                                "\(change >= 0 ? "+" : "")\(change.formatted(.number.precision(.fractionLength(1)).locale(AppSettings.locale)))% vs \(previous.name)"))
                    }
                    Spacer(minLength: 8)
                    Text(tr("\(Num.format(overview.employees)) موظف", "\(overview.employees) employees"))
                }
                .font(.footnote.weight(.medium))
                .opacity(0.9)
                if overview.trend.count > 1 {
                    trendChart(overview.trend)
                }
                let deductions = summary.first { ArabicText.normalize($0.key) == ArabicText.normalize(SalaryItems.deductionTotal) }?.value.total
                if let deductions, deductions > 0 {
                    HStack(spacing: 6) {
                        equationCell(tr("الاستحقاق", "Gross"), overview.netTotal + deductions)
                        Text("−").font(.title3.weight(.bold))
                        equationCell(tr("الاستقطاعات", "Deductions"), deductions)
                    }
                    .padding(8)
                    .background(.white.opacity(0.14), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                }
            } else {
                ProgressView().tint(.white).frame(height: 36)
            }
        }
        .foregroundStyle(.white)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 24, style: .continuous))
        .contextMenu {
            if let overview {
                Button(tr("نسخ الصافي", "Copy net"), systemImage: "doc.on.doc") { copyToClipboard(Money.format(overview.netTotal)) }
            }
            Button(tr("تدقيق الرواتب", "Payroll audit"), systemImage: "checkmark.shield") { route = .salary }
        }
        .accessibilityIdentifier("homeNetCard")
    }

    private func trendChart(_ points: [HomeOverview.Point]) -> some View {
        let values = points.map(\.net)
        let low = values.min() ?? 0, high = values.max() ?? 0
        let pad = max((high - low) * 0.25, high * 0.001, 1)
        let last = points.count - 1
        return Chart {
            ForEach(points.indices, id: \.self) { i in
                LineMark(x: .value("m", i), y: .value("net", points[i].net))
                    .interpolationMethod(.catmullRom)
                    .lineStyle(StrokeStyle(lineWidth: 2.5, lineCap: .round))
                    .foregroundStyle(.white.opacity(0.9))
            }
            PointMark(x: .value("m", last), y: .value("net", points[last].net))
                .foregroundStyle(.white)
                .symbolSize(40)
        }
        .chartXAxis(.hidden)
        .chartYAxis(.hidden)
        .chartYScale(domain: (low - pad)...(high + pad))
        .chartXScale(domain: 0...max(1, points.count - 1))
        .environment(\.layoutDirection, .leftToRight)
        .frame(height: 34)
        .accessibilityLabel(tr("اتجاه الصافي لآخر \(Num.format(points.count)) أشهر", "Net trend, last \(points.count) months"))
    }

    private func equationCell(_ title: String, _ value: Double) -> some View {
        VStack(spacing: 1) {
            Text(title).font(.caption2).opacity(0.85)
            Text(Money.format(value)).font(.subheadline.weight(.bold).monospacedDigit()).lineLimit(1).minimumScaleFactor(0.6)
        }
        .frame(maxWidth: .infinity)
    }

    // MARK: يحتاج انتباهك

    private var attentionCard: some View {
        card(.attention, link: tr("التدقيق", "Audit"), route: .audit) {
            if let audit {
                if audit.issues.isEmpty {
                    Label(tr("لا ملاحظات في تدقيق هذا الشهر", "No audit issues this month"), systemImage: "checkmark.seal.fill")
                        .font(.subheadline).foregroundStyle(.green)
                } else {
                    VStack(spacing: 0) {
                        ForEach(Array(audit.issues.prefix(3).enumerated()), id: \.element.id) { i, issue in
                            Button {
                                route = issue.route == "deductions" ? .deductions : .salary
                            } label: {
                                HStack(spacing: 10) {
                                    Image(systemName: issue.id == "bank" ? "arrow.left.arrow.right" : "exclamationmark")
                                        .font(.footnote.weight(.bold)).foregroundStyle(.white)
                                        .frame(width: 28, height: 28)
                                        .background((issue.id == "bank" ? Color.red : .orange).gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(issue.title).font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                                        Text(issue.detail).font(.caption).foregroundStyle(.secondary)
                                    }
                                    Spacer(minLength: 8)
                                    if issue.count > 0 {
                                        Text(Num.format(issue.count)).font(.subheadline.weight(.bold).monospacedDigit())
                                            .foregroundStyle(issue.id == "bank" ? .red : .orange)
                                    }
                                }
                                .padding(.vertical, 7)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            if i < min(audit.issues.count, 3) - 1 { Divider().padding(.leading, 38) }
                        }
                        if audit.issues.count > 3 {
                            Text(tr("و\(Num.format(audit.issues.count - 3)) ملاحظات أخرى", "and \(audit.issues.count - 3) more"))
                                .font(.caption).foregroundStyle(.secondary).padding(.top, 4)
                        }
                    }
                }
            } else {
                loadingLine(tr("جارٍ التدقيق…", "Checking…"))
            }
        }
        .accessibilityIdentifier("homeAttention")
    }

    // MARK: متغيرات الشهر

    private var changesCard: some View {
        card(.changes, link: tr("تدقيق الرواتب", "Payroll audit"), route: .salary) {
            if let audit {
                if !audit.compared && !audit.hasDeductions {
                    Text(previous == nil ? tr("استورد شهراً ثانياً لتظهر المتغيرات.", "Import a second month to see changes.")
                                         : tr("لا توجد بيانات كافية للمقارنة.", "Not enough data to compare."))
                        .font(.subheadline).foregroundStyle(.secondary)
                } else {
                    VStack(spacing: 8) {
                        if audit.compared {
                            chipRow([
                                ("+" + Num.format(audit.added), tr("جدد", "New"), .green),
                                ("−" + Num.format(audit.removed), tr("خارجون", "Removed"), .red),
                                (Num.format(audit.rank), tr("ترفيع", "Promoted"), .primary),
                                (Num.format(audit.workplace), tr("نقل", "Transferred"), .primary),
                            ], destination: .salary)
                        }
                        chipRow([
                            (audit.hasDeductions ? Num.format(audit.stopped) : "—", tr("استقطاع توقف", "Stopped"), .red),
                            (audit.hasDeductions ? Num.format(audit.started) : "—", tr("استقطاع جديد", "New deduction"), .green),
                            (audit.hasDeductions ? Num.format(audit.changed) : "—", tr("تغيّر مبلغ", "Amount changed"), .orange),
                            (audit.compared ? Num.format(audit.net) : "—", tr("تغيّر صافي", "Net changed"), .blue),
                        ], destination: .deductions)
                    }
                }
            } else {
                loadingLine(tr("جارٍ المقارنة بالشهر السابق…", "Comparing with the previous month…"))
            }
        }
        .accessibilityIdentifier("homeChanges")
    }

    private enum ChipTarget { case salary, deductions }

    private func chipRow(_ items: [(String, String, Color)], destination: ChipTarget) -> some View {
        Button {
            route = destination == .salary ? .salary : .deductions
        } label: {
            HStack(spacing: 6) {
                ForEach(items.indices, id: \.self) { i in
                    let (value, label, color) = items[i]
                    VStack(spacing: 1) {
                        Text(value).font(.headline.monospacedDigit()).foregroundStyle(color).lineLimit(1).minimumScaleFactor(0.6)
                        Text(label).font(.caption2).foregroundStyle(.secondary).lineLimit(1).minimumScaleFactor(0.7)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 7)
                    .background(Color(.tertiarySystemFill), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                }
            }
        }
        .buttonStyle(.plain)
    }

    // MARK: أكبر جهات الاستقطاع

    private func entitiesCard(_ month: PayrollMonth) -> some View {
        Group {
            if let overview, overview.topEntities.isEmpty {
                EmptyView()
            } else {
                cardShell(.entities) {
                    cardHeader(.entities, link: tr("كل الجهات", "All entities")) { openReports(.entities, month: month) }
                    if let overview {
                        let top = overview.topEntities.first?.total ?? 1
                        ForEach(overview.topEntities, id: \.title) { entity in
                            Button {
                                openReports(.group(ReportGroup(kind: .deductions, month: month.name, title: entity.title,
                                                               count: entity.count, total: entity.total, byEntity: false)), month: month)
                            } label: {
                                HStack(spacing: 8) {
                                    Text(entity.title).font(.footnote).lineLimit(1).frame(width: 130, alignment: .leading)
                                    GeometryReader { geo in
                                        Capsule().fill(Color(.tertiarySystemFill))
                                            .overlay(alignment: .leading) {
                                                Capsule().fill(Color.indigo.gradient)
                                                    .frame(width: max(6, geo.size.width * entity.total / max(top, 1)))
                                            }
                                    }
                                    .frame(height: 8)
                                    Text(Money.format(entity.total)).font(.caption.weight(.semibold).monospacedDigit())
                                        .lineLimit(1).minimumScaleFactor(0.6).frame(width: 96, alignment: .trailing)
                                }
                                .padding(.vertical, 3)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                        Text(tr("\(Num.format(overview.entityCount)) جهة · المجموع \(Money.format(overview.entityTotal))",
                                "\(overview.entityCount) entities · total \(Money.format(overview.entityTotal))"))
                            .font(.caption).foregroundStyle(.secondary)
                    } else {
                        loadingLine(nil)
                    }
                }
                .contextMenu {
                    Button(tr("كل الجهات", "All entities"), systemImage: "building.columns") { openReports(.entities, month: month) }
                    hideButton(.entities)
                }
                .accessibilityIdentifier("homeEntities")
            }
        }
    }

    // MARK: الكشوف الخاصة

    private func specialCards(_ month: PayrollMonth) -> some View {
        let items = summary.map { (title: $0.key, summary: $0.value) }
        let fund = items.first { FundReport.applies(to: $0.title) }
        let health = items.first { HealthCareReport.applies(to: $0.title) }
        let food = items.first { FoodContractReport.applies(to: $0.title) }
        let list: [(String, String, Color, (title: String, summary: PayrollItems.Summary)?)] = [
            (tr("صندوق الشهداء", "Martyrs fund"), "star.fill", .green, fund),
            (tr("الرعاية الصحية", "Health care"), "cross.case.fill", .red, health),
            (tr("عقد الإطعام", "Food contract"), "fork.knife", .orange, food),
        ]
        return HStack(spacing: 8) {
            ForEach(list.indices, id: \.self) { index in
                let (name, icon, color, item) = list[index]
                Button {
                    if let item { openReports(.group(group(item, month: month)), month: month) }
                } label: {
                    VStack(alignment: .leading, spacing: 5) {
                        Image(systemName: icon).font(.footnote.weight(.semibold)).foregroundStyle(.white)
                            .frame(width: 28, height: 28)
                            .background(color.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                        Text(name).font(.caption.weight(.semibold)).foregroundStyle(.primary).lineLimit(1).minimumScaleFactor(0.8)
                        Text(item.map { Money.format($0.summary.total) } ?? "—")
                            .font(.footnote.weight(.bold).monospacedDigit()).foregroundStyle(.primary)
                            .lineLimit(1).minimumScaleFactor(0.6)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(10)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                    .contentShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                }
                .buttonStyle(.plain)
                .disabled(item == nil)
                .contextMenu {
                    if let item {
                        Button(tr("فتح الكشف", "Open report"), systemImage: "doc.text") { openReports(.group(group(item, month: month)), month: month) }
                        Button(tr("تصدير", "Export"), systemImage: ReportSymbols.export) {
                            UserDefaults.standard.set(true, forKey: "homeExportReport")
                            openReports(.group(group(item, month: month)), month: month)
                        }
                    }
                    hideButton(.special)
                }
            }
        }
        .accessibilityIdentifier("homeSpecial")
    }

    private func group(_ item: (title: String, summary: PayrollItems.Summary), month: PayrollMonth) -> ReportGroup {
        ReportGroup(kind: .payroll, month: month.name, title: item.title, count: item.summary.count, total: item.summary.total, byEntity: false)
    }

    // MARK: آخر البحث

    private var recentRow: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(HomeCard.recents.title).font(.subheadline.weight(.semibold))
                Spacer()
                Button(tr("مسح", "Clear")) { RecentSearches.clear() }
                    .font(.footnote.weight(.medium))
                    .accessibilityIdentifier("clearRecents")
            }
            .padding(.horizontal, 4)
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    ForEach(Array(recents.prefix(10).enumerated()), id: \.offset) { _, item in
                        Button {
                            ShortcutRouter.shared.pendingSearch = item.text
                            AppRouter.shared.tab = .search
                        } label: {
                            HStack(spacing: 8) {
                                NameAvatar(name: item.text, size: 30)
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(item.text).font(.footnote.weight(.semibold)).lineLimit(1).foregroundStyle(.primary)
                                    Text(item.inPayroll && item.inStaff ? tr("رواتب · ملاك", "Payroll · staff")
                                         : item.inStaff ? tr("ملاك", "Staff") : tr("رواتب", "Payroll"))
                                        .font(.caption2).foregroundStyle(.secondary)
                                }
                            }
                            .frame(maxWidth: 190, alignment: .leading)
                            .padding(.horizontal, 10).padding(.vertical, 8)
                            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                        }
                        .buttonStyle(.plain)
                        .contextMenu {
                            Button(tr("بحث عنه", "Search"), systemImage: "magnifyingglass") {
                                ShortcutRouter.shared.pendingSearch = item.text
                                AppRouter.shared.tab = .search
                            }
                            Button(tr("نسخ الاسم", "Copy name"), systemImage: "doc.on.doc") { copyToClipboard(item.text) }
                            Button(tr("حذف من السجل", "Remove from history"), systemImage: "trash", role: .destructive) { RecentSearches.remove(item.text) }
                            hideButton(.recents)
                        }
                    }
                }
            }
            .scrollClipDisabled()
        }
        .accessibilityIdentifier("homeRecents")
    }

    // MARK: التوزيع حسب الفئة

    private static let categoryColors: [Color] = [.indigo, .orange, .green, .gray]

    private var categoriesCard: some View {
        cardShell(.categories) {
            cardHeader(.categories, link: nil, action: nil)
            if let overview, overview.employees > 0 {
                let total = max(overview.netTotal, 1)
                GeometryReader { geo in
                    HStack(spacing: 2) {
                        ForEach(Array(overview.categories.enumerated()), id: \.offset) { i, c in
                            if c.total > 0 {
                                Rectangle().fill(Self.categoryColors[i % 4].gradient)
                                    .frame(width: max(3, (geo.size.width - 6) * c.total / total))
                            }
                        }
                    }
                }
                .frame(height: 12)
                .clipShape(Capsule())
                .environment(\.layoutDirection, .rightToLeft)
                LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], alignment: .leading, spacing: 8) {
                    ForEach(Array(overview.categories.enumerated()), id: \.offset) { i, c in
                        HStack(alignment: .top, spacing: 6) {
                            RoundedRectangle(cornerRadius: 3).fill(Self.categoryColors[i % 4]).frame(width: 10, height: 10).padding(.top, 4)
                            VStack(alignment: .leading, spacing: 1) {
                                Text("\(c.title) · \(Num.format(c.count))").font(.footnote.weight(.semibold))
                                Text(Money.format(c.total)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                                    .lineLimit(1).minimumScaleFactor(0.7)
                            }
                        }
                    }
                }
            } else {
                loadingLine(nil)
            }
        }
        .contextMenu {
            if let overview {
                Button(tr("نسخ التوزيع", "Copy"), systemImage: "doc.on.doc") {
                    copyToClipboard(overview.categories.map { "\($0.title): \(Num.format($0.count)) · \(Money.format($0.total))" }.joined(separator: "\n"))
                }
            }
            hideButton(.categories)
        }
        .accessibilityIdentifier("homeCategories")
    }

    // MARK: حالة البيانات

    private func dataStatusCard(_ month: PayrollMonth) -> some View {
        cardShell(.dataStatus) {
            cardHeader(.dataStatus, link: nil, action: nil)
            VStack(alignment: .leading, spacing: 7) {
                statusLine(month.employeeCount > 0, tr("رواتب \(month.name) · \(Num.format(month.employeeCount)) موظف", "\(month.name) payroll · \(month.employeeCount)"),
                           note: BackupManager.relative(month.importedAt))
                statusLine(month.deductionCount > 0,
                           month.deductionCount > 0 ? tr("استقطاعات \(month.name) · \(Num.format(month.deductionCount)) قيد", "\(month.name) deductions · \(month.deductionCount)")
                                                    : tr("لم يُستورد ملف استقطاعات \(month.name)", "No deductions file for \(month.name)"), note: nil)
                HStack(spacing: 8) {
                    Image(systemName: "icloud.fill").font(.footnote).foregroundStyle(backup.statusColor)
                        .frame(width: 18)
                    Text(backup.statusLine).font(.footnote).foregroundStyle(backup.statusColor == .secondary ? .primary : backup.statusColor)
                        .lineLimit(1).minimumScaleFactor(0.8)
                }
                Text(tr("الأشهر المحفوظة: \(Num.format(months.count))", "Saved months: \(months.count)"))
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .contextMenu {
            Button(tr("النسخ الاحتياطي", "Backup"), systemImage: "icloud") { route = .backup }
            hideButton(.dataStatus)
        }
        .accessibilityIdentifier("homeDataStatus")
    }

    private func statusLine(_ ok: Bool, _ text: String, note: String?) -> some View {
        HStack(spacing: 8) {
            Image(systemName: ok ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .font(.footnote).foregroundStyle(ok ? .green : .orange).frame(width: 18)
            Text(text).font(.footnote).lineLimit(1).minimumScaleFactor(0.8)
            Spacer(minLength: 6)
            if let note { Text(note).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
        }
    }

    // MARK: بلا رواتب

    private var welcome: some View {
        VStack(spacing: 14) {
            Image("HomeLogo")
                .resizable()
                .scaledToFit()
                .frame(width: 110, height: 110)
            Text(tr("أهلاً بك", "Welcome"))
                .font(.title2.bold())
            Text(tr("من الإعدادات اختر «استيراد ملفات» لإضافة الرواتب والاستقطاعات.",
                    "Open Settings and choose Import files to add payroll and deductions."))
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 24)
    }

    // MARK: أدوات البطاقات

    /// بطاقة بعنوان ورابط (يفتح صفحة) ومحتوى، مع ضغط مطوّل: فتح وإخفاء.
    private func card<Content: View>(_ kind: HomeCard, link: String, route target: HomeRoute,
                                     @ViewBuilder content: () -> Content) -> some View {
        cardShell(kind) {
            Button {
                route = target
            } label: {
                HStack {
                    Text(kind.title).font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                    Spacer()
                    HStack(spacing: 2) {
                        Text(link)
                        Image(systemName: "chevron.forward").font(.caption2.weight(.bold))
                    }
                    .font(.footnote.weight(.medium))
                    .foregroundStyle(Color.accentColor)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            content()
        }
        .contextMenu {
            Button(link, systemImage: "arrow.forward.circle") { route = target }
            hideButton(kind)
        }
    }

    private func cardShell<Content: View>(_ kind: HomeCard, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) { content() }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(14)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
            .contentShape(.contextMenuPreview, RoundedRectangle(cornerRadius: 20, style: .continuous))
    }

    private func cardHeader(_ kind: HomeCard, link: String?, action: (() -> Void)?) -> some View {
        HStack {
            Text(kind.title).font(.subheadline.weight(.semibold))
            Spacer()
            if let link, let action {
                Button(action: action) {
                    HStack(spacing: 2) {
                        Text(link)
                        Image(systemName: "chevron.forward").font(.caption2.weight(.bold))
                    }
                    .font(.footnote.weight(.medium))
                }
            }
        }
    }

    private func hideButton(_ kind: HomeCard) -> some View {
        Button(tr("إخفاء البطاقة", "Hide card"), systemImage: "eye.slash") {
            var set = HomeCard.hidden(hiddenRaw)
            set.insert(kind)
            hiddenRaw = HomeCard.raw(set)
        }
    }

    private func loadingLine(_ text: String?) -> some View {
        HStack(spacing: 6) {
            ProgressView().controlSize(.small)
            if let text { Text(text).font(.caption).foregroundStyle(.secondary) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, 4)
    }

    /// يفتح التقارير من الرئيسية على شهرها المعروض.
    private func openReports(_ route: ReportsRoute, month: PayrollMonth) {
        UserDefaults.standard.set(month.name, forKey: "reportMonth")
        AppRouter.shared.pendingReport = route
        AppRouter.shared.tab = .payroll
        AppRouter.shared.showReports = true
    }
}
