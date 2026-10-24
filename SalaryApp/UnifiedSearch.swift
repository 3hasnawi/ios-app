import SwiftUI
import SwiftData
import UIKit

/// شخص في البحث الموحّد: راتبه في آخر شهر (إن وُجد) وسجله في الملاك (إن وُجد)،
/// مربوطان بالاسم بعد توحيد الهمزات وحذف المسافات.
struct UnifiedPerson: Hashable, Identifiable {
    let id: String
    let name: String
    let employee: Employee?
    let staff: StaffMember?

    var subtitle: String {
        if let staff {
            return [staff.rank, staff.workplace].filter { !$0.isEmpty }.joined(separator: " · ")
        }
        guard let employee else { return "" }
        return [employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · ")
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

    private static let key = "unifiedRecentSearches"

    static var items: [Item] {
        guard let data = UserDefaults.standard.data(forKey: key) else { return [] }
        return (try? JSONDecoder().decode([Item].self, from: data)) ?? []
    }

    static func add(_ item: Item) {
        var list = items.filter { $0.text != item.text }
        list.insert(item, at: 0)
        if let data = try? JSONEncoder().encode(Array(list.prefix(8))) {
            UserDefaults.standard.set(data, forKey: key)
        }
    }

    static func clear() { UserDefaults.standard.removeObject(forKey: key) }
}

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
    @State private var recents = RecentSearches.items
    @State private var importState = StaffImportState.shared
    @State private var router = ShortcutRouter.shared
    @State private var path: [UnifiedPerson] = []
    @State private var people: [UnifiedPerson] = []
    @State private var loadedKey = ""
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
                people = (try? UnifiedFinder.find(trimmed, mode: mode, scope: scope, months: monthNames)) ?? []
                loadedKey = key
                if LaunchOptions.openPerson, path.isEmpty, let first = people.first { path = [first] }
            }
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
            .navigationDestination(for: UnifiedPerson.self) { person in
                UnifiedPersonView(person: person)
                    .onAppear {
                        RecentSearches.add(.init(text: person.name, inPayroll: person.employee != nil, inStaff: person.staff != nil))
                        recents = RecentSearches.items
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
                        recents = []
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

enum UnifiedFinder {
    /// يبحث في الملاك وفي رواتب الأشهر المختارة، ويربط الطرفين بالاسم (بلا مسافات).
    @MainActor
    static func find(_ search: String, mode: StaffSearchMode, scope: UnifiedScope, months: [String]) throws -> [UnifiedPerson] {
        let context = SharedModel.container.mainContext
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
            for e in try EmployeeLookup.find(search, in: months) where seen.insert(e.matchKey).inserted {
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

        return keys.compactMap { key -> UnifiedPerson? in
            guard let pair = byKey[key] else { return nil }
            let name = pair.staff?.name ?? pair.employee?.name ?? ""
            return UnifiedPerson(id: key, name: name, employee: pair.employee, staff: pair.staff)
        }
        .filter { p in
            switch scope {
            case .all: true
            case .payroll: p.employee != nil
            case .staff: p.staff != nil
            }
        }
        .sorted { lhs, rhs in
            // من في الطرفين أولاً، ثم بالاسم.
            let l = (lhs.employee != nil ? 1 : 0) + (lhs.staff != nil ? 1 : 0)
            let r = (rhs.employee != nil ? 1 : 0) + (rhs.staff != nil ? 1 : 0)
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
    private var month: String? { person.employee?.month }

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
                    title: person.employee != nil ? tr("راتب \(month ?? "")", "Salary \(month ?? "")") : tr("لا يوجد راتب", "No salary"),
                    value: person.employee.map { Money.format($0.netSalary) } ?? "—",
                    color: .primary
                )
                tile(
                    title: tr("الملاك", "Staff"),
                    value: person.staff.map { $0.status.isEmpty ? tr("موجود", "Found") : $0.status } ?? tr("غير موجود", "Not found"),
                    color: person.staff == nil ? .secondary : (person.staff!.status.contains("مستمر") ? .green : .orange)
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

    enum Part: String, CaseIterable, Identifiable {
        case salary, staff, months
        var id: String { rawValue }
        var title: String {
            switch self {
            case .salary: tr("الراتب", "Salary")
            case .staff: tr("الملاك", "Staff")
            case .months: tr("الأشهر", "Months")
            }
        }
    }

    @State private var part: Part
    @State private var showShare = false
    @State private var copied = false

    init(person: UnifiedPerson) {
        self.person = person
        let start = Part(rawValue: LaunchOptions.personPart)
        _part = State(initialValue: start ?? (person.employee != nil ? .salary : .staff))
    }

    private var parts: [Part] {
        Part.allCases.filter { p in
            switch p {
            case .salary, .months: person.employee != nil
            case .staff: person.staff != nil
            }
        }
    }

    var body: some View {
        List {
            Section {
                header
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(Color.clear)
                if parts.count > 1 {
                    Picker(tr("القسم", "Section"), selection: $part) {
                        ForEach(parts) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .listRowBackground(Color.clear)
                    .listRowInsets(EdgeInsets(top: 12, leading: 0, bottom: 0, trailing: 0))
                }
            }

            switch part {
            case .salary:
                if let employee = person.employee {
                    PersonDeductionsSection(employee: employee)
                    Section {
                        HStack(spacing: 10) {
                            Button {
                                copyImage(employee)
                            } label: {
                                Label(copied ? tr("تم النسخ", "Copied") : tr("نسخ كصورة", "Copy as image"),
                                      systemImage: copied ? "checkmark" : "doc.on.doc")
                                    .frame(maxWidth: .infinity)
                            }
                            .buttonStyle(.borderedProminent)
                            .tint(copied ? .green : .blue)
                            Button {
                                showShare = true
                            } label: {
                                Label(tr("مشاركة", "Share"), systemImage: "square.and.arrow.up")
                                    .frame(maxWidth: .infinity)
                            }
                            .buttonStyle(.bordered)
                        }
                        .listRowBackground(Color.clear)
                        .listRowInsets(EdgeInsets())
                    }
                }
            case .staff:
                if let staff = person.staff {
                    StaffSectionsContent(sections: staff.sections)
                }
            case .months:
                if let employee = person.employee {
                    PersonMonthsSection(matchKey: employee.matchKey)
                }
            }
        }
        .listSectionSpacing(.compact)
        .navigationTitle(person.name)
        .navigationBarTitleDisplayMode(.inline)
        .navigationDestination(for: Employee.self) { EmployeeDetailView(employee: $0) }
        .sheet(isPresented: $showShare) {
            if let employee = person.employee { ShareOptionsSheet(employee: employee) }
        }
    }

    private var header: some View {
        let chips = [person.staff?.rank ?? person.employee?.rank ?? "",
                     person.staff?.status ?? "",
                     person.staff.map { $0.statNumber.isEmpty ? "" : tr("الرقم الإحصائي \($0.statNumber)", "Stat. no. \($0.statNumber)") } ?? ""]
            .filter { !$0.isEmpty }
        return VStack(alignment: .leading, spacing: 8) {
            Text(person.name).font(.title3.bold())
            if !chips.isEmpty {
                HStack(spacing: 6) {
                    ForEach(chips, id: \.self) { chip in
                        Text(Num.digits(chip))
                            .font(.caption.weight(.medium))
                            .lineLimit(1)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 3)
                            .background(.white.opacity(0.2), in: Capsule())
                    }
                }
            }
            if let employee = person.employee {
                Text(tr("صافي راتب \(employee.month)", "Net salary, \(employee.month)"))
                    .font(.caption.weight(.medium))
                    .opacity(0.85)
                    .padding(.top, 4)
                Text(Money.format(employee.netSalary))
                    .font(.system(size: 34, weight: .bold, design: .rounded))
                    .monospacedDigit()
            }
        }
        .foregroundStyle(.white)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(20)
        .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 26, style: .continuous))
    }

    private func copyImage(_ employee: Employee) {
        guard let deductions = try? EmployeeLookup.deductions(of: employee),
              let files = try? PayslipExporter.export(employee: employee, deductions: deductions) else { return }
        UIPasteboard.general.image = files.image
        withAnimation { copied = true }
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
