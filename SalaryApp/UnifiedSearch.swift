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
    @AppStorage("unifiedScope") private var scopeRaw = UnifiedScope.all.rawValue
    @AppStorage("unifiedMode") private var modeRaw = StaffSearchMode.name.rawValue
    @AppStorage("selectedMonth") private var selectedMonth = ""
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

    private var scope: UnifiedScope { UnifiedScope(rawValue: scopeRaw) ?? .all }
    private var mode: StaffSearchMode { StaffSearchMode(rawValue: modeRaw) ?? .name }
    private var trimmed: String { appliedSearch.trimmingCharacters(in: .whitespaces) }

    /// أشهر الرواتب المختارة (نفس اختيار الشاشة الرئيسية)، أو آخر شهر.
    private var monthNames: [String] {
        let names = Set(selectedMonth.split(separator: "|").map(String.init))
        let chosen = months.filter { names.contains($0.name) }.map(\.name)
        return chosen.isEmpty ? months.prefix(1).map(\.name) : chosen
    }

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
                                if let employee = person.employee {
                                    Button(tr("مشاركة تقرير الراتب", "Share salary report"), systemImage: "doc.richtext") { shareTarget = employee }
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
            .safeAreaInset(edge: .top, spacing: 0) { filters }
            // في تبويب العدسة يظهر شريط البحث عند الضغط على العدسة (iOS 26).
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
            .navigationBarTitleDisplayMode(.large)
            .sheet(item: $shareTarget) { ShareOptionsSheet(employee: $0) }
            .navigationDestination(for: UnifiedPerson.self) { person in
                UnifiedPersonView(person: person)
                    .onAppear {
                        RecentSearches.add(.init(text: person.name, inPayroll: person.hasPayroll, inStaff: person.hasStaff))
                    }
            }
        }
    }

    /// أزرار صغيرة: النطاق، نوع البحث، أشهر الرواتب.
    private var filters: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                Menu {
                    Picker(tr("النطاق", "Scope"), selection: $scopeRaw) {
                        ForEach(UnifiedScope.allCases) { Text($0.title).tag($0.rawValue) }
                    }
                } label: {
                    pill(scope.title, systemImage: "square.stack.3d.up")
                }
                .accessibilityIdentifier("scopeMenu")
                .glassButtonStyle(prominent: true)
                .buttonBorderShape(.capsule)

                Menu {
                    Picker(tr("البحث حسب", "Search by"), selection: $modeRaw) {
                        ForEach(StaffSearchMode.allCases) { Text($0.title).tag($0.rawValue) }
                    }
                } label: {
                    pill(tr("بحث: \(mode.title)", "By: \(mode.title)"), systemImage: "magnifyingglass")
                }
                .glassButtonStyle()
                .buttonBorderShape(.capsule)

                if !months.isEmpty {
                    Menu {
                        ForEach(months) { month in
                            Button {
                                toggle(month.name)
                            } label: {
                                if monthNames.contains(month.name) {
                                    Label(month.name, systemImage: "checkmark")
                                } else {
                                    Text(month.name)
                                }
                            }
                            .menuActionDismissBehavior(.disabled)
                        }
                    } label: {
                        pill(monthNames.count == 1 ? monthNames[0] : tr("\(Num.format(monthNames.count)) أشهر", "\(Num.format(monthNames.count)) months"),
                             systemImage: "calendar")
                    }
                    .accessibilityIdentifier("searchMonthMenu")
                    .glassButtonStyle()
                    .buttonBorderShape(.capsule)
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 6)
        }
        .scrollClipDisabled()
    }

    private func toggle(_ name: String) {
        var names = monthNames
        if let i = names.firstIndex(of: name) {
            guard names.count > 1 else { return }
            names.remove(at: i)
        } else {
            names.append(name)
        }
        selectedMonth = names.joined(separator: "|")
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
                    description: Text(tr("اكتب الاسم في شريط البحث. يمكنك تغيير نوع البحث والأشهر من الأزرار في الأعلى.",
                                         "Type a name in the search bar. Change the search type and months with the buttons above."))
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
        let hits = try await Task.detached(priority: .userInitiated) {
            try await UnifiedSearchActor(modelContainer: container).find(search, mode: mode, scope: scope, months: months)
        }.value
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
            d.fetchLimit = 3000
            staff = try context.fetch(d).filter { matches($0, tokens: tokens, mode: mode) }.prefix(150).map { $0 }
        }
        if scope != .staff, mode == .name, !months.isEmpty {
            // لكل شخص: أحدث شهر من الأشهر المختارة.
            var seen = Set<String>()
            for e in try EmployeeLookup.find(search, in: months, context: context) where seen.insert(e.matchKey).inserted {
                employees.append(e)
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
            let d = FetchDescriptor<Employee>(predicate: #Predicate { months.contains($0.month) && missingPay.contains($0.matchKey) })
            let found = try context.fetch(d).sorted { (order[$0.month] ?? .max) < (order[$1.month] ?? .max) }
            for e in found where byKey[e.matchKey] != nil && byKey[e.matchKey]!.employee == nil {
                byKey[e.matchKey]!.employee = e
            }
        }
        // من وُجد في الرواتب فقط: البحث عنه في الملاك بالاسم الكامل.
        if StaffInfo.count > 0 {
            for key in keys where byKey[key]!.staff == nil {
                guard let e = byKey[key]!.employee else { continue }
                let nameKey = e.nameKey
                var d = FetchDescriptor<StaffMember>(predicate: #Predicate { $0.nameKey == nameKey })
                d.fetchLimit = 1
                if let s = try context.fetch(d).first { byKey[key]!.staff = s }
            }
        }

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
            let t = ArabicText.latinDigits(search).trimmingCharacters(in: .whitespaces)
            return t.isEmpty ? [] : [t]
        }
        return ArabicText.normalize(search).split(separator: " ").map(String.init)
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
                    title: person.hasPayroll ? tr("راتب \(month ?? "")", "Salary \(month ?? "")") : tr("لا يوجد راتب", "No salary"),
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
        let employee = person.employee
        let key = employee?.matchKey ?? "\u{0}"
        _records = Query(filter: #Predicate<Employee> { $0.matchKey == key })
        let wantsStaff = LaunchOptions.personPart == "staff" && person.hasStaff
        _selection = State(initialValue: wantsStaff || employee == nil ? Self.staffTag : employee!.month)
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
        // أزرار الأشهر ثابتة أعلى الصفحة (بدون فراغ فوقها).
        .safeAreaInset(edge: .top, spacing: 0) {
            chips(staff: staff)
                .padding(.bottom, 6)
        }
        .navigationTitle(tr("قسيمة الراتب", "Payslip"))
        .navigationBarTitleDisplayMode(.inline)
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

    /// أزرار صغيرة: كل شهر له راتب للشخص، ثم الملاك.
    private func chips(staff: StaffMember?) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(sortedRecords) { record in
                    chip(record.month, tag: record.month)
                        .contextMenu {
                            Button(tr("عرض هذا الشهر", "Show this month"), systemImage: "eye") { selection = record.month }
                            Button(tr("مشاركة تقرير راتب هذا الشهر", "Share this month's salary report"), systemImage: "doc.richtext") {
                                selection = record.month
                                router.payslipToShare = record
                                router.showShare = true
                            }
                        }
                }
                if staff != nil {
                    chip(tr("الملاك", "Staff"), tag: Self.staffTag)
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 4)
        }
        .scrollClipDisabled()
        .accessibilityIdentifier("personPartPicker")
    }

    private func chip(_ title: String, tag: String) -> some View {
        let on = selection == tag
        return Button {
            withAnimation(.snappy(duration: 0.2)) { selection = tag }
        } label: {
            Text(title)
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .foregroundStyle(on ? Color.white : Color.primary)
                .background(on ? AnyShapeStyle(PayslipDocument.green) : AnyShapeStyle(.regularMaterial), in: Capsule())
        }
        .buttonStyle(.plain)
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
