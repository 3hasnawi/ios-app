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
    struct EmployeeRecord: Codable { var month: String; var name: String; var rank: String; var workplace: String; var net: Double?; var items: Data?; var details: Data? }
    struct DeductionRecord: Codable { var month: String; var name: String; var title: String; var amount: Double?; var note: String; var rank: String }
    struct StaffRecord: Codable { var name: String; var mother: String; var stat: String; var unified: String; var rank: String; var status: String; var workplace: String; var details: Data }

    /// إعدادات تُحفظ مع البيانات (بدون قفل Face ID).
    static let settingsKeys = ["staffCount", "staffImportedAt", "staffFileName", "staffStatuses", "unifiedRecentSearches",
                               ReportSheet.headerKey, AppSettings.languageKey, AppSettings.appearanceKey, AppSettings.numeralsKey, AppSettings.textSizeKey]

    /// اسم ثابت: كل نسخة جديدة تحلّ محل السابقة (تاريخ النسخة محفوظ داخلها).
    static func fileName() -> String { "\(prefix).zip" }
}

enum BackupError: LocalizedError {
    case notBackup, newerVersion, folderUnavailable, noFolder
    var errorDescription: String? {
        switch self {
        case .notBackup: tr("الملف ليس نسخة احتياطية من تطبيق الرواتب.", "This file isn't a payroll app backup.")
        case .newerVersion: tr("النسخة من إصدار أحدث من التطبيق. حدّث التطبيق ثم أعد المحاولة.", "The backup is from a newer app version. Update the app first.")
        case .folderUnavailable: tr("تعذّر الوصول إلى مجلد iCloud. اختر المجلد من جديد في الإعدادات ← النسخ الاحتياطي.", "The iCloud folder can't be reached. Choose it again in Settings › Backup.")
        case .noFolder: tr("لم يُختر مجلد iCloud بعد. اختره من الإعدادات ← النسخ الاحتياطي.", "No iCloud folder chosen yet. Choose it in Settings › Backup.")
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
            BackupFormat.EmployeeRecord(month: $0.month, name: $0.name, rank: $0.rank, workplace: $0.workplace, net: $0.netSalary, items: $0.itemsData, details: $0.detailsData)
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
            e.detailsData = r.details
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

/// النسخة تُحفظ في مجلد "نسخ الرواتب" داخل مجلد iCloud Drive يختاره المستخدم مرة واحدة.
/// (مجلد خاص بالتطبيق في iCloud يحتاج حساب مطوّر مدفوع، لذلك يُختار المجلد من نافذة الملفات.)
enum BackupDestination: String {
    case folder
}

@MainActor @Observable
final class BackupManager {
    static let shared = BackupManager()
    static let autoKey = "autoBackupEnabled"
    /// رابط محفوظ (bookmark) لملف النسخة الذي حفظه المستخدم في iCloud Drive: كل نسخة جديدة تُكتب فوقه.
    private static let fileKey = "autoBackupFile"
    static let subfolder = "نسخ الرواتب"
    private static let lastKey = "autoBackupLast"
    private static let lastSizeKey = "autoBackupLastSize"
    private static let lastPlaceKey = "autoBackupLastPlace"
    private static let errorKey = "autoBackupLastError"
    private static let errorAtKey = "autoBackupLastErrorAt"
    /// آخر تغيير في البيانات (استيراد أو حذف): إذا كان بعد آخر نسخة ناجحة تُعاد المحاولة تلقائياً.
    private static let changedKey = "backupDataChangedAt"
    /// اختبار المحاكي: مسار ملف بدل iCloud.
    private static let testFileKey = "backupTestFile"

    var isWorking = false
    var message: String?
    /// نسخة تنتظر تأكيد الاسترجاع.
    var pendingRestore: (folder: URL, manifest: BackupFormat.Manifest)?
    var version = 0

    var lastBackup: Date? { UserDefaults.standard.object(forKey: Self.lastKey) as? Date }
    var lastSize: Int64 { Int64(UserDefaults.standard.integer(forKey: Self.lastSizeKey)) }
    var lastPlace: String { UserDefaults.standard.string(forKey: Self.lastPlaceKey) ?? "" }
    var lastError: String? { UserDefaults.standard.string(forKey: Self.errorKey) }
    var lastErrorAt: Date? { UserDefaults.standard.object(forKey: Self.errorAtKey) as? Date }
    static var autoEnabled: Bool { UserDefaults.standard.bool(forKey: autoKey) }

    /// "iCloud Drive › نسخ الرواتب"، أو nil إذا لم يُختر مكان.
    var folderName: String? {
        _ = version
        return resolveFile().map { Self.placeName($0.deletingLastPathComponent()) }
    }

    /// المكان المختار ليس في iCloud (مثلاً على الآيفون): لا يصلح لجهاز آخر.
    var folderIsLocal: Bool {
        _ = version
        guard let url = resolveFile(), UserDefaults.standard.string(forKey: Self.testFileKey) == nil else { return false }
        return !url.path.contains("Mobile Documents")
    }

    /// سطر الحالة في صفحة الإعدادات.
    var statusLine: String {
        _ = version
        if folderName == nil { return tr("⚠️ اختر مكان النسخة في iCloud", "⚠️ Choose where to save in iCloud") }
        if lastError != nil { return tr("⚠️ فشل آخر نسخ، اضغط للتفاصيل", "⚠️ Last backup failed, tap for details") }
        guard Self.autoEnabled else { return lastBackup == nil ? tr("متوقف", "Off") : tr("متوقف · آخر نسخة \(Self.relative(lastBackup!))", "Off · last \(Self.relative(lastBackup!))") }
        guard let lastBackup else { return tr("تلقائي · iCloud · لم تُؤخذ نسخة بعد", "Auto · iCloud · no backup yet") }
        return tr("تلقائي · iCloud · \(Self.relative(lastBackup)) ✓", "Auto · iCloud · \(Self.relative(lastBackup)) ✓")
    }

    var statusColor: Color {
        _ = version
        if folderName == nil || lastError != nil { return .red }
        return Self.autoEnabled && lastBackup != nil ? .green : .secondary
    }

    static func relative(_ date: Date) -> String {
        date.formatted(.relative(presentation: .named).locale(AppSettings.locale))
    }

    /// "iCloud Drive › نسخ الرواتب" أو مسار المجلد.
    nonisolated static func placeName(_ url: URL) -> String {
        let parts = url.pathComponents
        if let i = parts.firstIndex(where: { $0.hasPrefix("com~apple~CloudDocs") }) {
            return (["iCloud Drive"] + parts[(i + 1)...]).joined(separator: " › ")
        }
        if url.path.contains("Mobile Documents") { return "iCloud Drive › " + parts.suffix(2).joined(separator: " › ") }
        return parts.suffix(2).joined(separator: " › ")
    }

    // MARK: - مكان النسخة

    /// تُجهَّز نسخة ثم تظهر نافذة "الحفظ": يختار المستخدم iCloud Drive (أو مجلداً فيه) ويضغط "حفظ".
    /// يحتفظ التطبيق برابط هذا الملف ويكتب كل نسخة جديدة فوقه.
    func chooseLocation() {
        guard !isWorking else { return }
        isWorking = true
        Task {
            defer { isWorking = false; version += 1 }
            do {
                let (url, _) = try await makeBackup(title: tr("تجهيز النسخة", "Preparing backup"))
                await FilePicker.waitUntilSettled()
                guard let saved = await FilePicker.exportForLocation(url) else {
                    try? FileManager.default.removeItem(at: url)
                    return
                }
                try? FileManager.default.removeItem(at: url)
                let granted = saved.startAccessingSecurityScopedResource()
                defer { if granted { saved.stopAccessingSecurityScopedResource() } }
                guard let data = try? saved.bookmarkData(options: .minimalBookmark, includingResourceValuesForKeys: nil, relativeTo: nil) else {
                    throw BackupError.folderUnavailable
                }
                let defaults = UserDefaults.standard
                defaults.set(data, forKey: Self.fileKey)
                let size = (try? saved.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                defaults.set(Date(), forKey: Self.lastKey)
                defaults.set(size, forKey: Self.lastSizeKey)
                defaults.set(Self.placeName(saved.deletingLastPathComponent()), forKey: Self.lastPlaceKey)
                defaults.removeObject(forKey: Self.errorKey)
                defaults.removeObject(forKey: Self.errorAtKey)
                Self.removeDeviceCopies()
            } catch {
                message = tr("تعذّر حفظ النسخة: \(error.localizedDescription)", "Couldn't save the backup: \(error.localizedDescription)")
            }
        }
    }

    /// ملف النسخة المحفوظ (في iCloud Drive).
    private func resolveFile() -> URL? {
        if let test = UserDefaults.standard.string(forKey: Self.testFileKey) { return URL(fileURLWithPath: test) }
        guard let data = UserDefaults.standard.data(forKey: Self.fileKey) else { return nil }
        var stale = false
        guard let url = try? URL(resolvingBookmarkData: data, options: [], relativeTo: nil, bookmarkDataIsStale: &stale) else { return nil }
        if stale {
            let granted = url.startAccessingSecurityScopedResource()
            if let fresh = try? url.bookmarkData(options: .minimalBookmark, includingResourceValuesForKeys: nil, relativeTo: nil) {
                UserDefaults.standard.set(fresh, forKey: Self.fileKey)
            }
            if granted { url.stopAccessingSecurityScopedResource() }
        }
        return url
    }

    /// النسخ القديمة على الجهاز (من إصدار سابق) لم تعد مطلوبة: تُحذف لتوفير المساحة.
    nonisolated static func removeDeviceCopies() {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        try? FileManager.default.removeItem(at: docs.appendingPathComponent("نسخ احتياطية", isDirectory: true))
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

    /// نسخة يدوية: نافذة المشاركة (Google Drive، OneDrive، واتساب، حفظ في الملفات).
    func shareNow() {
        guard !isWorking else { return }
        isWorking = true
        Task {
            defer { isWorking = false }
            do {
                let (url, _) = try await makeBackup(title: tr("نسخة احتياطية", "Backup"))
                ActivityShare.present([url])
            } catch {
                message = tr("تعذّر إنشاء النسخة: \(error.localizedDescription)", "Couldn't create the backup: \(error.localizedDescription)")
            }
        }
    }

    func markChanged() {
        UserDefaults.standard.set(Date(), forKey: Self.changedKey)
    }

    /// بيانات تغيّرت بعد آخر نسخة ناجحة (أو فشلت آخر محاولة).
    var needsBackup: Bool {
        guard Self.autoEnabled, resolveFile() != nil else { return false }
        guard let changed = UserDefaults.standard.object(forKey: Self.changedKey) as? Date else { return lastError != nil }
        return changed > (lastBackup ?? .distantPast) || lastError != nil
    }

    /// عند فتح التطبيق أو الخروج منه: يكمل نسخة فاتت (مثلاً أُغلق التطبيق أثناء النسخ أو فشلت المحاولة).
    func autoBackupIfNeeded() async {
        guard needsBackup, !isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else { return }
        _ = await backupToDestination()
    }

    /// نسخ تلقائي بعد الاستيراد. يعيد سطر النتيجة لرسالة الاستيراد.
    func autoBackup() async -> String? {
        markChanged()
        guard Self.autoEnabled else { return nil }
        guard resolveFile() != nil else { return "⚠️ " + BackupError.noFolder.localizedDescription }
        return await backupToDestination()
    }

    /// يكتب النسخة فوق ملف النسخة المحفوظ في iCloud (نسخة واحدة دائماً) ويحفظ النتيجة نجاحاً أو فشلاً.
    @discardableResult
    func backupToDestination() async -> String? {
        guard !isWorking else { return nil }
        guard let file = resolveFile() else {
            message = BackupError.noFolder.localizedDescription
            return "⚠️ " + BackupError.noFolder.localizedDescription
        }
        isWorking = true
        defer { isWorking = false; version += 1 }
        // يكمل النسخ حتى لو خرج المستخدم من التطبيق أثناءه.
        let task = UIApplication.shared.beginBackgroundTask(withName: "backup")
        defer { if task != .invalid { UIApplication.shared.endBackgroundTask(task) } }
        do {
            let (url, _) = try await makeBackup(title: tr("نسخ احتياطي إلى iCloud", "Backing up to iCloud"))
            defer { try? FileManager.default.removeItem(at: url) }
            let size = try await Task.detached(priority: .utility) { () throws -> Int64 in
                let granted = file.startAccessingSecurityScopedResource()
                defer { if granted { file.stopAccessingSecurityScopedResource() } }
                var coordinationError: NSError?
                var writeError: Error?
                // الكتابة داخل نفس الملف (لا يُنشأ ملف جديد): الصلاحية على الملف المحفوظ نفسه.
                NSFileCoordinator().coordinate(writingItemAt: file, options: .forMerging, error: &coordinationError) { target in
                    do {
                        let input = try FileHandle(forReadingFrom: url)
                        defer { try? input.close() }
                        let output = try FileHandle(forWritingTo: target)
                        defer { try? output.close() }
                        try output.truncate(atOffset: 0)
                        while let chunk = try input.read(upToCount: 1 << 20), !chunk.isEmpty {
                            try output.write(contentsOf: chunk)
                        }
                        try output.synchronize()
                    } catch { writeError = error }
                }
                if let error = coordinationError ?? writeError {
                    if !FileManager.default.fileExists(atPath: file.path) { throw BackupError.folderUnavailable }
                    throw error
                }
                let attrs = try? FileManager.default.attributesOfItem(atPath: file.path)
                return (attrs?[.size] as? NSNumber)?.int64Value ?? 0
            }.value
            let place = Self.placeName(file.deletingLastPathComponent())
            let defaults = UserDefaults.standard
            defaults.set(Date(), forKey: Self.lastKey)
            defaults.set(Int(size), forKey: Self.lastSizeKey)
            defaults.set(place, forKey: Self.lastPlaceKey)
            defaults.removeObject(forKey: Self.errorKey)
            defaults.removeObject(forKey: Self.errorAtKey)
            Self.removeDeviceCopies()
            return tr("☁️ حُفظت نسخة احتياطية: \(place).", "☁️ Backup saved: \(place).")
        } catch {
            let reason = (error as? BackupError)?.localizedDescription ?? error.localizedDescription
            UserDefaults.standard.set(reason, forKey: Self.errorKey)
            UserDefaults.standard.set(Date(), forKey: Self.errorAtKey)
            return tr("⚠️ تعذّر النسخ الاحتياطي التلقائي: \(reason)", "⚠️ Auto backup failed: \(reason)")
        }
    }

    /// اختبار المحاكي (-autoBackupTest YES): ملف تجريبي بدل iCloud، نسختان فوق نفس الملف.
    func autoBackupSelfTest() async {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let folder = docs.appendingPathComponent("icloud-test", isDirectory: true).appendingPathComponent(Self.subfolder, isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent(BackupFormat.fileName())
        if !FileManager.default.fileExists(atPath: file.path) { FileManager.default.createFile(atPath: file.path, contents: Data()) }
        UserDefaults.standard.set(file.path, forKey: Self.testFileKey)
        UserDefaults.standard.set(true, forKey: Self.autoKey)
        let line = await autoBackup() ?? "nil"
        let size = (try? FileManager.default.attributesOfItem(atPath: file.path)[.size] as? NSNumber)?.intValue ?? 0
        let again = await backupToDestination() ?? "nil"
        let count = ((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []).count
        let valid = (try? Archive(url: file, accessMode: .read))?["manifest.json"] != nil
        LaunchOptions.writeReport("autobackup=\(size > 0 && valid && lastError == nil && count == 1 ? "ok" : "failed") size=\(size) zip=\(valid) files=\(count) line=\(line) again=\(again)")
    }

    // MARK: - الاسترجاع

    func pickRestore() {
        FilePicker.present(types: [.zip]) { [weak self] urls in
            guard let self, let url = urls.first else { return }
            Task { await self.open(url) }
        }
    }

    func openForRestore(_ url: URL) async { await open(url) }

    private func open(_ url: URL) async {
        do {
            let folder = try await Task.detached(priority: .userInitiated) { () throws -> URL in
                let dir = FileManager.default.temporaryDirectory.appendingPathComponent("restore-\(UUID().uuidString)", isDirectory: true)
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                guard let archive = try? Archive(url: url, accessMode: .read), archive["manifest.json"] != nil else { throw BackupError.notBackup }
                for entry in archive { _ = try archive.extract(entry, to: dir.appendingPathComponent(entry.path), skipCRC32: true) }
                ImportCenter.removeCopy(url)
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
    /// نافذة "حفظ في الملفات" لملف النسخة: يعيد رابط المكان الذي حُفظ فيه (أو nil عند الإلغاء).
    static func exportForLocation(_ url: URL) async -> URL? {
        await withCheckedContinuation { continuation in
            guard let top = topController() else { continuation.resume(returning: nil); return }
            let picker = UIDocumentPickerViewController(forExporting: [url], asCopy: true)
            let d = ExportDelegate { continuation.resume(returning: $0) }
            exportDelegate = d
            picker.delegate = d
            top.present(picker, animated: true)
        }
    }

    private static var exportDelegate: ExportDelegate?

    private final class ExportDelegate: NSObject, UIDocumentPickerDelegate {
        private var onDone: ((URL?) -> Void)?
        init(onDone: @escaping (URL?) -> Void) { self.onDone = onDone }
        private func finish(_ url: URL?) {
            onDone?(url)
            onDone = nil
            Task { @MainActor in FilePicker.exportDelegate = nil }
        }
        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) { finish(urls.first) }
        func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) { finish(nil) }
    }
}

// MARK: - صفحة النسخ الاحتياطي

struct BackupView: View {
    @State private var manager = BackupManager.shared
    @State private var progress = ImportProgress.shared
    @AppStorage(BackupManager.autoKey) private var autoEnabled = true
    @State private var showFolderHelp = false

    var body: some View {
        List {
            Section {
                statusCard
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(Color.clear)
            }

            Section {
                if let folder = manager.folderName {
                    Button {
                        showFolderHelp = true
                    } label: {
                        LabeledContent {
                            Text(tr("تغيير", "Change")).foregroundStyle(.tint)
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(tr("مكان النسخة", "Backup location")).foregroundStyle(.primary)
                                Text(folder).font(.caption).foregroundStyle(manager.folderIsLocal ? .orange : .secondary)
                            }
                        }
                    }
                    if manager.folderIsLocal {
                        Label(tr("المجلد المختار على الآيفون وليس في iCloud، فلن تصل النسخة لجهاز آخر.", "This folder is on the iPhone, not iCloud."),
                              systemImage: "exclamationmark.triangle.fill")
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                } else {
                    Button {
                        showFolderHelp = true
                    } label: {
                        Label(tr("حفظ النسخة في iCloud", "Save the backup to iCloud"), systemImage: "icloud.and.arrow.up.fill")
                            .font(.body.weight(.semibold))
                    }
                    .accessibilityIdentifier("chooseICloudFolder")
                }
                Toggle(tr("نسخ تلقائي بعد كل استيراد", "Auto backup after each import"), isOn: $autoEnabled)
                    .accessibilityIdentifier("autoBackupToggle")
                Button {
                    Task { await manager.backupToDestination() }
                } label: {
                    HStack {
                        Text(tr("نسخ الآن إلى iCloud", "Back up to iCloud now"))
                        Spacer()
                        if manager.isWorking { ProgressView() }
                    }
                }
                .disabled(manager.isWorking || manager.folderName == nil)
                .accessibilityIdentifier("backupToDestination")
            } header: {
                Text("iCloud Drive")
            } footer: {
                Text(tr("النسخة ملف واحد \"نسخة الرواتب.zip\" في المكان الذي حفظته فيه داخل iCloud Drive، فيه كل البيانات: الرواتب والاستقطاعات لكل الأشهر، والملاك، والإعدادات. كل نسخة جديدة تستبدل السابقة، ولا تُحفظ نسخة على الآيفون. إذا فاتت نسخة تُعاد المحاولة تلقائياً عند فتح التطبيق.",
                        "One file \"نسخة الرواتب.zip\" in the \"نسخ الرواتب\" folder in iCloud Drive with all data. Each backup replaces the previous one; no copy is kept on the iPhone."))
            }

            Section {
                Button {
                    manager.pickRestore()
                } label: {
                    Label {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(tr("استرجاع من نسخة", "Restore from backup")).foregroundStyle(.primary)
                            Text(tr("من iCloud Drive ← نسخ الرواتب ← نسخة الرواتب.zip", "iCloud Drive › نسخ الرواتب › نسخة الرواتب.zip"))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    } icon: {
                        Image(systemName: "arrow.counterclockwise").foregroundStyle(.blue)
                    }
                }
                .disabled(manager.isWorking)
                Button {
                    manager.shareNow()
                } label: {
                    Label {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(tr("مشاركة نسخة الآن", "Share a backup now")).foregroundStyle(.primary)
                            Text("Google Drive · OneDrive · " + tr("واتساب", "WhatsApp")).font(.caption).foregroundStyle(.secondary)
                        }
                    } icon: {
                        Image(systemName: "square.and.arrow.up").foregroundStyle(.gray)
                    }
                }
                .disabled(manager.isWorking)
                .accessibilityIdentifier("shareBackup")
            } header: {
                Text(tr("جهاز آخر أو نسخة يدوية", "Another device or manual copy"))
            } footer: {
                Text(tr("على جهاز آخر: ثبّت التطبيق وسجّل الدخول بنفس حساب iCloud، ثم الإعدادات ← النسخ الاحتياطي ← استرجاع من نسخة (أو \"استيراد ملفات\") واختر \"نسخة الرواتب.zip\". بعدها اضغط \"حفظ النسخة في iCloud\" واحفظها فوق نفس الملف (استبدال) ليستمر النسخ التلقائي من الجهاز الجديد.",
                        "On another device: install the app with the same iCloud account, then Settings › Backup › Restore (or Import files) and pick \"نسخة الرواتب.zip\"."))
            }
        }
        .navigationTitle(tr("النسخ الاحتياطي", "Backup"))
        .navigationBarTitleDisplayMode(.inline)
        .alert(tr("حفظ النسخة في iCloud", "Save the backup to iCloud"), isPresented: $showFolderHelp) {
            Button(tr("متابعة", "Continue")) { manager.chooseLocation() }
            Button(tr("إلغاء", "Cancel"), role: .cancel) {}
        } message: {
            Text(tr("تُجهَّز النسخة ثم تظهر نافذة الحفظ: اختر \"iCloud Drive\" (يُفضّل مجلداً باسم \"نسخ الرواتب\"، ويمكن إنشاؤه من الزر أعلى النافذة) ثم اضغط \"حفظ\". بعدها تُكتب كل نسخة تلقائية فوق نفس الملف.",
                    "The backup is prepared, then the save window opens: choose iCloud Drive and tap Save. Every automatic backup then overwrites that same file."))
        }
    }

    /// آخر نسخة ناجحة، أو سبب فشل آخر محاولة، أو التقدّم أثناء النسخ.
    @ViewBuilder
    private var statusCard: some View {
        let _ = manager.version
        let failed = manager.lastError != nil
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: manager.isWorking ? "arrow.triangle.2.circlepath" : failed ? "exclamationmark.triangle.fill" : manager.lastBackup == nil ? "clock" : "checkmark")
                .font(.title3.weight(.bold))
                .foregroundStyle(.white)
                .frame(width: 44, height: 44)
                .background((manager.isWorking ? Color.blue : failed ? .red : manager.lastBackup == nil ? .gray : .green).gradient,
                            in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            VStack(alignment: .leading, spacing: 4) {
                if manager.isWorking && progress.isActive {
                    Text(progress.title).font(.headline)
                    ProgressView(value: progress.fraction)
                    Text(progress.detail).font(.caption).foregroundStyle(.secondary)
                } else {
                    if let last = manager.lastBackup {
                        Text(tr("آخر نسخة: \(Self.stamp(last))", "Last backup: \(Self.stamp(last))")).font(.headline)
                        Text("\(BackupFormat.fileName()) · \(Num.bytes(manager.lastSize))\n\(manager.lastPlace)")
                            .font(.caption).foregroundStyle(.secondary)
                    } else {
                        Text(tr("لم تُؤخذ نسخة بعد", "No backup yet")).font(.headline)
                    }
                    if let error = manager.lastError {
                        Text(tr("فشلت آخر محاولة\(manager.lastErrorAt.map { " (" + Self.stamp($0) + ")" } ?? ""): \(error)",
                                "Last attempt failed: \(error)"))
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.red)
                        Button(tr("إعادة المحاولة", "Try again")) { Task { await manager.backupToDestination() } }
                            .font(.subheadline.weight(.semibold))
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("backupStatus")
    }

    static func stamp(_ date: Date) -> String {
        Calendar.current.isDateInToday(date)
            ? tr("اليوم ", "Today ") + date.formatted(.dateTime.hour().minute().locale(AppSettings.locale))
            : date.formatted(.dateTime.year().month().day().hour().minute().locale(AppSettings.locale))
    }
}
