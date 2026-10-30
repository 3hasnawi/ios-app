import Foundation
import ZIPFoundation

/// تدقيق محتوى Word المكتوب بمقارنته بلقطة المصدر، ولا يدّعي التحقق من توزيع الصفحات.
nonisolated enum TransferWordVerification {
    enum Failure: LocalizedError {
        case mismatch
        var errorDescription: String? { tr("محتوى مسودة Word لا يطابق بيانات الكتاب؛ لم تُعرض المعاينة.", "The Word draft content does not match the letter data; the preview was not shown.") }
    }
    private final class Reader: NSObject, XMLParserDelegate {
        private(set) var paragraphs: [String] = []
        private(set) var tables: [[[String]]] = []
        private var paragraph: String?
        private var inText = false
        private var table: [[String]]?
        private var row: [String]?
        private var cell: String?
        private(set) var invalid = false
        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName qName: String?, attributes: [String: String] = [:]) {
            switch name {
            case "w:tbl": if table != nil { invalid = true }; table = []
            case "w:tr": if table == nil || row != nil { invalid = true }; row = []
            case "w:tc": if row == nil || cell != nil { invalid = true }; cell = ""
            case "w:p": if paragraph != nil { invalid = true }; paragraph = ""
            case "w:t": inText = true
            case "w:br": if paragraph != nil { paragraph?.append("\n") }
            default: break
            }
        }
        func parser(_ parser: XMLParser, foundCharacters string: String) { if inText { paragraph?.append(string) } }
        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName qName: String?) {
            switch name {
            case "w:t": inText = false
            case "w:p":
                guard let value = paragraph else { invalid = true; return }
                paragraphs.append(value)
                if cell != nil { cell?.append(value) }
                paragraph = nil
            case "w:tc":
                guard let value = cell, row != nil else { invalid = true; return }
                row?.append(value); cell = nil
            case "w:tr":
                guard let value = row, table != nil else { invalid = true; return }
                table?.append(value); row = nil
            case "w:tbl":
                guard let value = table, row == nil, cell == nil else { invalid = true; return }
                tables.append(value); table = nil
            default: break
            }
        }
    }
    static func verify(url: URL, month: String, amount: Double, payerIBAN: String,
                       text: TransferWordGroupDraft.LetterText, table: TransferWordDocument.Table?,
                       beneficiaryName: String? = nil, beneficiaryAccount: String? = nil,
                       bank: String? = nil, personName: String? = nil, personRank: String? = nil,
                       contractReference: String? = nil) throws {
        let archive = try Archive(url: url, accessMode: .read)
        guard let entry = archive["word/document.xml"], entry.type == .file,
              entry.uncompressedSize <= 80_000_000 else { throw Failure.mismatch }
        var xml = Data(); _ = try archive.extract(entry) { xml.append($0) }
        let reader = Reader(), parser = XMLParser(data: xml)
        parser.shouldResolveExternalEntities = false; parser.delegate = reader
        guard parser.parse(), !reader.invalid else { throw Failure.mismatch }
        let values = try TransferWordValues.make(month: month, amount: amount, custom: .init(), requested: ["AMOUNT", "AMOUNT_WORDS", "MONTH", "YEAR"])
        guard let numeric = values["AMOUNT"], let words = values["AMOUNT_WORDS"],
              let monthName = values["MONTH"], let year = values["YEAR"] else { throw Failure.mismatch }
        let bodies = reader.paragraphs.filter { $0.hasPrefix("نرجو تحويل مبلغ قدره ") }
        guard bodies.count == 1, let body = bodies.first,
              body.contains("(\(numeric))"), body.contains("(\(words))"), body.contains("(\(payerIBAN))"),
              body.contains("(\(monthName) / \(year))"), reader.paragraphs.contains(text.header),
              reader.paragraphs.contains(where: { $0.contains("العدد: \(text.fixedNumber)") }),
              reader.paragraphs.contains(text.footer) else { throw Failure.mismatch }
        for value in [beneficiaryName, beneficiaryAccount, bank, personName, personRank, contractReference].compactMap({ $0 }) {
            guard !value.isEmpty, body.contains(value) else { throw Failure.mismatch }
        }
        let date = try TransferLetterPeriod(month).signatureDate
        let signatures = [text.committee.map { $0 + "\n" + date }]
        guard reader.tables.filter({ $0 == signatures }).count == 1 else { throw Failure.mismatch }
        if let table {
            let expected = (table.headers.isEmpty ? [] : [table.headers]) + table.rows
            guard reader.tables.filter({ $0 == expected }).count == 1 else { throw Failure.mismatch }
        } else {
            guard reader.tables.count == 1 else { throw Failure.mismatch }
        }
    }
}
