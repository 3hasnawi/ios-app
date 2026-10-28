import WidgetKit
import SwiftUI
import AppIntents

enum SalaryWidgetMetric: String, AppEnum {
    case none, net, employees, deductions, deductionCount, bank, cash, card
    case officers, civil, ranks, contracts, change, audit, entityAmount, entityEmployees, entityRepeated
    case healthcare, martyrs, sport, tax, protection, mobile, pension, books, backup, googleDrive
    case unclassified, bankDeductions, executionDifference, leave, rewards, singleList
    case newEmployees, removedEmployees, changedSalaries, stoppedDeductions, startedDeductions, changedDeductions
    case changedBanks, changedRanks, changedWorkplaces
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "بيانات البطاقة"
    static let caseDisplayRepresentations: [Self: DisplayRepresentation] = [
        .none: "إخفاء البطاقة", .net: "صافي الرواتب", .employees: "عدد المنتسبين", .deductions: "مبلغ استقطاعات التنفيذ",
        .deductionCount: "عدد الاستقطاعات", .bank: "كشف البنك", .cash: "الرواتب كاش", .card: "الرواتب بطاقة",
        .officers: "الضباط", .civil: "الموظفون", .ranks: "المراتب", .contracts: "العقود", .change: "تغير الصافي",
        .audit: "ملاحظات التدقيق", .entityAmount: "مبلغ جهة التنفيذ", .entityEmployees: "عدد موظفي الجهة",
        .entityRepeated: "المكرر في كشف الجهة", .healthcare: "الرعاية الصحية", .martyrs: "صندوق الشهداء",
        .sport: "النشاط الرياضي", .tax: "الضريبة", .protection: "الحماية الاجتماعية", .mobile: "شركة عراق الجوال",
        .pension: "تقاعد وضمان العقود", .books: "الكتب الرسمية", .backup: "حالة النسخ", .googleDrive: "حالة Google Drive",
        .unclassified: "صافي رواتب غير مصنف", .bankDeductions: "استقطاعات المصارف", .executionDifference: "فرق مطابقة التنفيذ",
        .leave: "الإجازات المتراكمة", .rewards: "مكافآت المنتسبين", .singleList: "القائمة المفردة",
        .newEmployees: "المنتسبون الجدد", .removedEmployees: "المنتسبون الخارجون", .changedSalaries: "تغير صافي الرواتب",
        .stoppedDeductions: "الاستقطاعات المتوقفة", .startedDeductions: "الاستقطاعات الجديدة", .changedDeductions: "الاستقطاعات المتغيرة",
        .changedBanks: "تغير الحساب المصرفي", .changedRanks: "تغير الرتبة", .changedWorkplaces: "تغير محل العمل"
    ]
}

struct SalaryWidgetMonthChoice: AppEntity {
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "الشهر"
    static let defaultQuery = SalaryWidgetMonthQuery()
    let id: String
    var displayRepresentation: DisplayRepresentation { DisplayRepresentation(title: "\(id)") }
}
struct SalaryWidgetMonthQuery: EntityStringQuery {
    func entities(for identifiers: [String]) async throws -> [SalaryWidgetMonthChoice] {
        identifiers.map { SalaryWidgetMonthChoice(id: $0) }
    }
    func entities(matching string: String) async throws -> [SalaryWidgetMonthChoice] {
        (SalaryWidgetStore.read()?.months ?? []).filter { $0.name.localizedStandardContains(string) }.map { SalaryWidgetMonthChoice(id: $0.name) }
    }
    func suggestedEntities() async throws -> [SalaryWidgetMonthChoice] {
        (SalaryWidgetStore.read()?.months ?? []).map { SalaryWidgetMonthChoice(id: $0.name) }
    }
}

struct SalaryWidgetEntityChoice: AppEntity {
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "جهة التنفيذ"
    static let defaultQuery = SalaryWidgetEntityQuery()
    let id: String
    var displayRepresentation: DisplayRepresentation { DisplayRepresentation(title: "\(id)") }
}
struct SalaryWidgetEntityQuery: EntityStringQuery {
    private func all() -> [String] {
        Array(Set((SalaryWidgetStore.read()?.months ?? []).flatMap { $0.entities.map(\.title) })).sorted()
    }
    func entities(for identifiers: [String]) async throws -> [SalaryWidgetEntityChoice] {
        identifiers.map { SalaryWidgetEntityChoice(id: $0) }
    }
    func entities(matching string: String) async throws -> [SalaryWidgetEntityChoice] {
        all().filter { $0.localizedStandardContains(string) }.prefix(100).map { SalaryWidgetEntityChoice(id: $0) }
    }
    func suggestedEntities() async throws -> [SalaryWidgetEntityChoice] {
        all().prefix(100).map { SalaryWidgetEntityChoice(id: $0) }
    }
}

struct SalaryWidgetConfiguration: WidgetConfigurationIntent {
    static let title: LocalizedStringResource = "ويدجت الرواتب"
    static let description = IntentDescription("اختر الشهر والبطاقات التي تريد عرضها.")
    @Parameter(title: "الشهر — اتركه فارغاً لآخر شهر") var month: SalaryWidgetMonthChoice?
    @Parameter(title: "جهة التنفيذ") var entity: SalaryWidgetEntityChoice?
    @Parameter(title: "البطاقة الأولى", default: .net) var first: SalaryWidgetMetric
    @Parameter(title: "البطاقة الثانية", default: .employees) var second: SalaryWidgetMetric
    @Parameter(title: "البطاقة الثالثة", default: .deductions) var third: SalaryWidgetMetric
    @Parameter(title: "البطاقة الرابعة", default: .bank) var fourth: SalaryWidgetMetric
    @Parameter(title: "البطاقة الخامسة", default: .audit) var fifth: SalaryWidgetMetric
    @Parameter(title: "البطاقة السادسة", default: .backup) var sixth: SalaryWidgetMetric
    var metrics: [SalaryWidgetMetric] { [first, second, third, fourth, fifth, sixth].filter { $0 != .none } }
}

struct SalaryWidgetEntry: TimelineEntry {
    let date: Date
    let configuration: SalaryWidgetConfiguration
    let snapshot: SalaryWidgetSnapshot?
}
struct SalaryWidgetProvider: AppIntentTimelineProvider {
    func placeholder(in context: Context) -> SalaryWidgetEntry { SalaryWidgetEntry(date: .now, configuration: SalaryWidgetConfiguration(), snapshot: nil) }
    func snapshot(for configuration: SalaryWidgetConfiguration, in context: Context) async -> SalaryWidgetEntry {
        SalaryWidgetEntry(date: .now, configuration: configuration, snapshot: SalaryWidgetStore.read())
    }
    func timeline(for configuration: SalaryWidgetConfiguration, in context: Context) async -> Timeline<SalaryWidgetEntry> {
        let entry = SalaryWidgetEntry(date: .now, configuration: configuration, snapshot: SalaryWidgetStore.read())
        return Timeline(entries: [entry], policy: .after(Date().addingTimeInterval(30 * 60)))
    }
}

struct SalaryWidgetView: View {
    @Environment(\.widgetFamily) private var family
    @Environment(\.colorScheme) private var scheme
    let entry: SalaryWidgetEntry
    private var current: SalaryWidgetMonth? {
        guard let months = entry.snapshot?.months else { return nil }
        if let selected = entry.configuration.month { return months.first { $0.name == selected.id } }
        return months.first
    }
    private var english: Bool { entry.snapshot?.isEnglish ?? false }
    private func label(_ ar: String, _ en: String) -> String { english ? en : ar }
    private var foreground: Color { scheme == .dark ? .white : Color(red: 0.05, green: 0.23, blue: 0.18) }
    private var surface: Color { scheme == .dark ? Color(red: 0.06, green: 0.23, blue: 0.19) : Color(red: 0.92, green: 0.97, blue: 0.94) }
    private var maxCards: Int { family == .systemSmall ? 2 : family == .systemMedium ? 4 : 6 }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let snapshot = entry.snapshot, let month = current {
                HStack {
                    Text(month.name).font(.caption2.weight(.semibold)).lineLimit(1)
                    Spacer(minLength: 0)
                    Image(systemName: "calendar").font(.caption2)
                }.foregroundStyle(foreground.opacity(0.8))
                let metrics = Array(entry.configuration.metrics.prefix(maxCards))
                if metrics.isEmpty {
                    Text(label("اختر بطاقات من إعدادات الويدجت", "Choose cards in widget settings"))
                        .font(.caption2).foregroundStyle(foreground)
                }
                if family == .systemSmall {
                    VStack(alignment: .leading, spacing: 7) {
                        ForEach(Array(metrics.enumerated()), id: \.offset) { item in
                            card(item.element, month: month, snapshot: snapshot, prominent: item.offset == 0)
                        }
                    }
                } else if family == .systemMedium {
                    HStack(alignment: .top, spacing: 6) {
                        if let first = metrics.first {
                            card(first, month: month, snapshot: snapshot, prominent: true)
                        }
                        VStack(spacing: 4) {
                            ForEach(Array(metrics.dropFirst().enumerated()), id: \.offset) { item in
                                card(item.element, month: month, snapshot: snapshot, prominent: false)
                            }
                        }.frame(maxWidth: .infinity)
                    }
                } else {
                    VStack(spacing: 6) {
                        if let first = metrics.first {
                            card(first, month: month, snapshot: snapshot, prominent: true)
                        }
                        LazyVGrid(columns: [GridItem(.flexible(), spacing: 6), GridItem(.flexible(), spacing: 6)], spacing: 6) {
                            ForEach(Array(metrics.dropFirst().enumerated()), id: \.offset) { item in
                                card(item.element, month: month, snapshot: snapshot, prominent: false)
                            }
                        }
                    }
                }
                Spacer(minLength: 0)
                Text("\(label("تحديث", "Updated")): \(snapshot.updatedAt.formatted(.dateTime.month(.twoDigits).day(.twoDigits).hour().minute()))")
                    .font(.system(size: 9)).foregroundStyle(foreground.opacity(0.65)).lineLimit(1)
            } else {
                Text(entry.snapshot == nil
                     ? (SalaryWidgetStore.available ? label("افتح التطبيق لتحديث الويدجت", "Open the app to update the widget") : label("توقيع التطبيق لا يتيح مشاركة بيانات الويدجت", "App signing does not allow widget data sharing"))
                     : (entry.snapshot?.months.isEmpty == true ? label("لا توجد بيانات رواتب", "No payroll data") : label("الشهر المختار غير متاح", "The selected month is unavailable")))
                    .font(.caption).foregroundStyle(foreground)
            }
        }
        .environment(\.layoutDirection, english ? .leftToRight : .rightToLeft)
        .privacySensitive()
        .containerBackground(for: .widget) { surface }
    }

    private func card(_ metric: SalaryWidgetMetric, month: SalaryWidgetMonth, snapshot: SalaryWidgetSnapshot, prominent: Bool) -> some View {
        let content = data(metric, month: month, snapshot: snapshot)
        return VStack(alignment: .leading, spacing: 3) {
            Text(content.0).font(.system(size: prominent ? 11 : 10, weight: .medium)).lineLimit(1).minimumScaleFactor(0.7)
            Text(content.1).font(.system(size: prominent ? 20 : 13, weight: .bold, design: .rounded))
                .monospacedDigit().lineLimit((metric == .backup || metric == .googleDrive) ? 2 : 1).minimumScaleFactor(0.45)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(family == .systemSmall ? 2 : 6)
        .background(foreground.opacity(family == .systemSmall ? 0 : 0.08), in: RoundedRectangle(cornerRadius: 9))
        .foregroundStyle(foreground)
        .accessibilityElement(children: .combine)
    }

    private func number(_ value: Double, snapshot: SalaryWidgetSnapshot, decimals: Int = 0) -> String {
        let f = NumberFormatter(); f.numberStyle = .decimal; f.maximumFractionDigits = decimals
        f.locale = Locale(identifier: snapshot.arabicDigits ? "ar-u-nu-arab" : "en_US_POSIX")
        return f.string(from: NSNumber(value: value)) ?? "—"
    }
    private func data(_ metric: SalaryWidgetMetric, month: SalaryWidgetMonth, snapshot: SalaryWidgetSnapshot) -> (String, String) {
        let entity = entry.configuration.entity.flatMap { selected in month.entities.first { $0.title == selected.id } }
        func n(_ value: Int) -> String { number(Double(value), snapshot: snapshot) }
        func m(_ value: Double) -> String { number(value, snapshot: snapshot, decimals: 2) }
        switch metric {
        case .none: return ("", "")
        case .net: return (label("صافي الرواتب", "Net payroll"), m(month.net))
        case .employees: return (label("عدد المنتسبين", "Employee count"), n(month.employees))
        case .deductions: return (label("استقطاعات التنفيذ", "Execution deductions"), m(month.deductionAmount))
        case .deductionCount: return (label("عدد الاستقطاعات", "Deduction entries"), n(month.deductionCount))
        case .bank: return (month.bankMatched ? label("كشف البنك", "Bank statement") : label("كشف البنك · أولي", "Bank statement · provisional"), month.bankTotal.map(m) ?? "—")
        case .cash: return (label("الرواتب كاش", "Cash payroll"), m(month.cash))
        case .card: return (label("الرواتب بطاقة", "Card payroll"), m(month.card))
        case .officers: return (label("الضباط", "Officers"), n(month.officers))
        case .civil: return (label("الموظفون", "Employees"), n(month.civil))
        case .ranks: return (label("المراتب", "Ranks"), n(month.ranks))
        case .contracts: return (label("العقود", "Contracts"), n(month.contracts))
        case .change: return (label("التغير عن الشهر السابق", "Change vs previous month"), month.changePercent.map { number($0, snapshot: snapshot, decimals: 1) + "%" } ?? "—")
        case .audit: return (label("ملاحظات التدقيق", "Audit issues"), month.auditCount.map(n) ?? "—")
        case .entityAmount: return (entity?.title ?? label("اختر جهة التنفيذ", "Choose an entity"), entity.map { m($0.amount) } ?? "—")
        case .entityEmployees: return (label("عدد موظفي الجهة", "Entity employees"), entity.map { n($0.rows) } ?? "—")
        case .entityRepeated: return (label("المكرر في الجهة", "Repeated rows"), entity.map { n(max(0, $0.rows - $0.employees)) } ?? "—")
        case .healthcare: return (label("الرعاية الصحية", "Health care"), m(month.healthcare))
        case .martyrs: return (label("صندوق الشهداء", "Martyrs fund"), m(month.martyrs))
        case .sport: return (label("النشاط الرياضي", "Sport"), m(month.sport))
        case .tax: return (label("الضريبة", "Tax"), m(month.tax))
        case .protection: return (label("الحماية الاجتماعية", "Social protection"), m(month.protection))
        case .mobile: return (label("شركة عراق الجوال", "Iraq Mobile"), m(month.mobile))
        case .pension: return (label("تقاعد وضمان العقود", "Contract pension"), m(month.pension))
        case .books: return (label("الكتب الرسمية", "Documents"), n(month.bookCount))
        case .backup: return (label("النسخ الاحتياطي", "Backup"), snapshot.backupStatus)
        case .googleDrive: return ("Google Drive", snapshot.googleDriveStatus ?? "—")
        case .unclassified: return (label("صافي غير مصنف", "Unclassified net"), m(month.unclassified))
        case .bankDeductions: return (label("استقطاعات المصارف", "Bank deductions"), m(month.bankDeductions))
        case .executionDifference: return (label("فرق مطابقة التنفيذ", "Execution difference"), m(month.executionDifference))
        case .leave: return (label("الإجازات المتراكمة", "Accumulated leave"), m(month.leave))
        case .rewards: return (label("مكافآت المنتسبين", "Employee rewards"), m(month.rewards))
        case .singleList: return (label("القائمة المفردة", "Single list"), m(month.singleList))
        case .newEmployees: return (label("المنتسبون الجدد", "New employees"), month.newEmployees.map(n) ?? "—")
        case .removedEmployees: return (label("المنتسبون الخارجون", "Removed employees"), month.removedEmployees.map(n) ?? "—")
        case .changedSalaries: return (label("تغير صافي الرواتب", "Net changes"), month.changedSalaries.map(n) ?? "—")
        case .stoppedDeductions: return (label("الاستقطاعات المتوقفة", "Stopped deductions"), month.stoppedDeductions.map(n) ?? "—")
        case .startedDeductions: return (label("الاستقطاعات الجديدة", "New deductions"), month.startedDeductions.map(n) ?? "—")
        case .changedDeductions: return (label("الاستقطاعات المتغيرة", "Changed deductions"), month.changedDeductions.map(n) ?? "—")
        case .changedBanks: return (label("تغير الحساب المصرفي", "Bank account changes"), month.changedBanks.map(n) ?? "—")
        case .changedRanks: return (label("تغير الرتبة", "Rank changes"), month.changedRanks.map(n) ?? "—")
        case .changedWorkplaces: return (label("تغير محل العمل", "Workplace changes"), month.changedWorkplaces.map(n) ?? "—")
        }
    }
}

struct SalaryOverviewWidget: Widget {
    let kind = "SalaryOverviewWidget"
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: kind, intent: SalaryWidgetConfiguration.self, provider: SalaryWidgetProvider()) { entry in
            SalaryWidgetView(entry: entry)
        }
        .configurationDisplayName("ويدجت الرواتب")
        .description("اختر الشهر والبطاقات التي تناسبك.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge])
    }
}

@main
struct SalaryWidgetBundle: WidgetBundle {
    var body: some Widget { SalaryOverviewWidget() }
}
