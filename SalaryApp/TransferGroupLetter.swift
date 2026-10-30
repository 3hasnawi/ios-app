import UIKit
import PDFKit
import CryptoKit

/// إعداد تجريبي للكتاب والجدول وCSV من مجموعة واحدة؛ غير متاح للمشاركة قبل اختبار القوالب.
nonisolated enum TransferGroupLetter {
    struct Result: Sendable { let letter: URL; let csv: URL }
    enum Failure: LocalizedError {
        case incomplete, unresolved, accountMismatch, reference, invalidTable
        var errorDescription: String? {
            switch self {
            case .incomplete: tr("بيانات المجموعة أو الشهر غير مكتملة.", "The group or month data is incomplete.")
            case .unresolved: tr("توجد جهة غير مصنفة أو بياناتها غير مكتملة.", "An entity is unclassified or incomplete.")
            case .accountMismatch: tr("حساب الجهة الدافعة لا يطابق مرجع الكتاب.", "The payer account does not match the letter reference.")
            case .reference: tr("تعذّر التحقق من حقول مرجع الكتاب.", "Could not verify the letter reference fields.")
            case .invalidTable: tr("تعذّر ضم جدول المجموعة للكتاب.", "Could not append the group table to the letter.")
            }
        }
    }
    private struct Layout {
        let id: String
        let amount: CGRect
        let words: [CGRect]
        let years: [CGRect]
        let numericMonths: [CGRect]
        let monthNames: [CGRect]
        let count: CGRect
        let payer: CGRect
    }
    private static func layout(_ category: TransferCategory) throws -> Layout {
        switch category {
        case .rafidain: Layout(id: "rafidain_group", amount: CGRect(x: 342.91, y: 305.224, width: 79.986, height: 15.654), words: [CGRect(x: 167.78, y: 305.42, width: 155.423, height: 15.543), CGRect(x: 290.534, y: 321.5, width: 270.559, height: 15.543)], years: [CGRect(x: 17.4, y: 180.902, width: 40.356, height: 19.926), CGRect(x: 45.12, y: 353.66, width: 31.65, height: 15.543), CGRect(x: 468.46, y: 698.08, width: 35.97, height: 17.667), CGRect(x: 312.05, y: 698.08, width: 35.97, height: 17.667), CGRect(x: 94.224, y: 697.976, width: 36.054, height: 17.796)], numericMonths: [CGRect(x: 67.344, y: 180.902, width: 20.196, height: 19.926), CGRect(x: 512.86, y: 698.08, width: 17.97, height: 17.667), CGRect(x: 356.59, y: 698.08, width: 17.97, height: 17.667)], monthNames: [CGRect(x: 126.352, y: 353.66, width: 36.434, height: 15.543), CGRect(x: 143.308, y: 698.2, width: 63.552, height: 17.667)], count: CGRect(x: 353.11, y: 436.25, width: 12.44, height: 12.22), payer: CGRect(x: 385.99, y: 337.58, width: 170.685, height: 15.543))
        case .otherBanks: Layout(id: "other_banks_group", amount: CGRect(x: 333.91, y: 305.224, width: 92.355, height: 15.654), words: [CGRect(x: 168.271, y: 305.42, width: 145.946, height: 15.543), CGRect(x: 246.803, y: 321.5, width: 314.256, height: 15.543)], years: [CGRect(x: 17.4, y: 180.902, width: 40.356, height: 19.926), CGRect(x: 45.12, y: 353.66, width: 31.65, height: 15.543), CGRect(x: 468.46, y: 698.08, width: 35.97, height: 17.667), CGRect(x: 312.05, y: 698.08, width: 35.97, height: 17.667), CGRect(x: 94.224, y: 697.976, width: 36.054, height: 17.796)], numericMonths: [CGRect(x: 67.344, y: 180.902, width: 20.196, height: 19.926), CGRect(x: 512.86, y: 698.08, width: 17.97, height: 17.667), CGRect(x: 356.59, y: 698.08, width: 17.97, height: 17.667)], monthNames: [CGRect(x: 126.352, y: 353.66, width: 36.434, height: 15.543), CGRect(x: 143.308, y: 698.2, width: 63.552, height: 17.667)], count: CGRect(x: 358.51, y: 436.25, width: 12.44, height: 12.22), payer: CGRect(x: 385.99, y: 337.58, width: 170.685, height: 15.543))
        default: throw Failure.incomplete
        }
    }
    private static let monthNames = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"]
    private static func selected(_ rect: CGRect, _ page: PDFPage, _ height: CGFloat) -> String {
        let box = CGRect(x: rect.minX - 0.2, y: height - rect.maxY - 0.2, width: rect.width + 0.4, height: rect.height + 0.4)
        return page.selection(for: box)?.string?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }
    private static func ascii(_ input: String) -> String {
        ArabicText.latinDigits(input).uppercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
    }
    private static func digits(_ input: String) -> String { ArabicText.latinDigits(input).filter { $0.isASCII && $0.isNumber } }
    private static func slot(_ rect: CGRect, _ font: String, _ size: CGFloat) -> TransferPDFTextSlot {
        TransferPDFTextSlot(rect: rect, fontName: font, fontSize: size, alignment: .right)
    }
    static func make(allEntities: [TransferEntity], category: TransferCategory, month: String,
                     payer: String, payerIBAN: String, references: Data) async throws -> Result {
        let style = try layout(category)
        let period = try TransferLetterPeriod(month)
        guard !allEntities.isEmpty, Set(allEntities.map(\.id)).count == allEntities.count,
              allEntities.allSatisfy({ $0.group.month == month && $0.category != .unresolved }) else { throw Failure.unresolved }
        let entries = allEntities.filter { $0.category == category }
        guard !entries.isEmpty, entries.allSatisfy({ TransferRules.issues($0).isEmpty }) else { throw Failure.incomplete }
        let sum = entries.reduce(0.0) { $0 + $1.group.total }
        guard sum.isFinite, sum > 0, sum.rounded() == sum, sum <= 9_007_199_254_740_991 else { throw Failure.incomplete }
        let csvData = try TransferRules.csv(entries, category: category, month: month, payer: payer, payerIBAN: payerIBAN)
        let task = Task.detached(priority: .userInitiated) { try TransferReferencePack.load([style.id], from: references) }
        let source = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        try Task.checkCancellation()
        guard let raw = source[style.id], let doc = PDFDocument(data: raw), !doc.isLocked, doc.pageCount == 2,
              let page = doc.page(at: 0) else { throw Failure.reference }
        let size = page.bounds(for: .mediaBox)
        guard abs(size.width - 595.3) < 2, abs(size.height - 841.9) < 2 else { throw Failure.reference }
        guard ascii(selected(style.payer, page, size.height)) == ascii(payerIBAN) else { throw Failure.accountMismatch }
        let amount = selected(style.amount, page, size.height).trimmingCharacters(in: CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn: "()")))
        let years = style.years.map { digits(selected($0, page, size.height)) }
        let months = style.numericMonths.compactMap { Int(digits(selected($0, page, size.height))) }
        guard AnnualDeductionAmount.parse(amount) != nil,
              style.words.allSatisfy({ !selected($0, page, size.height).isEmpty }),
              years.allSatisfy({ $0.count == 4 }), Set(years).count == 1,
              months.count == 3, months.allSatisfy({ (1...12).contains($0) }), Set(months).count == 1,
              style.monthNames.allSatisfy({ !selected($0, page, size.height).isEmpty }),
              let oldCount = Int(digits(selected(style.count, page, size.height))), oldCount > 0 else { throw Failure.reference }
        let masks = [style.amount, style.count] + style.words + style.years + style.numericMonths + style.monthNames
        let digest = SHA256.hash(data: raw).map { String(format: "%02x", $0) }.joined()
        let blank = try await TransferPDFBackground.make(reference: raw, sha256: digest, pageIndex: 0, masks: masks)
        let formatter = NumberFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.numberStyle = .decimal; formatter.maximumFractionDigits = 0
        guard let numeric = formatter.string(from: NSNumber(value: sum)) else { throw Failure.incomplete }
        let words = try ArabicAmountWords.dinars(Int64(sum))
        // توسعة اسم الشهر داخل مساحة بيضاء في المرجع، دون تغطية الفواصل الثابتة.
        let firstName = CGRect(x: 86.1, y: style.monthNames[0].minY,
                               width: style.monthNames[0].maxX - 86.1, height: style.monthNames[0].height)
        var flows = [
            TransferPDFTextFlow(value: numeric, slots: [slot(style.amount, "Arial-BoldMT", 14.04)]),
            TransferPDFTextFlow(value: words, slots: style.words.map { slot($0, "TimesNewRomanPSMT", 14.04) }, keepLastWordOnLastLine: true),
            TransferPDFTextFlow(value: monthNames[period.month - 1], slots: [slot(firstName, "TimesNewRomanPS-BoldMT", 14.04)]),
            TransferPDFTextFlow(value: monthNames[period.month - 1], slots: [slot(style.monthNames[1], "TimesNewRomanPS-BoldMT", 16)]),
            TransferPDFTextFlow(value: String(entries.count), slots: [slot(style.count, "TimesNewRomanPS-BoldMT", 11)])
        ]
        let yearFonts = ["TimesNewRomanPS-BoldMT", "TimesNewRomanPS-BoldMT", "TimesNewRomanPS-BoldMT", "TimesNewRomanPS-BoldMT", "Arial-BoldMT"]
        let yearSizes: [CGFloat] = [18, 14.04, 16, 16, 16]
        for i in style.years.indices { flows.append(TransferPDFTextFlow(value: String(period.year), slots: [slot(style.years[i], yearFonts[i], yearSizes[i])])) }
        for i in style.numericMonths.indices { flows.append(TransferPDFTextFlow(value: String(period.month), slots: [slot(style.numericMonths[i], "TimesNewRomanPS-BoldMT", i == 0 ? 18 : 16)])) }
        let letter = try await TransferPDFOverlay.make(blankReference: blank, flows: flows)
        defer { try? FileManager.default.removeItem(at: letter.deletingLastPathComponent()) }
        let table = try await TransferGroupTablePDF.make(entities: entries, category: category, month: month)
        defer { try? FileManager.default.removeItem(at: table.deletingLastPathComponent()) }
        try Task.checkCancellation()
        return try await PDFWork.run {
            guard let first = PDFDocument(url: letter), first.pageCount == 1, let firstPage = first.page(at: 0),
                  let second = PDFDocument(url: table), second.pageCount == 1, let secondPage = second.page(at: 0),
                  let firstCopy = firstPage.copy() as? PDFPage, let secondCopy = secondPage.copy() as? PDFPage else { throw Failure.invalidTable }
            let merged = PDFDocument(); merged.insert(firstCopy, at: 0); merged.insert(secondCopy, at: 1)
            guard merged.pageCount == 2, let data = merged.dataRepresentation() else { throw Failure.invalidTable }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("group-package-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            var complete = false
            defer { if !complete { try? FileManager.default.removeItem(at: folder) } }
            let pdf = folder.appendingPathComponent("كتاب المجموعة.pdf")
            let csv = folder.appendingPathComponent(category == .rafidain ? "Rafidain.csv" : "OtherBanks.csv")
            try data.write(to: pdf, options: [.atomic, .completeFileProtection])
            try csvData.write(to: csv, options: [.atomic, .completeFileProtection])
            complete = true
            return Result(letter: pdf, csv: csv)
        }
    }
}
