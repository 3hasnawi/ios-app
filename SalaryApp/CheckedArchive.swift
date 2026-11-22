import Foundation
import ZIPFoundation

/// يفحص الأجزاء التي يقرأها التطبيق، ويكتبها على دفعات إلى مسارات يختارها المستدعي.
nonisolated enum CheckedArchive {
    enum Failure: LocalizedError {
        case invalid, tooLarge, noSpace
        var errorDescription: String? {
            switch self {
            case .invalid: tr("الملف المضغوط تالف أو يحتوي أجزاءً مكررة. أعد تصديره من المصدر.", "The archive is damaged or has duplicate entries. Export it again from the source.")
            case .tooLarge: tr("محتوى الملف أكبر من الحجم الذي يمكن معالجته على الجهاز.", "The expanded content exceeds the supported size.")
            case .noSpace: tr("المساحة المحلية غير كافية لفك الملف بأمان.", "There is not enough local space to extract this file safely.")
            }
        }
    }

    struct Index {
        private(set) var entries: [Entry] = []
        private var byPath: [String: Int] = [:]
        init(_ archive: Archive) throws {
            var pathBytes = 0
            for entry in archive {
                try Task.checkCancellation()
                let path = entry.path
                let bytes = path.utf8.count
                guard entries.count < 65_536, bytes <= 4096, pathBytes + bytes <= 8 * 1024 * 1024 else { throw Failure.tooLarge }
                pathBytes += bytes
                guard byPath.updateValue(entries.count, forKey: path) == nil else { throw Failure.invalid }
                entries.append(entry)
            }
        }
        subscript(_ path: String) -> Entry? { byPath[path].map { entries[$0] } }
    }

    static func total(_ entries: [Entry], limit: UInt64) throws -> UInt64 {
        var size: UInt64 = 0
        for entry in entries {
            guard entry.uncompressedSize <= limit - size else { throw Failure.tooLarge }
            size += entry.uncompressedSize
        }
        return size
    }

    static func checkSpace(for size: UInt64, at folder: URL) throws {
        let attributes = try FileManager.default.attributesOfFileSystem(forPath: folder.path)
        if let available = attributes[.systemFreeSize] as? NSNumber {
            let bytes = available.uint64Value
            guard bytes >= size, bytes - size >= 32 * 1024 * 1024 else { throw Failure.noSpace }
        }
    }

    static func consume(_ entry: Entry, from archive: Archive, limit: UInt64, write: (Data) throws -> Void) throws {
        guard entry.type == .file else { throw Failure.invalid }
        let expected = entry.uncompressedSize
        guard expected <= limit else { throw Failure.tooLarge }
        var size: UInt64 = 0
        let checksum = try archive.extract(entry) { chunk in
            try Task.checkCancellation()
            guard UInt64(chunk.count) <= expected - size else { throw Failure.invalid }
            size += UInt64(chunk.count)
            try write(chunk)
        }
        guard size == expected, checksum == entry.checksum else { throw Failure.invalid }
    }

    static func data(_ entry: Entry, from archive: Archive, limit: UInt64) throws -> Data {
        var result = Data()
        try consume(entry, from: archive, limit: limit) { result.append($0) }
        return result
    }

    static func write(_ entry: Entry, from archive: Archive, to target: URL, limit: UInt64) throws {
        guard entry.type == .file else { throw Failure.invalid }
        guard entry.uncompressedSize <= limit else { throw Failure.tooLarge }
        try checkSpace(for: entry.uncompressedSize, at: target.deletingLastPathComponent())
        try Data().write(to: target, options: .withoutOverwriting)
        var finished = false
        defer { if !finished { try? FileManager.default.removeItem(at: target) } }
        let file = try FileHandle(forWritingTo: target)
        do {
            try consume(entry, from: archive, limit: limit) { try file.write(contentsOf: $0) }
            try file.close()
            finished = true
        } catch {
            try? file.close()
            throw error
        }
    }
}
