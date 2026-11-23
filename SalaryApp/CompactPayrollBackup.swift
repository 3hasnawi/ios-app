import Foundation
import CryptoKit

/// صيغة الآيفون ١٢. يبقى كل بايت من لقطة Excel قابلاً للاسترجاع، بما فيه ترتيب الأعمدة وتكرارها.
nonisolated enum CompactPayrollBackup {
    typealias Record = BackupFormat.EmployeeRecord
    static let schemaFile = "payroll-headers.json"
    static let schemaFileLimit = 48 * 1024 * 1024
    static let rowLimit = 64 * 1024 * 1024

    /// بصمة مستقرة لكل حقول السجل الأصلي؛ الطول يميز الحقول، والقيمة الغائبة تختلف عن الفارغة.
    struct Fingerprint {
        private var hash = SHA256()
        private(set) var count = 0

        private mutating func integer(_ value: UInt64) {
            var big = value.bigEndian
            withUnsafeBytes(of: &big) { hash.update(bufferPointer: $0) }
        }
        private mutating func field(_ data: Data?) {
            guard let data else { integer(.max); return }
            integer(UInt64(data.count))
            hash.update(data: data)
        }
        mutating func append(_ row: Record) {
            for text in [row.month, row.name, row.rank, row.workplace] { field(Data(text.utf8)) }
            if let net = row.net { integer(1); integer(net.bitPattern) } else { integer(0) }
            field(row.items); field(row.details); field(row.rawColumns)
            count += 1
        }
        var digest: Data { Data(hash.finalize()) }
    }

    struct Writer {
        private(set) var headers: [Data] = []
        private var indices: [Data: Int] = [:]
        private var bytes = 0
        private(set) var fingerprint = Fingerprint()

        mutating func pack(_ source: Record) throws -> Record {
            fingerprint.append(source)
            guard let raw = source.rawColumns else { return source }
            guard raw.count <= rowLimit else { throw CheckedArchive.Failure.tooLarge }
            guard let parts = SharedPayrollColumns.headerParts(raw), parts.prefix.count >= 32 else { return source }
            let index: Int
            if let existing = indices[parts.prefix] { index = existing }
            else {
                // المخططات النادرة الزائدة تبقى بسجلها الأصلي كي لا تُسقط أي حقول.
                guard headers.count < 100_000, parts.prefix.count <= 32 * 1024 * 1024 - bytes else { return source }
                index = headers.count
                headers.append(parts.prefix)
                indices[parts.prefix] = index
                bytes += parts.prefix.count
            }
            var packed = source
            packed.headerIndex = index
            packed.headerOffset = parts.before.count
            packed.rawColumns = parts.before + parts.tail
            return packed
        }
    }

    static func expand(_ row: Record, headers: [Data]) throws -> Record {
        guard row.headerIndex != nil || row.headerOffset != nil else { return row }
        guard let index = row.headerIndex, headers.indices.contains(index),
              let offset = row.headerOffset, let remainder = row.rawColumns,
              (0...remainder.count).contains(offset), headers[index].count <= rowLimit - remainder.count else {
            throw BackupError.notBackup
        }
        var restored = row
        var raw = Data(capacity: remainder.count + headers[index].count)
        raw.append(remainder.prefix(offset))
        raw.append(headers[index])
        raw.append(remainder.dropFirst(offset))
        restored.rawColumns = raw
        restored.headerIndex = nil
        restored.headerOffset = nil
        return restored
    }

    /// نفس المرور يُستخدم للتحقق من النسخة الجديدة، وقبل الحذف، وأثناء استرجاع المعاملة.
    @discardableResult
    static func read(in dir: URL, manifest: BackupFormat.Manifest,
                     visit: (Int, Record) throws -> Void = { _, _ in }) throws -> Data {
        let decoder = JSONDecoder()
        let compact = manifest.version >= 12
        let headers: [Data]
        if compact {
            guard manifest.payrollDigest?.count == 32 else { throw BackupError.notBackup }
            let data = try BoundedFile.read(dir.appendingPathComponent(schemaFile), limit: schemaFileLimit)
            headers = try decoder.decode([Data].self, from: data)
            guard headers.count <= 100_000 else { throw BackupError.notBackup }
        } else { headers = [] }
        var fingerprint = Fingerprint()
        try BackupLineReader.read(dir.appendingPathComponent("employees.jsonl"), maxLineBytes: rowLimit) { line in
            try Task.checkCancellation()
            let stored = try decoder.decode(Record.self, from: line)
            guard compact || (stored.headerIndex == nil && stored.headerOffset == nil),
                  fingerprint.count < manifest.employees else { throw BackupError.notBackup }
            let row = try expand(stored, headers: headers)
            try visit(fingerprint.count, row)
            fingerprint.append(row)
        }
        let digest = fingerprint.digest
        guard fingerprint.count == manifest.employees, !compact || digest == manifest.payrollDigest else { throw BackupError.notBackup }
        return digest
    }
}
