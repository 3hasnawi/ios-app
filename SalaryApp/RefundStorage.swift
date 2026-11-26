import Foundation
import SwiftData
import CryptoKit

nonisolated struct RefundDraftRow: Codable, Identifiable, Sendable {
    var candidate: RefundCandidate
    var amount: String
    var recoveredReference: EmployeeRefund.RecoveredReference? = nil
    var id: PersistentIdentifier { candidate.id }
}

nonisolated struct RefundSnapshot: Codable, Sendable {
    var batchID: UUID
    var rows: [RefundDraftRow]
    var banks: [String: RefundBank]
    var source: String
    var date: Date
    var payer: String
    var account: String
    var letters: RefundLetters
    var month: String? { rows.first?.candidate.month }

    var payrollMonths: String { Array(Set(rows.map(\.candidate.month))).sorted().joined(separator: "، ") }

    var enteredTotal: Int64? {
        var total: Int64 = 0
        for row in rows {
            guard let amount = EmployeeRefund.amount(row.amount) else { return nil }
            let result = total.addingReportingOverflow(amount)
            guard !result.overflow else { return nil }
            total = result.partialValue
        }
        return total
    }

    func plan() throws -> EmployeeRefund.Batch {
        try EmployeeRefund.plan(entries: rows.map { .init(person: $0.candidate.person, amount: $0.amount, recoveredReference: $0.recoveredReference) },
                                banks: banks, payer: payer, account: account, source: source)
    }
}

nonisolated struct RefundStorage: Sendable {
    let root: URL
    static let local = RefundStorage(root: URL.applicationSupportDirectory.appendingPathComponent("EmployeeRefunds/v1", isDirectory: true))
    struct Archived: Identifiable, Sendable {
        let id: String
        static func newestFirst(_ lhs: Self, _ rhs: Self) -> Bool {
            lhs.snapshot.date == rhs.snapshot.date ? lhs.id < rhs.id : lhs.snapshot.date > rhs.snapshot.date
        }
        let revision: Data
        let snapshot: RefundSnapshot
    }
    private var draft: URL { root.appendingPathComponent("draft.json") }
    private var history: URL { root.appendingPathComponent("history", isDirectory: true) }

    func loadDraft() throws -> RefundSnapshot? {
        do { return try JSONDecoder().decode(RefundSnapshot.self, from: Data(contentsOf: draft)) }
        catch let error as CocoaError where error.code == .fileReadNoSuchFile { return nil }
    }

    func saveDraft(_ snapshot: RefundSnapshot) throws {
        try RefundBackup.lock.withLock {
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            try JSONEncoder().encode(snapshot).write(to: draft, options: [.atomic, .completeFileProtection])
        }
    }


    private func encoded(_ snapshot: RefundSnapshot) throws -> Data {
        _ = try snapshot.plan()
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        return try encoder.encode(snapshot)
    }

    private func historyFiles() throws -> [URL] {
        do {
            return try FileManager.default.contentsOfDirectory(at: history, includingPropertiesForKeys: nil, options: .skipsHiddenFiles)
                .filter { $0.pathExtension == "json" }
        } catch let error as CocoaError where error.code == .fileReadNoSuchFile { return [] }
    }

    func archive(_ snapshot: RefundSnapshot) throws {
        let data = try encoded(snapshot)
        try RefundBackup.lock.withLock {
            let id = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            let files = try historyFiles()
            var url = history.appendingPathComponent(id + ".json")
            // قد تكون نسخة مطابقة محفوظة الآن باسمها القديم بعد تعديلها.
            for file in files where try Data(contentsOf: file) == data { return }
            if FileManager.default.fileExists(atPath: url.path) {
                url = history.appendingPathComponent(UUID().uuidString + ".json")
            }
            try FileManager.default.createDirectory(at: history, withIntermediateDirectories: true)
            try data.write(to: url, options: [.atomic, .completeFileProtection])
        }
    }

    private func checkedURL(_ original: Archived) throws -> URL {
        let url = history.appendingPathComponent(original.id + ".json")
        guard try Data(SHA256.hash(data: Data(contentsOf: url))) == original.revision else {
            throw EmployeeRefund.error("تغيّرت الدفعة المحفوظة. افتحها من جديد.", "The saved batch changed. Reopen it.")
        }
        return url
    }

    func replace(_ original: Archived, with snapshot: RefundSnapshot) throws -> Archived {
        guard snapshot.batchID == original.snapshot.batchID else {
            throw EmployeeRefund.error("تعذّر تحديد الدفعة المطلوب تعديلها.", "The batch identity changed.")
        }
        let data = try encoded(snapshot)
        return try RefundBackup.lock.withLock {
            let url = try checkedURL(original)
            try data.write(to: url, options: [.atomic, .completeFileProtection])
            return Archived(id: original.id, revision: Data(SHA256.hash(data: data)), snapshot: snapshot)
        }
    }

    func delete(_ original: Archived) throws {
        try RefundBackup.lock.withLock { try FileManager.default.removeItem(at: checkedURL(original)) }
    }

    func archived() throws -> [Archived] {
        try RefundBackup.lock.withLock {
            try historyFiles().map { url in
                let data = try Data(contentsOf: url)
                return Archived(id: url.deletingPathExtension().lastPathComponent, revision: Data(SHA256.hash(data: data)),
                                snapshot: try JSONDecoder().decode(RefundSnapshot.self, from: data))
            }.sorted(by: Archived.newestFirst)
        }
    }
}
