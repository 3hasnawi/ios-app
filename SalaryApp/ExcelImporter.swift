import Foundation
import ZIPFoundation

nonisolated struct ImportedEmployee: Sendable {
    var name: String
    var rank: String
    var workplace: String
    var netSalary: Double?
    /// مبالغ من أعمدة ورقة الرواتب لتقاريرها (صندوق الشهداء، الرعاية الصحية...)
    /// ومن أزواج "اسم الاستقطاع / مبلغ الاستقطاع" (مثل شركة عراق الجوال). القيم غير الصفرية فقط.
    var items: [String: Double] = [:]
    /// حقول تقرير الراتب (المخصصات، الاستقطاعات، بيانات الموظف) كما في الملف، غير الفارغة فقط.
    var details: [String: String] = [:]
    /// صف الورقة بكامل أعمدته، منفصل عن الحقول المستخدمة في الحسابات الحالية.
    var rawColumnsData: Data? = nil
}

nonisolated struct ImportedDeduction: Sendable {
    var employeeName: String
    var title: String
    var amount: Double?
    var note: String
    var rank: String = ""
}

nonisolated struct ImportedExecutionAccount: Sendable {
    var entity: String
    var iban: String
    var branch: String
    var swift: String
}

/// خلل في خلية IBAN؛ تبقى القيمة الأصلية ظاهرة في مراجعة الاستيراد لتصحيحها في Excel.
nonisolated struct InvalidExecutionAccount: Sendable, Identifiable {
    enum Reason: Sendable {
        case empty, missingPrefix, wrongLength(Int), invalidCharacters, invalidChecksum

        var explanation: String {
            switch self {
            case .empty: tr("الخلية فارغة.", "The cell is empty.")
            case .missingPrefix: tr("الرقم لا يبدأ بـ IQ، لذلك ليس IBAN عراقياً كاملاً.", "The number doesn't start with IQ, so it isn't a complete Iraqi IBAN.")
            case .wrongLength(let count): tr("الطول \(Num.format(count)) خانة، والمطلوب 23 خانة.", "It has \(count) characters; 23 are required.")
            case .invalidCharacters: tr("يحتوي على رموز غير مقبولة في IBAN.", "It contains characters not allowed in an IBAN.")
            case .invalidChecksum: tr("خانات التحقق في IBAN غير صحيحة.", "The IBAN check digits are invalid.")
            }
        }

        var solution: String {
            switch self {
            case .empty: tr("أدخل IBAN الكامل من مستند البنك بعد ضبط الخلية كنص.", "Enter the full IBAN from the bank document after formatting the cell as Text.")
            case .missingPrefix: tr("استبدل رقم الحساب بـIBAN الكامل من البنك، واضبط خلية Excel كنص؛ لا تضف IQ بالتخمين.",
                                     "Replace the account number with the complete bank-issued IBAN and format the Excel cell as Text; don't guess the IQ prefix.")
            case .wrongLength: tr("راجع الرقم الأصلي لدى البنك وأدخل الخانات الناقصة في خلية نصية؛ لا تضف أصفاراً بالتخمين.",
                                  "Check the bank-issued number and enter the missing characters in a Text cell; don't guess zeros.")
            case .invalidCharacters: tr("الصق IBAN الأصلي كنص دون علامات أو رموز إضافية.", "Paste the original IBAN as Text without extra symbols.")
            case .invalidChecksum: tr("طابق IBAN مع مستند البنك وصحّح الرقم الأصلي.", "Compare the IBAN with the bank document and correct the source number.")
            }
        }
    }

    let location: String
    let entered: String
    let reason: Reason
    var id: String { location }
    var displayValue: String { entered.isEmpty ? tr("خلية فارغة", "Empty cell") : entered }
}

nonisolated struct ImportResult: Sendable {
    var employees: [ImportedEmployee] = []
    var deductions: [ImportedDeduction] = []
    var accounts: [ImportedExecutionAccount] = []
    var invalidAccountDetails: [InvalidExecutionAccount] = []
    var invalidAccounts: Int { invalidAccountDetails.count }
    /// أسباب عدم فهم بعض الأوراق (للتنبيه).
    var problems: [String] = []

    mutating func merge(_ other: ImportResult) {
        employees += other.employees
        deductions += other.deductions
        accounts += other.accounts
        invalidAccountDetails += other.invalidAccountDetails
        problems += other.problems
    }
}

nonisolated enum ImportError: LocalizedError {
    case cannotOpen(String)
    case noData(String)
    /// الملف فُتح لكن لم تُفهم أوراقه: لكل ورقة سبب.
    case unrecognized(String, [String])

    var errorDescription: String? {
        switch self {
        case .cannotOpen(let file):
            return tr("تعذّر فتح الملف \(file): ليس ملف xlsx سليماً (ربما بصيغة xls قديمة أو تالف أو محمي بكلمة سر). افتحه في Excel واحفظه بصيغة xlsx.",
                      "Couldn't open \(file): not a valid xlsx file (maybe old .xls, damaged or password-protected). Open it in Excel and save as xlsx.")
        case .noData(let file):
            return tr("لم يُعثر في \(file) على عمود \"الاسم\" مع عمود صافي الراتب أو مبلغ الاستقطاع.", "\(file) has no \"name\" column with a net salary or deduction amount column.")
        case .unrecognized(let file, let reasons):
            let intro = tr("الملف \(file) غير متوافق مع نظام التطبيق:", "\(file) doesn't match the app's format:")
            let help = tr("المطلوب: رواتب (الاسم/Name_m وصافي الراتب)، أو استقطاعات (الاسم والمبلغ)، أو حسابات جهات (جهة التنفيذ ورقم الايبان). العناوين في أول ٢٠ صفاً.",
                          "Expected: payroll (name and net salary), deductions (name and amount), or entity accounts (entity and IBAN). Headers must be within the first 20 rows.")
            return ([intro] + reasons.map { "• " + $0 } + [help]).joined(separator: "\n")
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
            sheet.finish()
            result.merge(sheet.result)
            progress?(1)
        default:
            try readXLSX(url, into: &result, progress: progress)
        }
        if result.employees.isEmpty && result.deductions.isEmpty && result.accounts.isEmpty && result.invalidAccountDetails.isEmpty {
            throw ImportError.unrecognized(url.lastPathComponent, result.problems.isEmpty
                ? [tr("لا توجد صفوف بيانات في الملف.", "The file has no data rows.")] : result.problems)
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

        for (sheetIndex, entry) in sheets.enumerated() {
            let sheet = SheetParser()
            sheet.sheetName = sheets.count > 1 ? tr("الورقة \(sheetIndex + 1)", "Sheet \(sheetIndex + 1)") : tr("الورقة", "Sheet")
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
            sheet.finish()
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
        static let title = ["جهة التنفيذ", "نوع الاستقطاع", "جهة الاستقطاع", "اسم الفرع", "الجهة المستفيدة", "الجهة", "اسم المصرف", "المصرف",
                            "اسم الاستقطاع", "الاستقطاع", "البيان", "السبب", "type"]
        static let note = ["رقم وتاريخ الاضبارة", "رقم الاضبارة", "الاضبارة", "الملاحظات", "ملاحظات", "ملاحظة", "note"]
        /// صفوف المجاميع في آخر الكشوف ليست أسماء موظفين.
        static let totals = ["المجموع", "مجموع", "الاجمالي", "اجمالي", "المجموع الكلي", "المجموع العام", "الاجمالي العام"]
        /// أعمدة ورقة الرواتب التي تُحفظ لتقاريرها: كل عنوان يحتوي إحدى هذه الكلمات.
        static let itemKeywords = ["صندوق", "شهداء", "رعاية", "نشاط", "نادي", "تامين", "ضريبة", "توقيفات", "تقاعد", "حماية",
                                   "تضمين", "ايراد", "رسم", "جوال", "اتصالات", "استقطاعات المصارف", "مجموع الاستقطاعات"]
        /// أزواج (اسم الجهة، المبلغ) داخل ورقة الرواتب.
        /// أعمدة تقرير الراتب (بنفس أسماء ورقة الرواتب المعتمدة).
        static let reportFields = [
            "الدرجة الوظيفية", "اسم الوكالة", "اسم المديرية", "NO_markaz",
            "الاسمي", "عدد العلاوات", "الخطورة", "الارزاق", "الاستثنائية", "الاطفال", "الزوجية", "الشهادة", "منصب",
            "مخصصات صنف", "نسبة الصنف", "جامعية", "مخصصات نقل", "مؤقتة", "اضافية", "مخصصات اخرى", "لقب علمي", "نسبة من الاسمي",
            "الاستحقاق", "توقيفات تقاعدية", "الرعاية الصحية", "صندوق الشهداء", "نشاط صحي", "نشاط رياضي", "الضريبة", "تضمين",
            "ايراد نهائي", "تامين صحي", "مجموع استقطاعات المصارف", "حماية الاجتماعية", "رسم طابع", "اسم الاستقطاع", "مبلغ الاستقطاع",
            "2اسم الاستقطاع", "2مبلغ الاستقطاع", "% استقطاع 3", "مجموع الاستقطاعات", "استقطاع مؤقت", "اسم استقطاع المؤقته",
            "اسم الشهدة", "اسم الشهادة", "اسم المنصب", "اسم الصنف", "اسم المخصصات الاخرى", "اسم اللقب العلمي",
            "الرقم الوظيفي", "الرقم الموحد", "رقم الضمان الصحي", "طريقة الدفع", "توطين المصرف", "IBAN", "ملاحظات عامه", "ملاحظات",
        ]
        static let pairs: [(String, String)] = [("اسم الاستقطاع", "مبلغ الاستقطاع"), ("2اسم الاستقطاع", "2مبلغ الاستقطاع"),
                                                ("اسم استقطاع المؤقته", "استقطاع مؤقت"), ("اسم استقطاع المؤقتة", "استقطاع مؤقت")]
    }

    private struct EmployeeColumns {
        var name: Int
        var rank: Int?
        var workplace: Int?
        var netSalary: Int
        var headers: [String] = []
        /// (العمود، عنوانه كما في الملف)
        var items: [(Int, String)] = []
        var pairs: [(name: Int, amount: Int, fallback: String)] = []
        /// (العمود، اسم الحقل) لتقرير الراتب.
        var details: [(Int, String)] = []
    }

    private struct DeductionColumns {
        var name: Int
        var amount: Int
        var title: Int?
        var note: Int?
        var rank: Int?
    }

    private struct AccountColumns {
        var entity: Int
        var iban: Int
        var branch: Int?
        var swift: Int?
    }

    private enum Mode {
        case findingHeader(rowsSeen: Int)
        case employees(EmployeeColumns)
        case deductions(DeductionColumns)
        case accounts(AccountColumns)
        case skip
    }

    private(set) var result = ImportResult()
    private var mode = Mode.findingHeader(rowsSeen: 0)
    /// اسم الورقة في رسائل الخطأ.
    var sheetName = ""
    /// أول عناوين رآها مع عمود الاسم، لشرح سبب الرفض.
    private var nameRowHeaders: [String]?
    private var accountHeaderFound = false
    private var rowsRead = 0
    /// لكل عمود مبلغ: كم مرة ظهر كل اسم جهة، لإلحاق المبالغ التي اسمها فارغ بالجهة الغالبة.
    private var pairNames: [String: [String: Int]] = [:]
    private let nameHeaders = Set((Synonyms.name + Synonyms.totals).map(ArabicText.normalize))

    func consume(_ row: [String]) {
        rowsRead += 1
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
        case .accounts(let columns):
            readAccount(row, columns)
        case .skip:
            break
        }
    }

    private func detectHeader(_ row: [String]) -> Mode? {
        let headers = row.map(ArabicText.normalize)
        var used = Set<Int>()
        // ملفات حسابات الجهات مستقلة عن الرواتب، وقد تضم عمود «المبلغ» الفارغ.
        if let entity = Self.column(["جهة التنفيذ", "الجهة"], in: headers, used: &used, exactOnly: true),
           let iban = Self.column(["رقم الايبان", "الايبان", "iban"], in: headers, used: &used) {
            accountHeaderFound = true
            let branch = Self.column(["الفرع", "اسم الفرع"], in: headers, used: &used)
            let swift = Self.column(["swift", "رمز السويفت"], in: headers, used: &used)
            return .accounts(AccountColumns(entity: entity, iban: iban, branch: branch, swift: swift))
        }
        used.removeAll()
        guard let name = Self.column(Synonyms.name, in: headers, used: &used, exactOnly: true) else { return nil }
        nameRowHeaders = row.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }

        if let net = Self.column(Synonyms.netSalary, in: headers, used: &used) {
            let rank = Self.column(Synonyms.rank, in: headers, used: &used)
            let workplace = Self.column(Synonyms.workplace, in: headers, used: &used)
            var columns = EmployeeColumns(name: name, rank: rank, workplace: workplace, netSalary: net)
            columns.headers = row
            for (nameHeader, amountHeader) in Synonyms.pairs {
                let n = ArabicText.normalize(nameHeader), a = ArabicText.normalize(amountHeader)
                guard let nc = headers.firstIndex(of: n), let ac = headers.firstIndex(of: a),
                      !used.contains(nc), !columns.pairs.contains(where: { $0.name == nc }) else { continue }
                // مبلغ بلا اسم جهة يُحفظ باسم عمود المبلغ حتى لا يضيع من المجموع.
                columns.pairs.append((nc, ac, row[ac].trimmingCharacters(in: .whitespacesAndNewlines)))
                used.insert(nc)
                used.insert(ac)
            }
            let keywords = Synonyms.itemKeywords.map(ArabicText.normalize)
            for (col, header) in headers.enumerated() where !used.contains(col) && !header.isEmpty && !header.contains("%") {
                guard keywords.contains(where: { header.contains($0) }), !header.hasPrefix(ArabicText.normalize("اسم")) else { continue }
                let title = row[col].trimmingCharacters(in: .whitespacesAndNewlines)
                columns.items.append((col, title.isEmpty ? header : title))
            }
            let fields = Dictionary(Synonyms.reportFields.map { (ArabicText.normalize($0), $0) }, uniquingKeysWith: { a, _ in a })
            for (col, header) in headers.enumerated() {
                if let key = fields[header], !columns.details.contains(where: { $0.1 == key }) { columns.details.append((col, key)) }
            }
            return .employees(columns)
        }
        if let amount = Self.column(Synonyms.amount, in: headers, used: &used) {
            let title = Self.column(Synonyms.title, in: headers, used: &used)
            let note = Self.column(Synonyms.note, in: headers, used: &used)
            let rank = Self.column(Synonyms.rank, in: headers, used: &used)
            return .deductions(DeductionColumns(name: name, amount: amount, title: title, note: note, rank: rank))
        }
        return .skip
    }

    private func readEmployee(_ row: [String], _ columns: EmployeeColumns) {
        let name = cell(row, columns.name)
        guard !name.isEmpty, !nameHeaders.contains(ArabicText.normalize(name)) else { return }

        var items: [String: Double] = [:]
        for (col, title) in columns.items {
            if let v = ArabicText.number(from: cell(row, col)), v != 0 { items[title, default: 0] += v }
        }
        for pair in columns.pairs {
            guard let v = ArabicText.number(from: cell(row, pair.amount)), v != 0 else { continue }
            let title = cell(row, pair.name)
            if !title.isEmpty { pairNames[pair.fallback, default: [:]][title, default: 0] += 1 }
            // اسم فارغ: يُحفظ مؤقتاً باسم عمود المبلغ، ويُلحق بالجهة الغالبة بعد آخر صف.
            items[title.isEmpty ? pair.fallback : title, default: 0] += v
        }
        var details: [String: String] = [:]
        for (col, key) in columns.details {
            let value = cell(row, col).trimmingCharacters(in: .whitespacesAndNewlines)
            if !value.isEmpty, value != "0" { details[key] = value }
        }
        result.employees.append(ImportedEmployee(
            name: name,
            rank: cell(row, columns.rank),
            workplace: cell(row, columns.workplace),
            netSalary: ArabicText.number(from: cell(row, columns.netSalary)),
            items: items,
            details: details,
            rawColumnsData: PayrollColumnSnapshot.encode(headers: columns.headers, values: row)
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
            note: cell(row, columns.note),
            rank: cell(row, columns.rank)
        ))
    }

    private func readAccount(_ row: [String], _ columns: AccountColumns) {
        let entity = cell(row, columns.entity)
        guard !entity.isEmpty else { return }
        let entered = cell(row, columns.iban)
        let iban = Self.cleanIBAN(entered)
        if let reason = Self.ibanProblem(iban) {
            result.invalidAccountDetails.append(InvalidExecutionAccount(
                location: tr("\(sheetName) · الصف \(Num.format(rowsRead))", "\(sheetName) · row \(rowsRead)"),
                entered: entered, reason: reason))
            return
        }
        result.accounts.append(ImportedExecutionAccount(entity: entity, iban: iban,
                                                       branch: cell(row, columns.branch), swift: cell(row, columns.swift).uppercased()))
    }

    private static func cleanIBAN(_ text: String) -> String {
        ArabicText.latinDigits(text).uppercased().filter { !$0.isWhitespace && $0 != "-" }
    }

    /// IBAN العراقي: طول 23 وحساب mod 97؛ يمنع قبول أرقام Excel المبتورة.
    private static func ibanProblem(_ iban: String) -> InvalidExecutionAccount.Reason? {
        if iban.isEmpty { return .empty }
        if !iban.hasPrefix("IQ") { return .missingPrefix }
        if iban.count != 23 { return .wrongLength(iban.count) }
        if !iban.unicodeScalars.allSatisfy({ (48...57).contains($0.value) || (65...90).contains($0.value) }) { return .invalidCharacters }
        let shifted = String(iban.dropFirst(4)) + String(iban.prefix(4))
        var remainder = 0
        for c in shifted.uppercased().unicodeScalars {
            let value = c.value
            let digits = (65...90).contains(value) ? String(value - 55) : String(value - 48)
            for digit in digits.utf8 { remainder = (remainder * 10 + Int(digit - 48)) % 97 }
        }
        return remainder == 1 ? nil : .invalidChecksum
    }

    /// بعد آخر صف: سبب عدم قبول الورقة إن لم يُقرأ منها شيء.
    func finish() {
        // المبالغ التي خانة اسمها فارغة تُحسب للجهة الأكثر ظهوراً في نفس العمود (مثل شركة عراق الجوال).
        for (fallback, names) in pairNames {
            guard let main = names.max(by: { $0.value < $1.value })?.key else { continue }
            for i in result.employees.indices {
                if let v = result.employees[i].items.removeValue(forKey: fallback) {
                    result.employees[i].items[main, default: 0] += v
                }
            }
        }
        guard result.employees.isEmpty, result.deductions.isEmpty, result.accounts.isEmpty else { return }
        let sheet = sheetName.isEmpty ? tr("الورقة", "Sheet") : sheetName
        if rowsRead == 0 {
            return  // ورقة فارغة: لا داعي لذكرها
        }
        if accountHeaderFound {
            result.problems.append(tr("\(sheet): لم يُعثر على IBAN عراقي صالح؛ راجع الخانات المحفوظة كأرقام في Excel.",
                                      "\(sheet): no valid Iraqi IBAN; check cells stored as numbers in Excel."))
            return
        }
        if let headers = nameRowHeaders {
            let sample = headers.prefix(8).joined(separator: "، ")
            switch mode {
            case .employees: result.problems.append(tr("\(sheet): عناوين الرواتب موجودة لكن لا توجد أسماء تحتها.", "\(sheet): payroll headers found but no names under them."))
            case .deductions: result.problems.append(tr("\(sheet): عناوين الاستقطاعات موجودة لكن لا توجد صفوف صالحة تحتها.", "\(sheet): deduction headers found but no valid rows."))
            default: result.problems.append(tr("\(sheet): فيها عمود الاسم لكن لا يوجد عمود (صافي الراتب) ولا (المبلغ). العناوين الموجودة: \(sample)",
                                               "\(sheet): has a name column but no (net salary) or (amount) column. Headers: \(sample)"))
            }
        } else {
            result.problems.append(tr("\(sheet): لم يُعثر على عمود (الاسم) في أول ٢٠ صفاً.", "\(sheet): no (name) column in the first 20 rows."))
        }
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
