import SwiftUI
import UserNotifications
import SwiftData
import UIKit
import UniformTypeIdentifiers
import ZIPFoundation
import CryptoKit

/// النسخ الاحتياطي: ملف zip واحد فيه كل البيانات (الأشهر، الرواتب، الاستقطاعات، الملاك، الكتب المصوّرة) والإعدادات.
/// يختار المستخدم مكان حفظه في نافذة «الملفات» عند كل تصدير.
nonisolated enum BackupFormat {
    // تمنع الإصدارات القديمة من استرجاع نسخة تحوي حسابات محلية ثم إسقاطها بصمت.
    static let version = 12
    /// الإصدار ٧ صيغة رواتب تجريبية مختلفة ولم يُفعّل استرجاعها.
    static func supports(_ version: Int) -> Bool { (1...6).contains(version) || version == 8 || version == 9 || version == 10 || version == 11 || version == 12 || version == 13 || version == 14 }
    static func exportVersion(for settings: [String: Data]) -> Int {
        // تمنع الصيغة الجديدة إسقاط تاريخ الصرف أو تعديل دفتر الملف بتطبيق قديم.
        if let raw = settings[SalaryTrust.settingsKey],
           let data = try? PropertyListSerialization.propertyList(from: raw, format: nil) as? Data,
           let ledger = try? SalaryTrust.decode(data), ledger.version == 2 { return 11 }
        if let raw = settings[TransferLetterCustomizationStore.settingsKey],
           let data = try? PropertyListSerialization.propertyList(from: raw, format: nil) as? Data, !data.isEmpty {
            guard let store = try? JSONDecoder().decode(TransferLetterCustomizationStore.self, from: data) else { return 10 }
            if store.hasUnifiedTemplates { return 10 }
        }
        return settings[CustomTransferTemplate.settingsKey] != nil ? 9 : (settings[FundingApproval.settingsKey] != nil ? 8 : (settings[ContractOverrides.settingsKey] == nil ? 5 : 6))
    }
    static func validateTrust(_ ledger: SalaryTrust.Ledger?, manifest: Manifest) throws {
        if let ledger {
            guard manifest.salaryTrustEntries == nil || manifest.salaryTrustEntries == ledger.entries.count,
                  ledger.version < 2 || manifest.dataVersion == 11,
                  manifest.dataVersion != 11 || (ledger.version == 2 && manifest.salaryTrustEntries == ledger.entries.count) else { throw BackupError.notBackup }
        } else if manifest.dataVersion == 11 || (manifest.salaryTrustEntries ?? 0) > 0 { throw BackupError.notBackup }
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
        var salaryTrustEntries: Int? = nil
        var contractBatchCount: Int? = nil
        var contractExecutionMarkers: [String]? = nil
        var refundBatchCount: Int? = nil
        var hasRefundDraft: Bool? = nil
        var contentVersion: Int? = nil
        var payrollDigest: Data? = nil
        var supportingDigest: Data? = nil

        func entryLimit(_ path: String) -> UInt64 {
            switch path {
            case "manifest.json": return 1_000_000
            case "refunds.jsonl": return 512 * 1024 * 1024
            case "settings.json": return 64_000_000
            case "books/index.json": return 9_999_999
            case "payroll-headers.json": return 48 * 1024 * 1024
            case let name where name.hasPrefix("books/"): return 60_000_000
            default: return .max
            }
        }
        var dataVersion: Int { version >= 12 ? contentVersion ?? -1 : version }
        func validateFormat() throws {
            guard BackupFormat.supports(version) else { throw BackupError.newerVersion }
            if version == 14 {
                guard let refundBatchCount, (0...10_000).contains(refundBatchCount), hasRefundDraft != nil else { throw BackupError.notBackup }
            } else if refundBatchCount != nil || hasRefundDraft != nil { throw BackupError.notBackup }
            if version >= 12 {
                guard [5, 6, 8, 9, 10, 11].contains(dataVersion), payrollDigest?.count == 32, supportingDigest?.count == 32 else { throw BackupError.notBackup }
            }
        }
        var hasData: Bool {
            !months.isEmpty || employees > 0 || deductions > 0 || staff > 0 ||
            (accounts ?? 0) > 0 || (books ?? 0) > 0 || (salaryTrustEntries ?? 0) > 0 || (contractBatchCount ?? 0) > 0 ||
            (refundBatchCount ?? 0) > 0 || hasRefundDraft == true
        }
    }

    struct MonthRecord: Codable { var name: String; var importedAt: Date; var employeeCount: Int; var deductionCount: Int; var summary: Data? }
    struct EmployeeRecord: Codable { var month: String; var name: String; var rank: String; var workplace: String; var net: Double?; var items: Data?; var details: Data?; var rawColumns: Data?; var headerIndex: Int? = nil; var headerOffset: Int? = nil }
    struct DeductionRecord: Codable { var month: String; var name: String; var title: String; var amount: Double?; var note: String; var rank: String }
    struct StaffRecord: Codable { var name: String; var mother: String; var stat: String; var unified: String; var rank: String; var status: String; var workplace: String; var details: Data }
    struct AccountRecord: Codable { var entity: String; var iban: String; var branch: String; var swift: String; var localAccount: String? = nil }

    /// إعدادات تُحفظ مع البيانات (بدون قفل Face ID).
    static let settingsKeys = [ContractTransferArchive.key, "homePinnedEntityNames", "homeHeadlineDeductions", "homeHiddenWorkReports", "homePinnedWorkItems", "staffCount", "staffImportedAt", "staffFileName", "staffStatuses", "unifiedRecentSearches",
                               "autoBackupEnabled", "backupSchedule", "homeTileLayout",
                               SalaryTrust.settingsKey, FundingApproval.settingsKey, CustomTransferTemplate.settingsKey, "bankStatementMonth", "bankStatementAccountName", "bankStatementIBAN", "bankStatementBranch", "bankStatementLocalAccount", "bankStatementManualAmounts", ContractOverrides.settingsKey, BankReconciliation.settingsKey, TransferRules.settingsKey, "transferPayrollAccountLinks", TransferIssuance.settingsKey, TransferReferencePack.settingsKey, TransferLetterCustomizationStore.settingsKey, TransferLetterEntityLinks.settingsKey, TransferHeaderFont.boldSettingsKey, TransferHeaderFont.regularSettingsKey,
                               ReportSheet.headerKey, AppSettings.languageKey, AppSettings.appearanceKey, AppSettings.numeralsKey, AppSettings.textSizeKey, "windowsContentTextLevel"]

    static let workspaceSettingsKeys = settingsKeys.filter { $0 != AppSettings.appearanceKey }

    static func removeExport(_ file: URL) {
        let parent = file.deletingLastPathComponent()
        guard file.lastPathComponent == fileName(), parent.lastPathComponent.hasPrefix("payroll-export-"),
              parent.deletingLastPathComponent().standardizedFileURL == FileManager.default.temporaryDirectory.standardizedFileURL else { return }
        try? FileManager.default.removeItem(at: parent)
    }

    /// اسم ثابت: كل نسخة جديدة تحلّ محل السابقة (تاريخ النسخة محفوظ داخلها).
    static func fileName() -> String { "\(prefix).zip" }
}

enum BackupError: LocalizedError {
    case notBackup, unavailableArchive, newerVersion, folderUnavailable, noFolder, emptyArchive, emptyData, emptyRestore, dataLossRisk, sourceChanged
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
        case .sourceChanged: tr("تغيّرت البيانات أثناء تجهيز النسخة. أعد النسخ بعد اكتمال التعديلات.", "The data changed while preparing the backup. Retry once the changes are complete.")
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
        let batchArchive = try ContractTransferArchive.read(UserDefaults.standard.data(forKey: ContractTransferArchive.key) ?? Data())
        let refunds = try RefundBackup.inventory(at: RefundStorage.local.root)
        return BackupFormat.Manifest(version: refunds.count == nil ? BackupFormat.version : 14, createdAt: .now, months: months,
                                     employees: employees, deductions: deductions, staff: staff,
                                     accounts: try ExecutionAccountStore.load().count,
                                     books: try OfficialBookFiles.read().count,
                                     salaryTrustEntries: try SalaryTrust.decode(UserDefaults.standard.data(forKey: SalaryTrust.settingsKey) ?? Data()).entries.count,
                                     contractBatchCount: batchArchive.records.count, contractExecutionMarkers: batchArchive.executionMarkers,
                                     refundBatchCount: refunds.count, hasRefundDraft: refunds.draft)
    }

    /// يكتب ملف النسخة (zip) ويعيد رابطه ووصفه.
    func export(settings: [String: Data], progress: ProgressSink?) throws -> (URL, BackupFormat.Manifest) {
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("backup-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: dir) }
        let encoder = JSONEncoder()

        var expectedDigests: [String: Data] = [:]
        func writeLines<S: Sequence>(_ name: String, _ items: S, count: Int, from: Double, to: Double) throws where S.Element: Encodable {
            let url = dir.appendingPathComponent(name)
            _ = fm.createFile(atPath: url.path, contents: nil)
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            var hash = SHA256()
            var buffer = Data()
            for (i, item) in items.enumerated() {
                buffer.append(try encoder.encode(item))
                buffer.append(0x0A)
                if buffer.count > 1_000_000 { hash.update(data: buffer); try handle.write(contentsOf: buffer); buffer.removeAll(keepingCapacity: true) }
                if i % 2000 == 0 { progress?(from + (to - from) * Double(i) / Double(max(1, count))) }
            }
            hash.update(data: buffer)
            try handle.write(contentsOf: buffer)
            try handle.close()
            expectedDigests[name] = Data(hash.finalize())
        }

        let months = try modelContext.fetch(FetchDescriptor<PayrollMonth>())
        try writeLines("months.jsonl", months.lazy.map {
            BackupFormat.MonthRecord(name: $0.name, importedAt: $0.importedAt, employeeCount: $0.employeeCount,
                                     deductionCount: $0.deductionCount, summary: $0.itemSummaryData)
        }, count: months.count, from: 0, to: 0.02)
        func writeModels<T: PersistentModel, R: Encodable>(_ type: T.Type, file: String, from: Double, to: Double, convert: (T) throws -> R) throws -> Int {
            let count = try modelContext.fetchCount(FetchDescriptor<T>())
            let target = dir.appendingPathComponent(file)
            _ = fm.createFile(atPath: target.path, contents: nil)
            let handle = try FileHandle(forWritingTo: target)
            defer { try? handle.close() }
            var hash = SHA256()
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
                        hash.update(data: data)
                        try handle.write(contentsOf: data)
                    }
                    offset += items.count
                }
                progress?(from + (to - from) * Double(offset) / Double(max(1, count)))
            }
            try handle.close()
            expectedDigests[file] = Data(hash.finalize())
            return count
        }
        var payroll = CompactPayrollBackup.Writer()
        let employeeCount = try writeModels(Employee.self, file: "employees.jsonl", from: 0.02, to: 0.4) {
            try payroll.pack(BackupFormat.EmployeeRecord(month: $0.month, name: $0.name, rank: $0.rank, workplace: $0.workplace, net: $0.netSalary, items: $0.itemsData, details: $0.detailsData, rawColumns: $0.rawColumnsData))
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
        var trustCount: Int?
        if let serialized = settings[SalaryTrust.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            let ledger = try SalaryTrust.decode(data)
            try SalaryTrust.validateBooks(ledger, available: Set(books.map(\.id)))
            trustCount = ledger.entries.count
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
        try JSONEncoder().encode(payroll.headers).write(to: dir.appendingPathComponent(CompactPayrollBackup.schemaFile))
        let batchArchive = try ContractTransferArchive.fromSettings(settings)
        let settingsFile = try JSONEncoder().encode(settings)
        guard settingsFile.count <= 64_000_000 else { throw CheckedArchive.Failure.tooLarge }
        try settingsFile.write(to: dir.appendingPathComponent("settings.json"))
        guard books.count <= 10_000 else { throw CheckedArchive.Failure.tooLarge }
        let refunds = try RefundBackup.write(from: RefundStorage.local.root, to: dir.appendingPathComponent(RefundBackup.file))
        let includesRefunds = refunds.present
        let baseVersion = settings[ContractTransferArchive.key] == nil ? BackupFormat.version : 13
        let supporting = BackupArchiveVerification.supportingPaths(bookIDs: books.map(\.id), refunds: includesRefunds)
        let manifest = BackupFormat.Manifest(version: includesRefunds ? 14 : baseVersion, createdAt: .now,
                                             months: months.sorted { $0.sortKey > $1.sortKey }.map(\.name),
                                             employees: employeeCount, deductions: deductionCount, staff: staffCount,
                                             accounts: accounts.count, books: books.count, salaryTrustEntries: trustCount,
                                             contractBatchCount: batchArchive.records.count, contractExecutionMarkers: batchArchive.executionMarkers,
                                             refundBatchCount: includesRefunds ? refunds.count : nil, hasRefundDraft: includesRefunds ? refunds.draft : nil,
                                             contentVersion: formatVersion, payrollDigest: payroll.fingerprint.digest,
                                             supportingDigest: try BackupArchiveVerification.supportingDigest(in: dir, paths: supporting, expected: expectedDigests))
        try JSONEncoder().encode(manifest).write(to: dir.appendingPathComponent("manifest.json"))
        let outputDirectory = fm.temporaryDirectory.appendingPathComponent("payroll-export-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
        let out = outputDirectory.appendingPathComponent(BackupFormat.fileName())
        let files = ["manifest.json", "employees.jsonl"] + supporting
        do {
            try BackupArchiveVerification.create(at: out, source: dir, files: files, manifest: manifest)
        } catch {
            try? fm.removeItem(at: outputDirectory)
            throw error
        }
        progress?(1)
        return (out, manifest)
    }

    /// يستبدل كل البيانات بمحتوى النسخة. يعيد الإعدادات المحفوظة فيها.
    func restore(from folder: URL, progress: ProgressSink?, recoverable: Bool = false, validateOnly: Bool = false) throws -> [String: Data] {
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
        try manifest.validateFormat()
        guard manifest.hasData else { throw BackupError.emptyRestore }
        if manifest.version >= 12 {
            let archivedBooks = try OfficialBookFiles.read(in: folder.appendingPathComponent("books"))
            let paths = BackupArchiveVerification.supportingPaths(bookIDs: archivedBooks.map(\.id), refunds: manifest.version == 14)
            guard try BackupArchiveVerification.supportingDigest(in: folder, paths: paths) == manifest.supportingDigest else {
                throw BackupError.notBackup
            }
        }
        let months = try records("months.jsonl", BackupFormat.MonthRecord.self)
        let employeeDigest = try CompactPayrollBackup.read(in: folder, manifest: manifest)
        let deductions = try validate("deductions.jsonl", BackupFormat.DeductionRecord.self)
        let staff = try validate("staff.jsonl", BackupFormat.StaffRecord.self)
        let accounts: [BackupFormat.AccountRecord]
        if manifest.version >= 2 {
            accounts = try records("accounts.jsonl", BackupFormat.AccountRecord.self)
        } else {
            accounts = []
        }
        guard months.map(\.name).sorted() == manifest.months.sorted(),
              deductions.count == manifest.deductions,
              staff.count == manifest.staff, manifest.version < 2 || manifest.accounts != nil,
              accounts.count == (manifest.accounts ?? 0) else { throw BackupError.notBackup }
        let settings = try decoder.decode([String: Data].self, from: Data(contentsOf: folder.appendingPathComponent("settings.json")))
        if manifest.version >= 12, manifest.dataVersion != BackupFormat.exportVersion(for: settings) { throw BackupError.notBackup }
        for data in settings.values { _ = try PropertyListSerialization.propertyList(from: data, format: nil) }
        let batches = try ContractTransferArchive.fromSettings(settings)
        guard (manifest.version == 14 || ((manifest.version == 13) == !batches.records.isEmpty)), manifest.contractBatchCount ?? 0 == batches.records.count,
              manifest.contractExecutionMarkers ?? [] == batches.executionMarkers else { throw BackupError.notBackup }
        let existing = try ContractTransferArchive.read(UserDefaults.standard.data(forKey: ContractTransferArchive.key) ?? Data())
        try batches.checkRestore(over: existing)

        if let serialized = settings[FundingApproval.settingsKey] {
            guard (manifest.dataVersion == 8 || manifest.dataVersion == 9 || manifest.dataVersion == 10 || manifest.dataVersion == 11), let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try FundingApproval.decode(data)
        } else if manifest.dataVersion == 8 { throw BackupError.notBackup }
        if let serialized = settings[CustomTransferTemplate.settingsKey] {
            guard (manifest.dataVersion == 9 || manifest.dataVersion == 10 || manifest.dataVersion == 11), let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try CustomTransferTemplate.decode(data)
        } else if manifest.dataVersion == 9 { throw BackupError.notBackup }
        if let serialized = settings[TransferLetterCustomizationStore.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            let templates = try (data.isEmpty ? TransferLetterCustomizationStore() : JSONDecoder().decode(TransferLetterCustomizationStore.self, from: data))
            try templates.validateUnifiedTemplates()
            if templates.hasUnifiedTemplates && manifest.dataVersion < 10 { throw BackupError.notBackup }
            if !templates.hasUnifiedTemplates && manifest.dataVersion == 10 { throw BackupError.notBackup }
        } else if manifest.dataVersion == 10 { throw BackupError.notBackup }
        if settings[ContractOverrides.settingsKey] == nil,
           let current = try ContractOverrides.currentData(), !(try ContractOverrides.decode(current)).isEmpty {
            throw NSError(domain: "ContractOverride", code: 3, userInfo: [NSLocalizedDescriptionKey: tr("النسخة المختارة لا تحتوي تعديلات العقود الموجودة على الجهاز. احفظ نسخة شاملة وراجع التعديلات قبل الاسترجاع.", "The selected backup does not contain this device’s contract adjustments. Save a full backup and review the adjustments before restoring.")])
        }
        if let serialized = settings[ContractOverrides.settingsKey] {
            guard manifest.dataVersion >= 6, let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            _ = try ContractOverrides.decode(data)
        }
        if settings[SalaryTrust.settingsKey] == nil,
           let saved = UserDefaults.standard.data(forKey: SalaryTrust.settingsKey),
           !(try SalaryTrust.decode(saved)).entries.isEmpty {
            throw SalaryTrust.invalid(tr("النسخة المختارة لا تحتوي دفتر الأمانات الموجود على الجهاز. لم تُستبدل البيانات؛ احفظ نسخة شاملة أولاً واسترجع نسخة تحتوي الدفتر.", "The selected backup does not contain this device’s trust ledger. Data was not replaced; save a full backup and restore one containing the ledger."))
        }
        let restoredTrust: SalaryTrust.Ledger?
        if let serialized = settings[SalaryTrust.settingsKey] {
            guard let data = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw BackupError.notBackup }
            restoredTrust = try SalaryTrust.decode(data)
        } else { restoredTrust = nil }
        try BackupFormat.validateTrust(restoredTrust, manifest: manifest)
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

        let preparedRefunds: URL?
        if let count = manifest.refundBatchCount, let draft = manifest.hasRefundDraft {
            preparedRefunds = try RefundBackup.prepare(from: folder.appendingPathComponent(RefundBackup.file),
                                                       beside: RefundStorage.local.root, count: count, draft: draft)
        } else { preparedRefunds = nil }
        defer { if let preparedRefunds { try? FileManager.default.removeItem(at: preparedRefunds) } }

        if validateOnly { return settings }
        if recoverable {
            guard let store = modelContainer.configurations.first?.url else { throw BackupError.folderUnavailable }
            try RestoreSafety.prepare(store: store, includeRefunds: preparedRefunds != nil)
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
        let insertedDigest = try CompactPayrollBackup.read(in: folder, manifest: manifest) { i, r in
            let e = Employee(month: r.month, name: r.name, rank: r.rank, workplace: r.workplace, netSalary: r.net)
            e.itemsData = r.items
            e.detailsData = r.details
            e.rawColumnsData = r.rawColumns
            destination.insert(e)
            if recoverable && i % 256 == 255 { try flush() }
            if i % 1000 == 0 { progress?(0.6 * Double(i) / Double(max(1, manifest.employees))) }
        }
        guard insertedDigest == employeeDigest else { throw BackupError.notBackup }
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
        if let preparedRefunds {
            try RefundPayrollSearch.relinkDraft(in: RefundStorage(root: preparedRefunds), context: destination)
            try RefundBackup.install(preparedRefunds, at: RefundStorage.local.root)
        }
        ExecutionAccountStore.saveEncoded(accountData)
        if let previousBooks { try? FileManager.default.removeItem(at: previousBooks) }
        progress?(1)
        committed = true
        return settings.filter { BackupFormat.settingsKeys.contains($0.key) }
    }
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
    private static let sizesKey = "backupSectionSizes"
    private static let previousSizesKey = "previousBackupSectionSizes"
    private static let previousSizeKey = "previousBackupFileSize"
    var sizes: BackupSizes? { Self.savedSizes(Self.sizesKey) }
    var previousSizes: BackupSizes? { Self.savedSizes(Self.previousSizesKey) }
    var previousSize: Int64 { Int64(UserDefaults.standard.integer(forKey: Self.previousSizeKey)) }
    private static func savedSizes(_ key: String) -> BackupSizes? {
        UserDefaults.standard.data(forKey: key).flatMap { try? JSONDecoder().decode(BackupSizes.self, from: $0) }
    }
    private static func recordSizes(_ sizes: BackupSizes) {
        let defaults = UserDefaults.standard
        defaults.set(defaults.data(forKey: sizesKey), forKey: previousSizesKey)
        defaults.set(defaults.integer(forKey: lastSizeKey), forKey: previousSizeKey)
        defaults.set(try? JSONEncoder().encode(sizes), forKey: sizesKey)
        defaults.set(Int(sizes.total), forKey: lastSizeKey)
    }
    private static let lastSizeKey = "autoBackupLastSize"
    private static let lastPlaceKey = "autoBackupLastPlace"
    private static let errorKey = "autoBackupLastError"
    private static let errorAtKey = "autoBackupLastErrorAt"
    /// آخر تغيير في البيانات (استيراد أو حذف): إذا كان بعد آخر نسخة ناجحة تُعاد المحاولة تلقائياً.
    private static let changedKey = "backupDataChangedAt"
    private static let appearanceChangedKey = "backupAppearanceChangedAt"
    private static var backupChangeDate: Date? {
        [changedKey, appearanceChangedKey].compactMap { UserDefaults.standard.object(forKey: $0) as? Date }.max()
    }
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
        return Self.hasPendingChange(Self.backupChangeDate,
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
        #if targetEnvironment(simulator)
        if UserDefaults.standard.bool(forKey: "autoBackupTest") { return false }
        #endif
        return !BackupServerConfig.enabled
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
        guard let lastBackup else { return tr("لم تُحفظ نسخة بعد", "No backup saved yet") }
        if hasUnbackedChanges { return tr("تغيّرت البيانات منذ آخر نسخة", "Data changed since the last backup") }
        return tr("آخر نسخة: \(Self.relative(lastBackup))", "Last backup: \(Self.relative(lastBackup))")
    }

    var statusColor: Color {
        _ = version
        if integrityWarning || (lastBackup != nil && hasUnbackedChanges) { return .orange }
        return lastBackup == nil ? .secondary : .green
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
        let changeWatermark = Self.backupChangeDate
        Task {
            defer { isWorking = false; version += 1 }
            do {
                let (url, _) = try await makeBackup(title: tr("تجهيز النسخة", "Preparing backup"))
                defer { BackupFormat.removeExport(url) }
                let sizes = try await Task.detached { try BackupSizes.read(url) }.value
                await FilePicker.waitUntilSettled()
                guard let saved = await FilePicker.exportBackup(url) else { return }
                let defaults = UserDefaults.standard
                // التصدير يحفظ نسخة، ولا يضمن صلاحية تحديثها بعد إغلاق نافذة الملفات.
                defaults.removeObject(forKey: Self.fileKey)
                defaults.removeObject(forKey: Self.folderKey)
                if !BackupServerConfig.ready {
                    defaults.set(Date(), forKey: Self.lastKey)
                    defaults.set(changeWatermark ?? Date.distantPast, forKey: Self.acknowledgedChangeKey)
                    Self.recordSizes(sizes)
                    defaults.set(Self.placeName(saved.deletingLastPathComponent()), forKey: Self.lastPlaceKey)
                    defaults.removeObject(forKey: Self.errorKey)
                    defaults.removeObject(forKey: Self.errorAtKey)
                }
                BackupBackground.schedule()
                await FilePicker.waitUntilSettled()
                message = tr("حُفظت النسخة في المكان الذي اخترته.", "Backup saved to the location you chose.")
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

    private func makeBackup(title: String, forCloudTransfer: Bool = false) async throws -> (URL, BackupFormat.Manifest) {
        RefundDraft.shared.flushAmount()
        if let error = RefundDraft.shared.storageError { throw NSError(domain: "RefundBackup", code: 1, userInfo: [NSLocalizedDescriptionKey: error]) }
        let revision = Self.backupChangeDate
        let staffRevision = StaffImportState.shared.version
        let settings = try BackupFormat.settingsKeys.reduce(into: [String: Data]()) { dict, key in
            if let value = UserDefaults.standard.object(forKey: key) {
                if key == ContractTransferArchive.key, (value as? Data)?.isEmpty == true { return }
                dict[key] = try PropertyListSerialization.data(fromPropertyList: value, format: .binary, options: 0)
            }
        }
        let progress = forCloudTransfer ? nil : ImportProgress.shared
        progress?.begin(title)
        defer { progress?.end() }
        let sink = progress?.sink(0, 1, tr("تجهيز النسخة", "Preparing backup"))
        let container = SharedModel.container
        let result = try await Task.detached(priority: .userInitiated) {
            try await BackupActor(modelContainer: container).export(settings: settings, progress: sink)
        }.value
        guard revision == Self.backupChangeDate,
              staffRevision == StaffImportState.shared.version, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
            BackupFormat.removeExport(result.0)
            throw BackupError.sourceChanged
        }
        // حاوية جديدة فارغة بعد إعادة تثبيت التطبيق لا يجوز أن تمحو آخر نسخة صالحة.
        guard result.1.hasData else {
            BackupFormat.removeExport(result.0)
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

    nonisolated static func replaceVerifiedArchive(_ url: URL, size preparedSize: Int64, manifest: BackupFormat.Manifest, at file: URL) throws {
        var coordinationError: NSError?
        var writeError: Error?
        var didWrite = false
        // تنسيق الاستبدال مع مزود الملفات يحافظ على صلاحية الوصول المختارة.
        NSFileCoordinator().coordinate(writingItemAt: file, options: .forReplacing, error: &coordinationError) { target in
            do {
                try Self.checkReplacement(manifest, at: target)
                // تُكتب النسخة إلى ملف مجاور ثم تُستبدل دفعة واحدة؛ تبقى القديمة عند فشل الكتابة.
                let contents = try Data(contentsOf: url, options: .mappedIfSafe)
                guard contents.count > 0, Int64(contents.count) == preparedSize else { throw BackupError.emptyArchive }
                try contents.write(to: target, options: .atomic)
                didWrite = true
            } catch { writeError = error }
        }
        if let error = coordinationError ?? writeError {
            if !FileManager.default.fileExists(atPath: file.path) { throw BackupError.folderUnavailable }
            throw error
        }
        guard didWrite else { throw BackupError.folderUnavailable }
    }

    /// يمنع تحديث حاوية ناقصة من محو أشهر أو ملاك أو حسابات كانت في نسخة iCloud السابقة.
    nonisolated static func checkReplacement(_ current: BackupFormat.Manifest, at file: URL) throws {
        let attributes: [FileAttributeKey: Any]
        do { attributes = try FileManager.default.attributesOfItem(atPath: file.path) }
        catch let error as NSError where error.domain == NSCocoaErrorDomain && (error.code == NSFileReadNoSuchFileError || error.code == NSFileNoSuchFileError) { return }
        // حجم iCloud قد يكون صفراً لملف لم يُنزّل؛ لا نعتبر ذلك إذناً بمحو النسخة.
        if (attributes[.size] as? NSNumber)?.intValue == 0 { throw BackupError.unavailableArchive }
        let archive = try Archive(url: file, accessMode: .read)
        let entries = try CheckedArchive.Index(archive)
        guard let entry = entries["manifest.json"] else { throw BackupError.notBackup }
        let data = try CheckedArchive.data(entry, from: archive, limit: 1_000_000)
        let previous = try JSONDecoder().decode(BackupFormat.Manifest.self, from: data)
        try previous.validateFormat()
        try checkMissingData(current, previous: previous)
    }

    nonisolated static func checkMissingData(_ current: BackupFormat.Manifest, previous: BackupFormat.Manifest) throws {
        let lostMonth = !Set(previous.months).isSubset(of: Set(current.months))
        let lostRows = previous.employees > 0 && current.employees == 0 ||
            previous.deductions > 0 && current.deductions == 0 ||
            previous.staff > 0 && current.staff == 0 ||
            (previous.accounts ?? 0) > 0 && (current.accounts ?? 0) == 0 ||
            (previous.books ?? 0) > 0 && (current.books ?? 0) == 0 ||
            (previous.salaryTrustEntries ?? 0) > 0 && (current.salaryTrustEntries ?? 0) == 0
        guard previous.refundBatchCount == nil || current.refundBatchCount != nil else { throw BackupError.dataLossRisk }
        guard !lostMonth && !lostRows, Set(previous.contractExecutionMarkers ?? []).isSubset(of: Set(current.contractExecutionMarkers ?? [])) else { throw BackupError.dataLossRisk }
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
                  manifest.hasData else { return }
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

    func markChanged() {
        UserDefaults.standard.set(Date(), forKey: Self.changedKey)
        version += 1
    }

    /// قد يصل إشعار إعداد مستعاد لاحقاً، حتى بعد انتهاء تطبيق قيم النسخة.
    func markSettingChanged(_ key: String) {
        if let restored = restoredSettingValues[key],
           Self.isRestoredSetting(UserDefaults.standard.object(forKey: key), equalTo: restored) { return }
        restoredSettingValues.removeValue(forKey: key)
        if key == AppSettings.appearanceKey {
            UserDefaults.standard.set(Date(), forKey: Self.appearanceChangedKey)
            version += 1
            return
        }
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
        let changed = Self.backupChangeDate
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
        guard Self.autoEnabled, !Self.manualOnly else { return nil }
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
        let changeWatermark = Self.backupChangeDate
        defer { isWorking = false; version += 1 }
        // يكمل النسخ حتى لو خرج المستخدم من التطبيق أثناءه.
        let task = UIApplication.shared.beginBackgroundTask(withName: "backup")
        defer { if task != .invalid { UIApplication.shared.endBackgroundTask(task) } }
        do {
            let (url, manifest) = try await makeBackup(title: tr("نسخ احتياطي", "Backing up"))
            defer { BackupFormat.removeExport(url) }
            try Self.checkSafetyManifest(manifest)
            let sizes = try await Task.detached { try BackupSizes.read(url) }.value
            let scope = resolveBookmark(Self.folderKey) ?? file
            let refreshedBookmark = try await Task.detached(priority: .utility) { () throws -> Data? in
                let granted = scope.startAccessingSecurityScopedResource()
                defer { if granted { scope.stopAccessingSecurityScopedResource() } }
                try Self.replaceVerifiedArchive(url, size: sizes.total, manifest: manifest, at: file)
                // حجم مزود iCloud الوصفي قد يبقى صفراً مؤقتاً بعد الكتابة؛ نعتمد حجم البيانات المكتوبة.
                let bookmark = try? file.bookmarkData(options: .minimalBookmark, includingResourceValuesForKeys: nil, relativeTo: nil)
                return bookmark
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
            Self.recordSizes(sizes)
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
        let changeWatermark = Self.backupChangeDate
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
            defer { BackupFormat.removeExport(file) }
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
            defaults.removeObject(forKey: Self.sizesKey)
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
        // Report independent failures separately; never hide a failing component behind another.
        #if targetEnvironment(simulator)
        let scanOK: Bool, decorOK: Bool
        var diagnostic = ""
        do { scanOK = try OfficialBookScanEnhancement.selfTest() }
        catch { scanOK = false; diagnostic += " scanError=\(error.localizedDescription)" }
        do { decorOK = try PDFDecor.selfTest() }
        catch { decorOK = false; diagnostic += " decorError=\(error.localizedDescription)" }
        #else
        let scanOK = true, decorOK = true
        let diagnostic = ""
        #endif
        let trustOK = SalaryTrustSelfTest.run()
        let trustPDFOK = await SalaryTrustSelfTest.pdf()
        let breakdownOK = sizes?.total == Int64(periodicSize) && previousSizes?.total == Int64(replacementSize)
        let passed = breakdownOK && firstSize > 0 && replacementSize > 0 && periodicSize > 0 && lastSize == Int64(periodicSize)
            && periodicDue && valid && lastError == nil && count == 1 && serverDatesOK && trustOK && trustPDFOK && scanOK && decorOK
        LaunchOptions.writeReport("autobackup=\(passed ? "ok" : "failed") sizeBreakdown=\(breakdownOK) serverDates=\(serverDatesOK) trust=\(trustOK) trustPDF=\(trustPDFOK) scan=\(scanOK) decor=\(decorOK) size=\(periodicSize) recorded=\(lastSize) due=\(periodicDue) zip=\(valid) files=\(count) line=\(line) again=\(again)\(diagnostic)")
    }

    // MARK: - الاسترجاع

    func pickRestore() {
        guard !isWorking, pendingRestore == nil else { return }
        FilePicker.present(types: [.zip], multiple: false) { [weak self] urls in
            guard let self, let url = urls.first, !self.isWorking, self.pendingRestore == nil else { return }
            self.isWorking = true
            Task {
                defer { self.isWorking = false }
                await self.open(url)
            }
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
            let entries = try CheckedArchive.Index(archive)
            guard let metadata = entries["manifest.json"] else { throw BackupError.notBackup }
            let data = try CheckedArchive.data(metadata, from: archive, limit: 1_000_000)
            let manifest = try JSONDecoder().decode(BackupFormat.Manifest.self, from: data)
            try manifest.validateFormat()
            guard manifest.hasData else { throw BackupError.emptyRestore }
            try data.write(to: dir.appendingPathComponent("manifest.json"))
            var required = ["settings.json", "months.jsonl", "employees.jsonl", "deductions.jsonl", "staff.jsonl"]
            if manifest.version == 14 { required.append(RefundBackup.file) }
            else if entries[RefundBackup.file] != nil { throw BackupError.notBackup }
            if manifest.version >= 12 { required.append(CompactPayrollBackup.schemaFile) }
            if manifest.version >= 2 || entries["accounts.jsonl"] != nil { required.append("accounts.jsonl") }
            for name in required {
                guard let entry = entries[name] else { throw BackupError.notBackup }
                try CheckedArchive.write(entry, from: archive, to: dir.appendingPathComponent(name),
                                         limit: manifest.entryLimit(name))
            }
            if manifest.version >= 3 {
                guard let expected = manifest.books, expected >= 0 && expected <= 10_000,
                      let index = entries["books/index.json"], index.type == .file,
                      index.uncompressedSize < 10_000_000 else { throw BackupError.notBackup }
                let booksDir = dir.appendingPathComponent("books", isDirectory: true)
                try FileManager.default.createDirectory(at: booksDir, withIntermediateDirectories: true)
                try CheckedArchive.write(index, from: archive, to: booksDir.appendingPathComponent("index.json"), limit: 10_000_000)
                let books = try OfficialBookFiles.read(in: booksDir)
                guard books.count == expected, Set(books.map(\.id)).count == expected else { throw BackupError.notBackup }
                for book in books {
                    let name = "books/\(book.id.uuidString).pdf"
                    guard let entry = entries[name], entry.type == .file,
                          entry.uncompressedSize <= 60_000_000 else { throw BackupError.notBackup }
                    try CheckedArchive.write(entry, from: archive, to: OfficialBookFiles.file(book.id, in: booksDir), limit: 60_000_000)
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
            try manifest.validateFormat()
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

    private var syncCompletion: CheckedContinuation<String, Error>?

    var workSyncStamp: String {
        let defaults = UserDefaults.standard
        var hash = SHA256()
        for key in (BackupFormat.workspaceSettingsKeys + [ExecutionAccountStore.storageKey, "oneDriveRestoredRevision"]).sorted() {
            guard let value = defaults.object(forKey: key),
                  let bytes = try? PropertyListSerialization.data(fromPropertyList: value, format: .binary, options: 0) else { continue }
            hash.update(data: Data(key.utf8))
            hash.update(data: Data(SHA256.hash(data: bytes)))
        }
        let changed = defaults.object(forKey: Self.changedKey) as? Date
        return String(changed?.timeIntervalSince1970 ?? 0) + ":" + Data(hash.finalize()).base64EncodedString()
    }

    var workSyncAvailable: Bool {
        !isWorking && pendingRestore == nil && !SharedModel.health.restoring && !SharedModel.health.restartRequired &&
        !ImportCenter.shared.isBusy && !StaffImportState.shared.isImporting && !AppLock.shared.isLocked
    }

    func workSyncExport() async throws -> URL {
        guard workSyncAvailable else { throw BackupError.sourceChanged }
        isWorking = true
        defer { isWorking = false }
        return try await makeBackup(title: tr("تجهيز النسخة الكاملة", "Preparing full backup"), forCloudTransfer: true).0
    }

    func workSyncValidate(_ file: URL) async throws -> URL {
        let folder = try await Self.extractForRestore(file)
        do {
            _ = try await BackupActor(modelContainer: SharedModel.container).restore(from: folder, progress: nil, validateOnly: true)
            return folder
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }

    func workSyncApply(_ folder: URL, expected: String) async throws -> String {
        guard workSyncAvailable, workSyncStamp == expected else { throw BackupError.sourceChanged }
        let manifest = try JSONDecoder().decode(BackupFormat.Manifest.self, from: Data(contentsOf: folder.appendingPathComponent("manifest.json")))
        return try await withCheckedThrowingContinuation { continuation in
            syncCompletion = continuation
            pendingRestore = (folder, manifest)
            confirmRestore()
        }
    }

    func confirmRestore() {
        guard let pending = pendingRestore else { return }
        guard !isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
            cancelRestore()
            message = tr("انتظر انتهاء العملية الحالية، ثم اختر النسخة مجدداً للاسترجاع.", "Wait for the current operation to finish, then select the backup again to restore.")
            return
        }
        RefundDraft.shared.flushAmount()
        pendingRestore = nil
        let serverRestore = pendingServerRestore
        pendingServerRestore = nil
        isWorking = true
        SharedModel.health.restoring = true
        let changeWatermark = Self.backupChangeDate
        Task {
            defer { isWorking = false; SharedModel.health.restoring = false; version += 1; try? FileManager.default.removeItem(at: pending.folder) }
            let progress = ImportProgress.shared
            if syncCompletion == nil { await WorkspaceSync.shared.rememberPoint("import") }
            progress.begin(tr("استرجاع النسخة", "Restoring backup"))
            let sink = progress.sink(0, 1, tr("استبدال البيانات", "Replacing data"))
            let container = SharedModel.container
            do {
                var settings = try await Task.detached(priority: .userInitiated) {
                    try await BackupActor(modelContainer: container).restore(from: pending.folder, progress: sink, recoverable: true)
                }.value
                if syncCompletion != nil { settings.removeValue(forKey: AppSettings.appearanceKey) }
                BankStatementCache.shared.invalidate()
                restoredSettingValues = settings.reduce(into: [:]) { result, item in
                    if let value = try? PropertyListSerialization.propertyList(from: item.value, format: nil),
                       let normalized = try? PropertyListSerialization.data(fromPropertyList: value, format: .binary, options: 0) {
                        result[item.key] = normalized
                    }
                }
                if syncCompletion != nil {
                    for key in BackupFormat.workspaceSettingsKeys where settings[key] == nil { UserDefaults.standard.removeObject(forKey: key) }
                }
                for key in [SalaryTrust.settingsKey, "pendingTrustPayrollMonths", "pendingTrustPayrollStarted", WorkReport.pinsKey, "homeHiddenWorkReports", "windowsContentTextLevel", ContractTransferArchive.key] where settings[key] == nil {
                    UserDefaults.standard.removeObject(forKey: key)
                }
                for (key, data) in settings {
                    if let value = try? PropertyListSerialization.propertyList(from: data, format: nil) {
                        UserDefaults.standard.set(value, forKey: key)
                    }
                }
                UserDefaults.standard.synchronize()
                if pending.manifest.version == 14 { RefundDraft.shared.reloadAfterRestore() }
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
                UserDefaults.standard.set(UUID().uuidString, forKey:"oneDriveRestoredRevision")
                if let continuation = syncCompletion {
                    syncCompletion = nil
                    continuation.resume(returning: workSyncStamp)
                    return
                }
                message = tr("تم الاسترجاع: \(pending.manifest.months.count) أشهر، \(Num.format(pending.manifest.staff)) في الملاك.",
                             "Restored: \(pending.manifest.months.count) months, \(pending.manifest.staff) staff.")
            } catch {
                if RestoreSafety.pending { SharedModel.health.restartRequired = true }
                BankStatementCache.shared.invalidate()
                progress.end()
                if let continuation = syncCompletion {
                    syncCompletion = nil
                    continuation.resume(throwing: error)
                    return
                }
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
            defer { BackupFormat.removeExport(url) }
            #if targetEnvironment(simulator)
            guard testManifest.version == 12 && [10, 11].contains(testManifest.dataVersion) else { LaunchOptions.writeReport("backup=failed unifiedTemplateVersion=failed"); return }
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

enum BackupOperation: Hashable {
    case upload, restore
    var title: String {
        self == .upload ? tr("رفع نسخة", "Upload backup") : tr("استيراد نسخة", "Import backup")
    }
}

struct BackupView: View {
    @State private var manager = BackupManager.shared
    @State private var cloud: BackupOperation?

    private func backupMenu(_ operation: BackupOperation) -> some View {
        Menu {
            Button("OneDrive", systemImage: "cloud") { cloud = operation }
            Button(tr("محلي", "Local"), systemImage: "folder") {
                if operation == .upload {
                    manager.chooseLocation()
                } else {
                    Task {
                        await FilePicker.waitUntilSettled()
                        manager.pickRestore()
                    }
                }
            }
        } label: {
            SettingRow(title: operation.title,
                       systemImage: operation == .upload ? "square.and.arrow.up" : "square.and.arrow.down",
                       color: operation == .upload ? .blue : .indigo) { EmptyView() }
        }
        .accessibilityIdentifier(operation == .upload ? "saveBackup" : "restoreBackup")
    }

    var body: some View {
        List {
            Section {
                backupMenu(.upload)
                backupMenu(.restore)
            }
            .disabled(manager.isWorking || manager.pendingRestore != nil || WorkspaceSync.shared.busy)
        }
        .navigationTitle(tr("النسخ الاحتياطي", "Backup"))
        .navigationBarTitleDisplayMode(.inline)
        .navigationDestination(item: $cloud) { operation in
            OneDriveBackupView(operation: operation)
        }
    }
}
