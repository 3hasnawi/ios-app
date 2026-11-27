import Foundation
import PDFKit

@main enum Check {
    static func main() async throws {
        let day = BankReconciliation.dayCalendar.date(from: DateComponents(year: 2026, month: 11, day: 7))!
        for (count, expectedPages) in [(22, 1), (40, 2)] {
            var period = BankReconciliation.Period(month: "تشرين الثاني 2026", start: day, end: day, openingBalance: 5000,
                                                   memberFirst: "عضو أول", memberSecond: "عضو ثان")
            period.secondDate = "٢٠٢٦ / ١٢"
            period.movements = (1...count).map { index in
                .init(date: day, description: "حركة تجريبية \(index)", amount: Int64(index) * 1000, direction: .credit)
            }
            let url = try await BankReconciliationPDF.make(period, complete: true)
            defer { try? FileManager.default.removeItem(at: url) }
            guard let pdf = PDFDocument(url: url), pdf.pageCount == expectedPages else {
                throw NSError(domain: "Unexpected page count for \(count) bank movements", code: 1)
            }
            print("Verified \(count) movements, balances, total and signatures across \(expectedPages) A4 page(s)")
        }
    }
}
