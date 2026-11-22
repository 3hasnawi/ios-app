import Foundation

/// يقرأ الملفات الخارجية على دفعات ويتحقق من الحجم الفعلي حتى لو تغيّر الملف أثناء القراءة.
nonisolated enum BoundedFile {
    struct TooLarge: LocalizedError {
        var errorDescription: String? {
            tr("حجم الملف يتجاوز الحد المدعوم.", "The file exceeds the supported size.")
        }
    }

    static func read(_ url: URL, limit: Int) throws -> Data {
        guard let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= limit else { throw TooLarge() }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var data = Data()
        while true {
            try Task.checkCancellation()
            guard let chunk = try handle.read(upToCount: 64 * 1024), !chunk.isEmpty else { return data }
            guard chunk.count <= limit - data.count else { throw TooLarge() }
            data.append(chunk)
        }
    }
}
