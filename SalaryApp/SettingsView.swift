import SwiftUI
import SwiftData

/// الإعدادات في صفحة واحدة بلا تمرير: الخيارات قوائم منسدلة، والتفاصيل في صفحات فرعية.
/// تبويب في الشريط السفلي.
struct SettingsView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @State private var importState = StaffImportState.shared
    @State private var importCenter = ImportCenter.shared
    @State private var backup = BackupManager.shared
    @State private var bookStore = OfficialBookStore.shared
    @Environment(\.modelContext) private var context
    @AppStorage(AppSettings.languageKey) private var language = AppLanguage.arabic.rawValue
    @AppStorage(AppSettings.appearanceKey) private var appearance = AppAppearance.system.rawValue
    @AppStorage(AppSettings.numeralsKey) private var numerals = AppNumerals.latin.rawValue
    @AppStorage(AppSettings.textSizeKey) private var textSize = -1
    @AppStorage(AppLock.enabledKey) private var lockEnabled = false
    @AppStorage(AppLock.timeoutKey) private var lockTimeout = 0
    @State private var lockMessage: String?
    @State private var storeSize: Int64?
    /// اختبار المحاكي: -openBackup YES يفتح صفحة النسخ الاحتياطي.
    @State private var showBackup = UserDefaults.standard.bool(forKey: "openBackup")
    @State private var showNotifications = UserDefaults.standard.bool(forKey: "openNotifications")
    @State private var showAccounts = UserDefaults.standard.bool(forKey: "openAccounts")
    @State private var showAbout = UserDefaults.standard.bool(forKey: "openAbout")
    @State private var showBooks = UserDefaults.standard.bool(forKey: "openBooks")

    @AppStorage(AuditNotifications.enabledKey) private var notifyOn = false

    var body: some View {
        NavigationStack {
            List {
                Section {
                    NavigationLink {
                        AboutView()
                    } label: {
                        SettingRow(title: tr("حول", "About"), systemImage: "info.circle.fill", color: .teal,
                                   subtitle: tr("معلومات التطبيق والمطوّر", "App and developer information")) { EmptyView() }
                    }
                    .accessibilityIdentifier("aboutRow")
                }

                Section(tr("العرض", "Display")) {
                    Picker(selection: $language) {
                        Text("العربية").tag(AppLanguage.arabic.rawValue)
                        Text("English").tag(AppLanguage.english.rawValue)
                    } label: {
                        SettingRow(title: tr("اللغة", "Language"), systemImage: "globe", color: .indigo) { EmptyView() }
                    }
                    Picker(selection: $appearance) {
                        Text(tr("نهاري", "Light")).tag(AppAppearance.light.rawValue)
                        Text(tr("ليلي", "Dark")).tag(AppAppearance.dark.rawValue)
                        Text(tr("النظام", "System")).tag(AppAppearance.system.rawValue)
                    } label: {
                        SettingRow(title: tr("المظهر", "Appearance"), systemImage: "moon.fill", color: Color(.darkGray)) { EmptyView() }
                    }
                    Picker(selection: $numerals) {
                        Text("123").tag(AppNumerals.latin.rawValue)
                        Text("١٢٣").tag(AppNumerals.arabicIndic.rawValue)
                    } label: {
                        SettingRow(title: tr("الأرقام", "Numerals"), systemImage: "number", color: .orange) { EmptyView() }
                    }
                    textSizeRow
                }

                Section {
                    Picker(selection: lockChoice) {
                        Text(tr("متوقف", "Off")).tag(-1)
                        Text(tr("فوراً", "Immediately")).tag(0)
                        Text(tr("بعد دقيقة", "After 1 minute")).tag(60)
                        Text(tr("بعد 5 دقائق", "After 5 minutes")).tag(300)
                        Text(tr("بعد 15 دقيقة", "After 15 minutes")).tag(900)
                    } label: {
                        SettingRow(title: tr("قفل \(AppLock.methodName)", "\(AppLock.methodName) lock"), systemImage: AppLock.systemImage, color: .green) { EmptyView() }
                    }
                    .accessibilityIdentifier("faceIDToggle")
                    NavigationLink {
                        NotificationSettingsView()
                    } label: {
                        SettingRow(title: tr("الإشعارات", "Notifications"), systemImage: "bell.badge.fill", color: .red,
                                   subtitle: notifyOn ? tr("مفعّلة بعد كل استيراد", "On after each import") : tr("متوقفة", "Off")) { EmptyView() }
                    }
                    .accessibilityIdentifier("notificationsRow")
                } header: {
                    Text(tr("الأمان والإشعارات", "Security and notifications"))
                } footer: {
                    if let lockMessage { Text(lockMessage).foregroundStyle(.red) }
                }

                Section {
                    Button {
                        importCenter.pick()
                    } label: {
                        SettingRow(title: tr("استيراد ملفات", "Import files"), systemImage: "square.and.arrow.down", color: .blue) {
                            if importCenter.isBusy { ProgressView() }
                        }
                    }
                    .accessibilityIdentifier("importFilesButton")
                    NavigationLink {
                        SavedDataView()
                    } label: {
                        SettingRow(title: tr("البيانات المحفوظة", "Saved data"), systemImage: "externaldrive.fill", color: .cyan,
                                   subtitle: dataSummary) { EmptyView() }
                    }
                    NavigationLink {
                        BackupView()
                    } label: {
                        SettingRow(title: tr("النسخ الاحتياطي", "Backup"), systemImage: "icloud.fill", color: .pink,
                                   subtitle: backup.statusLine, subtitleColor: backup.statusColor) { EmptyView() }
                    }
                    .accessibilityIdentifier("backupRow")
                } header: {
                    Text(tr("البيانات", "Data"))
                }

            }
            .listSectionSpacing(.compact)
            .navigationDestination(isPresented: $showBackup) { BackupView() }
            .navigationDestination(isPresented: $showNotifications) { NotificationSettingsView() }
            .navigationDestination(isPresented: $showAccounts) { ExecutionAccountsView() }
            .navigationDestination(isPresented: $showAbout) { AboutView() }
            .navigationDestination(isPresented: $showBooks) { OfficialBooksView() }
            .navigationTitle(tr("الإعدادات", "Settings"))
            // عنوان صغير حتى تتسع الصفحة كلها بلا تمرير.
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .principal) {
                    Text(tr("الإعدادات", "Settings")).font(.headline)
                }
            }
            .task(id: "\(months.count)-\(importState.version)-\(bookStore.revision)") {
                storeSize = await SavedDataView.storeSize(of: context.container)
            }
        }
        // يُعاد رسم اللوحة فوراً عند تغيير اللغة أو الأرقام.
        .id("\(language)-\(numerals)")
    }

    /// حجم النص: شريط بسبعة مستويات (الوسط = الحجم العادي)، والضغط على "مثل الآيفون" يعيد حجم النظام.
    private var textSizeRow: some View {
        HStack(spacing: 10) {
            Image(systemName: "textformat.size")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(.white)
                .frame(width: 30, height: 30)
                .background(Color.teal.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            Text("A").font(.system(size: 12, weight: .semibold))
            Slider(value: Binding(
                get: { Double(textSize < 0 ? AppSettings.defaultTextSizeIndex : textSize) },
                set: { textSize = Int($0.rounded()) }
            ), in: 0...Double(AppSettings.textSizes.count - 1), step: 1)
            .accessibilityLabel(tr("حجم النص", "Text size"))
            .accessibilityIdentifier("textSizeSlider")
            Text("A").font(.system(size: 20, weight: .semibold))
            Button {
                textSize = -1
            } label: {
                Text(textSize < 0 ? tr("الآيفون", "iPhone") : tr("إعادة", "Reset"))
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(textSize < 0 ? Color.secondary : Color.accentColor)
            }
            .buttonStyle(.borderless)
            .disabled(textSize < 0)
        }
        .padding(.vertical, 2)
        // الشريط نفسه لا يتغيّر حجمه مع تغيّر الحجم حتى لا يتحرك تحت الإصبع.
        .dynamicTypeSize(.large)
    }

    /// "3 أشهر · 42,108 ملاك · 48 ميغا"
    private var dataSummary: String {
        var parts = [tr("\(Num.format(months.count)) أشهر", "\(months.count) months")]
        if StaffInfo.count > 0 { parts.append(tr("\(Num.format(StaffInfo.count)) ملاك", "\(StaffInfo.count) staff")) }
        if let storeSize { parts.append(Num.bytes(storeSize)) }
        return parts.joined(separator: " · ")
    }

    /// قائمة القفل: "متوقف" أو وقت القفل. التفعيل يحتاج تحققاً ناجحاً أولاً.
    private var lockChoice: Binding<Int> {
        Binding(
            get: { lockEnabled ? lockTimeout : -1 },
            set: { value in
                if value < 0 {
                    setLock(false)
                } else if lockEnabled {
                    lockTimeout = value
                } else {
                    setLock(true) { lockTimeout = value }
                }
            }
        )
    }

    /// التفعيل يحتاج تحققاً ناجحاً أولاً، حتى لا يُقفل المستخدم بقفل لا يعمل على جهازه.
    private func setLock(_ on: Bool, then: (() -> Void)? = nil) {
        lockMessage = nil
        guard on else {
            lockEnabled = false
            AppLock.shared.disable()
            return
        }
        guard AppLock.kind != .none else {
            lockMessage = tr("فعّل رمز القفل أو Face ID في إعدادات الآيفون أولاً.", "Set up a passcode or Face ID in iPhone Settings first.")
            return
        }
        Task {
            switch await AppLock.authenticate(reason: tr("تفعيل قفل تطبيق الرواتب", "Turn on the payroll app lock")) {
            case .success: lockEnabled = true; then?()
            case .failure: lockMessage = tr("لم يُفعَّل القفل: لم يتم التحقق.", "Lock not turned on: verification failed.")
            }
        }
    }
}

/// البيانات المحفوظة: الأشهر، والملاك، ومساحة البيانات.
struct SavedDataView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()
    @State private var importState = StaffImportState.shared
    @State private var bookStore = OfficialBookStore.shared
    @State private var confirmStaffDelete = false
    @State private var storeSize: Int64?
    @State private var bookBytes: Int64 = 0
    @Environment(\.modelContext) private var context
    @State private var recentCount = RecentSearches.items.count
    @State private var confirmClearRecents = false

    var body: some View {
        Form {
            Section(tr("البحث", "Search")) {
                Menu {
                    Button(tr("مسح سجل البحث", "Clear search history"), systemImage: "trash", role: .destructive) {
                        confirmClearRecents = true
                    }
                    .disabled(recentCount == 0)
                } label: {
                    SettingRow(title: tr("سجل البحث", "Search history"), systemImage: "clock.arrow.circlepath", color: .gray) {
                        HStack(spacing: 4) {
                            Text(recentCount > 0 ? Num.format(recentCount) : tr("فارغ", "Empty"))
                            Image(systemName: "chevron.up.chevron.down").font(.caption2.weight(.semibold))
                        }
                        .foregroundStyle(.secondary)
                    }
                }
                .tint(.primary)
            }
            Section(tr("الرواتب والاستقطاعات", "Payroll and deductions")) {
                NavigationLink {
                    SavedMonthsView()
                } label: {
                    SettingRow(title: tr("الأشهر المحفوظة", "Saved months"), systemImage: "calendar", color: .blue) {
                        Text(Num.format(months.count)).foregroundStyle(.secondary)
                    }
                }
                NavigationLink {
                    ExecutionAccountsView()
                } label: {
                    SettingRow(title: tr("حسابات جهات التنفيذ", "Execution accounts"), systemImage: "building.columns.fill", color: .teal) {
                        Text(Num.format(((try? ExecutionAccountStore.decode(accountData)) ?? []).count)).foregroundStyle(.secondary)
                    }
                }
                NavigationLink {
                    OfficialBooksView()
                } label: {
                    SettingRow(title: tr("الأرشيف", "Archive"), systemImage: "doc.viewfinder", color: .indigo) {
                        Text(Num.format(OfficialBookStore.shared.books.count)).foregroundStyle(.secondary)
                    }
                }
            }
            staffSection
            storageSection
        }
        .onAppear { recentCount = RecentSearches.items.count }
        .confirmationDialog(tr("مسح سجل البحث؟", "Clear search history?"), isPresented: $confirmClearRecents, titleVisibility: .visible) {
            Button(tr("مسح", "Clear"), role: .destructive) {
                RecentSearches.clear()
                recentCount = 0
            }
        } message: {
            Text(tr("تُحذف قائمة آخر من بحثت عنهم. بيانات الرواتب والملاك لا تتأثر.",
                    "The recently viewed list is removed. Payroll and staff data are not affected."))
        }
        .navigationTitle(tr("البيانات المحفوظة", "Saved data"))
        .navigationBarTitleDisplayMode(.inline)
        .task(id: "\(months.count)-\(importState.version)-\(bookStore.revision)") {
            storeSize = await Self.storeSize(of: context.container)
            bookBytes = OfficialBookFiles.bytes()
        }
    }

    // MARK: - الملاك

    private var staffSection: some View {
        Section {
            if StaffInfo.count == 0 {
                Text(tr("لا توجد سجلات ملاك محفوظة. استورد ملف Access الأصلي أدناه.", "No staff records are saved. Import the original Access file below."))
                    .foregroundStyle(.secondary)
            } else {
                LabeledContent(tr("عدد الموظفين", "Employees")) {
                    Text(Num.format(StaffInfo.count))
                }
                LabeledContent(tr("الملف", "File")) {
                    Text(StaffInfo.fileName).lineLimit(1)
                }
                if let date = StaffInfo.importedAt {
                    LabeledContent(tr("تاريخ الاستيراد", "Imported")) {
                        Text(date.formatted(.dateTime.year().month().day().locale(AppSettings.locale)))
                    }
                }
                Button(tr("حذف بيانات الملاك", "Delete staff data"), role: .destructive) {
                    confirmStaffDelete = true
                }
            }
            Button {
                ImportCenter.shared.pickStaff()
            } label: {
                Label(StaffInfo.count == 0 ? tr("استيراد ملف الملاك", "Import staff database")
                                           : tr("إعادة استيراد الملاك", "Reimport staff database"),
                      systemImage: "square.and.arrow.down")
            }
        } header: {
            Text(tr("الملاك", "Staff"))
        } footer: {
            Text(tr("اختر ملف Access الأصلي (‎.accdb أو ‎.mdb)، وليس كشف الرواتب. الاستيراد الجديد يستبدل الملاك القديم بعد نجاح القراءة فقط.",
                    "Choose the original Access file (.accdb or .mdb), not a payroll sheet. The old staff data is replaced only after a successful import."))
        }
        .id(importState.version)
        .confirmationDialog(tr("حذف بيانات الملاك؟", "Delete staff data?"), isPresented: $confirmStaffDelete, titleVisibility: .visible) {
            Button(tr("حذف", "Delete"), role: .destructive) { importState.deleteAll() }
        }
    }

    // MARK: - مساحة البيانات

    private var storageSection: some View {
        Section {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .firstTextBaseline) {
                    Text(tr("المستخدم", "Used"))
                    Spacer()
                    if let storeSize {
                        Text(Num.bytes(storeSize))
                            .font(.title2.weight(.bold).monospacedDigit())
                    } else {
                        ProgressView()
                    }
                }

                StorageBar(segments: segments)

                ForEach(segments) { segment in
                    HStack(spacing: 10) {
                        Circle().fill(segment.color).frame(width: 10, height: 10)
                        Text(segment.name)
                        Spacer()
                        Text(Num.bytes(segment.bytes))
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                    }
                    .font(.subheadline)
                }
            }
            .padding(.vertical, 6)
        } header: {
            Text(tr("مساحة البيانات", "Storage"))
        } footer: {
            Text(tr("حجم كل شهر تقريبي حسب عدد سجلاته.", "Each month's size is estimated from its number of records."))
        }
    }

    /// حجم كل شهر = الحجم الكلي موزعاً حسب عدد السجلات (موظفون + استقطاعات).
    private var segments: [StorageSegment] {
        guard let storeSize else { return [] }
        let palette: [Color] = [.blue, .indigo, .cyan, .teal, .purple, .mint, .orange, .pink]
        // سجل الملاك أكبر بكثير من سجل الراتب (فيه كل الحقول)، فوزنه تقريبي ×8.
        var parts = months.map { (name: $0.name, weight: max($0.employeeCount + $0.deductionCount, 1)) }
        if StaffInfo.count > 0 { parts.append((name: tr("الملاك", "Staff"), weight: StaffInfo.count * 8)) }
        let total = Double(max(parts.reduce(0) { $0 + $1.weight }, 1))
        var result = parts.enumerated().map { i, part in
            StorageSegment(
                name: part.name,
                bytes: Int64(Double(max(storeSize - bookBytes, 0)) * Double(part.weight) / total),
                color: i == months.count ? .teal : palette[i % palette.count]
            )
        }
        if bookBytes > 0 {
            result.append(StorageSegment(name: tr("الأرشيف", "Archive"), bytes: bookBytes, color: .brown))
        }
        return result
    }

    /// حجم ملفات قاعدة البيانات على الجهاز (الملف الأساسي وملفات -wal و-shm).
    static func storeSize(of container: ModelContainer) async -> Int64 {
        guard let url = container.configurations.first?.url else { return 0 }
        return await Task.detached {
            let fm = FileManager.default
            let dir = url.deletingLastPathComponent()
            let base = url.lastPathComponent
            let files = (try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.fileSizeKey])) ?? []
            let databaseBytes = files
                .filter { $0.lastPathComponent.hasPrefix(base) }
                .reduce(Int64(0)) { sum, file in
                    sum + Int64((try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
                }
            return databaseBytes + OfficialBookFiles.bytes()
        }.value
    }
}

struct StorageSegment: Identifiable {
    let name: String
    let bytes: Int64
    let color: Color
    var id: String { name }
}

/// شريط أفقي مقسّم بألوان الأشهر حسب حجم كل شهر.
private struct StorageBar: View {
    let segments: [StorageSegment]

    var body: some View {
        GeometryReader { geo in
            let total = max(segments.reduce(Int64(0)) { $0 + $1.bytes }, 1)
            let spacing: CGFloat = 2
            let usable = geo.size.width - spacing * CGFloat(max(segments.count - 1, 0))
            HStack(spacing: spacing) {
                ForEach(segments) { segment in
                    segment.color
                        .frame(width: max(usable * CGFloat(segment.bytes) / CGFloat(total), 2))
                }
            }
        }
        .frame(height: 14)
        .background(Color(.tertiarySystemFill))
        .clipShape(Capsule())
    }
}

/// صف إعداد: أيقونة ملوّنة + عنوان (وسطر وصف اختياري) + عنصر تحكم في الطرف الآخر.
struct SettingRow<Trailing: View>: View {
    let title: String
    let systemImage: String
    let color: Color
    var subtitle: String? = nil
    var subtitleColor: Color = .secondary
    @ViewBuilder let trailing: () -> Trailing

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: systemImage)
                .font(.footnote.weight(.semibold))
                .foregroundStyle(.white)
                .frame(width: 30, height: 30)
                .background(color.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .foregroundStyle(.primary)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                if let subtitle, !subtitle.isEmpty {
                    Text(subtitle).font(.caption).foregroundStyle(subtitleColor).lineLimit(1)
                }
            }
            .layoutPriority(1)
            Spacer(minLength: 8)
            trailing()
        }
    }
}

/// الأشهر المحفوظة مع عدد السجلات، والحذف بالسحب.
private struct SavedMonthsView: View {
    @Environment(\.modelContext) private var context
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @State private var monthToDelete: PayrollMonth?

    var body: some View {
        List {
            ForEach(months) { month in
                VStack(alignment: .leading, spacing: 3) {
                    Text(month.name).font(.body.weight(.medium))
                    Text(tr("\(Num.format(month.employeeCount)) موظف · \(Num.format(month.deductionCount)) استقطاع",
                            "\(Num.format(month.employeeCount)) employees · \(Num.format(month.deductionCount)) deductions"))
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                .swipeActions {
                    Button(tr("حذف", "Delete"), systemImage: "trash", role: .destructive) {
                        monthToDelete = month
                    }
                }
                .contextMenu {
                    Button(tr("حذف الشهر", "Delete month"), systemImage: "trash", role: .destructive) { monthToDelete = month }
                }
            }
        }
        .overlay {
            if months.isEmpty {
                ContentUnavailableView(tr("لا توجد أشهر", "No months"), systemImage: "calendar")
            }
        }
        .navigationTitle(tr("الأشهر المحفوظة", "Saved months"))
        .confirmationDialog(
            tr("حذف بيانات \(monthToDelete?.name ?? "")؟", "Delete \(monthToDelete?.name ?? "")?"),
            isPresented: Binding(get: { monthToDelete != nil }, set: { if !$0 { monthToDelete = nil } }),
            titleVisibility: .visible
        ) {
            Button(tr("حذف", "Delete"), role: .destructive) {
                guard let name = monthToDelete?.name else { return }
                let importer = DataImporter(modelContainer: context.container)
                BackupManager.shared.markChanged()
                Task { try? await importer.delete(month: name) }
            }
        }
    }
}

/// رقم الإصدار من ملف التطبيق: "الإصدار 1.34 (بناء 34)".
/// البناء على GitHub يضع رقم البناء في الإصدار تلقائياً (build.yml).
enum AppVersion {
    static var version: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0" }
    static var build: String { Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "1" }
    static var label: String { tr("الإصدار \(version) (بناء \(build))", "Version \(version) (build \(build))") }
}
