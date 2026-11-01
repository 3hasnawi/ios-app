import Foundation
import ZIPFoundation

/// قراءة افتتاح الأمانات محلياً؛ أي صف غير واضح يوقف الاستيراد دون حفظ جزئي.
nonisolated enum SalaryTrustSpreadsheet {
    static func read(_ url: URL) throws -> SalaryTrust.Ledger {
        let granted = url.startAccessingSecurityScopedResource()
        defer { if granted { url.stopAccessingSecurityScopedResource() } }
        let attrs = try FileManager.default.attributesOfItem(atPath: url.path)
        guard let bytes = (attrs[.size] as? NSNumber)?.int64Value, bytes > 0, bytes <= 32 * 1024 * 1024 else {
            throw SalaryTrust.invalid(tr("حجم ملف الأمانات غير مدعوم.", "Unsupported trust spreadsheet size."))
        }
        let archive = try Archive(url: url, accessMode: .read)
        let entries = Array(archive)
        guard Set(entries.map(\.path)).count == entries.count,
              entries.allSatisfy({ $0.uncompressedSize <= 128 * 1024 * 1024 }),
              entries.reduce(UInt64(0), { $0 + $1.uncompressedSize }) <= 256 * 1024 * 1024 else {
            throw SalaryTrust.invalid(tr("ملف Excel مكرر المحتويات أو كبير جداً.", "The Excel file contains duplicate entries or is too large."))
        }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("trust-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        func extract(_ e: Entry) throws -> URL {
            let target = dir.appendingPathComponent(UUID().uuidString + ".xml")
            _ = try archive.extract(e, to: target)
            return target
        }
        let strings: [String]
        if let e = archive["xl/sharedStrings.xml"] { strings = try SharedStringsReader.read(extract(e)) }
        else { strings = [] }
        let sheets = entries.filter { $0.path.hasPrefix("xl/worksheets/") && $0.path.hasSuffix(".xml") && !$0.path.contains("_rels") }
        guard !sheets.isEmpty else { throw SalaryTrust.invalid(tr("لم توجد أوراق في الملف.", "No worksheets were found.")) }
        var result: [SalaryTrust.Entry] = []
        var found = false, problems: [String] = []
        for sheet in sheets {
            let file = try extract(sheet)
            var headers: [String: Int]?, rowNumber = 0
            try WorksheetReader.read(file, sharedStrings: strings) { row in
                rowNumber += 1
                guard rowNumber <= 100_000, row.count <= 500 else { problems.append(tr("عدد صفوف الملف أو أعمدته كبير جداً.", "The file has too many rows or columns.")); return }
                func cell(_ i: Int?) -> String {
                    guard let i, i >= 0, i < row.count else { return "" }
                    return row[i].trimmingCharacters(in: .whitespacesAndNewlines)
                }
                if headers == nil {
                    guard rowNumber <= 20 else { return }
                    let normalized = row.map(ArabicText.normalize)
                    func index(_ label: String) -> Int? { normalized.firstIndex(of: ArabicText.normalize(label)) }
                    guard let name = index("الاسم الكامل"), let amount = index("صافي الراتب"),
                          let month = index("الشهر"), let year = index("السنة") else { return }
                    var map = ["name": name, "amount": amount, "month": month, "year": year]
                    map["rank"] = index("الرتبة"); map["workplace"] = index("الفوج او المديرية")
                    map["note"] = index("ملاحظات4"); map["note2"] = index("الملاحظات"); map["status"] = index("الحالة")
                    headers = map; found = true; return
                }
                guard let h = headers else { return }
                let name = cell(h["name"])
                if name.isEmpty && cell(h["amount"]).isEmpty && cell(h["month"]).isEmpty && cell(h["year"]).isEmpty { return }
                let period = MonthName.sortKey(cell(h["month"]) + " " + cell(h["year"]))
                let status = cell(h["status"])
                let normalized = ArabicText.normalize(status)
                let paid = normalized == ArabicText.normalize("صرف")
                let pending = normalized == ArabicText.normalize("قطع علاقة تقاعد")
                guard !name.isEmpty, period > 0, period <= SalaryTrust.openingCutoff,
                      let number = ArabicText.number(from: cell(h["amount"])), number.isFinite,
                      number > 0, number.rounded() == number, number <= Double(SalaryTrust.maximum),
                      status.isEmpty || paid || pending else {
                    problems.append(tr("صف \(rowNumber): تحقق من الاسم والشهر والمبلغ والحالة.", "Row \(rowNumber): check the name, month, amount and status.")); return
                }
                let notes = cell(h["note2"])
                result.append(.init(id: UUID().uuidString, name: name, rank: cell(h["rank"]), workplace: cell(h["workplace"]), year: period / 100, month: period % 100,
                                    amount: Int64(number), caseStatus: cell(h["note"]), note: notes, importedStatus: status, legacyPaid: paid, reviewPending: pending, payments: []))
            }
        }
        guard found, !result.isEmpty, problems.isEmpty else {
            throw SalaryTrust.invalid(problems.first ?? tr("تعذّر العثور على بيانات الأمانات في الملف.", "Could not find trust entries in the file."))
        }
        let ledger = SalaryTrust.Ledger(entries: result, openingImported: true)
        try SalaryTrust.validate(ledger)
        return ledger
    }
}
