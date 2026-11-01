import Foundation

/// دفتر الأمانات المشترك؛ المبالغ أعداد دينار صحيحة ولا تُحسب القيود المختلف عليها بصمت.
nonisolated enum SalaryTrust {
    static let settingsKey = "salaryTrustLedgerV1"
    static let maximum: Int64 = 9_007_199_254_740_991
    static let openingCutoff = 202609

    enum PaymentFilter: String, CaseIterable, Identifiable, Sendable {
        case all, unfinished, unpaid, partial, paid, review
        var id: String { rawValue }
        var title: String {
            switch self {
            case .all: tr("كل الحالات", "All statuses")
            case .unfinished: tr("غير مكتمل", "Not fully paid")
            case .unpaid: tr("غير مصروف", "Unpaid")
            case .partial: tr("مصروف جزئياً", "Partially paid")
            case .paid: tr("مصروف بالكامل", "Paid in full")
            case .review: tr("للمراجعة", "Needs review")
            }
        }
        func includes(_ entry: Entry) -> Bool {
            if self == .all { return true }
            if entry.reviewPending { return self == .review }
            switch self {
            case .all: return true
            case .unfinished: return entry.remaining > 0
            case .unpaid: return entry.paid == 0
            case .partial: return entry.paid > 0 && entry.remaining > 0
            case .paid: return entry.remaining == 0
            case .review: return false
            }
        }
    }
    static func paymentStatus(_ entry: Entry) -> String {
        if entry.reviewPending { return PaymentFilter.review.title }
        if entry.remaining == 0 { return PaymentFilter.paid.title }
        if entry.paid > 0 { return PaymentFilter.partial.title }
        return PaymentFilter.unpaid.title
    }
    static func monthLabel(_ key: Int) -> String {
        let arabic = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"]
        let english = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"]
        let index = key % 100 - 1
        guard (0..<12).contains(index) else { return "—" }
        return "\(AppSettings.isEnglish ? english[index] : arabic[index]) \(Num.digits(String(key / 100)))"
    }
    static func outstanding(_ entries: [Entry]) -> Int64 {
        entries.filter { !$0.reviewPending }.reduce(0) { $0 + $1.remaining }
    }

    struct Payment: Codable, Identifiable, Equatable, Sendable {
        var id: String
        var amount: Int64
        var bookNumber: String
        /// تاريخ مدني ميلادي yyyy-MM-dd، لا يتأثر بالمنطقة الزمنية.
        var bookDate: String
        var note: String
    }
    struct Entry: Codable, Identifiable, Equatable, Sendable {
        var id: String
        var name: String
        var rank: String
        var workplace: String
        var year: Int
        var month: Int
        var amount: Int64
        /// الحالة من عمود الملاحظات المجاور لصافي الراتب.
        var caseStatus: String
        var note: String
        var importedStatus: String
        var legacyPaid: Bool
        var reviewPending: Bool
        var payments: [Payment]
        /// روابط كتب الأرشيف فقط؛ لا تثبت الصرف ولا تغيّر الرصيد.
        var bookIds: [String]? = nil
        var paid: Int64 { legacyPaid ? amount : payments.reduce(0) { $0 + $1.amount } }
        var remaining: Int64 { amount - paid }
    }
    struct Ledger: Codable, Equatable, Sendable {
        var version: Int = 1
        var entries: [Entry] = []
        /// لم يُسجل شهر الافتتاح إلا بعد مراجعة الملف بكامله.
        var openingImported: Bool = false
    }
    static func invalid(_ text: String) -> NSError {
        NSError(domain: "SalaryTrust", code: 1, userInfo: [NSLocalizedDescriptionKey: text])
    }
    static func key(_ entry: Entry) -> String {
        "\(entry.year)-\(entry.month)|" + ArabicText.matchKey(entry.name)
    }
    static func validDate(_ text: String) -> Bool {
        let parts = text.split(separator: "-", omittingEmptySubsequences: false)
        guard parts.count == 3, parts[0].count == 4, parts[1].count == 2, parts[2].count == 2,
              let y = Int(parts[0]), let m = Int(parts[1]), let d = Int(parts[2]),
              (1900...2200).contains(y), (1...12).contains(m), (1...31).contains(d) else { return false }
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(secondsFromGMT: 0)!
        guard let date = c.date(from: DateComponents(year: y, month: m, day: d)) else { return false }
        let actual = c.dateComponents([.year, .month, .day], from: date)
        return actual.year == y && actual.month == m && actual.day == d
    }
    static func validate(_ ledger: Ledger) throws {
        guard ledger.version == 1, ledger.entries.count <= 100_000 else { throw invalid(tr("دفتر الأمانات غير صالح أو من إصدار أحدث.", "The trust ledger is invalid or from a newer version.")) }
        var ids = Set<String>(), keys = Set<String>(), payments = Set<String>()
        var total: Int64 = 0
        for e in ledger.entries {
            guard UUID(uuidString: e.id) != nil, ids.insert(e.id.lowercased()).inserted,
                  !e.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  e.name.count <= 300, e.rank.count <= 150, e.workplace.count <= 300,
                  e.caseStatus.count <= 2000, e.note.count <= 2000, e.importedStatus.count <= 300,
                  (1900...2200).contains(e.year), (1...12).contains(e.month),
                  e.amount > 0, e.amount <= maximum, keys.insert(key(e)).inserted,
                  (!e.legacyPaid || e.payments.isEmpty), (!e.reviewPending || (!e.legacyPaid && e.payments.isEmpty)) else { throw invalid(tr("سجل أمانة غير صالح أو مكرر؛ لم تُحفظ التغييرات.", "A trust entry is invalid or duplicated; changes were not saved.")) }
            let bookIds = e.bookIds ?? []
            guard bookIds.count <= 100, bookIds.allSatisfy({ UUID(uuidString: $0) != nil }),
                  Set(bookIds.map { $0.lowercased() }).count == bookIds.count else {
                throw invalid(tr("روابط كتب الأمانة غير صالحة أو مكررة.", "Trust document links are invalid or duplicated."))
            }
            var paid: Int64 = 0
            for p in e.payments {
                guard UUID(uuidString: p.id) != nil, payments.insert(p.id.lowercased()).inserted,
                      p.amount > 0, p.amount <= e.amount,
                      p.bookNumber.count <= 150, p.note.count <= 2000,
                      (p.bookDate.isEmpty || validDate(p.bookDate)), paid <= e.amount - p.amount else {
                    throw invalid(tr("دفعة الصرف غير صالحة أو تتجاوز الرصيد.", "A payment is invalid or exceeds the balance."))
                }
                paid += p.amount
            }
            guard total <= maximum - e.amount else { throw invalid(tr("مجموع الأمانات يتجاوز الحد المدعوم.", "Trust total exceeds the supported limit.")) }
            total += e.amount
        }
    }
    /// يربط كتاباً بمجموعة في عملية واحدة دون تغيير المبالغ أو حالة الصرف.
    static func linking(_ ledger: Ledger, bookID: UUID, entries ids: Set<String>) throws -> Ledger {
        try validate(ledger)
        guard !ids.isEmpty, ids.isSubset(of: Set(ledger.entries.map(\.id))) else {
            throw invalid(tr("تغيّرت القيود؛ أعد اختيارها.", "Entries changed; select them again."))
        }
        var result = ledger
        for index in result.entries.indices where ids.contains(result.entries[index].id) {
            var links = result.entries[index].bookIds ?? []
            if !links.contains(where: { UUID(uuidString: $0) == bookID }) { links.append(bookID.uuidString) }
            result.entries[index].bookIds = links
        }
        try validate(result)
        return result
    }
    static func validateBooks(_ ledger: Ledger, available: Set<UUID>) throws {
        for entry in ledger.entries {
            for id in entry.bookIds ?? [] {
                guard let uuid = UUID(uuidString: id), available.contains(uuid) else {
                    throw invalid(tr("كتاب مرتبط بأحد قيود الأمانات غير موجود في الأرشيف؛ لم تُحفظ النسخة.", "A document linked to a trust entry is missing from the archive; the backup was not saved."))
                }
            }
        }
    }
    static func decode(_ data: Data) throws -> Ledger {
        if data.isEmpty { return Ledger() }
        let ledger: Ledger
        do { ledger = try JSONDecoder().decode(Ledger.self, from: data) }
        catch { throw invalid(tr("تعذّر قراءة دفتر الأمانات؛ لم تتغير البيانات.", "Could not read the trust ledger; data was not changed.")) }
        try validate(ledger)
        return ledger
    }
    static func encode(_ ledger: Ledger) throws -> Data {
        try validate(ledger)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(ledger)
    }
}
