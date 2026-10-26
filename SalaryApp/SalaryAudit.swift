import Foundation
import SwiftData

// MARK: - النتائج

/// حالات المقارنة بين شهرين في الرواتب.
nonisolated enum SalaryChangeKind: String, CaseIterable, Identifiable, Sendable {
    case added, removed, net, rank, workplace, bank
    var id: String { rawValue }
    var title: String {
        switch self {
        case .added: tr("جدد", "New")
        case .removed: tr("خارجون", "Removed")
        case .net: tr("تغيّر الصافي", "Net changed")
        case .rank: tr("ترفيع", "Rank changed")
        case .workplace: tr("نقل", "Transferred")
        case .bank: tr("تغيّر الحساب", "Bank account changed")
        }
    }
    /// عنوان الكشف المصدَّر.
    var reportTitle: String {
        switch self {
        case .added: tr("الموظفون الجدد", "New employees")
        case .removed: tr("الخارجون من الرواتب", "Removed from payroll")
        case .net: tr("تغيّر صافي الراتب", "Net salary changes")
        case .rank: tr("تغيّر الرتبة", "Rank changes")
        case .workplace: tr("تغيّر محل العمل", "Workplace changes")
        case .bank: tr("تغيّر الحساب المصرفي", "Bank account changes")
        }
    }
}

/// موظف تغيّر بين الشهرين.
nonisolated struct SalaryChange: Identifiable, Sendable, Hashable {
    let id: Int
    let key: String
    let name: String
    let rank: String
    let workplace: String
    /// الصافي في الشهر الأول (nil: غير موجود فيه).
    let before: Double?
    let after: Double?
    /// السبب أو التفاصيل: "الاسمي +20,000"، "نقيب ← رائد"...
    let reasons: [String]
    var difference: Double { (after ?? 0) - (before ?? 0) }
    var line: String { [rank, workplace].filter { !$0.isEmpty }.joined(separator: " · ") }
}

/// بند من بنود الرواتب: مجموعه في الشهرين، ومن تغيّر عندهم.
nonisolated struct ItemDelta: Identifiable, Sendable, Hashable {
    enum Kind: Int, Sendable { case allowance, entitlement, deduction, deductionTotal, net }
    let id: Int
    let title: String
    let group: Kind
    let before: Double
    let after: Double
    let people: [SalaryChange]
    var difference: Double { after - before }
    var isTotal: Bool { group == .entitlement || group == .deductionTotal || group == .net }
}

nonisolated struct SalaryAudit: Sendable {
    let from: String
    let to: String
    /// المقارنة متاحة (شهران مختلفان فيهما رواتب).
    let compared: Bool
    let netFrom: Double
    let netTo: Double
    let changes: [SalaryChangeKind: [SalaryChange]]
    let items: [ItemDelta]
    /// فحوص شهر "إلى".
    let checks: [AuditCheck]
    /// مطابقة الملاك (فارغة إذا لم يُستورد الملاك).
    let staffChecks: [AuditCheck]
    var issues: Int { (checks + staffChecks).filter { !$0.ok }.count }
    func rows(_ kind: SalaryChangeKind) -> [SalaryChange] { changes[kind] ?? [] }
}

// MARK: - البنود المعروفة

nonisolated enum SalaryItems {
    /// المخصصات (من أعمدة ورقة الرواتب) بترتيب الكشف.
    static let allowances = ["الاسمي", "الخطورة", "الارزاق", "الاستثنائية", "الاطفال", "الزوجية", "الشهادة", "منصب",
                             "مخصصات صنف", "جامعية", "مخصصات نقل", "مؤقتة", "اضافية", "مخصصات اخرى", "لقب علمي"]
    static let entitlement = "الاستحقاق"
    static let deductionTotal = "مجموع الاستقطاعات"
    /// الاستقطاعات الثابتة في ورقة الرواتب بترتيب الكشف؛ ما عداها (أسماء جهات) يُجمع في "استقطاعات أخرى".
    static let deductions = ["توقيفات تقاعدية", "الضريبة", "حماية الاجتماعية", "الرعاية الصحية", "صندوق الشهداء",
                             "نشاط صحي", "نشاط رياضي", "تامين صحي", "تضمين", "ايراد نهائي", "رسم طابع",
                             "مجموع استقطاعات المصارف", "استقطاع مؤقت"]
    static let otherDeductions = "استقطاعات أخرى"

    private static let knownDeductions = Dictionary(deductions.map { (ArabicText.normalize($0), $0) }, uniquingKeysWith: { a, _ in a })

    /// اسم البند الثابت المطابق لعنوان عمود (أو nil لأسماء الجهات).
    static func deduction(_ title: String) -> String? {
        let key = ArabicText.normalize(title)
        if let exact = knownDeductions[key] { return exact }
        return deductions.first { key.contains(ArabicText.normalize($0)) }
    }
}

// MARK: - الحساب

extension ReportEngine {
    private struct Snapshot {
        let name: String
        let key: String
        let rank: String
        let workplace: String
        let net: Double
        /// المخصصات والاستحقاق والاستقطاعات (بأسماء أعمدتها) لهذا الموظف.
        let values: [String: Double]
        let iban: String
        let bank: String
        let jobNumber: String
        let unified: String
        var line: String { [rank, workplace].filter { !$0.isEmpty }.joined(separator: " · ") }
    }

    private func snapshots(_ month: String) throws -> [Snapshot] {
        var e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        e.propertiesToFetch = [\.name, \.matchKey, \.netSalary, \.itemsData, \.detailsData, \.rank, \.workplace]
        return try modelContext.fetch(e).enumerated().map { i, emp in
            if i.isMultiple(of: 512) { try Task.checkCancellation() }
            let details = emp.details
            var values = PayrollItems.decode(emp.itemsData)
            for key in SalaryItems.allowances + [SalaryItems.entitlement] {
                if let v = details[key].flatMap(ArabicText.number(from:)), v != 0 { values[key] = v }
            }
            func text(_ key: String) -> String { (details[key] ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }
            return Snapshot(name: emp.name, key: emp.matchKey, rank: emp.rank, workplace: emp.workplace,
                            net: emp.netSalary ?? 0, values: values,
                            iban: SalaryAuditRules.iban(text("IBAN")), bank: text("توطين المصرف"),
                            jobNumber: SalaryAuditRules.number(text("الرقم الوظيفي")),
                            unified: SalaryAuditRules.number(text("الرقم الموحد")))
        }
    }

    /// تدقيق الرواتب: مقارنة شهر "من" مع "إلى"، وفحوص شهر "إلى"، ومطابقة الملاك.
    func salaryAudit(from: String, to: String) throws -> SalaryAudit {
        let new = try snapshots(to)
        let compared = !from.isEmpty && from != to
        var old: [Snapshot] = []
        if compared { old = try snapshots(from) }
        var oldMap: [String: Snapshot] = [:], newMap: [String: Snapshot] = [:]
        for s in old where oldMap[s.key] == nil { oldMap[s.key] = s }
        for s in new where newMap[s.key] == nil { newMap[s.key] = s }

        var changes: [SalaryChangeKind: [SalaryChange]] = [:]
        var itemPeople: [String: [SalaryChange]] = [:]
        var id = 0
        func change(_ s: Snapshot, before: Double?, after: Double?, reasons: [String]) -> SalaryChange {
            id += 1
            return SalaryChange(id: id, key: s.key, name: s.name, rank: s.rank, workplace: s.workplace,
                                before: before, after: after, reasons: reasons)
        }
        func signed(_ v: Double) -> String { (v > 0 ? "+" : v < 0 ? "−" : "") + Money.format(abs(v)) }
        // تصنيف عناوين البنود مرة واحدة لكل عنوان (توحيد النص العربي مكلف إذا تكرر لكل موظف).
        let allowanceSet = Set(SalaryItems.allowances)
        var classes: [String: (item: String, kind: ItemDelta.Kind)] = [:]
        func classify(_ title: String) -> (item: String, kind: ItemDelta.Kind) {
            if let known = classes[title] { return known }
            let result: (item: String, kind: ItemDelta.Kind)
            if allowanceSet.contains(title) { result = (title, .allowance) }
            else if title == SalaryItems.entitlement { result = (title, .entitlement) }
            else if title == SalaryItems.deductionTotal { result = (title, .deductionTotal) }
            else { result = (SalaryItems.deduction(title) ?? SalaryItems.otherDeductions, .deduction) }
            classes[title] = result
            return result
        }

        if compared && !old.isEmpty {
            for s in newMap.values where oldMap[s.key] == nil {
                changes[.added, default: []].append(change(s, before: nil, after: s.net, reasons: []))
            }
            for s in oldMap.values where newMap[s.key] == nil {
                changes[.removed, default: []].append(change(s, before: s.net, after: nil, reasons: []))
            }
            for (key, n) in newMap {
                guard let o = oldMap[key] else { continue }
                if key.hashValue & 0x3FF == 0 { try Task.checkCancellation() }
                // البنود التي تغيّرت (المجاميع لا تُذكر سبباً).
                var deltas: [(title: String, diff: Double)] = []
                for title in Set(o.values.keys).union(n.values.keys) {
                    let a = o.values[title] ?? 0, b = n.values[title] ?? 0
                    guard abs(b - a) >= 1 else { continue }
                    let itemTitle = classify(title).item
                    itemPeople[itemTitle, default: []].append(change(n, before: a, after: b, reasons: itemTitle == title ? [] : [title]))
                    if title != SalaryItems.entitlement && title != SalaryItems.deductionTotal { deltas.append((title, b - a)) }
                }
                if abs(n.net - o.net) >= 1 {
                    let reasons = deltas.sorted { abs($0.diff) > abs($1.diff) }.prefix(4).map { "\($0.title) \(signed($0.diff))" }
                    changes[.net, default: []].append(change(n, before: o.net, after: n.net, reasons: reasons))
                }
                if !o.rank.isEmpty, !n.rank.isEmpty, o.rank != n.rank, SalaryAuditRules.rank(o.rank) != SalaryAuditRules.rank(n.rank) {
                    changes[.rank, default: []].append(change(n, before: o.net, after: n.net, reasons: ["\(o.rank) ← \(n.rank)"]))
                }
                if !o.workplace.isEmpty, !n.workplace.isEmpty, o.workplace != n.workplace,
                   ArabicText.matchKey(o.workplace) != ArabicText.matchKey(n.workplace) {
                    changes[.workplace, default: []].append(change(n, before: o.net, after: n.net, reasons: ["\(o.workplace) ← \(n.workplace)"]))
                }
                let ibanChanged = !o.iban.isEmpty && !n.iban.isEmpty && o.iban != n.iban
                let bankChanged = !o.bank.isEmpty && !n.bank.isEmpty && o.bank != n.bank && ArabicText.matchKey(o.bank) != ArabicText.matchKey(n.bank)
                if ibanChanged || bankChanged || (o.iban.isEmpty != n.iban.isEmpty) {
                    var reasons: [String] = []
                    if bankChanged { reasons.append("\(o.bank) ← \(n.bank)") }
                    reasons.append("IBAN " + SalaryAuditRules.tail(o.iban) + " ← " + SalaryAuditRules.tail(n.iban))
                    changes[.bank, default: []].append(change(n, before: o.net, after: n.net, reasons: reasons))
                }
            }
            for kind in SalaryChangeKind.allCases {
                changes[kind]?.sort {
                    kind == .net ? abs($0.difference) > abs($1.difference) : $0.name < $1.name
                }
            }
        }

        // مجاميع البنود في الشهرين (مرور واحد على الموظفين).
        func sums(_ list: [Snapshot]) -> [String: Double] {
            var result: [String: Double] = [:]
            for s in list { for (k, v) in s.values { result[classify(k).item, default: 0] += v } }
            return result
        }
        let before = sums(old), after = sums(new)
        try Task.checkCancellation()
        var items: [ItemDelta] = []
        func add(_ title: String, _ group: ItemDelta.Kind) {
            let a = before[title] ?? 0, b = after[title] ?? 0
            guard a != 0 || b != 0 else { return }
            let people = (itemPeople[title] ?? []).sorted { abs($0.difference) > abs($1.difference) }
            items.append(ItemDelta(id: items.count, title: title, group: group, before: a, after: b, people: people))
        }
        for title in SalaryItems.allowances { add(title, .allowance) }
        add(SalaryItems.entitlement, .entitlement)
        for title in SalaryItems.deductions { add(title, .deduction) }
        add(SalaryItems.otherDeductions, .deduction)
        add(SalaryItems.deductionTotal, .deductionTotal)
        let netPeople = (changes[.net] ?? [])
        items.append(ItemDelta(id: items.count, title: tr("الصافي", "Net"), group: .net,
                               before: old.reduce(0) { $0 + $1.net }, after: new.reduce(0) { $0 + $1.net }, people: netPeople))

        let checks = try salaryChecks(new)
        let staff = try staffChecks(new)
        return SalaryAudit(from: from, to: to, compared: compared && !old.isEmpty,
                           netFrom: old.reduce(0) { $0 + $1.net }, netTo: new.reduce(0) { $0 + $1.net },
                           changes: changes, items: items, checks: checks, staffChecks: staff)
    }

    // MARK: فحوص الشهر

    private func salaryChecks(_ employees: [Snapshot]) throws -> [AuditCheck] {
        var checks: [AuditCheck] = []
        func money(_ v: Double) -> String { Money.format(v) }

        // ١. خطأ في الحساب: الصافي = الاستحقاق − مجموع الاستقطاعات، والاستحقاق = مجموع المخصصات.
        var netErrors: [AuditRow] = [], grossErrors: [AuditRow] = []
        var netChecked = 0, grossChecked = 0
        // بلا عمود "مجموع الاستقطاعات" لا يُفحص الصافي.
        let hasDeductionTotal = employees.contains { $0.values[SalaryItems.deductionTotal] != nil }
        for (i, s) in employees.enumerated() {
            guard let gross = s.values[SalaryItems.entitlement] else { continue }
            if hasDeductionTotal {
                netChecked += 1
                let deductions = s.values[SalaryItems.deductionTotal] ?? 0
                let expected = gross - deductions
                if abs(expected - s.net) >= 1 {
                    netErrors.append(AuditRow(id: i, name: s.name,
                                              detail: tr("الاستحقاق \(money(gross)) − الاستقطاعات \(money(deductions)) = \(money(expected)) · الصافي \(money(s.net))",
                                                         "Gross \(money(gross)) − deductions \(money(deductions)) = \(money(expected)) · net \(money(s.net))"),
                                              amount: s.net - expected))
                }
            }
            if s.values["الاسمي"] != nil {
                grossChecked += 1
                let sum = SalaryItems.allowances.reduce(0) { $0 + (s.values[$1] ?? 0) }
                if abs(sum - gross) >= 1 {
                    grossErrors.append(AuditRow(id: 100_000 + i, name: s.name,
                                                detail: tr("مجموع المخصصات \(money(sum)) · الاستحقاق \(money(gross))",
                                                           "Allowances \(money(sum)) · gross \(money(gross))"),
                                                amount: gross - sum))
                }
            }
        }
        // ملف فيه أعمدة مخصصات غير معروفة: لا يُعتمد فحص الاستحقاق حتى لا تظهر آلاف الملاحظات الخاطئة.
        let netReliable = netChecked > 0 && Double(netErrors.count) / Double(netChecked) < 0.3
        let grossReliable = grossChecked > 0 && Double(grossErrors.count) / Double(grossChecked) < 0.3
        let calcRows = (netReliable ? netErrors : []) + (grossReliable ? grossErrors : [])
        checks.append(AuditCheck(id: "calc", title: tr("خطأ في الحساب", "Calculation errors"),
                                 subtitle: calcRows.isEmpty
                                    ? tr("الاستحقاق والصافي يطابقان البنود", "Gross and net match the items")
                                    : tr("الاستحقاق أو الصافي لا يطابق البنود", "Gross or net doesn't match the items"),
                                 ok: calcRows.isEmpty, rows: calcRows, amountTitle: "الفرق"))

        // ٢. IBAN: مكرر، فارغ، أو طول/بداية غير صحيحة.
        var ibanRows: [AuditRow] = []
        let withIban = employees.filter { !$0.iban.isEmpty }
        let byIban = Dictionary(grouping: withIban, by: \.iban).filter { Set($0.value.map(\.key)).count > 1 }
        for (iban, list) in byIban.sorted(by: { $0.key < $1.key }) {
            for s in list {
                let others = list.filter { $0.key != s.key }.map(\.name).joined(separator: "، ")
                ibanRows.append(AuditRow(id: ibanRows.count, name: s.name,
                                         detail: tr("مكرر \(SalaryAuditRules.tail(iban)) مع: \(others)", "Shared \(SalaryAuditRules.tail(iban)) with: \(others)"),
                                         amount: s.net))
            }
        }
        for s in withIban where !SalaryAuditRules.validIban(s.iban) {
            ibanRows.append(AuditRow(id: ibanRows.count, name: s.name,
                                     detail: tr("غير صحيح: \(s.iban.count) خانة · \(String(s.iban.prefix(4)))…", "Invalid: \(s.iban.count) chars"),
                                     amount: s.net))
        }
        // الفارغ يُفحص فقط إذا كان العمود موجوداً لأغلب الموظفين.
        if !employees.isEmpty, Double(withIban.count) / Double(employees.count) >= 0.5 {
            for s in employees where s.iban.isEmpty {
                ibanRows.append(AuditRow(id: ibanRows.count, name: s.name, detail: tr("IBAN فارغ", "Empty IBAN") + (s.line.isEmpty ? "" : " · " + s.line), amount: s.net))
            }
        }
        checks.append(AuditCheck(id: "iban", title: "IBAN",
                                 subtitle: withIban.isEmpty ? tr("لا يوجد عمود IBAN في ملف الرواتب", "No IBAN column")
                                    : ibanRows.isEmpty ? tr("لا تكرار ولا أرقام ناقصة", "No duplicates or invalid numbers")
                                    : tr("مكرر · فارغ · طول غير صحيح", "Duplicate · empty · invalid length"),
                                 ok: ibanRows.isEmpty, rows: ibanRows, amountTitle: "الصافي"))

        // ٣. أسماء مكررة.
        let groups = Dictionary(grouping: employees, by: \.key).filter { $0.value.count > 1 }
        let dupes = groups.values.enumerated().map { i, list in
            AuditRow(id: i, name: list[0].name,
                     detail: tr("مكرر \(Num.format(list.count)) مرات", "\(list.count) times") + " · " + list.map(\.workplace).filter { !$0.isEmpty }.joined(separator: "، "),
                     amount: list.reduce(0) { $0 + $1.net })
        }.sorted { $0.name < $1.name }
        checks.append(AuditCheck(id: "duplicates", title: tr("أسماء مكررة", "Duplicate names"),
                                 subtitle: dupes.isEmpty ? tr("لا يوجد اسم مكرر", "No duplicates") : tr("نفس الاسم أكثر من مرة في نفس الشهر", "Same name more than once"),
                                 ok: dupes.isEmpty, rows: dupes, amountTitle: "مجموع الصافي"))

        // ٤. الرقم الوظيفي / الموحد لأكثر من موظف.
        var idRows: [AuditRow] = []
        for (label, path) in [(tr("الرقم الوظيفي", "Job number"), \Snapshot.jobNumber), (tr("الرقم الموحد", "Unified number"), \Snapshot.unified)] {
            let shared = Dictionary(grouping: employees.filter { !$0[keyPath: path].isEmpty }, by: { $0[keyPath: path] })
                .filter { Set($0.value.map(\.key)).count > 1 }
            for (number, list) in shared.sorted(by: { $0.key < $1.key }) {
                for s in list {
                    let others = list.filter { $0.key != s.key }.map(\.name).joined(separator: "، ")
                    idRows.append(AuditRow(id: idRows.count, name: s.name, detail: "\(label) \(number) · " + tr("مع: ", "with: ") + others, amount: s.net))
                }
            }
        }
        checks.append(AuditCheck(id: "ids", title: tr("الرقم الوظيفي / الموحد", "Job / unified number"),
                                 subtitle: idRows.isEmpty ? tr("لا يوجد تكرار", "No duplicates") : tr("نفس الرقم لأكثر من موظف", "Same number for different employees"),
                                 ok: idRows.isEmpty, rows: idRows, amountTitle: "الصافي"))

        // ٥. الرعاية الصحية: مبالغ لا تقبل القسمة على 3,500.
        var odd: [AuditRow] = []
        var healthKeys: [String: Bool] = [:]
        func isHealth(_ title: String) -> Bool {
            if let known = healthKeys[title] { return known }
            let value = HealthCareReport.applies(to: title)
            healthKeys[title] = value
            return value
        }
        for (i, s) in employees.enumerated() {
            for (title, amount) in s.values where amount != 0 && isHealth(title) && !HealthCareReport.isExact(amount) {
                odd.append(AuditRow(id: i, name: s.name, detail: tr("\(money(amount)) ÷ 3,500 = \(HealthCareReport.familyText(amount))", "\(money(amount)) ÷ 3,500"), amount: amount))
            }
        }
        checks.append(AuditCheck(id: "health", title: tr("الرعاية الصحية", "Health care"),
                                 subtitle: odd.isEmpty ? tr("كل المبالغ تقبل القسمة على 3,500", "All amounts divisible by 3,500") : tr("مبالغ لا تقبل القسمة على 3,500", "Amounts not divisible by 3,500"),
                                 ok: odd.isEmpty, rows: odd))
        return checks
    }

    // MARK: مطابقة الملاك

    private func staffChecks(_ employees: [Snapshot]) throws -> [AuditCheck] {
        var d = FetchDescriptor<StaffMember>()
        d.propertiesToFetch = [\.name, \.nameKey, \.unifiedNumber, \.rank, \.status, \.workplace]
        let staff = try modelContext.fetch(d)
        guard !staff.isEmpty, !employees.isEmpty else { return [] }
        try Task.checkCancellation()

        var byUnified: [String: Int] = [:], byName: [String: Int] = [:]
        for (i, m) in staff.enumerated() {
            let u = SalaryAuditRules.number(m.unifiedNumber)
            if !u.isEmpty, byUnified[u] == nil { byUnified[u] = i }
            let k = m.nameKey.replacingOccurrences(of: " ", with: "")
            if byName[k] == nil { byName[k] = i }
        }
        var matched = Set<Int>()
        var ranks: [String: String] = [:]
        func rank(_ text: String) -> String {
            if let known = ranks[text] { return known }
            let value = SalaryAuditRules.rank(text)
            ranks[text] = value
            return value
        }
        var noStaff: [AuditRow] = [], rankRows: [AuditRow] = []
        for (i, s) in employees.enumerated() {
            guard let index = (s.unified.isEmpty ? nil : byUnified[s.unified]) ?? byName[s.key] else {
                noStaff.append(AuditRow(id: i, name: s.name, detail: s.line, amount: s.net))
                continue
            }
            matched.insert(index)
            let m = staff[index]
            if !s.rank.isEmpty, !m.rank.isEmpty, s.rank != m.rank, rank(s.rank) != rank(m.rank) {
                rankRows.append(AuditRow(id: i, name: s.name,
                                         detail: tr("الرواتب: \(s.rank) · الملاك: \(m.rank)", "Payroll: \(s.rank) · staff: \(m.rank)"),
                                         amount: s.net))
            }
        }
        var noSalary: [AuditRow] = []
        var statuses: [String: Bool] = [:]
        for (i, m) in staff.enumerated() where !matched.contains(i) {
            let active = statuses[m.status] ?? SalaryAuditRules.active(m.status)
            statuses[m.status] = active
            guard active else { continue }
            noSalary.append(AuditRow(id: i, name: m.name,
                                     detail: [m.rank, m.status, m.workplace].filter { !$0.isEmpty }.joined(separator: " · "), amount: 0))
        }
        noStaff.sort { $0.name < $1.name }
        noSalary.sort { $0.name < $1.name }
        rankRows.sort { $0.name < $1.name }
        return [
            AuditCheck(id: "staffNoSalary", title: tr("في الملاك بلا راتب", "In staff without salary"),
                       subtitle: noSalary.isEmpty ? tr("كل المستمرين في الملاك لهم راتب", "Every active staff member has a salary")
                                                  : tr("مستمرون في الملاك وليس لهم راتب هذا الشهر", "Active staff without salary this month"),
                       ok: noSalary.isEmpty, rows: noSalary, amountTitle: " ", detailTitle: "الرتبة · الحالة · محل العمل"),
            AuditCheck(id: "salaryNoStaff", title: tr("لهم راتب وليسوا في الملاك", "Salary without staff record"),
                       subtitle: noStaff.isEmpty ? tr("كل من له راتب موجود في الملاك", "Everyone paid is in staff")
                                                 : tr("بالرقم الموحد أو الاسم", "By unified number or name"),
                       ok: noStaff.isEmpty, rows: noStaff, amountTitle: "الصافي"),
            AuditCheck(id: "staffRank", title: tr("الرتبة مختلفة عن الملاك", "Rank differs from staff"),
                       subtitle: rankRows.isEmpty ? tr("الرتب متطابقة", "Ranks match") : tr("رتبة الرواتب غير رتبة الملاك", "Payroll rank differs from staff"),
                       ok: rankRows.isEmpty, rows: rankRows, amountTitle: "الصافي"),
        ]
    }
}

// MARK: - قواعد المقارنة

nonisolated enum SalaryAuditRules {
    /// IBAN بلا مسافات وبأحرف كبيرة وأرقام لاتينية.
    static func iban(_ text: String) -> String {
        ArabicText.latinDigits(text).uppercased().filter { $0.isLetter || $0.isNumber }
    }

    /// IBAN العراقي: 23 خانة تبدأ بـ IQ.
    static func validIban(_ iban: String) -> Bool { iban.count == 23 && iban.hasPrefix("IQ") }

    /// آخر أربع خانات فقط في العرض.
    static func tail(_ iban: String) -> String { iban.isEmpty ? tr("فارغ", "empty") : "…" + iban.suffix(4) }

    static func number(_ text: String) -> String {
        let digits = ArabicText.latinDigits(text).filter(\.isNumber)
        let trimmed = digits.drop { $0 == "0" }
        return trimmed.isEmpty ? "" : String(trimmed)
    }

    /// "ملازم أول" = "ملازم اول" = "ملازم1"، "مفوض خامس" = "مفوض5".
    static func rank(_ text: String) -> String {
        var r = ArabicText.normalize(ArabicText.latinDigits(text))
        for (word, digit) in [("الاول", "1"), ("اول", "1"), ("الثاني", "2"), ("ثاني", "2"), ("الثالث", "3"), ("ثالث", "3"),
                              ("الرابع", "4"), ("رابع", "4"), ("الخامس", "5"), ("خامس", "5")] {
            r = r.replacingOccurrences(of: word, with: digit)
        }
        return r.filter { !$0.isWhitespace && $0 != "." && $0 != "/" && $0 != "-" }
    }

    /// الحالات التي لا يُنتظر لها راتب (متقاعد، متوفى، مفصول...).
    private static let inactive = ["تقاعد", "احاله", "وفاه", "متوفي", "شهيد", "استشهد", "فصل", "مفصول", "استقاله", "مستقيل",
                                   "ترقين", "طي قيد", "منقول", "نقل خارج", "انهاء خدمه", "منتهي"]
    static func active(_ status: String) -> Bool {
        let s = ArabicText.normalize(status)
        return !inactive.contains { s.contains($0) }
    }
}

// MARK: - الحفظ المؤقت

/// نتائج التدقيق الأخيرة: صفحة التدقيق وبطاقاتها وصفحاتها الفرعية تشترك فيها بدل إعادة الحساب.
actor AuditResults {
    static let shared = AuditResults()
    private var salaryTasks: [String: Task<SalaryAudit, Error>] = [:]
    private var deductionTasks: [String: Task<DeductionComparison, Error>] = [:]
    private var checkTasks: [String: Task<[AuditCheck], Error>] = [:]

    static func revision(_ months: [String], container: ModelContainer) -> String {
        let context = ModelContext(container)
        let parts = months.map { month -> String in
            let d = FetchDescriptor<PayrollMonth>(predicate: #Predicate { $0.name == month })
            let stamp = (try? context.fetch(d).first?.importedAt.timeIntervalSince1970) ?? 0
            return "\(month):\(stamp)"
        }
        let staff = StaffInfo.importedAt?.timeIntervalSince1970 ?? 0
        return parts.joined(separator: "|") + "|staff:\(staff)|\(AppSettings.locale.identifier)"
    }

    func salary(from: String, to: String, container: ModelContainer) async throws -> SalaryAudit {
        let key = Self.revision([from, to], container: container)
        if let task = salaryTasks[key] { return try await task.value }
        if salaryTasks.count >= 4 { salaryTasks.removeAll() }
        let task = Task.detached(priority: .userInitiated) {
            let started = Date()
            let result = try await ReportEngine(modelContainer: container).salaryAudit(from: from, to: to)
            PerfLog.record("salaryAudit", since: started)
            return result
        }
        salaryTasks[key] = task
        do { return try await task.value } catch { salaryTasks[key] = nil; throw error }
    }

    func deductions(from: String, to: String, container: ModelContainer) async throws -> DeductionComparison {
        let key = Self.revision([from, to], container: container)
        if let task = deductionTasks[key] { return try await task.value }
        if deductionTasks.count >= 4 { deductionTasks.removeAll() }
        let task = Task.detached(priority: .userInitiated) {
            try await ReportEngine(modelContainer: container).deductionComparison(from: from, to: to)
        }
        deductionTasks[key] = task
        do { return try await task.value } catch { deductionTasks[key] = nil; throw error }
    }

    func deductionChecks(month: String, container: ModelContainer) async throws -> [AuditCheck] {
        let key = Self.revision([month], container: container)
        if let task = checkTasks[key] { return try await task.value }
        if checkTasks.count >= 4 { checkTasks.removeAll() }
        let task = Task.detached(priority: .userInitiated) {
            try await ReportEngine(modelContainer: container).deductionChecks(month: month)
        }
        checkTasks[key] = task
        do { return try await task.value } catch { checkTasks[key] = nil; throw error }
    }

    /// عدد الملاحظات الجاهز فقط (لبطاقة التقارير، بلا حساب جديد).
    func cachedIssues(month: String, previous: String, container: ModelContainer) async -> Int? {
        let salaryKey = Self.revision([previous, month], container: container)
        let checksKey = Self.revision([month], container: container)
        guard let s = salaryTasks[salaryKey], let c = checkTasks[checksKey],
              let a = try? await s.value, let b = try? await c.value else { return nil }
        return a.issues + b.filter { !$0.ok }.count
    }

    func invalidate() {
        salaryTasks.values.forEach { $0.cancel() }
        deductionTasks.values.forEach { $0.cancel() }
        checkTasks.values.forEach { $0.cancel() }
        salaryTasks.removeAll(); deductionTasks.removeAll(); checkTasks.removeAll()
    }
}
