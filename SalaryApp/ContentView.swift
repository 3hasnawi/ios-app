import SwiftUI
import SwiftData
import UniformTypeIdentifiers
import VisionKit

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
    /// يفتح صفحة أول نتيجة في البحث الموحّد، وقسمها: salary / staff / months.
    static var openPerson: Bool { UserDefaults.standard.bool(forKey: "openPerson") }
    static var personPart: String { UserDefaults.standard.string(forKey: "personPart") ?? "" }
    /// يفتح أول مجموعة في تبويب التقارير.
    static var openReport: Bool { UserDefaults.standard.bool(forKey: "openReport") }
    /// التبويب عند التشغيل: "search" لتبويب العدسة.
    static var startTab: String { UserDefaults.standard.string(forKey: "tab") ?? "" }

    /// نتيجة الاستيراد تُكتب في Documents/import-report.txt ليقرأها سكربت الاختبار.
    static func writeReport(_ text: String) {
        guard let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first else { return }
        try? text.write(to: docs.appendingPathComponent("import-report.txt"), atomically: true, encoding: .utf8)
    }

    static func writeImportStage(_ stage: String) {
        #if targetEnvironment(simulator)
        guard let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first else { return }
        try? stage.write(to: docs.appendingPathComponent("import-stage.txt"), atomically: true, encoding: .utf8)
        #endif
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
    /// اسم قادم من اختصار "فتح البحث في الرواتب".
    @State private var router = ShortcutRouter.shared
    @State private var importCenter = ImportCenter.shared
    @State private var appRouter = AppRouter.shared
    @State private var search = LaunchOptions.search
    /// النص المطبَّق فعلاً على البحث: يتأخر قليلاً عن الكتابة حتى لا يُعاد البحث مع كل حرف.
    @State private var appliedSearch = LaunchOptions.search
    @State private var path: [Employee] = []
    @State private var showingBookScanner = false
    @State private var bookDraft: ScannedBookDraft?
    @State private var scannedBookMonth = ""
    @State private var scanError: String?

    private let fileTypes: [UTType] = [
        UTType(filenameExtension: "xlsx") ?? .spreadsheet,
        .commaSeparatedText,
    ]

    /// الاختيار القديم المتعدد لا يطابق اسماً واحداً، فيُفتح أحدث شهر تلقائياً.
    private var currentMonth: PayrollMonth? {
        months.first { $0.name == selectedMonth } ?? months.first
    }

    var body: some View {
        NavigationStack(path: $path) {
            // اختيار شهر واحد من تقويم شريط العنوان؛ أحدث شهر هو الافتراضي.
            // الشاشة الرئيسية: ملخص الشهر وآخر عمليات البحث، والشعار خلفية شفافة.
            // البحث نفسه في تبويب العدسة.
            HomeDashboard(months: months, current: currentMonth)
                .id("\(language)-\(numerals)")
                // زر الأشهر في شريط العنوان (لا يغطي البطاقات عند التمرير).
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) { scanBookButton }
                    if !months.isEmpty {
                        ToolbarItem(placement: .topBarTrailing) { monthMenu }
                    }
                }
                .background(Color(.systemGroupedBackground))
                .navigationTitle(tr("الرئيسية", "Home"))
                // عنوان صغير حتى تبدأ البطاقات من أعلى الشاشة.
                .navigationBarTitleDisplayMode(.inline)
                .sheet(isPresented: $showingBookScanner) {
                    OfficialBookScanner(onComplete: { images in
                        showingBookScanner = false
                        Task { @MainActor in
                            try? await Task.sleep(for: .milliseconds(250))
                            bookDraft = ScannedBookDraft(images: images)
                        }
                    }, onFailure: { message in
                        showingBookScanner = false
                        scanError = message
                    }, onCancel: { showingBookScanner = false })
                }
                .sheet(item: $bookDraft) { draft in
                    OfficialBookEditor(months: months.map(\.name), initialMonth: scannedBookMonth,
                                       images: draft.images, existing: nil)
                }
                .alert(tr("الأرشيف", "Archive"), isPresented: Binding(
                    get: { scanError != nil }, set: { if !$0 { scanError = nil } })) {
                    Button(tr("حسناً", "OK"), role: .cancel) {}
                } message: { Text(scanError ?? "") }
                .navigationDestination(for: Employee.self) { EmployeeDetailView(employee: $0) }
                .task {
                    await applyLaunchOptions()
                }
                .onChange(of: appRouter.pendingPayrollImport || appRouter.pendingStaffImport, initial: true) { _, pending in
                    guard pending else { return }
                    let staff = appRouter.pendingStaffImport
                    appRouter.pendingPayrollImport = false
                    appRouter.pendingStaffImport = false
                    if staff { importCenter.pickStaff() } else { importCenter.pick() }
                }
                // بعد الاستيراد يُعرض أحدث شهر رواتب مستورد.
                .onChange(of: importCenter.lastPayrollMonth) { _, name in
                    guard let name else { return }
                    selectedMonth = months.first?.name ?? name
                    path = []
                    importCenter.lastPayrollMonth = nil
                }
                .onChange(of: months.first?.name) { _, newest in
                    selectedMonth = newest ?? ""
                }
        }
    }

    private var scanBookButton: some View {
        Button {
            guard let currentMonth else {
                scanError = tr("استورد شهراً أولاً لحفظ الكتاب ضمنه.", "Import a month first to save a document in it.")
                return
            }
            guard VNDocumentCameraViewController.isSupported else {
                scanError = OfficialBookError.unavailableCamera.localizedDescription
                return
            }
            scannedBookMonth = currentMonth.name
            showingBookScanner = true
        } label: {
            Image(systemName: "doc.viewfinder")
        }
        .accessibilityLabel(tr("مسح كتاب جديد", "Scan a document"))
        .accessibilityIdentifier("homeScanOfficialBook")
        .glassButtonStyle()
        .buttonBorderShape(.circle)
    }

    /// اختيار شهر واحد بضغطة واحدة؛ إدارة الحذف موجودة في الإعدادات فقط.
    private var monthMenu: some View {
        Menu {
            ForEach(months) { month in
                Button {
                    selectedMonth = month.name
                    path = []
                } label: {
                    if month.name == currentMonth?.name { Label(month.name, systemImage: "checkmark") }
                    else { Text(month.name) }
                }
            }
        } label: {
            Image(systemName: "calendar")
                .accessibilityLabel(tr("اختيار الشهر: \(currentMonth?.name ?? "")", "Choose month: \(currentMonth?.name ?? "")"))
        }
        .accessibilityIdentifier("homeMonthMenu")
        .glassButtonStyle()
        .buttonBorderShape(.circle)
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
        let files = LaunchOptions.importFiles
        if !files.isEmpty {
            LaunchOptions.writeImportStage("launch")
            // اختبار المحاكي: يستورد مباشرة، أو يعرض نافذة المراجعة فقط (-importReview YES).
            let review = UserDefaults.standard.bool(forKey: "importReview")
            await importCenter.analyze(files.map { URL(fileURLWithPath: $0) }, autoRun: !review)
        }
        let key = ArabicText.normalize(ArabicText.cleanQuery(search))
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
        tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)

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
                systemImage: ReportSymbols.excel,
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
