import SwiftUI
import VisionKit
import ImageIO
import CoreGraphics
import UIKit

/// بيانات الكتاب فقط؛ الصفحات تبقى ملف PDF مستقلًا ولا تُحمَّل أثناء البحث.
nonisolated struct OfficialBook: Codable, Sendable, Identifiable, Hashable {
    var id: UUID
    var title: String
    var month: String
    var pages: Int
    var createdAt: Date
    /// اسم الجهة محفوظ الآن ليسهل ربطه بكشفها لاحقًا.
    var entity: String?
    var entityKey: String? { entity.map(ArabicText.normalize) }
}

enum OfficialBookError: LocalizedError {
    case invalidPages, invalidTitle, invalidMonth, damagedPDF, missingFile, unavailableCamera
    var errorDescription: String? {
        switch self {
        case .invalidPages: tr("اختر كتابًا من صفحة إلى ثلاث صفحات.", "Scan one to three pages.")
        case .invalidTitle: tr("اكتب اسمًا للكتاب.", "Enter a document name.")
        case .invalidMonth: tr("اختر شهر الكتاب من الأشهر المحفوظة.", "Choose a saved month for the document.")
        case .damagedPDF: tr("ملف الكتاب غير صالح أو عدد صفحاته غير مطابق.", "The PDF is invalid or has the wrong page count.")
        case .missingFile: tr("ملف الكتاب غير موجود على الجهاز.", "The document file is missing.")
        case .unavailableCamera: tr("الماسح غير متاح على هذا الجهاز.", "The scanner is unavailable on this device.")
        }
    }
}

/// ملف الفهرس وملفات PDF بأسماء UUID تمنع تعارض العناوين أو الوصول خارج المجلد.
nonisolated enum OfficialBookFiles {
    static var root: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OfficialBooks", isDirectory: true)
    }
    static func file(_ id: UUID, in directory: URL = root) -> URL {
        directory.appendingPathComponent(id.uuidString + ".pdf")
    }
    static func index(in directory: URL = root) -> URL {
        directory.appendingPathComponent("index.json")
    }
    static func bytes() -> Int64 {
        let files = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.fileSizeKey])) ?? []
        return files.reduce(0) { sum, file in
            sum + Int64((try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
        }
    }
    static func read(in directory: URL = root) throws -> [OfficialBook] {
        let url = index(in: directory)
        guard FileManager.default.fileExists(atPath: url.path) else { return [] }
        return try JSONDecoder().decode([OfficialBook].self, from: Data(contentsOf: url))
    }
    static func write(_ books: [OfficialBook], in directory: URL = root) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try JSONEncoder().encode(books).write(to: index(in: directory), options: .atomic)
    }
    static func validate(_ book: OfficialBook, in directory: URL = root) throws {
        guard !book.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !book.month.isEmpty, (1...3).contains(book.pages) else { throw OfficialBookError.damagedPDF }
        let url = file(book.id, in: directory)
        guard FileManager.default.fileExists(atPath: url.path) else { throw OfficialBookError.missingFile }
        guard let pdf = CGPDFDocument(url as CFURL), pdf.numberOfPages == book.pages else { throw OfficialBookError.damagedPDF }
    }
    static func stage(into directory: URL) throws -> [OfficialBook] {
        let books = try read()
        guard Set(books.map(\.id)).count == books.count else { throw OfficialBookError.damagedPDF }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for book in books {
            try validate(book)
            try FileManager.default.copyItem(at: file(book.id), to: file(book.id, in: directory))
        }
        try write(books, in: directory)
        return books
    }
    static func validateArchive(in directory: URL, expected: Int) throws -> [OfficialBook] {
        guard FileManager.default.fileExists(atPath: index(in: directory).path) else { throw OfficialBookError.missingFile }
        let books = try read(in: directory)
        guard books.count == expected, Set(books.map(\.id)).count == books.count else { throw OfficialBookError.damagedPDF }
        for book in books { try validate(book, in: directory) }
        return books
    }
    /// يجهز نسخة مستقلة بجانب المجلد الحالي قبل استبدال أي ملف.
    static func prepareReplacement(from source: URL) throws -> URL {
        let parent = root.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
        let prepared = parent.appendingPathComponent("OfficialBooks-ready-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.copyItem(at: source, to: prepared)
        return prepared
    }
    /// يعيد اسم المجلد القديم ليُستخدم للتراجع إذا تعذر حفظ قاعدة البيانات.
    static func installPrepared(_ prepared: URL) throws -> URL? {
        let fm = FileManager.default
        let old = root.deletingLastPathComponent().appendingPathComponent("OfficialBooks-old-\(UUID().uuidString)", isDirectory: true)
        let hadOld = fm.fileExists(atPath: root.path)
        if hadOld { try fm.moveItem(at: root, to: old) }
        do { try fm.moveItem(at: prepared, to: root) }
        catch {
            if hadOld { try? fm.moveItem(at: old, to: root) }
            throw error
        }
        return hadOld ? old : nil
    }
    static func rollBackInstallation(_ old: URL?) {
        try? FileManager.default.removeItem(at: root)
        if let old { try? FileManager.default.moveItem(at: old, to: root) }
    }
}

/// يُنشأ PDF مصوّر من صور الماسح بعيدًا عن خيط الواجهة، صفحة A4 لكل صورة.
nonisolated enum OfficialBookPDF {
    static func create(_ images: [Data], at url: URL) throws {
        guard (1...3).contains(images.count) else { throw OfficialBookError.invalidPages }
        var page = CGRect(x: 0, y: 0, width: 595, height: 842)
        guard let pdf = CGContext(url as CFURL, mediaBox: &page, nil) else { throw OfficialBookError.damagedPDF }
        for imageData in images {
            try Task.checkCancellation()
            guard let source = CGImageSourceCreateWithData(imageData as CFData, nil),
                  let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: 3000
                  ] as CFDictionary) else { throw OfficialBookError.damagedPDF }
            pdf.beginPDFPage(nil)
            let margin: CGFloat = 18
            let scale = min((page.width - margin * 2) / CGFloat(image.width),
                            (page.height - margin * 2) / CGFloat(image.height))
            let width = CGFloat(image.width) * scale, height = CGFloat(image.height) * scale
            pdf.draw(image, in: CGRect(x: (page.width - width) / 2, y: (page.height - height) / 2,
                                       width: width, height: height))
            pdf.endPDFPage()
        }
        pdf.closePDF()
        guard let document = CGPDFDocument(url as CFURL), document.numberOfPages == images.count else {
            throw OfficialBookError.damagedPDF
        }
    }
}

/// تدوير صفحة مصوّرة قبل تجميع PDF، مع الإبقاء على الصفحات الأخرى كما هي.
nonisolated enum OfficialBookImage {
    static func rotateClockwise(_ data: Data) throws -> Data {
        guard let image = UIImage(data: data) else { throw OfficialBookError.damagedPDF }
        let size = CGSize(width: image.size.height, height: image.size.width)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        let rotated = UIGraphicsImageRenderer(size: size, format: format).image { renderer in
            renderer.cgContext.translateBy(x: size.width, y: 0)
            renderer.cgContext.rotate(by: .pi / 2)
            image.draw(in: CGRect(origin: .zero, size: image.size))
        }
        guard let result = rotated.jpegData(compressionQuality: 0.86) else { throw OfficialBookError.damagedPDF }
        return result
    }
}

@MainActor @Observable
final class OfficialBookStore {
    static let shared = OfficialBookStore()
    private(set) var books: [OfficialBook] = []
    private(set) var revision = 0
    private(set) var loadError: String?

    private init() { reload() }

    func reload() {
        do {
            books = try OfficialBookFiles.read().sorted { $0.createdAt > $1.createdAt }
            loadError = nil
        } catch {
            books = []
            loadError = error.localizedDescription
        }
        revision += 1
    }

    func save(title rawTitle: String, month: String, entity rawEntity: String, images: [Data]) async throws {
        let title = rawTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        let entity = rawEntity.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty else { throw OfficialBookError.invalidTitle }
        guard !month.isEmpty else { throw OfficialBookError.invalidMonth }
        guard (1...3).contains(images.count) else { throw OfficialBookError.invalidPages }
        if let loadError { throw NSError(domain: "OfficialBooks", code: 1, userInfo: [NSLocalizedDescriptionKey: loadError]) }
        let book = OfficialBook(id: UUID(), title: title, month: month, pages: images.count,
                                createdAt: .now, entity: entity.isEmpty ? nil : entity)
        let destination = OfficialBookFiles.file(book.id)
        try FileManager.default.createDirectory(at: OfficialBookFiles.root, withIntermediateDirectories: true)
        do {
            try await Task.detached(priority: .userInitiated) {
                try OfficialBookPDF.create(images, at: destination)
            }.value
            try OfficialBookFiles.validate(book)
            var next = books
            next.insert(book, at: 0)
            try OfficialBookFiles.write(next)
            books = next
            revision += 1
            BackupManager.shared.markChanged()
            Task { _ = await BackupManager.shared.autoBackup() }
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
    }

    func rename(_ book: OfficialBook, to rawTitle: String, entity rawEntity: String) throws {
        let title = rawTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        let entity = rawEntity.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty else { throw OfficialBookError.invalidTitle }
        if let loadError { throw NSError(domain: "OfficialBooks", code: 1, userInfo: [NSLocalizedDescriptionKey: loadError]) }
        guard let index = books.firstIndex(where: { $0.id == book.id }) else { throw OfficialBookError.missingFile }
        var next = books
        next[index].title = title
        next[index].entity = entity.isEmpty ? nil : entity
        try OfficialBookFiles.write(next)
        books = next
        revision += 1
        BackupManager.shared.markChanged()
        Task { _ = await BackupManager.shared.autoBackup() }
    }

    func delete(_ book: OfficialBook) throws {
        if let loadError { throw NSError(domain: "OfficialBooks", code: 1, userInfo: [NSLocalizedDescriptionKey: loadError]) }
        let next = books.filter { $0.id != book.id }
        try OfficialBookFiles.write(next)
        books = next
        revision += 1
        try? FileManager.default.removeItem(at: OfficialBookFiles.file(book.id))
        BackupManager.shared.markChanged()
        Task { _ = await BackupManager.shared.autoBackup() }
    }
}

/// اسم الملف المشارك يطابق اسم الكتاب بدل UUID الداخلي.
@MainActor enum OfficialBookShare {
    static func present(_ book: OfficialBook) throws {
        let source = OfficialBookFiles.file(book.id)
        guard FileManager.default.fileExists(atPath: source.path) else { throw OfficialBookError.missingFile }
        let safe = String(book.title.replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: "\\", with: "-")
            .replacingOccurrences(of: ":", with: "-").prefix(80))
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("OfficialBookShare-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent(safe + ".pdf")
        try FileManager.default.copyItem(at: source, to: destination)
        ActivityShare.present([destination])
        try? FileManager.default.removeItem(at: directory)
    }
}

/// ماسح iOS الأصلي: يحسن حواف الورقة ويعيد الصور دون طلب الوصول إلى الصور الشخصية.
struct OfficialBookScanner: UIViewControllerRepresentable {
    var maxPages = 3
    let onComplete: ([Data]) -> Void
    let onFailure: (String) -> Void
    let onCancel: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(maxPages: maxPages, onComplete: onComplete, onFailure: onFailure, onCancel: onCancel) }
    func makeUIViewController(context: Context) -> VNDocumentCameraViewController {
        let controller = VNDocumentCameraViewController()
        controller.delegate = context.coordinator
        return controller
    }
    func updateUIViewController(_ controller: VNDocumentCameraViewController, context: Context) {}

    final class Coordinator: NSObject, VNDocumentCameraViewControllerDelegate {
        let maxPages: Int
        let onComplete: ([Data]) -> Void
        let onFailure: (String) -> Void
        let onCancel: () -> Void
        init(maxPages: Int, onComplete: @escaping ([Data]) -> Void, onFailure: @escaping (String) -> Void, onCancel: @escaping () -> Void) {
            self.maxPages = maxPages; self.onComplete = onComplete; self.onFailure = onFailure; self.onCancel = onCancel
        }
        func documentCameraViewController(_ controller: VNDocumentCameraViewController, didFinishWith scan: VNDocumentCameraScan) {
            guard (1...maxPages).contains(scan.pageCount) else {
                let message = maxPages == 1
                    ? tr("أعد تصوير صفحة واحدة فقط.", "Retake one page only.")
                    : OfficialBookError.invalidPages.localizedDescription
                controller.dismiss(animated: true) { self.onFailure(message) }
                return
            }
            var images: [Data] = []
            for index in 0..<scan.pageCount {
                guard let data = scan.imageOfPage(at: index).jpegData(compressionQuality: 0.86) else {
                    controller.dismiss(animated: true) { self.onFailure(OfficialBookError.damagedPDF.localizedDescription) }
                    return
                }
                images.append(data)
            }
            controller.dismiss(animated: true) { self.onComplete(images) }
        }
        func documentCameraViewControllerDidCancel(_ controller: VNDocumentCameraViewController) {
            controller.dismiss(animated: true, completion: onCancel)
        }
        func documentCameraViewController(_ controller: VNDocumentCameraViewController, didFailWithError error: Error) {
            controller.dismiss(animated: true) { self.onFailure(error.localizedDescription) }
        }
    }
}
