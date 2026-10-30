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
    enum Failure: LocalizedError {
        case invalid, damaged, oversized
        var errorDescription: String? {
            switch self {
            case .invalid: tr("حزمة قوالب التحويل غير صالحة أو لا تحتوي جميع القوالب المطلوبة.", "The transfer template package is invalid or incomplete.")
            case .damaged: tr("تعذّر التحقق من أحد ملفات PDF في حزمة القوالب.", "A PDF in the template package could not be verified.")
            case .oversized: tr("حزمة قوالب التحويل أكبر من الحد المدعوم.", "The transfer template package is too large.")
            }
        }
    }
    static let expected: [String: Int] = [
        "rafidain_group": 2, "other_banks_group": 2, "cash_salary": 1, "food_company": 1,
        "food_tax": 1, "tax_social": 1, "contracts": 1, "water": 1, "housing": 1,
        "rafidain_anbar": 1, "ramadi": 1
    ]
    private static func read(_ entry: Entry, from archive: Archive, maxSize: UInt64) throws -> Data {
        guard entry.type == .file, UInt64(entry.uncompressedSize) <= maxSize else { throw Failure.oversized }
        var result = Data()
        _ = try archive.extract(entry) { result.append($0) }
        return result
    }
    private static func hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
    static func validate(_ url: URL) throws -> TransferReferenceManifest {
        let archive = try Archive(url: url, accessMode: .read)
        guard let entry = archive["manifest.json"] else { throw Failure.invalid }
        let manifest = try JSONDecoder().decode(TransferReferenceManifest.self, from: read(entry, from: archive, maxSize: 100_000))
        guard manifest.version == 1, manifest.templates.count == expected.count,
              Set(manifest.templates.map(\.id)) == Set(expected.keys) else { throw Failure.invalid }
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
            guard template.path == "templates/\(template.id).pdf", let pages = expected[template.id],
                  template.pages == pages, let item = archive[template.path] else { throw Failure.invalid }
            let data = try read(item, from: archive, maxSize: 10_000_000)
            guard hex(data) == template.sha256.lowercased(), let document = PDFDocument(data: data),
                  !document.isLocked, document.pageCount == pages else { throw Failure.damaged }
            for index in 0..<pages {
                guard let page = document.page(at: index) else { throw Failure.damaged }
                let box = page.bounds(for: .mediaBox)
                guard abs(box.width - 595.3) < 2, abs(box.height - 841.9) < 2 else { throw Failure.damaged }
            }
        }
        return manifest
    }
}
