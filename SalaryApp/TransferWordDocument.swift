import Foundation
import ZIPFoundation

/// منشئ مستند Word مرن. يبقى التحقق من البيانات وتخطيط صفحات الجداول على عاتق المستدعي.
nonisolated enum TransferWordDocument {
    struct Paragraph: Sendable {
        var text: String
        var font: String = "Arial"
        var halfPoints: Int = 28
        var bold = false
        var centered = false
    }
    struct Table: Sendable {
        var headers: [String]
        var rows: [[String]]
        var font: String = "Arial"
        var halfPoints: Int = 22
        var columnWeights: [Int]? = nil
        var showBorders = true
    }
    enum Block: Sendable {
        case paragraph(Paragraph)
        case table(Table)
        case pageBreak
    }
    enum Failure: LocalizedError {
        case invalidContent, destinationExists
        var errorDescription: String? {
            switch self {
            case .invalidContent: tr("بيانات مستند Word غير صالحة.", "Invalid Word document content.")
            case .destinationExists: tr("ملف Word موجود مسبقاً.", "The Word file already exists.")
            }
        }
    }
    private static func escape(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
    }
    private static func textXML(_ value: String) throws -> String {
        guard value.utf8.count <= 100_000,
              !value.unicodeScalars.contains(where: { $0.value < 32 && $0.value != 9 && $0.value != 10 && $0.value != 13 }) else { throw Failure.invalidContent }
        return value.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
            .components(separatedBy: "\n").map { "<w:t xml:space=\"preserve\">\(escape($0))</w:t>" }.joined(separator: "<w:br/>")
    }
    private static func paragraph(_ p: Paragraph) throws -> String {
        guard !p.font.isEmpty, p.font.count <= 100, (12...96).contains(p.halfPoints) else { throw Failure.invalidContent }
        let alignment = p.centered ? "center" : "right"
        let bold = p.bold ? "<w:b/><w:bCs/>" : ""
        let font = escape(p.font)
        return "<w:p><w:pPr><w:bidi/><w:jc w:val=\"\(alignment)\"/></w:pPr><w:r><w:rPr><w:rFonts w:ascii=\"\(font)\" w:hAnsi=\"\(font)\" w:cs=\"\(font)\"/><w:rtl/>\(bold)<w:sz w:val=\"\(p.halfPoints)\"/><w:szCs w:val=\"\(p.halfPoints)\"/></w:rPr>\(try textXML(p.text))</w:r></w:p>"
    }
    private static func table(_ t: Table) throws -> String {
        let columns = t.headers.isEmpty ? (t.rows.first?.count ?? 0) : t.headers.count
        guard (1...12).contains(columns), t.rows.count <= 50_000,
              t.rows.allSatisfy({ $0.count == columns }),
              t.headers.isEmpty || t.headers.count == columns else { throw Failure.invalidContent }
        let weights = t.columnWeights ?? Array(repeating: 1, count: columns)
        guard weights.count == columns, weights.allSatisfy({ (1...10_000).contains($0) }) else { throw Failure.invalidContent }
        let weightSum = weights.reduce(0, +)
        let widths = weights.map { 10206 * $0 / weightSum }
        guard widths.allSatisfy({ $0 >= 100 }) else { throw Failure.invalidContent }
        var textBytes = 0
        for values in [t.headers] + t.rows {
            for value in values {
                textBytes += value.utf8.count
                guard textBytes <= 10_000_000 else { throw Failure.invalidContent }
            }
        }
        let borders = ["top", "left", "bottom", "right", "insideH", "insideV"]
            .map { "<w:\($0) w:val=\"\(t.showBorders ? "single" : "nil")\" w:sz=\"8\" w:color=\"000000\"/>" }.joined()
        func row(_ cells: [String], header: Bool) throws -> String {
            let result = try cells.enumerated().map { index, text in
                "<w:tc><w:tcPr><w:tcW w:w=\"\(widths[index])\" w:type=\"dxa\"/></w:tcPr>\(try paragraph(Paragraph(text: text, font: t.font, halfPoints: t.halfPoints, bold: header, centered: true)))</w:tc>"
            }.joined()
            return "<w:tr>\(header ? "<w:trPr><w:tblHeader/></w:trPr>" : "")\(result)</w:tr>"
        }
        let headers: String
        if t.headers.isEmpty { headers = "" } else { headers = try row(t.headers, header: true) }
        let rows = try t.rows.map { try row($0, header: false) }.joined()
        let grid = widths.map { "<w:gridCol w:w=\"\($0)\"/>" }.joined()
        return "<w:tbl><w:tblPr><w:bidiVisual/><w:tblW w:w=\"10206\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders>\(borders)</w:tblBorders></w:tblPr><w:tblGrid>\(grid)</w:tblGrid>\(headers)\(rows)</w:tbl>"
    }
    static func write(blocks: [Block], destination: URL) throws {
        guard !blocks.isEmpty, blocks.count <= 1000 else { throw Failure.invalidContent }
        let fm = FileManager.default
        guard !fm.fileExists(atPath: destination.path) else { throw Failure.destinationExists }
        var body = ""
        for block in blocks {
            try Task.checkCancellation()
            switch block {
            case .paragraph(let p): body += try paragraph(p)
            case .table(let t): body += try table(t)
            case .pageBreak: body += "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>"
            }
        }
        let xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>\(body)<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"850\" w:right=\"850\" w:bottom=\"850\" w:left=\"850\" w:header=\"425\" w:footer=\"425\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>"
        let parser = XMLParser(data: Data(xml.utf8)); parser.shouldResolveExternalEntities = false
        guard parser.parse() else { throw Failure.invalidContent }
        let root = fm.temporaryDirectory.appendingPathComponent("word-document-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.protectionKey: FileProtectionType.complete])
        defer { try? fm.removeItem(at: root) }
        let files = [
            "[Content_Types].xml": "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
            "_rels/.rels": "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            "word/document.xml": xml
        ]
        for (path, content) in files {
            let url = root.appendingPathComponent(path)
            try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.protectionKey: FileProtectionType.complete])
            try Data(content.utf8).write(to: url, options: [.atomic, .completeFileProtection])
        }
        var complete = false
        defer { if !complete { try? fm.removeItem(at: destination) } }
        let archive = try Archive(url: destination, accessMode: .create)
        for path in files.keys.sorted() {
            try Task.checkCancellation()
            try archive.addEntry(with: path, relativeTo: root, compressionMethod: .deflate)
        }
        try fm.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: destination.path)
        complete = true
    }
}
