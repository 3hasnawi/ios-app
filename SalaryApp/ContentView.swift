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
            EmployeeList(search: appliedSearch, months: selectedMonths)
                .safeAreaInset(edge: .top, spacing: 0) {
                    if !months.isEmpty {
                        monthMenu
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 20)
                            .padding(.top, 4)
                            .padding(.bottom, 8)
                    }
                }
                .background(Color(.systemGroupedBackground))
                .adaptiveSearchable(text: $search, prompt: "ابحث بالاسم")
                .onSubmit(of: .search) { appliedSearch = search }
                .task(id: search) {
                    // مسح النص يُطبَّق فوراً؛ الكتابة تنتظر توقفاً قصيراً.
                    if !search.isEmpty {
                        try? await Task.sleep(for: .milliseconds(220))
                        guard !Task.isCancelled else { return }
                    }
                    if appliedSearch != search { appliedSearch = search }
                }
                .navigationTitle("الرواتب")
                .navigationBarTitleDisplayMode(.large)
                .navigationDestination(for: Employee.self) { EmployeeDetailView(employee: $0) }
                .toolbar {
                    ToolbarItem(placement: .primaryAction) {
                        Button {
                            showImporter = true
                        } label: {
                            Label("استيراد ملفات Excel", systemImage: "square.and.arrow.down")
                        }
                        .disabled(isImporting)
                    }
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
                        ProgressView("جارٍ الاستيراد… قد يستغرق دقيقة لكل ملف كبير")
                            .padding(24)
                            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
                    }
                }
                .alert("الاستيراد", isPresented: Binding(get: { message != nil }, set: { if !$0 { message = nil } })) {
                    Button("حسناً", role: .cancel) {}
                } message: {
                    Text(message ?? "")
                }
                .confirmationDialog(
                    "حذف بيانات \(monthToDelete?.name ?? "")؟",
                    isPresented: Binding(get: { monthToDelete != nil }, set: { if !$0 { monthToDelete = nil } }),
                    titleVisibility: .visible
                ) {
                    Button("حذف", role: .destructive) {
                        if let name = monthToDelete?.name { deleteMonth(name) }
                    }
                } message: {
                    Text("يُحذف الموظفون والاستقطاعات لهذا الشهر فقط. يمكنك استيراد الملف مرة أخرى لاحقاً.")
                }
                .task {
                    await applyLaunchOptions()
                }
        }
    }

    /// قائمة منسدلة لاختيار شهر أو عدة أشهر (تبقى مفتوحة أثناء التحديد).
    private var monthMenu: some View {
        let chosen = Set(selectedMonths.map(\.name))
        return Menu {
            Button("كل الأشهر", systemImage: chosen.count == months.count ? "checkmark.circle.fill" : "circle") {
                setSelection(months.map(\.name))
            }
            .menuActionDismissBehavior(.disabled)
            Divider()
            ForEach(months) { month in
                Button {
                    toggle(month)
                } label: {
                    if chosen.contains(month.name) {
                        Label(month.name, systemImage: "checkmark")
                    } else {
                        Text(month.name)
                    }
                }
                .menuActionDismissBehavior(.disabled)
            }
            Divider()
            Menu("حذف شهر", systemImage: "trash") {
                ForEach(months) { month in
                    Button(month.name, role: .destructive) { monthToDelete = month }
                }
            }
        } label: {
            // كبسولة ملوّنة: زجاج بارز على iOS 26، وزر ممتلئ على iOS 18.
            HStack(spacing: 6) {
                Image(systemName: "calendar")
                Text(monthMenuTitle)
                    .lineLimit(1)
                Image(systemName: "chevron.down")
                    .font(.caption.weight(.bold))
            }
            .font(.subheadline.weight(.semibold))
        }
        .glassButtonStyle(prominent: true)
        .buttonBorderShape(.capsule)
    }

    private var monthMenuTitle: String {
        let chosen = selectedMonths
        if chosen.count == 1 { return chosen[0].name }
        if chosen.count == months.count, months.count > 2 { return "كل الأشهر" }
        if chosen.count == 2 { return chosen.map(\.shortName).joined(separator: " + ") }
        return "\(chosen.count.formatted()) أشهر"
    }

    private func applyLaunchOptions() async {
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
                lines.append("\(monthName): \(imported.employees.count.formatted()) موظف و\(imported.deductions.count.formatted()) استقطاع")
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
        if !lines.isEmpty { parts += ["تم الاستيراد:"] + lines }
        if !failures.isEmpty { parts += ["", "تعذّر:"] + failures }
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

    init(search: String, months: [PayrollMonth]) {
        self.months = months
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
                    "ابحث بالاسم",
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
                                    EmployeeRow(employee: employee)
                                }
                                .contextMenu {
                                    Button("مشاركة", systemImage: "square.and.arrow.up") {
                                        shareTarget = employee
                                    }
                                }
                                .swipeActions(edge: .leading) {
                                    Button("مشاركة", systemImage: "square.and.arrow.up") {
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
                "لا توجد بيانات",
                systemImage: "tablecells",
                description: Text("اضغط زر الاستيراد واختر ملف Excel أو أكثر. كل ملف يُحفظ كشهر باسم الملف، مثل \"تشرين الاول 2026\".")
            )
        }
    }

    private var summary: String {
        if months.count == 1, let month = months.first {
            return "شهر \(month.name): \(month.employeeCount.formatted()) موظف و\(month.deductionCount.formatted()) استقطاع.\nلتغيير الشهر أو اختيار عدة أشهر استخدم القائمة في الأعلى."
        }
        let employees = months.reduce(0) { $0 + $1.employeeCount }
        return "\(months.count.formatted()) أشهر مختارة: \(employees.formatted()) سجل موظف.\nالنتائج تُعرض مجمّعة حسب الشهر."
    }
}

struct MonthSection: Identifiable {
    let month: PayrollMonth
    let employees: [Employee]
    var id: String { month.name }
}

struct EmployeeRow: View {
    let employee: Employee

    var body: some View {
        HStack(spacing: 12) {
            NameAvatar(name: employee.name, size: 40)
            VStack(alignment: .leading, spacing: 2) {
                Text(employee.name)
                    .font(.body.weight(.medium))
                    .lineLimit(2)
                Text([employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            Text(Money.format(employee.netSalary))
                .font(.body.weight(.semibold).monospacedDigit())
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }
}
