import Foundation

/// دفتر مطابقة مستقل؛ الحركات لا تدّعي حصولها في المصرف قبل تأشير المستخدم.
nonisolated enum BankReconciliation {
    static let settingsKey = "bankReconciliations"
    static let maximumAmount: Int64 = 9_007_199_254_740_991

    enum Failure: LocalizedError {
        case invalid, duplicated, outOfPeriod, refundMismatch, overflow, incompatibleVersion
        var errorDescription: String? {
            switch self {
            case .invalid: tr("تحقق من الفترة والمبلغ والبيان قبل الحفظ.", "Check the period, amount and description before saving.")
            case .duplicated: tr("هذه الحركة المقترحة أُضيفت مسبقاً إلى الفترة.", "This suggested movement was already added to the period.")
            case .outOfPeriod: tr("تاريخ الحركة خارج فترة المطابقة.", "The movement date is outside the reconciliation period.")
            case .refundMismatch: tr("راجع ربط المبالغ المسترجعة: يجب أن يكون الصرف مرتبطاً بوارد وأن لا يتجاوزه.", "Check refund links: repayments must reference a credit and cannot exceed it.")
            case .overflow: tr("مجموع الحركات يتجاوز الحد المدعوم؛ لم يُحفظ التغيير.", "The movement total exceeds the supported limit; the change was not saved.")
            case .incompatibleVersion: tr("تعذّر قراءة بيانات المطابقة المحفوظة؛ لم تُستبدل البيانات.", "Could not read saved reconciliation data; it was not replaced.")
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
    }
    static func preparedSignatures(_ input: Period) -> Period {
        var value = input
        if value.signatureDefaultsInitialized == nil {
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
        guard !period.month.isEmpty, day(period.start) <= day(period.end),
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
        return result
    }
}
