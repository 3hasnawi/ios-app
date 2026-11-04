import Foundation

/// إقرار يدوي يحفظ لقطة المبالغ المعروضة؛ لا يثبت حصول الصرف أو اكتمال البيانات.
nonisolated enum FundingApproval {
    static let settingsKey = "fundingExpenseApprovalsV1"
    struct Record: Codable, Identifiable, Equatable, Sendable {
        let id: UUID
        let confirmedAt: Date
        let snapshot: FundingExpenses
        let sourceRevision: String
        let header: String
        var revokedAt: Date? = nil
    }
    struct Ledger: Codable, Sendable {
        var version = 1
        var records: [Record] = []
    }
    enum Failure: LocalizedError {
        case invalid
        var errorDescription: String? { tr("تعذّر قراءة سجل اعتماد المصروفات بأمان؛ لم يُستبدل السجل.", "The expense approval ledger could not be read safely; it was preserved.") }
    }
    static func decode(_ data: Data?) throws -> Ledger {
        guard let data else { return Ledger() }
        guard !data.isEmpty, data.count <= 16_000_000,
              let ledger = try? JSONDecoder().decode(Ledger.self, from: data), ledger.version == 1,
              ledger.records.count <= 5_000, Set(ledger.records.map(\.id)).count == ledger.records.count else { throw Failure.invalid }
        for record in ledger.records {
            let report = record.snapshot
            let numbers = report.rows.map(\.amount) + [report.bankTotal, report.total]
            guard record.confirmedAt.timeIntervalSinceReferenceDate.isFinite,
                  (0...7_000_000_000).contains(record.confirmedAt.timeIntervalSinceReferenceDate),
                  record.revokedAt.map({ $0.timeIntervalSinceReferenceDate.isFinite && $0 >= record.confirmedAt && $0.timeIntervalSinceReferenceDate <= 7_000_000_000 }) ?? true,
                  !report.month.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, report.month.count <= 200,
                  !record.sourceRevision.isEmpty, record.sourceRevision.count <= 200,
                  record.header.count <= 400,
                  !report.rows.isEmpty, report.rows.count <= 20_000,
                  report.rows.enumerated().allSatisfy({ $0.offset == $0.element.id && !$0.element.title.isEmpty && $0.element.title.count <= 1_000 }),
                  numbers.allSatisfy({ $0.isFinite && $0 >= 0 && $0 <= 9_007_199_254_740_991 && $0.rounded() == $0 }),
                  report.unclassifiedCount >= 0, report.missingNet >= 0, report.missingContractNominal >= 0, report.missingExecutionAmount >= 0 else { throw Failure.invalid }
        }
        return ledger
    }
    static func encoded(_ ledger: Ledger) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let data = try encoder.encode(ledger)
        _ = try decode(data)
        return data
    }
    static func currentData() throws -> Data? {
        guard let object = UserDefaults.standard.object(forKey: settingsKey) else { return nil }
        guard let data = object as? Data else { throw Failure.invalid }
        return data
    }
    static func current() throws -> Ledger { try decode(currentData()) }
    static func latest(in ledger: Ledger, month: String) -> Record? {
        guard let last = ledger.records.last(where: { $0.snapshot.month == month }), last.revokedAt == nil else { return nil }
        return last
    }
}
