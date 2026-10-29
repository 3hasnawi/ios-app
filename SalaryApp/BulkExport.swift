import SwiftUI
import SwiftData

/// أنواع جهات التنفيذ في ملف الاستقطاعات، للتصدير المجمّع.
nonisolated enum EntityCategory: String, CaseIterable, Identifiable, Sendable {
    case directorates, banks, others, all
    var id: String { rawValue }

    var title: String {
        switch self {
        case .directorates: tr("كل مديريات التنفيذ", "All execution directorates")
        case .banks: tr("كل المصارف", "All banks")
        case .others: tr("الجهات الأخرى", "Other entities")
        case .all: tr("كل الجهات", "All entities")
        }
    }

    /// للعنوان واسم الملف.
    var listName: String {
        switch self {
        case .directorates: "مديريات التنفيذ"
        case .banks: "المصارف"
        case .others: "الجهات الأخرى"
        case .all: "كل الجهات"
        }
    }

    var systemImage: String {
        switch self {
        case .directorates: "building.columns.fill"
        case .banks: "banknote.fill"
        case .others: "ellipsis"
        case .all: "square.grid.2x2.fill"
        }
    }

    var color: Color {
        switch self {
        case .directorates: .indigo
        case .banks: .green
        case .others: .gray
        case .all: .orange
        }
    }

    /// "مديرية تنفيذ الرمادي" ← مديريات، "مصرف الرشيد / فرع ..." و"م المشرق ..." ← مصارف، والباقي جهات أخرى.
    static func of(_ title: String) -> EntityCategory {
        let key = ArabicText.normalize(title)
        if key.hasPrefix(ArabicText.normalize("مديرية تنفيذ")) || key.hasPrefix(ArabicText.normalize("مديرية التنفيذ")) { return .directorates }
        if key.contains(ArabicText.normalize("مصرف")) || key.hasPrefix("م ") { return .banks }
        return .others
    }

    func contains(_ title: String) -> Bool { self == .all || Self.of(title) == self }
}

extension ReportEngine {
    /// صفوف كل الجهات دفعة واحدة (للتصدير المجمّع): الجهة ← صفوفها مرتبة بالاسم.
    func deductionRows(month: String, byEntity: Bool) throws -> [String: [ReportRow]] {
        var e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        e.propertiesToFetch = [\.matchKey, \.rank, \.workplace]
        var info: [String: (String, String)] = [:]
        for emp in try modelContext.fetch(e) where info[emp.matchKey] == nil { info[emp.matchKey] = (emp.rank, emp.workplace) }
        var d = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
        d.propertiesToFetch = [\.title, \.amount, \.matchKey, \.employeeName, \.note, \.rank]
        var result: [String: [ReportRow]] = [:]
        for (i, item) in try modelContext.fetch(d).enumerated() {
            if DeductionStatus.read(item.note, amount: item.amount) != nil { continue }
            let key = byEntity ? Self.entity(of: item.title) : item.title
            let (payrollRank, workplace) = info[item.matchKey] ?? ("", "")
            let rank = item.rank.isEmpty ? payrollRank : item.rank
            result[key, default: []].append(ReportRow(id: i, name: item.employeeName, rank: rank, workplace: workplace,
                                                      amount: item.amount ?? 0, note: item.note, entity: item.title))
        }
        for key in result.keys { result[key]?.sort { $0.name < $1.name } }
        return result
    }
}

/// تصدير عدة جهات: Excel بورقة لكل جهة (وورقة خلاصة اختيارية)، أو PDF مجمّع.
nonisolated enum BulkExport {
    struct Entity: Sendable {
        let group: ReportGroup
        let rows: [ReportRow]
    }

    /// ترتيب الجهات أبجدياً.
    static func sorted(_ entities: [Entity]) -> [Entity] {
        entities.sorted { $0.group.title.localizedStandardCompare($1.group.title) == .orderedAscending }
    }

    static func fileName(_ category: EntityCategory, month: String) -> String {
        "قائمة \(category.listName) - \(month)"
    }

    /// Excel يقبل اسم ورقة حتى 31 حرفاً، ولا يقبل / \ ? * [ ] :
    /// "مصرف الرشيد / فرع المعارض الحديثة" ← "مصرف الرشيد - المعارض الحديثة".
    static func sheetNames(_ titles: [String]) -> [String] {
        var used = Set<String>()
        return titles.map { title in
            var name = title
            for bad in ["\\", "?", "*", "[", "]", ":"] { name = name.replacingOccurrences(of: bad, with: " ") }
            name = name.replacingOccurrences(of: "/", with: " - ")
            name = name.split(whereSeparator: \.isWhitespace).joined(separator: " ")
            name = name.replacingOccurrences(of: "- -", with: "-")
            if name.count > 31 { name = name.replacingOccurrences(of: "فرع ", with: "") }
            if name.count > 31 { name = name.replacingOccurrences(of: " - ", with: "-") }
            if name.count > 31 { name = String(name.prefix(31)) }
            name = name.trimmingCharacters(in: CharacterSet.whitespaces.union(CharacterSet(charactersIn: "'-")))
            if name.isEmpty { name = "جهة" }
            var unique = name
            var n = 2
            while used.contains(unique.lowercased()) {
                let suffix = " (\(n))"
                unique = String(name.prefix(31 - suffix.count)) + suffix
                n += 1
            }
            used.insert(unique.lowercased())
            return unique
        }
    }

    /// ورقة الخلاصة: كل جهة بعدد استقطاعاتها ومجموعها.
    static func summarySheet(_ entities: [Entity], category: EntityCategory, month: String) -> ReportSheet {
        let header = UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader
        return ReportSheet(
            header: header,
            title: "خلاصة استقطاعات \(category.listName) لـ شهر \(ReportSheet.easternDigits(month))",
            columns: [
                .init(title: "ت", width: 5, kind: .serial),
                .init(title: "جهة التنفيذ", width: 40, kind: .text),
                .init(title: "عدد الاستقطاعات", width: 16, kind: .text),
                .init(title: "المجموع", width: 16, kind: .amount),
            ],
            rows: entities.enumerated().map { i, e in [String(i + 1), e.group.title, String(e.rows.count), ""] },
            amounts: entities.map { e in e.rows.reduce(0) { $0 + $1.amount } }
        ).fitted()
    }

    static func excel(_ entities: [Entity], category: EntityCategory, month: String, summary: Bool) async throws -> URL {
        let ordered = sorted(entities)
        var sheets: [(name: String, sheet: ReportSheet)] = []
        let names = sheetNames((summary ? ["الخلاصة"] : []) + ordered.map(\.group.title))
        if summary { sheets.append((names[0], summarySheet(ordered, category: category, month: month))) }
        for (i, entity) in ordered.enumerated() {
            sheets.append((names[i + (summary ? 1 : 0)], ReportSheet.make(group: entity.group, rows: entity.rows)))
        }
        let name = fileName(category, month: month)
        return try await PDFWork.run {
            let url = try folder().appendingPathComponent(name + ".xlsx")
            try XLSXWriter.writeWorkbook(sheets, to: url)
            return url
        }
    }

    static func pdf(_ entities: [Entity], category: EntityCategory, month: String, summary: Bool) async throws -> URL {
        let ordered = sorted(entities)
        var sheets = summary ? [summarySheet(ordered, category: category, month: month)] : []
        sheets += ordered.map { ReportSheet.make(group: $0.group, rows: $0.rows) }
        let name = fileName(category, month: month)
        return try await PDFWork.run {
            let url = try folder().appendingPathComponent(name + ".pdf")
            try PDFReportWriter.write(sheets, to: url)
            try PDFDecor.apply(to: url)
            return url
        }
    }

    private static func folder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("report-export", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }
}
