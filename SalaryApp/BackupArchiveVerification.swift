import Foundation
import ZIPFoundation
import CryptoKit

nonisolated enum BackupArchiveVerification {
    static func create(at url: URL, source: URL, files: [String], manifest: BackupFormat.Manifest) throws {
        do {
            let archive = try Archive(url: url, accessMode: .create)
            for name in files {
                try Task.checkCancellation()
                try archive.addEntry(with: name, relativeTo: source, compressionMethod: .deflate)
            }
        }
        try verify(url, source: source, files: files, manifest: manifest)
    }

    /// نعيد فتح الناتج ونقارن محتوى كل جزء بالمصدر المستقل، بما فيه ملفات PDF وحركات البنك.
    static func verify(_ url: URL, source: URL, files: [String], manifest: BackupFormat.Manifest) throws {
        let archive = try Archive(url: url, accessMode: .read)
        let entries = try CheckedArchive.Index(archive)
        guard Set(entries.entries.map(\.path)) == Set(files) else { throw BackupError.notBackup }
        for name in files {
            guard let entry = entries[name] else { throw BackupError.notBackup }
            let file = try FileHandle(forReadingFrom: source.appendingPathComponent(name))
            defer { try? file.close() }
            var sourceHash = SHA256(), archiveHash = SHA256()
            while let chunk = try file.read(upToCount: 256 * 1024), !chunk.isEmpty {
                try Task.checkCancellation()
                sourceHash.update(data: chunk)
            }
            try CheckedArchive.consume(entry, from: archive, limit: .max) { archiveHash.update(data: $0) }
            guard sourceHash.finalize() == archiveHash.finalize() else { throw BackupError.notBackup }
        }
        // بعد إثبات تطابق أجزاء الملف نختبر مسار الاسترجاع نفسه مقابل بصمة كل الرواتب قبل الاختزال.
        try CompactPayrollBackup.read(in: source, manifest: manifest)
    }
}

nonisolated struct BackupSizes: Codable, Equatable {
    var total: Int64
    var payroll: Int64 = 0
    var deductions: Int64 = 0
    var staff: Int64 = 0
    var books: Int64 = 0
    var other: Int64 { total - payroll - deductions - staff - books }

    static func read(_ file: URL) throws -> Self {
        guard let bytes = try file.resourceValues(forKeys: [.fileSizeKey]).fileSize, bytes > 0 else { throw BackupError.notBackup }
        let archive = try Archive(url: file, accessMode: .read)
        var result = Self(total: Int64(bytes))
        var remaining = UInt64(bytes)
        for entry in try CheckedArchive.Index(archive).entries {
            guard entry.compressedSize <= remaining else { throw BackupError.notBackup }
            remaining -= entry.compressedSize
            let size = Int64(entry.compressedSize)
            switch entry.path {
            case "employees.jsonl", CompactPayrollBackup.schemaFile: result.payroll += size
            case "deductions.jsonl": result.deductions += size
            case "staff.jsonl": result.staff += size
            case let path where path.hasPrefix("books/"): result.books += size
            default: break
            }
        }
        return result
    }
}
