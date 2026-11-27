import SwiftUI

enum HomeReviewPriority: Int {
    case high, attention, information

    var label: String {
        switch self {
        case .high: tr("أولوية عالية", "High priority")
        case .attention: tr("تحتاج مراجعة", "Needs review")
        case .information: tr("للاطلاع", "For information")
        }
    }

    var color: Color {
        switch self {
        case .high: .red
        case .attention: .orange
        case .information: .blue
        }
    }
}

/// حالة واحدة من نتائج التدقيق، وتفتح موضعها داخل التدقيق مباشرة.
struct HomeReviewItem: Identifiable {
    let id: String
    let title: String
    let detail: String
    let symbol: String
    let priority: HomeReviewPriority
    let route: HomeRoute
}

enum HomePalette {
    static let green = Color(red: 0.07, green: 0.25, blue: 0.22)
    static func accent(for scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(red: 0.43, green: 0.86, blue: 0.71) : green
    }
}

struct HomeAccounting<Shortcuts: View>: View {
    let month: PayrollMonth
    let previous: PayrollMonth?
    let overview: HomeOverview?
    let audit: HomeAudit?
    let openRoute: (HomeRoute) -> Void
    let openEntities: () -> Void
    @ViewBuilder var shortcuts: () -> Shortcuts

    @Environment(\.colorScheme) private var colorScheme
    @AppStorage("homeHeadlineDeductions") private var showDeductions = true

    // الأخضر الداكن مناسب لسطح البطاقة العلوية، لكنه لا يُقرأ فوق بطاقات الوضع الليلي.
    private var accent: Color {
        HomePalette.accent(for: colorScheme)
    }
    private var cardSurface: Color { Color(.secondarySystemGroupedBackground) }

    var body: some View {
        VStack(spacing: 14) {
            netCard
                .onTapGesture { if showDeductions { openEntities() } else { openRoute(.payrollBreakdown) } }
                .accessibilityAddTraits(.isButton)
                .accessibilityAction { if showDeductions { openEntities() } else { openRoute(.payrollBreakdown) } }
                .contextMenu {
                    Button(tr("مجموع الاستقطاعات", "Total deductions")) { showDeductions = true }
                    Button(tr("صافي الرواتب", "Net payroll")) { showDeductions = false }
                }
            reviewStrip
            shortcuts()
        }
    }

    private var netCard: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 8) {
                Text(showDeductions ? tr("مجموع الاستقطاعات · \(month.name)", "Total deductions · \(month.name)") : tr("صافي رواتب \(month.name)", "Net payroll · \(month.name)"))
                    .font(.subheadline).foregroundStyle(.white.opacity(0.83))
                Text(Money.format(showDeductions ? overview?.entityTotal : overview?.netTotal))
                    .font(.system(size: 35, weight: .bold, design: .rounded))
                    .monospacedDigit().lineLimit(1).minimumScaleFactor(0.55)
                    .foregroundStyle(.white)
                HStack(spacing: 8) {
                    if !showDeductions, let now = overview?.netTotal, let before = overview?.previousNetTotal, before > 0 {
                        let change = (now - before) / before * 100
                        Text(tr("\(change >= 0 ? "+" : "")\(change.formatted(.number.precision(.fractionLength(1)).locale(AppSettings.locale)))% عن الشهر السابق",
                                "\(change >= 0 ? "+" : "")\(change.formatted(.number.precision(.fractionLength(1)).locale(AppSettings.locale)))% vs previous month"))
                            .lineLimit(1).minimumScaleFactor(0.75)
                    }
                    Spacer(minLength: 4)
                    Text(tr("\(Num.format(overview?.employees ?? month.employeeCount)) موظف", "\(Num.format(overview?.employees ?? month.employeeCount)) employees"))
                        .lineLimit(1).minimumScaleFactor(0.75)
                }
                .font(.caption).foregroundStyle(.white.opacity(0.85))
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(20)
            .background(HomePalette.green)
            HStack(spacing: 0) {
                category(0, title: tr("ضباط", "Officers"), symbol: "shield.lefthalf.filled")
                category(1, title: tr("موظفون", "Employees"), symbol: "briefcase")
                category(2, title: tr("مراتب", "Ranks"), symbol: "chevron.down.2")
                category(3, title: tr("عقود", "Contracts"), symbol: "doc.text")
            }
            .padding(.vertical, 12)
            .background(cardSurface)
        }
        .clipShape(RoundedRectangle(cornerRadius: 24, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 24, style: .continuous).strokeBorder(HomePalette.green.opacity(0.10)))
        .accessibilityIdentifier("homeNetCard")
    }

    private func category(_ index: Int, title: String, symbol: String) -> some View {
        VStack(spacing: 3) {
            Image(systemName: symbol).font(.footnote.weight(.semibold))
            if let overview, overview.categories.indices.contains(index) {
                Text(Num.format(overview.categories[index].count))
                    .font(.subheadline.weight(.bold)).monospacedDigit()
            } else {
                Text("—").font(.subheadline.weight(.bold))
            }
            Text(title).font(.caption2).lineLimit(1).minimumScaleFactor(0.8)
        }
        .foregroundStyle(accent)
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .combine)
    }

    private var reviewItems: [HomeReviewItem] {
        guard let audit else { return [] }
        var items: [HomeReviewItem] = []
        for kind in DeductionEntityAlertKind.allCases {
            let count = kind == .added ? audit.addedEntities : audit.missingEntities
            if count > 0 {
                items.append(HomeReviewItem(id: "entity-" + kind.rawValue, title: kind.title,
                                            detail: tr("\(Num.format(count)) جهة · \(month.name)", "\(count) entities · \(month.name)"),
                                            symbol: "building.columns", priority: .attention, route: .entityAlert(kind)))
            }
        }
        if !audit.salaryAvailable {
            items.append(HomeReviewItem(id: "salary-unavailable",
                                        title: tr("تدقيق الرواتب غير مكتمل", "Payroll audit incomplete"),
                                        detail: tr("افتح التدقيق لمراجعة بيانات هذا الشهر", "Open the audit to review this month"),
                                        symbol: "exclamationmark.triangle", priority: .attention, route: .salary))
        }
        if audit.deductionComparisonBlocked {
            items.append(HomeReviewItem(id: "deduction-comparison-blocked",
                                        title: tr("مقارنة الاستقطاعات تحتاج مراجعة", "Deduction comparison needs review"),
                                        detail: tr("أحد الشهرين بلا قيود استقطاع", "One of the months has no deduction entries"),
                                        symbol: "exclamationmark.triangle", priority: .attention, route: .deductions))
        }
        for issue in audit.issues where issue.id != "bank" {
            let title = issue.count > 0 ? tr("\(issue.title) · \(Num.format(issue.count))", "\(issue.title) · \(issue.count)") : issue.title
            let destination: HomeRoute = issue.route == "salary"
                ? (issue.count > 0 ? .salaryCheck(issue.id) : .salary)
                : (issue.count > 0 ? .deductionCheck(issue.id) : .deductions)
            let priority: HomeReviewPriority = ["totals", "duplicateDeductions", "iban", "calc", "duplicates", "ids"].contains(issue.id) ? .high : .attention
            items.append(HomeReviewItem(id: "check-\(issue.route)-\(issue.id)",
                                        title: title,
                                        detail: issue.detail, symbol: "exclamationmark.triangle",
                                        priority: priority, route: destination))
        }
        func addSalary(_ kind: SalaryChangeKind, _ count: Int) {
            guard count > 0 else { return }
            items.append(HomeReviewItem(id: "salary-\(kind.rawValue)",
                                        title: tr("\(kind.reportTitle) · \(Num.format(count))", "\(kind.reportTitle) · \(count)"),
                                        detail: previous.map { tr("مقارنة بـ\($0.name)", "Compared with \($0.name)") }
                                            ?? tr("متغيرات الرواتب", "Payroll changes"),
                                        symbol: kind == .bank ? "creditcard" : "arrow.left.arrow.right",
                                        priority: [.bank, .removed, .net].contains(kind) ? .attention : .information, route: .salaryChange(kind)))
        }
        func addDeduction(_ kind: DeductionGroupKind, _ count: Int) {
            guard count > 0 else { return }
            items.append(HomeReviewItem(id: "deduction-\(kind.rawValue)",
                                        title: tr("\(kind.reportTitle) · \(Num.format(count))", "\(kind.reportTitle) · \(count)"),
                                        detail: previous.map { tr("مقارنة بـ\($0.name)", "Compared with \($0.name)") }
                                            ?? tr("متغيرات الاستقطاعات", "Deduction changes"),
                                        symbol: "minus.circle", priority: kind == .started ? .information : .attention, route: .deductionChange(kind)))
        }
        addSalary(.bank, audit.bank)
        addSalary(.removed, audit.removed)
        addDeduction(.stopped, audit.stopped)
        addDeduction(.changed, audit.changed)
        addSalary(.net, audit.net)
        addSalary(.rank, audit.rank)
        addSalary(.workplace, audit.workplace)
        for kind in SalaryChangeKind.profileKinds { addSalary(kind, audit.profileCounts[kind.rawValue] ?? 0) }
        addSalary(.added, audit.added)
        addDeduction(.started, audit.started)
        return items.sorted { $0.priority.rawValue < $1.priority.rawValue }
    }

    @ViewBuilder
    private var reviewStrip: some View {
        let items = reviewItems
        if audit == nil {
            HStack(spacing: 12) {
                ProgressView()
                Text(tr("جارٍ فحص الحالات والمتغيرات…", "Checking issues and changes…"))
                    .font(.subheadline).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, minHeight: 108)
            .background(cardSurface, in: RoundedRectangle(cornerRadius: 21))
        } else if items.isEmpty {
            Label(previous == nil
                  ? tr("لا يوجد شهر سابق للمقارنة؛ فحوص الشهر الحالي فقط", "No previous month; current checks only")
                  : tr("لا توجد ملاحظات أو متغيرات للمراجعة", "No issues or changes to review"),
                  systemImage: previous == nil ? "info.circle" : "checkmark.shield")
                .font(.subheadline.weight(.medium)).foregroundStyle(accent)
                .frame(maxWidth: .infinity, minHeight: 108)
                .background(cardSurface, in: RoundedRectangle(cornerRadius: 21))
        } else {
            HomeReviewPager(items: items, openRoute: openRoute)
                .id(month.name)
        }
    }
}

private struct HomeReviewPager: View {
    let items: [HomeReviewItem]
    let openRoute: (HomeRoute) -> Void
    @ScaledMetric(relativeTo: .body) private var height: CGFloat = 122
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.accessibilityVoiceOverEnabled) private var voiceOver
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var selection: String?
    @State private var visible = false
    @GestureState private var dragging = false

    var body: some View {
        if !items.isEmpty {
            let index = items.firstIndex { $0.id == selection } ?? 0
            let current = items[index]
            let nextID = items[(index + 1) % items.count].id
            let pages = Binding(get: { current.id }, set: { selection = $0 })
            let canRotate = items.count > 1 && visible && scenePhase == .active && !dragging && !voiceOver
            TabView(selection: pages) {
                ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                    Button {
                        selection = item.id
                        openRoute(item.route)
                    } label: {
                        VStack(alignment: .leading, spacing: 7) {
                            HStack {
                                Label(item.priority.label, systemImage: item.symbol)
                                    .font(.caption2.weight(.semibold))
                                    .padding(.horizontal, 7).padding(.vertical, 3)
                                    .background(item.priority.color.opacity(0.20), in: Capsule())
                                Spacer()
                                Text(tr("حالة \(Num.format(index + 1)) من \(Num.format(items.count))", "Review \(index + 1) of \(items.count)"))
                                    .font(.caption).monospacedDigit().foregroundStyle(.secondary)
                            }
                            HStack {
                                Text(item.title).font(.subheadline.weight(.semibold))
                                Spacer()
                                Image(systemName: "chevron.forward").font(.caption).foregroundStyle(.secondary)
                            }
                            Text(item.detail).font(.caption).foregroundStyle(.secondary)
                        }
                        .padding(.horizontal, 16)
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityActions {
                        if items.count > 1 {
                            Button(tr("الحالة السابقة", "Previous review item")) { selection = items[(index + items.count - 1) % items.count].id }
                            Button(tr("الحالة التالية", "Next review item")) { selection = items[(index + 1) % items.count].id }
                        }
                    }
                    .accessibilityIdentifier("homeReview-" + item.id)
                    .tag(item.id)
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .never))
            .frame(height: height)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 20))
            .clipShape(RoundedRectangle(cornerRadius: 20))
            .simultaneousGesture(DragGesture(minimumDistance: 4).updating($dragging) { _, inProgress, _ in inProgress = true })
            .onScrollVisibilityChange(threshold: 0.5) { visible = $0 }
            .task(id: canRotate ? nextID : nil) {
                guard canRotate else { return }
                do { try await Task.sleep(for: .seconds(5)) } catch { return }
                guard !Task.isCancelled else { return }
                withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.28)) {
                    selection = nextID
                }
            }
        }
    }
}
