import SwiftUI

/// حالة واحدة من نتائج التدقيق، وتفتح موضعها داخل التدقيق مباشرة.
struct HomeReviewItem: Identifiable {
    let id: String
    let title: String
    let detail: String
    let symbol: String
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
    @ScaledMetric(relativeTo: .body) private var reviewHeight: CGFloat = 112
    @State private var selectedReviewID: String?

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
                                            symbol: "building.columns", route: .entityAlert(kind)))
            }
        }
        if !audit.salaryAvailable {
            items.append(HomeReviewItem(id: "salary-unavailable",
                                        title: tr("تدقيق الرواتب غير مكتمل", "Payroll audit incomplete"),
                                        detail: tr("افتح التدقيق لمراجعة بيانات هذا الشهر", "Open the audit to review this month"),
                                        symbol: "exclamationmark.triangle", route: .salary))
        }
        if audit.deductionComparisonBlocked {
            items.append(HomeReviewItem(id: "deduction-comparison-blocked",
                                        title: tr("مقارنة الاستقطاعات تحتاج مراجعة", "Deduction comparison needs review"),
                                        detail: tr("أحد الشهرين بلا قيود استقطاع", "One of the months has no deduction entries"),
                                        symbol: "exclamationmark.triangle", route: .deductions))
        }
        for issue in audit.issues where issue.id != "bank" {
            let title = issue.count > 0 ? tr("\(issue.title) · \(Num.format(issue.count))", "\(issue.title) · \(issue.count)") : issue.title
            let destination: HomeRoute = issue.route == "salary"
                ? (issue.count > 0 ? .salaryCheck(issue.id) : .salary)
                : (issue.count > 0 ? .deductionCheck(issue.id) : .deductions)
            items.append(HomeReviewItem(id: "check-\(issue.route)-\(issue.id)",
                                        title: title,
                                        detail: issue.detail, symbol: "exclamationmark.triangle",
                                        route: destination))
        }
        func addSalary(_ kind: SalaryChangeKind, _ count: Int) {
            guard count > 0 else { return }
            items.append(HomeReviewItem(id: "salary-\(kind.rawValue)",
                                        title: tr("\(kind.reportTitle) · \(Num.format(count))", "\(kind.reportTitle) · \(count)"),
                                        detail: previous.map { tr("مقارنة بـ\($0.name)", "Compared with \($0.name)") }
                                            ?? tr("متغيرات الرواتب", "Payroll changes"),
                                        symbol: kind == .bank ? "creditcard" : "arrow.left.arrow.right",
                                        route: .salaryChange(kind)))
        }
        func addDeduction(_ kind: DeductionGroupKind, _ count: Int) {
            guard count > 0 else { return }
            items.append(HomeReviewItem(id: "deduction-\(kind.rawValue)",
                                        title: tr("\(kind.reportTitle) · \(Num.format(count))", "\(kind.reportTitle) · \(count)"),
                                        detail: previous.map { tr("مقارنة بـ\($0.name)", "Compared with \($0.name)") }
                                            ?? tr("متغيرات الاستقطاعات", "Deduction changes"),
                                        symbol: "minus.circle", route: .deductionChange(kind)))
        }
        addSalary(.bank, audit.bank)
        addSalary(.removed, audit.removed)
        addDeduction(.stopped, audit.stopped)
        addDeduction(.changed, audit.changed)
        addSalary(.net, audit.net)
        addSalary(.rank, audit.rank)
        addSalary(.workplace, audit.workplace)
        addSalary(.added, audit.added)
        addDeduction(.started, audit.started)
        return items
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
        } else if let first = items.first {
            let currentID = items.first { $0.id == selectedReviewID }?.id ?? first.id
            let selection = Binding(get: { currentID }, set: { selectedReviewID = $0 })
            VStack(spacing: 2) {
                TabView(selection: selection) {
                    ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                        Button {
                            selectedReviewID = item.id
                            openRoute(item.route)
                        } label: {
                            HStack(spacing: 12) {
                                Image(systemName: item.symbol).font(.title3).foregroundStyle(accent)
                                VStack(alignment: .leading, spacing: 5) {
                                    Text(tr("حالة للمراجعة · \(Num.format(index + 1)) من \(Num.format(items.count))",
                                            "Review item · \(index + 1) of \(items.count)"))
                                        .font(.caption).foregroundStyle(.secondary)
                                    Text(item.title).font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                                        .lineLimit(2)
                                    Text(item.detail).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                }
                                Spacer(minLength: 0)
                                Image(systemName: "chevron.forward").font(.caption.weight(.semibold)).foregroundStyle(accent)
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(16)
                            .background(cardSurface, in: RoundedRectangle(cornerRadius: 21))
                            .contentShape(RoundedRectangle(cornerRadius: 21))
                        }
                        .buttonStyle(.plain)
                        .accessibilityActions {
                            if index > 0 {
                                Button(tr("الحالة السابقة", "Previous review item")) { selectedReviewID = items[index - 1].id }
                            }
                            if index + 1 < items.count {
                                Button(tr("الحالة التالية", "Next review item")) { selectedReviewID = items[index + 1].id }
                            }
                        }
                        .accessibilityIdentifier("homeReview-" + item.id)
                        .tag(item.id)
                    }
                }
                .tabViewStyle(.page(indexDisplayMode: .never))
                .frame(height: reviewHeight)
                HStack(spacing: 5) {
                    ForEach(items) { item in
                        Circle().fill(item.id == currentID ? accent : accent.opacity(0.35))
                            .frame(width: item.id == currentID ? 6 : 4, height: item.id == currentID ? 6 : 4)
                    }
                }
                .accessibilityHidden(true)
            }
            .accessibilityIdentifier("homeReviewCarousel")
            .onChange(of: currentID, initial: true) { _, id in
                selectedReviewID = id
            }
        }
    }
}
