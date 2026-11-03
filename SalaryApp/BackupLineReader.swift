import Foundation

/// يقرأ JSONL على دفعات دون تحميل الملف كله. الأسطر الفارغة تُتجاهل مثل القارئ السابق.
enum BackupLineReader {
    static func read(_ url: URL, chunkSize: Int = 65_536, includeNewlines: Bool = false, maxLineBytes: Int? = nil,
                     onChunk: (Data) -> Void = { _ in },
                     onLine: (Data) throws -> Void) throws {
        guard chunkSize > 0, maxLineBytes == nil || maxLineBytes! > 0 else { throw CocoaError(.fileReadUnknown) }
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        var pending = Data()
        while let chunk = try file.read(upToCount: chunkSize), !chunk.isEmpty {
            onChunk(chunk)
            var start = chunk.startIndex
            for index in chunk.indices where chunk[index] == 0x0A {
                pending.append(contentsOf: chunk[start..<index])
                if includeNewlines { pending.append(0x0A) }
                if let maxLineBytes, pending.count > maxLineBytes { throw CocoaError(.fileReadTooLarge) }
                if !pending.isEmpty {
                    try onLine(pending)
                    pending.removeAll(keepingCapacity: true)
                }
                start = chunk.index(after: index)
            }
            pending.append(contentsOf: chunk[start..<chunk.endIndex])
            if let maxLineBytes, pending.count > maxLineBytes { throw CocoaError(.fileReadTooLarge) }
        }
        if !pending.isEmpty { try onLine(pending) }
    }
}
