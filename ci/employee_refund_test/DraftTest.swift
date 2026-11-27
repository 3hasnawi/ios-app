import Foundation
import SwiftData
import PDFKit

@main enum DraftTest {
    @MainActor static func main() async throws {
        if let path = ProcessInfo.processInfo.environment["WINDOWS_TEMPLATE_SETTINGS"] {
            let settings = try JSONDecoder().decode([String: Data].self, from: Data(contentsOf: URL(fileURLWithPath: path)))
            func payload(_ key: String) throws -> Data {
                try PropertyListSerialization.propertyList(from: settings[key]!, format: nil) as! Data
            }
            let templates = try CustomTransferTemplate.decode(payload(CustomTransferTemplate.settingsKey))
            precondition(templates.count == 1 && templates[0].source == .payroll && templates[0].fontSizes?["body"] == 13)
            struct Builtin: Decodable { struct Item: Decodable { var letterTemplate: CustomTransferTemplate }; var individual: [String: Item] }
            let t = try JSONDecoder().decode(Builtin.self, from: payload("transferLetterCustomizations")).individual["rafidain_group"]!.letterTemplate
            try t.validate()
            let cells = try CustomTransferTemplate.signatureCells([t.firstSigner,t.secondSigner,t.thirdSigner], date: " / ١ / ٢٠٢٦")
            precondition(t.bodyKashida == .high && t.signatureHorizontalOffset == -10 && cells[0].isEmpty && !cells[2].isEmpty)
            print("PASS Windows custom and built-in templates decoded by the iPhone model")
        }
        if let path = ProcessInfo.processInfo.environment["TRIAL_REFUND_SNAPSHOT"] {
            let url = URL(fileURLWithPath: path)
            let saved = try JSONDecoder().decode(RefundSnapshot.self, from: Data(contentsOf: url))
            let batch = try saved.plan()
            precondition(batch.total == 40000 && Set(saved.rows.map(\.id)).count == 2)
            let files = try EmployeeRefundExport.make(batch, date: saved.date, letters: saved.letters)
            defer { if let file = files.first { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) } }
            precondition(files.count == 5)
            for group in batch.groups {
                let code = group.code
                let csv = try Data(contentsOf: files.first { $0.lastPathComponent.hasSuffix(code + ".csv") }!)
                let windowsCSV = try Data(contentsOf: url.deletingLastPathComponent().appendingPathComponent(code + ".csv"))
                precondition(csv == windowsCSV)
                let title = group.isRafidain ? "الرافدين" : "الرشيد والمصارف الأخرى"
                let pdf = PDFDocument(url: files.first { $0.lastPathComponent == "كتاب إعادة مبالغ - " + title + ".pdf" }!)!
                precondition(pdf.pageCount == 1 && pdf.string?.contains(group.bank.bic) == true)
            }
            print("PASS saved OneDrive refund with both bank letters, roster and byte-identical Windows/iPhone CSVs")
        }
        if let path = ProcessInfo.processInfo.environment["WINDOWS_REFUND"] {
            let data = try Data(contentsOf: URL(fileURLWithPath: path))
            let snapshot = try JSONDecoder().decode(RefundSnapshot.self, from: data)
            let batch = try snapshot.plan()
            precondition(snapshot.rows.count == 3 && batch.total == 3003 && Set(snapshot.rows.map(\.id)).count == 3)
            for group in batch.groups {
                let expected = URL(fileURLWithPath: path).deletingLastPathComponent().appendingPathComponent(group.code + ".csv")
                let csv = try Data(contentsOf: expected)
                precondition(EmployeeRefund.csv(group, batch: batch, day: "20261120") == csv)
            }
            print("PASS Windows refund IDs, per-bank BIC and full CSV bytes match iPhone")
        }
        let container = try ModelContainer(for: Employee.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let context = container.mainContext
        func employee(_ month: String, _ name: String, _ iban: String) throws -> Employee {
            let person = Employee(name: name)
            person.month = month
            person.detailsData = try JSONEncoder().encode(["IBAN": iban, "الرفرنس": "RAFBIQ00"])
            context.insert(person)
            try context.save()
            return person
        }
        func fakeIBAN(_ suffix: Int, code: String = "RAFB") -> String {
            let bban = code + String(format: "%015d", suffix)
            var mod = 0
            for c in (bban + "IQ00").utf8 {
                for n in (c >= 65 ? String(c - 55) : String(c - 48)).utf8 { mod = (mod * 10 + Int(n - 48)) % 97 }
            }
            return "IQ" + String(format: "%02d", 98 - mod) + bban
        }
        let first = try employee("A", "موظف افتراضي", fakeIBAN(1))
        let previous = try employee("B", first.name, fakeIBAN(2))
        let second = try employee("A", "موظف ثان", fakeIBAN(3))
        let engine = RefundPayrollSearch(modelContainer: container)
        let target = RefundSearchTarget(id: first.persistentModelID, key: first.matchKey, month: first.month)
        let one = try await engine.searchResult(target, month: "A")
        let old = try await engine.searchResult(target, month: "B")
        precondition(old.id == previous.persistentModelID && old.person.iban == fakeIBAN(2))
        let two = try await engine.searchResult(.init(id: second.persistentModelID, key: second.matchKey, month: "A"), month: "A")
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let storage = RefundStorage(root: root)
        let draft = RefundDraft(storage: storage)
        try draft.add(one)
        try draft.add(one, amount: "25000")
        try draft.add(two, amount: "400")
        precondition(target.isIn(draft) && draft.rows.count == 2 && draft.rows.map(\.amount) == ["25000", "400"])
        draft.source = "اختبار"
        draft.payer = "دافع"; draft.account = fakeIBAN(99)
        let reopened = RefundDraft(storage: storage)
        precondition(reopened.source == draft.source && reopened.rows.map(\.candidate) == draft.rows.map(\.candidate))
        precondition(reopened.rows.map(\.amount) == draft.rows.map(\.amount) && reopened.date == draft.date)
        try storage.archive(reopened.snapshot)
        try storage.archive(draft.snapshot)
        let records = try storage.archived()
        precondition(records.count == 1)
        let historical = try records[0].snapshot.plan()
        precondition(historical.total == 25400)
        let recovered = try await engine.recover(historical.groups[0].payments, month: "A")
        precondition(recovered.map(\.candidate) == draft.rows.map(\.candidate))
        // نفس نقطة الدخول التي يستدعيها منتقي الملفات بعد زر فتح.
        let importedStorage = RefundStorage(root: root.appendingPathComponent("recovered"))
        let imported = RefundDraft(storage: importedStorage)
        imported.source = "مسودة قبل الاسترجاع"
        var files: [URL] = []
        for row in draft.rows {
            var part = draft.snapshot
            part.rows = [row]
            let batch = try part.plan()
            let file = root.appendingPathComponent("refund-\(files.count).csv")
            let data = EmployeeRefund.csv(batch.groups[0], batch: batch, day: "20261120")
            try (Data([0xEF, 0xBB, 0xBF]) + data).write(to: file)
            files.append(file)
        }
        let bad = root.appendingPathComponent("wrong.csv")
        try Data("invalid".utf8).write(to: bad)
        do {
            try await imported.recover(files: [files[0], bad], month: "A", container: container)
            fatalError("Partial import accepted")
        } catch {
            precondition(error.localizedDescription.contains("wrong.csv"))
        }
        precondition(imported.rows.isEmpty && !imported.working)
        let unchanged = try importedStorage.loadDraft()
        precondition(unchanged?.source == "مسودة قبل الاسترجاع" && unchanged?.rows.isEmpty == true)
        try await imported.recover(files: files, month: "A", container: container)
        let fromDisk = RefundDraft(storage: importedStorage)
        precondition(fromDisk.rows.map(\.candidate) == draft.rows.map(\.candidate))
        precondition(fromDisk.rows.map(\.amount) == ["25000", "400"] && !imported.working)
        precondition((try? draft.add(old, amount: "1")) == nil)
        precondition((try? draft.add(two, amount: "0")) == nil && draft.rows[1].amount == "400")
        _ = try employee("B", first.name, fakeIBAN(4))
        let fresh = RefundPayrollSearch(modelContainer: container)
        let ambiguous = try? await fresh.searchResult(target, month: "B")
        precondition(ambiguous == nil)
        draft.remove(two.id)
        draft.clear()
        precondition(draft.month == nil && draft.rows.isEmpty)
        // Preserve an incomplete live draft independently of an earlier completed payment.
        try draft.add(one)
        let interchange = root.appendingPathComponent("test-refunds.jsonl")
        let exported = try RefundBackup.write(from: root, to: interchange)
        precondition(exported.count == 1 && exported.draft)
        let secondRoot = root.appendingPathComponent("device")
        let staged = try RefundBackup.prepare(from: interchange, beside: secondRoot, count: 1, draft: true)
        try RefundBackup.install(staged, at: secondRoot)
        let targetStorage = RefundStorage(root: secondRoot)
        let importedDraft = try targetStorage.loadDraft()
        let importedHistory = try targetStorage.archived()
        precondition(importedDraft?.rows.first?.amount == "" && importedHistory.count == 1)
        let substitute = try ModelContainer(for: Employee.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let replacement = Employee(name: first.name)
        replacement.month = first.month
        replacement.detailsData = first.detailsData
        substitute.mainContext.insert(replacement)
        try substitute.mainContext.save()
        try RefundPayrollSearch.relinkDraft(in: targetStorage, context: substitute.mainContext)
        let relinked = try targetStorage.loadDraft()
        precondition(relinked?.rows.first?.id == replacement.persistentModelID)
        draft.clear()
        let afterClear = try storage.archived()
        precondition(afterClear.count == 1 && afterClear[0].snapshot.rows.count == 2 && RefundDraft(storage: storage).rows.isEmpty)
        // تعديل الأرشيف مع بقاء المسودة الحية مستقلة وحماية التعديل من نسخة متقادمة.
        let original = afterClear[0]
        let remote = RefundStorage(root: root.appendingPathComponent("shared-device"))
        let sharedID = original.snapshot.batchID
        let originalBytes = try storage.sharedData(sharedID)!
        try remote.applyShared(originalBytes,id:sharedID,expected:"")
        let receivedBytes = try remote.sharedData(sharedID)
        precondition(receivedBytes == originalBytes)
        do { try remote.applyShared(originalBytes,id:sharedID,expected:"stale"); fatalError("Overwrote a concurrent edit") } catch {}
        let received = try remote.archived()
        precondition(received.count == 1 && received[0].revision == original.revision)
        do { try remote.applyShared(originalBytes,id:UUID(),expected:""); fatalError("Imported wrong batch") } catch {}
        let editor = RefundDraft(storage: storage, editing: original)
        editor.setAmount("١٢٣٤٥", for: one.id)
        precondition(editor.rows.first?.amount == "١٢٣٤٥")
        editor.remove(two.id)
        try editor.add(old, amount: "٦٠")
        editor.date = original.snapshot.date.addingTimeInterval(86400)
        editor.source = "جهة معدّلة"
        let beforeSave = try storage.archived()
        precondition(beforeSave[0].revision == original.revision && RefundDraft(storage: storage).rows.isEmpty)
        let updated = try await editor.saveArchiveEdit(container: container)
        let editedBatch = try updated.snapshot.plan()
        precondition(updated.id == original.id && editedBatch.total == 12405 && updated.snapshot.date == editor.date)
        precondition(updated.snapshot.rows.map(\.candidate) == [one, old] && updated.snapshot.source == editor.source)
        try storage.archive(updated.snapshot)
        let once = try storage.archived()
        precondition(once.count == 1 && once[0].revision == updated.revision)
        do { try storage.delete(original); fatalError("Deleted newer edit") } catch {}
        do { _ = try storage.replace(original, with: original.snapshot); fatalError("Overwrote newer edit") } catch {}
        let cancelled = RefundDraft(storage: storage, editing: updated)
        cancelled.remove(one.id); cancelled.remove(old.id)
        precondition(cancelled.rows.isEmpty && cancelled.date == editor.date && cancelled.source == editor.source)
        do { _ = try await cancelled.saveArchiveEdit(container: container); fatalError("Saved empty batch") } catch {}
        try storage.archive(original.snapshot)
        let separate = try storage.archived()
        precondition(separate.count == 2)
        try storage.delete(updated)
        let remaining = try storage.archived()
        precondition(remaining.count == 1 && remaining[0].revision == original.revision)
        precondition(RefundDraft(storage: storage).rows.isEmpty)
        try draft.add(one, amount: "1")
        for value in ["٢", "٢٣", "٢٣٤", "", "٨٥"] { draft.setAmount(value, for: one.id) }
        draft.flushAmount()
        precondition(RefundDraft(storage: storage).rows.first?.amount == "٨٥")
        draft.clear()
        let incomplete = try employee("A", "مستفيد تجريبي", fakeIBAN(9))
        incomplete.detailsData = try JSONEncoder().encode(["IBAN": fakeIBAN(9)])
        try context.save()
        let revised = RefundPayrollSearch(modelContainer: container)
        let pending = try await revised.searchResult(.init(id: incomplete.persistentModelID, key: incomplete.matchKey, month: "A"), month: "A")
        precondition(pending.person.payrollBIC == .missingColumn)
        try draft.add(pending)
        try draft.add(pending, amount: "2500")
        precondition(draft.rows.count == 1 && draft.rows[0].amount == "2500")
        let entries = draft.rows.map { EmployeeRefund.Entry(person: $0.candidate.person, amount: $0.amount) }
        precondition((try? EmployeeRefund.plan(entries: entries, banks: [:], payer: "دافع", account: fakeIBAN(8), source: "")) == nil)
        draft.clear()
        func branchCandidate(_ id: PersistentIdentifier, _ serial: Int, _ bic: RefundPayrollBIC) -> RefundCandidate {
            .init(id: id, month: "A", person: .init(name: "تجريبي", rank: "", workplace: "", iban: fakeIBAN(serial, code: "RSHD"),
                                                  bank: "مصرف تجريبي", payrollBIC: bic))
        }
        let branchA = "RSHDIQ00001", branchB = "RSHDIQ00002"
        try draft.add(branchCandidate(one.id, 1, .value(branchB)))
        precondition(draft.letterBICOptions["RSHD"] == [branchB])
        try draft.add(branchCandidate(two.id, 2, .value(branchA)))
        precondition(draft.letterBICOptions["RSHD"] == [branchA, branchB])
        try draft.add(branchCandidate(old.id, 3, .value(branchB)))
        precondition(draft.letterBICOptions["RSHD"] == [branchB])
        draft.remove(old.id)
        draft.remove(one.id)
        precondition(draft.letterBICOptions["RSHD"] == [branchA])
        try draft.add(branchCandidate(old.id, 3, .value("OTHRIQ00")))
        draft.remove(two.id)
        precondition(draft.letterBICOptions["RSHD"] == [])
        print("Shared refund draft selection, editing, month resolution and ambiguity checks passed.")
    }
}
