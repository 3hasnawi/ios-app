import SwiftUI
import SwiftData
import UniformTypeIdentifiers

/// خيارات تشغيل للاختبار الآلي على المحاكي، مثلاً:
/// `xcrun simctl launch <device> com.example.SalaryApp -importFile /path/file.xlsx -search "احمد" -openFirst YES`
/// لا تؤثر على الاستخدام العادي لأنها لا تُضبط إلا من سطر الأوامر.
private enum LaunchOptions {
    static var importFile: String? { UserDefaults.standard.string(forKey: "importFile") }
    static var search: String { UserDefaults.standard.string(forKey: "search") ?? "" }
    static var openFirst: Bool { UserDefaults.standard.bool(forKey: "openFirst") }

    /// نتيجة الاستيراد تُكتب في Documents/import-report.txt ليقرأها سكربت الاختبار.
    static func writeReport(_ text: String) {
        guard let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first else { return }
        try? text.write(to: docs.appendingPathComponent("import-report.txt"), atomically: true, encoding: .utf8)
    }
}

struct ContentView: View {
    @Environment(\.modelContext) private var context
    @State private var search = LaunchOptions.search
    @State private var path: [Employee] = []
    @State private var showImporter = false
    @State private var isImporting = false
    @State private var message: String?
    @State private var employeeCount = 0

    private let fileTypes: [UTType] = [
        UTType(filenameExtension: "xlsx") ?? .spreadsheet,
        .commaSeparatedText,
    ]

    var body: some View {
        NavigationStack(path: $path) {
            EmployeeList(search: search, employeeCount: employeeCount)
                .navigationTitle("الرواتب")
                .searchable(text: $search, placement: .navigationBarDrawer(displayMode: .always), prompt: "ابحث بالاسم")
                .navigationDestination(for: Employee.self) { EmployeeDetailView(employee: $0) }
                .toolbar {
                    ToolbarItem(placement: .primaryAction) {
                        Button {
                            showImporter = true
                        } label: {
                            Label("استيراد ملف Excel", systemImage: "square.and.arrow.down")
                        }
                        .disabled(isImporting)
                    }
                }
                .fileImporter(isPresented: $showImporter, allowedContentTypes: fileTypes, allowsMultipleSelection: true) {
                    handleImport($0)
                }
                .overlay {
                    if isImporting {
                        ProgressView("جارٍ الاستيراد… قد يستغرق دقيقة للملفات الكبيرة")
                            .padding(24)
                            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
                    }
                }
                .alert("الاستيراد", isPresented: Binding(get: { message != nil }, set: { if !$0 { message = nil } })) {
                    Button("حسناً", role: .cancel) {}
                } message: {
                    Text(message ?? "")
                }
                .task {
                    refreshCount()
                    await applyLaunchOptions()
                }
        }
    }

    private func refreshCount() {
        employeeCount = (try? context.fetchCount(FetchDescriptor<Employee>())) ?? 0
    }

    private func applyLaunchOptions() async {
        if let file = LaunchOptions.importFile {
            await importFiles([URL(fileURLWithPath: file)])
        }
        let key = ArabicText.normalize(search)
        if LaunchOptions.openFirst, !key.isEmpty {
            var descriptor = FetchDescriptor<Employee>(predicate: #Predicate { $0.nameKey.contains(key) }, sortBy: [SortDescriptor(\.name)])
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

    private func importFiles(_ urls: [URL]) async {
        isImporting = true
        let container = context.container
        let start = Date()
        let report: String
        do {
            // القراءة والحفظ في الخلفية حتى لا تتجمد الواجهة مع الملفات الكبيرة.
            let imported = try await Task.detached(priority: .userInitiated) {
                var all = ImportResult()
                for url in urls {
                    let granted = url.startAccessingSecurityScopedResource()
                    defer { if granted { url.stopAccessingSecurityScopedResource() } }
                    all.merge(try ExcelImporter.load(url: url))
                }
                return all
            }.value
            try await DataImporter(modelContainer: container).replace(with: imported)
            message = "تم استيراد \(imported.employees.count) موظف و\(imported.deductions.count) استقطاع."
            report = "employees=\(imported.employees.count) deductions=\(imported.deductions.count)"
        } catch {
            message = error.localizedDescription
            report = "error=\(error.localizedDescription)"
        }
        refreshCount()
        isImporting = false
        if LaunchOptions.importFile != nil {
            LaunchOptions.writeReport(report + String(format: " seconds=%.1f\n", Date().timeIntervalSince(start)))
        }
    }
}

struct EmployeeList: View {
    @Query private var employees: [Employee]
    private let tokens: [String]
    private let employeeCount: Int

    init(search: String, employeeCount: Int) {
        self.employeeCount = employeeCount
        tokens = ArabicText.normalize(search).split(separator: " ").map(String.init)

        // قاعدة البيانات تُصفّي بأول كلمتين، والباقي يُطابق في الذاكرة
        // حتى يجد "محمد حسين" الاسم "محمد علي حسين".
        let first = tokens.first ?? ""
        let second = tokens.count > 1 ? tokens[1] : ""
        let filter: Predicate<Employee>
        if second.isEmpty {
            filter = #Predicate { $0.nameKey.contains(first) }
        } else {
            filter = #Predicate { $0.nameKey.contains(first) && $0.nameKey.contains(second) }
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
        if tokens.isEmpty {
            if employeeCount == 0 {
                ContentUnavailableView(
                    "لا توجد بيانات",
                    systemImage: "tablecells",
                    description: Text("اضغط زر الاستيراد في الأعلى واختر ملف Excel للرواتب.")
                )
            } else {
                ContentUnavailableView(
                    "ابحث بالاسم",
                    systemImage: "magnifyingglass",
                    description: Text("قاعدة البيانات تحتوي على \(employeeCount) موظف.")
                )
            }
        } else if results.isEmpty {
            ContentUnavailableView.search
        } else {
            List(results) { employee in
                NavigationLink(value: employee) {
                    EmployeeRow(employee: employee)
                }
            }
            .listStyle(.plain)
        }
    }
}

struct EmployeeRow: View {
    let employee: Employee

    var body: some View {
        HStack {
            VStack(alignment: .leading, spacing: 4) {
                Text(employee.name).font(.headline)
                Text([employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer()
            Text(Money.format(employee.netSalary))
                .font(.subheadline.monospacedDigit())
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }
}
