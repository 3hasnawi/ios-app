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
    @State private var path: [Employee] = []
    @State private var showImporter = false
    @State private var isImporting = false
    @State private var message: String?
    @State private var monthToDelete: PayrollMonth?

    private let fileTypes: [UTType] = [
        UTType(filenameExtension: "xlsx") ?? .spreadsheet,
        .commaSeparatedText,
    ]

    /// الشهر المختار، أو أحدث شهر إذا لم يُختر شيء.
    private var currentMonth: PayrollMonth? {
        months.first { $0.name == selectedMonth } ?? months.first
    }

    var body: some View {
        NavigationStack(path: $path) {
            EmployeeList(search: search, month: currentMonth)
                .navigationTitle("الرواتب")
                .adaptiveSearchable(text: $search, prompt: "ابحث بالاسم")
                .navigationDestination(for: Employee.self) { EmployeeDetailView(employee: $0) }
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) {
                        monthMenu
                    }
                    ToolbarItem(placement: .primaryAction) {
                        Button {
                            showImporter = true
                        } label: {
                            Label("استيراد ملفات Excel", systemImage: "square.and.arrow.down")
                        }
                        .disabled(isImporting)
                    }
                }
                .fileImporter(isPresented: $showImporter, allowedContentTypes: fileTypes, allowsMultipleSelection: true) {
                    handleImport($0)
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

    /// قائمة اختيار الشهر الذي يتم البحث فيه.
    private var monthMenu: some View {
        Menu {
            Picker("الشهر", selection: Binding(
                get: { currentMonth?.name ?? "" },
                set: { selectedMonth = $0; path = [] }
            )) {
                ForEach(months) { month in
                    Text(month.name).tag(month.name)
                }
            }
            if let month = currentMonth {
                Divider()
                Button("حذف بيانات \(month.name)", systemImage: "trash", role: .destructive) {
                    monthToDelete = month
                }
            }
        } label: {
            // HStack بدل Label: شريط الأدوات يعرض أيقونة Label فقط ويخفي اسم الشهر.
            HStack(spacing: 4) {
                Image(systemName: "calendar")
                Text(currentMonth?.name ?? "لا توجد أشهر")
                Image(systemName: "chevron.down").font(.caption2.weight(.semibold))
            }
        }
        .disabled(months.isEmpty)
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

    private func handleImport(_ result: Result<[URL], Error>) {
        switch result {
        case .success(let urls) where !urls.isEmpty:
            Task { await importFiles(urls) }
        case .success:
            break
        case .failure(let error):
            message = error.localizedDescription
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
                if selectedMonth == name { selectedMonth = "" }
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
    private let month: PayrollMonth?

    init(search: String, month: PayrollMonth?) {
        self.month = month
        tokens = ArabicText.normalize(search).split(separator: " ").map(String.init)

        // قاعدة البيانات تُصفّي بالشهر وأول كلمتين، والباقي يُطابق في الذاكرة
        // حتى يجد "محمد حسين" الاسم "محمد علي حسين".
        let monthName = month?.name ?? ""
        let first = tokens.first ?? ""
        let second = tokens.count > 1 ? tokens[1] : ""
        let filter: Predicate<Employee>
        if second.isEmpty {
            filter = #Predicate { $0.month == monthName && $0.nameKey.contains(first) }
        } else {
            filter = #Predicate { $0.month == monthName && $0.nameKey.contains(first) && $0.nameKey.contains(second) }
        }
        var descriptor = FetchDescriptor(predicate: filter, sortBy: [SortDescriptor(\.name)])
        // بدون بحث لا نعرض شيئاً (fetchLimit = 0 يعني "بلا حد" فنستخدم 1).
        descriptor.fetchLimit = tokens.isEmpty ? 1 : 300
        _employees = Query(descriptor)
    }

    private var results: [Employee] {
        guard !tokens.isEmpty else { return [] }
        guard tokens.count > 2 else { return employees }
        return employees.filter { e in tokens.allSatisfy { e.nameKey.contains($0) } }
    }

    var body: some View {
        let results = results
        if let month {
            if tokens.isEmpty {
                ContentUnavailableView(
                    "ابحث بالاسم",
                    systemImage: "magnifyingglass",
                    description: Text("شهر \(month.name): \(month.employeeCount.formatted()) موظف و\(month.deductionCount.formatted()) استقطاع.\nلتغيير الشهر اضغط على اسمه في الأعلى.")
                )
            } else if results.isEmpty {
                ContentUnavailableView.search
            } else {
                List(results) { employee in
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
                .listStyle(.plain)
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
}

struct EmployeeRow: View {
    let employee: Employee

    var body: some View {
        HStack(spacing: 12) {
            NameAvatar(name: employee.name)
            VStack(alignment: .leading, spacing: 3) {
                Text(employee.name)
                    .font(.headline)
                    .lineLimit(1)
                Text([employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            VStack(alignment: .trailing, spacing: 2) {
                Text(Money.format(employee.netSalary))
                    .font(.subheadline.weight(.semibold).monospacedDigit())
                Text("صافي")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 4)
    }
}
