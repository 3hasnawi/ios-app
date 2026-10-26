#if targetEnvironment(simulator)
import Foundation
import PDFKit

/// حالات محاسبية صغيرة مستقلة عن الملفات المستوردة، تُشغّل بأمر اختبار المحاكي فقط.
nonisolated enum DeductionAuditSelfTest {
    struct Failure: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    static func run() async throws -> String {
        func require(_ condition: Bool, _ message: String) throws {
            if !condition { throw Failure(message: message) }
        }
        func row(_ name: String, _ amount: Double?, _ entity: String = "جهة أ", _ note: String = "") -> DeductionSnapshot {
            DeductionSnapshot(name: name, entity: entity, note: note, amount: amount)
        }
        let old = [row("تجريبي أ", 100), row("تجريبي أ", 50, "جهة ب"), row("تجريبي ب", 200),
                   row("تجريبي ج", 300), row("تجريبي د", 400), row("تجريبي هـ", 100), row("تجريبي و", 100), row("تجريبي ز", 0)]
        let new = [row("تجريبي أ", 100), row("تجريبي ب", 250), row("تجريبي د", 400), row("تجريبي هـ", 100, "جهة ب"),
                   row("تجريبي و", 50), row("تجريبي و", 50), row("تجريبي ح", 80), row("تجريبي ز", 0)]
        let result = try DeductionReview.compare(from: "أيلول 2026", to: "تشرين الأول 2026", old: old, new: new)
        for (kind, count) in [(DeductionChangeKind.started, 1), (.stopped, 1), (.increased, 1), (.decreased, 1), (.unchanged, 3)] {
            try require(result.people.filter { $0.kind == kind }.count == count, "Incorrect employee count: \(kind)")
        }
        try require(result.before == 1250 && result.after == 1030, "Incorrect totals")
        try require(result.entries.filter { $0.kind == .stopped }.count == 3, "Partial/entity stops lost")
        try require(result.entries.filter { $0.kind == .started }.count == 2, "Entity starts lost")
        try require(!result.people.contains { $0.name == "تجريبي ز" }, "Zero entries count as active")
        let duplicates = DeductionReview.duplicates(new)
        try require(duplicates.rows.count == 1 && duplicates.rows[0].amount == 100, "Duplicate group count/total")
        let separateCases = [row("تجريبي", 50, "جهة أ", "12/2026"), row("تجريبي", 50, "جهة أ", "13/2026"),
                             row("تجريبي", 50, "جهة ب", "12/2026"), row("آخر", 50, "جهة أ", "12/2026")]
        try require(DeductionReview.duplicates(separateCases).rows.isEmpty, "Different cases/entities/people flagged")
        let equivalent = [row("أحمد تجريبي", 50, "جهة أ", "١٢/٢٠٢٦"), row("احمد تجريبي", 50, "جهة أ", "12/2026")]
        try require(DeductionReview.duplicates(equivalent).rows.count == 1, "Arabic normalization failed")
        let split = try DeductionReview.compare(from: "a", to: "b", old: [row("تجريبي", 100, "جهة أ", "1")],
                                               new: [row("تجريبي", 60, "جهة أ", "2"), row("تجريبي", 40, "جهة أ", "3")])
        try require(split.people[0].kind == .unchanged && split.entries.filter { $0.kind == .started }.count == 2 && split.entries.filter { $0.kind == .stopped }.count == 1, "Changed case references must remain visible without changing employee totals")
        let empty = try DeductionReview.compare(from: "a", to: "b", old: [], new: new)
        try require(empty.emptyMonths == ["a"], "Missing empty-month warning")
        let bothEmpty = try DeductionReview.compare(from: "a", to: "b", old: [], new: [])
        try require(bothEmpty.emptyMonths.count == 2 && bothEmpty.people.isEmpty, "Both empty")
        let reverse = try DeductionReview.compare(from: "b", to: "a", old: new, new: old)
        try require(reverse.after - reverse.before == 220, "Reverse comparison direction")
        for invalid in [row("تجريبي", nil), row("تجريبي", -1), row("تجريبي", .nan), row("تجريبي", .infinity), row("", 50), row("تجريبي", 50, "")] {
            do {
                _ = try DeductionReview.compare(from: "a", to: "b", old: old, new: [invalid])
                throw Failure(message: "Invalid input accepted")
            } catch is DeductionReview.InvalidData { }
        }
        let files = try await AuditExport.deductions(result)
        try require((PDFDocument(url: files.pdf)?.pageCount ?? 0) > 0, "Invalid comparison PDF")
        let excel = try Data(contentsOf: files.excel)
        try require(excel.count > 100 && Array(excel.prefix(2)) == [0x50, 0x4b], "Invalid comparison workbook")
        let duplicateFiles = try await AuditExport.check(duplicates, month: "تشرين الأول 2026")
        try require((PDFDocument(url: duplicateFiles.pdf)?.pageCount ?? 0) > 0, "Invalid duplicate PDF")
        return "deductionAudit=ok people=7 started=1 stopped=1 increased=1 decreased=1 unchanged=3 entityStopped=3 entityStarted=2 duplicates=1 totals=1250/1030 invalid=ok empty=ok export=ok"
    }
}
#endif
