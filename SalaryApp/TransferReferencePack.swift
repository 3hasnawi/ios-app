import Foundation
import PDFKit
import ZIPFoundation
import CryptoKit

/// فهرس حزمة قوالب صاحب التطبيق؛ ملفات PDF تبقى خاصة على الجهاز.
nonisolated struct TransferReferenceManifest: Codable, Sendable {
    struct Template: Codable, Sendable {
        let id: String
        let path: String
        let sha256: String
        let pages: Int
    }
    let version: Int
    let templates: [Template]
}

nonisolated enum TransferReferencePack {
    static let settingsKey = "transferReferenceArchiveData"
    enum Failure: LocalizedError {
        case invalid, damaged, oversized, missingDigitReference
        var errorDescription: String? {
            switch self {
            case .invalid: tr("حزمة قوالب التحويل غير صالحة أو لا تحتوي جميع القوالب المطلوبة.", "The transfer template package is invalid or incomplete.")
            case .damaged: tr("تعذّر التحقق من أحد ملفات PDF في حزمة القوالب.", "A PDF in the template package could not be verified.")
            case .oversized: tr("حزمة قوالب التحويل أكبر من الحد المدعوم.", "The transfer template package is too large.")
            case .missingDigitReference: tr("حزمة المراجع القديمة لا تحتوي أشكال أرقام الشهر؛ استورد الحزمة المحدّثة.", "The older reference package lacks month digit shapes; import the updated package.")
            }
        }
    }
    static let expected: [String: Int] = [
        "rafidain_group": 2, "other_banks_group": 2, "cash_salary": 1, "food_company": 1,
        "food_tax": 1, "tax_social": 1, "contracts": 1, "water": 1, "housing": 1,
        "rafidain_anbar": 1, "ramadi": 1
    ]
    static let digitTemplateID = "month_digits"
    private static func read(_ entry: Entry, from archive: Archive, maxSize: UInt64) throws -> Data {
        guard entry.type == .file, UInt64(entry.uncompressedSize) <= maxSize else { throw Failure.oversized }
        var result = Data()
        _ = try archive.extract(entry) { result.append($0) }
        return result
    }
    private static func hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
    /// للتحقق من بيانات النسخة قبل استبدال أي سجلات أثناء الاسترجاع.
    static func validateData(_ data: Data) throws -> TransferReferenceManifest {
        guard !data.isEmpty, data.count <= 10_000_000 else { throw Failure.oversized }
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("references-\(UUID().uuidString).zip")
        defer { try? FileManager.default.removeItem(at: url) }
        try data.write(to: url, options: [.atomic, .completeFileProtection])
        return try validate(url)
    }
    /// القراءة بعد التحقق الكامل من الحزمة، دون الاعتماد على مسارات من خارجها.
    static func load(_ ids: Set<String>, from data: Data) throws -> [String: Data] {
        guard !data.isEmpty, data.count <= 10_000_000, !ids.isEmpty else { throw Failure.invalid }
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("references-read-\(UUID().uuidString).zip")
        defer { try? FileManager.default.removeItem(at: url) }
        try data.write(to: url, options: [.atomic, .completeFileProtection])
        let manifest = try validate(url)
        let records = manifest.templates.filter { ids.contains($0.id) }
        guard records.count == ids.count else {
            if ids.contains(digitTemplateID), !manifest.templates.contains(where: { $0.id == digitTemplateID }) { throw Failure.missingDigitReference }
            throw Failure.invalid
        }
        let archive = try Archive(url: url, accessMode: .read)
        var result: [String: Data] = [:]
        for record in records {
            guard let entry = archive[record.path] else { throw Failure.invalid }
            let contents = try read(entry, from: archive, maxSize: 10_000_000)
            guard hex(contents) == record.sha256.lowercased() else { throw Failure.damaged }
            result[record.id] = contents
        }
        return result
    }
    static func validate(_ url: URL) throws -> TransferReferenceManifest {
        let archive = try Archive(url: url, accessMode: .read)
        guard let entry = archive["manifest.json"] else { throw Failure.invalid }
        let manifest = try JSONDecoder().decode(TransferReferenceManifest.self, from: read(entry, from: archive, maxSize: 100_000))
        var expectedPages = expected
        if manifest.version == 2 { expectedPages[digitTemplateID] = 1 }
        guard (manifest.version == 1 || manifest.version == 2), manifest.templates.count == expectedPages.count,
              Set(manifest.templates.map(\.id)) == Set(expectedPages.keys) else { throw Failure.invalid }
        let allowed = Set(manifest.templates.map(\.path)).union(["manifest.json"])
        var seen: Set<String> = []
        var bytes: UInt64 = 0
        for item in archive {
            if item.type == .directory && item.path == "templates/" { continue }
            guard item.type == .file, allowed.contains(item.path), seen.insert(item.path).inserted else { throw Failure.invalid }
            bytes += UInt64(item.uncompressedSize)
            guard bytes <= 40_000_000 else { throw Failure.oversized }
        }
        guard seen == allowed else { throw Failure.invalid }
        for template in manifest.templates {
            try Task.checkCancellation()
            guard template.path == "templates/\(template.id).pdf", let pages = expectedPages[template.id],
                  template.pages == pages, let item = archive[template.path] else { throw Failure.invalid }
            let data = try read(item, from: archive, maxSize: 10_000_000)
            guard hex(data) == template.sha256.lowercased(), let document = PDFDocument(data: data),
                  !document.isLocked, document.pageCount == pages else { throw Failure.damaged }
            for index in 0..<pages {
                guard let page = document.page(at: index) else { throw Failure.damaged }
                let box = page.bounds(for: .mediaBox)
                let width: CGFloat = template.id == digitTemplateID ? 612 : 595.3
                let height: CGFloat = template.id == digitTemplateID ? 792 : 841.9
                guard abs(box.width - width) < 2, abs(box.height - height) < 2 else { throw Failure.damaged }
                if template.id == digitTemplateID {
                    guard (page.string ?? "").contains("0123456789") else { throw Failure.damaged }
                }
            }
        }
        return manifest
    }
}
