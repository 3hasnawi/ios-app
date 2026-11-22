import Foundation
import SwiftData
import ZIPFoundation
import CoreGraphics

@main struct BackupTests {
    static let months = ["تشرين الأول 2026", "تشرين الثاني 2026"]
    static let count = 3200

    struct Snapshot: Equatable {
        var payroll: [String: [Data?]] = [:]
        var net: [String: Double] = [:]
        var deductions: [String: Double] = [:]
        var staff: [String: Data] = [:]
        var summaries: [String: Data] = [:]

        init(_ container: ModelContainer) throws {
            let context = ModelContext(container)
            for employee in try context.fetch(FetchDescriptor<Employee>()) {
                let key = employee.month + employee.name
                precondition(payroll[key] == nil)
                payroll[key] = [Data(employee.rank.utf8), Data(employee.workplace.utf8), employee.rawColumnsData,
                                employee.itemsData, employee.detailsData, try JSONEncoder().encode(employee.netSalary)]
                net[employee.month, default: 0] += employee.netSalary ?? 0
            }
            for row in try context.fetch(FetchDescriptor<Deduction>()) {
                deductions[row.month + row.title, default: 0] += row.amount ?? 0
            }
            for row in try context.fetch(FetchDescriptor<StaffMember>()) { staff[row.name] = row.detailsData }
            for row in try context.fetch(FetchDescriptor<PayrollMonth>()) { summaries[row.name] = row.itemSummaryData }
        }
    }

    static func rejected(_ operation: () async throws -> Void) async {
        do { try await operation(); fatalError("Expected verification to stop") } catch {}
    }

    @MainActor static func main() async throws {
        let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        let schema = Schema([PayrollMonth.self, Employee.self, Deduction.self, StaffMember.self])
        func database() throws -> ModelContainer {
            try ModelContainer(for: schema, configurations: [ModelConfiguration(isStoredInMemoryOnly: true)])
        }
        let source = try database(), destination = try database()
        let context = ModelContext(source), encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let headers = (0..<96).map { "عنوان عمود رواتب تجريبي رقم " + String($0) } + ["مكرر", "مكرر", ""]
        var expectedNet = [String: Double](), expectedDeduct = [String: Double]()
        for (m, month) in months.enumerated() {
            let record = PayrollMonth(name: month, employeeCount: count / 2, deductionCount: count / 2)
            record.itemSummaryData = try encoder.encode(["حقل تجريبي": 42 + m])
            context.insert(record)
        }
        for i in 0..<count {
            let month = months[i % 2], name = "منتسب تجريبي " + String(i / 2)
            let net: Double? = i % 13 == 0 ? nil : Double(400_000 + i) + 0.25
            let amount = Double(11 + i % 499), title = "جهة " + String(i % 7)
            var values = Array(repeating: "", count: headers.count)
            values[i % values.count] = "٠٠١ / \"مبلغ\""
            let raw: Data?
            switch i % 41 {
            case 0: raw = nil
            case 1: raw = Data([0, 123, 255])
            default: raw = try encoder.encode(PayrollColumnSnapshot(headers: i.isMultiple(of: 2) ? headers : headers.reversed(), values: values))
            }
            context.insert(Employee(month: month, name: name, rank: "عقود", workplace: "قسم تجريبي", netSalary: net,
                                    items: [title: amount], details: ["رقم": String(i)], rawColumnsData: raw))
            context.insert(Deduction(month: month, employeeName: name, title: title, amount: amount, note: "تجريبي", rank: "عقود"))
            expectedNet[month, default: 0] += net ?? 0
            expectedDeduct[month + title, default: 0] += amount
        }
        context.insert(StaffMember(name: "سجل وهمي", mother: "", statNumber: "1", unifiedNumber: "", rank: "", status: "", workplace: "", sections: []))
        try context.save()
        let original = try Snapshot(source)
        precondition(original.net == expectedNet && original.deductions == expectedDeduct)
        let manual = ContractOverride(total: 22_000_000, nominal: 178_723_001, retirement: 125_000, count: 3, missing: 0)
        let overrides = try ContractOverrides.setting(nil, month: months[0], value: manual)
        let prop = try PropertyListSerialization.data(fromPropertyList: overrides, format: .binary, options: 0)
        let settings = [ContractOverrides.settingsKey: prop]
        let book = OfficialBook(id: UUID(), title: "كتاب تجريبي", month: months[0], pages: 1, createdAt: .now)
        try OfficialBookFiles.write([book])
        var box = CGRect(x: 0, y: 0, width: 200, height: 200)
        let pdf = CGContext(OfficialBookFiles.file(book.id) as CFURL, mediaBox: &box, nil)!
        pdf.beginPDFPage(nil); pdf.fill(box); pdf.endPDFPage(); pdf.closePDF()
        let bookData = try Data(contentsOf: OfficialBookFiles.file(book.id))
        let actor = BackupActor(modelContainer: destination)
        let (url, manifest) = try await BackupActor(modelContainer: source).export(settings: settings, progress: nil)
        defer { try? FileManager.default.removeItem(at: url) }
        precondition(manifest.version == 12 && manifest.dataVersion == 6 && manifest.employees == count)
        let folder = try await Restore.extractForRestore(url)
        defer { try? FileManager.default.removeItem(at: folder) }
        let definitions = try JSONDecoder().decode([Data].self, from: Data(contentsOf: folder.appendingPathComponent(CompactPayrollBackup.schemaFile)))
        precondition(definitions.count == 2)
        let restoredSettings = try await actor.restore(from: folder, progress: nil)
        let restored = try Snapshot(destination)
        let restoredPDF = try Data(contentsOf: OfficialBookFiles.file(book.id))
        precondition(restored == original && restoredSettings == settings && bookData == restoredPDF)
        let restoredData = try PropertyListSerialization.propertyList(from: restoredSettings[ContractOverrides.settingsKey]!, format: nil) as! Data
        let restoredOverrides = try ContractOverrides.decode(restoredData)
        precondition(restoredOverrides[months[0]] == manual)
        var bank = BankStatementSummary(month: months[0])
        bank.contractNominal = manual.nominal; bank.contractCount = manual.count; bank.contractRetirement = manual.retirement
        precondition(bank.contractNominalShare == 21_446_760 && bank.contractContribution == 21_571_760)
        bank.manualContractOverride = restoredOverrides[months[0]]
        bank.cardNet = restored.net[months[0]]!; bank.bankDeductions = 4800
        precondition(bank.bankTotal == expectedNet[months[0]]! + 4800 + manual.total)

        let rowsURL = folder.appendingPathComponent("employees.jsonl")
        let rows = try Data(contentsOf: rowsURL)
        let headerURL = folder.appendingPathComponent(CompactPayrollBackup.schemaFile)
        let goodHeaders = try Data(contentsOf: headerURL)
        let manifestURL = folder.appendingPathComponent("manifest.json")
        let goodManifest = try Data(contentsOf: manifestURL)
        func rejectRestore() async throws {
            await rejected { _ = try await actor.restore(from: folder, progress: nil) }
            let unchanged = try Snapshot(destination)
            precondition(unchanged == original)
        }
        var damaged = manifest; damaged.payrollDigest = Data(repeating: 0, count: 32)
        try encoder.encode(damaged).write(to: manifestURL); try await rejectRestore()
        try goodManifest.write(to: manifestURL)
        try encoder.encode(Array(definitions.reversed())).write(to: headerURL); try await rejectRestore()
        try FileManager.default.removeItem(at: headerURL); try await rejectRestore()
        try goodHeaders.write(to: headerURL)
        var alteredRows = rows
        let boundary = alteredRows.firstIndex(of: 10)!
        var first = try JSONDecoder().decode(BackupFormat.EmployeeRecord.self, from: alteredRows.prefix(upTo: boundary))
        first.net = (first.net ?? 0) + 1
        alteredRows.replaceSubrange(..<boundary, with: try encoder.encode(first))
        try alteredRows.write(to: rowsURL); try await rejectRestore()
        try rows.dropFirst(boundary + 1).write(to: rowsURL); try await rejectRestore()
        try rows.write(to: rowsURL)

        // نبني نسخة قديمة كاملة من سجلات SwiftData الأصلية ثم نسترجعها عبر المسار المعتاد.
        let sizes = try BackupSizes.read(url)
        let archive = try Archive(url: url, accessMode: .read)
        let files = try CheckedArchive.Index(archive).entries.map(\.path)
        precondition(sizes.total == sizes.payroll + sizes.deductions + sizes.staff + sizes.books + sizes.other)
        precondition(sizes.payroll > 0 && sizes.books > 0 && sizes.deductions > 0)
        var oldRows = Data()
        for e in try context.fetch(FetchDescriptor<Employee>()) {
            let r = BackupFormat.EmployeeRecord(month: e.month, name: e.name, rank: e.rank, workplace: e.workplace, net: e.netSalary, items: e.itemsData, details: e.detailsData, rawColumns: e.rawColumnsData)
            oldRows.append(try encoder.encode(r)); oldRows.append(10)
        }
        try oldRows.write(to: rowsURL)
        var legacy = manifest
        legacy.version = 6; legacy.contentVersion = nil; legacy.payrollDigest = nil
        try encoder.encode(legacy).write(to: manifestURL)
        let legacyURL = root.appendingPathComponent("legacy.zip")
        do {
            let zip = try Archive(url: legacyURL, accessMode: .create)
            for path in files where path != CompactPayrollBackup.schemaFile {
                try zip.addEntry(with: path, relativeTo: folder, compressionMethod: .deflate)
            }
        }
        let oldDir = try await Restore.extractForRestore(legacyURL)
        defer { try? FileManager.default.removeItem(at: oldDir) }
        _ = try await actor.restore(from: oldDir, progress: nil)
        let oldRestored = try Snapshot(destination)
        precondition(oldRestored == original)
        let oldSizes = try BackupSizes.read(legacyURL)
        precondition(sizes.total < oldSizes.total)
        print("Verified \(count) payroll and \(count) deduction rows, two months, duplicate/empty columns, independent totals, manual contract amount, PDF, legacy restore and four corruption classes. ZIP bytes old=\(oldSizes.total) compact=\(sizes.total); payroll bytes before ZIP old=\(oldRows.count) compact=\(rows.count + goodHeaders.count)")
    }
}
