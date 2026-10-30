import Foundation
import ZIPFoundation

/// تعبئة قالب Word مجهّز بعلامات واضحة، مع الإبقاء على أجزاء الملف وتنسيقها.
/// لا يتعامل مع ملف .doc القديم مباشرة ولا يخمّن مواضع البيانات داخل النموذج.
nonisolated enum TransferWordTemplate {
    /// يجمع النص داخل الفقرة لكشف علامة قُسمت بين مقاطع Word المختلفة.
    private final class ParagraphMarkerCheck: NSObject, XMLParserDelegate {
        private var depth = 0
        private var paragraph = ""
        private(set) var unresolved = false
        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
            if elementName == "w:p" { depth += 1; if depth == 1 { paragraph = "" } }
        }
        func parser(_ parser: XMLParser, foundCharacters string: String) {
            if depth > 0 { paragraph += string }
        }
        func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
            guard elementName == "w:p", depth > 0 else { return }
            depth -= 1
            if depth == 0 && paragraph.range(of: "\\{\\{[A-Z_]+\\}\\}", options: .regularExpression) != nil {
                unresolved = true
            }
        }
    }
    enum Failure: LocalizedError {
        case invalidTemplate, unsafePath, oversized, missingField(String), unresolvedField, invalidXML
        var errorDescription: String? {
            switch self {
            case .invalidTemplate: tr("قالب Word غير صالح أو غير مجهز.", "Invalid or unprepared Word template.")
            case .unsafePath: tr("يحتوي قالب Word مساراً غير صالح.", "The Word template contains an invalid path.")
            case .oversized: tr("قالب Word أكبر من الحجم المدعوم.", "The Word template exceeds the size limit.")
            case .missingField(let key): tr("الحقل المطلوب غير موجود في قالب Word: \(key).", "Required Word template field is missing: \(key).")
            case .unresolvedField: tr("توجد حقول لم تُملأ في قالب Word.", "The Word template has unresolved fields.")
            case .invalidXML: tr("تعذّر قراءة محتوى قالب Word.", "Could not read the Word template content.")
            }
        }
    }
    private static func isXMLPart(_ name: String) -> Bool {
        name == "word/document.xml" || (name.hasPrefix("word/header") && name.hasSuffix(".xml")) || (name.hasPrefix("word/footer") && name.hasSuffix(".xml"))
    }
    private static func escaped(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
    }
    /// القيم متعددة السطور تُكتب كفواصل Word فعلية داخل المقطع نفسه.
    private static func wordText(_ value: String) -> String {
        let lines = value.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n").components(separatedBy: "\n")
        return lines.map(escaped).joined(separator: "</w:t><w:br/><w:t xml:space=\"preserve\">")
    }
    /// صف القالب يحمل علامات ROW_؛ ينسخ لكل سطر حتى تبقى أعمدة Word قابلة للتعديل.
    private static func expandTable(in xml: String, rows: [[String: String]]) throws -> String {
        guard !rows.isEmpty, rows.count <= 2000 else { throw Failure.invalidTemplate }
        let pattern = "<w:tr(?:\\s[^>]*)?>(?:(?!</w:tr>).)*\\{\\{ROW_[A-Z_]+\\}\\}(?:(?!</w:tr>).)*</w:tr>"
        let regex = try NSRegularExpression(pattern: pattern, options: [.dotMatchesLineSeparators])
        let range = NSRange(xml.startIndex..., in: xml)
        let matches = regex.matches(in: xml, range: range)
        guard matches.count == 1, let rowRange = Range(matches[0].range, in: xml) else { throw Failure.invalidTemplate }
        let prototype = String(xml[rowRange])
        let markerRegex = try NSRegularExpression(pattern: "\\{\\{(ROW_[A-Z_]+)\\}\\}")
        let markers = markerRegex.matches(in: prototype, range: NSRange(prototype.startIndex..., in: prototype))
        let keys = Set(markers.compactMap { Range($0.range(at: 1), in: prototype).map { String(prototype[$0]) } })
        guard !keys.isEmpty else { throw Failure.invalidTemplate }
        var expanded = ""
        for row in rows {
            guard Set(row.keys) == keys else { throw Failure.invalidTemplate }
            var item = prototype
            for (key, value) in row {
                guard !value.unicodeScalars.contains(where: { $0.value < 32 }) else { throw Failure.invalidXML }
                item = item.replacingOccurrences(of: "{{\(key)}}", with: escaped(value))
            }
            expanded += item
        }
        var result = xml
        result.replaceSubrange(rowRange, with: expanded)
        return result
    }

    static func render(template: URL, destination: URL, fields: [String: String], tableRows: [[String: String]]? = nil) throws {
        guard !fields.isEmpty, template != destination else { throw Failure.invalidTemplate }
        let fm = FileManager.default
        let root = fm.temporaryDirectory.appendingPathComponent("word-template-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: root) }
        let archive = try Archive(url: template, accessMode: .read)
        guard archive["word/document.xml"] != nil, archive["[Content_Types].xml"] != nil else { throw Failure.invalidTemplate }
        var seen: Set<String> = []
        var paths: [String] = []
        var size: UInt64 = 0
        var found: Set<String> = []
        for entry in archive {
            try Task.checkCancellation()
            let path = entry.path
            let components = path.split(separator: "/", omittingEmptySubsequences: false)
            guard !path.hasPrefix("/"), !path.contains("\\"), !components.contains(".."), !components.contains(".") else { throw Failure.unsafePath }
            guard seen.insert(path).inserted else { throw Failure.invalidTemplate }
            if entry.type == .directory { continue }
            guard entry.type == .file, entry.uncompressedSize <= 20_000_000 else { throw Failure.invalidTemplate }
            size += UInt64(entry.uncompressedSize)
            guard size <= 80_000_000, paths.count < 2000 else { throw Failure.oversized }
            let output = root.appendingPathComponent(path)
            try fm.createDirectory(at: output.deletingLastPathComponent(), withIntermediateDirectories: true)
            _ = try archive.extract(entry, to: output)
            if isXMLPart(path) {
                guard var xml = String(data: try Data(contentsOf: output), encoding: .utf8) else { throw Failure.invalidXML }
                if path == "word/document.xml", let tableRows { xml = try expandTable(in: xml, rows: tableRows) }
                for (key, value) in fields {
                    guard !key.isEmpty, key.utf8.allSatisfy({ (65...90).contains($0) || $0 == 95 }) else { throw Failure.invalidTemplate }
                    guard !value.unicodeScalars.contains(where: { $0.value < 32 && $0.value != 9 && $0.value != 10 && $0.value != 13 }) else { throw Failure.invalidXML }
                    let marker = "{{\(key)}}"
                    if xml.contains(marker) { found.insert(key); xml = xml.replacingOccurrences(of: marker, with: wordText(value)) }
                }
                let expression = try NSRegularExpression(pattern: "\\{\\{[A-Z_]+\\}\\}")
                if expression.firstMatch(in: xml, range: NSRange(xml.startIndex..., in: xml)) != nil { throw Failure.unresolvedField }
                // لا نحفظ مستنداً قد يبدو ناجحاً لكنه يحتوي محارف XML غير صالحة.
                let parser = XMLParser(data: Data(xml.utf8))
                parser.shouldResolveExternalEntities = false
                let markerCheck = ParagraphMarkerCheck()
                parser.delegate = markerCheck
                guard parser.parse() else { throw Failure.invalidXML }
                guard !markerCheck.unresolved else { throw Failure.unresolvedField }
                try Data(xml.utf8).write(to: output, options: .atomic)
            }
            paths.append(path)
        }
        for key in fields.keys where !found.contains(key) { throw Failure.missingField(key) }
        guard !fm.fileExists(atPath: destination.path) else { throw Failure.invalidTemplate }
        var complete = false
        defer { if !complete { try? fm.removeItem(at: destination) } }
        let result = try Archive(url: destination, accessMode: .create)
        for path in paths.sorted() {
            try Task.checkCancellation()
            try result.addEntry(with: path, relativeTo: root, compressionMethod: .deflate)
        }
        complete = true
    }
}
