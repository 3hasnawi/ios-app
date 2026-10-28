import AppIntents
import SwiftUI
import SwiftData
import UIKit
import UniformTypeIdentifiers

/// معرف مستقل عن كائنات SwiftData حتى تُعرض النتائج الثلاث في قائمة اختيار واحدة.
nonisolated struct SmartShortcutPayload: Codable {
    enum Kind: String, Codable { case person, entity, book }
    var kind: Kind
    var title: String
    var month: String
    var reference: String
    var count: Int = 0
    var total: Double = 0
    var byEntity: Bool = false
    var repeated: Int = 0
}

struct SmartShortcutChoice: AppEntity {
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "نتيجة البحث"
    static let defaultQuery = SmartShortcutChoiceQuery()
    let id: String

    init?(_ payload: SmartShortcutPayload) {
        guard let data = try? JSONEncoder().encode(payload) else { return nil }
        id = data.base64EncodedString()
    }
    init?(id: String) {
        guard let data = Data(base64Encoded: id), (try? JSONDecoder().decode(SmartShortcutPayload.self, from: data)) != nil else { return nil }
        self.id = id
    }
    var payload: SmartShortcutPayload? {
        Data(base64Encoded: id).flatMap { try? JSONDecoder().decode(SmartShortcutPayload.self, from: $0) }
    }
    var displayRepresentation: DisplayRepresentation {
        let item = payload
        let subtitle: String
        let symbol: String
        switch item?.kind {
        case .person: subtitle = tr("موظف · \(item?.month ?? "")", "Employee · \(item?.month ?? "")"); symbol = "person.crop.circle"
        case .entity: subtitle = tr("جهة تنفيذ · \(item?.month ?? "")", "Execution entity · \(item?.month ?? "")"); symbol = "building.columns"
        case .book: subtitle = tr("كتاب رسمي · \(item?.month ?? "")", "Official document · \(item?.month ?? "")"); symbol = "doc.text"
        case nil: subtitle = ""; symbol = "magnifyingglass"
        }
        return DisplayRepresentation(title: "\(item?.title ?? "")", subtitle: "\(subtitle)", image: .init(systemName: symbol))
    }
}

struct SmartShortcutChoiceQuery: EntityQuery {
    func entities(for identifiers: [String]) async throws -> [SmartShortcutChoice] { identifiers.compactMap(SmartShortcutChoice.init(id:)) }
    func suggestedEntities() async throws -> [SmartShortcutChoice] { [] }
}

enum SmartShortcutExport: String, AppEnum {
    case pdf, excel
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "صيغة الكشف"
    static let caseDisplayRepresentations: [SmartShortcutExport: DisplayRepresentation] = [.pdf: "PDF", .excel: "Excel"]
}

struct SmartLookupIntent: AppIntent {
    static let authenticationPolicy: IntentAuthenticationPolicy = .requiresLocalDeviceAuthentication
    static let title: LocalizedStringResource = "البحث الذكي"
    static let description = IntentDescription("ابحث عن موظف أو جهة تنفيذ أو كتاب رسمي. يعيد الاختصار ملفاً؛ أضف بعده نسخ إلى الحافظة أو نظرة سريعة أو مشاركة.", categoryName: "البحث")

    @Parameter(title: "البحث", requestValueDialog: "ما اسم الموظف أو جهة التنفيذ أو الكتاب؟")
    var query: String
    @Parameter(title: "النتيجة") var selected: SmartShortcutChoice?
    @Parameter(title: "صيغة كشف الجهة", requestValueDialog: "اختر صيغة كشف الجهة") var format: SmartShortcutExport?

    static var parameterSummary: some ParameterSummary { Summary("ابحث ذكياً عن \(\.$query)") }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<IntentFile> & ShowsSnippetIntent {
        let choices = try await SmartShortcutSearch.choices(query)
        guard let first = choices.first else { throw ShortcutError.noMatch(query) }
        let choice: SmartShortcutChoice
        if choices.count == 1 { choice = first }
        else { choice = try await $selected.requestDisambiguation(among: choices, dialog: "اختر نتيجة البحث") }
        guard let item = choice.payload else { throw ShortcutError.recordMissing }
        let file: IntentFile
        var snippetChoice = choice
        switch item.kind {
        case .person:
            guard let employee = SmartShortcutEmployee.find(item.reference) else { throw ShortcutError.recordMissing }
            let files = try PayslipExporter.export(employee: employee, deductions: try EmployeeLookup.deductions(of: employee))
            file = IntentFile(fileURL: files.imageURL, filename: files.imageURL.lastPathComponent, type: .png)
        case .entity:
            let output: SmartShortcutExport
            if let format { output = format }
            else { output = try await $format.requestDisambiguation(among: [.pdf, .excel], dialog: "اختر صيغة كشف جهة التنفيذ") }
            let group = ReportGroup(kind: .deductions, month: item.month, title: item.reference,
                                    count: item.count, total: item.total, byEntity: item.byEntity)
            let container = SharedModel.container
            let rows = try await Task.detached(priority: .userInitiated) {
                try await ReportEngine(modelContainer: container).rows(for: group)
            }.value
            var updated = item
            updated.count = rows.count
            updated.total = rows.reduce(0) { $0 + $1.amount }
            updated.repeated = max(0, rows.count - item.count)
            if let corrected = SmartShortcutChoice(updated) { snippetChoice = corrected }
            let url: URL
            if output == .pdf {
                url = try await ReportFiles.preview(group: group, rows: rows)
                file = IntentFile(fileURL: url, filename: ReportSheet.fileName(for: group) + ".pdf", type: .pdf)
            } else {
                url = try await PDFWork.run {
                    let folder = FileManager.default.temporaryDirectory.appendingPathComponent("smart-shortcut-\(UUID().uuidString)", isDirectory: true)
                    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                    let target = folder.appendingPathComponent(ReportSheet.fileName(for: group) + ".xlsx")
                    try XLSXWriter.write(ReportSheet.make(group: group, rows: rows), to: target)
                    return target
                }
                file = IntentFile(fileURL: url, filename: url.lastPathComponent, type: UTType(filenameExtension: "xlsx") ?? .spreadsheet)
            }
        case .book:
            guard let id = UUID(uuidString: item.reference),
                  let book = OfficialBookStore.shared.books.first(where: { $0.id == id }) else { throw ShortcutError.recordMissing }
            try OfficialBookFiles.validate(book)
            let source = OfficialBookFiles.file(id)
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("smart-book-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let copy = folder.appendingPathComponent("document.pdf")
            try FileManager.default.copyItem(at: source, to: copy)
            file = IntentFile(fileURL: copy, filename: "document.pdf", type: .pdf)
        }
        return .result(value: file, snippetIntent: SmartLookupSnippetIntent(result: snippetChoice))
    }
}

enum SmartShortcutEmployee {
    @MainActor static func find(_ reference: String) -> Employee? {
        guard let data = Data(base64Encoded: reference),
              let id = try? JSONDecoder().decode(PersistentIdentifier.self, from: data) else { return nil }
        return SharedModel.container.mainContext.model(for: id) as? Employee
    }
}

enum SmartShortcutSearch {
    @MainActor static func choices(_ query: String) async throws -> [SmartShortcutChoice] {
        let context = SharedModel.container.mainContext
        let months = try context.fetch(FetchDescriptor<PayrollMonth>(sortBy: [
            SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)]))
        let accounts = UserDefaults.standard.data(forKey: ExecutionAccountStore.storageKey) ?? Data()
        let results = try await SmartSearch.find(query, months: months, accountData: accounts)
        var choices: [SmartShortcutChoice] = []
        for person in results.people.prefix(50) {
            guard let employee = person.employee else { continue }
            guard let encodedID = try? JSONEncoder().encode(employee.persistentModelID) else { continue }
            let ref = encodedID.base64EncodedString()
            if let choice = SmartShortcutChoice(SmartShortcutPayload(kind: .person, title: employee.name, month: employee.month, reference: ref)) { choices.append(choice) }
        }
        for group in results.groups where group.kind == .deductions {
            if let choice = SmartShortcutChoice(SmartShortcutPayload(kind: .entity, title: group.title, month: group.month,
                                                                      reference: group.title, count: group.count, total: group.total, byEntity: group.byEntity)) { choices.append(choice) }
        }
        for book in results.books {
            if let choice = SmartShortcutChoice(SmartShortcutPayload(kind: .book, title: book.title, month: book.month,
                                                                      reference: book.id.uuidString, count: book.pages)) { choices.append(choice) }
        }
        return choices
    }
}

struct SmartLookupSnippetIntent: SnippetIntent {
    static let title: LocalizedStringResource = "نتيجة البحث الذكي"
    static let isDiscoverable = false
    @Parameter(title: "النتيجة") var result: SmartShortcutChoice
    init() {}
    init(result: SmartShortcutChoice) { self.result = result }

    @MainActor
    func perform() async throws -> some IntentResult & ShowsSnippetView {
        guard let item = result.payload else { throw ShortcutError.recordMissing }
        var person: PayslipSnippetData?
        if item.kind == .person {
            guard let employee = SmartShortcutEmployee.find(item.reference) else { throw ShortcutError.recordMissing }
            person = PayslipSnippetData(employee: employee, deductions: try EmployeeLookup.deductions(of: employee))
        }
        return .result(view: SmartLookupSnippetView(item: item, person: person))
    }
}

struct SmartLookupSnippetView: View {
    let item: SmartShortcutPayload
    let person: PayslipSnippetData?
    var body: some View {
        Group {
            if let person {
                VStack(alignment: .leading, spacing: 8) {
                    PayslipSnippetView(data: person)
                    Text(tr("لنسخ صورة القسيمة، أضف إجراء «نسخ إلى الحافظة» بعد البحث.", "To copy the payslip image, add Copy to Clipboard after the search."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            } else {
                VStack(alignment: .leading, spacing: 12) {
                    Label(item.month, systemImage: "calendar").font(.caption).foregroundStyle(.secondary)
                    Label(item.title, systemImage: item.kind == .book ? "doc.text" : "building.columns").font(.headline)
                    if item.kind == .entity {
                        LabeledContent(tr("المبلغ", "Amount"), value: Money.format(item.total))
                        LabeledContent(tr("عدد الموظفين", "Employee count"), value: Num.format(item.count))
                        if item.repeated > 0 {
                            LabeledContent(tr("المكرر", "Repeated rows"), value: Num.format(item.repeated))
                        }
                    } else {
                        LabeledContent(tr("الصفحات", "Pages"), value: Num.format(item.count))
                    }
                    Text(tr("للمعاينة أو الإرسال، أضف «نظرة سريعة» أو «مشاركة» بعد البحث.", "For preview or sharing, add Quick Look or Share after the search."))
                        .font(.caption).foregroundStyle(.secondary)
                }
                .padding(16)
            }
        }
    }
}

#if targetEnvironment(simulator)
/// استيراد وهمي ثم اختبار البحث والملفات الثلاثة دون استخدام بيانات حقيقية.
enum SmartShortcutSelfTest {
    @MainActor static func runIfRequested() async {
        guard UserDefaults.standard.bool(forKey: "smartShortcutTest") else { return }
        let store = OfficialBookStore.shared
        var testBook: OfficialBook?
        defer { if let testBook { try? store.delete(testBook) } }
        do {
            let months = try SharedModel.container.mainContext.fetch(FetchDescriptor<PayrollMonth>())
            guard let month = months.first else { throw ShortcutError.recordMissing }
            let renderer = UIGraphicsImageRenderer(size: CGSize(width: 20, height: 20))
            guard let image = renderer.image(actions: { _ in UIColor.white.setFill(); UIRectFill(CGRect(x: 0, y: 0, width: 20, height: 20)) }).jpegData(compressionQuality: 0.8) else { throw ShortcutError.recordMissing }
            let old = Set(store.books.map(\.id))
            try await store.save(title: "كتاب تجريبي للاختصار", month: month.name, entity: "", images: [image])
            testBook = store.books.first { !old.contains($0.id) }
            let persons = try await SmartShortcutSearch.choices("أحمد علي حسين الدليمي")
            let entities = try await SmartShortcutSearch.choices("تنفيذ الخالدية")
            let books = try await SmartShortcutSearch.choices("كتاب تجريبي للاختصار")
            guard let person = persons.compactMap(\.payload).first(where: { $0.kind == .person }),
                  let employee = SmartShortcutEmployee.find(person.reference),
                  let entity = entities.compactMap(\.payload).first(where: { $0.kind == .entity }),
                  let book = books.compactMap(\.payload).first(where: { $0.kind == .book }),
                  book.reference == testBook?.id.uuidString else { throw ShortcutError.recordMissing }
            let group = ReportGroup(kind: .deductions, month: entity.month, title: entity.reference, count: entity.count, total: entity.total, byEntity: entity.byEntity)
            let rows = try await ReportEngine(modelContainer: SharedModel.container).rows(for: group)
            let slip = try PayslipExporter.export(employee: employee, deductions: try EmployeeLookup.deductions(of: employee))
            let pdf = try await ReportFiles.preview(group: group, rows: rows)
            let xlsx = FileManager.default.temporaryDirectory.appendingPathComponent("smart-shortcut-selftest.xlsx")
            try await PDFWork.run { try XLSXWriter.write(ReportSheet.make(group: group, rows: rows), to: xlsx) }
            guard !rows.isEmpty, FileManager.default.fileExists(atPath: slip.imageURL.path),
                  FileManager.default.fileExists(atPath: pdf.path), FileManager.default.fileExists(atPath: xlsx.path),
                  let testBook else { throw ShortcutError.recordMissing }
            try OfficialBookFiles.validate(testBook)
            LaunchOptions.writeReport("smartShortcut=ok person=1 entity=1 book=1 pdf=1 excel=1")
        } catch {
            LaunchOptions.writeReport("smartShortcut=failed \(error.localizedDescription)")
        }
    }
}
#endif
