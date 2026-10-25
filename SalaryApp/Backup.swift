import SwiftUI
import SwiftData
import UIKit
import UniformTypeIdentifiers
import ZIPFoundation

/// النسخ الاحتياطي: ملف zip واحد فيه كل البيانات (الأشهر، الرواتب، الاستقطاعات، الملاك) والإعدادات.
/// يُحفظ في أي مكان من نافذة "الملفات" (iCloud Drive، OneDrive، Google Drive)،
/// ونسخ تلقائي بعد كل استيراد إلى مجلد يُختار مرة واحدة. نسخة واحدة فقط باسم ثابت: الجديدة تستبدل السابقة.
nonisolated enum BackupFormat {
    static let version = 1
    static let prefix = "نسخة الرواتب"

    struct Manifest: Codable, Sendable {
        var version: Int
        var createdAt: Date
        var months: [String]
        var employees: Int
        var deductions: Int
        var staff: Int
    }

    struct MonthRecord: Codable { var name: String; var importedAt: Date; var employeeCount: Int; var deductionCount: Int; var summary: Data? }
    struct EmployeeRecord: Codable { var month: String; var name: String; var rank: String; var workplace: String; var net: Double?; var items: Data? }
    struct DeductionRecord: Codable { var month: String; var name: String; var title: String; var amount: Double?; var note: String; var rank: String }
    struct StaffRecord: Codable { var name: String; var mother: String; var stat: String; var unified: String; var rank: String; var status: String; var workplace: String; var details: Data }

    /// إعدادات تُحفظ مع البيانات (بدون قفل Face ID).
    static let settingsKeys = ["staffCount", "staffImportedAt", "staffFileName", "staffStatuses", "unifiedRecentSearches",
                               ReportSheet.headerKey, AppSettings.languageKey, AppSettings.appearanceKey, AppSettings.numeralsKey]

    /// اسم ثابت: كل نسخة جديدة تحلّ محل السابقة (تاريخ النسخة محفوظ داخلها).
    static func fileName() -> String { "\(prefix).zip" }
}

enum BackupError: LocalizedError {
    case notBackup, newerVersion, folderUnavailable
    var errorDescription: String? {
        switch self {
        case .notBackup: tr("الملف ليس نسخة احتياطية من تطبيق الرواتب.", "This file isn't a payroll app backup.")
        case .newerVersion: tr("النسخة من إصدار أحدث من التطبيق. حدّث التطبيق ثم أعد المحاولة.", "The backup is from a newer app version. Update the app first.")
        case .folderUnavailable: tr("تعذّر الوصول إلى مجلد النسخ التلقائي. اختر المجلد من جديد في الإعدادات.", "The auto-backup folder can't be reached. Choose it again in Settings.")
        }
    }
}

/// القراءة والكتابة في الخلفية.
@ModelActor
actor BackupActor {
    /// يكتب ملف النسخة (zip) ويعيد رابطه ووصفه.
    func export(settings: [String: Data], progress: ProgressSink?) throws -> (URL, BackupFormat.Manifest) {
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("backup-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: dir) }
        let encoder = JSONEncoder()

        func writeLines<T: Encodable>(_ name: String, _ items: [T], from: Double, to: Double) throws {
            let url = dir.appendingPathComponent(name)
            fm.createFile(atPath: url.path, contents: nil)
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            var buffer = Data()
            for (i, item) in items.enumerated() {
                buffer.append(try encoder.encode(item))
                buffer.append(0x0A)
                if buffer.count > 1_000_000 { handle.write(buffer); buffer.removeAll(keepingCapacity: true) }
                if i % 2000 == 0 { progress?(from + (to - from) * Double(i) / Double(max(1, items.count))) }
            }
            handle.write(buffer)
        }

        let months = try modelContext.fetch(FetchDescriptor<PayrollMonth>())
        try writeLines("months.jsonl", months.map {
            BackupFormat.MonthRecord(name: $0.name, importedAt: $0.importedAt, employeeCount: $0.employeeCount,
                                     deductionCount: $0.deductionCount, summary: $0.itemSummaryData)
        }, from: 0, to: 0.02)
        let employees = try modelContext.fetch(FetchDescriptor<Employee>())
        try writeLines("employees.jsonl", employees.map {
            BackupFormat.EmployeeRecord(month: $0.month, name: $0.name, rank: $0.rank, workplace: $0.workplace, net: $0.netSalary, items: $0.itemsData)
        }, from: 0.02, to: 0.4)
        let employeeCount = employees.count
        let deductions = try modelContext.fetch(FetchDescriptor<Deduction>())
        try writeLines("deductions.jsonl", deductions.map {
            BackupFormat.DeductionRecord(month: $0.month, name: $0.employeeName, title: $0.title, amount: $0.amount, note: $0.note, rank: $0.rank)
        }, from: 0.4, to: 0.5)
        let deductionCount = deductions.count
        let staff = try modelContext.fetch(FetchDescriptor<StaffMember>())
        try writeLines("staff.jsonl", staff.map {
            BackupFormat.StaffRecord(name: $0.name, mother: $0.motherKey, stat: $0.statNumber, unified: $0.unifiedNumber,
                                     rank: $0.rank, status: $0.status, workplace: $0.workplace, details: $0.detailsData)
        }, from: 0.5, to: 0.8)

        let manifest = BackupFormat.Manifest(version: BackupFormat.version, createdAt: .now,
                                             months: months.sorted { $0.sortKey > $1.sortKey }.map(\.name),
                                             employees: employeeCount, deductions: deductionCount, staff: staff.count)
        try JSONEncoder().encode(manifest).write(to: dir.appendingPathComponent("manifest.json"))
        try JSONEncoder().encode(settings).write(to: dir.appendingPathComponent("settings.json"))

        let out = fm.temporaryDirectory.appendingPathComponent(BackupFormat.fileName())
        try? fm.removeItem(at: out)
        let archive = try Archive(url: out, accessMode: .create)
        for name in ["manifest.json", "settings.json", "months.jsonl", "employees.jsonl", "deductions.jsonl", "staff.jsonl"] {
            try archive.addEntry(with: name, relativeTo: dir, compressionMethod: .deflate)
        }
        progress?(1)
        return (out, manifest)
    }

    /// يستبدل كل البيانات بمحتوى النسخة. يعيد الإعدادات المحفوظة فيها.
    func restore(from folder: URL, progress: ProgressSink?) throws -> [String: Data] {
        let decoder = JSONDecoder()
        func lines(_ name: String) throws -> [Data] {
            let data = try Data(contentsOf: folder.appendingPathComponent(name), options: .alwaysMapped)
            return data.split(separator: 0x0A).map { Data($0) }
        }
        try modelContext.delete(model: Employee.self)
        try modelContext.delete(model: Deduction.self)
        try modelContext.delete(model: PayrollMonth.self)
        try modelContext.delete(model: StaffMember.self)
        try modelContext.save()

        var pending = 0
        func flush() throws {
            pending += 1
            if pending >= 3000 { try modelContext.save(); pending = 0 }
        }
        for line in try lines("months.jsonl") {
            let r = try decoder.decode(BackupFormat.MonthRecord.self, from: line)
            let m = PayrollMonth(name: r.name, employeeCount: r.employeeCount, deductionCount: r.deductionCount)
            m.importedAt = r.importedAt
            m.itemSummaryData = r.summary
            modelContext.insert(m)
        }
        let employees = try lines("employees.jsonl")
        for (i, line) in employees.enumerated() {
            let r = try decoder.decode(BackupFormat.EmployeeRecord.self, from: line)
            let e = Employee(month: r.month, name: r.name, rank: r.rank, workplace: r.workplace, netSalary: r.net)
            e.itemsData = r.items
            modelContext.insert(e)
            try flush()
            if i % 1000 == 0 { progress?(0.6 * Double(i) / Double(max(1, employees.count))) }
        }
        let deductions = try lines("deductions.jsonl")
        for (i, line) in deductions.enumerated() {
            let r = try decoder.decode(BackupFormat.DeductionRecord.self, from: line)
            modelContext.insert(Deduction(month: r.month, employeeName: r.name, title: r.title, amount: r.amount, note: r.note, rank: r.rank))
            try flush()
            if i % 1000 == 0 { progress?(0.6 + 0.1 * Double(i) / Double(max(1, deductions.count))) }
        }
        let staff = try lines("staff.jsonl")
        for (i, line) in staff.enumerated() {
            let r = try decoder.decode(BackupFormat.StaffRecord.self, from: line)
            let s = StaffMember(name: r.name, mother: "", statNumber: r.stat, unifiedNumber: r.unified,
                                rank: r.rank, status: r.status, workplace: r.workplace, sections: [])
            s.motherKey = r.mother
            s.detailsData = r.details
            modelContext.insert(s)
            try flush()
            if i % 1000 == 0 { progress?(0.7 + 0.3 * Double(i) / Double(max(1, staff.count))) }
        }
        try modelContext.save()
        progress?(1)
        let settings = try? decoder.decode([String: Data].self, from: Data(contentsOf: folder.appendingPathComponent("settings.json")))
        return settings ?? [:]
    }
}

@MainActor @Observable
final class BackupManager {
    static let shared = BackupManager()
    static let autoKey = "autoBackupEnabled"
    private static let folderKey = "autoBackupFolder"
    private static let lastKey = "autoBackupLast"
    private static let lastSizeKey = "autoBackupLastSize"

    var isWorking = false
    var message: String?
    /// نسخة تنتظر تأكيد الاسترجاع.
    var pendingRestore: (folder: URL, manifest: BackupFormat.Manifest)?
    var version = 0

    var folderName: String? { resolveFolder().map { FileManager.default.displayName(atPath: $0.path) } }
    var lastBackup: Date? { UserDefaults.standard.object(forKey: Self.lastKey) as? Date }
    var lastSize: Int64 { Int64(UserDefaults.standard.integer(forKey: Self.lastSizeKey)) }
    static var autoEnabled: Bool { UserDefaults.standard.bool(forKey: autoKey) }

    // MARK: - المجلد

    func chooseFolder(then: (() -> Void)? = nil) {
        FilePicker.presentFolder { [weak self] url in
            guard let self, let url else { return }
            let granted = url.startAccessingSecurityScopedResource()
            defer { if granted { url.stopAccessingSecurityScopedResource() } }
            if let data = try? url.bookmarkData(options: [], includingResourceValuesForKeys: nil, relativeTo: nil) {
                UserDefaults.standard.set(data, forKey: Self.folderKey)
                self.version += 1
                then?()
            } else {
                self.message = BackupError.folderUnavailable.localizedDescription
            }
        }
    }

    private func resolveFolder() -> URL? {
        guard let data = UserDefaults.standard.data(forKey: Self.folderKey) else { return nil }
        var stale = false
        guard let url = try? URL(resolvingBookmarkData: data, options: [], relativeTo: nil, bookmarkDataIsStale: &stale) else { return nil }
        if stale, let fresh = try? url.bookmarkData(options: [], includingResourceValuesForKeys: nil, relativeTo: nil) {
            UserDefaults.standard.set(fresh, forKey: Self.folderKey)
        }
        return url
    }

    // MARK: - إنشاء نسخة

    private func makeBackup(title: String) async throws -> (URL, BackupFormat.Manifest) {
        let settings = BackupFormat.settingsKeys.reduce(into: [String: Data]()) { dict, key in
            if let value = UserDefaults.standard.object(forKey: key),
               let data = try? PropertyListSerialization.data(fromPropertyList: value, format: .binary, options: 0) {
                dict[key] = data
            }
        }
        let progress = ImportProgress.shared
        progress.begin(title)
        defer { progress.end() }
        let sink = progress.sink(0, 1, tr("تجهيز النسخة", "Preparing backup"))
        let container = SharedModel.container
        return try await Task.detached(priority: .userInitiated) {
            try await BackupActor(modelContainer: container).export(settings: settings, progress: sink)
        }.value
    }

    /// نسخة يدوية: تُحفظ حيث يختار المستخدم.
    func backupNow() {
        guard !isWorking else { return }
        isWorking = true
        Task {
            defer { isWorking = false }
            do {
                let (url, _) = try await makeBackup(title: tr("نسخة احتياطية", "Backup"))
                await FilePicker.waitUntilSettled()
                FilePicker.presentExport(url) { [weak self] saved in
                    try? FileManager.default.removeItem(at: url)
                    if saved { self?.message = tr("تم حفظ النسخة الاحتياطية.", "Backup saved.") }
                }
            } catch {
                message = tr("تعذّر إنشاء النسخة: \(error.localizedDescription)", "Couldn't create the backup: \(error.localizedDescription)")
            }
        }
    }

    /// نسخ تلقائي إلى المجلد المختار (بعد الاستيراد). يعيد سطر النتيجة لرسالة الاستيراد.
    func autoBackup() async -> String? {
        guard Self.autoEnabled else { return nil }
        guard let folder = resolveFolder() else { return "⚠️ " + BackupError.folderUnavailable.localizedDescription }
        do {
            let (url, _) = try await makeBackup(title: tr("نسخ احتياطي تلقائي", "Auto backup"))
            defer { try? FileManager.default.removeItem(at: url) }
            let size = try await Task.detached(priority: .utility) { () throws -> Int64 in
                guard folder.startAccessingSecurityScopedResource() else { throw BackupError.folderUnavailable }
                defer { folder.stopAccessingSecurityScopedResource() }
                let destination = folder.appendingPathComponent(url.lastPathComponent)
                var coordinationError: NSError?
                var writeError: Error?
                NSFileCoordinator().coordinate(writingItemAt: destination, options: .forReplacing, error: &coordinationError) { target in
                    do {
                        try? FileManager.default.removeItem(at: target)
                        try FileManager.default.copyItem(at: url, to: target)
                    } catch { writeError = error }
                }
                if let error = coordinationError ?? writeError { throw error }
                // نسخة واحدة فقط: تُحذف أي نسخ قديمة بأسماء أخرى في المجلد.
                let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
                for old in files where old.lastPathComponent.hasPrefix(BackupFormat.prefix) && old.pathExtension == "zip"
                    && old.lastPathComponent != destination.lastPathComponent {
                    try? FileManager.default.removeItem(at: old)
                }
                return (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
            }.value
            UserDefaults.standard.set(Date(), forKey: Self.lastKey)
            UserDefaults.standard.set(Int(size), forKey: Self.lastSizeKey)
            version += 1
            return tr("☁️ حُفظت نسخة احتياطية في \(folderName ?? "المجلد").", "☁️ Backup saved to \(folderName ?? "the folder").")
        } catch {
            return tr("⚠️ تعذّر النسخ الاحتياطي التلقائي: \(error.localizedDescription)", "⚠️ Auto backup failed: \(error.localizedDescription)")
        }
    }

    // MARK: - الاسترجاع

    func pickRestore() {
        FilePicker.present(types: [.zip]) { [weak self] urls in
            guard let self, let url = urls.first else { return }
            Task { await self.open(url) }
        }
    }

    private func open(_ url: URL) async {
        do {
            let folder = try await Task.detached(priority: .userInitiated) { () throws -> URL in
                let dir = FileManager.default.temporaryDirectory.appendingPathComponent("restore-\(UUID().uuidString)", isDirectory: true)
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                guard let archive = try? Archive(url: url, accessMode: .read), archive["manifest.json"] != nil else { throw BackupError.notBackup }
                for entry in archive { _ = try archive.extract(entry, to: dir.appendingPathComponent(entry.path), skipCRC32: true) }
                try? FileManager.default.removeItem(at: url)
                return dir
            }.value
            let manifest = try JSONDecoder().decode(BackupFormat.Manifest.self, from: Data(contentsOf: folder.appendingPathComponent("manifest.json")))
            guard manifest.version <= BackupFormat.version else { throw BackupError.newerVersion }
            await FilePicker.waitUntilSettled()
            pendingRestore = (folder, manifest)
        } catch {
            message = error.localizedDescription
        }
    }

    func confirmRestore() {
        guard let pending = pendingRestore else { return }
        pendingRestore = nil
        isWorking = true
        Task {
            defer { isWorking = false; try? FileManager.default.removeItem(at: pending.folder) }
            let progress = ImportProgress.shared
            progress.begin(tr("استرجاع النسخة", "Restoring backup"))
            let sink = progress.sink(0, 1, tr("استبدال البيانات", "Replacing data"))
            let container = SharedModel.container
            do {
                let settings = try await Task.detached(priority: .userInitiated) {
                    try await BackupActor(modelContainer: container).restore(from: pending.folder, progress: sink)
                }.value
                for (key, data) in settings {
                    if let value = try? PropertyListSerialization.propertyList(from: data, format: nil) {
                        UserDefaults.standard.set(value, forKey: key)
                    }
                }
                UserDefaults.standard.removeObject(forKey: "homeStatsCache")
                StaffImportState.shared.version += 1
                progress.end()
                message = tr("تم الاسترجاع: \(pending.manifest.months.count) أشهر، \(Num.format(pending.manifest.staff)) في الملاك.",
                             "Restored: \(pending.manifest.months.count) months, \(pending.manifest.staff) staff.")
            } catch {
                progress.end()
                message = tr("تعذّر الاسترجاع: \(error.localizedDescription)", "Restore failed: \(error.localizedDescription)")
            }
        }
    }

    /// اختبار المحاكي (-backupTest YES): نسخ ثم استرجاع ثم مقارنة الأعداد.
    func selfTest() async {
        let context = SharedModel.container.mainContext
        func counts() -> String {
            let e = (try? context.fetchCount(FetchDescriptor<Employee>())) ?? -1
            let d = (try? context.fetchCount(FetchDescriptor<Deduction>())) ?? -1
            let m = (try? context.fetchCount(FetchDescriptor<PayrollMonth>())) ?? -1
            let s = (try? context.fetchCount(FetchDescriptor<StaffMember>())) ?? -1
            return "employees=\(e) deductions=\(d) months=\(m) staff=\(s)"
        }
        let before = counts()
        do {
            let start = Date()
            let (url, _) = try await makeBackup(title: "test")
            let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.intValue ?? 0
            await open(url)
            guard pendingRestore != nil else { LaunchOptions.writeReport("backup=failed open \(message ?? "")"); return }
            confirmRestore()
            while isWorking || pendingRestore != nil { try? await Task.sleep(for: .milliseconds(200)) }
            let after = counts()
            LaunchOptions.writeReport("backup=\(before == after ? "ok" : "mismatch") before[\(before)] after[\(after)] size=\(size) seconds=\(Int(Date().timeIntervalSince(start)))")
        } catch {
            LaunchOptions.writeReport("backup=failed \(error.localizedDescription)")
        }
    }

    func cancelRestore() {
        if let folder = pendingRestore?.folder { try? FileManager.default.removeItem(at: folder) }
        pendingRestore = nil
    }
}

// MARK: - نوافذ الملفات للنسخ

extension FilePicker {
    /// اختيار مجلد (للنسخ التلقائي).
    static func presentFolder(onPick: @escaping (URL?) -> Void) {
        present(types: [.folder], multiple: false) { urls in onPick(urls.first) }
    }

    /// حفظ ملف في مكان يختاره المستخدم.
    static func presentExport(_ url: URL, onDone: @escaping (Bool) -> Void) {
        guard let top = topController() else { onDone(false); return }
        let picker = UIDocumentPickerViewController(forExporting: [url], asCopy: true)
        let d = ExportDelegate(onDone: onDone)
        exportDelegate = d
        picker.delegate = d
        top.present(picker, animated: true)
    }

    private static var exportDelegate: ExportDelegate?

    private final class ExportDelegate: NSObject, UIDocumentPickerDelegate {
        let onDone: (Bool) -> Void
        init(onDone: @escaping (Bool) -> Void) { self.onDone = onDone }
        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
            onDone(true)
            Task { @MainActor in FilePicker.exportDelegate = nil }
        }
        func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
            onDone(false)
            Task { @MainActor in FilePicker.exportDelegate = nil }
        }
    }
}

// MARK: - قسم الإعدادات

struct BackupSection: View {
    @State private var manager = BackupManager.shared
    @State private var progress = ImportProgress.shared
    @AppStorage(BackupManager.autoKey) private var autoEnabled = false

    var body: some View {
        Section {
            if progress.isActive && manager.isWorking {
                VStack(alignment: .leading, spacing: 6) {
                    Text(progress.title).font(.subheadline.weight(.semibold))
                    ProgressView(value: progress.fraction)
                    Text(progress.detail).font(.caption).foregroundStyle(.secondary)
                }
            }
            Button {
                manager.backupNow()
            } label: {
                Label(tr("نسخة احتياطية الآن", "Back up now"), systemImage: "arrow.down.doc.fill")
            }
            .disabled(manager.isWorking)
            Button {
                manager.pickRestore()
            } label: {
                Label(tr("استرجاع من نسخة", "Restore from backup"), systemImage: "arrow.counterclockwise")
            }
            .disabled(manager.isWorking)

            Toggle(isOn: Binding(get: { autoEnabled }, set: { on in
                if on && manager.folderName == nil {
                    manager.chooseFolder { autoEnabled = true }
                } else {
                    autoEnabled = on
                }
            })) {
                Label(tr("نسخ تلقائي بعد كل استيراد", "Auto backup after each import"), systemImage: "arrow.triangle.2.circlepath")
            }
            if autoEnabled {
                Button {
                    manager.chooseFolder()
                } label: {
                    LabeledContent {
                        Text(tr("تغيير", "Change")).foregroundStyle(.tint)
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(tr("المجلد", "Folder")).foregroundStyle(.primary)
                            Text(manager.folderName ?? tr("لم يُختر", "Not chosen")).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                if let last = manager.lastBackup {
                    LabeledContent(tr("آخر نسخة", "Last backup")) {
                        Text("\(last.formatted(.relative(presentation: .named).locale(AppSettings.locale))) · \(Num.bytes(manager.lastSize))")
                    }
                }
            }
        } header: {
            Text(tr("النسخ الاحتياطي", "Backup"))
        } footer: {
            Text(tr("النسخة ملف واحد \"نسخة الرواتب.zip\" فيه كل البيانات: الرواتب والاستقطاعات لكل الأشهر، والملاك، والإعدادات. احفظها في iCloud Drive أو OneDrive أو Google Drive. كل نسخة جديدة تستبدل السابقة، فلا تتكدس النسخ.",
                    "One file \"نسخة الرواتب.zip\" with all data: payroll and deductions for every month, staff and settings. Save it to iCloud Drive, OneDrive or Google Drive. Each new backup replaces the previous one."))
        }
        .id(manager.version)
        .alert(tr("استرجاع النسخة؟", "Restore this backup?"), isPresented: Binding(
            get: { manager.pendingRestore != nil },
            set: { if !$0 && manager.pendingRestore != nil { manager.cancelRestore() } }
        )) {
            Button(tr("استرجاع", "Restore"), role: .destructive) { manager.confirmRestore() }
            Button(tr("إلغاء", "Cancel"), role: .cancel) { manager.cancelRestore() }
        } message: {
            if let m = manager.pendingRestore?.manifest {
                Text(tr("نسخة \(m.createdAt.formatted(.dateTime.year().month().day().hour().minute().locale(AppSettings.locale)))\n\(m.months.count) أشهر رواتب · \(Num.format(m.employees)) سجل رواتب · \(Num.format(m.staff)) في الملاك\nستُستبدل كل البيانات الحالية بمحتوى النسخة.",
                        "Backup of \(m.createdAt.formatted())\n\(m.months.count) months · \(m.employees) payroll rows · \(m.staff) staff\nAll current data will be replaced."))
            }
        }
        .alert(tr("النسخ الاحتياطي", "Backup"), isPresented: Binding(get: { manager.message != nil }, set: { if !$0 { manager.message = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: {
            Text(manager.message ?? "")
        }
    }
}
