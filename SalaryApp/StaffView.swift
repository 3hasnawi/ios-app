import SwiftUI
import SwiftData
import UniformTypeIdentifiers

/// التبويبات وطلبات الانتقال بينها (مثل "عرض راتبه" من صفحة الملاك).
@MainActor @Observable
final class AppRouter {
    static let shared = AppRouter()
    enum Tab: Hashable { case payroll, staff, search, bank, settings }
    /// البحث يفتح تبويب العدسة؛ إلا عند فتح صفحة موظف مباشرة (اختبار المحاكي).
    var tab: Tab = LaunchOptions.startTab == "bank" ? .bank
        : LaunchOptions.startTab == "reports" ? .payroll
        : LaunchOptions.openSettings || LaunchOptions.startTab == "settings" ? .settings
        : LaunchOptions.startTab == "search" || (!LaunchOptions.search.isEmpty && !LaunchOptions.openFirst) ? .search : .payroll
    /// يفتح نافذة اختيار ملف الملاك في تبويب الملاك.
    var pendingStaffImport = false
    /// يفتح نافذة اختيار ملفات الرواتب في تبويب الرواتب.
    var pendingPayrollImport = false
    /// القسيمة المعروضة الآن للمشاركة من الضغط المطوّل عليها.
    var payslipToShare: Employee?
    var shareMode: ShareOptionsSheet.Mode?
    /// صفحة التقارير تُفتح من بطاقة الرئيسية، وتبقى مساراتها الداخلية كما هي.
    var showReports = LaunchOptions.startTab == "reports"
    /// صفحة في تبويب التقارير تُفتح من إشعار (التدقيق، الرواتب، الاستقطاعات).
    var pendingReport: ReportsRoute?
    var showShare = false
}

/// حالة استيراد الملاك، يراها التبويبان والإعدادات.
@MainActor @Observable
final class StaffImportState {
    static let shared = StaffImportState()
    var isImporting = false
    var message: String?
    /// يزيد بعد كل استيراد أو حذف حتى تُحدّث الواجهة.
    var version = 0

    /// يصحّح العدد الظاهر بعد تحديث التطبيق أو استرجاع نسخة اعتماداً على السجلات الفعلية.
    func refreshStoredCount() async {
        let container = SharedModel.container
        let actual = try? await Task.detached(priority: .utility) {
            try ModelContext(container).fetchCount(FetchDescriptor<StaffMember>())
        }.value
        guard let actual, actual != StaffInfo.count else { return }
        StaffInfo.reconcile(count: actual)
        version += 1
    }

    func importDatabase(_ url: URL) {
        guard !isImporting else { return }
        guard !BackupManager.shared.isWorking else {
            message = tr("انتظر انتهاء النسخ أو الاسترجاع قبل استيراد الملاك.", "Wait for backup or restore to finish before importing staff.")
            return
        }
        isImporting = true
        let progress = ImportProgress.shared
        progress.begin(tr("استيراد الملاك", "Importing staff"))
        let sink = progress.sink(0, 1, tr("فتح الملف", "Opening the file"))
        let container = SharedModel.container
        Task {
            do {
                // يُنشأ في الخلفية حتى لا تعمل القراءة والحفظ على خيط الواجهة.
                let count = try await Task.detached(priority: .userInitiated) {
                    try await StaffImporter(modelContainer: container).importDatabase(at: url, progress: sink)
                }.value
                message = tr("تم استيراد الملاك: \(Num.format(count)) موظف.", "Staff imported: \(Num.format(count)) employees.")
            } catch {
                message = error.localizedDescription
            }
            try? FileManager.default.removeItem(at: url)
            isImporting = false
            progress.end()
            version += 1
            await BackupManager.shared.refreshIntegrityWarning()
        }
    }

    func deleteAll() {
        BackupManager.shared.markChanged()
        let container = SharedModel.container
        Task {
            try? await Task.detached(priority: .userInitiated) {
                try await StaffImporter(modelContainer: container).deleteAll()
            }.value
            version += 1
        }
    }
}

let accessFileTypes: [UTType] = [
    UTType(filenameExtension: "accdb") ?? .data,
    UTType(filenameExtension: "mdb") ?? .data,
]

// MARK: - التبويبات

struct RootView: View {
    @State private var router = AppRouter.shared
    @State private var staffImport = StaffImportState.shared
    @State private var importProgress = ImportProgress.shared
    @State private var importCenter = ImportCenter.shared
    @Environment(\.scenePhase) private var scenePhase
    @State private var backup = BackupManager.shared
    private var selection: Binding<AppRouter.Tab> {
        Binding(
            get: { router.tab },
            set: { router.tab = $0 }
        )
    }

    var body: some View {
        TabView(selection: selection) {
            Tab(tr("الرئيسية", "Home"), systemImage: "house", value: AppRouter.Tab.payroll) {
                ContentView()
            }
            Tab(tr("كشف البنك", "Bank statement"), systemImage: "building.columns", value: AppRouter.Tab.bank) {
                BankStatementView()
            }
            Tab(tr("الإعدادات", "Settings"), systemImage: "gearshape", value: AppRouter.Tab.settings) {
                SettingsView()
            }
            Tab(tr("بحث", "Search"), systemImage: "magnifyingglass", value: AppRouter.Tab.search, role: .search) {
                UnifiedSearchView()
            }
        }
        // النظام يرسم الزجاج ويحرّك المؤشر ويصغّر الشريط أثناء التمرير.
        .tabBarMinimizeBehavior(.onScrollDown)
        .fullScreenCover(isPresented: $router.showReports) {
            ReportsView(presentedFromHome: true)
        }
        // بيانات ملاك وهمية وتنقّل آلي لاختبار المحاكي فقط (-fakeStaff، -autopilot).
        .task {
            PerfLog.record("firstAppear", since: PerfLog.processStart)
            await staffImport.refreshStoredCount()
            await BackupManager.shared.establishSafetyBaseline()
            await BackupManager.shared.refreshIntegrityWarning()
            await TestData.prepareIfRequested()
            TestData.checkRawPayrollIfRequested()
            #if targetEnvironment(simulator)
            await SmartShortcutSelfTest.runIfRequested()
            await StaffReportSelfTest.runIfRequested()
            #endif
            if UserDefaults.standard.bool(forKey: "backupTest") { await BackupManager.shared.selfTest() }
            if UserDefaults.standard.bool(forKey: "autoBackupTest") { await BackupManager.shared.autoBackupSelfTest() }
            await BackupManager.shared.autoBackupIfNeeded()
            await AutoPilot.shared.run()
        }
        // نسخة فاتت (أُغلق التطبيق أثناء النسخ أو تعذّر الوصول للمجلد): تُكمل عند فتح التطبيق أو الخروج منه.
        .onChange(of: scenePhase) { _, phase in
            guard phase != .inactive else { return }
            Task {
                if phase == .active { try? await Task.sleep(for: .seconds(3)) }
                if phase == .active { await BackupManager.shared.refreshIntegrityWarning() }
                await BackupManager.shared.autoBackupIfNeeded()
            }
        }
        // أثناء بقاء التطبيق مفتوحاً تُراجع ساعة النسخ دون انتظار استيراد جديد.
        .onReceive(Timer.publish(every: 60, on: .main, in: .common).autoconnect()) { _ in
            guard scenePhase == .active else { return }
            Task {
                await BackupManager.shared.autoBackupIfNeeded()
            }
        }
        // شريط تقدّم الاستيراد (رواتب أو ملاك) فوق الشريط السفلي أياً كان التبويب.
        .overlay(alignment: .bottom) {
            if importProgress.isActive {
                ImportProgressBar(progress: importProgress)
                    .padding(.bottom, 76)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .sheet(isPresented: $router.showShare, onDismiss: { router.shareMode = nil }) {
            if let employee = router.payslipToShare { ShareOptionsSheet(employee: employee, initialMode: router.shareMode) }
        }
        // استيراد موحّد: نافذة الملفات، ثم مراجعة ما تعرّف عليه التطبيق، ثم النتيجة.
        .sheet(isPresented: $importCenter.showReview) {
            ImportReviewSheet(center: importCenter)
        }
        .alert(tr("الاستيراد", "Import"), isPresented: Binding(
            get: { importCenter.message != nil },
            set: { if !$0 { importCenter.message = nil } }
        )) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: {
            Text(importCenter.message ?? "")
        }
        .alert(tr("الملاك", "Staff"), isPresented: Binding(
            get: { staffImport.message != nil },
            set: { if !$0 { staffImport.message = nil } }
        )) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: {
            Text(staffImport.message ?? "")
        }
        // الاسترجاع من نسخة (من الإعدادات أو من "استيراد ملفات") ورسائل النسخ الاحتياطي، أياً كان التبويب.
        .alert(tr("استرجاع النسخة؟", "Restore this backup?"), isPresented: Binding(
            get: { backup.pendingRestore != nil },
            set: { if !$0 && backup.pendingRestore != nil { backup.cancelRestore() } }
        )) {
            Button(tr("استرجاع", "Restore"), role: .destructive) { backup.confirmRestore() }
            Button(tr("إلغاء", "Cancel"), role: .cancel) { backup.cancelRestore() }
        } message: {
            if let m = backup.pendingRestore?.manifest {
                Text(tr("نسخة \(m.createdAt.formatted(.dateTime.year().month().day().hour().minute().locale(AppSettings.locale)))\n\(m.months.count) أشهر رواتب · \(Num.format(m.employees)) سجل رواتب · \(Num.format(m.staff)) في الملاك · \(Num.format(m.books ?? 0)) كتب PDF\nستُستبدل كل البيانات الحالية، بما فيها الكتب، بمحتوى النسخة.",
                        "Backup of \(m.createdAt.formatted())\n\(m.months.count) months · \(m.employees) payroll rows · \(m.staff) staff · \(m.books ?? 0) PDFs\nAll current data, including documents, will be replaced."))
            }
        }
        .alert(tr("النسخ الاحتياطي", "Backup"), isPresented: Binding(get: { backup.message != nil }, set: { if !$0 { backup.message = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: {
            Text(backup.message ?? "")
        }
    }
}

// MARK: - تبويب الملاك

enum StaffSearchMode: String, CaseIterable, Identifiable {
    case name, mother, statNumber, unifiedNumber
    var id: String { rawValue }

    var title: String {
        switch self {
        case .name: tr("الاسم", "Name")
        case .mother: tr("اسم الأم", "Mother's name")
        case .statNumber: tr("الرقم الإحصائي", "Statistical no.")
        case .unifiedNumber: tr("الرقم الموحد", "Unified no.")
        }
    }

    var systemImage: String {
        switch self {
        case .name: "person.fill"
        case .mother: "person.2.fill"
        case .statNumber: "number"
        case .unifiedNumber: "person.text.rectangle.fill"
        }
    }

    var color: Color {
        switch self {
        case .name: .blue
        case .mother: .purple
        case .statNumber: .orange
        case .unifiedNumber: .teal
        }
    }

    /// سطر توضيحي تحت النوع في قائمة البحث.
    var hint: String {
        switch self {
        case .name: tr("الرواتب والملاك", "Payroll and staff")
        case .mother: tr("للتمييز بين الأسماء المتشابهة", "To tell similar names apart")
        case .statNumber: tr("من الملاك", "From staff")
        case .unifiedNumber: tr("من الملاك", "From staff")
        }
    }

    var prompt: String {
        switch self {
        case .name: tr("ابحث بالاسم", "Search by name")
        case .mother: tr("ابحث باسم الأم", "Search by mother's name")
        case .statNumber: tr("ابحث بالرقم الإحصائي", "Search by statistical number")
        case .unifiedNumber: tr("ابحث بالرقم الموحد", "Search by unified number")
        }
    }
}

struct StaffView: View {
    @State private var router = AppRouter.shared
    @State private var importState = StaffImportState.shared
    @State private var search = ""
    @State private var appliedSearch = ""
    @AppStorage("staffSearchMode") private var modeRaw = StaffSearchMode.name.rawValue
    @AppStorage("staffStatusFilter") private var statusFilter = ""
    @State private var path: [StaffMember] = []
    @State private var showPicker = false
    @State private var pickedFile: URL?
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]

    private var mode: StaffSearchMode { StaffSearchMode(rawValue: modeRaw) ?? .name }

    var body: some View {
        NavigationStack(path: $path) {
            StaffResultsList(search: appliedSearch, mode: mode, status: statusFilter, version: importState.version)
                .id("\(AppSettings.language.rawValue)-\(AppSettings.numerals.rawValue)-\(importState.version)")
                .safeAreaInset(edge: .top, spacing: 0) {
                    if StaffInfo.count > 0 {
                        ScrollView(.horizontal, showsIndicators: false) {
                            HStack(spacing: 8) {
                                modeMenu
                                statusMenu
                            }
                            .padding(.horizontal, 20)
                            .padding(.top, 4)
                            .padding(.bottom, 8)
                        }
                        .scrollClipDisabled()
                    }
                }
                .background(Color(.systemGroupedBackground))
                .adaptiveSearchable(text: $search, prompt: mode.prompt)
                .onSubmit(of: .search) { appliedSearch = search }
                .task(id: search) {
                    if !search.isEmpty {
                        try? await Task.sleep(for: .milliseconds(250))
                        guard !Task.isCancelled else { return }
                    }
                    if appliedSearch != search { appliedSearch = search }
                }
                .navigationTitle(tr("الملاك", "Staff"))
                .navigationBarTitleDisplayMode(.large)
                .navigationDestination(for: StaffMember.self) { StaffDetailView(member: $0) }
                .sheet(isPresented: $showPicker, onDismiss: {
                    if let url = pickedFile {
                        pickedFile = nil
                        importState.importDatabase(url)
                    }
                }) {
                    DocumentPicker(contentTypes: accessFileTypes) { urls in
                        pickedFile = urls.first
                        showPicker = false
                    }
                    .ignoresSafeArea()
                }
                .overlay {
                    if importState.isImporting {
                        ProgressView(tr("جارٍ استيراد الملاك… قد يستغرق دقيقة أو اثنتين", "Importing staff… this can take a minute or two"))
                            .padding(24)
                            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
                    }
                }
                .alert(tr("الملاك", "Staff"), isPresented: Binding(
                    get: { importState.message != nil },
                    set: { if !$0 { importState.message = nil } }
                )) {
                    Button(tr("حسناً", "OK"), role: .cancel) {}
                } message: {
                    Text(importState.message ?? "")
                }
                .onChange(of: router.pendingStaffImport, initial: true) { _, pending in
                    guard pending else { return }
                    router.pendingStaffImport = false
                    showPicker = true
                }
        }
    }

    private var modeMenu: some View {
        Menu {
            Picker(tr("البحث حسب", "Search by"), selection: $modeRaw) {
                ForEach(StaffSearchMode.allCases) { m in
                    Text(m.title).tag(m.rawValue)
                }
            }
        } label: {
            pill(tr("بحث: \(mode.title)", "By: \(mode.title)"), systemImage: "magnifyingglass")
        }
        .glassButtonStyle(prominent: true)
        .buttonBorderShape(.capsule)
    }

    private var statusMenu: some View {
        Menu {
            Picker(tr("الحالة الوظيفية", "Status"), selection: $statusFilter) {
                Text(tr("كل الحالات", "All statuses")).tag("")
                ForEach(StaffInfo.statuses, id: \.self) { Text($0).tag($0) }
            }
        } label: {
            pill(statusFilter.isEmpty ? tr("كل الحالات", "All statuses") : statusFilter, systemImage: "line.3.horizontal.decrease")
        }
        .glassButtonStyle()
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
}

/// نتائج البحث في الملاك.
struct StaffResultsList: View {
    @Query private var members: [StaffMember]
    private let tokens: [String]
    private let status: String
    private let mode: StaffSearchMode

    init(search: String, mode: StaffSearchMode, status: String, version: Int) {
        self.mode = mode
        self.status = status
        let isNumber = mode == .statNumber || mode == .unifiedNumber
        let normalized = isNumber
            ? ArabicText.latinDigits(search).filter(\.isNumber)
            : ArabicText.normalize(ArabicText.cleanQuery(search))
        tokens = isNumber ? (normalized.isEmpty ? [] : [normalized]) : normalized.split(separator: " ").map(String.init)
        var descriptor = FetchDescriptor<StaffMember>(predicate: Self.predicate(mode: mode, tokens: tokens), sortBy: [SortDescriptor(\.name)])
        descriptor.fetchLimit = tokens.isEmpty ? 1 : 5000
        _members = Query(descriptor)
    }

    /// أول ثلاث كلمات تُصفّى داخل قاعدة البيانات، والباقي في الذاكرة.
    nonisolated static func predicate(mode: StaffSearchMode, tokens: [String]) -> Predicate<StaffMember> {
        let a = tokens.first ?? ""
        let b = tokens.count > 1 ? tokens[1] : ""
        let c = tokens.count > 2 ? tokens[2] : ""
        switch (mode, min(tokens.count, 3)) {
        case (.statNumber, _): return #Predicate { $0.statNumber.contains(a) }
        case (.unifiedNumber, _): return #Predicate { $0.unifiedNumber.contains(a) }
        case (.mother, 0), (.mother, 1): return #Predicate { $0.motherKey.contains(a) }
        case (.mother, 2): return #Predicate { $0.motherKey.contains(a) && $0.motherKey.contains(b) }
        case (.mother, _): return #Predicate { $0.motherKey.contains(a) && $0.motherKey.contains(b) && $0.motherKey.contains(c) }
        case (_, 0), (_, 1): return #Predicate { $0.nameKey.contains(a) }
        case (_, 2): return #Predicate { $0.nameKey.contains(a) && $0.nameKey.contains(b) }
        default: return #Predicate { $0.nameKey.contains(a) && $0.nameKey.contains(b) && $0.nameKey.contains(c) }
        }
    }

    private var results: [StaffMember] {
        guard !tokens.isEmpty else { return [] }
        return members.filter { m in
            (status.isEmpty || m.status == status) && tokens.allSatisfy { t in
                switch mode {
                case .name: m.nameKey.contains(t)
                case .mother: m.motherKey.contains(t)
                case .statNumber: m.statNumber.contains(t)
                case .unifiedNumber: m.unifiedNumber.contains(t)
                }
            }
        }
    }

    var body: some View {
        let results = results
        if StaffInfo.count == 0 {
            ContentUnavailableView {
                Label(tr("لا توجد بيانات ملاك", "No staff data yet"), systemImage: "person.3")
            } description: {
                Text(tr("استورد ملف قاعدة بيانات الملاك (Access ‎.accdb) من الإعدادات، أو من الزر أدناه.",
                        "Import the staff Access database (.accdb) from Settings or with the button below."))
            } actions: {
                Button(tr("استيراد ملف الملاك", "Import staff database")) {
                    AppRouter.shared.pendingStaffImport = true
                }
                .glassButtonStyle(prominent: true)
            }
        } else if tokens.isEmpty {
            ContentUnavailableView(
                tr("ابحث في الملاك", "Search staff"),
                systemImage: "magnifyingglass",
                description: Text(tr("\(Num.format(StaffInfo.count)) موظف من ملف \"\(StaffInfo.fileName)\".",
                                     "\(Num.format(StaffInfo.count)) employees from \"\(StaffInfo.fileName)\"."))
            )
        } else if results.isEmpty {
            ContentUnavailableView.search
        } else {
            List {
                Section {
                    ForEach(results.prefix(300)) { member in
                        NavigationLink(value: member) { StaffRow(member: member) }
                    }
                } header: {
                    Text(tr("\(Num.format(results.count)) نتيجة", "\(Num.format(results.count)) results"))
                }
            }
            .listStyle(.insetGrouped)
            .scrollDismissesKeyboard(.immediately)
        }
    }
}

struct StaffRow: View {
    let member: StaffMember

    var body: some View {
        HStack(spacing: 12) {
            NameAvatar(name: member.name, size: 40)
            VStack(alignment: .leading, spacing: 2) {
                Text(member.name)
                    .font(.body.weight(.medium))
                    .lineLimit(2)
                Text([member.rank, member.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            if !member.status.isEmpty {
                StatusChip(status: member.status)
            }
        }
        .padding(.vertical, 2)
    }
}

struct StatusChip: View {
    let status: String

    var body: some View {
        let active = status.contains("مستمر")
        Text(status)
            .font(.caption.weight(.medium))
            .lineLimit(1)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .foregroundStyle(active ? Color.green : Color.orange)
            .background((active ? Color.green : Color.orange).opacity(0.15), in: Capsule())
    }
}

// MARK: - صفحة الموظف

/// أقسام معلومات الملاك داخل List (تُستخدم في صفحة الملاك وصفحة البحث الموحّد).
struct StaffSectionsContent: View {
    let sections: [StaffSection]

    var body: some View {
        ForEach(sections, id: \.title) { section in
            Section(section.title) {
                ForEach(Array(section.fields.enumerated()), id: \.offset) { _, field in
                    LabeledContent {
                        Text(Num.digits(field.value))
                            .multilineTextAlignment(.trailing)
                    } label: {
                        Text(field.label)
                    }
                    .contextMenu {
                        Button(tr("نسخ القيمة", "Copy value"), systemImage: "doc.on.doc") { copyToClipboard(field.value) }
                        Button(tr("نسخ مع العنوان", "Copy with label"), systemImage: "text.badge.plus") { copyToClipboard("\(field.label): \(field.value)") }
                    }
                }
            }
        }
    }
}

struct StaffDetailView: View {
    let member: StaffMember

    var body: some View {
        let sections = member.sections
        List {
            Section {
                header
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(Color.clear)
            }
            StaffSectionsContent(sections: sections)
            Section {
                Button {
                    ShortcutRouter.shared.pendingSearch = member.name
                    AppRouter.shared.tab = .search
                } label: {
                    Label(tr("عرض راتبه في الرواتب", "Show salary in Payroll"), systemImage: "banknote")
                        .frame(maxWidth: .infinity)
                }
            }
        }
        .listSectionSpacing(.compact)
        .navigationTitle(member.name)
        .navigationBarTitleDisplayMode(.inline)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(member.name)
                .font(.title3.bold())
            HStack(spacing: 8) {
                ForEach([member.rank, member.status, member.statNumber.isEmpty ? "" : tr("الرقم الإحصائي \(member.statNumber)", "Stat. no. \(member.statNumber)")]
                    .filter { !$0.isEmpty }, id: \.self) { chip in
                    Text(Num.digits(chip))
                        .font(.caption.weight(.medium))
                        .lineLimit(1)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(.white.opacity(0.2), in: Capsule())
                }
            }
            if !member.workplace.isEmpty {
                Text(member.workplace).font(.subheadline).opacity(0.85)
            }
        }
        .foregroundStyle(.white)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(20)
        .background(
            LinearGradient(colors: [Color(red: 0.11, green: 0.23, blue: 0.42), Color(red: 0.04, green: 0.4, blue: 0.91)],
                           startPoint: .topLeading, endPoint: .bottomTrailing),
            in: RoundedRectangle(cornerRadius: 26, style: .continuous)
        )
    }
}
