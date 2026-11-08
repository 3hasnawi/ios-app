import Foundation
import SQLite3

/// نقطة رجوع محلية قبل الاسترجاع؛ تُعاد قبل فتح SwiftData بعد أي توقف غير مكتمل.
/// لا تتغير صيغة النسخة المصدّرة ولا يُحذف المصدر القديم عند فشل الاسترجاع.
nonisolated enum RestoreSafety {
    private struct Marker: Codable { let folder: String; let store: URL; let hadBooks: Bool }
    static var root: URL { FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("RestoreRecovery", isDirectory: true) }
    static var pending: Bool { FileManager.default.fileExists(atPath: root.appendingPathComponent("active.json").path) }
    static func prepare(store: URL, defaults: UserDefaults = .standard, domain: String? = Bundle.main.bundleIdentifier) throws {
        guard !pending else { throw failure("يوجد استرجاع غير مكتمل؛ أغلق التطبيق وافتحه لإعادة البيانات السابقة.") }
        let fm = FileManager.default
        let folder = root.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        var activated = false
        defer { if !activated { try? fm.removeItem(at: folder) } }
        try snapshot(from: store, to: folder.appendingPathComponent("database.sqlite"))
        let books = OfficialBookFiles.root
        let hadBooks = fm.fileExists(atPath: books.path)
        if hadBooks { try fm.copyItem(at: books, to: folder.appendingPathComponent("books")) }
        let settings = domain.flatMap { defaults.persistentDomain(forName: $0) } ?? [:]
        try PropertyListSerialization.data(fromPropertyList: settings, format: .binary, options: 0).write(to: folder.appendingPathComponent("defaults.plist"), options: .atomic)
        let marker = Marker(folder: folder.lastPathComponent, store: store, hadBooks: hadBooks)
        try JSONEncoder().encode(marker).write(to: root.appendingPathComponent("active.json"), options: .atomic)
        activated = true
    }
    private static func failure(_ text: String) -> NSError { NSError(domain: "RestoreSafety", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }
    /// SQLite online backup يشمل سجلات WAL الملتزمة، بخلاف نسخ ملف القاعدة المفتوحة مباشرة.
    static func snapshot(from source: URL, to destination: URL) throws {
        var input: OpaquePointer?, output: OpaquePointer?
        guard sqlite3_open_v2(source.path, &input, SQLITE_OPEN_READONLY, nil) == SQLITE_OK else {
            if let input { sqlite3_close(input) }; throw failure("تعذّر فتح قاعدة البيانات لحفظ نقطة الرجوع.")
        }
        defer { sqlite3_close(input) }
        guard sqlite3_open_v2(destination.path, &output, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, nil) == SQLITE_OK else {
            if let output { sqlite3_close(output) }; throw failure("تعذّر إنشاء نقطة الرجوع؛ تحقق من مساحة الجهاز.")
        }
        defer { sqlite3_close(output) }
        guard let backup = sqlite3_backup_init(output, "main", input, "main") else { throw failure("تعذّر تجهيز نقطة الرجوع.") }
        let result = sqlite3_backup_step(backup, -1)
        let finish = sqlite3_backup_finish(backup)
        guard result == SQLITE_DONE, finish == SQLITE_OK else { throw failure("تعذّر إكمال نقطة الرجوع (\(result)/\(finish))؛ بقيت البيانات دون استبدال.") }
        // ملف اللقطة مستقل: لا يعتمد على ملفات WAL/SHM مؤقتة قد تُحذف بعد إغلاق الاتصال.
        guard sqlite3_exec(output, "PRAGMA journal_mode=DELETE", nil, nil, nil) == SQLITE_OK else {
            throw failure("تعذّر تثبيت ملف نقطة الرجوع.")
        }
    }
    /// يُستدعى عند البدء فقط، قبل فتح أي ModelContainer للقاعدة.
    static func recoverIfNeeded(defaults preferences: UserDefaults = .standard, domain: String? = Bundle.main.bundleIdentifier) throws {
        guard pending else { try discardInactive(); return }
        let fm = FileManager.default
        let marker = try JSONDecoder().decode(Marker.self, from: Data(contentsOf: root.appendingPathComponent("active.json")))
        guard UUID(uuidString: marker.folder) != nil else { throw failure("نقطة الرجوع غير صالحة؛ لم تُحذف الملفات.") }
        let folder = root.appendingPathComponent(marker.folder, isDirectory: true)
        guard let defaults = try PropertyListSerialization.propertyList(from: Data(contentsOf: folder.appendingPathComponent("defaults.plist")), format: nil) as? [String: Any] else { throw failure("إعدادات نقطة الرجوع غير صالحة.") }
        let ready = folder.appendingPathComponent("ready.sqlite")
        if fm.fileExists(atPath: ready.path) { try fm.removeItem(at: ready) }
        try snapshot(from: folder.appendingPathComponent("database.sqlite"), to: ready)
        // لا توجد اتصالات مفتوحة هنا. إعادة المحاولة من نفس اللقطة آمنة عند توقف العملية.
        if fm.fileExists(atPath: marker.store.path) { _ = try fm.replaceItemAt(marker.store, withItemAt: ready) }
        else { try fm.moveItem(at: ready, to: marker.store) }
        for suffix in ["-wal", "-shm"] {
            let sidecar = URL(fileURLWithPath: marker.store.path + suffix)
            if fm.fileExists(atPath: sidecar.path) { try fm.removeItem(at: sidecar) }
        }
        if fm.fileExists(atPath: OfficialBookFiles.root.path) { try fm.removeItem(at: OfficialBookFiles.root) }
        if marker.hadBooks { try fm.copyItem(at: folder.appendingPathComponent("books"), to: OfficialBookFiles.root) }
        // نستبدل مفاتيح مجال التطبيق فقط؛ لا نمس افتراضيات النظام.
        if let domain { preferences.setPersistentDomain(defaults, forName: domain) }
        preferences.set(false, forKey: "backupServerEnabled")
        preferences.synchronize()
        try finish()
    }
    private static func discardInactive() throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: root.path) else { return }
        for folder in try fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) where UUID(uuidString: folder.lastPathComponent) != nil {
            try fm.removeItem(at: folder)
        }
    }
    static func finish() throws {
        let fm = FileManager.default
        let markerURL = root.appendingPathComponent("active.json")
        guard fm.fileExists(atPath: markerURL.path) else { return }
        let marker = try JSONDecoder().decode(Marker.self, from: Data(contentsOf: markerURL))
        try fm.removeItem(at: markerURL)
        if UUID(uuidString: marker.folder) != nil { try? fm.removeItem(at: root.appendingPathComponent(marker.folder)) }
    }
}
