import Foundation

/// حالات وهمية فقط؛ تُستدعى من اختبار النسخ في المحاكي عند طلبه صراحةً.
nonisolated enum SalaryTrustSelfTest {
    static func run() -> Bool {
        do {
            let payment = SalaryTrust.Payment(id: UUID().uuidString, amount: 300, bookNumber: "كتاب وهمي", bookDate: "2026-11-02", note: "ملاحظة صرف وهمية")
            let entry = SalaryTrust.Entry(id: UUID().uuidString, name: "منتسب وهمي", rank: "رتبة", workplace: "قسم", year: 2026, month: 10,
                amount: 1000, caseStatus: "حالة من الراتب", note: "ملاحظة قيد وهمية", importedStatus: "", legacyPaid: false, reviewPending: false, payments: [payment])
            let review = SalaryTrust.Entry(id: UUID().uuidString, name: "منتسب ثان وهمي", rank: "رتبة", workplace: "قسم", year: 2026, month: 9,
                amount: 500, caseStatus: "", note: "", importedStatus: "قطع علاقة تقاعد", legacyPaid: false, reviewPending: true, payments: [])
            let ledger = SalaryTrust.Ledger(entries: [entry, review], openingImported: true)
            let restored = try SalaryTrust.decode(SalaryTrust.encode(ledger))
            guard restored == ledger, restored.entries[0].remaining == 700, restored.entries[1].reviewPending else { return false }
            guard SalaryTrust.outstanding(restored.entries) == 700,
                  SalaryTrust.PaymentFilter.partial.includes(entry),
                  SalaryTrust.PaymentFilter.review.includes(review),
                  !SalaryTrust.PaymentFilter.unfinished.includes(review) else { return false }
            for (number, date) in [("", ""), ("123", ""), ("", "2026-11-02"), ("123", "2026-11-02")] {
                var optional = ledger
                optional.entries[0].payments[0].bookNumber = number
                optional.entries[0].payments[0].bookDate = date
                guard try SalaryTrust.decode(SalaryTrust.encode(optional)) == optional else { return false }
            }
            let report = SalaryTrustReportRequest(entries: ledger.entries, month: "كل الأشهر", status: "كل الحالات", query: "", ledgerData: try SalaryTrust.encode(ledger), preview: true)
            let sheet = try SalaryTrustReport.sheet(report)
            guard sheet.rows.count == 3, sheet.rows[0][7] == "700", sheet.rows[1][7] == "—", sheet.rows[2][7] == "700" else { return false }
            let bookID = UUID()
            let ids: Set<String> = [entry.id, review.id]
            let linked = try SalaryTrust.linking(ledger, bookID: bookID, entries: ids)
            guard linked.entries.allSatisfy({ $0.bookIds == [bookID.uuidString] }) else { return false }
            let duplicate = try SalaryTrust.linking(linked, bookID: bookID, entries: ids)
            guard duplicate == linked else { return false }
            do { _ = try SalaryTrust.linking(ledger, bookID: bookID, entries: [UUID().uuidString]); return false } catch { }
            try SalaryTrust.validate(linked)
            try SalaryTrust.validateBooks(linked, available: [bookID])
            guard linked.entries[0].remaining == entry.remaining else { return false }
            do { try SalaryTrust.validateBooks(linked, available: []); return false } catch { }
            let linkedRoundTrip = try SalaryTrust.decode(SalaryTrust.encode(linked))
            guard linkedRoundTrip.entries[0].bookIds == [bookID.uuidString] else { return false }
            var bad = ledger
            bad.entries[0].payments[0].amount = 1001
            do { try SalaryTrust.validate(bad); return false } catch { }
            bad = ledger; bad.entries[0].payments[0].bookDate = "2026-02-30"
            do { try SalaryTrust.validate(bad); return false } catch { }
            bad = ledger; var repeated = entry; repeated.id = UUID().uuidString; bad.entries.append(repeated)
            do { try SalaryTrust.validate(bad); return false } catch { }
            return true
        } catch { return false }
    }
}
