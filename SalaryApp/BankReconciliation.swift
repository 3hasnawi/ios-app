import Foundation

/// دفتر مطابقة مستقل؛ الحركات لا تدّعي حصولها في المصرف قبل تأشير المستخدم.
nonisolated enum BankReconciliation {
    static let settingsKey = "bankReconciliations"
    static let maximumAmount: Int64 = 9_007_199_254_740_991

    enum Failure: LocalizedError {
        case invalid, duplicated, outOfPeriod, refundMismatch, overflow, incompatibleVersion, locked, differentDay
        var errorDescription: String? {
            switch self {
            case .invalid: tr("تحقق من الفترة والمبلغ والبيان قبل الحفظ.", "Check the period, amount and description before saving.")
            case .duplicated: tr("هذه الحركة المقترحة أُضيفت مسبقاً إلى الفترة.", "This suggested movement was already added to the period.")
            case .outOfPeriod: tr("تاريخ الحركة خارج فترة المطابقة.", "The movement date is outside the reconciliation period.")
            case .refundMismatch: tr("راجع ربط المبالغ المسترجعة: يجب أن يكون الصرف مرتبطاً بوارد وأن لا يتجاوزه.", "Check refund links: repayments must reference a credit and cannot exceed it.")
            case .overflow: tr("مجموع الحركات يتجاوز الحد المدعوم؛ لم يُحفظ التغيير.", "The movement total exceeds the supported limit; the change was not saved.")
            case .incompatibleVersion: tr("تعذّر قراءة بيانات المطابقة المحفوظة؛ لم تُستبدل البيانات.", "Could not read saved reconciliation data; it was not replaced.")
            case .locked: tr("الفترة معتمدة ومقفلة؛ ألغِ اعتمادها قبل تعديلها أو حذفها.", "This period is confirmed and locked; remove the confirmation before editing or deleting it.")
            case .differentDay: tr("يمكن ترتيب الحركات داخل اليوم نفسه فقط؛ تبقى الأيام مرتبة حسب التاريخ.", "Movements can only be reordered within the same day; days remain in date order.")
            }
        }
    }
    enum Direction: String, Codable, CaseIterable, Sendable {
        case credit, debit
        var title: String { self == .credit ? tr("دائن · وارد", "Credit · incoming") : tr("مدين · مصروف", "Debit · outgoing") }
    }
    struct Movement: Codable, Identifiable, Equatable, Sendable {
        var id = UUID()
        var date: Date
        var description: String
        var amount: Int64
        var direction: Direction
        /// لا يملأ إلا عند إدراج اقتراح محدد من مصدر التطبيق.
        var sourceID: String? = nil
        /// مصادر الكتاب أو المجموعة تمنع احتساب البند نفسه ضمن حركتين مقترحتين.
        var sourceLineIDs: [String]? = nil
        /// ربط الصرف بوارد مسترجع صريح، ولا يُحسب ربطه حركة إضافية.
        var refundOf: UUID? = nil
        var isRefund: Bool? = nil
        var isFunding: Bool? = nil
        /// بصمة بيانات الحركة عند مراجعتها؛ أي تغيير في الحقول يلغي المطابقة.
        var matched: Bool = false
        /// حقول لا يعرفها هذا الإصدار (من ويندوز أو إصدار أحدث) تُحفظ كما وردت ولا تضيع عند الحفظ.
        var extra: [String: LedgerJSON]? = nil
    }
    /// بيانات الحساب؛ لا يُستنتج الحساب المحلي من رقم IBAN. يظهر في الكشف الاسم والفرع والعملة وIBAN فقط (حُذف الحساب المحلي بطلب المالك).
    struct AccountDetails: Equatable, Sendable {
        var name: String = ""
        var branch: String = "الأنبار"
        var localAccount: String = ""
        var iban: String = ""
        var isIncomplete: Bool {
            [name, branch, iban].contains { $0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        }
    }
    struct Period: Codable, Identifiable, Equatable, Sendable {
        var id = UUID()
        var month: String
        var start: Date
        var end: Date
        var openingBalance: Int64 = 0
        var memberFirst: String = ""
        var memberSecond: String = ""
        var firstRank: String? = nil
        var firstPosition: String? = nil
        var firstDate: String? = nil
        var secondRank: String? = nil
        var secondPosition: String? = nil
        var secondDate: String? = nil
        var signatureDefaultsInitialized: Bool? = nil
        var acceptedContractMissing: Int? = nil
        var acceptedContractAmount: Int64? = nil
        var movements: [Movement] = []
        /// وقت اعتماد مطابقة الفترة (مشترك مع ويندوز)؛ الفترة المعتمدة مقفلة حتى يُلغى اعتمادها.
        var confirmedAt: Date? = nil
        /// حقول لا يعرفها هذا الإصدار تُحفظ كما وردت.
        var extra: [String: LedgerJSON]? = nil
        var isConfirmed: Bool { confirmedAt != nil }
    }
    static func preparedSignatures(_ input: Period) -> Period {
        var value = input
        // الفترة المعتمدة تُعرض كما حُفظت؛ لا تُضاف إليها القيم الافتراضية فتبدو معدّلة.
        if value.signatureDefaultsInitialized == nil && value.confirmedAt == nil {
            if value.memberFirst.isEmpty { value.memberFirst = "باسم فواز نواف" }
            if value.memberSecond.isEmpty { value.memberSecond = "حميد زيدان خلف" }
            value.signatureDefaultsInitialized = true
        }
        return value
    }
    static func signatureLines(_ period: Period, first: Bool) -> [String] {
        if first {
            return [period.firstRank ?? "عقيد الشرطة", period.memberFirst,
                    period.firstPosition ?? "مدير قسم التدقيق والرقابة الداخلية",
                    ReportSheet.easternDigits(period.firstDate ?? signatureDate(period.end))]
        }
        return [period.secondRank ?? "العميد", period.memberSecond,
                period.secondPosition ?? "مدير الحسابات",
                ReportSheet.easternDigits(period.secondDate ?? signatureDate(period.end))]
    }
    struct Ledger: Codable, Sendable {
        var version = 1
        var periods: [Period] = []
    }
    /// اقتراح مصدره آخر تأكيد لمجموعة الشهر؛ لا يتضمن تاريخ تنفيذ ولا يؤشر المطابقة.
    struct GroupSuggestion: Identifiable, Sendable {
        let id: String
        let title: String
        let amount: Int64
        let confirmationID: UUID
        let sourceLineIDs: [String]
    }
    static func groupSuggestions(records: [TransferIssuedRecord], month: String) throws -> [GroupSuggestion] {
        var result: [GroupSuggestion] = []
        for category in [TransferCategory.rafidain, .otherBanks] {
            guard let record = TransferGroupIssuance.latest(records: records, category: category, month: month) else { continue }
            var amount: Int64 = 0
            for line in record.lines {
                guard line.amount.isFinite, line.amount > 0, line.amount.rounded() == line.amount,
                      line.amount <= Double(maximumAmount) else { throw Failure.invalid }
                let (sum, overflow) = amount.addingReportingOverflow(Int64(line.amount))
                guard !overflow, sum <= maximumAmount else { throw Failure.overflow }
                amount = sum
            }
            guard amount > 0 else { throw Failure.invalid }
            result.append(GroupSuggestion(id: "group|\(month)|\(category.rawValue)", title: category.title,
                                          amount: amount, confirmationID: record.id, sourceLineIDs: record.lines.map(\.id)))
        }
        return result
    }
    struct Line: Sendable {
        let movement: Movement
        let balance: Int64
    }
    struct OutstandingRefund: Identifiable, Sendable {
        let movement: Movement
        let remaining: Int64
        var id: UUID { movement.id }
    }
    struct Totals: Sendable {
        let credits: Int64
        let funding: Int64
        let debits: Int64
        let balance: Int64
        let lines: [Line]
        let outstandingRefunds: [OutstandingRefund]
    }
    private static var dayCalendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Asia/Baghdad") ?? .current
        return calendar
    }
    static func previousMonthKey(_ label: String) -> Int? {
        let key = MonthName.sortKey(label), year = key / 100, month = key % 100
        guard year >= 1900, (1...12).contains(month) else { return nil }
        return month == 1 ? (year - 1) * 100 + 12 : year * 100 + month - 1
    }
    static func day(_ value: Date) -> Date { dayCalendar.startOfDay(for: value) }
    static func reorder(_ period: Period, sourceID: UUID, targetID: UUID) throws -> Period {
        guard !period.isConfirmed else { throw Failure.locked }
        var ordered = try calculate(period).lines.map(\.movement)
        guard let source = ordered.firstIndex(where: { $0.id == sourceID }),
              let target = ordered.firstIndex(where: { $0.id == targetID }) else { throw Failure.invalid }
        guard day(ordered[source].date) == day(ordered[target].date) else { throw Failure.differentDay }
        guard source != target else { return period }
        let moved = ordered.remove(at: source)
        ordered.insert(moved, at: target)
        var result = period
        result.movements = ordered
        _ = try calculate(result)
        return result
    }
    static func signatureDate(_ date: Date) -> String {
        let components = dayCalendar.dateComponents([.month, .year], from: date)
        return ReportSheet.easternDigits(" / \(components.month ?? 0) / \(components.year ?? 0)")
    }
    static func contains(_ value: Date, in period: Period) -> Bool {
        let date = day(value)
        return date >= day(period.start) && date <= day(period.end)
    }
    static func decode(_ data: Data) throws -> Ledger {
        guard !data.isEmpty else { return Ledger() }
        let ledger: Ledger
        do { ledger = try JSONDecoder().decode(Ledger.self, from: data) }
        catch { throw Failure.incompatibleVersion }
        guard ledger.version == 1, ledger.periods.count <= 5_000,
              Set(ledger.periods.map(\.id)).count == ledger.periods.count else { throw Failure.incompatibleVersion }
        for period in ledger.periods { _ = try calculate(period) }
        return ledger
    }
    static func calculate(_ period: Period) throws -> Totals {
        guard period.confirmedAt.map({ $0.timeIntervalSinceReferenceDate.isFinite && (0...7_000_000_000).contains($0.timeIntervalSinceReferenceDate) }) ?? true,
              !period.month.isEmpty, day(period.start) <= day(period.end),
              period.openingBalance >= 0, period.openingBalance <= maximumAmount,
              period.memberFirst.count <= 150, period.memberSecond.count <= 150,
              [period.firstRank, period.firstPosition, period.firstDate, period.secondRank, period.secondPosition, period.secondDate].allSatisfy({ value in
                  guard let value else { return true }
                  return value.count <= 150 && !value.unicodeScalars.contains(where: { $0.value < 32 })
              }),
              (period.acceptedContractMissing == nil && period.acceptedContractAmount == nil) ||
                  ((period.acceptedContractMissing ?? 0) > 0 && (period.acceptedContractAmount ?? -1) >= 0 && (period.acceptedContractAmount ?? 0) <= maximumAmount),
              period.movements.count <= 20_000, Set(period.movements.map(\.id)).count == period.movements.count else { throw Failure.invalid }
        let sourceIDs = period.movements.compactMap(\.sourceID)
        guard sourceIDs.allSatisfy({ !$0.isEmpty && $0.count <= 1000 }), Set(sourceIDs).count == sourceIDs.count else { throw Failure.duplicated }
        let byID = Dictionary(uniqueKeysWithValues: period.movements.map { ($0.id, $0) })
        var refunded: [UUID: Int64] = [:]
        for movement in period.movements {
            guard let source = movement.refundOf else { continue }
            guard movement.direction == .debit, let incoming = byID[source], incoming.direction == .credit, incoming.isRefund == true,
                  incoming.refundOf == nil, day(movement.date) >= day(incoming.date), movement.amount > 0 else { throw Failure.refundMismatch }
            let (sum, overflow) = (refunded[source] ?? 0).addingReportingOverflow(movement.amount)
            guard !overflow, sum <= incoming.amount else { throw Failure.refundMismatch }
            refunded[source] = sum
        }
        let covered = period.movements.flatMap { $0.sourceLineIDs ?? [] }
        guard covered.allSatisfy({ !$0.isEmpty && $0.count <= 1000 }), Set(covered).count == covered.count else { throw Failure.duplicated }
        let ordered = period.movements.enumerated().sorted {
            let left = day($0.element.date), right = day($1.element.date)
            return left == right ? $0.offset < $1.offset : left < right
        }.map(\.element)
        var credits: Int64 = 0, debits: Int64 = 0, funding: Int64 = 0, balance = period.openingBalance
        var lines: [Line] = []
        for movement in ordered {
            guard contains(movement.date, in: period) else { throw Failure.outOfPeriod }
            guard movement.amount > 0, movement.amount <= maximumAmount,
                  movement.isRefund != true || movement.direction == .credit,
                  movement.isFunding != true || (movement.direction == .credit && movement.isRefund != true),
                  !movement.description.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  movement.description.count <= 200,
                  !movement.description.unicodeScalars.contains(where: { $0.value < 32 }) else { throw Failure.invalid }
            let credit = movement.direction == .credit ? movement.amount : 0
            let debit = movement.direction == .debit ? movement.amount : 0
            let (newCredits, creditOverflow) = credits.addingReportingOverflow(credit)
            let (newDebits, debitOverflow) = debits.addingReportingOverflow(debit)
            let (added, addOverflow) = balance.addingReportingOverflow(credit)
            let (newBalance, subtractOverflow) = added.subtractingReportingOverflow(debit)
            guard !creditOverflow, !debitOverflow, !addOverflow, !subtractOverflow,
                  newCredits <= maximumAmount, newDebits <= maximumAmount,
                  newBalance <= maximumAmount, newBalance >= -maximumAmount else { throw Failure.overflow }
            credits = newCredits; debits = newDebits; balance = newBalance
            if movement.isFunding == true {
                let (newFunding, overflow) = funding.addingReportingOverflow(movement.amount)
                guard !overflow, newFunding <= maximumAmount else { throw Failure.overflow }
                funding = newFunding
            }
            lines.append(Line(movement: movement, balance: balance))
        }
        let outstanding = ordered.compactMap { movement -> OutstandingRefund? in
            guard movement.direction == .credit, movement.isRefund == true else { return nil }
            let remaining = movement.amount - (refunded[movement.id] ?? 0)
            return remaining > 0 ? OutstandingRefund(movement: movement, remaining: remaining) : nil
        }
        return Totals(credits: credits, funding: funding, debits: debits, balance: balance, lines: lines, outstandingRefunds: outstanding)
    }
    /// يؤدي تغيير المبلغ أو التاريخ أو البيان أو الاتجاه إلى إلغاء التأشير السابق.
    static func replacing(_ old: Movement, with changed: Movement) -> Movement {
        var result = changed
        if old.date != changed.date || old.description != changed.description || old.amount != changed.amount || old.direction != changed.direction || old.sourceID != changed.sourceID || old.sourceLineIDs != changed.sourceLineIDs || old.refundOf != changed.refundOf || old.isRefund != changed.isRefund || old.isFunding != changed.isFunding {
            result.matched = false
        } else { result.matched = old.matched }
        result.extra = old.extra
        return result
    }
    /// الفترة المعتمدة لا تُعدّل ولا تُحذف إلا بعد إلغاء اعتمادها (نفس قاعدة ويندوز).
    static func checkLocked(before: [Period], after: [Period]) throws {
        for old in before where old.isConfirmed {
            guard let next = after.first(where: { $0.id == old.id }) else { throw Failure.locked }
            var unlocked = old
            unlocked.confirmedAt = nil
            guard next == old || next == unlocked else { throw Failure.locked }
        }
    }
}

/// قيمة JSON عامة لحفظ الحقول غير المعروفة في دفتر المطابقة.
nonisolated indirect enum LedgerJSON: Codable, Equatable, Sendable {
    case null, bool(Bool), integer(Int64), unsigned(UInt64), decimal(Decimal), string(String), array([LedgerJSON]), object([String: LedgerJSON])
    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null }
        else if let value = try? container.decode(Bool.self) { self = .bool(value) }
        else if let value = try? container.decode(Int64.self) { self = .integer(value) }
        else if let value = try? container.decode(UInt64.self) { self = .unsigned(value) }
        else if let value = try? container.decode(Decimal.self) { self = .decimal(value) }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else if let value = try? container.decode([LedgerJSON].self) { self = .array(value) }
        else { self = .object(try container.decode([String: LedgerJSON].self)) }
    }
    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .integer(let value): try container.encode(value)
        case .unsigned(let value): try container.encode(value)
        case .decimal(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        }
    }
}

nonisolated struct LedgerKey: CodingKey, Hashable {
    let stringValue: String
    init(_ value: String) { stringValue = value }
    init?(stringValue: String) { self.stringValue = stringValue }
    var intValue: Int? { nil }
    init?(intValue: Int) { return nil }
}

/// ترميز يدوي يطابق الترميز التلقائي السابق (التواريخ بإعداد المرمّز نفسه، والاختياري الفارغ لا يُكتب)
/// ويضيف الحقول غير المعروفة كما وردت.
extension BankReconciliation.Movement {
    private nonisolated static let known: Set<String> = ["id", "date", "description", "amount", "direction", "sourceID", "sourceLineIDs", "refundOf", "isRefund", "isFunding", "matched"]
    nonisolated init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: LedgerKey.self)
        id = try c.decode(UUID.self, forKey: LedgerKey("id"))
        date = try c.decode(Date.self, forKey: LedgerKey("date"))
        description = try c.decode(String.self, forKey: LedgerKey("description"))
        amount = try c.decode(Int64.self, forKey: LedgerKey("amount"))
        direction = try c.decode(BankReconciliation.Direction.self, forKey: LedgerKey("direction"))
        sourceID = try c.decodeIfPresent(String.self, forKey: LedgerKey("sourceID"))
        sourceLineIDs = try c.decodeIfPresent([String].self, forKey: LedgerKey("sourceLineIDs"))
        refundOf = try c.decodeIfPresent(UUID.self, forKey: LedgerKey("refundOf"))
        isRefund = try c.decodeIfPresent(Bool.self, forKey: LedgerKey("isRefund"))
        isFunding = try c.decodeIfPresent(Bool.self, forKey: LedgerKey("isFunding"))
        matched = try c.decodeIfPresent(Bool.self, forKey: LedgerKey("matched")) ?? false
        var unknown: [String: LedgerJSON] = [:]
        for key in c.allKeys where !Self.known.contains(key.stringValue) { unknown[key.stringValue] = try c.decode(LedgerJSON.self, forKey: key) }
        extra = unknown.isEmpty ? nil : unknown
    }
    nonisolated func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: LedgerKey.self)
        for (key, value) in extra ?? [:] where !Self.known.contains(key) { try c.encode(value, forKey: LedgerKey(key)) }
        try c.encode(id, forKey: LedgerKey("id")); try c.encode(date, forKey: LedgerKey("date"))
        try c.encode(description, forKey: LedgerKey("description")); try c.encode(amount, forKey: LedgerKey("amount"))
        try c.encode(direction, forKey: LedgerKey("direction"))
        try c.encodeIfPresent(sourceID, forKey: LedgerKey("sourceID")); try c.encodeIfPresent(sourceLineIDs, forKey: LedgerKey("sourceLineIDs"))
        try c.encodeIfPresent(refundOf, forKey: LedgerKey("refundOf")); try c.encodeIfPresent(isRefund, forKey: LedgerKey("isRefund"))
        try c.encodeIfPresent(isFunding, forKey: LedgerKey("isFunding")); try c.encode(matched, forKey: LedgerKey("matched"))
    }
}

extension BankReconciliation.Period {
    private nonisolated static let known: Set<String> = ["id", "month", "start", "end", "openingBalance", "memberFirst", "memberSecond", "firstRank", "firstPosition", "firstDate",
                                             "secondRank", "secondPosition", "secondDate", "signatureDefaultsInitialized", "acceptedContractMissing", "acceptedContractAmount",
                                             "movements", "confirmedAt"]
    nonisolated init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: LedgerKey.self)
        id = try c.decode(UUID.self, forKey: LedgerKey("id"))
        month = try c.decode(String.self, forKey: LedgerKey("month"))
        start = try c.decode(Date.self, forKey: LedgerKey("start"))
        end = try c.decode(Date.self, forKey: LedgerKey("end"))
        openingBalance = try c.decodeIfPresent(Int64.self, forKey: LedgerKey("openingBalance")) ?? 0
        memberFirst = try c.decodeIfPresent(String.self, forKey: LedgerKey("memberFirst")) ?? ""
        memberSecond = try c.decodeIfPresent(String.self, forKey: LedgerKey("memberSecond")) ?? ""
        firstRank = try c.decodeIfPresent(String.self, forKey: LedgerKey("firstRank"))
        firstPosition = try c.decodeIfPresent(String.self, forKey: LedgerKey("firstPosition"))
        firstDate = try c.decodeIfPresent(String.self, forKey: LedgerKey("firstDate"))
        secondRank = try c.decodeIfPresent(String.self, forKey: LedgerKey("secondRank"))
        secondPosition = try c.decodeIfPresent(String.self, forKey: LedgerKey("secondPosition"))
        secondDate = try c.decodeIfPresent(String.self, forKey: LedgerKey("secondDate"))
        signatureDefaultsInitialized = try c.decodeIfPresent(Bool.self, forKey: LedgerKey("signatureDefaultsInitialized"))
        acceptedContractMissing = try c.decodeIfPresent(Int.self, forKey: LedgerKey("acceptedContractMissing"))
        acceptedContractAmount = try c.decodeIfPresent(Int64.self, forKey: LedgerKey("acceptedContractAmount"))
        movements = try c.decodeIfPresent([BankReconciliation.Movement].self, forKey: LedgerKey("movements")) ?? []
        confirmedAt = try c.decodeIfPresent(Date.self, forKey: LedgerKey("confirmedAt"))
        var unknown: [String: LedgerJSON] = [:]
        for key in c.allKeys where !Self.known.contains(key.stringValue) { unknown[key.stringValue] = try c.decode(LedgerJSON.self, forKey: key) }
        extra = unknown.isEmpty ? nil : unknown
    }
    nonisolated func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: LedgerKey.self)
        for (key, value) in extra ?? [:] where !Self.known.contains(key) { try c.encode(value, forKey: LedgerKey(key)) }
        try c.encode(id, forKey: LedgerKey("id")); try c.encode(month, forKey: LedgerKey("month"))
        try c.encode(start, forKey: LedgerKey("start")); try c.encode(end, forKey: LedgerKey("end"))
        try c.encode(openingBalance, forKey: LedgerKey("openingBalance"))
        try c.encode(memberFirst, forKey: LedgerKey("memberFirst")); try c.encode(memberSecond, forKey: LedgerKey("memberSecond"))
        try c.encodeIfPresent(firstRank, forKey: LedgerKey("firstRank")); try c.encodeIfPresent(firstPosition, forKey: LedgerKey("firstPosition"))
        try c.encodeIfPresent(firstDate, forKey: LedgerKey("firstDate")); try c.encodeIfPresent(secondRank, forKey: LedgerKey("secondRank"))
        try c.encodeIfPresent(secondPosition, forKey: LedgerKey("secondPosition")); try c.encodeIfPresent(secondDate, forKey: LedgerKey("secondDate"))
        try c.encodeIfPresent(signatureDefaultsInitialized, forKey: LedgerKey("signatureDefaultsInitialized"))
        try c.encodeIfPresent(acceptedContractMissing, forKey: LedgerKey("acceptedContractMissing"))
        try c.encodeIfPresent(acceptedContractAmount, forKey: LedgerKey("acceptedContractAmount"))
        try c.encode(movements, forKey: LedgerKey("movements"))
        try c.encodeIfPresent(confirmedAt, forKey: LedgerKey("confirmedAt"))
    }
}
