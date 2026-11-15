import Foundation

@MainActor enum EntityRenameEffects {
    static func refresh() async {
        BankStatementCache.shared.invalidate()
        await SearchIndex.shared.invalidate()
        await AuditResults.shared.invalidate()
        BackupManager.shared.markChanged()
    }
}
