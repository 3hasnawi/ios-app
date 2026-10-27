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
    @AppStorage("unifiedSmartMode") private var smart = true
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()
    /// يتحدّث تلقائياً عند أي إضافة أو مسح (من أي شاشة).
    @AppStorage(RecentSearches.key) private var recentsData = Data()
    private var recents: [RecentSearches.Item] { RecentSearches.decode(recentsData) }
    @State private var importState = StaffImportState.shared
    @State private var bookStore = OfficialBookStore.shared
    @State private var router = ShortcutRouter.shared
    @State private var path: [UnifiedPerson] = []
    /// مشاركة تقرير الراتب من الضغط المطوّل على بطاقة النتيجة.
    @State private var shareTarget: Employee?
    @State private var people: [UnifiedPerson] = []
    @State private var smartResults = SmartSearchResults()
    @State private var loadedKey = ""
    @State private var searchError: String?
    @State private var autopilot = AutoPilot.shared
    /// القائمة العائمة لنوع البحث.
    @State private var showModes = false
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
        let revision = months.map { "\($0.name)@\($0.importedAt.timeIntervalSince1970)" }.joined(separator: ",")
        return "\(trimmed)|\(smart)|\(mode.rawValue)|\(revision)|\(accountData.hashValue)|\(importState.version)|\(bookStore.revision)"
    }

    var body: some View {
        NavigationStack(path: $path) {
            List {
                if trimmed.isEmpty {
                    emptyState
                } else if loadedKey == queryKey, let searchError {
                    Section {
                        ContentUnavailableView(tr("تعذّر البحث", "Search failed"), systemImage: "magnifyingglass",
                                               description: Text(searchError))
                            .listRowBackground(Color.clear)
                    }
                } else if smart && loadedKey != queryKey {
                    Section { ProgressView().frame(maxWidth: .infinity) }
                } else if loadedKey == queryKey && (smart ? smartResults.isEmpty : people.isEmpty) {
                    Section {
                        if !smart && mode != .name && StaffInfo.count == 0 {
                            ContentUnavailableView(tr("بيانات الملاك غير متوفرة", "Staff data unavailable"), systemImage: "person.3.fill",
                                                   description: Text(tr("اسم الأم والرقم الإحصائي من ملف الملاك. أعد استيراد ملف Access من الإعدادات ← البيانات المحفوظة.",
                                                                        "Mother's name and statistical number come from the staff database. Reimport the Access file from Settings › Saved data.")))
                                .listRowBackground(Color.clear)
                        } else {
                            ContentUnavailableView.search(text: trimmed)
                                .listRowBackground(Color.clear)
                        }
                    }
                } else {
                    if !people.isEmpty || !smart {
                        Section {
                            ForEach(people) { person in
                                NavigationLink(value: person) {
                                    UnifiedCard(person: person)
                                }
                                .accessibilityIdentifier("resultCard")
                                .contextMenu {
                                    Button(tr("فتح", "Open"), systemImage: "person.text.rectangle") { path = [person] }
                                    if person.hasPayroll {
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
                    if smart {
                        if !smartResults.books.isEmpty {
                            Section(tr("الكتب الرسمية", "Official documents")) {
                                ForEach(smartResults.books) { book in
                                    NavigationLink {
                                        OfficialBookDetailView(id: book.id)
                                    } label: {
                                        VStack(alignment: .leading, spacing: 3) {
                                            Text(book.title).font(.body.weight(.semibold))
                                            Text("\(book.month) · \(tr("\(Num.format(book.pages)) صفحات", "\(book.pages) pages"))")
                                                .font(.caption).foregroundStyle(.secondary)
                                        }
                                    }
                                }
                            }
                        }
                        if !smartResults.groups.isEmpty {
                            Section(tr("جهات التنفيذ وبنود الرواتب", "Entities and payroll items")) {
                                ForEach(smartResults.groups) { group in
                                    NavigationLink {
                                        ReportDetailView(group: group)
                                    } label: {
                                        VStack(alignment: .leading, spacing: 3) {
                                            Text(group.title).font(.body.weight(.semibold))
                                            Text("\(group.kind == .deductions ? tr("جهة تنفيذ", "Execution entity") : tr("بند راتب", "Payroll item")) · \(group.month) · \(Money.format(group.total))")
                                                .font(.caption).foregroundStyle(.secondary)
                                        }
                                    }
                                    .accessibilityIdentifier("smartReportResult")
                                }
                            }
                        }
                        if !smartResults.accounts.isEmpty {
                            Section(tr("حسابات جهات التنفيذ", "Execution accounts")) {
                                ForEach(smartResults.accounts) { account in
                                    NavigationLink {
                                        SmartAccountDetailView(account: account)
                                    } label: {
                                        VStack(alignment: .leading, spacing: 3) {
                                            Text(account.entity).font(.body.weight(.semibold))
                                            Text(account.iban).font(.caption.monospaced())
                                                .foregroundStyle(.secondary).privacySensitive()
                                        }
                                    }
                                    .accessibilityIdentifier("smartAccountResult")
                                }
                            }
                        }
                        if !smartResults.months.isEmpty {
                            Section(tr("الأشهر", "Months")) {
                                ForEach(smartResults.months, id: \.self) { month in
                                    Button {
                                        UserDefaults.standard.set(month, forKey: "reportMonth")
                                        AppRouter.shared.tab = .payroll
                                        AppRouter.shared.showReports = true
                                    } label: {
                                        Label(month, systemImage: "calendar")
                                    }
                                }
                            }
                        }
                    }
                }
            }
            .listStyle(.insetGrouped)
            .accessibilityIdentifier("searchList")
            .scrollDismissesKeyboard(.immediately)
            .searchable(text: $search, prompt: Text(smart ? tr("ابحث في كل بيانات التطبيق", "Search all app data") : mode.prompt))
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
                guard !trimmed.isEmpty else { people = []; smartResults = SmartSearchResults(); searchError = nil; loadedKey = key; return }
                let started = Date()
                do {
                    if smart {
                        let found = try await SmartSearch.find(trimmed, months: months, accountData: accountData)
                        guard !Task.isCancelled else { return }
                        smartResults = found
                        people = found.people
                    } else {
                        let found = try await UnifiedFinder.find(trimmed, mode: mode, scope: scope, months: monthNames)
                        guard !Task.isCancelled else { return }
                        people = found
                    }
                    searchError = nil
                } catch is CancellationError {
                    return
                } catch {
                    guard !Task.isCancelled else { return }
                    people = []
                    smartResults = SmartSearchResults()
                    searchError = error.localizedDescription
                }
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
                smart = false
                modeRaw = StaffSearchMode.name.rawValue
                search = name
                appliedSearch = name
            }
            .navigationTitle(tr("بحث", "Search"))
            // عنوان صغير بلا فراغ فوقه، ونوع البحث زر في شريط العنوان يفتح قائمة عائمة تحته.
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) { modeButton }
            }
            .overlay { modePicker }
            .sheet(item: $shareTarget) { ShareOptionsSheet(employee: $0) }
            .navigationDestination(for: UnifiedPerson.self) { person in
                UnifiedPersonView(person: person)
                    .onAppear {
                        RecentSearches.add(.init(text: person.name, inPayroll: person.hasPayroll, inStaff: person.hasStaff))
                    }
            }
        }
    }

    /// نوع البحث: زر في شريط العنوان.
    private var modeButton: some View {
        Button {
            withAnimation(.snappy(duration: 0.25)) { showModes.toggle() }
        } label: {
            HStack(spacing: 6) {
                Image(systemName: smart ? "sparkle.magnifyingglass" : mode.systemImage)
                Text(smart ? tr("بحث شامل", "All data") : mode.title).lineLimit(1)
                Image(systemName: showModes ? "chevron.up" : "chevron.down").font(.caption.weight(.bold))
            }
            .font(.subheadline.weight(.semibold))
        }
        .accessibilityIdentifier("searchModeMenu")
        .accessibilityLabel(tr("البحث حسب: \(smart ? "بحث شامل" : mode.title)", "Search by: \(smart ? "All data" : mode.title)"))
        .glassButtonStyle(prominent: true)
        .buttonBorderShape(.capsule)
    }

    /// القائمة العائمة تحت زر نوع البحث، والخلفية معتمة حتى تُغلق بالضغط خارجها.
    private var modePicker: some View {
        ZStack(alignment: .topTrailing) {
            if showModes {
                Color.black.opacity(0.35)
                    .ignoresSafeArea()
                    .onTapGesture { withAnimation(.snappy(duration: 0.25)) { showModes = false } }
                    .transition(.opacity)
                modePanel
                    .padding(.horizontal, 16)
                    .padding(.top, 6)
                    .transition(.scale(scale: 0.85, anchor: .topTrailing).combined(with: .opacity))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
    }

    private var modePanel: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(tr("البحث حسب", "Search by"))
                .font(.footnote)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 10)
                .padding(.top, 6)
            Button {
                smart = true
                withAnimation(.snappy(duration: 0.25)) { showModes = false }
            } label: {
                HStack(spacing: 12) {
                    Image(systemName: "sparkle.magnifyingglass")
                        .font(.callout.weight(.semibold)).foregroundStyle(.white)
                        .frame(width: 36, height: 36)
                        .background(Color.blue.gradient, in: RoundedRectangle(cornerRadius: 11))
                    VStack(alignment: .leading, spacing: 1) {
                        Text(tr("بحث شامل", "All data")).font(.body.weight(.semibold)).foregroundStyle(.primary)
                        Text(tr("أشخاص، جهات، حسابات، أشهر", "People, entities, accounts, months"))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 8)
                    if smart { Image(systemName: "checkmark").foregroundStyle(Color.accentColor) }
                }
                .padding(10)
                .background(smart ? Color.accentColor.opacity(0.16) : .clear, in: RoundedRectangle(cornerRadius: 14))
            }
            .buttonStyle(.plain)
            ForEach(StaffSearchMode.allCases) { option in
                let on = !smart && option == mode
                Button {
                    smart = false
                    modeRaw = option.rawValue
                    withAnimation(.snappy(duration: 0.25)) { showModes = false }
                } label: {
                    HStack(spacing: 12) {
                        Image(systemName: option.systemImage)
                            .font(.callout.weight(.semibold))
                            .foregroundStyle(.white)
                            .frame(width: 36, height: 36)
                            .background(option.color.gradient, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
                        VStack(alignment: .leading, spacing: 1) {
                            Text(option.title).font(.body.weight(.semibold)).foregroundStyle(.primary)
                            Text(option.hint).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer(minLength: 8)
                        if on {
                            Image(systemName: "checkmark").font(.body.weight(.bold)).foregroundStyle(Color.accentColor)
                        }
                    }
                    .padding(10)
                    .background(on ? Color.accentColor.opacity(0.16) : .clear, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                    .contentShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
                }
                .buttonStyle(.plain)
                .accessibilityLabel(option.title)
            }
        }
        .padding(8)
        .frame(width: 290)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 24, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 24, style: .continuous).strokeBorder(.white.opacity(0.08)))
        .shadow(color: .black.opacity(0.35), radius: 20, y: 8)
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
                    tr("ابحث في بيانات التطبيق", "Search app data"),
                    systemImage: "magnifyingglass",
                    description: Text(tr("ابحث عن شخص، جهة تنفيذ، كتاب رسمي يشمل إشعاره، رقم حساب، بند راتب أو شهر. استخدم الزر في الأعلى للبحث المحدد باسم الأم أو الرقم.",
                                         "Search people, entities, official documents and their notices, account numbers, payroll items or months. Use the top button to search specifically by mother's name or number."))
                )
                .listRowBackground(Color.clear)
            }
        }
    }
}

enum SourceKind { case payroll, staff }

private struct SmartAccountDetailView: View {
    let account: SavedExecutionAccount

    var body: some View {
        List {
            Section(tr("حساب جهة التنفيذ", "Execution account")) {
                LabeledContent(tr("الجهة", "Entity"), value: account.entity)
                LabeledContent("IBAN") {
                    Text(account.iban).monospaced().textSelection(.enabled).privacySensitive()
                }
                if !account.branch.isEmpty { LabeledContent(tr("الفرع", "Branch"), value: account.branch) }
                if !account.swift.isEmpty { LabeledContent("SWIFT", value: account.swift) }
                Button(tr("نسخ IBAN", "Copy IBAN"), systemImage: "doc.on.doc") {
                    UIPasteboard.general.string = account.iban
                }
            }
        }
        .navigationTitle(account.entity)
        .navigationBarTitleDisplayMode(.inline)
    }
}

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
        // عدّاد الملاك في الإعدادات قد يغيب بعد الاسترجاع؛ قاعدة البيانات هي المرجع.
        if scope != .payroll || mode != .name {
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
        if !keys.isEmpty {
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
                        .accessibilityIdentifier("personPayslip")
                        .contextMenu {
                            Button(tr("تقرير راتب PDF", "Salary report PDF"), systemImage: ReportSymbols.pdf) {
                                share(record, mode: .report)
                            }
                            Button(tr("قسيمة صورة", "Payslip image"), systemImage: "photo") {
                                share(record, mode: .image)
                            }
                        }
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
        // الضغط المطوّل على القسيمة يفتح خيارات المشاركة؛ بلا تبويب مشاركة إضافي.
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

    /// قائمة منسدلة: كل شهر له راتب للشخص (الأحدث أولاً)، ثم الملاك.
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
        } label: {
            Image(systemName: selection == Self.staffTag ? "person.text.rectangle" : "calendar")
                .accessibilityLabel(tr("اختيار الشهر أو الملاك: \(selection == Self.staffTag ? tr("الملاك", "Staff") : selection)",
                                       "Choose month or staff: \(selection == Self.staffTag ? tr("الملاك", "Staff") : selection)"))
        }
        .glassButtonStyle()
        .buttonBorderShape(.circle)
        .accessibilityIdentifier("personPartPicker")
    }

    private func share(_ record: Employee, mode: ShareOptionsSheet.Mode) {
        router.payslipToShare = record
        router.shareMode = mode
        router.showShare = true
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
