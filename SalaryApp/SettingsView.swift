import SwiftUI
import SwiftData

/// لوحة الإعدادات: اللغة والمظهر والأرقام، والاستيراد، ومساحة البيانات.
struct SettingsView: View {
    let months: [PayrollMonth]
    /// يُستدعى عند الضغط على "استيراد ملفات Excel": تُغلق الإعدادات وتُفتح نافذة الملفات.
    let onImport: () -> Void

    @Environment(\.modelContext) private var context
    @Environment(\.dismiss) private var dismiss
    @AppStorage(AppSettings.languageKey) private var language = AppLanguage.arabic.rawValue
    @AppStorage(AppSettings.appearanceKey) private var appearance = AppAppearance.system.rawValue
    @AppStorage(AppSettings.numeralsKey) private var numerals = AppNumerals.latin.rawValue
    @State private var storeSize: Int64?

    var body: some View {
        NavigationStack {
            Form {
                displaySection
                importSection
                storageSection
            }
            .navigationTitle(tr("الإعدادات", "Settings"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button(tr("تم", "Done")) { dismiss() }
                }
            }
            .task(id: months.count) { storeSize = await Self.storeSize(of: context.container) }
        }
        // يُعاد رسم اللوحة فوراً عند تغيير اللغة أو الأرقام.
        .id("\(language)-\(numerals)")
    }

    // MARK: - العرض

    private var displaySection: some View {
        Section {
            SettingRow(title: tr("اللغة", "Language"), systemImage: "globe", color: .indigo) {
                Picker(tr("اللغة", "Language"), selection: $language) {
                    Text("العربية").tag(AppLanguage.arabic.rawValue)
                    Text("English").tag(AppLanguage.english.rawValue)
                }
                .pickerStyle(.segmented)
                .frame(width: 170)
            }

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
                SettingRow(title: tr("الأرقام", "Numerals"), systemImage: "number", color: .orange) {
                    Picker(tr("الأرقام", "Numerals"), selection: $numerals) {
                        Text("123").tag(AppNumerals.latin.rawValue)
                        Text("١٢٣").tag(AppNumerals.arabicIndic.rawValue)
                    }
                    .pickerStyle(.segmented)
                    .frame(width: 170)
                }
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
                SettingRow(title: tr("استيراد ملفات Excel", "Import Excel files"), systemImage: "square.and.arrow.down", color: .green) { EmptyView() }
            }
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
            Text(tr("كل ملف يُحفظ كشهر باسم الملف. استيراد نفس الشهر مرة أخرى يستبدل بياناته.",
                    "Each file is saved as a month named after the file. Importing the same month again replaces its data."))
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
        guard let storeSize, !months.isEmpty else { return [] }
        let palette: [Color] = [.blue, .indigo, .cyan, .teal, .purple, .mint, .orange, .pink]
        let records = months.map { max($0.employeeCount + $0.deductionCount, 1) }
        let total = Double(records.reduce(0, +))
        return months.enumerated().map { i, month in
            StorageSegment(
                name: month.name,
                bytes: Int64(Double(storeSize) * Double(records[i]) / total),
                color: palette[i % palette.count]
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
