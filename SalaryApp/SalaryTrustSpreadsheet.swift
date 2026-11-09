import Foundation
import ZIPFoundation

/// قراءة ملف الأمانات محلياً؛ أي صف غير واضح يوقف الاستيراد دون حفظ جزئي.
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
        let uses1904: Bool
        if let workbook = archive["xl/workbook.xml"] { uses1904 = try SalaryTrustWorkbookDates.read(extract(workbook)) }
        else { uses1904 = false }
        var result: [SalaryTrust.Entry] = []
        for sheet in sheets {
            let file = try extract(sheet)
            var rows = SalaryTrustImportRows()
            try WorksheetReader.read(file, sharedStrings: strings, transformCell: { column, type, value in
                column == rows.dateColumn ? SalaryTrustExcelDate.text(value, cellType: type, uses1904: uses1904) : value
            }) { row in rows.append(row) }
            if rows.foundHeader {
                let entries = try rows.result().entries
                guard result.count <= 100_000 - entries.count else { throw SalaryTrust.invalid(tr("عدد سجلات الأمانات كبير جداً.", "Too many trust entries.")) }
                result += entries
            }
        }
        guard !result.isEmpty else { throw SalaryTrust.invalid(tr("تعذّر العثور على بيانات الأمانات في الملف.", "Could not find trust entries in the file.")) }
        let ledger = SalaryTrust.Ledger(version: 2, entries: result, openingImported: true)
        try SalaryTrust.validate(ledger)
        return ledger
    }
}
