import Foundation
import ZIPFoundation

nonisolated struct ImportedEmployee: Sendable {
    var name: String
    var rank: String
    var workplace: String
    var netSalary: Double?
}

nonisolated struct ImportedDeduction: Sendable {
    var employeeName: String
    var title: String
    var amount: Double?
    var note: String
}

nonisolated struct ImportResult: Sendable {
    var employees: [ImportedEmployee] = []
    var deductions: [ImportedDeduction] = []

    mutating func merge(_ other: ImportResult) {
        employees += other.employees
        deductions += other.deductions
    }
}

nonisolated enum ImportError: LocalizedError {
    case cannotOpen(String)
    case noData(String)

    var errorDescription: String? {
        switch self {
        case .cannotOpen(let file):
            return tr("تعذّر فتح الملف \(file). تأكد أنه بصيغة xlsx أو csv.", "Couldn't open \(file). Make sure it's an xlsx or csv file.")
        case .noData(let file):
            return tr("لم يُعثر في \(file) على عمود \"الاسم\" مع عمود صافي الراتب أو مبلغ الاستقطاع.", "\(file) has no \"name\" column with a net salary or deduction amount column.")
        }
    }
}

/// يقرأ ملفات Excel (xlsx) أو CSV ويتعرف على الأوراق والأعمدة من العناوين.
///
/// - ورقة فيها الاسم + صافي الراتب ← ورقة موظفين (أعمدة الاستقطاع داخلها لا تُقرأ).
/// - ورقة فيها الاسم + المبلغ (بدون صافي راتب) ← ورقة الاستقطاعات، وهي المصدر الوحيد للاستقطاعات.
///
/// ملف xlsx يُقرأ صفاً صفاً (XMLParser) لأن ورقة رواتب كبيرة قد تتجاوز 50 ميغابايت من XML.
nonisolated enum ExcelImporter {

    static func load(url: URL, progress: ProgressSink? = nil) throws -> ImportResult {
        var result = ImportResult()
        switch url.pathExtension.lowercased() {
        case "csv", "txt":
            let sheet = SheetParser()
            for row in try readCSV(url) { sheet.consume(row) }
            result.merge(sheet.result)
            progress?(1)
        default:
            try readXLSX(url, into: &result, progress: progress)
        }
        if result.employees.isEmpty && result.deductions.isEmpty {
            throw ImportError.noData(url.lastPathComponent)
        }
        return result
    }

    // MARK: - xlsx

    private static func readXLSX(_ url: URL, into result: inout ImportResult, progress: ProgressSink?) throws {
        let archive: Archive
        do {
            archive = try Archive(url: url, accessMode: .read)
        } catch {
            throw ImportError.cannotOpen(url.lastPathComponent)
        }

        // كل جزء XML يُفك إلى ملف مؤقت ثم يُقرأ منه بالتدريج، بدل تحميله كاملاً في الذاكرة
        // (ملف xlsx بحجم 9 ميغابايت قد يصير مئات الميغابايتات من XML).
        let tempDir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tempDir) }

        let sheets = archive.filter {
            $0.path.hasPrefix("xl/worksheets/") && $0.path.hasSuffix(".xml") && !$0.path.contains("_rels")
        }
        guard !sheets.isEmpty else { throw ImportError.cannotOpen(url.lastPathComponent) }

        // التقدّم بحسب حجم أجزاء XML: النصوص المشتركة ثم كل ورقة (فكّ الضغط ثم القراءة).
        let stringsEntry = archive["xl/sharedStrings.xml"]
        let total = Double(max(1, (stringsEntry?.uncompressedSize ?? 0) + sheets.reduce(0) { $0 + $1.uncompressedSize }))
        var done = 0.0

        var sharedStrings: [String] = []
        if let entry = stringsEntry {
            let file = try extract(entry, from: archive, to: tempDir)
            sharedStrings = try SharedStringsReader.read(file)
            try? FileManager.default.removeItem(at: file)
            done += Double(entry.uncompressedSize)
            progress?(done / total)
        }

        for entry in sheets {
            let sheet = SheetParser()
            let size = Double(entry.uncompressedSize)
            let file = try extract(entry, from: archive, to: tempDir)
            progress?((done + size * 0.15) / total)
            let base = done
            try autoreleasepool {
                try WorksheetReader.read(file, sharedStrings: sharedStrings, onProgress: { read in
                    progress?((base + size * (0.15 + 0.85 * read)) / total)
                }) {
                    sheet.consume($0)
                }
            }
            try? FileManager.default.removeItem(at: file)
            result.merge(sheet.result)
            done += size
            progress?(done / total)
        }
    }

    private static func extract(_ entry: Entry, from archive: Archive, to dir: URL) throws -> URL {
        let file = dir.appendingPathComponent(UUID().uuidString + ".xml")
        _ = try archive.extract(entry, to: file, skipCRC32: true)
        return file
    }

    // MARK: - csv

    private static func readCSV(_ url: URL) throws -> [[String]] {
        let data = try Data(contentsOf: url)
        let windowsArabic = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(
            CFStringEncoding(CFStringEncodings.windowsArabic.rawValue)))
        guard var content = String(data: data, encoding: .utf8) ?? String(data: data, encoding: windowsArabic) else {
            throw ImportError.cannotOpen(url.lastPathComponent)
        }
        if content.hasPrefix("\u{FEFF}") { content.removeFirst() }

        let firstLine = content.prefix { $0 != "\n" }
        let separator: Character = firstLine.filter { $0 == ";" }.count > firstLine.filter { $0 == "," }.count ? ";" : ","

        var rows: [[String]] = []
        var row: [String] = []
        var field = ""
        var inQuotes = false
        let chars = Array(content)
        var i = 0
        while i < chars.count {
            let char = chars[i]
            i += 1
            if inQuotes {
                if char == "\"" {
                    if i < chars.count, chars[i] == "\"" {   // "" داخل الاقتباس تعني علامة " واحدة
                        field.append("\"")
                        i += 1
                    } else {
                        inQuotes = false
                    }
                } else {
                    field.append(char)
                }
            } else if char == "\"" {
                inQuotes = true
            } else if char == separator {
                row.append(field); field = ""
            } else if char == "\n" || char == "\r\n" {
                row.append(field); field = ""
                rows.append(row); row = []
            } else if char != "\r" {
                field.append(char)
            }
        }
        if !field.isEmpty || !row.isEmpty {
            row.append(field)
            rows.append(row)
        }
        return rows
    }
}

// MARK: - التعرف على الأعمدة

/// يستقبل صفوف ورقة واحدة بالترتيب: يبحث عن صف العناوين ثم يقرأ البيانات.
nonisolated final class SheetParser {

    enum Synonyms {
        static let name = ["الاسم", "اسم الموظف", "الاسم الكامل", "الاسم الثلاثي", "الاسم الرباعي", "Name_m", "name"]
        static let rank = ["الرتبة", "الرتبه", "rank"]
        static let netSalary = ["صافي الراتب", "الراتب الصافي", "الصافي", "صافي", "net salary", "net"]
        static let workplace = ["محل العمل", "مكان العمل", "جهة العمل", "NO_markaz", "المركز", "القسم", "الوحدة", "الدائرة", "اسم المديرية", "workplace"]
        static let amount = ["مبلغ الاستقطاع", "مبلغ القسط", "المبلغ", "مبلغ", "القسط", "amount"]
        static let title = ["نوع الاستقطاع", "جهة الاستقطاع", "اسم الفرع", "الجهة", "اسم الاستقطاع", "الاستقطاع", "البيان", "السبب", "type"]
        static let note = ["الملاحظات", "ملاحظات", "ملاحظة", "note"]
    }

    private struct EmployeeColumns {
        var name: Int
        var rank: Int?
        var workplace: Int?
        var netSalary: Int
    }

    private struct DeductionColumns {
        var name: Int
        var amount: Int
        var title: Int?
        var note: Int?
    }

    private enum Mode {
        case findingHeader(rowsSeen: Int)
        case employees(EmployeeColumns)
        case deductions(DeductionColumns)
        case skip
    }

    private(set) var result = ImportResult()
    private var mode = Mode.findingHeader(rowsSeen: 0)
    private let nameHeaders = Set(Synonyms.name.map(ArabicText.normalize))

    func consume(_ row: [String]) {
        switch mode {
        case .findingHeader(let seen):
            if let detected = detectHeader(row) {
                mode = detected
            } else {
                mode = seen >= 20 ? .skip : .findingHeader(rowsSeen: seen + 1)
            }
        case .employees(let columns):
            readEmployee(row, columns)
        case .deductions(let columns):
            readDeduction(row, columns)
        case .skip:
            break
        }
    }

    private func detectHeader(_ row: [String]) -> Mode? {
        let headers = row.map(ArabicText.normalize)
        var used = Set<Int>()
        guard let name = Self.column(Synonyms.name, in: headers, used: &used, exactOnly: true) else { return nil }

        if let net = Self.column(Synonyms.netSalary, in: headers, used: &used) {
            let rank = Self.column(Synonyms.rank, in: headers, used: &used)
            let workplace = Self.column(Synonyms.workplace, in: headers, used: &used)
            return .employees(EmployeeColumns(name: name, rank: rank, workplace: workplace, netSalary: net))
        }
        if let amount = Self.column(Synonyms.amount, in: headers, used: &used) {
            let title = Self.column(Synonyms.title, in: headers, used: &used)
            let note = Self.column(Synonyms.note, in: headers, used: &used)
            return .deductions(DeductionColumns(name: name, amount: amount, title: title, note: note))
        }
        return .skip
    }

    private func readEmployee(_ row: [String], _ columns: EmployeeColumns) {
        let name = cell(row, columns.name)
        guard !name.isEmpty, !nameHeaders.contains(ArabicText.normalize(name)) else { return }

        result.employees.append(ImportedEmployee(
            name: name,
            rank: cell(row, columns.rank),
            workplace: cell(row, columns.workplace),
            netSalary: ArabicText.number(from: cell(row, columns.netSalary))
        ))
    }

    private func readDeduction(_ row: [String], _ columns: DeductionColumns) {
        let name = cell(row, columns.name)
        guard !name.isEmpty, !nameHeaders.contains(ArabicText.normalize(name)) else { return }

        let amount = ArabicText.number(from: cell(row, columns.amount))
        let title = cell(row, columns.title)
        guard amount != nil || !title.isEmpty else { return }
        result.deductions.append(ImportedDeduction(
            employeeName: name,
            title: title.isEmpty ? "استقطاع" : title,
            amount: amount,
            note: cell(row, columns.note)
        ))
    }

    private func cell(_ row: [String], _ col: Int?) -> String {
        guard let col, col < row.count else { return "" }
        return row[col].trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// يبحث بترتيب المرادفات: التطابق التام أولاً ثم احتواء العنوان على الكلمة.
    private static func column(_ synonyms: [String], in headers: [String], used: inout Set<Int>, exactOnly: Bool = false) -> Int? {
        let synonyms = synonyms.map(ArabicText.normalize)
        for exact in exactOnly ? [true] : [true, false] {
            for synonym in synonyms {
                if let col = headers.indices.first(where: { col in
                    !used.contains(col) && !headers[col].isEmpty
                        && (exact ? headers[col] == synonym : headers[col].contains(synonym))
                }) {
                    used.insert(col)
                    return col
                }
            }
        }
        return nil
    }
}

// MARK: - قراءة XML

private nonisolated func localName(_ qualified: String) -> Substring {
    qualified.split(separator: ":").last ?? Substring(qualified)
}

/// يقرأ xl/sharedStrings.xml: كل <si> نص واحد (قد يتكون من عدة <t>).
nonisolated final class SharedStringsReader: NSObject, XMLParserDelegate {
    private var strings: [String] = []
    private var current = ""
    private var inText = false
    private var phoneticDepth = 0

    static func read(_ file: URL) throws -> [String] {
        let reader = SharedStringsReader()
        guard let stream = InputStream(url: file) else { throw ImportError.cannotOpen("sharedStrings.xml") }
        let parser = XMLParser(stream: stream)
        parser.delegate = reader
        guard parser.parse() else { throw parser.parserError ?? ImportError.cannotOpen("sharedStrings.xml") }
        return reader.strings
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?,
                qualifiedName qName: String?, attributes: [String: String] = [:]) {
        switch localName(elementName) {
        case "si": current = ""
        case "t": inText = phoneticDepth == 0
        case "rPh": phoneticDepth += 1                 // نص النطق الياباني، لا يدخل في القيمة
        default: break
        }
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        switch localName(elementName) {
        case "si": strings.append(current)
        case "t": inText = false
        case "rPh": phoneticDepth -= 1
        default: break
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if inText { current += string }
    }
}

/// يقرأ ورقة عمل ويستدعي onRow لكل صف (الأعمدة الفارغة تبقى نصاً فارغاً).
nonisolated final class WorksheetReader: NSObject, XMLParserDelegate {
    private let sharedStrings: [String]
    private let onRow: ([String]) -> Void
    private var row: [String] = []
    private var column = 0
    private var nextColumn = 0
    private var type: String?
    private var value = ""
    private var capturing = false
    /// موضع القراءة في الملف لحساب التقدّم.
    private var stream: InputStream?
    private var fileSize: Double = 1
    private var onProgress: ((Double) -> Void)?
    private var rowsRead = 0

    private init(sharedStrings: [String], onRow: @escaping ([String]) -> Void) {
        self.sharedStrings = sharedStrings
        self.onRow = onRow
    }

    static func read(_ file: URL, sharedStrings: [String], onProgress: ((Double) -> Void)? = nil,
                     onRow: @escaping ([String]) -> Void) throws {
        let reader = WorksheetReader(sharedStrings: sharedStrings, onRow: onRow)
        guard let stream = InputStream(url: file) else { throw ImportError.cannotOpen("worksheet") }
        reader.stream = stream
        reader.onProgress = onProgress
        let attributes = try? FileManager.default.attributesOfItem(atPath: file.path)
        reader.fileSize = max(1, (attributes?[.size] as? NSNumber)?.doubleValue ?? 1)
        let parser = XMLParser(stream: stream)
        parser.delegate = reader
        guard parser.parse() else { throw parser.parserError ?? ImportError.cannotOpen("worksheet") }
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?,
                qualifiedName qName: String?, attributes: [String: String] = [:]) {
        switch localName(elementName) {
        case "row":
            row = []
            nextColumn = 0
        case "c":
            column = attributes["r"].map(Self.columnIndex) ?? nextColumn
            type = attributes["t"]
            value = ""
        case "v", "t":
            capturing = true
        default:
            break
        }
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        switch localName(elementName) {
        case "v", "t":
            capturing = false
        case "c":
            var text = value
            if type == "s" {
                text = Int(value).flatMap { $0 < sharedStrings.count ? sharedStrings[$0] : nil } ?? ""
            }
            if row.count <= column {
                row += Array(repeating: "", count: column - row.count + 1)
            }
            row[column] = text
            nextColumn = column + 1
        case "row":
            onRow(row)
            rowsRead += 1
            if rowsRead % 400 == 0, let onProgress,
               let offset = (stream?.property(forKey: .fileCurrentOffsetKey) as? NSNumber)?.doubleValue {
                onProgress(min(1, offset / fileSize))
            }
        default:
            break
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if capturing { value += string }
    }

    /// "A1" → 0، "B7" → 1، "AA3" → 26
    private static func columnIndex(_ reference: String) -> Int {
        var index = 0
        for scalar in reference.unicodeScalars {
            guard (65...90).contains(scalar.value) || (97...122).contains(scalar.value) else { break }
            index = index * 26 + Int(scalar.value & 0xDF) - 64
        }
        return index - 1
    }
}
