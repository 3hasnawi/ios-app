import Foundation
import SwiftData

nonisolated struct DeductionEntityName: Identifiable, Sendable {
    let title: String
    let months: Int
    var id: String { title }
}
nonisolated struct EntityRenameResult: Sendable {
    let records: Int
    let months: Int
}

/// يربط حفظ SwiftData بروابط الإعدادات بنقطة إكمال محلية؛ الانقطاع لا يترك نصف الروابط محدثاً.
nonisolated enum EntityRenameJournal {
    struct Pending: Codable {
        let source: String
        let destination: String
        let records: Int
        let updates: [String: Data]
    }
    static var url: URL {
        let identity = UserDefaults.standard.string(forKey: "activeRecoveredDatabase") ?? "default"
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("EntityRename-\(identity).json")
    }
    static func apply(_ updates: [String: Data]) {
        for (key, data) in updates { UserDefaults.standard.set(data, forKey: key) }
        for key in ["homeStatsCache", "homeOverviewCache", "homeAuditCache"] { UserDefaults.standard.removeObject(forKey: key) }
        UserDefaults.standard.set(Date(), forKey: "backupDataChangedAt")
        UserDefaults.standard.synchronize()
    }
}

@ModelActor actor DeductionEntityEditor {
    func names() throws -> [DeductionEntityName] {
        var names: [String: Set<String>] = [:]
        try modelContext.enumerate(FetchDescriptor<Deduction>(), batchSize: 256) { row in
            try Task.checkCancellation()
            names[row.title, default: []].insert(row.month)
        }
        return names.map { DeductionEntityName(title: $0.key, months: $0.value.count) }
            .sorted { ArabicText.alphabeticalOrder($0.title, $1.title) == .orderedAscending }
    }

    /// الاستدعاء قبل إتاحة القاعدة، وأيضاً قبل عملية تعديل جديدة.
    @discardableResult func recoverIfNeeded(journalURL: URL = EntityRenameJournal.url) throws -> Bool {
        guard FileManager.default.fileExists(atPath: journalURL.path) else { return false }
        let pending = try JSONDecoder().decode(EntityRenameJournal.Pending.self, from: Data(contentsOf: journalURL))
        _ = try EntityRenamePlan.validatedName(pending.destination)
        guard !pending.source.isEmpty, pending.source != pending.destination, pending.records > 0,
              Set(pending.updates.keys).isSubset(of: Set(EntityRenamePlan.keys)), pending.updates[EntityRenamePlan.accountsKey] == nil else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let source = pending.source, destination = pending.destination
        let remaining = try modelContext.fetchCount(FetchDescriptor<Deduction>(predicate: #Predicate { $0.title == source }))
        if remaining == 0 {
            let saved = try modelContext.fetchCount(FetchDescriptor<Deduction>(predicate: #Predicate { $0.title == destination }))
            guard saved >= pending.records else { throw CocoaError(.fileReadCorruptFile) }
            EntityRenameJournal.apply(pending.updates)
        } else {
            // حفظ القاعدة معاملة واحدة؛ وجود كل السجلات الأصلية يعني أن المعاملة لم تُحفظ.
            guard remaining == pending.records else { throw CocoaError(.fileReadCorruptFile) }
        }
        try FileManager.default.removeItem(at: journalURL)
        return remaining == 0
    }

    func rename(source: String, destination input: String, journalURL: URL = EntityRenameJournal.url) throws -> EntityRenameResult {
        _ = try recoverIfNeeded(journalURL: journalURL)
        let destination = try EntityRenamePlan.destination(input, source: source, names: names().map(\.title))
        guard destination != source else {
            throw EntityRenamePlan.invalid(tr("التسمية لم تتغير.", "The name has not changed."))
        }
        var settings: [String: Data] = [:]
        for key in EntityRenamePlan.keys {
            if let value = UserDefaults.standard.object(forKey: key) {
                guard let data = value as? Data else { throw CocoaError(.fileReadCorruptFile) }
                settings[key] = data
            }
        }
        let updates = try EntityRenamePlan.settings(source: source, destination: destination, current: settings)
        let descriptor = FetchDescriptor<Deduction>(predicate: #Predicate { $0.title == source })
        let count = try modelContext.fetchCount(descriptor)
        guard count > 0 else {
            throw EntityRenamePlan.invalid(tr("تغيّرت بيانات الجهة؛ أعد فتح القائمة ثم حاول مجدداً.", "Entity data changed. Reopen the list and try again."))
        }
        modelContext.autosaveEnabled = false
        var months = Set<String>()
        do {
            try modelContext.enumerate(descriptor, batchSize: 256, allowEscapingMutations: true) { row in
                try Task.checkCancellation()
                months.insert(row.month)
                row.title = destination
            }
            // يحدّث مفاتيح التقارير والبحث والتدقيق، دون تغيير عدد القيود أو مبالغها.
            let revision = Date()
            for month in try modelContext.fetch(FetchDescriptor<PayrollMonth>()) where months.contains(month.name) {
                month.importedAt = revision
            }
            try Task.checkCancellation()
            for key in EntityRenamePlan.keys {
                guard (UserDefaults.standard.object(forKey: key) as? Data) == settings[key] else {
                    throw EntityRenamePlan.invalid(tr("تغيرت روابط الحسابات أثناء التعديل؛ أعد المحاولة.", "Account links changed during the edit. Try again."))
                }
            }
            let pending = EntityRenameJournal.Pending(source: source, destination: destination, records: count, updates: updates)
            try FileManager.default.createDirectory(at: journalURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(pending).write(to: journalURL, options: .atomic)
            try modelContext.save()
        } catch {
            modelContext.rollback()
            try? FileManager.default.removeItem(at: journalURL)
            throw error
        }
        // لا نتوقف لإلغاء المهمة بعد حفظ المعاملة؛ يجب إكمال روابطها أولاً.
        EntityRenameJournal.apply(updates)
        try? FileManager.default.removeItem(at: journalURL)
        return EntityRenameResult(records: count, months: months.count)
    }
}
