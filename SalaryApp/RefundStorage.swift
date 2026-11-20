import Foundation
import SwiftData
import CryptoKit

nonisolated struct RefundDraftRow: Codable, Identifiable, Sendable {
    var candidate: RefundCandidate
    var amount: String
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

    func plan() throws -> EmployeeRefund.Batch {
        try EmployeeRefund.plan(entries: rows.map { .init(person: $0.candidate.person, amount: $0.amount) },
                                banks: banks, payer: payer, account: account, source: source)
    }
}

nonisolated struct RefundStorage: Sendable {
    let root: URL
    static let local = RefundStorage(root: URL.applicationSupportDirectory.appendingPathComponent("EmployeeRefunds/v1", isDirectory: true))
    struct Archived: Identifiable, Sendable {
        let id: String
        let snapshot: RefundSnapshot
    }
    private var draft: URL { root.appendingPathComponent("draft.json") }
    private var history: URL { root.appendingPathComponent("history", isDirectory: true) }

    func loadDraft() throws -> RefundSnapshot? {
        do { return try JSONDecoder().decode(RefundSnapshot.self, from: Data(contentsOf: draft)) }
        catch let error as CocoaError where error.code == .fileReadNoSuchFile { return nil }
    }

    func saveDraft(_ snapshot: RefundSnapshot) throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try JSONEncoder().encode(snapshot).write(to: draft, options: [.atomic, .completeFileProtection])
    }

    func archive(_ snapshot: RefundSnapshot) throws {
        _ = try snapshot.plan()
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        let data = try encoder.encode(snapshot)
        let id = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        try FileManager.default.createDirectory(at: history, withIntermediateDirectories: true)
        let url = history.appendingPathComponent(id + ".json")
        try data.write(to: url, options: [.atomic, .completeFileProtection])
    }

    func archived() throws -> [Archived] {
        let urls: [URL]
        do { urls = try FileManager.default.contentsOfDirectory(at: history, includingPropertiesForKeys: nil, options: .skipsHiddenFiles) }
        catch let error as CocoaError where error.code == .fileReadNoSuchFile { return [] }
        return try urls.filter { $0.pathExtension == "json" }.map { url in
            Archived(id: url.deletingPathExtension().lastPathComponent,
                     snapshot: try JSONDecoder().decode(RefundSnapshot.self, from: Data(contentsOf: url)))
        }.sorted { $0.snapshot.date == $1.snapshot.date ? $0.id < $1.id : $0.snapshot.date > $1.snapshot.date }
    }
}
