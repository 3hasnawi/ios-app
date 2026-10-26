import SwiftUI
import SwiftData
import UIKit

/// شخص في البحث الموحّد: راتبه في آخر شهر (إن وُجد) وسجله في الملاك (إن وُجد)،
/// مربوطان بالاسم بعد توحيد الهمزات وحذف المسافات.
/// البطاقة تُرسم من قيم جاهزة (تُحسب في الخلفية)، والكائنات نفسها تُجلب فقط عند فتح صفحة الشخص.
struct UnifiedPerson: Hashable, Identifiable {
    let hit: UnifiedHit

    var id: String { hit.key }
    var name: String { hit.name }
    var subtitle: String { hit.subtitle }
    var hasPayroll: Bool { hit.employeeID != nil }
    var hasStaff: Bool { hit.staffID != nil }

    @MainActor var employee: Employee? {
        hit.employeeID.flatMap { SharedModel.container.mainContext.model(for: $0) as? Employee }
    }
    @MainActor var staff: StaffMember? {
        hit.staffID.flatMap { SharedModel.container.mainContext.model(for: $0) as? StaffMember }
    }
}

enum UnifiedScope: String, CaseIterable, Identifiable {
    case all, payroll, staff
    var id: String { rawValue }
    var title: String {
        switch self {
        case .all: tr("الكل", "All")
        case .payroll: tr("الرواتب", "Payroll")
        case .staff: tr("الملاك", "Staff")
        }
    }
}

/// آخر عمليات البحث (الأشخاص الذين فُتحت صفحاتهم).
enum RecentSearches {
    struct Item: Codable, Hashable {
        var text: String
        var inPayroll: Bool
        var inStaff: Bool
    }

    static let key = "unifiedRecentSearches"

    static var items: [Item] {
        decode(UserDefaults.standard.data(forKey: key) ?? Data())
    }

    static func decode(_ data: Data) -> [Item] {
        data.isEmpty ? [] : (try? JSONDecoder().decode([Item].self, from: data)) ?? []
    }

    static func add(_ item: Item) {
        var list = items.filter { $0.text != item.text }
        list.insert(item, at: 0)
        if let data = try? JSONEncoder().encode(Array(list.prefix(8))) {
            UserDefaults.standard.set(data, forKey: key)
        }
    }

    static func clear() { UserDefaults.standard.removeObject(forKey: key) }

    static func remove(_ text: String) {
        let list = items.filter { $0.text != text }
        if let data = try? JSONEncoder().encode(list) { UserDefaults.standard.set(data, forKey: key) }
    }
}

/// نسخ نص إلى الحافظة.
@MainActor func copyToClipboard(_ text: String) { UIPasteboard.general.string = text }

// MARK: - تبويب البحث

struct UnifiedSearchView: View {
    private static var initialSearch: String {
        LaunchOptions.startTab == "search" || !LaunchOptions.openFirst ? LaunchOptions.search : ""
    }

    @State private var search = Self.initialSearch
    @State private var appliedSearch = Self.initialSearch
    @AppStorage("unifiedMode") private var modeRaw = StaffSearchMode.name.rawValue
    /// يتحدّث تلقائياً عند أي إضافة أو مسح (من أي شاشة).
    @AppStorage(RecentSearches.key) private var recentsData = Data()
    private var recents: [RecentSearches.Item] { RecentSearches.decode(recentsData) }
    @State private var importState = StaffImportState.shared
    @State private var router = ShortcutRouter.shared
    @State private var path: [UnifiedPerson] = []
    /// مشاركة تقرير الراتب من الضغط المطوّل على بطاقة النتيجة.
    @State private var shareTarget: Employee?
    @State private var people: [UnifiedPerson] = []
    @State private var loadedKey = ""
    @State private var autopilot = AutoPilot.shared
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]

    /// البحث دائماً في الرواتب والملاك معاً (بلا شريط نطاق).
    private var scope: UnifiedScope { .all }
    private var mode: StaffSearchMode { StaffSearchMode(rawValue: modeRaw) ?? .name }
    /// يشمل البحث الصوتي: تُحذف النقطة وعلامات الترقيم التي يضيفها الإملاء.
    private var trimmed: String { ArabicText.cleanQuery(appliedSearch) }

    /// كل الأشهر من الأحدث: لكل شخص يُعرض آخر شهر يوجد فيه اسمه، والشهر يظهر في بطاقته
    /// ويُختار غيره من صفحة الشخص.
    private var monthNames: [String] { months.map(\.name) }

    private var queryKey: String {
        "\(trimmed)|\(mode.rawValue)|\(scope.rawValue)|\(monthNames.joined(separator: ","))|\(importState.version)"
    }

    var body: some View {
        NavigationStack(path: $path) {
            List {
                if trimmed.isEmpty {
                    emptyState
                } else if loadedKey == queryKey && people.isEmpty {
                    Section {
                        ContentUnavailableView.search(text: trimmed)
                            .listRowBackground(Color.clear)
                    }
                } else {
                    Section {
                        ForEach(people) { person in
                            NavigationLink(value: person) {
                                UnifiedCard(person: person)
                            }
                            .accessibilityIdentifier("resultCard")
                            .contextMenu {
                                Button(tr("فتح", "Open"), systemImage: "person.text.rectangle") { path = [person] }
                                if person.hasPayroll {
                                    // لا نجلب كائن الراتب لكل بطاقة أثناء رسم النتائج.
                                    Button(tr("مشاركة تقرير الراتب", "Share salary report"), systemImage: "square.and.arrow.up") {
                                        shareTarget = person.employee
                                    }
                                }
                                Button(tr("نسخ الاسم", "Copy name"), systemImage: "doc.on.doc") { copyToClipboard(person.name) }
                            }
                        }
                    } header: {
                        if loadedKey == queryKey {
                            Text(tr("\(Num.format(people.count)) شخص", "\(Num.format(people.count)) people"))
                        } else {
                            ProgressView()
                        }
                    }
                }
            }
            .listStyle(.insetGrouped)
            .accessibilityIdentifier("searchList")
            .scrollDismissesKeyboard(.immediately)
            .searchable(text: $search, prompt: Text(mode.prompt))
            .onSubmit(of: .search) { appliedSearch = search }
            .task(id: search) {
                if !search.isEmpty {
                    try? await Task.sleep(for: .milliseconds(250))
                    guard !Task.isCancelled else { return }
                }
                if appliedSearch != search { appliedSearch = search }
            }
            // البحث نفسه: يُحسب عند تغيّر النص أو الخيارات.
            .task(id: queryKey) {
                let key = queryKey
                guard !trimmed.isEmpty else { people = []; loadedKey = key; return }
                let started = Date()
                let found = (try? await UnifiedFinder.find(trimmed, mode: mode, scope: scope, months: monthNames)) ?? []
                guard !Task.isCancelled else { return }
                people = found
                loadedKey = key
                PerfLog.record("search", since: started)
                if LaunchOptions.openPerson, path.isEmpty, let first = people.first { path = [first] }
            }
            .onChange(of: autopilot.openRequest) {
                if autopilot.openIndex < people.count { path = [people[autopilot.openIndex]] }
            }
            .onChange(of: autopilot.backRequest) { path = [] }
            .onChange(of: router.pendingSearch, initial: true) { _, name in
                guard let name else { return }
                router.pendingSearch = nil
                path = []
                modeRaw = StaffSearchMode.name.rawValue
                search = name
                appliedSearch = name
            }
            .navigationTitle(tr("بحث", "Search"))
            // عنوان صغير بلا فراغ فوقه، ونوع البحث (الاسم، اسم الأم، الرقم الإحصائي، الموحد) قائمة صغيرة بجانبه.
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) { modeMenu }
            }
            .sheet(item: $shareTarget) { ShareOptionsSheet(employee: $0) }
            .navigationDestination(for: UnifiedPerson.self) { person in
                UnifiedPersonView(person: person)
                    .onAppear {
                        RecentSearches.add(.init(text: person.name, inPayroll: person.hasPayroll, inStaff: person.hasStaff))
                    }
            }
        }
    }

    /// نوع البحث: الاسم، اسم الأم، الرقم الإحصائي، الرقم الموحد.
    private var modeMenu: some View {
        Menu {
            Picker(tr("البحث حسب", "Search by"), selection: $modeRaw) {
                ForEach(StaffSearchMode.allCases) { Text($0.title).tag($0.rawValue) }
            }
        } label: {
            pill(mode.title, systemImage: "magnifyingglass")
        }
        .accessibilityIdentifier("searchModeMenu")
        .glassButtonStyle(prominent: true)
        .buttonBorderShape(.capsule)
    }

    private func pill(_ title: String, systemImage: String) -> some View {
        HStack(spacing: 6) {
            Image(systemName: systemImage)
            Text(title).lineLimit(1)
            Image(systemName: "chevron.down").font(.caption.weight(.bold))
        }
        .font(.subheadline.weight(.semibold))
    }

    @ViewBuilder
    private var emptyState: some View {
        if !recents.isEmpty {
            Section {
                ForEach(recents, id: \.self) { item in
                    Button {
                        search = item.text
                        appliedSearch = item.text
                    } label: {
                        HStack(spacing: 10) {
                            Image(systemName: "clock.arrow.circlepath").foregroundStyle(.secondary)
                            Text(Num.digits(item.text)).foregroundStyle(.primary).lineLimit(1)
                            Spacer()
                            if item.inPayroll { SourceTag(kind: .payroll) }
                            if item.inStaff { SourceTag(kind: .staff) }
                        }
                    }
                }
            } header: {
                HStack {
                    Text(tr("عمليات البحث الأخيرة", "Recent searches"))
                    Spacer()
                    Button(tr("مسح", "Clear")) {
                        RecentSearches.clear()
                    }
                    .font(.footnote)
                    .textCase(nil)
                }
            }
        } else {
            Section {
                ContentUnavailableView(
                    tr("ابحث في الرواتب والملاك", "Search payroll and staff"),
                    systemImage: "magnifyingglass",
                    description: Text(tr("اكتب الاسم في شريط البحث. تظهر لكل شخص آخر شهر يوجد فيه راتبه، ومن صفحته تختار شهراً آخر. نوع البحث من الزر في الأعلى.",
                                         "Type a name. Each person shows their latest month; pick another month on their page. Change the search type with the button above."))
                )
                .listRowBackground(Color.clear)
            }
        }
    }
}

enum SourceKind { case payroll, staff }

struct SourceTag: View {
    let kind: SourceKind

    var body: some View {
        let isPayroll = kind == .payroll
        Text(isPayroll ? tr("رواتب", "Payroll") : tr("ملاك", "Staff"))
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .foregroundStyle(isPayroll ? Color.blue : Color.green)
            .background((isPayroll ? Color.blue : Color.green).opacity(0.15), in: RoundedRectangle(cornerRadius: 6))
    }
}

// MARK: - النتائج

/// نتيجة البحث كما تخرج من الخلفية: معرّفات فقط (آمنة للنقل بين الخيوط).
nonisolated struct UnifiedHit: Sendable, Hashable {
    let key: String
    let name: String
    let subtitle: String
    let employeeID: PersistentIdentifier?
    let staffID: PersistentIdentifier?
    /// ما تعرضه البطاقة.
    let month: String?
    let netSalary: Double?
    let staffStatus: String?
}

/// البحث الموحّد يعمل على سياق خاص في الخلفية حتى تبقى الكتابة والتمرير سلسة.
@ModelActor
actor UnifiedSearchActor {
    func find(_ search: String, mode: StaffSearchMode, scope: UnifiedScope, months: [String]) throws -> [UnifiedHit] {
        try UnifiedFinder.hits(search, mode: mode, scope: scope, months: months, context: modelContext)
    }
}

enum UnifiedFinder {
    /// يبحث في الخلفية، ثم يجلب الكائنات الناتجة (150 كحد أقصى) في سياق الواجهة.
    @MainActor
    static func find(_ search: String, mode: StaffSearchMode, scope: UnifiedScope, months: [String]) async throws -> [UnifiedPerson] {
        let container = SharedModel.container
        // استعلام محدود في الخلفية، وتُعاد النتائج الحديثة من الذاكرة.
        let hits = try await SearchIndex.shared.hits(search, mode: mode, scope: scope, months: months, container: container)
        try Task.checkCancellation()
        return hits.map(UnifiedPerson.init)
    }

    /// يبحث في الملاك وفي رواتب الأشهر المختارة، ويربط الطرفين بالاسم (بلا مسافات).
    static func hits(_ search: String, mode: StaffSearchMode, scope: UnifiedScope, months: [String], context: ModelContext) throws -> [UnifiedHit] {
        let tokens = tokens(search, mode: mode)
        guard !tokens.isEmpty else { return [] }
        let order = Dictionary(months.enumerated().map { ($1, $0) }, uniquingKeysWith: { a, _ in a })

        var staff: [StaffMember] = []
        var employees: [Employee] = []
        if StaffInfo.count > 0, scope != .payroll || mode != .name {
            var d = FetchDescriptor<StaffMember>(predicate: StaffResultsList.predicate(mode: mode, tokens: tokens), sortBy: [SortDescriptor(\.name)])
            d.fetchLimit = 200
            d.propertiesToFetch = [\.name, \.nameKey, \.motherKey, \.statNumber, \.unifiedNumber, \.rank, \.workplace, \.status]
            // نقرأ على دفعات حتى نجد النتائج، مع إبقاء جميع كلمات البحث معتبرة.
            while staff.count < 150 {
                try Task.checkCancellation()
                let page = try context.fetch(d)
                staff += page.filter { matches($0, tokens: tokens, mode: mode) }.prefix(150 - staff.count)
                if page.count < 200 { break }
                d.fetchOffset = (d.fetchOffset ?? 0) + page.count
            }
        }
        if scope != .staff, mode == .name, !months.isEmpty {
            // لكل شخص: أحدث شهر من الأشهر المختارة.
            var seen = Set<String>()
            for month in months {
                try Task.checkCancellation()
                for e in try EmployeeLookup.find(search, in: [month], context: context) where seen.insert(e.matchKey).inserted {
                    employees.append(e)
                    if employees.count >= 150 { break }
                }
                if employees.count >= 150 { break }
            }
        }

        struct Pair { var employee: Employee?; var staff: StaffMember? }
        var byKey: [String: Pair] = [:]
        var keys: [String] = []
        for s in staff {
            let key = s.nameKey.replacingOccurrences(of: " ", with: "")
            if byKey[key] == nil { keys.append(key); byKey[key] = Pair() }
            if byKey[key]!.staff == nil { byKey[key]!.staff = s }
        }
        for e in employees {
            let key = e.matchKey
            if byKey[key] == nil { keys.append(key); byKey[key] = Pair() }
            if byKey[key]!.employee == nil { byKey[key]!.employee = e }
        }
        // من وُجد في الملاك فقط: البحث عن راتبه بالاسم الكامل في الأشهر المختارة.
        let missingPay = keys.filter { byKey[$0]!.employee == nil }
        if !missingPay.isEmpty, !months.isEmpty {
            try Task.checkCancellation()
            var d = FetchDescriptor<Employee>(predicate: #Predicate { months.contains($0.month) && missingPay.contains($0.matchKey) })
            d.propertiesToFetch = [\.name, \.nameKey, \.matchKey, \.month, \.rank, \.workplace, \.netSalary]
            let found = try context.fetch(d).sorted { (order[$0.month] ?? .max) < (order[$1.month] ?? .max) }
            for e in found where byKey[e.matchKey] != nil && byKey[e.matchKey]!.employee == nil {
                byKey[e.matchKey]!.employee = e
            }
        }
        // ربط الأسماء الناقصة باستعلام واحد بدل استعلام منفصل لكل بطاقة.
        if StaffInfo.count > 0 {
            try Task.checkCancellation()
            let names = keys.compactMap { key -> String? in
                guard byKey[key]!.staff == nil else { return nil }
                return byKey[key]!.employee?.nameKey
            }
            if !names.isEmpty {
                var d = FetchDescriptor<StaffMember>(predicate: #Predicate { names.contains($0.nameKey) }, sortBy: [SortDescriptor(\.name)])
                d.propertiesToFetch = [\.name, \.nameKey, \.rank, \.workplace, \.status]
                for s in try context.fetch(d) {
                    let key = ArabicText.matchKey(s.name)
                    if byKey[key] != nil, byKey[key]!.staff == nil { byKey[key]!.staff = s }
                }
            }
        }
        try Task.checkCancellation()

        return keys.compactMap { key -> UnifiedHit? in
            guard let pair = byKey[key] else { return nil }
            switch scope {
            case .all: break
            case .payroll: if pair.employee == nil { return nil }
            case .staff: if pair.staff == nil { return nil }
            }
            let name = pair.staff?.name ?? pair.employee?.name ?? ""
            let subtitle = pair.staff.map { [$0.rank, $0.workplace] } ?? pair.employee.map { [$0.rank, $0.workplace] } ?? []
            return UnifiedHit(
                key: key, name: name,
                subtitle: subtitle.filter { !$0.isEmpty }.joined(separator: " · "),
                employeeID: pair.employee?.persistentModelID, staffID: pair.staff?.persistentModelID,
                month: pair.employee?.month, netSalary: pair.employee?.netSalary, staffStatus: pair.staff?.status
            )
        }
        .sorted { lhs, rhs in
            // من في الطرفين أولاً، ثم بالاسم.
            let l = (lhs.employeeID != nil ? 1 : 0) + (lhs.staffID != nil ? 1 : 0)
            let r = (rhs.employeeID != nil ? 1 : 0) + (rhs.staffID != nil ? 1 : 0)
            return l != r ? l > r : lhs.name < rhs.name
        }
    }

    static func tokens(_ search: String, mode: StaffSearchMode) -> [String] {
        if mode == .statNumber || mode == .unifiedNumber {
            // الرقم بالصوت قد يُكتب بمسافات أو بأرقام هندية: تبقى الأرقام فقط.
            let t = ArabicText.latinDigits(search).filter(\.isNumber)
            return t.isEmpty ? [] : [t]
        }
        return ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
    }

    static func matches(_ m: StaffMember, tokens: [String], mode: StaffSearchMode) -> Bool {
        tokens.allSatisfy { t in
            switch mode {
            case .name: m.nameKey.contains(t)
            case .mother: m.motherKey.contains(t)
            case .statNumber: m.statNumber.contains(t)
            case .unifiedNumber: m.unifiedNumber.contains(t)
            }
        }
    }
}

/// بطاقة نتيجة: الاسم، وراتب آخر شهر، والحالة في الملاك.
struct UnifiedCard: View {
    let person: UnifiedPerson
    private var hit: UnifiedHit { person.hit }
    private var month: String? { hit.month }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                NameAvatar(name: person.name, size: 40)
                VStack(alignment: .leading, spacing: 2) {
                    Text(person.name).font(.body.weight(.semibold)).lineLimit(2)
                    if !person.subtitle.isEmpty {
                        Text(person.subtitle).font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
            }
            HStack(spacing: 8) {
                tile(
                    title: person.hasPayroll ? (month ?? tr("الراتب", "Salary")) : tr("لا يوجد راتب", "No salary"),
                    value: person.hasPayroll ? Money.format(hit.netSalary) : "—",
                    color: .primary
                )
                tile(
                    title: tr("الملاك", "Staff"),
                    value: hit.staffStatus.map { $0.isEmpty ? tr("موجود", "Found") : $0 } ?? tr("غير موجود", "Not found"),
                    color: hit.staffStatus == nil ? .secondary : (hit.staffStatus!.contains("مستمر") ? .green : .orange)
                )
            }
        }
        .padding(.vertical, 4)
    }

    private func tile(title: String, value: String, color: Color) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            Text(value).font(.subheadline.weight(.bold).monospacedDigit()).foregroundStyle(color).lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Color(.tertiarySystemFill), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

// MARK: - صفحة الشخص الموحّدة

struct UnifiedPersonView: View {
    let person: UnifiedPerson

    /// "staff" للملاك، وإلا اسم الشهر.
    private static let staffTag = "staff"

    @State private var selection: String
    @State private var autopilot = AutoPilot.shared
    @State private var router = AppRouter.shared
    @Query private var records: [Employee]
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]

    init(person: UnifiedPerson) {
        self.person = person
        let key = person.hasPayroll ? person.hit.key : "\u{0}"
        _records = Query(filter: #Predicate<Employee> { $0.matchKey == key })
        let wantsStaff = LaunchOptions.personPart == "staff" && person.hasStaff
        _selection = State(initialValue: wantsStaff || !person.hasPayroll ? Self.staffTag : (person.hit.month ?? Self.staffTag))
    }

    /// رواتب الشخص من الأحدث إلى الأقدم.
    private var sortedRecords: [Employee] {
        let order = Dictionary(months.enumerated().map { ($1.name, $0) }, uniquingKeysWith: { a, _ in a })
        return records.sorted { (order[$0.month] ?? .max) < (order[$1.month] ?? .max) }
    }

    private var selectedRecord: Employee? {
        sortedRecords.first { $0.month == selection }
    }

    var body: some View {
        let staff = person.staff
        List {
            if selection == Self.staffTag {
                if let staff {
                    Section {
                        staffHeader(staff)
                            .listRowInsets(EdgeInsets())
                            .listRowBackground(Color.clear)
                    }
                    StaffSectionsContent(sections: staff.sections)
                }
            } else if let record = selectedRecord {
                Section {
                    PersonPayslip(employee: record, staff: staff)
                        .id(record.persistentModelID)
                        .listRowInsets(EdgeInsets())
                        .listRowBackground(Color.clear)
                }
            }
        }
        .listSectionSpacing(.compact)
        .contentMargins(.top, 4, for: .scrollContent)
        .navigationTitle(selection == Self.staffTag ? tr("الملاك", "Staff") : tr("قسيمة الراتب", "Payslip"))
        .navigationBarTitleDisplayMode(.inline)
        // الأشهر والملاك قائمة منسدلة بجانب العنوان، فتبدأ القسيمة من أعلى الصفحة.
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) { partMenu(staff: staff) }
        }
        // زر "مشاركة" في الشريط السفلي يشارك القسيمة المعروضة.
        .onAppear {
            router.payslipToShare = selectedRecord
            // اختبار المحاكي: -openPerson YES -share YES يفتح نافذة المشاركة.
            if LaunchOptions.share, LaunchOptions.openPerson, selectedRecord != nil { router.showShare = true }
        }
        .onChange(of: selection) { router.payslipToShare = selectedRecord }
        .onChange(of: records.count) { router.payslipToShare = selectedRecord }
        .onDisappear { router.payslipToShare = nil }
        .onChange(of: sortedRecords.count, initial: true) {
            // اختبار المحاكي: -personPart months يفتح الشهر السابق.
            if LaunchOptions.personPart == "months", sortedRecords.count > 1, selection == sortedRecords[0].month {
                selection = sortedRecords[1].month
            }
        }
        .onChange(of: autopilot.partRequest) {
            let list = sortedRecords
            switch autopilot.part {
            case "months" where list.count > 1: selection = list[1].month
            case "salary" where !list.isEmpty: selection = list[0].month
            default: break
            }
        }
    }

    /// قائمة منسدلة: كل شهر له راتب للشخص (الأحدث أولاً)، ثم الملاك، ومشاركة تقرير الشهر المعروض.
    private func partMenu(staff: StaffMember?) -> some View {
        Menu {
            Picker(tr("العرض", "Show"), selection: $selection) {
                ForEach(sortedRecords) { record in
                    Label(record.month, systemImage: "calendar").tag(record.month)
                }
                if staff != nil {
                    Label(tr("الملاك", "Staff"), systemImage: "person.text.rectangle").tag(Self.staffTag)
                }
            }
            if let record = selectedRecord {
                Section {
                    Button(tr("مشاركة تقرير راتب \(record.month)", "Share \(record.month) salary report"), systemImage: ReportSymbols.pdf) {
                        router.payslipToShare = record
                        router.showShare = true
                    }
                }
            }
        } label: {
            HStack(spacing: 6) {
                Image(systemName: selection == Self.staffTag ? "person.text.rectangle" : "calendar")
                Text(selection == Self.staffTag ? tr("الملاك", "Staff") : selection).lineLimit(1)
                Image(systemName: "chevron.down").font(.caption.weight(.bold))
            }
            .font(.subheadline.weight(.semibold))
        }
        .glassButtonStyle(prominent: true)
        .buttonBorderShape(.capsule)
        .accessibilityIdentifier("personPartPicker")
    }

    private func staffHeader(_ staff: StaffMember) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(staff.name).font(.title3.bold())
            let line = [staff.rank, staff.workplace].filter { !$0.isEmpty }.joined(separator: " · ")
            if !line.isEmpty { Text(line).font(.subheadline).foregroundStyle(.secondary) }
            if !staff.status.isEmpty { StatusChip(status: staff.status) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 20)
        .padding(.vertical, 6)
    }

}

/// القسيمة الرسمية لشهر واحد (الاستقطاعات من نفس الشهر).
private struct PersonPayslip: View {
    let employee: Employee
    let staff: StaffMember?
    @Query private var deductions: [Deduction]

    init(employee: Employee, staff: StaffMember?) {
        self.employee = employee
        self.staff = staff
        let key = employee.matchKey
        let month = employee.month
        _deductions = Query(filter: #Predicate { $0.month == month && $0.matchKey == key }, sort: \Deduction.title)
    }

    var body: some View {
        PayslipDocument(style: .screen, employee: employee, staff: staff, deductions: deductions)
            .shadow(color: .black.opacity(0.08), radius: 8, y: 2)
            .padding(.horizontal, 16)
            .padding(.vertical, 4)
    }
}

/// استقطاعات الموظف في شهره.
struct PersonDeductionsSection: View {
    @Query private var deductions: [Deduction]

    init(employee: Employee) {
        let key = employee.matchKey
        let month = employee.month
        _deductions = Query(filter: #Predicate { $0.month == month && $0.matchKey == key }, sort: \Deduction.title)
    }

    var body: some View {
        Section(tr("الاستقطاعات", "Deductions")) {
            if deductions.isEmpty {
                Label(tr("لا توجد استقطاعات", "No deductions"), systemImage: "checkmark.circle")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(deductions) { d in
                    LabeledContent {
                        Text(Money.format(d.amount)).monospacedDigit().foregroundStyle(.red)
                    } label: {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(d.title.orDash)
                            if !d.note.isEmpty { Text(d.note).font(.caption).foregroundStyle(.secondary) }
                        }
                    }
                }
                LabeledContent {
                    Text(Money.format(deductions.total)).bold().monospacedDigit().foregroundStyle(.red)
                } label: {
                    Text(tr("المجموع", "Total")).bold()
                }
            }
        }
    }
}

/// رواتب الشخص في كل الأشهر المحفوظة.
struct PersonMonthsSection: View {
    @Query private var records: [Employee]
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]

    init(matchKey: String) {
        _records = Query(filter: #Predicate { $0.matchKey == matchKey })
    }

    var body: some View {
        let order = Dictionary(months.enumerated().map { ($1.name, $0) }, uniquingKeysWith: { a, _ in a })
        let sorted = records.sorted { (order[$0.month] ?? .max) < (order[$1.month] ?? .max) }
        Section(tr("الرواتب حسب الشهر", "Salary by month")) {
            ForEach(sorted) { record in
                NavigationLink(value: record) {
                    LabeledContent(record.month) {
                        Text(Money.format(record.netSalary)).monospacedDigit().fontWeight(.semibold)
                    }
                }
            }
        }
    }
}
