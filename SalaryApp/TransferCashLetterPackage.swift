import Foundation
import PDFKit

/// يجمع صفحة كتاب أُنشئت وتُحققت من بياناتها من قبل مع قائمة الكاش.
/// لا يتحقق من مبلغ صفحة الكتاب نفسها؛ يجب ربطه بمولّد يضمن مصدر الصفحة قبل إتاحة التصدير.
nonisolated enum TransferCashLetterPackage {
    enum Failure: LocalizedError {
        case incomplete, mismatch, invalidPDF, saveFailed
        var errorDescription: String? {
            switch self {
            case .incomplete: tr("بيانات كتاب الكاش أو قائمته غير مكتملة.", "The cash letter or list is incomplete.")
            case .mismatch: tr("مجموع قائمة الكاش أو شهرها لا يطابق كتاب الرواتب.", "The cash list total or month does not match the salary letter.")
            case .invalidPDF: tr("تعذّر التحقق من صفحات الكتاب والقائمة.", "Could not verify the letter and list pages.")
            case .saveFailed: tr("تعذّر إنشاء ملف الكتاب وقائمة الكاش.", "Could not create the cash letter and list file.")
            }
        }
    }

    static func make(letter: URL, list: TransferCashList, figures: TransferLetterFigures) async throws -> URL {
        guard let expected = figures.payrollCash, expected.isFinite, expected > 0,
              expected.rounded() == expected, list.total.isFinite, list.total.rounded() == list.total,
              !list.employees.isEmpty else { throw Failure.incomplete }
        guard figures.month == list.month, expected == list.total else { throw Failure.mismatch }
        let appendix = try await TransferCashListPDF.make(list)
        defer { try? FileManager.default.removeItem(at: appendix.deletingLastPathComponent()) }
        try Task.checkCancellation()
        return try await PDFWork.run {
            guard let first = PDFDocument(url: letter), !first.isLocked, first.pageCount == 1,
                  let rest = PDFDocument(url: appendix), !rest.isLocked, rest.pageCount > 0 else { throw Failure.invalidPDF }
            let result = PDFDocument()
            for (document, count) in [(first, first.pageCount), (rest, rest.pageCount)] {
                for index in 0..<count {
                    try Task.checkCancellation()
                    guard let page = document.page(at: index),
                          abs(page.bounds(for: .mediaBox).width - 595.3) < 2,
                          abs(page.bounds(for: .mediaBox).height - 841.9) < 2,
                          let copy = page.copy() as? PDFPage else { throw Failure.invalidPDF }
                    result.insert(copy, at: result.pageCount)
                }
            }
            guard result.pageCount == first.pageCount + rest.pageCount,
                  let data = result.dataRepresentation(), !data.isEmpty else { throw Failure.invalidPDF }
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("cash-letter-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let destination = directory.appendingPathComponent("كتاب أمانات الرواتب.pdf")
            do { try data.write(to: destination, options: [.atomic, .completeFileProtection]) }
            catch { try? FileManager.default.removeItem(at: directory); throw Failure.saveFailed }
            return destination
        }
    }
}
