import Foundation
import SwiftData

@main struct Test {
    static func data(_ object: Any) throws -> Data { try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]) }
    static func object(_ bytes: Data) throws -> [String: Any] { try JSONSerialization.jsonObject(with: bytes) as! [String: Any] }
    static func expectFailure(_ block: () throws -> Void) {
        do { try block(); fatalError("Expected refusal") } catch { }
    }
    @MainActor static func main() async throws {
        let defaults = UserDefaults.standard
        let suite = "fictional-entity-rename-\(UUID())"
        defaults.addSuite(named: suite)
        let old = "جهة وهمية / فرع اختبار", target = "جهة وهمية / الفرع التجريبي"
        let oldKey = ArabicText.normalize(old), targetKey = ArabicText.normalize(target)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer {
            for key in EntityRenamePlan.keys + ["backupDataChangedAt", "homeStatsCache", "homeOverviewCache", "homeAuditCache"] { defaults.removeObject(forKey: key) }
            defaults.removeSuite(named: suite)
            try? FileManager.default.removeItem(at: root)
        }
        for key in EntityRenamePlan.keys { defaults.removeObject(forKey: key) }
        expectFailure { _ = try EntityRenamePlan.validatedName("  ") }
        expectFailure { _ = try EntityRenamePlan.validatedName("سطر\nجديد") }
        expectFailure { _ = try EntityRenamePlan.validatedName(String(repeating: "س", count: 301)) }
        let matched = try EntityRenamePlan.destination(" جهه وهميه / الفرع التجريبي ", source: old, names: [old, target])
        precondition(matched == target)
        let accounts: [[String: Any]] = [["entity": old, "iban": "FICTIONAL-ACCOUNT-A", "branch": "مصرف وهمي", "swift": "FAKE"]]
        let preservedAccounts = try data(accounts)
        let templates = try data(["version": 1, "templates": [["source": "execution", "sourceName": old, "body": old], ["source": "payroll", "sourceName": old]]])
        let settings: [String: Data] = [EntityRenamePlan.accountsKey: preservedAccounts,
            EntityRenamePlan.templatesKey: templates,
            EntityRenamePlan.letterLinksKey: try data(["test": ["title": oldKey, "accountKey": oldKey, "iban": "FICTIONAL"]]),
            EntityRenamePlan.customizationsKey: try data(["common": ["beneficiarySource": old, "bodyTemplate": old], "individual": [:]]),
            EntityRenamePlan.categoriesKey: try data(["\(oldKey)|FICTIONAL-ACCOUNT-A|مصرف وهمي": "otherBanks"])]
        let changes = try EntityRenamePlan.settings(source: old, destination: target, current: settings)
        precondition(changes[EntityRenamePlan.accountsKey] == nil)
        let links = try JSONDecoder().decode([String: String].self, from: changes[EntityRenamePlan.linksKey]!)
        precondition(links["deduction|" + targetKey] == oldKey)
        let nextTemplates = try object(changes[EntityRenamePlan.templatesKey]!) ["templates"] as! [[String: Any]]
        precondition(nextTemplates[0]["sourceName"] as? String == target && nextTemplates[0]["body"] as? String == old)
        precondition(nextTemplates[1]["sourceName"] as? String == old)
        let customization = try object(changes[EntityRenamePlan.customizationsKey]!)["common"] as! [String: Any]
        precondition(customization["beneficiarySource"] as? String == target && customization["bodyTemplate"] as? String == old)
        var conflict = settings
        conflict[EntityRenamePlan.accountsKey] = try data(accounts + [["entity": target, "iban": "FICTIONAL-ACCOUNT-B", "branch": "مصرف آخر", "swift": "FAKE"]])
        expectFailure { _ = try EntityRenamePlan.settings(source: old, destination: target, current: conflict) }
        conflict[EntityRenamePlan.linksKey] = Data("corrupt".utf8)
        expectFailure { _ = try EntityRenamePlan.settings(source: old, destination: target, current: conflict) }
        for (key, value) in settings { defaults.set(value, forKey: key) }

        let schema = Schema([PayrollMonth.self, Employee.self, Deduction.self])
        let container = try ModelContainer(for: schema, configurations: [ModelConfiguration(url: root.appendingPathComponent("fictional.store"))])
        let context = ModelContext(container)
        let months = ["أيلول 2026", "تشرين الأول 2026", "تشرين الثاني 2026"]
        let importedAt = Date(timeIntervalSince1970: 1000)
        for name in months {
            let month = PayrollMonth(name: name, employeeCount: 0, deductionCount: 234)
            month.importedAt = importedAt
            context.insert(month)
        }
        for i in 0..<701 {
            context.insert(Deduction(month: months[i % 3], employeeName: "منتسب وهمي \(i)", title: old,
                                     amount: i % 5 == 0 ? nil : Double(i), note: "إضبارة وهمية \(i)", rank: "اختبار"))
        }
        context.insert(Deduction(month: months[0], employeeName: "منتسب سابق وهمي", title: target, amount: 500, note: "لا يتغير"))
        context.insert(Deduction(month: months[1], employeeName: "منتسب آخر وهمي", title: "جهة أخرى", amount: 999, note: "لا يتغير"))
        try context.save()
        let before = try context.fetch(FetchDescriptor<Deduction>()).map { "\($0.month)|\($0.employeeName)|\($0.amount.map(String.init(describing:)) ?? "nil")|\($0.note)|\($0.rank)|\($0.matchKey)" }.sorted()
        let journal = root.appendingPathComponent("pending.json")
        let editor = DeductionEntityEditor(modelContainer: container)
        let names = try await editor.names()
        precondition(names.first { $0.title == old }?.months == 3)
        let result = try await editor.rename(source: old, destination: target, journalURL: journal)
        precondition(result.records == 701 && result.months == 3)
        let verify = ModelContext(container)
        let rows = try verify.fetch(FetchDescriptor<Deduction>())
        precondition(rows.filter { $0.title == target }.count == 702 && !rows.contains { $0.title == old })
        let after = rows.map { "\($0.month)|\($0.employeeName)|\($0.amount.map(String.init(describing:)) ?? "nil")|\($0.note)|\($0.rank)|\($0.matchKey)" }.sorted()
        precondition(before == after)
        precondition(defaults.data(forKey: EntityRenamePlan.accountsKey) == preservedAccounts)
        let revisions = try verify.fetch(FetchDescriptor<PayrollMonth>())
        precondition(revisions.allSatisfy { $0.importedAt > importedAt && $0.deductionCount == 234 })
        precondition(!FileManager.default.fileExists(atPath: journal.path))
        do { _ = try await editor.rename(source: old, destination: target, journalURL: journal); fatalError("Stale source accepted") } catch { }

        // انقطاع بعد حفظ القاعدة وقبل اكتمال الإعدادات: تعاد كل الروابط بشكل قابل للتكرار.
        defaults.removeObject(forKey: EntityRenamePlan.linksKey)
        let pending = EntityRenameJournal.Pending(source: old, destination: target, records: 701, updates: changes)
        try JSONEncoder().encode(pending).write(to: journal, options: .atomic)
        let recovered = try await DeductionEntityEditor(modelContainer: container).recoverIfNeeded(journalURL: journal)
        precondition(recovered && defaults.data(forKey: EntityRenamePlan.linksKey) == changes[EntityRenamePlan.linksKey])
        let repeated = try await editor.recoverIfNeeded(journalURL: journal)
        precondition(!repeated)
        // انقطاع قبل حفظ القاعدة: لا تطبق روابط الاسم الجديد.
        let staged = EntityRenameJournal.Pending(source: target, destination: "جهة لم تحفظ", records: 702, updates: [EntityRenamePlan.linksKey: Data("uncommitted".utf8)])
        try JSONEncoder().encode(staged).write(to: journal, options: .atomic)
        let rolledBack = try await editor.recoverIfNeeded(journalURL: journal)
        precondition(!rolledBack && defaults.data(forKey: EntityRenamePlan.linksKey) == changes[EntityRenamePlan.linksKey])
        // الإلغاء لا يترك سجلات معدلة أو نقطة إكمال.
        let cancellation = Task { try await editor.rename(source: target, destination: "ملغاة", journalURL: journal) }
        cancellation.cancel()
        do { _ = try await cancellation.value; fatalError("Cancellation ignored") } catch is CancellationError { }
        let final = try ModelContext(container).fetch(FetchDescriptor<Deduction>())
        precondition(!final.contains { $0.title == "ملغاة" } && final.count == 703)
        print("Entity rename: all-month merge, 701 batched records, unchanged amounts/notes/ranks, source links, collision refusal, cancellation and crash recovery passed")
    }
}
