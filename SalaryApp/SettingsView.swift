import SwiftUI
import SwiftData

/// لوحة الإعدادات: اللغة والمظهر والأرقام، والاستيراد، ومساحة البيانات.
struct SettingsView: View {
    let months: [PayrollMonth]
    /// يُستدعى عند الضغط على "استيراد ملفات Excel": تُغلق الإعدادات وتُفتح نافذة الملفات.
    let onImport: () -> Void
    /// "استيراد ملف الملاك": تُغلق الإعدادات وتُفتح نافذة اختيار ملف Access.
    var onImportStaff: () -> Void = {}
    @State private var importState = StaffImportState.shared
    @State private var confirmStaffDelete = false

    @Environment(\.modelContext) private var context
    @Environment(\.dismiss) private var dismiss
    @AppStorage(AppSettings.languageKey) private var language = AppLanguage.arabic.rawValue
    @AppStorage(AppSettings.appearanceKey) private var appearance = AppAppearance.system.rawValue
    @AppStorage(AppSettings.numeralsKey) private var numerals = AppNumerals.latin.rawValue
    @State private var storeSize: Int64?
    @AppStorage(AppLock.enabledKey) private var lockEnabled = false
    @AppStorage(AppLock.timeoutKey) private var lockTimeout = 0
    @State private var lockMessage: String?
    @State private var recentCount = RecentSearches.items.count
    @AppStorage(ReportSheet.headerKey) private var reportHeader = ReportSheet.defaultHeader
    @State private var confirmClearRecents = false

    var body: some View {
        NavigationStack {
            Form {
                displaySection
                securitySection
                searchSection
                reportsSection
                importSection
                staffSection
                storageSection
            }
            .navigationTitle(tr("الإعدادات", "Settings"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button(tr("تم", "Done")) { dismiss() }
                }
            }
            .task(id: "\(months.count)-\(importState.version)") { storeSize = await Self.storeSize(of: context.container) }
        }
        // يُعاد رسم اللوحة فوراً عند تغيير اللغة أو الأرقام.
        .id("\(language)-\(numerals)")
    }

    // MARK: - الأمان

    private var securitySection: some View {
        Section {
            SettingRow(title: tr("القفل بـ \(AppLock.methodName)", "Lock with \(AppLock.methodName)"),
                       systemImage: AppLock.systemImage, color: .green) {
                Toggle("", isOn: Binding(get: { lockEnabled }, set: { setLock($0) }))
                    .labelsHidden()
                    .accessibilityIdentifier("faceIDToggle")
            }
            if lockEnabled {
                Picker(selection: $lockTimeout) {
                    Text(tr("فوراً", "Immediately")).tag(0)
                    Text(tr("بعد دقيقة", "After 1 minute")).tag(60)
                    Text(tr("بعد 5 دقائق", "After 5 minutes")).tag(300)
                    Text(tr("بعد 15 دقيقة", "After 15 minutes")).tag(900)
                } label: {
                    SettingRow(title: tr("القفل عند الخروج", "Lock when leaving"), systemImage: "timer", color: .teal) { EmptyView() }
                }
                .pickerStyle(.menu)
            }
        } header: {
            Text(tr("الأمان", "Security"))
        } footer: {
            if let lockMessage {
                Text(lockMessage).foregroundStyle(.red)
            } else {
                Text(tr("يُطلب \(AppLock.methodName) عند فتح التطبيق وعند الرجوع إليه، ويُستخدم رمز الجهاز إذا تعذّر التعرّف. البيانات تُخفى في قائمة التطبيقات المفتوحة.",
                        "\(AppLock.methodName) is required when opening or returning to the app; the device passcode is used if recognition fails. Data is hidden in the app switcher."))
            }
        }
    }

    // MARK: - الكشوف

    private var reportsSection: some View {
        Section {
            VStack(alignment: .leading, spacing: 8) {
                SettingRow(title: tr("رأس الكشوف", "Report header"), systemImage: "doc.text", color: .indigo) { EmptyView() }
                TextField(tr("اسم الجهة", "Organisation name"), text: $reportHeader)
                    .textFieldStyle(.roundedBorder)
            }
            .padding(.vertical, 2)
        } header: {
            Text(tr("الكشوف المصدّرة", "Exported lists"))
        } footer: {
            Text(tr("يظهر في أعلى كل كشف Excel أو PDF.", "Shown at the top of every exported Excel or PDF list."))
        }
    }

    // MARK: - سجل البحث

    private var searchSection: some View {
        Section {
            Button(role: .destructive) {
                confirmClearRecents = true
            } label: {
                SettingRow(title: tr("مسح سجل البحث", "Clear search history"), systemImage: "clock.arrow.circlepath", color: .red) {
                    Text(recentCount > 0 ? Num.format(recentCount) : tr("فارغ", "Empty"))
                        .foregroundStyle(.secondary)
                }
            }
            .disabled(recentCount == 0)
            .confirmationDialog(tr("مسح سجل البحث؟", "Clear search history?"), isPresented: $confirmClearRecents, titleVisibility: .visible) {
                Button(tr("مسح", "Clear"), role: .destructive) {
                    RecentSearches.clear()
                    recentCount = 0
                }
            } message: {
                Text(tr("تُحذف قائمة آخر من بحثت عنهم من الشاشة الرئيسية وصفحة البحث. بيانات الرواتب والملاك لا تتأثر.",
                        "The recently viewed list is removed from Home and Search. Payroll and staff data are not affected."))
            }
        } header: {
            Text(tr("البحث", "Search"))
        }
    }

    /// التفعيل يحتاج تحققاً ناجحاً أولاً، حتى لا يُقفل المستخدم بقفل لا يعمل على جهازه.
    private func setLock(_ on: Bool) {
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
            case .success: lockEnabled = true
            case .failure: lockMessage = tr("لم يُفعَّل القفل: لم يتم التحقق.", "Lock not turned on: verification failed.")
            }
        }
    }

    // MARK: - العرض

    private var displaySection: some View {
        Section {
            // العنوان في سطر وأزرار الاختيار تحته بعرض كامل، حتى لا ينكسر العنوان مع الخط الكبير.
            VStack(alignment: .leading, spacing: 10) {
                SettingRow(title: tr("اللغة", "Language"), systemImage: "globe", color: .indigo) { EmptyView() }
                Picker(tr("اللغة", "Language"), selection: $language) {
                    Text("العربية").tag(AppLanguage.arabic.rawValue)
                    Text("English").tag(AppLanguage.english.rawValue)
                }
                .pickerStyle(.segmented)
            }
            .padding(.vertical, 2)

            VStack(alignment: .leading, spacing: 10) {
                SettingRow(title: tr("المظهر", "Appearance"), systemImage: "moon.fill", color: Color(.darkGray)) { EmptyView() }
                Picker(tr("المظهر", "Appearance"), selection: $appearance) {
                    Text(tr("نهاري", "Light")).tag(AppAppearance.light.rawValue)
                    Text(tr("ليلي", "Dark")).tag(AppAppearance.dark.rawValue)
                    Text(tr("النظام", "System")).tag(AppAppearance.system.rawValue)
                }
                .pickerStyle(.segmented)
            }
            .padding(.vertical, 2)

            VStack(alignment: .leading, spacing: 10) {
                SettingRow(title: tr("الأرقام", "Numerals"), systemImage: "number", color: .orange) { EmptyView() }
                Picker(tr("الأرقام", "Numerals"), selection: $numerals) {
                    Text("123").tag(AppNumerals.latin.rawValue)
                    Text("١٢٣").tag(AppNumerals.arabicIndic.rawValue)
                }
                .pickerStyle(.segmented)
                LabeledContent(tr("مثال", "Example")) {
                    Text(Money.format(1_250_000))
                        .font(.body.weight(.semibold).monospacedDigit())
                        .foregroundStyle(.primary)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(Color(.tertiarySystemFill), in: RoundedRectangle(cornerRadius: 10))
            }
            .padding(.vertical, 2)
        } header: {
            Text(tr("العرض", "Display"))
        } footer: {
            Text(tr("الأرقام العربية: 123 — الأرقام الهندية: ١٢٣", "Western digits: 123 — Eastern Arabic digits: ١٢٣"))
        }
    }

    // MARK: - الاستيراد

    private var importSection: some View {
        Section {
            Button {
                onImport()
            } label: {
                SettingRow(title: tr("استيراد ملفات", "Import files"), systemImage: "square.and.arrow.down", color: .green) {
                    if ImportCenter.shared.isBusy { ProgressView() }
                }
            }
            .accessibilityIdentifier("importFilesButton")
            NavigationLink {
                SavedMonthsView()
            } label: {
                SettingRow(title: tr("الأشهر المحفوظة", "Saved months"), systemImage: "calendar", color: .blue) {
                    Text(Num.format(months.count)).foregroundStyle(.secondary)
                }
            }
        } header: {
            Text(tr("الاستيراد", "Import"))
        } footer: {
            Text(tr("رواتب أو استقطاعات (Excel) أو ملاك (Access)، ملف واحد أو عدة ملفات معاً. التطبيق يتعرّف على نوع كل ملف وشهره وتراجعه قبل الاستيراد. ملف الرواتب يستبدل رواتب شهره فقط، وملف الاستقطاعات يستبدل استقطاعاته فقط.",
                    "Payroll or deductions (Excel) or staff (Access), one or several files. The app recognises each file's type and month for you to review. A payroll file replaces only that month's payroll; a deductions file only its deductions."))
        }
    }

    // MARK: - الملاك

    private var staffSection: some View {
        Section {
            if StaffInfo.count == 0 {
                Text(tr("لم يُستورد ملف الملاك بعد. استورده من \"استيراد ملفات\".", "No staff file yet. Import it from \"Import files\"."))
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
        } header: {
            Text(tr("الملاك", "Staff"))
        } footer: {
            Text(tr("ملف قاعدة البيانات (‎.accdb) كما هو. الاستيراد الجديد يستبدل الملاك القديم كاملاً.",
                    "The database file (.accdb) as it is. A new import replaces the previous staff data."))
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
        guard let storeSize, !months.isEmpty || StaffInfo.count > 0 else { return [] }
        let palette: [Color] = [.blue, .indigo, .cyan, .teal, .purple, .mint, .orange, .pink]
        // سجل الملاك أكبر بكثير من سجل الراتب (فيه كل الحقول)، فوزنه تقريبي ×8.
        var parts = months.map { (name: $0.name, weight: max($0.employeeCount + $0.deductionCount, 1)) }
        if StaffInfo.count > 0 { parts.append((name: tr("الملاك", "Staff"), weight: StaffInfo.count * 8)) }
        let total = Double(parts.reduce(0) { $0 + $1.weight })
        return parts.enumerated().map { i, part in
            StorageSegment(
                name: part.name,
                bytes: Int64(Double(storeSize) * Double(part.weight) / total),
                color: i == months.count ? .teal : palette[i % palette.count]
            )
        }
    }

    /// حجم ملفات قاعدة البيانات على الجهاز (الملف الأساسي وملفات -wal و-shm).
    private static func storeSize(of container: ModelContainer) async -> Int64 {
        guard let url = container.configurations.first?.url else { return 0 }
        return await Task.detached {
            let fm = FileManager.default
            let dir = url.deletingLastPathComponent()
            let base = url.lastPathComponent
            let files = (try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.fileSizeKey])) ?? []
            return files
                .filter { $0.lastPathComponent.hasPrefix(base) }
                .reduce(Int64(0)) { sum, file in
                    sum + Int64((try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
                }
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

/// صف إعداد: أيقونة ملوّنة + عنوان + عنصر تحكم في الطرف الآخر.
private struct SettingRow<Trailing: View>: View {
    let title: String
    let systemImage: String
    let color: Color
    @ViewBuilder let trailing: () -> Trailing

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: systemImage)
                .font(.footnote.weight(.semibold))
                .foregroundStyle(.white)
                .frame(width: 30, height: 30)
                .background(color.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            Text(title)
                .foregroundStyle(.primary)
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
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
                Task { try? await importer.delete(month: name) }
            }
        }
    }
}
