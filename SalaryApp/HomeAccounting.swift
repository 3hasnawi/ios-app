import SwiftUI

/// موضعا العمل تحت صافي الرواتب؛ يبقيان قابلين للتبديل بالضغط المطوّل.
enum HomeActionKind: String, CaseIterable, Identifiable {
    case reports, salaryAudit, deductionAudit, entities, changes, special
    var id: String { rawValue }
    var title: String {
        switch self {
        case .reports: tr("التقارير", "Reports")
        case .salaryAudit: tr("تدقيق الرواتب", "Payroll audit")
        case .deductionAudit: tr("تدقيق الاستقطاعات", "Deduction audit")
        case .entities: tr("جهات التنفيذ", "Execution entities")
        case .changes: tr("متغيرات الرواتب", "Payroll changes")
        case .special: tr("الكشوف الخاصة", "Special reports")
        }
    }
    var symbol: String {
        switch self {
        case .reports: "doc.text"
        case .salaryAudit: "checklist"
        case .deductionAudit: "checkmark.shield"
        case .entities: "building.columns"
        case .changes: "arrow.left.arrow.right"
        case .special: "star.square"
        }
    }
}

enum HomeActionLayout {
    static let key = "homeAccountingActions"
    static let defaults: [HomeActionKind] = [.reports, .salaryAudit]
    static func decode(_ raw: String) -> [HomeActionKind] {
        let choices = raw.split(separator: "|").compactMap { HomeActionKind(rawValue: String($0)) }
        return choices.count == 2 && choices[0] != choices[1] ? choices : defaults
    }
    static func select(_ kind: HomeActionKind, at index: Int, in raw: String) -> String {
        var choices = decode(raw)
        guard choices.indices.contains(index) else { return raw }
        if let other = choices.firstIndex(of: kind), other != index { choices[other] = choices[index] }
        choices[index] = kind
        return choices.map(\.rawValue).joined(separator: "|")
    }
}

/// حالة واحدة من نتائج التدقيق، وتفتح موضعها داخل التدقيق مباشرة.
struct HomeReviewItem: Identifiable {
    let id: String
    let title: String
    let detail: String
    let symbol: String
    let route: HomeRoute
}

struct HomeAccounting: View {
    let month: PayrollMonth
    let previous: PayrollMonth?
    let overview: HomeOverview?
    let audit: HomeAudit?
    let openReports: () -> Void
    let openRoute: (HomeRoute) -> Void
    let openEntities: () -> Void

    @AppStorage(HomeActionLayout.key) private var actionsRaw = ""
    @Environment(\.colorScheme) private var colorScheme
    @State private var selectedReview = 0

    private let green = Color(red: 0.07, green: 0.25, blue: 0.22)
    // الأخضر الداكن مناسب لسطح البطاقة العلوية، لكنه لا يُقرأ فوق بطاقات الوضع الليلي.
    private var accent: Color {
        colorScheme == .dark ? Color(red: 0.43, green: 0.86, blue: 0.71) : green
    }
    private var cardSurface: Color { Color(.secondarySystemGroupedBackground) }

    var body: some View {
        VStack(spacing: 14) {
            netCard
            HStack(alignment: .top, spacing: 12) {
                actionCard(0)
                actionCard(1)
            }
            reviewStrip
        }
        .animation(.snappy, value: actionsRaw)
    }

    private var netCard: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 8) {
                Text(tr("صافي رواتب \(month.name)", "Net payroll · \(month.name)"))
                    .font(.subheadline).foregroundStyle(.white.opacity(0.83))
                Text(Money.format(overview?.netTotal))
                    .font(.system(size: 35, weight: .bold, design: .rounded))
                    .monospacedDigit().lineLimit(1).minimumScaleFactor(0.55)
                    .foregroundStyle(.white)
                HStack(spacing: 8) {
                    if let now = overview?.netTotal, let before = overview?.previousNetTotal, before > 0 {
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
            .background(green)
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
        .overlay(RoundedRectangle(cornerRadius: 24, style: .continuous).strokeBorder(green.opacity(0.10)))
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

    private func actionCard(_ index: Int) -> some View {
        let kind = HomeActionLayout.decode(actionsRaw)[index]
        return Button { open(kind) } label: {
            VStack(alignment: .leading, spacing: 10) {
                Image(systemName: kind.symbol)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(accent)
                Text(kind.title).font(.headline).foregroundStyle(.primary)
                    .lineLimit(1).minimumScaleFactor(0.8)
                Text(detail(kind)).font(.caption).foregroundStyle(.secondary)
                    .lineLimit(3).frame(maxWidth: .infinity, alignment: .leading)
                Spacer(minLength: 0)
                Text(footnote(kind)).font(.caption2).foregroundStyle(accent)
                    .lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(16)
            .frame(maxWidth: .infinity, minHeight: 166, alignment: .leading)
            .background(cardSurface, in: RoundedRectangle(cornerRadius: 21))
            .overlay(RoundedRectangle(cornerRadius: 21).strokeBorder(accent.opacity(0.18)))
            .contentShape(RoundedRectangle(cornerRadius: 21))
        }
        .buttonStyle(.plain)
        .contextMenu {
            Menu {
                ForEach(HomeActionKind.allCases) { choice in
                    Button {
                        actionsRaw = HomeActionLayout.select(choice, at: index, in: actionsRaw)
                    } label: {
                        if choice == kind { Label(choice.title, systemImage: "checkmark") }
                        else { Text(choice.title) }
                    }
                }
            } label: {
                Label(tr("تخصيص بطاقة البيانات", "Customize data card"), systemImage: "slider.horizontal.3")
            }
        }
        .accessibilityIdentifier(kind == .reports ? "homeReportsEntry" : "homeAction\(index)")
    }

    private func detail(_ kind: HomeActionKind) -> String {
        switch kind {
        case .reports: tr("كشوف الرواتب والاستقطاعات والجهات", "Payroll, deductions and entity reports")
        case .salaryAudit: tr("فحوص الشهر ومقارنة الرواتب", "Monthly checks and payroll comparison")
        case .deductionAudit: tr("التكرار والمطابقة وتغيّر المبالغ", "Duplicates, matching and amount changes")
        case .entities: tr("مبالغ الاستقطاعات حسب الجهة", "Deductions by entity")
        case .changes: tr("الجدد والخارجون وتغيّر الصافي", "New, removed and net pay changes")
        case .special: tr("صندوق الشهداء والرعاية الصحية", "Martyrs fund and health care")
        }
    }

    private func footnote(_ kind: HomeActionKind) -> String {
        switch kind {
        case .reports, .special: return tr("عرض الكشوف ←", "Open reports ←")
        case .salaryAudit:
            let count = audit?.issues.filter { $0.route == "salary" }.count ?? 0
            return audit == nil ? tr("جارٍ الفحص…", "Checking…") :
                tr("\(Num.format(count)) ملاحظات للمراجعة ←", "\(count) issues to review ←")
        case .deductionAudit:
            let count = audit?.issues.filter { $0.route == "deductions" }.count ?? 0
            return audit == nil ? tr("جارٍ الفحص…", "Checking…") :
                tr("\(Num.format(count)) ملاحظات للمراجعة ←", "\(count) issues to review ←")
        case .entities: return tr("\(Num.format(overview?.entityCount ?? 0)) جهة ←", "\(overview?.entityCount ?? 0) entities ←")
        case .changes: return tr("عرض المتغيرات ←", "Open changes ←")
        }
    }

    private func open(_ kind: HomeActionKind) {
        switch kind {
        case .reports, .special: openReports()
        case .salaryAudit, .changes: openRoute(.salary)
        case .deductionAudit: openRoute(.deductions)
        case .entities: openEntities()
        }
    }

    private var reviewItems: [HomeReviewItem] {
        guard let audit else { return [] }
        var items: [HomeReviewItem] = []
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
        if audit == nil {
            HStack(spacing: 12) {
                ProgressView()
                Text(tr("جارٍ فحص الحالات والمتغيرات…", "Checking issues and changes…"))
                    .font(.subheadline).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, minHeight: 108)
            .background(cardSurface, in: RoundedRectangle(cornerRadius: 21))
        } else if reviewItems.isEmpty {
            Label(previous == nil
                  ? tr("لا يوجد شهر سابق للمقارنة؛ فحوص الشهر الحالي فقط", "No previous month; current checks only")
                  : tr("لا توجد ملاحظات أو متغيرات للمراجعة", "No issues or changes to review"),
                  systemImage: previous == nil ? "info.circle" : "checkmark.shield")
                .font(.subheadline.weight(.medium)).foregroundStyle(accent)
                .frame(maxWidth: .infinity, minHeight: 108)
                .background(cardSurface, in: RoundedRectangle(cornerRadius: 21))
        } else {
            VStack(spacing: 2) {
                TabView(selection: $selectedReview) {
                    ForEach(reviewItems.indices, id: \.self) { index in
                        let item = reviewItems[index]
                        Button { openRoute(item.route) } label: {
                            HStack(spacing: 12) {
                                Image(systemName: item.symbol).font(.title3).foregroundStyle(accent)
                                VStack(alignment: .leading, spacing: 5) {
                                    Text(tr("حالة للمراجعة · \(Num.format(index + 1)) من \(Num.format(reviewItems.count))",
                                            "Review item · \(index + 1) of \(reviewItems.count)"))
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
                        .tag(index)
                    }
                }
                .tabViewStyle(.page(indexDisplayMode: .never))
                .frame(height: 112)
                HStack(spacing: 5) {
                    ForEach(reviewItems.indices, id: \.self) { index in
                        Circle().fill(index == selectedReview ? accent : accent.opacity(0.35))
                            .frame(width: index == selectedReview ? 6 : 4, height: index == selectedReview ? 6 : 4)
                    }
                }
                .accessibilityHidden(true)
            }
            .accessibilityIdentifier("homeReviewCarousel")
            .task(id: reviewItems.map(\.id).joined(separator: "|")) {
                selectedReview = 0
                guard reviewItems.count > 1 else { return }
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(7))
                    guard !Task.isCancelled else { return }
                    withAnimation(.easeInOut) { selectedReview = (selectedReview + 1) % reviewItems.count }
                }
            }
        }
    }
}
