import Foundation
import SwiftData

/// موظف من قاعدة الملاك (Access). الحقول المستخدمة في البحث والقائمة محفوظة كأعمدة،
/// وكل المعلومات الأخرى محفوظة كأقسام جاهزة للعرض (الحقول الفارغة غير محفوظة).
@Model
final class StaffMember {
    #Index<StaffMember>([\.nameKey], [\.motherKey], [\.statNumber], [\.unifiedNumber])

    var name: String
    var nameKey: String
    var motherKey: String
    var statNumber: String
    var unifiedNumber: String
    var rank: String
    var status: String
    var workplace: String
    /// [StaffSection] بصيغة JSON.
    var detailsData: Data

    init(name: String, mother: String, statNumber: String, unifiedNumber: String,
         rank: String, status: String, workplace: String, sections: [StaffSection]) {
        self.name = name
        self.nameKey = ArabicText.normalize(name)
        self.motherKey = ArabicText.normalize(mother)
        self.statNumber = statNumber
        self.unifiedNumber = unifiedNumber
        self.rank = rank
        self.status = status
        self.workplace = workplace
        self.detailsData = (try? JSONEncoder().encode(sections)) ?? Data()
    }

    var sections: [StaffSection] {
        (try? JSONDecoder().decode([StaffSection].self, from: detailsData)) ?? []
    }
}

nonisolated struct StaffSection: Codable, Hashable, Sendable {
    var title: String
    var fields: [StaffField]
}

nonisolated struct StaffField: Codable, Hashable, Sendable {
    var label: String
    var value: String
}

/// معلومات آخر استيراد للملاك (عدد السجلات، التاريخ، اسم الملف، الحالات الوظيفية للفلترة).
nonisolated enum StaffInfo {
    private static let countKey = "staffCount"
    private static let dateKey = "staffImportedAt"
    private static let fileKey = "staffFileName"
    private static let statusesKey = "staffStatuses"

    static var count: Int { UserDefaults.standard.integer(forKey: countKey) }
    static var importedAt: Date? { UserDefaults.standard.object(forKey: dateKey) as? Date }
    static var fileName: String { UserDefaults.standard.string(forKey: fileKey) ?? "" }
    /// الحالات الوظيفية مرتبة من الأكثر إلى الأقل.
    static var statuses: [String] { UserDefaults.standard.stringArray(forKey: statusesKey) ?? [] }

    static func save(count: Int, fileName: String, statuses: [String]) {
        UserDefaults.standard.set(count, forKey: countKey)
        UserDefaults.standard.set(Date(), forKey: dateKey)
        UserDefaults.standard.set(fileName, forKey: fileKey)
        UserDefaults.standard.set(statuses, forKey: statusesKey)
    }

    static func clear() {
        for key in [countKey, dateKey, fileKey, statusesKey] { UserDefaults.standard.removeObject(forKey: key) }
    }
}

// MARK: - ترتيب الحقول في الأقسام (مثل نافذة Access)

nonisolated enum StaffLayout {
    /// أعمدة لا تُعرض (أرقام داخلية ومسارات صور على الكمبيوتر).
    static let hidden: Set<String> = ["رقم_معرف", "Imagepath", "Bookname"]

    static let sections: [(title: String, columns: [String])] = [
        ("الوظيفية", ["صنف الوظيفة", "الصنف", "الرتبة", "العنوان الوظيفي", "جهة التعيين", "اشارة الركن",
                      "الحالة الوظيفية", "حالة العمل", "حالة التعيين", "نوع التعيين", "المنصب الاداري", "المنصب",
                      "رقم الامر الوزاري لاسناد المنصب", "تاريخ امر المنصب", "تاريخ المباشرة بالمنصب",
                      "الصنف الاداري", "الصنف الرئيسي", "الصنف الفرعي", "طبيعة العمل", "ممارسة الاختصاص"]),
        ("الترقية", ["الشهر", "سنة الترقية", "رقم امر الترقية", "تاريخ الترقية"]),
        ("مكان العمل", ["المحافظة", "الوكالة او الهيئة", "المديرية العامة", "المديرية", "المديرية الفرعية",
                        "القسم", "الشعبة", "هل يعمل ضمن مقر المديرية", "تاريخ المباشرة في الدائرة"]),
        ("التعيين والمباشرة", ["رقم الامر", "تاريخ الامر", "تاريخ مباشرة بالخدمة لاول مرة", "تاريخ مباشرة بالاعادة",
                               "رقم امر الاعادة للخدمة", "تاريخ امر الاعادة للخدمة", "لديه تمديد", "مدة التمديد",
                               "رقم امر التمديد", "تاريخ امر التمديد"]),
        ("المعلومات الشخصية", ["اسم الام الكامل", "التولد", "الجنس", "الحالة الزوجية", "اسم الزوجة/ الزوج",
                               "عدد الاطفال", "القومية", "الديانة", "الحالة الصحية", "نسبة العجز", "من ذوي"]),
        ("التحصيل الدراسي", ["التحصيل الدراسي", "نوع الشهادة عسكري او مدني", "الجامعة", "اسم الكلية او المعهد",
                             "تخصص الشهادة", "التخصص", "سنة التخرج", "الدورات", "دورات الضباط", "رقم الدورة"]),
        ("الأرقام", ["الرقم الاحصائي", "الرقم الموحد", "الرقم الوظيفي", "رقم الضمان", "رقم البطاقة الموحدة",
                     "رقم البطاقة التموينية", "رقم الهوية العسكرية", "تاريخ اصدار الهوية العسكرية", "رقم الهاتف"]),
        ("السكن", ["محافظة السكن", "القضاء او الناحية", "محلة", "زقاق", "دار", "اقرب نقطة دالة"]),
        ("الراتب", ["صرف الراتب", "جهة تقاضي الراتب", "اسباب عدم صرف الراتب", "اسباب ايقاف صرق الراتب",
                    "الوكالة حسب الملاك الحسابي", "المديرية حسب الملاك الحسابي", "التبويب المالي حسب الملاك الحسابي"]),
        ("معلومات سائق الضابط", ["صفة العمل", "رتبة", "الاسم"]),
    ]

    /// يُعرض في آخر الصفحة.
    static let notesColumn = "الملاحظات"
    static let recordColumns = ["تاريخ الادخال", "تاريخ التحديث"]

    /// عناوين أوضح لبعض الأعمدة.
    static let labels: [String: String] = [
        "اسم الام الكامل": "اسم الأم",
        "الشهر": "شهر الترقية",
        "المحافظة": "محافظة العمل",
        "رقم الامر": "رقم امر التعيين",
        "تاريخ الامر": "تاريخ التعيين",
        "اسباب ايقاف صرق الراتب": "أسباب إيقاف صرف الراتب",
        "رتبة": "رتبة السائق",
        "الاسم": "اسم السائق",
        "صفة العمل": "صفة العمل",
        "اسم الكلية او المعهد": "الكلية او المعهد",
        "نوع الشهادة عسكري او مدني": "نوع الشهادة",
        "هل يعمل ضمن مقر المديرية": "يعمل ضمن المقر",
    ]

    /// أعمدة مرمّزة جدول قيمها باسم آخر.
    static let lookupAliases: [String: String] = [
        "محافظة السكن": "المحافظة",
        "الوكالة او الهيئة": "الوكالة",
        "المديرية العامة": "المديرية او الهيئة",
        "المنصب الاداري": "المناصب",
        "الجهة المنقول اليها": "الدائرة المنقول اليها",
        "هل يعمل ضمن مقر المديرية": "هل يعمل ضمن المقر",
        "الشهر": "شهر الترقية",
    ]
}

// MARK: - الاستيراد من ملف Access

@ModelActor
actor StaffImporter {
    enum ImportError: LocalizedError {
        case noMainTable
        case noRows
        var errorDescription: String? {
            switch self {
            case .noMainTable: tr("لم يُعثر في قاعدة البيانات على جدول الموظفين: المطلوب جدول باسم (الرئيسي) أو جدول فيه عمود (الاسم الكامل).", "No employee table: expected a table named (الرئيسي) or one with a (الاسم الكامل) column.")
            case .noRows: tr("جدول الموظفين فارغ.", "The employee table is empty.")
            }
        }
    }

    /// فحص سريع قبل الاستيراد: عدد سجلات الجدول الرئيسي.
    nonisolated static func peekCount(at url: URL) throws -> Int {
        let data = try Data(contentsOf: url, options: .alwaysMapped)
        return try data.withUnsafeBytes { buffer in
            let reader = try AccessReader(bytes: buffer)
            let tables = try reader.userTables()
            if let page = tables["الرئيسي"] { return try reader.table(at: page).rowCount }
            var best: Int?
            for (_, page) in tables {
                guard let t = try? reader.table(at: page), t.columns.contains(where: { $0.name == "الاسم الكامل" }) else { continue }
                best = max(best ?? 0, t.rowCount)
            }
            guard let best else { throw ImportError.noMainTable }
            return best
        }
    }

    /// يقرأ ملف .accdb ويستبدل بيانات الملاك كلها. يعيد عدد الموظفين.
    func importDatabase(at url: URL, progress: ProgressSink? = nil) throws -> Int {
        let data = try Data(contentsOf: url, options: .alwaysMapped)
        let fileName = url.deletingPathExtension().lastPathComponent

        var members: [StaffMember] = []
        var statusCounts: [String: Int] = [:]
        try data.withUnsafeBytes { buffer in
            let reader = try AccessReader(bytes: buffer)
            let tables = try reader.userTables()

            // الجدول الرئيسي: "الرئيسي"، وإلا أكبر جدول فيه عمود "الاسم الكامل".
            var mainPage = tables["الرئيسي"]
            if mainPage == nil {
                var best = 0
                for (_, page) in tables {
                    guard let t = try? reader.table(at: page), t.columns.contains(where: { $0.name == "الاسم الكامل" }) else { continue }
                    if t.rowCount > best { best = t.rowCount; mainPage = page }
                }
            }
            guard let mainPage else { throw ImportError.noMainTable }
            let main = try reader.table(at: mainPage)
            let rowTotal = Double(max(1, main.rowCount))
            let firstPass = progress?.sub(0, 0.12, tr("فحص قاعدة البيانات", "Scanning the database"))
            let lists = progress?.sub(0.12, 0.18, tr("تجهيز القوائم", "Preparing lists"))
            let secondPass = progress?.sub(0.18, 0.55, tr("قراءة الموظفين", "Reading employees"))

            let integerTypes: Set<UInt8> = [AccessReader.ColumnType.byte.rawValue, AccessReader.ColumnType.int.rawValue,
                                             AccessReader.ColumnType.long.rawValue]
            // الأعمدة المرمّزة: رقم يقابله نص في جدول القوائم.
            var codedTables: [String: Int] = [:]
            for c in main.columns where integerTypes.contains(c.type) {
                let tableName = StaffLayout.lookupAliases[c.name] ?? c.name
                if let page = tables[tableName], page != mainPage { codedTables[c.name] = page }
            }

            // المرور الأول: القيم المستخدمة في كل عمود مرمّز، لاختيار عمود المفتاح الصحيح في جدول القوائم.
            var used: [String: Set<Int>] = [:]
            var scanned = 0
            reader.forEachRow(of: mainPage, table: main) { row in
                scanned += 1
                if scanned % 500 == 0 { firstPass?(Double(scanned) / rowTotal) }
                for name in codedTables.keys {
                    if case .int(let v)? = row[name] { used[name, default: []].insert(v) }
                }
            }
            var lookups: [String: [Int: String]] = [:]
            for (column, page) in codedTables {
                guard let t = try? reader.table(at: page) else { continue }
                var rows: [[String: AccessReader.Value]] = []
                reader.forEachRow(of: page, table: t) { rows.append($0) }
                let textColumns = t.columns.filter {
                    $0.type == AccessReader.ColumnType.text.rawValue || $0.type == AccessReader.ColumnType.memo.rawValue
                }
                let tableName = StaffLayout.lookupAliases[column] ?? column
                guard let textColumn = textColumns.first(where: { $0.name == tableName || $0.name == column })?.name
                        ?? textColumns.first?.name else { continue }
                let values = used[column] ?? []
                var bestMap: [Int: String] = [:]
                var bestHits = -1
                for key in t.columns where integerTypes.contains(key.type) {
                    var map: [Int: String] = [:]
                    for r in rows {
                        if case .int(let k)? = r[key.name], case .text(let text)? = r[textColumn] {
                            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                            if !trimmed.isEmpty { map[k] = trimmed }
                        }
                    }
                    let hits = values.reduce(0) { $0 + (map[$1] != nil ? 1 : 0) }
                    if hits > bestHits { bestHits = hits; bestMap = map }
                }
                if bestHits > 0 { lookups[column] = bestMap }
                lists?(Double(lookups.count) / Double(max(1, codedTables.count)))
            }

            // المرور الثاني: بناء سجل لكل موظف.
            let placed = Set(StaffLayout.sections.flatMap(\.columns) + [StaffLayout.notesColumn] + StaffLayout.recordColumns)
            let otherColumns = main.columns.map(\.name).filter { !placed.contains($0) && !StaffLayout.hidden.contains($0) && $0 != "الاسم الكامل" }
            var built = 0
            reader.forEachRow(of: mainPage, table: main) { row in
                built += 1
                if built % 500 == 0 { secondPass?(Double(built) / rowTotal) }
                func text(_ column: String) -> String? {
                    guard let value = row[column] else { return nil }
                    return Self.display(value, lookup: lookups[column])
                }
                guard let name = text("الاسم الكامل") else { return }

                var sections: [StaffSection] = []
                func add(_ title: String, _ columns: [String]) {
                    let fields = columns.compactMap { column -> StaffField? in
                        guard let value = text(column) else { return nil }
                        return StaffField(label: StaffLayout.labels[column] ?? column, value: value)
                    }
                    if !fields.isEmpty { sections.append(StaffSection(title: title, fields: fields)) }
                }
                for section in StaffLayout.sections { add(section.title, section.columns) }
                add("الأوامر ومعلومات أخرى", otherColumns)
                add("الملاحظات", [StaffLayout.notesColumn])
                add("السجل", StaffLayout.recordColumns)

                let status = text("الحالة الوظيفية") ?? ""
                if !status.isEmpty { statusCounts[status, default: 0] += 1 }
                members.append(StaffMember(
                    name: name,
                    mother: text("اسم الام الكامل") ?? "",
                    statNumber: text("الرقم الاحصائي") ?? "",
                    unifiedNumber: text("الرقم الموحد") ?? "",
                    rank: text("الرتبة") ?? "",
                    status: status,
                    workplace: text("القسم") ?? text("المديرية") ?? "",
                    sections: sections
                ))
            }
        }
        guard !members.isEmpty else { throw ImportError.noRows }

        // استبدال الملاك القديم بالجديد، والحفظ على دفعات.
        try modelContext.delete(model: StaffMember.self)
        try modelContext.save()
        let saving = progress?.sub(0.55, 1, tr("حفظ الملاك", "Saving staff"))
        let count = Double(members.count)
        for (i, member) in members.enumerated() {
            modelContext.insert(member)
            if i % 3000 == 2999 { try modelContext.save() }
            if i % 500 == 499 { saving?(Double(i + 1) / count) }
        }
        try modelContext.save()

        let statuses = statusCounts.sorted { $0.value > $1.value }.map(\.key)
        StaffInfo.save(count: members.count, fileName: fileName, statuses: statuses)
        return members.count
    }

    func deleteAll() throws {
        try modelContext.delete(model: StaffMember.self)
        try modelContext.save()
        StaffInfo.clear()
    }

    /// القيمة كنص للعرض (بأرقام لاتينية؛ تتحول إلى هندية عند العرض حسب الإعداد). nil = فارغة.
    private static func display(_ value: AccessReader.Value, lookup: [Int: String]?) -> String? {
        switch value {
        case .text(let s):
            let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
            return t.isEmpty ? nil : t
        case .int(let i):
            if let lookup { return lookup[i] ?? (i == 0 ? nil : String(i)) }
            return i == 0 ? nil : String(i)
        case .double(let d):
            guard d != 0, d.isFinite else { return nil }
            return d == d.rounded() ? String(Int(d)) : String(d)
        case .date(let date):
            let year = Calendar(identifier: .gregorian).component(.year, from: date)
            guard year > 1900 else { return nil }
            return dateFormatter.string(from: date)
        case .bool(let b):
            return b ? "نعم" : nil
        }
    }

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(secondsFromGMT: 0)
        f.dateFormat = "yyyy/MM/dd"
        return f
    }()
}
