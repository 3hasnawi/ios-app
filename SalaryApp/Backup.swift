import SwiftUI
import UserNotifications
import SwiftData
import UIKit
import UniformTypeIdentifiers
import ZIPFoundation
import CryptoKit

/// النسخ الاحتياطي: ملف zip واحد فيه كل البيانات (الأشهر، الرواتب، الاستقطاعات، الملاك، الكتب المصوّرة) والإعدادات.
/// يُحفظ في أي مكان من نافذة "الملفات" (iCloud Drive، OneDrive، Google Drive)،
/// ونسخ تلقائي بعد كل استيراد إلى مجلد يُختار مرة واحدة. نسخة واحدة فقط باسم ثابت: الجديدة تستبدل السابقة.
nonisolated enum BackupFormat {
    // تمنع الإصدارات القديمة من استرجاع نسخة تحوي حسابات محلية ثم إسقاطها بصمت.
    static let version = 10
    /// الإصدار ٧ صيغة رواتب تجريبية مختلفة ولم يُفعّل استرجاعها.
    static func supports(_ version: Int) -> Bool { (1...6).contains(version) || version == 8 || version == 9 || version == 10 }
    static func exportVersion(for settings: [String: Data]) -> Int {
        if let raw = settings[TransferLetterCustomizationStore.settingsKey],
           let data = try? PropertyListSerialization.propertyList(from: raw, format: nil) as? Data, !data.isEmpty {
            guard let store = try? JSONDecoder().decode(TransferLetterCustomizationStore.self, from: data) else { return 10 }
            if store.hasUnifiedTemplates { return 10 }
        }
        return settings[CustomTransferTemplate.settingsKey] != nil ? 9 : (settings[FundingApproval.settingsKey] != nil ? 8 : (settings[ContractOverrides.settingsKey] == nil ? 5 : 6))
    }
    static let prefix = "نسخة الرواتب"

    struct Manifest: Codable, Sendable {
        var version: Int
        var createdAt: Date
        var months: [String]
        var employees: Int
        var deductions: Int
        var staff: Int
        var accounts: Int?
        var books: Int?
        var hasData: Bool {
            !months.isEmpty || employees > 0 || deductions > 0 || staff > 0 ||
            (accounts ?? 0) > 0 || (books ?? 0) > 0
        }
    }

    struct MonthRecord: Codable { var name: String; var importedAt: Date; var employeeCount: Int; var deductionCount: Int; var summary: Data? }
    struct EmployeeRecord: Codable { var month: String; var name: String; var rank: String; var workplace: String; var net: Double?; var items: Data?; var details: Data?; var rawColumns: Data? }
    struct DeductionRecord: Codable { var month: String; var name: String; var title: String; var amount: Double?; var note: String; var rank: String }
    struct StaffRecord: Codable { var name: String; var mother: String; var stat: String; var unified: String; var rank: String; var status: String; var workplace: String; var details: Data }
    struct AccountRecord: Codable { var entity: String; var iban: String; var branch: String; var swift: String; var localAccount: String? = nil }

    /// إعدادات تُحفظ مع البيانات (بدون قفل Face ID).
    static let settingsKeys = ["pendingTrustPayrollMonths", "pendingTrustPayrollStarted", "staffCount", "staffImportedAt", "staffFileName", "staffStatuses", "unifiedRecentSearches",
                               "autoBackupEnabled", "backupSchedule", "homeTileLayout", "homeAccountingActions",
                               SalaryTrust.settingsKey, FundingApproval.settingsKey, CustomTransferTemplate.settingsKey, "bankStatementMonth", "bankStatementAccountName", "bankStatementIBAN", "bankStatementBranch", "bankStatementLocalAccount", "bankStatementManualAmounts", ContractOverrides.settingsKey, BankReconciliation.settingsKey, TransferRules.settingsKey, "transferPayrollAccountLinks", TransferIssuance.settingsKey, TransferReferencePack.settingsKey, TransferLetterCustomizationStore.settingsKey, TransferLetterEntityLinks.settingsKey, TransferHeaderFont.boldSettingsKey, TransferHeaderFont.regularSettingsKey,
                               ReportSheet.headerKey, AppSettings.languageKey, AppSettings.appearanceKey, AppSettings.numeralsKey, AppSettings.textSizeKey]

    /// اسم ثابت: كل نسخة جديدة تحلّ محل السابقة (تاريخ النسخة محفوظ داخلها).
    static func fileName() -> String { "\(prefix).zip" }
}

enum BackupError: LocalizedError {
    case notBackup, unavailableArchive, newerVersion, folderUnavailable, noFolder, emptyArchive, emptyData, emptyRestore, dataLossRisk
    var errorDescription: String? {
        switch self {
        case .notBackup: tr("الملف ليس نسخة احتياطية من تطبيق الرواتب.", "This file isn't a payroll app backup.")
        case .unavailableArchive: tr("تعذّر التحقق من النسخة السابقة لأن حجمها غير متاح. لم تُستبدل؛ تأكد من توفر الملف ثم أعد المحاولة.", "The previous backup could not be verified because its size is unavailable. It was preserved; make it available and retry.")
        case .newerVersion: tr("النسخة من إصدار أحدث من التطبيق. حدّث التطبيق ثم أعد المحاولة.", "The backup is from a newer app version. Update the app first.")
        case .folderUnavailable: tr("تعذّر الوصول إلى ملف النسخة. اختره من جديد في الإعدادات ← النسخ الاحتياطي.", "The backup file can't be reached. Choose it again in Settings › Backup.")
        case .noFolder: tr("لم يُختر ملف النسخة بعد. اختره من الإعدادات ← النسخ الاحتياطي.", "No backup file chosen yet. Choose it in Settings › Backup.")
        case .emptyArchive: tr("النسخة الناتجة فارغة؛ لم يُستبدل الملف السابق.", "The new backup is empty; the previous file was preserved.")
        case .emptyData: tr("لا توجد بيانات محفوظة للنسخ؛ لم يُستبدل ملف النسخة السابقة.", "There is no saved data to back up; the previous backup was preserved.")
        case .emptyRestore: tr("النسخة المختارة لا تحتوي بيانات؛ لم تُستبدل بيانات التطبيق.", "The selected backup contains no data; app data was preserved.")
        case .dataLossRisk: tr("النسخة السابقة تحتوي بيانات غير موجودة الآن. لم تُستبدل؛ استرجع البيانات أولاً أو احفظ النسخة الجديدة في مجلد آخر.", "The previous backup contains data missing from this app. It was preserved; restore first or save the new backup in another folder.")
        }
    }
}

/// القراءة والكتابة في الخلفية.
@ModelActor
actor BackupActor {
    /// فهرس صغير يُقرأ من قاعدة البيانات دون نسخ السجلات أو الملفات.
    func safetyManifest() throws -> BackupFormat.Manifest {
        let months = try modelContext.fetch(FetchDescriptor<PayrollMonth>()).map(\.name)
        let employees = try modelContext.fetchCount(FetchDescriptor<Employee>())
        let deductions = try modelContext.fetchCount(FetchDescriptor<Deduction>())
        let staff = try modelContext.fetchCount(FetchDescriptor<StaffMember>())
        return BackupFormat.Manifest(version: BackupFormat.version, createdAt: .now, months: months,
                                     employees: employees, deductions: deductions, staff: staff,
                                     accounts: try ExecutionAccountStore.load().count,
                                     books: try OfficialBookFiles.read().count)
    }

    /// يكتب ملف النسخة (zip) ويعيد رابطه ووصفه.
    func export(settings: [String: Data], progress: ProgressSink?) throws -> (URL, BackupFormat.Manifest) {
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("backup-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: dir) }
        let encoder = JSONEncoder()

        func writeLines<S: Sequence>(_ name: String, _ items: S, count: Int, from: Double, to: Double) throws where S.Element: Encodable {
            let url = dir.appendingPathComponent(name)
            _ = fm.createFile(atPath: url.path, contents: nil)
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            var buffer = Data()
            for (i, item) in items.enumerated() {
                buffer.append(try encoder.encode(item))
                buffer.append(0x0A)
                if buffer.count > 1_000_000 { handle.write(buffer); buffer.removeAll(keepingCapacity: true) }
                if i % 2000 == 0 { progress?(from + (to - from) * Double(i) / Double(max(1, count))) }
            }
            handle.write(buffer)
        }

        let months = try modelContext.fetch(FetchDescriptor<PayrollMonth>())
        try writeLines("months.jsonl", months.lazy.map {
            BackupFormat.MonthRecord(name: $0.name, importedAt: $0.importedAt, employeeCount: $0.employeeCount,
                                     deductionCount: $0.deductionCount, summary: $0.itemSummaryData)
        }, count: months.count, from: 0, to: 0.02)
        func writeModels<T: PersistentModel, R: Encodable>(_ type: T.Type, file: String, from: Double, to: Double, convert: (T) -> R) throws -> Int {
            let count = try modelContext.fetchCount(FetchDescriptor<T>())
            let target = dir.appendingPathComponent(file)
            _ = fm.createFile(atPath: target.path, contents: nil)
            let handle = try FileHandle(forWritingTo: target)
            defer { try? handle.close() }
            var offset = 0
            while offset < count {
                try Task.checkCancellation()
                // سياق قصير العمر لكل دفعة حتى لا تحتفظ القاعدة بكل الكائنات والحقول الكبيرة.
                try autoreleasepool {
                    let context = ModelContext(modelContainer)
                    var descriptor = FetchDescriptor<T>()
                    descriptor.fetchLimit = 256; descriptor.fetchOffset = offset
                    let items = try context.fetch(descriptor)
                    guard !items.isEmpty else { throw BackupError.notBackup }
                    for item in items {
                        var data = try encoder.encode(convert(item)); data.append(0x0A)
                        try handle.write(contentsOf: data)
                    }
                    offset += items.count
                }
                progress?(from + (to - from) * Double(offset) / Double(max(1, count)))
            }
            return count
        }
        let employeeCount = try writeModels(Employee.self, file: "employees.jsonl", from: 0.02, to: 0.4) {
            BackupFormat.EmployeeRecord(month: $0.month, name: $0.name, rank: $0.rank, workplace: $0.workplace, net: $0.netSalary, items: $0.itemsData, details: $0.detailsData, rawColumns: $0.rawColumnsData)
        }
        let deductionCount = try writeModels(Deduction.self, file: "deductions.jsonl", from: 0.4, to: 0.5) {
            BackupFormat.DeductionRecord(month: $0.month, name: $0.employeeName, title: $0.title, amount: $0.amount, note: $0.note, rank: $0.rank)
        }
        let staffCount = try writeModels(StaffMember.self, file: "staff.jsonl", from: 0.5, to: 0.8) {
            BackupFormat.StaffRecord(name: $0.name, mother: $0.motherKey, stat: $0.statNumber, unified: $0.unifiedNumber,
                                     rank: $0.rank, status: $0.status, workplace: $0.workplace, details: $0.detailsData)
        }
        let accounts = try ExecutionAccountStore.load()
        try writeLines("accounts.jsonl", accounts.lazy.map {
            BackupFormat.AccountRecord(entity: $0.entity, iban: $0.iban, branch: $0.branch, swift: $0.swift, localAccount: $0.localAccount)
        }, count: accounts.count, from: 0.8, to: 0.82)
        let books = try OfficialBookFiles.stage(into: dir.appendingPathComponent("books", isDirectory: true))
        if let serialized = settings[SalaryTrust.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            try SalaryTrust.validateBooks(SalaryTrust.decode(data), available: Set(books.map(\.id)))
        }

        if let serialized = settings[ContractOverrides.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try ContractOverrides.decode(data)
        }
        if let serialized = settings[FundingApproval.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try FundingApproval.decode(data)
        }
        if let serialized = settings[CustomTransferTemplate.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try CustomTransferTemplate.decode(data)
        }
        if let serialized = settings[TransferLetterCustomizationStore.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            if !data.isEmpty { try JSONDecoder().decode(TransferLetterCustomizationStore.self, from: data).validateUnifiedTemplates() }
        }
        let formatVersion = BackupFormat.exportVersion(for: settings)
        let manifest = BackupFormat.Manifest(version: formatVersion, createdAt: .now,
                                             months: months.sorted { $0.sortKey > $1.sortKey }.map(\.name),
                                             employees: employeeCount, deductions: deductionCount, staff: staffCount,
                                             accounts: accounts.count, books: books.count)
        try JSONEncoder().encode(manifest).write(to: dir.appendingPathComponent("manifest.json"))
        try JSONEncoder().encode(settings).write(to: dir.appendingPathComponent("settings.json"))

        let out = fm.temporaryDirectory.appendingPathComponent(BackupFormat.fileName())
        try? fm.removeItem(at: out)
        let archive = try Archive(url: out, accessMode: .create)
        for name in ["manifest.json", "settings.json", "months.jsonl", "employees.jsonl", "deductions.jsonl", "staff.jsonl", "accounts.jsonl"] {
            try archive.addEntry(with: name, relativeTo: dir, compressionMethod: .deflate)
        }
        try archive.addEntry(with: "books/index.json", relativeTo: dir, compressionMethod: .deflate)
        for book in books {
            try archive.addEntry(with: "books/\(book.id.uuidString).pdf", relativeTo: dir, compressionMethod: .deflate)
        }
        progress?(1)
        return (out, manifest)
    }

    /// يستبدل كل البيانات بمحتوى النسخة. يعيد الإعدادات المحفوظة فيها.
    func restore(from folder: URL, progress: ProgressSink?, recoverable: Bool = false) throws -> [String: Data] {
        let decoder = JSONDecoder()
        func records<T: Decodable>(_ name: String, _ type: T.Type) throws -> [T] {
            var result: [T] = []
            try BackupLineReader.read(folder.appendingPathComponent(name), maxLineBytes: 64 * 1024 * 1024) { line in
                result.append(try decoder.decode(type, from: line))
            }
            return result
        }
        // فحص كامل بالتدفق قبل حذف أي سجل. البصمة تُقارَن أثناء القراءة الثانية قبل حفظ المعاملة.
        func validate<T: Decodable>(_ name: String, _ type: T.Type) throws -> (count: Int, hash: Data) {
            var count = 0
            var hash = SHA256()
            try BackupLineReader.read(folder.appendingPathComponent(name), maxLineBytes: 64 * 1024 * 1024, onChunk: { hash.update(data: $0) }) { line in
                _ = try decoder.decode(type, from: line)
                count += 1
            }
            return (count, Data(hash.finalize()))
        }
        func insertRecords<T: Decodable>(_ name: String, _ type: T.Type, expected: (count: Int, hash: Data),
                                          insert: (Int, T) throws -> Void) throws {
            var count = 0
            var hash = SHA256()
            try BackupLineReader.read(folder.appendingPathComponent(name), maxLineBytes: 64 * 1024 * 1024, onChunk: { hash.update(data: $0) }) { line in
                try insert(count, decoder.decode(type, from: line))
                count += 1
            }
            guard count == expected.count, Data(hash.finalize()) == expected.hash else { throw BackupError.notBackup }
        }
        // نفحص النسخة كاملة قبل لمس البيانات الحالية.
        let manifest = try decoder.decode(BackupFormat.Manifest.self, from: Data(contentsOf: folder.appendingPathComponent("manifest.json")))
        guard BackupFormat.supports(manifest.version) else { throw BackupError.newerVersion }
        guard manifest.hasData else { throw BackupError.emptyRestore }
        let months = try records("months.jsonl", BackupFormat.MonthRecord.self)
        let employees = try validate("employees.jsonl", BackupFormat.EmployeeRecord.self)
        let deductions = try validate("deductions.jsonl", BackupFormat.DeductionRecord.self)
        let staff = try validate("staff.jsonl", BackupFormat.StaffRecord.self)
        let accounts: [BackupFormat.AccountRecord]
        if manifest.version >= 2 {
            accounts = try records("accounts.jsonl", BackupFormat.AccountRecord.self)
        } else {
            accounts = []
        }
        guard months.map(\.name).sorted() == manifest.months.sorted(),
              employees.count == manifest.employees, deductions.count == manifest.deductions,
              staff.count == manifest.staff, manifest.version < 2 || manifest.accounts != nil,
              accounts.count == (manifest.accounts ?? 0) else { throw BackupError.notBackup }
        let settings = try decoder.decode([String: Data].self, from: Data(contentsOf: folder.appendingPathComponent("settings.json")))
        for data in settings.values { _ = try PropertyListSerialization.propertyList(from: data, format: nil) }
        if let serialized = settings[FundingApproval.settingsKey] {
            guard (manifest.version == 8 || manifest.version == 9 || manifest.version == 10), let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try FundingApproval.decode(data)
        } else if manifest.version == 8 { throw BackupError.notBackup }
        if let serialized = settings[CustomTransferTemplate.settingsKey] {
            guard (manifest.version == 9 || manifest.version == 10), let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try CustomTransferTemplate.decode(data)
        } else if manifest.version == 9 { throw BackupError.notBackup }
        if let serialized = settings[TransferLetterCustomizationStore.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            let templates = try (data.isEmpty ? TransferLetterCustomizationStore() : JSONDecoder().decode(TransferLetterCustomizationStore.self, from: data))
            try templates.validateUnifiedTemplates()
            if templates.hasUnifiedTemplates && manifest.version < 10 { throw BackupError.notBackup }
            if !templates.hasUnifiedTemplates && manifest.version == 10 { throw BackupError.notBackup }
        } else if manifest.version == 10 { throw BackupError.notBackup }
        if settings[ContractOverrides.settingsKey] == nil,
           let current = try ContractOverrides.currentData(), !(try ContractOverrides.decode(current)).isEmpty {
            throw NSError(domain: "ContractOverride", code: 3, userInfo: [NSLocalizedDescriptionKey: tr("النسخة المختارة لا تحتوي تعديلات العقود الموجودة على الجهاز. احفظ نسخة شاملة وراجع التعديلات قبل الاسترجاع.", "The selected backup does not contain this device’s contract adjustments. Save a full backup and review the adjustments before restoring.")])
        }
        if let serialized = settings[ContractOverrides.settingsKey] {
            guard manifest.version >= 6, let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try ContractOverrides.decode(data)
        }
        if settings[SalaryTrust.settingsKey] == nil,
           let saved = UserDefaults.standard.data(forKey: SalaryTrust.settingsKey),
           !(try SalaryTrust.decode(saved)).entries.isEmpty {
            throw SalaryTrust.invalid(tr("النسخة المختارة لا تحتوي دفتر الأمانات الموجود على الجهاز. لم تُستبدل البيانات؛ احفظ نسخة شاملة أولاً واسترجع نسخة تحتوي الدفتر.", "The selected backup does not contain this device’s trust ledger. Data was not replaced; save a full backup and restore one containing the ledger."))
        }
        if let serialized = settings[SalaryTrust.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try SalaryTrust.decode(data)
        }
        if let serialized = settings[BankReconciliation.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try BankReconciliation.decode(data)
        }
        if let serialized = settings[TransferReferencePack.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try TransferReferencePack.validateData(data)
        }
        let accountData = try ExecutionAccountStore.encode(accounts.map {
            SavedExecutionAccount(entity: $0.entity, iban: $0.iban, branch: $0.branch, swift: $0.swift, localAccount: $0.localAccount)
        })
        let booksFolder = folder.appendingPathComponent("books", isDirectory: true)
        if manifest.version >= 3 {
            guard let expected = manifest.books else { throw BackupError.notBackup }
            let archivedBooks = try OfficialBookFiles.validateArchive(in: booksFolder, expected: expected)
            if let serialized = settings[SalaryTrust.settingsKey] {
                guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
                try SalaryTrust.validateBooks(SalaryTrust.decode(data), available: Set(archivedBooks.map(\.id)))
            }
        } else {
            if let serialized = settings[SalaryTrust.settingsKey] {
                guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
                try SalaryTrust.validateBooks(SalaryTrust.decode(data), available: [])
            }
            try OfficialBookFiles.write([], in: booksFolder)
        }
        let preparedBooks = try OfficialBookFiles.prepareReplacement(from: booksFolder)
        var booksInstalled = false
        var previousBooks: URL?
        defer { if !booksInstalled { try? FileManager.default.removeItem(at: preparedBooks) } }

        if recoverable {
            guard let store = modelContainer.configurations.first?.url else { throw BackupError.folderUnavailable }
            try RestoreSafety.prepare(store: store)
        }
        modelContext.autosaveEnabled = false
        var committed = false
        defer {
            if !committed {
                modelContext.rollback()
                if booksInstalled { OfficialBookFiles.rollBackInstallation(previousBooks) }
            }
        }
        // الواجهة تستخدم دفعات صغيرة مع نقطة رجوع على القرص؛ الاختبارات المعزولة تحتفظ بالمعاملة الواحدة.
        func remove<T: PersistentModel>(_ type: T.Type) throws {
            var descriptor = FetchDescriptor<T>()
            if recoverable { descriptor.fetchLimit = 256 }
            while true {
                let context = recoverable ? ModelContext(modelContainer) : modelContext
                context.autosaveEnabled = false
                let items = try context.fetch(descriptor)
                if items.isEmpty { break }
                for item in items { context.delete(item) }
                if recoverable { try context.save() } else { break }
            }
        }
        try remove(Employee.self); try remove(Deduction.self)
        try remove(PayrollMonth.self); try remove(StaffMember.self)
        var destination = recoverable ? ModelContext(modelContainer) : modelContext
        destination.autosaveEnabled = false
        func flush() throws {
            try destination.save()
            if recoverable { destination = ModelContext(modelContainer); destination.autosaveEnabled = false }
        }
        for r in months {
            let m = PayrollMonth(name: r.name, employeeCount: r.employeeCount, deductionCount: r.deductionCount)
            m.importedAt = r.importedAt
            m.itemSummaryData = r.summary
            destination.insert(m)
        }
        try insertRecords("employees.jsonl", BackupFormat.EmployeeRecord.self, expected: employees) { i, r in
            let e = Employee(month: r.month, name: r.name, rank: r.rank, workplace: r.workplace, netSalary: r.net)
            e.itemsData = r.items
            e.detailsData = r.details
            e.rawColumnsData = r.rawColumns
            destination.insert(e)
            if recoverable && i % 256 == 255 { try flush() }
            if i % 1000 == 0 { progress?(0.6 * Double(i) / Double(max(1, employees.count))) }
        }
        try insertRecords("deductions.jsonl", BackupFormat.DeductionRecord.self, expected: deductions) { i, r in
            destination.insert(Deduction(month: r.month, employeeName: r.name, title: r.title, amount: r.amount, note: r.note, rank: r.rank))
            if recoverable && i % 256 == 255 { try flush() }
            if i % 1000 == 0 { progress?(0.6 + 0.1 * Double(i) / Double(max(1, deductions.count))) }
        }
        try insertRecords("staff.jsonl", BackupFormat.StaffRecord.self, expected: staff) { i, r in
            let s = StaffMember(name: r.name, mother: "", statNumber: r.stat, unifiedNumber: r.unified,
                                rank: r.rank, status: r.status, workplace: r.workplace, sections: [])
            s.motherKey = r.mother
            s.detailsData = r.details
            destination.insert(s)
            if recoverable && i % 256 == 255 { try flush() }
            if i % 1000 == 0 { progress?(0.7 + 0.3 * Double(i) / Double(max(1, staff.count))) }
        }
        previousBooks = try OfficialBookFiles.installPrepared(preparedBooks)
        booksInstalled = true
        try destination.save()
        ExecutionAccountStore.saveEncoded(accountData)
        if let previousBooks { try? FileManager.default.removeItem(at: previousBooks) }
        progress?(1)
        committed = true
        return settings.filter { BackupFormat.settingsKeys.contains($0.key) }
    }
}

/// النسخة تُحفظ في مجلد "نسخ الرواتب" داخل مجلد iCloud Drive يختاره المستخدم مرة واحدة.
/// (مجلد خاص بالتطبيق في iCloud يحتاج حساب مطوّر مدفوع، لذلك يُختار المجلد من نافذة الملفات.)
enum BackupDestination: String {
    case folder
}

enum BackupSchedule: String, CaseIterable {
    case off, hourly, daily

    var interval: TimeInterval? {
        switch self {
        case .off: nil
        case .hourly: 60 * 60
        case .daily: 24 * 60 * 60
        }
    }
}

@MainActor @Observable
final class BackupManager {
    static let shared = BackupManager()
    static let autoKey = "autoBackupEnabled"
    static let scheduleKey = "backupSchedule"
    /// رابط محفوظ (bookmark) لملف النسخة الذي حفظه المستخدم في iCloud Drive: كل نسخة جديدة تُكتب فوقه.
    private static let fileKey = "autoBackupFile"
    private static let folderKey = "autoBackupFolder"
    static let subfolder = "نسخ الرواتب"
    private static let lastKey = "autoBackupLast"
    private static let lastSizeKey = "autoBackupLastSize"
    private static let lastPlaceKey = "autoBackupLastPlace"
    private static let errorKey = "autoBackupLastError"
    private static let errorAtKey = "autoBackupLastErrorAt"
    /// آخر تغيير في البيانات (استيراد أو حذف): إذا كان بعد آخر نسخة ناجحة تُعاد المحاولة تلقائياً.
    private static let changedKey = "backupDataChangedAt"
    /// آخر تغيير شملته نسخة ناجحة أو أزاله استرجاع مؤكد؛ لا يغيّر تاريخ النسخة المعروض.
    private static let acknowledgedChangeKey = "backupAcknowledgedChangeAt"
    /// فهرس آخر نسخة ناجحة أو مسترجعة؛ لا يحتوي أسماء أشخاص أو مبالغ.
    private static let safetyManifestKey = "backupSafetyManifestV1"
    /// اختبار المحاكي: مسار ملف بدل iCloud.
    private static let testFileKey = "backupTestFile"

    var isWorking = false
    var message: String?
    /// تحذير محلي يعتمد على فهرس سابق، ولا يغيّر البيانات أو النسخة.
    var integrityWarning = false
    private var checkingIntegrity = false
    /// نسخة تنتظر تأكيد الاسترجاع.
    var pendingRestore: (folder: URL, manifest: BackupFormat.Manifest)?
    private var pendingServerRestore: (date: Date, address: String, token: String, uploadID: String?, deviceName: String?)?
    /// وقت رفع الملف الحالي كما سجله السيرفر، لا تاريخ الإنشاء داخل الملف.
    var pendingServerRestoreDate: Date? { pendingServerRestore?.date }
    var pendingServerRestoreDevice: String? { pendingServerRestore?.deviceName }
    var overwriteCandidate: BackupServerStatus?
    var version = 0
    /// القيم المستعادة تمنع احتساب إشعارات AppStorage المتأخرة كتعديل يدوي.
    private var restoredSettingValues: [String: Data] = [:]

    var lastBackup: Date? { UserDefaults.standard.object(forKey: Self.lastKey) as? Date }
    var hasUnbackedChanges: Bool {
        if BackupServerConfig.enabled && UserDefaults.standard.bool(forKey: BackupServerConfig.pendingKey) { return true }
        return Self.hasPendingChange(UserDefaults.standard.object(forKey: Self.changedKey) as? Date,
                                     acknowledged: UserDefaults.standard.object(forKey: Self.acknowledgedChangeKey) as? Date,
                                     lastBackup: lastBackup)
    }
    private static func hasPendingChange(_ changed: Date?, acknowledged: Date?, lastBackup: Date?) -> Bool {
        guard let changed else { return false }
        return changed > (acknowledged ?? lastBackup ?? .distantPast)
    }
    private static func hasRetainedSettings(backupKeys: Set<String>, localKeys: Set<String>) -> Bool {
        !localKeys.isSubset(of: backupKeys)
    }
    var lastSize: Int64 { Int64(UserDefaults.standard.integer(forKey: Self.lastSizeKey)) }
    var lastPlace: String { UserDefaults.standard.string(forKey: Self.lastPlaceKey) ?? "" }
    var lastError: String? { UserDefaults.standard.string(forKey: Self.errorKey) }
    var lastErrorAt: Date? { UserDefaults.standard.object(forKey: Self.errorAtKey) as? Date }
    static var autoEnabled: Bool { UserDefaults.standard.bool(forKey: autoKey) }
    nonisolated static var manualOnly: Bool {
        let choice = UserDefaults.standard.string(forKey: "backupDestinationChoice") ?? ""
        return !choice.isEmpty && choice != "server" && !BackupServerConfig.enabled
    }
    static var schedule: BackupSchedule {
        BackupSchedule(rawValue: UserDefaults.standard.string(forKey: scheduleKey) ?? "") ?? .off
    }

    /// "iCloud Drive › نسخ الرواتب"، أو nil إذا لم يُختر مكان.
    var folderName: String? {
        _ = version
        return resolveFile().map { Self.placeName($0.deletingLastPathComponent()) }
    }

    /// سطر الحالة في صفحة الإعدادات.
    var statusLine: String {
        _ = version
        if integrityWarning { return tr("⚠️ تعذّر التحقق من اكتمال البيانات", "⚠️ Data completeness could not be verified") }
        if Self.manualOnly { return tr("نسخ يدوي للخدمة المختارة", "Manual backup for the selected service") }
        if folderName == nil && !BackupServerConfig.ready { return tr("⚠️ اختر ملف النسخة الاحتياطية أو فعّل السيرفر", "⚠️ Choose a backup file or enable the server") }
        if lastError != nil { return tr("⚠️ فشل آخر نسخ، اضغط للتفاصيل", "⚠️ Last backup failed, tap for details") }
        if hasUnbackedChanges { return tr("⚠️ تغيّرت البيانات منذ آخر نسخة", "⚠️ Data changed since the last backup") }
        guard Self.autoEnabled || Self.schedule != .off else { return lastBackup == nil ? tr("متوقف", "Off") : tr("متوقف · آخر نسخة \(Self.relative(lastBackup!))", "Off · last \(Self.relative(lastBackup!))") }
        guard let lastBackup else { return tr("تلقائي · لم تُؤخذ نسخة بعد", "Auto · no backup yet") }
        let schedule = Self.schedule == .hourly ? tr("كل ساعة", "Hourly") : Self.schedule == .daily ? tr("كل يوم", "Daily") : tr("بعد الاستيراد", "After import")
        return tr("\(schedule) · \(Self.relative(lastBackup)) ✓", "\(schedule) · \(Self.relative(lastBackup)) ✓")
    }

    var statusColor: Color {
        _ = version
        if integrityWarning { return .orange }
        if (folderName == nil && !BackupServerConfig.ready) || lastError != nil { return .red }
        if hasUnbackedChanges { return .orange }
        return (Self.autoEnabled || Self.schedule != .off) && lastBackup != nil ? .green : .secondary
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

    /// تصدير نسخة مستقلة يفتح نافذة «حفظ» التي يختار منها المستخدم المكان.
    func chooseLocation() {
        guard !isWorking else { return }
        guard !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
            message = tr("انتظر انتهاء الاستيراد قبل إنشاء نسخة احتياطية.", "Wait for the import to finish before creating a backup.")
            return
        }
        isWorking = true
        let changeWatermark = UserDefaults.standard.object(forKey: Self.changedKey) as? Date
        Task {
            defer { isWorking = false; version += 1 }
            do {
                let (url, _) = try await makeBackup(title: tr("تجهيز النسخة", "Preparing backup"))
                defer { try? FileManager.default.removeItem(at: url) }
                let size = try Self.archiveSize(url)
                await FilePicker.waitUntilSettled()
                guard let saved = await FilePicker.exportBackup(url) else { return }
                let defaults = UserDefaults.standard
                // التصدير يحفظ نسخة، ولا يضمن صلاحية تحديثها بعد إغلاق نافذة الملفات.
                defaults.removeObject(forKey: Self.fileKey)
                defaults.removeObject(forKey: Self.folderKey)
                if !BackupServerConfig.ready {
                    defaults.set(Date(), forKey: Self.lastKey)
                    defaults.set(changeWatermark ?? Date.distantPast, forKey: Self.acknowledgedChangeKey)
                    defaults.set(Int(size), forKey: Self.lastSizeKey)
                    defaults.set(Self.placeName(saved.deletingLastPathComponent()), forKey: Self.lastPlaceKey)
                    defaults.removeObject(forKey: Self.errorKey)
                    defaults.removeObject(forKey: Self.errorAtKey)
                }
                BackupBackground.schedule()
                await FilePicker.waitUntilSettled()
                message = BackupServerConfig.ready
                    ? tr("حُفظت النسخة اليدوية؛ يبقى السيرفر وجهة النسخ التلقائي.", "Manual backup saved; the server remains the automatic destination.")
                    : tr("حُفظت النسخة في الملفات. تأكد من اكتمال رفعها إلى الخدمة المختارة؛ النسخ التلقائي غير مفعّل لهذه الوجهة.", "Backup saved in Files. Make sure its upload to the selected service completes; automatic backup is not enabled for this destination.")
            } catch {
                message = tr("تعذّر حفظ النسخة: \(error.localizedDescription)", "Couldn't save the backup: \(error.localizedDescription)")
            }
        }
    }

    private func resolveBookmark(_ key: String) -> URL? {
        guard let data = UserDefaults.standard.data(forKey: key) else { return nil }
        var stale = false
        guard let url = try? URL(resolvingBookmarkData: data, options: [], relativeTo: nil, bookmarkDataIsStale: &stale) else { return nil }
        if stale {
            let granted = url.startAccessingSecurityScopedResource()
            if let fresh = try? url.bookmarkData(options: .minimalBookmark, includingResourceValuesForKeys: nil, relativeTo: nil) {
                UserDefaults.standard.set(fresh, forKey: key)
            }
            if granted { url.stopAccessingSecurityScopedResource() }
        }
        return url
    }

    private func resolveFile() -> URL? {
        if let test = UserDefaults.standard.string(forKey: Self.testFileKey) { return URL(fileURLWithPath: test) }
        if let folder = resolveBookmark(Self.folderKey) { return folder.appendingPathComponent(BackupFormat.fileName()) }
        return resolveBookmark(Self.fileKey)
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
        let result = try await Task.detached(priority: .userInitiated) {
            try await BackupActor(modelContainer: container).export(settings: settings, progress: sink)
        }.value
        // حاوية جديدة فارغة بعد إعادة تثبيت التطبيق لا يجوز أن تمحو آخر نسخة صالحة.
        guard result.1.hasData else {
            try? FileManager.default.removeItem(at: result.0)
            throw BackupError.emptyData
        }
        return result
    }

    private static func archiveSize(_ url: URL) throws -> Int64 {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        let size = (attributes[.size] as? NSNumber)?.int64Value ?? 0
        guard size > 0 else { throw BackupError.emptyArchive }
        guard (try? Archive(url: url, accessMode: .read))?["manifest.json"] != nil else { throw BackupError.notBackup }
        return size
    }

    /// يمنع تحديث حاوية ناقصة من محو أشهر أو ملاك أو حسابات كانت في نسخة iCloud السابقة.
    nonisolated static func checkReplacement(_ current: BackupFormat.Manifest, at file: URL) throws {
        let attributes: [FileAttributeKey: Any]
        do { attributes = try FileManager.default.attributesOfItem(atPath: file.path) }
        catch let error as NSError where error.domain == NSCocoaErrorDomain && (error.code == NSFileReadNoSuchFileError || error.code == NSFileNoSuchFileError) { return }
        // حجم iCloud قد يكون صفراً لملف لم يُنزّل؛ لا نعتبر ذلك إذناً بمحو النسخة.
        if (attributes[.size] as? NSNumber)?.intValue == 0 { throw BackupError.unavailableArchive }
        var coordinationError: NSError?
        var readError: Error?
        var previous: BackupFormat.Manifest?
        NSFileCoordinator().coordinate(readingItemAt: file, options: [], error: &coordinationError) { target in
            do {
                guard let archive = try? Archive(url: target, accessMode: .read),
                      let entry = archive["manifest.json"], entry.uncompressedSize < 1_000_000 else {
                    throw BackupError.notBackup
                }
                var data = Data()
                _ = try archive.extract(entry) { data.append($0) }
                previous = try JSONDecoder().decode(BackupFormat.Manifest.self, from: data)
            } catch { readError = error }
        }
        if let error = coordinationError ?? readError { throw error }
        guard let previous else { throw BackupError.folderUnavailable }
        guard BackupFormat.supports(previous.version) else { throw BackupError.newerVersion }
        try checkMissingData(current, previous: previous)
    }

    nonisolated static func checkMissingData(_ current: BackupFormat.Manifest, previous: BackupFormat.Manifest) throws {
        let lostMonth = !Set(previous.months).isSubset(of: Set(current.months))
        let lostRows = previous.employees > 0 && current.employees == 0 ||
            previous.deductions > 0 && current.deductions == 0 ||
            previous.staff > 0 && current.staff == 0 ||
            (previous.accounts ?? 0) > 0 && (current.accounts ?? 0) == 0 ||
            (previous.books ?? 0) > 0 && (current.books ?? 0) == 0
        guard !lostMonth && !lostRows else { throw BackupError.dataLossRisk }
    }

    /// يسجل خط الأساس لأول مرة فقط بعد التأكد من وجود بيانات في القاعدة.
    func establishSafetyBaseline() async {
        guard UserDefaults.standard.data(forKey: Self.safetyManifestKey) == nil else { return }
        let container = SharedModel.container
        do {
            let manifest = try await Task.detached(priority: .utility) {
                try await BackupActor(modelContainer: container).safetyManifest()
            }.value
            guard UserDefaults.standard.data(forKey: Self.safetyManifestKey) == nil,
                  !manifest.months.isEmpty || manifest.employees > 0 || manifest.staff > 0 else { return }
            Self.rememberSafetyManifest(manifest)
            integrityWarning = false
        } catch { /* لا ننشئ خط أساس من قراءة ناقصة. */ }
    }

    /// فحص القراءة فقط عند فتح التطبيق؛ لا نعتمد نتيجة تزامنت مع استيراد أو استرجاع.
    func refreshIntegrityWarning() async {
        guard !checkingIntegrity, !isWorking, pendingRestore == nil,
              !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting,
              let data = UserDefaults.standard.data(forKey: Self.safetyManifestKey) else { return }
        guard let previous = try? JSONDecoder().decode(BackupFormat.Manifest.self, from: data) else {
            integrityWarning = true
            return
        }
        checkingIntegrity = true
        defer { checkingIntegrity = false }
        let originalVersion = version
        let staffVersion = StaffImportState.shared.version
        let container = SharedModel.container
        do {
            let current = try await Task.detached(priority: .utility) {
                try await BackupActor(modelContainer: container).safetyManifest()
            }.value
            guard !isWorking, pendingRestore == nil, !ImportCenter.shared.isBusy,
                  !StaffImportState.shared.isImporting, version == originalVersion,
                  StaffImportState.shared.version == staffVersion,
                  UserDefaults.standard.data(forKey: Self.safetyManifestKey) == data else { return }
            integrityWarning = (try? Self.checkMissingData(current, previous: previous)) == nil
        } catch {
            // لا نعتبر فشل القراءة دليلاً على سلامة البيانات.
            integrityWarning = true
        }
    }

    /// حماية مستقلة عن مزود التخزين، حتى لو فُقد الوصول للنسخة السابقة أو تغير السيرفر.
    private static func checkSafetyManifest(_ current: BackupFormat.Manifest) throws {
        guard let data = UserDefaults.standard.data(forKey: safetyManifestKey) else { return }
        let previous: BackupFormat.Manifest
        do { previous = try JSONDecoder().decode(BackupFormat.Manifest.self, from: data) }
        catch { throw BackupError.dataLossRisk }
        try checkMissingData(current, previous: previous)
    }

    private static func rememberSafetyManifest(_ manifest: BackupFormat.Manifest) {
        guard let data = try? JSONEncoder().encode(manifest) else { return }
        UserDefaults.standard.set(data, forKey: safetyManifestKey)
    }

    /// نسخة يدوية: نافذة المشاركة (Google Drive، OneDrive، واتساب، حفظ في الملفات).
    func shareNow() {
        guard !isWorking else { return }
        guard !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
            message = tr("انتظر انتهاء الاستيراد قبل إنشاء نسخة احتياطية.", "Wait for the import to finish before creating a backup.")
            return
        }
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
        version += 1
    }

    /// قد يصل إشعار إعداد مستعاد لاحقاً، حتى بعد انتهاء تطبيق قيم النسخة.
    func markSettingChanged(_ key: String) {
        if let restored = restoredSettingValues[key],
           Self.isRestoredSetting(UserDefaults.standard.object(forKey: key), equalTo: restored) { return }
        restoredSettingValues.removeValue(forKey: key)
        markChanged()
    }

    private static func isRestoredSetting(_ value: Any?, equalTo restored: Data) -> Bool {
        guard let value,
              let current = try? PropertyListSerialization.data(fromPropertyList: value, format: .binary, options: 0) else { return false }
        return current == restored
    }

    /// بيانات تغيّرت بعد آخر نسخة ناجحة أو حان موعد النسخة الدورية.
    var needsBackup: Bool {
        guard !Self.manualOnly, BackupServerConfig.ready || resolveFile() != nil else { return false }
        // لا نحاول كل دقيقة عند تعذّر الاتصال بمزود iCloud.
        if let lastErrorAt, Date().timeIntervalSince(lastErrorAt) < 10 * 60 { return false }
        let changed = UserDefaults.standard.object(forKey: Self.changedKey) as? Date
        let acknowledged = UserDefaults.standard.object(forKey: Self.acknowledgedChangeKey) as? Date
        let pendingChange = Self.hasPendingChange(changed, acknowledged: acknowledged, lastBackup: lastBackup) &&
            changed.map { Date().timeIntervalSince($0) >= 120 } == true
        let firstServerBackup = BackupServerConfig.ready && UserDefaults.standard.bool(forKey: BackupServerConfig.pendingKey)
        let afterImport = Self.autoEnabled && (pendingChange || lastError != nil)
        let periodic = Self.schedule.interval.map { Date().timeIntervalSince(lastBackup ?? .distantPast) >= $0 } ?? false
        return firstServerBackup || afterImport || periodic
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
        if Self.manualOnly { return tr("النسخ للخدمة المختارة يدوي؛ استخدم حفظ نسخة.", "The selected service uses manual backup; use Save backup.") }
        guard BackupServerConfig.ready || resolveFile() != nil else { return "⚠️ " + BackupError.noFolder.localizedDescription }
        return await backupToDestination()
    }

    /// يكتب النسخة فوق ملف النسخة المحفوظ في iCloud (نسخة واحدة دائماً) ويحفظ النتيجة نجاحاً أو فشلاً.
    @discardableResult
    func backupToDestination(manual: Bool = false, approved: BackupServerStatus? = nil) async -> String? {
        guard !isWorking else { return nil }
        guard !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
            return tr("⚠️ انتظر انتهاء الاستيراد قبل النسخ.", "⚠️ Wait for the import to finish before backing up.")
        }
        if Self.manualOnly { return tr("استخدم حفظ نسخة للخدمة المختارة.", "Use Save backup for the selected service.") }
        if BackupServerConfig.enabled { return await backupToServer(manual: manual, approved: approved) }
        guard let file = resolveFile() else {
            message = BackupError.noFolder.localizedDescription
            return "⚠️ " + BackupError.noFolder.localizedDescription
        }
        isWorking = true
        let changeWatermark = UserDefaults.standard.object(forKey: Self.changedKey) as? Date
        defer { isWorking = false; version += 1 }
        // يكمل النسخ حتى لو خرج المستخدم من التطبيق أثناءه.
        let task = UIApplication.shared.beginBackgroundTask(withName: "backup")
        defer { if task != .invalid { UIApplication.shared.endBackgroundTask(task) } }
        do {
            let (url, manifest) = try await makeBackup(title: tr("نسخ احتياطي", "Backing up"))
            defer { try? FileManager.default.removeItem(at: url) }
            try Self.checkSafetyManifest(manifest)
            let preparedSize = try Self.archiveSize(url)
            let scope = resolveBookmark(Self.folderKey) ?? file
            let (size, refreshedBookmark) = try await Task.detached(priority: .utility) { () throws -> (Int64, Data?) in
                let granted = scope.startAccessingSecurityScopedResource()
                defer { if granted { scope.stopAccessingSecurityScopedResource() } }
                try Self.checkReplacement(manifest, at: file)
                var coordinationError: NSError?
                var writeError: Error?
                var didWrite = false
                // تنسيق الاستبدال مع مزود الملفات يحافظ على صلاحية الوصول المختارة.
                NSFileCoordinator().coordinate(writingItemAt: file, options: .forReplacing, error: &coordinationError) { target in
                    do {
                        // تُكتب النسخة إلى ملف مجاور ثم تُستبدل دفعة واحدة؛ تبقى القديمة عند فشل الكتابة.
                        let contents = try Data(contentsOf: url, options: .mappedIfSafe)
                        guard !contents.isEmpty else { throw BackupError.emptyArchive }
                        try contents.write(to: target, options: .atomic)
                        didWrite = true
                    } catch { writeError = error }
                }
                if let error = coordinationError ?? writeError {
                    if !FileManager.default.fileExists(atPath: file.path) { throw BackupError.folderUnavailable }
                    throw error
                }
                guard didWrite else { throw BackupError.folderUnavailable }
                // حجم مزود iCloud الوصفي قد يبقى صفراً مؤقتاً بعد الكتابة؛ نعتمد حجم البيانات المكتوبة.
                let bookmark = try? file.bookmarkData(options: .minimalBookmark, includingResourceValuesForKeys: nil, relativeTo: nil)
                return (preparedSize, bookmark)
            }.value
            if UserDefaults.standard.data(forKey: Self.folderKey) == nil,
               UserDefaults.standard.string(forKey: Self.testFileKey) == nil,
               let refreshedBookmark {
                UserDefaults.standard.set(refreshedBookmark, forKey: Self.fileKey)
            }
            let place = Self.placeName(file.deletingLastPathComponent())
            let defaults = UserDefaults.standard
            defaults.set(Date(), forKey: Self.lastKey)
            defaults.set(changeWatermark ?? Date.distantPast, forKey: Self.acknowledgedChangeKey)
            defaults.set(Int(size), forKey: Self.lastSizeKey)
            defaults.set(place, forKey: Self.lastPlaceKey)
            Self.rememberSafetyManifest(manifest)
            integrityWarning = false
            defaults.removeObject(forKey: Self.errorKey)
            defaults.removeObject(forKey: Self.errorAtKey)
            BackupBackground.schedule()
            return tr("☁️ حُفظت نسخة احتياطية: \(place).", "☁️ Backup saved: \(place).")
        } catch {
            let reason = (error as? BackupError)?.localizedDescription ?? error.localizedDescription
            UserDefaults.standard.set(reason, forKey: Self.errorKey)
            UserDefaults.standard.set(Date(), forKey: Self.errorAtKey)
            return tr("⚠️ تعذّر النسخ الاحتياطي التلقائي: \(reason)", "⚠️ Auto backup failed: \(reason)")
        }
    }

    private func backupToServer(manual: Bool, approved: BackupServerStatus?) async -> String? {
        guard let base = BackupServerConfig.url(BackupServerConfig.address), BackupServerConfig.ready else {
            message = tr("تحقق من إعدادات السيرفر المحلي.", "Check local server settings.")
            return "⚠️ " + (message ?? "")
        }
        let address = BackupServerConfig.address
        let token = BackupServerConfig.token
        isWorking = true
        let changeWatermark = UserDefaults.standard.object(forKey: Self.changedKey) as? Date
        defer { isWorking = false; version += 1 }
        let task = UIApplication.shared.beginBackgroundTask(withName: "server-backup")
        defer { if task != .invalid { UIApplication.shared.endBackgroundTask(task) } }
        do {
            try await BackupServerUpload.checkReachability(base: base)
            let current = try await BackupServerUpload.status(base: base, token: token)
            guard BackupServerConfig.ready, BackupServerConfig.address == address, BackupServerConfig.token == token else { throw CancellationError() }
            let defaults = UserDefaults.standard
            if defaults.bool(forKey: BackupServerConfig.pendingKey), current.count > 0 {
                defaults.removeObject(forKey: BackupServerConfig.pendingKey)
                defaults.set(true, forKey: BackupServerConfig.firstChoiceKey)
                defaults.set(true, forKey: BackupServerConfig.conflictKey)
                let reason = tr("على السيرفر نسخة رُفعت \(current.description). لم يُرفع شيء فوقها؛ استرجعها أولاً.", "A server backup was uploaded \(current.description). Nothing was overwritten; restore it first.")
                defaults.set(reason, forKey: Self.errorKey)
                defaults.set(Date(), forKey: Self.errorAtKey)
                return reason
            }
            if current.newer && (approved == nil || approved?.latest != current.latest || approved?.uploadID != current.uploadID) {
                defaults.set(true, forKey: BackupServerConfig.conflictKey)
                let reason = tr("على السيرفر نسخة أحدث من جهاز آخر رُفعت \(current.description)؛ استرجعها من «استرجاع من السيرفر» قبل رفع نسخة هذا الجهاز.", "The server has a newer backup from another device uploaded \(current.description); restore it before uploading this device’s backup.")
                defaults.set(reason, forKey: Self.errorKey)
                defaults.set(Date(), forKey: Self.errorAtKey)
                if manual { overwriteCandidate = current }
                return reason
            }
            let (file, manifest) = try await makeBackup(title: tr("نسخ إلى السيرفر", "Backing up to server"))
            defer { try? FileManager.default.removeItem(at: file) }
            try Self.checkSafetyManifest(manifest)
            let expected = try Self.archiveSize(file)
            // أعد الفحص بعد تجهيز الملف؛ التأكيد اليدوي لا يشمل نسخة تغيّرت أثناء التجهيز.
            let beforeUpload = try await BackupServerUpload.status(base: base, token: token)
            guard manifest.version < 6 || beforeUpload.backupFormatGuard else {
                throw NSError(domain: "BackupServer", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("حدّث إضافة سيرفر النسخ قبل رفع نسخة تحتوي تعديل العقود، حتى لا تستبدلها نسخة قديمة.", "Update the backup server before uploading a backup with contract adjustments, to protect it from older clients.")])
            }
            guard BackupServerConfig.ready, BackupServerConfig.address == address, BackupServerConfig.token == token else { throw CancellationError() }
            if defaults.bool(forKey: BackupServerConfig.pendingKey), beforeUpload.count > 0 {
                defaults.removeObject(forKey: BackupServerConfig.pendingKey)
                defaults.set(true, forKey: BackupServerConfig.firstChoiceKey)
                defaults.set(true, forKey: BackupServerConfig.conflictKey)
                let reason = tr("على السيرفر نسخة رُفعت \(beforeUpload.description). لم يُرفع شيء فوقها؛ استرجعها أولاً.", "A server backup was uploaded \(beforeUpload.description). Nothing was overwritten; restore it first.")
                defaults.set(reason, forKey: Self.errorKey); defaults.set(Date(), forKey: Self.errorAtKey)
                return reason
            }
            if beforeUpload.newer && (approved == nil || approved?.latest != beforeUpload.latest || approved?.uploadID != beforeUpload.uploadID) {
                defaults.set(true, forKey: BackupServerConfig.conflictKey)
                let reason = tr("على السيرفر نسخة أحدث من جهاز آخر رُفعت \(beforeUpload.description)؛ استرجعها أولاً.", "The server has a newer backup from another device uploaded \(beforeUpload.description); restore it first.")
                defaults.set(reason, forKey: Self.errorKey); defaults.set(Date(), forKey: Self.errorAtKey)
                if manual { overwriteCandidate = beforeUpload }
                return reason
            }
            var cleanedName = String(UIDevice.current.name.components(separatedBy: .controlCharacters).joined().prefix(100))
            while cleanedName.utf8.count > 240 && !cleanedName.isEmpty { cleanedName.removeLast() }
            let deviceID = BackupServerConfig.deviceID
            let deviceName = "\(cleanedName.isEmpty ? "iPhone" : cleanedName) · \(deviceID.prefix(4).uppercased())"
            let receipt = try await BackupServerUpload.upload(file: file, base: base, token: token, deviceID: deviceID, deviceName: deviceName)
            let size = receipt.size
            guard size == expected else { throw BackupError.emptyArchive }
            guard BackupServerConfig.ready, BackupServerConfig.address == address, BackupServerConfig.token == token else { throw CancellationError() }
            if let uploaded = receipt.uploaded, let uploadID = receipt.uploadID {
                BackupServerConfig.remember(uploaded, uploadID: uploadID)
            }
            defaults.removeObject(forKey: BackupServerConfig.conflictKey)
            defaults.removeObject(forKey: BackupServerConfig.firstChoiceKey)
            defaults.set(Date(), forKey: Self.lastKey)
            defaults.set(changeWatermark ?? Date.distantPast, forKey: Self.acknowledgedChangeKey)
            defaults.set(Int(size), forKey: Self.lastSizeKey)
            defaults.set(tr("السيرفر المحلي", "Local server"), forKey: Self.lastPlaceKey)
            Self.rememberSafetyManifest(manifest)
            integrityWarning = false
            defaults.removeObject(forKey: BackupServerConfig.pendingKey)
            defaults.removeObject(forKey: Self.errorKey)
            defaults.removeObject(forKey: Self.errorAtKey)
            BackupBackground.schedule()
            return tr("حُفظت النسخة في السيرفر المحلي.", "Backup saved on the local server.")
        } catch {
            UserDefaults.standard.set(error.localizedDescription, forKey: Self.errorKey)
            UserDefaults.standard.set(Date(), forKey: Self.errorAtKey)
            return tr("تعذّر النسخ إلى السيرفر: \(error.localizedDescription)", "Server backup failed: \(error.localizedDescription)")
        }
    }

    /// يفحص عند إتاحة وقت في الخلفية؛ لا يعتمد على تفعيل جدول النسخ.
    func checkServerNotification() async {
        let defaults = UserDefaults.standard
        guard BackupServerConfig.ready, defaults.bool(forKey: BackupServerConfig.notifyKey), !isWorking,
              !defaults.bool(forKey: BackupServerConfig.pendingKey),
              let base = BackupServerConfig.url(BackupServerConfig.address) else { return }
        let address = BackupServerConfig.address, token = BackupServerConfig.token
        do {
            let status = try await BackupServerUpload.status(base: base, token: token)
            guard !Task.isCancelled, !isWorking, BackupServerConfig.ready, defaults.bool(forKey: BackupServerConfig.notifyKey),
                  BackupServerConfig.address == address, BackupServerConfig.token == token,
                  status.newer, status.deviceID != BackupServerConfig.deviceID, let latest = status.latest else { return }
            defaults.set(true, forKey: BackupServerConfig.conflictKey)
            defaults.set(tr("على السيرفر نسخة أحدث من جهاز آخر رُفعت \(status.description)؛ استرجعها من «استرجاع من السيرفر» قبل رفع نسخة هذا الجهاز.", "The server has a newer backup from another device uploaded \(status.description); restore it before uploading this device’s backup."), forKey: Self.errorKey)
            defaults.set(Date(), forKey: Self.errorAtKey)
            version += 1
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            let identifier = status.uploadID ?? formatter.string(from: latest)
            guard defaults.string(forKey: BackupServerConfig.notifiedKey) != identifier else { return }
            let center = UNUserNotificationCenter.current()
            let authorization = await center.notificationSettings().authorizationStatus
            guard authorization == .authorized || authorization == .provisional || authorization == .ephemeral else { return }
            let content = UNMutableNotificationContent()
            content.title = tr("نسخة أحدث على السيرفر", "Newer backup on server")
            content.body = tr("رُفعت نسخة: \(status.description). افتح النسخ الاحتياطي لمراجعتها.", "Backup uploaded: \(status.description). Open Backup to review it.")
            content.sound = .default
            content.userInfo = ["route": "backup"]
            try await center.add(UNNotificationRequest(identifier: "backup-server-newer", content: content, trigger: nil))
            if BackupServerConfig.address == address && BackupServerConfig.token == token {
                defaults.set(identifier, forKey: BackupServerConfig.notifiedKey)
            }
        } catch { /* انقطاع السيرفر لا يُنشئ إشعاراً كاذباً. */ }
    }

    func restoreFromServer() {
        guard !isWorking, BackupServerConfig.ready, let base = BackupServerConfig.url(BackupServerConfig.address) else { return }
        let address = BackupServerConfig.address
        let token = BackupServerConfig.token
        isWorking = true
        Task {
            defer { isWorking = false; version += 1 }
            do {
                let status = try await BackupServerUpload.status(base: base, token: token)
                guard let latest = status.latest else { throw BackupError.notBackup }
                var request = URLRequest(url: base.appendingPathComponent("v1/backups/latest"))
                request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
                request.timeoutInterval = 600
                let (temporary, response) = try await URLSession.shared.download(for: request)
                guard let http = response as? HTTPURLResponse, http.statusCode == 200 else { throw BackupError.notBackup }
                let file = FileManager.default.temporaryDirectory.appendingPathComponent("server-restore-\(UUID().uuidString).zip")
                try FileManager.default.moveItem(at: temporary, to: file)
                defer { try? FileManager.default.removeItem(at: file) }
                // لا ننسب التنزيل لتاريخ نسخة تغيرت أثناء النقل.
                let after = try await BackupServerUpload.status(base: base, token: token)
                guard after.latest == latest, after.uploadID == status.uploadID, BackupServerConfig.address == address, BackupServerConfig.token == token else {
                    throw BackupServerUpload.failure(0, fallback: tr("تغيّرت نسخة السيرفر أثناء التنزيل؛ أعد المحاولة.", "The server backup changed during download; try again."))
                }
                await open(file, serverRestore: (latest, address, token, status.uploadID, status.deviceName))
            } catch {
                message = tr("تعذّر تنزيل النسخة من السيرفر: \(error.localizedDescription)", "Could not download the server backup: \(error.localizedDescription)")
            }
        }
    }

    /// اختبار المحاكي (-autoBackupTest YES): ملف تجريبي بدل iCloud، نسختان فوق نفس الملف.
    func autoBackupSelfTest() async {
        // لا تعتمد نتيجة الاختبار على خدمة النسخ التي اختارتها اختبارات الواجهة السابقة.
        let savedDestination = UserDefaults.standard.object(forKey: "backupDestinationChoice")
        UserDefaults.standard.removeObject(forKey: "backupDestinationChoice")
        defer {
            if let savedDestination { UserDefaults.standard.set(savedDestination, forKey: "backupDestinationChoice") }
            else { UserDefaults.standard.removeObject(forKey: "backupDestinationChoice") }
        }
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let folder = docs.appendingPathComponent("icloud-test", isDirectory: true).appendingPathComponent(Self.subfolder, isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent(BackupFormat.fileName())
        UserDefaults.standard.set(file.path, forKey: Self.testFileKey)
        UserDefaults.standard.set(true, forKey: Self.autoKey)
        let line = await autoBackup() ?? "nil"
        let firstSize = (try? FileManager.default.attributesOfItem(atPath: file.path)[.size] as? NSNumber)?.intValue ?? 0
        let again = await backupToDestination() ?? "nil"
        let replacementSize = (try? FileManager.default.attributesOfItem(atPath: file.path)[.size] as? NSNumber)?.intValue ?? 0
        // نسخة دورية بلا استيراد جديد: الموعد وحده يكفي لتشغيلها.
        UserDefaults.standard.set(BackupSchedule.hourly.rawValue, forKey: Self.scheduleKey)
        UserDefaults.standard.set(Date().addingTimeInterval(-2 * 60 * 60), forKey: Self.lastKey)
        let periodicDue = needsBackup
        await autoBackupIfNeeded()
        let periodicSize = (try? FileManager.default.attributesOfItem(atPath: file.path)[.size] as? NSNumber)?.intValue ?? 0
        let count = ((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []).count
        let valid = (try? Archive(url: file, accessMode: .read))?["manifest.json"] != nil
        // اختبار مقارنة تاريخ السيرفر دون ملفات حقيقية، مع إعادة القيمة السابقة بعد الاختبار.
        let savedSeen = UserDefaults.standard.string(forKey: BackupServerConfig.seenKey)
        let savedSeenUpload = UserDefaults.standard.string(forKey: BackupServerConfig.seenUploadKey)
        UserDefaults.standard.removeObject(forKey: BackupServerConfig.seenUploadKey)
        let serverDate = BackupServerUpload.parseDate("2026-01-01T00:00:00.123456+00:00")
        UserDefaults.standard.removeObject(forKey: BackupServerConfig.seenKey)
        let noSeenIsNewer = BackupServerStatus(count: 1, size: 1, free: 1, latest: serverDate).newer
        if let serverDate { BackupServerConfig.remember(serverDate) }
        let sameIsNewer = BackupServerStatus(count: 1, size: 1, free: 1, latest: serverDate).newer
        let laterIsNewer = BackupServerStatus(count: 1, size: 1, free: 1, latest: serverDate?.addingTimeInterval(3)).newer
        let uploadOne = UUID().uuidString.lowercased(), uploadTwo = UUID().uuidString.lowercased()
        if let serverDate { BackupServerConfig.remember(serverDate, uploadID: uploadOne) }
        let sameUploadIsNewer = BackupServerStatus(count: 1, size: 1, free: 1, latest: serverDate, uploadID: uploadOne).newer
        let differentUploadIsNewer = BackupServerStatus(count: 1, size: 1, free: 1, latest: serverDate, uploadID: uploadTwo).newer
        if let savedSeen { UserDefaults.standard.set(savedSeen, forKey: BackupServerConfig.seenKey) }
        else { UserDefaults.standard.removeObject(forKey: BackupServerConfig.seenKey) }
        if let savedSeenUpload { UserDefaults.standard.set(savedSeenUpload, forKey: BackupServerConfig.seenUploadKey) }
        else { UserDefaults.standard.removeObject(forKey: BackupServerConfig.seenUploadKey) }
        let serverDatesOK = serverDate != nil && noSeenIsNewer && !sameIsNewer && laterIsNewer && !sameUploadIsNewer && differentUploadIsNewer
        let trustOK = SalaryTrustSelfTest.run()
        let trustPDFOK = await SalaryTrustSelfTest.pdf()
        let passed = firstSize > 0 && replacementSize > 0 && periodicSize > 0 && lastSize == Int64(periodicSize)
            && periodicDue && valid && lastError == nil && count == 1 && serverDatesOK && trustOK && trustPDFOK
        LaunchOptions.writeReport("autobackup=\(passed ? "ok" : "failed") serverDates=\(serverDatesOK) trust=\(trustOK) trustPDF=\(trustPDFOK) size=\(periodicSize) recorded=\(lastSize) due=\(periodicDue) zip=\(valid) files=\(count) line=\(line) again=\(again)")
    }

    // MARK: - الاسترجاع

    func pickRestore() {
        FilePicker.present(types: [.zip]) { [weak self] urls in
            guard let self, let url = urls.first else { return }
            Task { await self.open(url) }
        }
    }

    func openForRestore(_ url: URL) async { await open(url) }

    nonisolated static func extractForRestore(_ url: URL) async throws -> URL {
        try await Task.detached(priority: .userInitiated) { () throws -> URL in
            let granted = url.startAccessingSecurityScopedResource()
            defer { if granted { url.stopAccessingSecurityScopedResource() } }

                let dir = FileManager.default.temporaryDirectory.appendingPathComponent("restore-\(UUID().uuidString)", isDirectory: true)
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                var extracted = false
                defer { if !extracted { try? FileManager.default.removeItem(at: dir) } }
                guard let archive = try? Archive(url: url, accessMode: .read) else { throw BackupError.notBackup }
                // الملفات المعروفة فقط، مع فحص سلامة الضغط قبل عرض تأكيد الاسترجاع.
                for name in ["manifest.json", "settings.json", "months.jsonl", "employees.jsonl", "deductions.jsonl", "staff.jsonl"] {
                    guard let entry = archive[name], entry.type == .file else { throw BackupError.notBackup }
                    if name == "manifest.json" && entry.uncompressedSize > 1_000_000 { throw BackupError.notBackup }
                    if name == "settings.json" && entry.uncompressedSize > 64_000_000 { throw BackupError.notBackup }
                    _ = try archive.extract(entry, to: dir.appendingPathComponent(name))
                }
                if let entry = archive["accounts.jsonl"], entry.type == .file {
                    _ = try archive.extract(entry, to: dir.appendingPathComponent("accounts.jsonl"))
                }
                let manifest = try JSONDecoder().decode(BackupFormat.Manifest.self,
                    from: Data(contentsOf: dir.appendingPathComponent("manifest.json")))
                guard BackupFormat.supports(manifest.version) else { throw BackupError.newerVersion }
                guard manifest.hasData else { throw BackupError.emptyRestore }
                if manifest.version >= 3 {
                    guard let expected = manifest.books, expected >= 0 && expected <= 10_000,
                          let index = archive["books/index.json"], index.type == .file,
                          index.uncompressedSize < 10_000_000 else { throw BackupError.notBackup }
                    let booksDir = dir.appendingPathComponent("books", isDirectory: true)
                    try FileManager.default.createDirectory(at: booksDir, withIntermediateDirectories: true)
                    _ = try archive.extract(index, to: booksDir.appendingPathComponent("index.json"))
                    let books = try OfficialBookFiles.read(in: booksDir)
                    guard books.count == expected, Set(books.map(\.id)).count == expected else { throw BackupError.notBackup }
                    for book in books {
                        let name = "books/\(book.id.uuidString).pdf"
                        guard let entry = archive[name], entry.type == .file,
                              entry.uncompressedSize < 60_000_000 else { throw BackupError.notBackup }
                        _ = try archive.extract(entry, to: OfficialBookFiles.file(book.id, in: booksDir))
                    }
                    _ = try OfficialBookFiles.validateArchive(in: booksDir, expected: expected)
                }
                ImportCenter.removeCopy(url)
                extracted = true
                return dir

        }.value
    }

    private func open(_ url: URL, serverRestore: (date: Date, address: String, token: String, uploadID: String?, deviceName: String?)? = nil) async {
        do {
            let folder = try await Self.extractForRestore(url)
            let manifest = try JSONDecoder().decode(BackupFormat.Manifest.self, from: Data(contentsOf: folder.appendingPathComponent("manifest.json")))
            guard BackupFormat.supports(manifest.version) else { throw BackupError.newerVersion }
            if manifest.version >= 2, !FileManager.default.fileExists(atPath: folder.appendingPathComponent("accounts.jsonl").path) {
                throw BackupError.notBackup
            }
            if manifest.version >= 3, !FileManager.default.fileExists(atPath: folder.appendingPathComponent("books/index.json").path) {
                throw BackupError.notBackup
            }
            await FilePicker.waitUntilSettled()
            // تُضبط بيانات السيرفر قبل إظهار نافذة الاسترجاع لتظهر مع الملف الصحيح.
            pendingServerRestore = serverRestore
            pendingRestore = (folder, manifest)
        } catch {
            message = error.localizedDescription
        }
    }

    func confirmRestore() {
        guard let pending = pendingRestore else { return }
        guard !isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
            cancelRestore()
            message = tr("انتظر انتهاء العملية الحالية، ثم اختر النسخة مجدداً للاسترجاع.", "Wait for the current operation to finish, then select the backup again to restore.")
            return
        }
        pendingRestore = nil
        let serverRestore = pendingServerRestore
        pendingServerRestore = nil
        isWorking = true
        let changeWatermark = UserDefaults.standard.object(forKey: Self.changedKey) as? Date
        Task {
            defer { isWorking = false; version += 1; try? FileManager.default.removeItem(at: pending.folder) }
            let progress = ImportProgress.shared
            progress.begin(tr("استرجاع النسخة", "Restoring backup"))
            let sink = progress.sink(0, 1, tr("استبدال البيانات", "Replacing data"))
            let container = SharedModel.container
            do {
                let settings = try await Task.detached(priority: .userInitiated) {
                    try await BackupActor(modelContainer: container).restore(from: pending.folder, progress: sink, recoverable: true)
                }.value
                BankStatementCache.shared.invalidate()
                restoredSettingValues = settings.reduce(into: [:]) { result, item in
                    if let value = try? PropertyListSerialization.propertyList(from: item.value, format: nil),
                       let normalized = try? PropertyListSerialization.data(fromPropertyList: value, format: .binary, options: 0) {
                        result[item.key] = normalized
                    }
                }
                for key in [SalaryTrust.settingsKey, "pendingTrustPayrollMonths", "pendingTrustPayrollStarted"] where settings[key] == nil {
                    UserDefaults.standard.removeObject(forKey: key)
                }
                for (key, data) in settings {
                    if let value = try? PropertyListSerialization.propertyList(from: data, format: nil) {
                        UserDefaults.standard.set(value, forKey: key)
                    }
                }
                UserDefaults.standard.synchronize()
                try RestoreSafety.finish()
                for key in ["homeStatsCache", "homeOverviewCache", "homeAuditCache"] { UserDefaults.standard.removeObject(forKey: key) }
                Self.rememberSafetyManifest(pending.manifest)
                if let serverRestore, BackupServerConfig.address == serverRestore.address, BackupServerConfig.token == serverRestore.token {
                    BackupServerConfig.remember(serverRestore.date, uploadID: serverRestore.uploadID)
                    // لا نعدّ الإعدادات المحلية التي لم تشملها النسخة محفوظة فيها.
                    let localKeys = Set(BackupFormat.settingsKeys.filter { UserDefaults.standard.object(forKey: $0) != nil })
                    let retained = Self.hasRetainedSettings(backupKeys: Set(settings.keys), localKeys: localKeys)
                    if !retained {
                        UserDefaults.standard.set(changeWatermark ?? Date.distantPast, forKey: Self.acknowledgedChangeKey)
                    }
                    UserDefaults.standard.removeObject(forKey: BackupServerConfig.pendingKey)
                    UserDefaults.standard.removeObject(forKey: BackupServerConfig.firstChoiceKey)
                    UserDefaults.standard.removeObject(forKey: BackupServerConfig.conflictKey)
                    UserDefaults.standard.removeObject(forKey: Self.errorKey)
                    UserDefaults.standard.removeObject(forKey: Self.errorAtKey)
                }
                integrityWarning = false
                await StaffImportState.shared.refreshStoredCount()
                await SearchIndex.shared.invalidate()
                await SmartGroupCatalog.shared.invalidate()
                await AuditResults.shared.invalidate()
                OfficialBookStore.shared.reload()
                StaffImportState.shared.version += 1
                progress.end()
                message = tr("تم الاسترجاع: \(pending.manifest.months.count) أشهر، \(Num.format(pending.manifest.staff)) في الملاك.",
                             "Restored: \(pending.manifest.months.count) months, \(pending.manifest.staff) staff.")
            } catch {
                if RestoreSafety.pending { SharedModel.health.restartRequired = true }
                BankStatementCache.shared.invalidate()
                progress.end()
                message = tr("تعذّر الاسترجاع: \(error.localizedDescription)", "Restore failed: \(error.localizedDescription)")
            }
        }
    }

    /// اختبار المحاكي (-backupTest YES): نسخ ثم استرجاع ثم مقارنة الأعداد.
    func selfTest() async {
        func counts() -> String {
            let context = ModelContext(SharedModel.container)
            let e = (try? context.fetchCount(FetchDescriptor<Employee>())) ?? -1
            let d = (try? context.fetchCount(FetchDescriptor<Deduction>())) ?? -1
            let m = (try? context.fetchCount(FetchDescriptor<PayrollMonth>())) ?? -1
            let s = (try? context.fetchCount(FetchDescriptor<StaffMember>())) ?? -1
            let a = (try? ExecutionAccountStore.load().count) ?? -1
            return "employees=\(e) deductions=\(d) months=\(m) staff=\(s) accounts=\(a)"
        }
        let before = counts()
        #if targetEnvironment(simulator)
        let previousTemplate = UserDefaults.standard.object(forKey: CustomTransferTemplate.settingsKey)
        let previousUnified = UserDefaults.standard.object(forKey: TransferLetterCustomizationStore.settingsKey)
        defer {
            if let previousTemplate { UserDefaults.standard.set(previousTemplate, forKey: CustomTransferTemplate.settingsKey) }
            else { UserDefaults.standard.removeObject(forKey: CustomTransferTemplate.settingsKey) }
            if let previousUnified { UserDefaults.standard.set(previousUnified, forKey: TransferLetterCustomizationStore.settingsKey) }
            else { UserDefaults.standard.removeObject(forKey: TransferLetterCustomizationStore.settingsKey) }
        }
        var fixtureTemplate = CustomTransferTemplate()
        fixtureTemplate.title = "قالب وهمي للنسخة"
        fixtureTemplate.body = "مبلغ وهمي {{المبلغ}}"
        guard let fixtureTemplateData = try? CustomTransferTemplate.encode([fixtureTemplate]) else {
            LaunchOptions.writeReport("backup=failed customTemplateSetup=failed"); return
        }
        UserDefaults.standard.set(fixtureTemplateData, forKey: CustomTransferTemplate.settingsKey)
        var unifiedFixture = CustomTransferTemplate()
        unifiedFixture.title = "كتاب وهمي"; unifiedFixture.header = "ترويسة وهمية"; unifiedFixture.number = "عدد وهمي"
        unifiedFixture.recipient = "جهة وهمية"; unifiedFixture.subject = "موضوع وهمي"; unifiedFixture.footer = "هامش وهمي"
        unifiedFixture.body = TransferLetterBody.template(for: "food_tax") ?? ""
        unifiedFixture.firstSigner = "صفة وهمية\nاسم وهمي\nمنصب وهمي"; unifiedFixture.signatureHorizontalOffset = 10
        var unifiedStore = TransferLetterCustomizationStore()
        unifiedStore.apply(.init(letterTemplate: unifiedFixture), to: "food_tax")
        guard let unifiedFixtureData = try? JSONEncoder().encode(unifiedStore) else {
            LaunchOptions.writeReport("backup=failed unifiedTemplateSetup=failed"); return
        }
        UserDefaults.standard.set(unifiedFixtureData, forKey: TransferLetterCustomizationStore.settingsKey)
        #endif
        do {
            let old = Date(timeIntervalSince1970: 100), newer = Date(timeIntervalSince1970: 200)
            let restored = try PropertyListSerialization.data(fromPropertyList: "قيمة وهمية", format: .binary, options: 0)
            guard !Self.hasPendingChange(old, acknowledged: old, lastBackup: .distantPast),
                  Self.hasPendingChange(newer, acknowledged: old, lastBackup: newer),
                  !Self.hasPendingChange(old, acknowledged: nil, lastBackup: newer),
                  Self.hasPendingChange(newer, acknowledged: nil, lastBackup: old),
                  !Self.hasRetainedSettings(backupKeys: ["a", "b"], localKeys: ["a"]),
                  Self.hasRetainedSettings(backupKeys: ["a"], localKeys: ["a", "b"]),
                  Self.isRestoredSetting("قيمة وهمية", equalTo: restored),
                  !Self.isRestoredSetting("تعديل وهمي", equalTo: restored),
                  !Self.isRestoredSetting(nil, equalTo: restored) else {
                LaunchOptions.writeReport("backup=failed changeWatermark=failed")
                return
            }
            let previous = BackupFormat.Manifest(version: 4, createdAt: .now, months: ["شهر تجريبي"], employees: 2, deductions: 1, staff: 2, accounts: 1, books: 1)
            var lostMonth = previous; lostMonth.months = []
            var lostEmployees = previous; lostEmployees.employees = 0
            var lostDeductions = previous; lostDeductions.deductions = 0
            var lostStaff = previous; lostStaff.staff = 0
            var lostAccounts = previous; lostAccounts.accounts = 0
            var lostBooks = previous; lostBooks.books = 0
            let missingCases = [lostMonth, lostEmployees, lostDeductions, lostStaff, lostAccounts, lostBooks]
            guard missingCases.allSatisfy({ (try? Self.checkMissingData($0, previous: previous)) == nil }),
                  (try? Self.checkMissingData(previous, previous: previous)) != nil else {
                LaunchOptions.writeReport("backup=failed missingDataGuard=failed")
                return
            }
            let start = Date()
            let (url, testManifest) = try await makeBackup(title: "test")
            #if targetEnvironment(simulator)
            guard testManifest.version == 10 else { LaunchOptions.writeReport("backup=failed unifiedTemplateVersion=failed"); return }
            #endif
            let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.intValue ?? 0
            // وقت السيرفر مستقل عن تاريخ الملف، ويختفي عند إلغاء الاختيار أو فتح ملف محلي.
            let serverCopy = FileManager.default.temporaryDirectory.appendingPathComponent("server-time-test-\(UUID().uuidString).zip")
            try FileManager.default.copyItem(at: url, to: serverCopy)
            let testServerDate = Date(timeIntervalSinceReferenceDate: 800_000_000)
            await open(serverCopy, serverRestore: (testServerDate, "test-address", "test-token", nil, nil))
            guard pendingRestore != nil, pendingServerRestoreDate == testServerDate else {
                cancelRestore(); LaunchOptions.writeReport("backup=failed serverTime=failed"); return
            }
            cancelRestore()
            guard pendingServerRestoreDate == nil else { LaunchOptions.writeReport("backup=failed serverTime=failed"); return }
            await open(url)
            guard pendingServerRestoreDate == nil, let pending = pendingRestore else { LaunchOptions.writeReport("backup=failed open \(message ?? "")"); return }
            // سجل تالف متأخر يجب ألا يحذف أو يغيّر البيانات الحالية.
            let employeesFile = pending.folder.appendingPathComponent("employees.jsonl")
            let original = try Data(contentsOf: employeesFile)
            var damaged = original
            damaged.append(contentsOf: "\n{invalid-json}\n".utf8)
            try damaged.write(to: employeesFile)
            let container = SharedModel.container
            var rejected = false
            do {
                _ = try await Task.detached {
                    try await BackupActor(modelContainer: container).restore(from: pending.folder, progress: nil)
                }.value
            } catch { rejected = true }
            let preserved = rejected && counts() == before
            try original.write(to: employeesFile)
            guard preserved else {
                cancelRestore()
                LaunchOptions.writeReport("backup=failed corruptRestore=failed")
                return
            }
            // مرجع كتاب تالف يجب أن يُرفض قبل استبدال أي بيانات رواتب أو كتب.
            let settingsFile = pending.folder.appendingPathComponent("settings.json")
            let originalSettings = try Data(contentsOf: settingsFile)
            var damagedSettings = try JSONDecoder().decode([String: Data].self, from: originalSettings)
            damagedSettings[TransferReferencePack.settingsKey] = try PropertyListSerialization.data(fromPropertyList: Data("invalid reference".utf8), format: .binary, options: 0)
            try JSONEncoder().encode(damagedSettings).write(to: settingsFile)
            var referenceRejected = false
            do {
                _ = try await Task.detached {
                    try await BackupActor(modelContainer: container).restore(from: pending.folder, progress: nil)
                }.value
            } catch { referenceRejected = true }
            let referencesPreserved = referenceRejected && counts() == before
            try originalSettings.write(to: settingsFile)
            guard referencesPreserved else {
                cancelRestore()
                LaunchOptions.writeReport("backup=failed corruptReferenceGuard=failed")
                return
            }
            #if targetEnvironment(simulator)
            // قالب تالف يجب رفضه قبل استبدال البيانات، ثم استرجاع النسخة السليمة.
            var damagedTemplateSettings = try JSONDecoder().decode([String: Data].self, from: originalSettings)
            damagedTemplateSettings[CustomTransferTemplate.settingsKey] = try PropertyListSerialization.data(fromPropertyList: Data("invalid template".utf8), format: .binary, options: 0)
            try JSONEncoder().encode(damagedTemplateSettings).write(to: settingsFile)
            var templateRejected = false
            do {
                _ = try await Task.detached {
                    try await BackupActor(modelContainer: container).restore(from: pending.folder, progress: nil)
                }.value
            } catch { templateRejected = true }
            try originalSettings.write(to: settingsFile)
            guard templateRejected, counts() == before else {
                cancelRestore(); LaunchOptions.writeReport("backup=failed corruptTemplateGuard=failed"); return
            }
            var damagedUnifiedSettings = try JSONDecoder().decode([String: Data].self, from: originalSettings)
            damagedUnifiedSettings[TransferLetterCustomizationStore.settingsKey] = try PropertyListSerialization.data(fromPropertyList: Data("invalid unified template".utf8), format: .binary, options: 0)
            try JSONEncoder().encode(damagedUnifiedSettings).write(to: settingsFile)
            var unifiedRejected = false
            do {
                _ = try await Task.detached {
                    try await BackupActor(modelContainer: container).restore(from: pending.folder, progress: nil)
                }.value
            } catch { unifiedRejected = true }
            try originalSettings.write(to: settingsFile)
            guard unifiedRejected, counts() == before else {
                cancelRestore(); LaunchOptions.writeReport("backup=failed corruptUnifiedTemplateGuard=failed"); return
            }
            #endif
            confirmRestore()
            while isWorking || pendingRestore != nil { try? await Task.sleep(for: .milliseconds(200)) }
            #if targetEnvironment(simulator)
            guard UserDefaults.standard.data(forKey: CustomTransferTemplate.settingsKey) == fixtureTemplateData else {
                LaunchOptions.writeReport("backup=failed customTemplateRestore=failed"); return
            }
            guard UserDefaults.standard.data(forKey: TransferLetterCustomizationStore.settingsKey) == unifiedFixtureData else {
                LaunchOptions.writeReport("backup=failed unifiedTemplateRestore=failed"); return
            }
            #endif
            let after = counts()
            LaunchOptions.writeReport("backup=\(before == after ? "ok" : "mismatch") corruptRestore=ok corruptReferenceGuard=ok serverTime=ok before[\(before)] after[\(after)] size=\(size) seconds=\(Int(Date().timeIntervalSince(start)))")
        } catch {
            LaunchOptions.writeReport("backup=failed \(error.localizedDescription)")
        }
    }

    func cancelRestore() {
        pendingServerRestore = nil
        if let folder = pendingRestore?.folder { try? FileManager.default.removeItem(at: folder) }
        pendingRestore = nil
    }
}

// MARK: - نوافذ الملفات للنسخ

extension FilePicker {
    /// يُصدّر الملف ليختار المستخدم مكان الحفظ من نافذة الملفات.
    static func exportBackup(_ url: URL) async -> URL? {
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
        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
            let url = urls.first
            controller.dismiss(animated: true) { self.finish(url) }
        }
        func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
            controller.dismiss(animated: true) { self.finish(nil) }
        }
    }
}

// MARK: - صفحة النسخ الاحتياطي

struct BackupView: View {
    @State private var manager = BackupManager.shared
    @State private var progress = ImportProgress.shared
    @AppStorage(BackupManager.autoKey) private var autoEnabled = true
    @AppStorage(BackupManager.scheduleKey) private var scheduleRaw = BackupSchedule.off.rawValue
    @State private var showFolderHelp = false
    @AppStorage(BackupServerConfig.enabledKey) private var serverEnabled = false
    @State private var chosenProvider = "iCloud Drive"
    @AppStorage("backupDestinationChoice") private var backupChoice = ""

    var body: some View {
        let _ = serverEnabled
        List {
            Section {
                statusCard
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(Color.clear)
            } footer: {
                Text(tr("قبل أي تحديث من AppStar تأكد أن آخر نسخة ناجحة بتاريخ اليوم. إذا احتاج المثبّت إلى إعادة التثبيت فقد يفقد iOS حاوية البيانات المحلية؛ يمكن استرجاعها من النسخة الاحتياطية.",
                        "Before updating through AppStar, confirm a successful backup today. If the installer reinstalls the app, iOS may lose local data; restore it from the backup."))
            }

            Section {
                Menu {
                    ForEach(["iCloud Drive", "OneDrive", "Google Drive"], id: \.self) { provider in
                        Button(provider) {
                            backupChoice = provider
                            chosenProvider = provider
                            serverEnabled = false
                            BackupBackground.schedule()
                        }
                    }
                } label: {
                    LabeledContent(tr("خدمة النسخ", "Backup service"), value: backupChoice.isEmpty ? tr("اختر خدمة", "Choose service") : backupChoice)
                }
                .accessibilityIdentifier("chooseICloudFolder")
                if !backupChoice.isEmpty {
                    Button(tr("حفظ نسخة في \(backupChoice)", "Save backup to \(backupChoice)"), systemImage: "cloud") {
                        chosenProvider = backupChoice
                        showFolderHelp = true
                    }
                }
                if !BackupManager.manualOnly {
                Toggle(tr("نسخ تلقائي بعد كل استيراد", "Auto backup after each import"), isOn: $autoEnabled)
                    .accessibilityIdentifier("autoBackupToggle")
                Picker(tr("نسخ دوري", "Periodic backup"), selection: $scheduleRaw) {
                    Text(tr("متوقف", "Off")).tag(BackupSchedule.off.rawValue)
                    Text(tr("كل ساعة", "Every hour")).tag(BackupSchedule.hourly.rawValue)
                    Text(tr("كل يوم", "Every day")).tag(BackupSchedule.daily.rawValue)
                }
                .pickerStyle(.menu)
                .accessibilityIdentifier("backupSchedulePicker")
                .onChange(of: scheduleRaw) {
                    manager.version += 1
                    BackupBackground.schedule()
                    Task { await manager.autoBackupIfNeeded() }
                }
                Button {
                    Task { await manager.backupToDestination(manual: true) }
                } label: {
                    HStack {
                        Text(tr("نسخ الآن", "Back up now"))
                        Spacer()
                        if manager.isWorking { ProgressView() }
                    }
                }
                .disabled(manager.isWorking || BackupManager.manualOnly || (!BackupServerConfig.ready && manager.folderName == nil))
                .accessibilityIdentifier("backupToDestination")
                }
            } header: {
                Text(tr("حفظ نسخة احتياطية", "Save a backup"))
            } footer: {
                Text(tr("اختر «حفظ نسخة احتياطية» لحفظ نسخة يدوياً في تطبيق الملفات. لا يُفعّل الحفظ اليدوي النسخ التلقائي، الذي يحتاج صلاحية مستمرة للمكان.",
                        "Choose Save a backup to save manually in Files. Manual export does not enable automatic backup, which requires ongoing access to the destination."))
            }

            Section {
                Menu {
                    ForEach(["iCloud Drive", "OneDrive", "Google Drive"], id: \.self) { provider in
                        Button(provider) { manager.pickRestore() }
                    }
                    Button(tr("الملفات على الآيفون", "Files on iPhone")) { manager.pickRestore() }
                } label: {
                    Label(tr("استرجاع نسخة من…", "Restore a backup from…"), systemImage: "arrow.counterclockwise")
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
                Text(tr("على جهاز آخر: افتح «استرجاع من نسخة» واختر الملف من الخدمة المناسبة في تطبيق الملفات.",
                        "On another device, open Restore and select your backup from its provider in Files."))
            }
        }
        .onAppear {
            if backupChoice.isEmpty || backupChoice == "server" { backupChoice = "iCloud Drive" }
        }
        .navigationTitle(tr("النسخ الاحتياطي", "Backup"))
        .navigationBarTitleDisplayMode(.inline)
        .alert(tr("حفظ نسخة احتياطية", "Save a backup"), isPresented: $showFolderHelp) {
            Button(tr("متابعة", "Continue")) { manager.chooseLocation() }
            Button(tr("إلغاء", "Cancel"), role: .cancel) {}
        } message: {
            Text(tr("ستظهر نافذة «حفظ» بعد تجهيز النسخة. اختر \(chosenProvider) والمجلد المناسب. لا يستبدل التصدير النسخ السابقة إلا إذا اخترت ذلك في تطبيق الملفات.",
                    "The Save window opens after preparing the backup. Choose \(chosenProvider) and a folder. Existing backups are replaced only if you choose to do so in Files."))
        }
    }

    /// آخر نسخة ناجحة، أو سبب فشل آخر محاولة، أو التقدّم أثناء النسخ.
    @ViewBuilder
    private var statusCard: some View {
        let _ = manager.version
        let failed = manager.lastError != nil && !BackupManager.manualOnly
        let pending = manager.integrityWarning || manager.hasUnbackedChanges || BackupManager.manualOnly || (manager.folderName == nil && !BackupServerConfig.ready)
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: manager.isWorking ? "arrow.triangle.2.circlepath" : failed || pending ? "exclamationmark.triangle.fill" : manager.lastBackup == nil ? "clock" : "checkmark")
                .font(.title3.weight(.bold))
                .foregroundStyle(.white)
                .frame(width: 44, height: 44)
                .background((manager.isWorking ? Color.blue : failed ? .red : pending ? .orange : manager.lastBackup == nil ? .gray : .green).gradient,
                            in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            VStack(alignment: .leading, spacing: 4) {
                if manager.isWorking && progress.isActive {
                    Text(progress.title).font(.headline)
                    ProgressView(value: progress.fraction)
                    Text(progress.detail).font(.caption).foregroundStyle(.secondary)
                } else {
                    if let last = manager.lastBackup {
                        Text(tr("آخر نسخة: \(Self.stamp(last))", "Last backup: \(Self.stamp(last))")).font(.headline)
                        Text("\(BackupFormat.fileName()) · \(manager.lastSize > 0 ? Num.bytes(manager.lastSize) : tr("الحجم غير متاح", "Size unavailable"))\n\(manager.lastPlace)")
                            .font(.caption).foregroundStyle(.secondary)
                    } else {
                        Text(tr("لم تُؤخذ نسخة بعد", "No backup yet")).font(.headline)
                    }
                    if manager.integrityWarning {
                        Text(tr("تعذّر التأكد من اكتمال البيانات مقارنة بالفهرس السابق. افحص البيانات والنسخة المحفوظة قبل الاسترجاع أو الاستيراد.", "Data could not be confirmed complete against the previous index. Check your data and saved backup before restoring or importing."))
                            .font(.caption.weight(.semibold)).foregroundStyle(.orange)
                    } else if let error = manager.lastError, !BackupManager.manualOnly {
                        Text(tr("فشلت آخر محاولة\(manager.lastErrorAt.map { " (" + Self.stamp($0) + ")" } ?? ""): \(error)",
                                "Last attempt failed: \(error)"))
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.red)
                        Button(tr("إعادة المحاولة", "Try again")) { Task { await manager.backupToDestination(manual: true) } }
                            .font(.subheadline.weight(.semibold))
                    } else if BackupManager.manualOnly || (manager.folderName == nil && !BackupServerConfig.ready) {
                        Text(tr("النسخ التلقائي غير مرتبط بمكان حفظ. يمكنك حفظ نسخة يدوياً.", "Automatic backup is not linked to a destination. You can save a backup manually."))
                            .font(.caption.weight(.semibold)).foregroundStyle(.orange)
                    } else if pending {
                        Text(tr("هناك بيانات أحدث من آخر نسخة. انتظر اكتمال النسخ أو اضغط «نسخ الآن» قبل تحديث التطبيق.",
                                "Newer data hasn't been backed up. Wait for backup or tap Back up now before updating the app."))
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.orange)
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
