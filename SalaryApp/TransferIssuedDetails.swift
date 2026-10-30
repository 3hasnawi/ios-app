import Foundation
import SwiftData
import CryptoKit

/// بصمة للصفوف فقط، مع الاحتفاظ بالتكرار، ولا تخزّن أسماء الموظفين في سجل الإقرارات.
nonisolated enum TransferIssuedDetails {
    enum Failure: LocalizedError {
        case mismatch
        var errorDescription: String? { tr("تعذّر مطابقة قائمة الأسماء مع بيانات الشهر؛ راجع المصدر قبل التأكيد.", "Could not reconcile the name list with the month data; review the source before confirming.") }
    }
    static func fingerprint(_ rows: [[String]]) throws -> String {
        let values = try rows.map { row in String(decoding: try JSONEncoder().encode(row), as: UTF8.self) }.sorted()
        let data = try JSONEncoder().encode(values)
        return Data(SHA256.hash(data: data)).base64EncodedString()
    }
    static func load(entities: [TransferEntity], ids: Set<String>, month: String,
                     cashID: String, cashAmount: Double?, container: ModelContainer) async throws -> [String: String] {
        let engine = ReportEngine(modelContainer: container)
        var results: [String: String] = [:]
        for entity in entities where ids.contains(entity.id) && entity.payrollRecipient == nil {
            try Task.checkCancellation()
            let rows = try await engine.rows(for: entity.group)
            _ = try TransferIndividualPlan(kind: .ramadi, group: entity.group, rows: rows, missingAmounts: entity.missingAmounts)
            let details = rows.map { [$0.name, $0.rank, $0.workplace, String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), $0.amount)] }
            results[entity.id] = try fingerprint(details)
        }
        if ids.contains(cashID) {
            guard let cashAmount else { throw Failure.mismatch }
            let list = try await TransferCashListEngine(modelContainer: container).list(month: month)
            guard list.month == month, list.total == cashAmount else { throw Failure.mismatch }
            results[cashID] = try fingerprint(list.employees.map { [$0.name, $0.rank, $0.workplace, String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), $0.amount)] })
        }
        return results
    }
}
