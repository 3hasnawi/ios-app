import SwiftUI
import SwiftData
import UniformTypeIdentifiers

/// خيارات تشغيل للاختبار الآلي على المحاكي، مثلاً:
/// `xcrun simctl launch <device> com.example.SalaryApp -importFile "/a.xlsx|/b.xlsx" -search "احمد" -openFirst YES -selectedMonth "أيلول 2026"`
/// لا تؤثر على الاستخدام العادي لأنها لا تُضبط إلا من سطر الأوامر.
enum LaunchOptions {
    /// مسار ملف أو عدة مسارات مفصولة بـ "|".
    static var importFiles: [String] {
        (UserDefaults.standard.string(forKey: "importFile") ?? "").split(separator: "|").map(String.init)
    }
    static var search: String { UserDefaults.standard.string(forKey: "search") ?? "" }
    static var openFirst: Bool { UserDefaults.standard.bool(forKey: "openFirst") }
    /// يفتح نافذة المشاركة في صفحة التفاصيل.
    static var share: Bool { UserDefaults.standard.bool(forKey: "share") }
    /// يفتح لوحة الإعدادات.
    static var openSettings: Bool { UserDefaults.standard.bool(forKey: "openSettings") }
    /// التبويب عند التشغيل: "staff" لتبويب الملاك.
    static var startTab: String { UserDefaults.standard.string(forKey: "tab") ?? "" }

    /// نتيجة الاستيراد تُكتب في Documents/import-report.txt ليقرأها سكربت الاختبار.
    static func writeReport(_ text: String) {
        guard let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first else { return }
        try? text.write(to: docs.appendingPathComponent("import-report.txt"), atomically: true, encoding: .utf8)
    }
}

struct ContentView: View {
    @Environment(\.modelContext) private var context
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @AppStorage("selectedMonth") private var selectedMonth = ""
    /// ماذا يظهر في النتائج: تفاصيل الراتب، الاستقطاعات، أو الاثنان معاً (واحد على الأقل).
    @AppStorage("showSalary") private var showSalary = true
    @AppStorage("showDeductions") private var showDeductions = false
    /// تغيير اللغة أو الأرقام يعيد رسم الشاشة بالنصوص والأرقام الجديدة.
    @AppStorage(AppSettings.languageKey) private var language = AppLanguage.arabic.rawValue
    @AppStorage(AppSettings.numeralsKey) private var numerals = AppNumerals.latin.rawValue
    @State private var showSettings = false
    /// اسم قادم من اختصار "فتح البحث في الرواتب".
    @State private var router = ShortcutRouter.shared
    @State private var importAfterSettings = false
    @State private var staffImportAfterSettings = false
    @State private var showStaffPicker = false
    @State private var pickedStaffFile: URL?
    @State private var appRouter = AppRouter.shared
    @State private var search = LaunchOptions.search
    /// النص المطبَّق فعلاً على البحث: يتأخر قليلاً عن الكتابة حتى لا يُعاد البحث مع كل حرف.
    @State private var appliedSearch = LaunchOptions.search
    @State private var path: [Employee] = []
    @State private var showImporter = false
    @State private var pickedFiles: [URL] = []
    @State private var isImporting = false
    @State private var message: String?
    @State private var monthToDelete: PayrollMonth?

    private let fileTypes: [UTType] = [
        UTType(filenameExtension: "xlsx") ?? .spreadsheet,
        .commaSeparatedText,
    ]

    /// الأشهر المختارة (تُحفظ مفصولة بـ "|")، أو أحدث شهر إذا لم يُختر شيء.
    private var selectedMonths: [PayrollMonth] {
        let names = Set(selectedMonth.split(separator: "|").map(String.init))
        let chosen = months.filter { names.contains($0.name) }
        return chosen.isEmpty ? Array(months.prefix(1)) : chosen
    }

    private var currentMonth: PayrollMonth? { selectedMonths.first }

    private func setSelection(_ names: [String]) {
        selectedMonth = names.joined(separator: "|")
        path = []
    }

    private func toggle(_ month: PayrollMonth) {
        var names = selectedMonths.map(\.name)
        if let i = names.firstIndex(of: month.name) {
            guard names.count > 1 else { return }   // يبقى شهر واحد على الأقل
            names.remove(at: i)
        } else {
            names.append(month.name)
        }
        setSelection(names)
    }

    var body: some View {
        NavigationStack(path: $path) {
            // التصميم "آيفون أصلي زجاجي": عنوان كبير، زر الأشهر تحته، النتائج مجمّعة بالأشهر،
            // والبحث شريط زجاجي في أسفل الشاشة (iOS 26) أو تحت العنوان (iOS 18).
            // الشاشة الرئيسية: ملخص الشهر وآخر عمليات البحث، والشعار خلفية شفافة.
            // البحث نفسه في تبويب العدسة.
            HomeDashboard(months: months, current: currentMonth,
                          onImportPayroll: { showImporter = true },
                          onImportStaff: { showStaffPicker = true })
                .id("\(language)-\(numerals)")
                .safeAreaInset(edge: .top, spacing: 0) {
                    if !months.isEmpty {
                        // فلترة النتائج: زرّان صغيران، كل واحد يفتح قائمة تقبل خياراً واحداً أو عدة خيارات.
                        ScrollView(.horizontal, showsIndicators: false) {
                            HStack(spacing: 8) {
                                monthMenu
                            }
                            .padding(.horizontal, 20)
                            .padding(.top, 4)
                            .padding(.bottom, 8)
                        }
                        .scrollClipDisabled()
                    }
                }
                .background(Color(.systemGroupedBackground))
                .navigationTitle(tr("الرئيسية", "Home"))
                .navigationBarTitleDisplayMode(.large)
                .navigationDestination(for: Employee.self) { EmployeeDetailView(employee: $0) }
                .toolbar {
                    ToolbarItem(placement: .primaryAction) {
                        Button {
                            showSettings = true
                        } label: {
                            Label(tr("الإعدادات", "Settings"), systemImage: "gearshape")
                        }
                    }
                }
                .sheet(isPresented: $showSettings, onDismiss: {
                    // زر الاستيراد داخل الإعدادات: تُغلق الإعدادات أولاً ثم تُفتح نافذة الملفات.
                    if importAfterSettings {
                        importAfterSettings = false
                        showImporter = true
                    }
                    if staffImportAfterSettings {
                        staffImportAfterSettings = false
                        showStaffPicker = true
                    }
                }) {
                    SettingsView(months: months, onImport: {
                        importAfterSettings = true
                        showSettings = false
                    }, onImportStaff: {
                        staffImportAfterSettings = true
                        showSettings = false
                    })
                }
                .sheet(isPresented: $showStaffPicker, onDismiss: {
                    if let url = pickedStaffFile {
                        pickedStaffFile = nil
                        StaffImportState.shared.importDatabase(url)
                    }
                }) {
                    DocumentPicker(contentTypes: accessFileTypes) { urls in
                        pickedStaffFile = urls.first
                        showStaffPicker = false
                    }
                    .ignoresSafeArea()
                }
                .sheet(isPresented: $showImporter, onDismiss: startPendingImport) {
                    DocumentPicker(contentTypes: fileTypes) { urls in
                        pickedFiles = urls
                        showImporter = false
                    }
                    .ignoresSafeArea()
                }
                .overlay {
                    if isImporting {
                        ProgressView(tr("جارٍ الاستيراد… قد يستغرق دقيقة لكل ملف كبير", "Importing… a large file can take a minute"))
                            .padding(24)
                            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
                    }
                }
                .alert(tr("الاستيراد", "Import"), isPresented: Binding(get: { message != nil }, set: { if !$0 { message = nil } })) {
                    Button(tr("حسناً", "OK"), role: .cancel) {}
                } message: {
                    Text(message ?? "")
                }
                .confirmationDialog(
                    tr("حذف بيانات \(monthToDelete?.name ?? "")؟", "Delete \(monthToDelete?.name ?? "")?"),
                    isPresented: Binding(get: { monthToDelete != nil }, set: { if !$0 { monthToDelete = nil } }),
                    titleVisibility: .visible
                ) {
                    Button(tr("حذف", "Delete"), role: .destructive) {
                        if let name = monthToDelete?.name { deleteMonth(name) }
                    }
                } message: {
                    Text(tr("يُحذف الموظفون والاستقطاعات لهذا الشهر فقط. يمكنك استيراد الملف مرة أخرى لاحقاً.", "Only this month's employees and deductions are deleted. You can import the file again later."))
                }
                .task {
                    await applyLaunchOptions()
                }
                .onChange(of: appRouter.pendingPayrollImport, initial: true) { _, pending in
                    guard pending else { return }
                    appRouter.pendingPayrollImport = false
                    showImporter = true
                }
                .onChange(of: appRouter.pendingStaffImport, initial: true) { _, pending in
                    guard pending else { return }
                    appRouter.pendingStaffImport = false
                    showStaffPicker = true
                }
        }
    }

    /// خيارات سريعة لعدد الأشهر (الأحدث أولاً).
    private var quickCounts: [Int] { [1, 3, 6].filter { $0 < months.count } }

    /// عدد الأشهر الأحدث المختارة إذا كان الاختيار "آخر n أشهر" بالضبط، وإلا nil.
    private var latestCount: Int? {
        let chosen = selectedMonths.map(\.name)
        let latest = months.prefix(chosen.count).map(\.name)
        return Set(chosen) == Set(latest) ? chosen.count : nil
    }

    /// زر الأشهر: آخر شهر / 3 / 6 / الكل، أو تحديد أشهر بعينها (شهر أو عدة أشهر).
    private var monthMenu: some View {
        let chosen = Set(selectedMonths.map(\.name))
        let latest = latestCount
        return Menu {
            Section(tr("عدد الأشهر", "Number of months")) {
                ForEach(quickCounts, id: \.self) { n in
                    Button {
                        setSelection(months.prefix(n).map(\.name))
                    } label: {
                        checkLabel(n == 1 ? tr("آخر شهر", "Latest month") : tr("آخر \(Num.format(n)) أشهر", "Last \(Num.format(n)) months"), latest == n)
                    }
                }
                Button {
                    setSelection(months.map(\.name))
                } label: {
                    checkLabel(tr("كل الأشهر", "All months"), chosen.count == months.count)
                }
            }
            Section(tr("أشهر محددة", "Specific months")) {
                ForEach(months) { month in
                    Button {
                        toggle(month)
                    } label: {
                        Label(month.name, systemImage: chosen.contains(month.name) ? "checkmark.circle.fill" : "circle")
                    }
                    .menuActionDismissBehavior(.disabled)
                }
            }
            Menu(tr("حذف شهر", "Delete a month"), systemImage: "trash") {
                ForEach(months) { month in
                    Button(month.name, role: .destructive) { monthToDelete = month }
                }
            }
        } label: {
            pillLabel(monthMenuTitle, systemImage: "calendar")
        }
        .glassButtonStyle(prominent: true)
        .buttonBorderShape(.capsule)
    }

    private var monthMenuTitle: String {
        let chosen = selectedMonths
        if chosen.count == months.count, months.count > 1 { return tr("كل الأشهر", "All months") }
        if chosen.count == 1 { return chosen[0].name }
        if latestCount != nil { return tr("آخر \(Num.format(chosen.count)) أشهر", "Last \(Num.format(chosen.count)) months") }
        if chosen.count == 2 { return chosen.map(\.shortName).joined(separator: " + ") }
        return tr("\(Num.format(chosen.count)) أشهر", "\(Num.format(chosen.count)) months")
    }

    /// زر العرض: تفاصيل الراتب و/أو الاستقطاعات (يبقى واحد على الأقل مختاراً).
    private var displayMenu: some View {
        let salary = showSalary || !showDeductions
        return Menu {
            Section(tr("ماذا يظهر في النتائج", "Show in results")) {
                Button {
                    guard showDeductions else { return }
                    showSalary = !salary
                } label: {
                    Label(tr("تفاصيل الراتب", "Salary details"), systemImage: salary ? "checkmark.circle.fill" : "circle")
                }
                .menuActionDismissBehavior(.disabled)
                Button {
                    if showDeductions { showSalary = true }
                    showDeductions.toggle()
                } label: {
                    Label(tr("الاستقطاعات", "Deductions"), systemImage: showDeductions ? "checkmark.circle.fill" : "circle")
                }
                .menuActionDismissBehavior(.disabled)
            }
        } label: {
            pillLabel(displayTitle, systemImage: showDeductions && !salary ? "minus.circle" : "list.bullet")
        }
        .glassButtonStyle()
        .buttonBorderShape(.capsule)
    }

    private var displayTitle: String {
        let salary = showSalary || !showDeductions
        switch (salary, showDeductions) {
        case (true, true): return tr("الراتب والاستقطاعات", "Salary and deductions")
        case (false, true): return tr("الاستقطاعات فقط", "Deductions only")
        default: return tr("تفاصيل الراتب", "Salary details")
        }
    }

    @ViewBuilder
    private func checkLabel(_ title: String, _ on: Bool) -> some View {
        if on { Label(title, systemImage: "checkmark") } else { Text(title) }
    }

    private func pillLabel(_ title: String, systemImage: String) -> some View {
        HStack(spacing: 6) {
            Image(systemName: systemImage)
            Text(title)
                .lineLimit(1)
            Image(systemName: "chevron.down")
                .font(.caption.weight(.bold))
        }
        .font(.subheadline.weight(.semibold))
    }

    private func applyLaunchOptions() async {
        if LaunchOptions.openSettings { showSettings = true }
        let files = LaunchOptions.importFiles
        if !files.isEmpty {
            await importFiles(files.map { URL(fileURLWithPath: $0) })
        }
        let key = ArabicText.normalize(search)
        if LaunchOptions.openFirst, !key.isEmpty, let monthName = currentMonth?.name {
            var descriptor = FetchDescriptor<Employee>(
                predicate: #Predicate { $0.month == monthName && $0.nameKey.contains(key) },
                sortBy: [SortDescriptor(\.name)]
            )
            descriptor.fetchLimit = 1
            if let first = try? context.fetch(descriptor).first {
                path = [first]
            }
        }
    }

    /// يبدأ الاستيراد بعد إغلاق نافذة الاختيار، ثم يحذف النسخ المؤقتة.
    private func startPendingImport() {
        let urls = pickedFiles
        pickedFiles = []
        guard !urls.isEmpty else { return }
        Task {
            await importFiles(urls)
            for url in urls { try? FileManager.default.removeItem(at: url) }
        }
    }

    /// كل ملف يصبح شهراً باسم الملف؛ استيراد نفس الشهر مرة أخرى يستبدل بياناته فقط.
    private func importFiles(_ urls: [URL]) async {
        isImporting = true
        let importer = DataImporter(modelContainer: context.container)
        let start = Date()
        var lines: [String] = []
        var failures: [String] = []
        var totals = (employees: 0, deductions: 0)
        var newest: (sortKey: Int, name: String)?

        for url in urls {
            let monthName = MonthName.label(for: url)
            do {
                // القراءة في الخلفية حتى لا تتجمد الواجهة مع الملفات الكبيرة.
                let imported = try await Task.detached(priority: .userInitiated) {
                    let granted = url.startAccessingSecurityScopedResource()
                    defer { if granted { url.stopAccessingSecurityScopedResource() } }
                    return try ExcelImporter.load(url: url)
                }.value
                try await importer.replace(month: monthName, with: imported)
                lines.append(tr("\(monthName): \(Num.format(imported.employees.count)) موظف و\(Num.format(imported.deductions.count)) استقطاع", "\(monthName): \(Num.format(imported.employees.count)) employees, \(Num.format(imported.deductions.count)) deductions"))
                totals.employees += imported.employees.count
                totals.deductions += imported.deductions.count
                let sortKey = MonthName.sortKey(monthName)
                if newest == nil || sortKey >= newest!.sortKey { newest = (sortKey, monthName) }
            } catch {
                failures.append("\(monthName): \(error.localizedDescription)")
            }
        }

        // بعد الاستيراد يُعرض أحدث شهر مستورد.
        if let newest { selectedMonth = newest.name }
        path = []
        isImporting = false
        var parts: [String] = []
        if !lines.isEmpty { parts += [tr("تم الاستيراد:", "Imported:")] + lines }
        if !failures.isEmpty { parts += ["", tr("تعذّر:", "Failed:")] + failures }
        message = parts.joined(separator: "\n")
        if !LaunchOptions.importFiles.isEmpty {
            let status = failures.isEmpty ? "" : " failed=\(failures.count)"
            LaunchOptions.writeReport("employees=\(totals.employees) deductions=\(totals.deductions) months=\(lines.count)\(status)"
                + String(format: " seconds=%.1f\n", Date().timeIntervalSince(start)))
        }
    }

    private func deleteMonth(_ name: String) {
        let importer = DataImporter(modelContainer: context.container)
        Task {
            do {
                try await importer.delete(month: name)
                setSelection(selectedMonths.map(\.name).filter { $0 != name })
                path = []
            } catch {
                message = error.localizedDescription
            }
        }
    }
}

struct EmployeeList: View {
    @Query private var employees: [Employee]
    @State private var shareTarget: Employee?
    private let tokens: [String]
    private let months: [PayrollMonth]
    private let showSalary: Bool
    private let showDeductions: Bool

    init(search: String, months: [PayrollMonth], showSalary: Bool = true, showDeductions: Bool = false) {
        self.months = months
        self.showSalary = showSalary
        self.showDeductions = showDeductions
        tokens = ArabicText.normalize(search).split(separator: " ").map(String.init)

        // قاعدة البيانات تُصفّي بالأشهر المختارة وأول كلمتين، والباقي يُطابق في الذاكرة
        // حتى يجد "محمد حسين" الاسم "محمد علي حسين".
        let monthNames = months.map(\.name)
        let first = tokens.first ?? ""
        let second = tokens.count > 1 ? tokens[1] : ""
        let filter: Predicate<Employee>
        if second.isEmpty {
            filter = #Predicate { monthNames.contains($0.month) && $0.nameKey.contains(first) }
        } else {
            filter = #Predicate { monthNames.contains($0.month) && $0.nameKey.contains(first) && $0.nameKey.contains(second) }
        }
        var descriptor = FetchDescriptor(predicate: filter, sortBy: [SortDescriptor(\.name)])
        // بدون بحث لا نعرض شيئاً (fetchLimit = 0 يعني "بلا حد" فنستخدم 1).
        descriptor.fetchLimit = tokens.isEmpty ? 1 : 300 * max(months.count, 1)
        _employees = Query(descriptor)
    }

    /// النتائج مجمّعة بالشهر (الأحدث أولاً)، وداخل كل شهر مرتبة بالاسم.
    private var sections: [MonthSection] {
        guard !tokens.isEmpty else { return [] }
        let matched = tokens.count > 2 ? employees.filter { e in tokens.allSatisfy { e.nameKey.contains($0) } } : employees
        let byMonth = Dictionary(grouping: matched, by: \.month)
        return months.compactMap { month in
            guard let list = byMonth[month.name], !list.isEmpty else { return nil }
            return MonthSection(month: month, employees: list)
        }
    }

    var body: some View {
        let sections = sections
        if !months.isEmpty {
            if tokens.isEmpty {
                ContentUnavailableView(
                    tr("ابحث بالاسم", "Search by name"),
                    systemImage: "magnifyingglass",
                    description: Text(summary)
                )
            } else if sections.isEmpty {
                ContentUnavailableView.search
            } else {
                List {
                    ForEach(sections) { section in
                        Section(section.month.name) {
                            ForEach(section.employees) { employee in
                                NavigationLink(value: employee) {
                                    EmployeeRow(employee: employee, showSalary: showSalary, showDeductions: showDeductions)
                                }
                                .contextMenu {
                                    Button(tr("مشاركة", "Share"), systemImage: "square.and.arrow.up") {
                                        shareTarget = employee
                                    }
                                }
                                .swipeActions(edge: .leading) {
                                    Button(tr("مشاركة", "Share"), systemImage: "square.and.arrow.up") {
                                        shareTarget = employee
                                    }
                                    .tint(.blue)
                                }
                            }
                        }
                    }
                }
                .listStyle(.insetGrouped)
                .scrollDismissesKeyboard(.immediately)
                .sheet(item: $shareTarget) { ShareOptionsSheet(employee: $0) }
            }
        } else {
            ContentUnavailableView(
                tr("لا توجد بيانات", "No data yet"),
                systemImage: "tablecells",
                description: Text(tr("افتح الإعدادات من زر الترس واضغط \"استيراد ملفات Excel\". كل ملف يُحفظ كشهر باسم الملف، مثل \"تشرين الاول 2026\".", "Open Settings from the gear button and tap \"Import Excel files\". Each file is saved as a month named after the file, like \"October 2026\"."))
            )
        }
    }

    private var summary: String {
        if months.count == 1, let month = months.first {
            return tr("شهر \(month.name): \(Num.format(month.employeeCount)) موظف و\(Num.format(month.deductionCount)) استقطاع.\nلتغيير الشهر أو اختيار عدة أشهر استخدم زر الأشهر في الأعلى.", "\(month.name): \(Num.format(month.employeeCount)) employees, \(Num.format(month.deductionCount)) deductions.\nUse the months button above to pick one or more months.")
        }
        let employees = months.reduce(0) { $0 + $1.employeeCount }
        return tr("\(Num.format(months.count)) أشهر مختارة: \(Num.format(employees)) سجل موظف.\nالنتائج تُعرض مجمّعة حسب الشهر.", "\(Num.format(months.count)) months selected: \(Num.format(employees)) employee records.\nResults are grouped by month.")
    }
}

struct MonthSection: Identifiable {
    let month: PayrollMonth
    let employees: [Employee]
    var id: String { month.name }
}

struct EmployeeRow: View {
    let employee: Employee
    var showSalary = true
    var showDeductions = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                NameAvatar(name: employee.name, size: 40)
                VStack(alignment: .leading, spacing: 2) {
                    Text(employee.name)
                        .font(.body.weight(.medium))
                        .lineLimit(2)
                    if showSalary {
                        Text([employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
                Spacer(minLength: 8)
                if showSalary {
                    Text(Money.format(employee.netSalary))
                        .font(.body.weight(.semibold).monospacedDigit())
                }
            }
            if showDeductions {
                DeductionLines(employee: employee)
                    .padding(.leading, 52)
            }
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }
}

/// استقطاعات موظف في شهره، تُعرض داخل صف النتيجة عند اختيار "الاستقطاعات".
struct DeductionLines: View {
    @Query private var deductions: [Deduction]

    init(employee: Employee) {
        let key = employee.matchKey
        let month = employee.month
        _deductions = Query(filter: #Predicate { $0.month == month && $0.matchKey == key }, sort: \Deduction.title)
    }

    var body: some View {
        if deductions.isEmpty {
            Label(tr("لا توجد استقطاعات", "No deductions"), systemImage: "checkmark.circle")
                .font(.subheadline)
                .foregroundStyle(.secondary)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                ForEach(deductions) { deduction in
                    HStack(alignment: .firstTextBaseline) {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(deduction.title.orDash)
                            if !deduction.note.isEmpty {
                                Text(deduction.note)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                        Spacer(minLength: 8)
                        Text(Money.format(deduction.amount))
                            .monospacedDigit()
                            .foregroundStyle(.red)
                    }
                    .font(.subheadline)
                }
                if deductions.count > 1 {
                    Divider()
                    HStack {
                        Text(tr("المجموع", "Total")).fontWeight(.semibold)
                        Spacer()
                        Text(Money.format(deductions.total))
                            .fontWeight(.bold)
                            .monospacedDigit()
                            .foregroundStyle(.red)
                    }
                    .font(.subheadline)
                }
            }
        }
    }
}
