import Foundation

/// Each line preserves the original JSON bytes and the stable local history identifier.
nonisolated enum RefundBackup {
    static let file = "refunds.jsonl"
    static let lock = NSLock()
    static let rowLimit = 32 * 1024 * 1024
    static let totalLimit = 512 * 1024 * 1024
    static let historyLimit = 10_000
    private struct Record: Codable { let id: String; let contents: Data }

    private static func validID(_ id: String) -> Bool {
        if let uuid = UUID(uuidString: id), uuid.uuidString == id { return true }
        return id.utf8.count == 64 && id.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }

    private static func historyFiles(in root: URL) throws -> [URL] {
        let history = root.appendingPathComponent("history")
        do {
            let files = try FileManager.default.contentsOfDirectory(at: history, includingPropertiesForKeys: nil).filter { $0.pathExtension == "json" }
            guard files.count <= historyLimit else { throw CocoaError(.fileReadTooLarge) }
            return files
        } catch let error as CocoaError where error.code == .fileReadNoSuchFile { return [] }
    }

    static func inventory(at root: URL) throws -> (count: Int?, draft: Bool?) {
        try lock.withLock {
            let files = try historyFiles(in: root)
            let fm = FileManager.default
            guard fm.fileExists(atPath: root.path) else { return (nil, nil) }
            return (files.count, fm.fileExists(atPath: root.appendingPathComponent("draft.json").path))
        }
    }

    static func write(from root: URL, to url: URL) throws -> (count: Int, draft: Bool, present: Bool) {
        try lock.withLock {
            let fm = FileManager.default
            let files = try historyFiles(in: root)
            try Data().write(to: url, options: .completeFileProtection)
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.withoutEscapingSlashes]
            var bytes = 0
            func append(_ id: String, contents: Data) throws {
                _ = try JSONDecoder().decode(RefundSnapshot.self, from: contents)
                var line = try encoder.encode(Record(id: id, contents: contents))
                guard line.count <= rowLimit else { throw CocoaError(.fileReadTooLarge) }
                line.append(10)
                guard line.count <= totalLimit - bytes else { throw CocoaError(.fileReadTooLarge) }
                try handle.write(contentsOf: line)
                bytes += line.count
            }
            let draft: Data?
            do { draft = try BoundedFile.read(root.appendingPathComponent("draft.json"), limit: rowLimit) }
            catch let error as CocoaError where error.code == .fileReadNoSuchFile { draft = nil }
            if let draft { try append("draft", contents: draft) }
            for url in files.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
                try Task.checkCancellation()
                let id = url.deletingPathExtension().lastPathComponent
                guard validID(id) else { throw CocoaError(.fileReadCorruptFile) }
                try append(id, contents: BoundedFile.read(url, limit: rowLimit))
            }
            try handle.synchronize()
            return (files.count, draft != nil, fm.fileExists(atPath: root.path))
        }
    }

    /// Builds a complete replacement outside the live directory before any database changes.
    static func prepare(from url: URL, beside root: URL, count: Int, draft: Bool) throws -> URL {
        let fm = FileManager.default
        let directory = root.deletingLastPathComponent().appendingPathComponent("refund-ready-" + UUID().uuidString)
        let history = directory.appendingPathComponent("history")
        var ready = false
        defer { if !ready { try? fm.removeItem(at: directory) } }
        try fm.createDirectory(at: history, withIntermediateDirectories: true)
        var ids = Set<String>()
        var hasDraft = false
        try BackupLineReader.read(url, maxLineBytes: rowLimit) { line in
            try Task.checkCancellation()
            let record = try JSONDecoder().decode(Record.self, from: line)
            let destination: URL
            if record.id == "draft" {
                guard draft, !hasDraft else { throw CocoaError(.fileReadCorruptFile) }
                hasDraft = true
                destination = directory.appendingPathComponent("draft.json")
            } else {
                guard validID(record.id), ids.count < count, ids.insert(record.id).inserted else { throw CocoaError(.fileReadCorruptFile) }
                destination = history.appendingPathComponent(record.id + ".json")
            }
            _ = try JSONDecoder().decode(RefundSnapshot.self, from: record.contents)
            try record.contents.write(to: destination, options: [.atomic, .completeFileProtection])
        }
        guard ids.count == count, draft == hasDraft else { throw CocoaError(.fileReadCorruptFile) }
        ready = true
        return directory
    }

    static func install(_ directory: URL, at root: URL) throws {
        try lock.withLock {
            let fm = FileManager.default
            if fm.fileExists(atPath: root.path) {
                _ = try fm.replaceItemAt(root, withItemAt: directory)
            } else {
                try fm.moveItem(at: directory, to: root)
            }
        }
    }
}
