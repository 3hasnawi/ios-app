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

    static func supportingPaths(bookIDs: [UUID], refunds: Bool = false) -> [String] {
        ["settings.json", "months.jsonl", "deductions.jsonl", "staff.jsonl", "accounts.jsonl",
         CompactPayrollBackup.schemaFile, "books/index.json"] + bookIDs.map { "books/\($0.uuidString).pdf" } + (refunds ? [RefundBackup.file] : [])
    }

    private static func fileDigest(_ url: URL) throws -> Data {
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        var hash = SHA256()
        while let chunk = try file.read(upToCount: 256 * 1024), !chunk.isEmpty {
            try Task.checkCancellation()
            hash.update(data: chunk)
        }
        return Data(hash.finalize())
    }

    private static func combined(_ names: [String], digest: (String) throws -> Data) rethrows -> Data {
        var hash = SHA256()
        for name in names.sorted() {
            hash.update(data: Data(name.utf8))
            hash.update(data: Data([0]))
            hash.update(data: try digest(name))
        }
        return Data(hash.finalize())
    }

    static func supportingDigest(in source: URL, paths: [String], expected: [String: Data] = [:]) throws -> Data {
        try combined(paths) { name in
            if let original = expected[name] { return original }
            return try fileDigest(source.appendingPathComponent(name))
        }
    }

    /// نعيد فتح الناتج ونقارن محتوى كل جزء بالمصدر المستقل، بما فيه ملفات PDF وحركات البنك.
    static func verify(_ url: URL, source: URL, files: [String], manifest: BackupFormat.Manifest) throws {
        let archive = try Archive(url: url, accessMode: .read)
        let entries = try CheckedArchive.Index(archive)
        guard Set(entries.entries.map(\.path)) == Set(files) else { throw BackupError.notBackup }
        func archiveDigest(_ name: String) throws -> Data {
            guard let entry = entries[name] else { throw BackupError.notBackup }
            var hash = SHA256()
            try CheckedArchive.consume(entry, from: archive, limit: manifest.entryLimit(name)) { hash.update(data: $0) }
            return Data(hash.finalize())
        }
        let separate = ["manifest.json", "employees.jsonl"]
        for name in separate {
            guard try archiveDigest(name) == fileDigest(source.appendingPathComponent(name)) else { throw BackupError.notBackup }
        }
        guard try combined(files.filter { !separate.contains($0) }, digest: archiveDigest) == manifest.supportingDigest else {
            throw BackupError.notBackup
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
