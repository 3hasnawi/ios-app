import Foundation
import PDFKit

/// يراجع النص المقروء بعد كتابة الحزمة؛ لا يدّعي إثبات التطابق البصري.
nonisolated enum TransferPDFPageProof {
    enum Failure: LocalizedError {
        case mismatch
        var errorDescription: String? { tr("تعذّر التحقق من صفحات الكتاب بعد جمعها؛ أُوقفت المعاينة.", "Could not verify the assembled letter pages; preview was stopped.") }
    }
    static func verify(sources: [PDFDocument], result: URL) throws {
        guard let written = PDFDocument(url: result), !written.isLocked,
              written.pageCount == sources.reduce(0, { $0 + $1.pageCount }) else { throw Failure.mismatch }
        var index = 0
        for source in sources {
            guard !source.isLocked, source.pageCount > 0 else { throw Failure.mismatch }
            for sourceIndex in 0..<source.pageCount {
                try Task.checkCancellation()
                guard let original = source.page(at: sourceIndex), let copy = written.page(at: index),
                      original.bounds(for: .mediaBox) == copy.bounds(for: .mediaBox),
                      let originalText = original.string, !originalText.isEmpty,
                      let copyText = copy.string, originalText == copyText else { throw Failure.mismatch }
                index += 1
            }
        }
    }
}
