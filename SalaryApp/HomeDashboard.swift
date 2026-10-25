import SwiftUI
import SwiftData

/// أرقام الشهر في الشاشة الرئيسية (تُحسب في الخلفية).
nonisolated struct HomeStats: Sendable, Equatable, Codable {
    var netTotal: Double = 0
    var deductionTotal: Double = 0
    var previousNetTotal: Double?
    var newEmployees: Int?
}

@ModelActor
actor HomeStatsLoader {
    func stats(month: String, previous: String?) throws -> HomeStats {
        var result = HomeStats()
        var employees = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        employees.propertiesToFetch = [\.netSalary, \.matchKey]
        let current = try modelContext.fetch(employees)
        result.netTotal = current.reduce(0) { $0 + ($1.netSalary ?? 0) }

        var deductions = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
        deductions.propertiesToFetch = [\.amount]
        result.deductionTotal = try modelContext.fetch(deductions).reduce(0) { $0 + ($1.amount ?? 0) }

        if let previous {
            var prev = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == previous })
            prev.propertiesToFetch = [\.netSalary, \.matchKey]
            let before = try modelContext.fetch(prev)
            result.previousNetTotal = before.reduce(0) { $0 + ($1.netSalary ?? 0) }
            let old = Set(before.map(\.matchKey))
            result.newEmployees = Set(current.map(\.matchKey)).subtracting(old).count
        }
        return result
    }
}

/// الشاشة الرئيسية (بدون شريط بحث؛ البحث من زر العدسة): الشعار خلفية شفافة، ملخص الشهر، الملاك، وآخر عمليات البحث.
struct HomeDashboard: View {
    let months: [PayrollMonth]
    let current: PayrollMonth?
    var onImportPayroll: () -> Void = {}
    var onImportStaff: () -> Void = {}

    @State private var stats: HomeStats?
    @State private var staffState = StaffImportState.shared
    /// يتحدّث تلقائياً عند أي إضافة أو مسح (من أي شاشة).
    @AppStorage(RecentSearches.key) private var recentsData = Data()
    private var recents: [RecentSearches.Item] { RecentSearches.decode(recentsData) }

    private var previous: PayrollMonth? {
        guard let current, let i = months.firstIndex(where: { $0.name == current.name }), i + 1 < months.count else { return nil }
        return months[i + 1]
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {

                if months.isEmpty && StaffInfo.count == 0 {
                    welcome
                }
                if let current {
                    netCard(current)
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 10), GridItem(.flexible(), spacing: 10)], spacing: 10) {
                        tile(tr("الموظفون", "Employees"), Num.format(current.employeeCount),
                             note: stats?.newEmployees.map { tr("+\(Num.format($0)) جديد", "+\(Num.format($0)) new") },
                             noteColor: .green, systemImage: "person.2.fill")
                        tile(tr("الاستقطاعات", "Deductions"), Num.format(current.deductionCount),
                             note: stats.map { Money.format($0.deductionTotal) }, noteColor: .orange, systemImage: "minus.circle.fill")
                        staffTile
                        tile(tr("الأشهر المحفوظة", "Saved months"), Num.format(months.count),
                             note: months.first?.importedAt.formatted(.relative(presentation: .named).locale(AppSettings.locale)),
                             noteColor: .secondary, systemImage: "calendar")
                    }
                } else if StaffInfo.count > 0 {
                    staffTile
                }

                if !recents.isEmpty {
                    recentList
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 4)
            .padding(.bottom, 24)
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
        .task(id: "\(current?.name ?? "")-\(current?.employeeCount ?? 0)-\(months.count)") {
            guard let current else { stats = nil; return }
            // الأرقام محفوظة بعد أول حساب: تظهر فوراً في المرات القادمة، وتُعاد فقط عند تغيّر الأشهر.
            let cacheKey = "homeStats|\(current.name)|\(current.employeeCount)|\(current.deductionCount)|\(previous?.name ?? "")|\(previous?.employeeCount ?? 0)"
            if let data = UserDefaults.standard.data(forKey: "homeStatsCache"),
               let cached = try? JSONDecoder().decode([String: HomeStats].self, from: data)[cacheKey] {
                stats = cached
                return
            }
            let container = SharedModel.container
            let month = current.name
            let previousName = previous?.name
            // يُنشأ في الخلفية حتى لا يعمل الحساب على خيط الواجهة.
            let started = Date()
            stats = try? await Task.detached(priority: .userInitiated) {
                try await HomeStatsLoader(modelContainer: container).stats(month: month, previous: previousName)
            }.value
            PerfLog.record("homeStats", since: started)
            if let stats, let data = try? JSONEncoder().encode([cacheKey: stats]) {
                UserDefaults.standard.set(data, forKey: "homeStatsCache")
            }
        }
    }

    // MARK: - الأجزاء

    private var welcome: some View {
        VStack(spacing: 14) {
            Image("HomeLogo")
                .resizable()
                .scaledToFit()
                .frame(width: 110, height: 110)
            Text(tr("أهلاً بك", "Welcome"))
                .font(.title2.bold())
            Text(tr("ابدأ باستيراد ملفات الرواتب (Excel) وملف الملاك (Access).",
                    "Start by importing payroll files (Excel) and the staff database (Access)."))
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Button(tr("استيراد ملفات الرواتب", "Import payroll files"), systemImage: "square.and.arrow.down", action: onImportPayroll)
                .glassButtonStyle(prominent: true)
            Button(tr("استيراد ملف الملاك", "Import staff database"), systemImage: "person.3.fill", action: onImportStaff)
                .glassButtonStyle()
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 24)
    }

    private func netCard(_ month: PayrollMonth) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(tr("مجموع صافي رواتب \(month.name)", "Total net salaries, \(month.name)"))
                .font(.subheadline.weight(.medium))
                .opacity(0.85)
            if let stats {
                Text(Money.format(stats.netTotal))
                    .font(.system(size: 30, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                if let prev = stats.previousNetTotal, prev > 0, let previous {
                    let change = (stats.netTotal - prev) / prev * 100
                    Text(tr("\(change >= 0 ? "+" : "")\(change.formatted(.number.precision(.fractionLength(1)).locale(AppSettings.locale)))% عن \(previous.name)",
                            "\(change >= 0 ? "+" : "")\(change.formatted(.number.precision(.fractionLength(1)).locale(AppSettings.locale)))% vs \(previous.name)"))
                        .font(.footnote.weight(.medium))
                        .opacity(0.85)
                }
            } else {
                ProgressView().tint(.white)
                    .frame(height: 36)
            }
        }
        .foregroundStyle(.white)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(18)
        .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 24, style: .continuous))
    }

    private var staffTile: some View {
        Button {
            UserDefaults.standard.set(UnifiedScope.staff.rawValue, forKey: "unifiedScope")
            AppRouter.shared.tab = .search
        } label: {
            tile(tr("الملاك", "Staff"),
                 StaffInfo.count > 0 ? Num.format(StaffInfo.count) : "—",
                 note: StaffInfo.count > 0 ? StaffInfo.fileName : tr("لم يُستورد بعد", "Not imported"),
                 noteColor: .secondary, systemImage: "person.3.fill")
        }
        .buttonStyle(.plain)
        .id(staffState.version)
    }

    private func tile(_ title: String, _ value: String, note: String?, noteColor: Color, systemImage: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Label(title, systemImage: systemImage)
                .font(.footnote)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            Text(value)
                .font(.title2.bold().monospacedDigit())
                .lineLimit(1)
                .minimumScaleFactor(0.6)
                .foregroundStyle(.primary)
            Text(note ?? " ")
                .font(.caption)
                .foregroundStyle(noteColor)
                .lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(14)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
    }

    private var recentList: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(tr("آخر من بحثت عنهم", "Recently viewed"))
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                Spacer()
                Button(tr("مسح السجل", "Clear")) {
                    RecentSearches.clear()
                }
                .font(.footnote.weight(.medium))
                .accessibilityIdentifier("clearRecents")
            }
            .padding(.horizontal, 4)
            VStack(spacing: 0) {
                ForEach(Array(recents.prefix(5).enumerated()), id: \.offset) { i, item in
                    Button {
                        ShortcutRouter.shared.pendingSearch = item.text
                        AppRouter.shared.tab = .search
                    } label: {
                        HStack(spacing: 12) {
                            NameAvatar(name: item.text, size: 36)
                            Text(item.text).lineLimit(1).foregroundStyle(.primary)
                            Spacer()
                            if item.inPayroll { SourceTag(kind: .payroll) }
                            if item.inStaff { SourceTag(kind: .staff) }
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 10)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    if i < min(recents.count, 5) - 1 { Divider().padding(.leading, 62) }
                }
            }
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        }
    }
}
