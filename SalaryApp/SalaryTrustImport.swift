import Foundation
#if canImport(FoundationXML)
import FoundationXML
#endif

/// معلومات الأمانات من الملف حصراً؛ لا تُستكمل الحالة أو الملاحظات من الرواتب.
nonisolated struct SalaryTrustImportRows {
    private var headers: [String: Int]?
    private(set) var entries: [SalaryTrust.Entry] = []
    private(set) var foundHeader = false
    private var rowNumber = 0
    private var problem: String?
    var dateColumn: Int? { headers?["date"] }

    mutating func append(_ row: [String]) {
        rowNumber += 1
        guard problem == nil else { return }
        guard rowNumber <= 100_000, row.count <= 500 else {
            problem = tr("عدد صفوف ملف الأمانات أو أعمدته كبير جداً.", "The trust spreadsheet has too many rows or columns."); return
        }
        if headers == nil {
            guard rowNumber <= 20 else { return }
            let labels = row.map(ArabicText.matchKey)
            func index(_ aliases: String...) -> Int? {
                aliases.lazy.compactMap { labels.firstIndex(of: ArabicText.matchKey($0)) }.first
            }
            guard let name = index("الاسم الكامل", "الاسم"), let amount = index("صافي الراتب", "المبلغ"),
                  let month = index("الشهر"), let year = index("السنة") else { return }
            foundHeader = true
            guard let status = index("الحالة", "حالة الصرف") else {
                problem = tr("يجب أن يحتوي ملف الأمانات على عمود الحالة.", "The trust spreadsheet must include a status column."); return
            }
            var map = ["name": name, "amount": amount, "month": month, "year": year, "status": status]
            map["rank"] = index("الرتبة")
            map["workplace"] = index("الفوج او المديرية", "محل العمل")
            map["note"] = index("ملاحظات4", "ملاحظات 4", "ملاحظات")
            map["note2"] = index("الملاحظات", "ملاحظات إضافية")
            map["date"] = index("تاريخ الصرف")
            headers = map; return
        }
        guard let h = headers else { return }
        func cell(_ key: String) -> String {
            guard let index = h[key], row.indices.contains(index) else { return "" }
            return row[index].trimmingCharacters(in: .whitespacesAndNewlines)
        }
        let name = cell("name")
        if name.isEmpty && cell("amount").isEmpty && cell("month").isEmpty && cell("year").isEmpty { return }
        let period = MonthName.sortKey(cell("month") + " " + cell("year"))
        guard !name.isEmpty, period > 0,
              let amount = ArabicText.number(from: cell("amount")), amount > 0,
              amount.rounded() == amount, amount <= Double(SalaryTrust.maximum) else {
            problem = tr("صف \(rowNumber): تحقق من الاسم والشهر والسنة والمبلغ.", "Row \(rowNumber): check the name, month, year and amount."); return
        }
        let status = cell("status")
        let disposition = SalaryTrust.importedDisposition(status)
        let date = cell("date")
        entries.append(.init(id: UUID().uuidString, name: name, rank: cell("rank"), workplace: cell("workplace"),
            year: period / 100, month: period % 100, amount: Int64(amount), caseStatus: cell("note"), note: cell("note2"),
            importedStatus: status, legacyPaid: disposition == .paid, reviewPending: disposition == .review, payments: [],
            importedPaymentDate: date.isEmpty ? nil : date))
    }

    func result() throws -> SalaryTrust.Ledger {
        if let problem { throw SalaryTrust.invalid(problem) }
        guard foundHeader, !entries.isEmpty else {
            throw SalaryTrust.invalid(tr("تعذّر العثور على بيانات الأمانات في الملف.", "Could not find trust entries in the file."))
        }
        let ledger = SalaryTrust.Ledger(version: 2, entries: entries, openingImported: true)
        try SalaryTrust.validate(ledger)
        return ledger
    }
}

/// التواريخ النصية تبقى كما كُتبت، بما فيها الشهر والسنة دون افتراض يوم.
nonisolated enum SalaryTrustExcelDate {
    static func text(_ raw: String, cellType: String?, uses1904: Bool) -> String {
        guard cellType == nil || cellType == "n", let number = Double(raw), number.isFinite,
              number >= (uses1904 ? 0 : 1), number <= 110_000 else { return raw }
        let whole = Int(number.rounded(.down))
        // اليوم الستون في نظام Excel القديم يوم غير موجود في التقويم الميلادي.
        guard uses1904 || whole != 60 else { return raw }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        guard let base = calendar.date(from: DateComponents(year: uses1904 ? 1904 : 1899, month: uses1904 ? 1 : 12, day: uses1904 ? 1 : 31)),
              let day = calendar.date(byAdding: .day, value: whole - (!uses1904 && whole > 60 ? 1 : 0), to: base) else { return raw }
        let formatter = DateFormatter(); formatter.calendar = calendar; formatter.timeZone = calendar.timeZone
        formatter.locale = Locale(identifier: "en_US_POSIX")
        let fraction = number - Double(whole)
        formatter.dateFormat = fraction == 0 ? "yyyy/MM/dd" : "yyyy/MM/dd HH:mm"
        return formatter.string(from: day.addingTimeInterval(fraction * 86_400))
    }
}

nonisolated final class SalaryTrustWorkbookDates: NSObject, XMLParserDelegate {
    private(set) var uses1904 = false
    static func read(_ url: URL) throws -> Bool {
        let reader = SalaryTrustWorkbookDates()
        guard let input = InputStream(url: url) else { throw SalaryTrust.invalid(tr("تعذّر فتح معلومات المصنف.", "Could not read workbook settings.")) }
        let parser = XMLParser(stream: input); parser.delegate = reader
        guard parser.parse() else { throw parser.parserError ?? SalaryTrust.invalid(tr("معلومات المصنف غير صالحة.", "Invalid workbook settings.")) }
        return reader.uses1904
    }
    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes: [String: String] = [:]) {
        if elementName.split(separator: ":").last == "workbookPr" {
            uses1904 = ["1", "true"].contains(attributes["date1904"]?.lowercased() ?? "")
        }
    }
}
