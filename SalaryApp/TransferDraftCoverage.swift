import Foundation
import Observation

/// تربط نتائج المراجعة بلقطة البيانات والإعدادات نفسها، ولا تحفظ بيانات الرواتب على القرص.
nonisolated struct TransferDraftRevision: Equatable, Sendable {
    let sourceKey: String
    let accounts: Data
    let categories: Data
    let payrollLinks: Data
    let customization: Data
    let references: Data
    let payerName: String
    let payerIBAN: String
    var boldFont: Data = Data()
    var regularFont: Data = Data()
}

@MainActor @Observable
final class TransferDraftCoverage {
    static let shared = TransferDraftCoverage()
    private(set) var revision: TransferDraftRevision?
    private(set) var prepared: [String: Set<String>] = [:]

    func records(for revision: TransferDraftRevision) -> [String: Set<String>] {
        self.revision == revision ? prepared : [:]
    }
    func discardIfDifferent(_ revision: TransferDraftRevision) {
        if let existing = self.revision, existing != revision {
            self.revision = nil
            prepared = [:]
        }
    }
    func record(revision: TransferDraftRevision, entityIDs: [String], bookID: String) {
        guard !entityIDs.isEmpty, !bookID.isEmpty else { return }
        if self.revision != revision { self.revision = revision; prepared = [:] }
        for id in entityIDs where !id.isEmpty { prepared[id, default: []].insert(bookID) }
    }
}
