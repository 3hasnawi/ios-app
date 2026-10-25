import SwiftUI
import SwiftData

// MARK: - النتائج

nonisolated struct AuditRow: Sendable, Identifiable, Hashable {
    let id: Int
    let name: String
    let detail: String
    let amount: Double
}

nonisolated struct AuditCheck: Sendable, Identifiable, Hashable {
    let id: String
    let title: String
    let subtitle: String
    let ok: Bool
    let rows: [AuditRow]
    /// عنوان عمود المبلغ عند التصدير.
    var amountTitle = "المبلغ"
    var detailTitle = "التفاصيل"
}

nonisolated struct AuditReport: Sendable, Hashable {
    let month: String
    let employees: Int
    let deductions: Int
    let checks: [AuditCheck]
    var issues: Int { checks.filter { !$0.ok }.count }
}

/// أساس نسبة الاستقطاعات: صافي الراتب أو الاستحقاق الكلي.
nonisolated enum AuditBase: String, CaseIterable, Identifiable, Sendable {
    case net, gross
    var id: String { rawValue }
    var title: String {
        switch self {
        case .net: tr("من صافي الراتب", "Of net salary")
        case .gross: tr("من الاستحقاق الكلي", "Of total entitlement")
        }
    }
}

nonisolated enum AuditSettings {
    static let thresholdKey = "auditThreshold"
    static let baseKey = "auditBase"
    static var threshold: Int { let v = UserDefaults.standard.integer(forKey: thresholdKey); return v == 0 ? 50 : v }
    static var base: AuditBase { AuditBase(rawValue: UserDefaults.standard.string(forKey: baseKey) ?? "") ?? .net }
}

nonisolated struct MonthComparison: Sendable, Hashable {
    let from: String
    let to: String
    let netFrom: Double
    let netTo: Double
    let added: [AuditRow]
    let removed: [AuditRow]
    let changed: [AuditRow]
    let entities: [AuditRow]
}

// MARK: - الحساب

extension ReportEngine {
    func audit(month: String, threshold: Int, base: AuditBase) throws -> AuditReport {
        var e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        e.propertiesToFetch = [\.name, \.matchKey, \.netSalary, \.itemsData, \.detailsData, \.rank, \.workplace]
        let employees = try modelContext.fetch(e)
        var d = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
        d.propertiesToFetch = [\.employeeName, \.matchKey, \.title, \.amount]
        let deductions = try modelContext.fetch(d)

        func money(_ v: Double) -> String { Money.format(v) }
        var checks: [AuditCheck] = []
        let payKeys = Set(employees.map(\.matchKey))
        var dedByKey: [String: Double] = [:]
        for x in deductions { dedByKey[x.matchKey, default: 0] += x.amount ?? 0 }

        // ١. استقطاعات بلا راتب.
        if employees.isEmpty {
            checks.append(AuditCheck(id: "orphans", title: tr("استقطاعات بلا راتب", "Deductions without payroll"),
                                     subtitle: tr("لم يُستورد ملف رواتب هذا الشهر بعد", "No payroll file for this month yet"), ok: true, rows: []))
        } else {
            let orphans = deductions.enumerated().filter { !payKeys.contains($0.element.matchKey) }
                .map { AuditRow(id: $0.offset, name: $0.element.employeeName, detail: $0.element.title, amount: $0.element.amount ?? 0) }
                .sorted { $0.name < $1.name }
            checks.append(AuditCheck(id: "orphans", title: tr("استقطاعات بلا راتب", "Deductions without payroll"),
                                     subtitle: orphans.isEmpty ? tr("كل الأسماء موجودة في الرواتب", "Every name is in the payroll")
                                                               : tr("اسم في ملف الاستقطاعات غير موجود في الرواتب", "Name in deductions but not in payroll"),
                                     ok: orphans.isEmpty, rows: orphans, detailTitle: "جهة التنفيذ"))
        }

        // ٢. أسماء مكررة في الرواتب.
        let groups = Dictionary(grouping: employees, by: \.matchKey).filter { $0.value.count > 1 }
        let dupes = groups.values.enumerated().map { i, list in
            AuditRow(id: i, name: list[0].name,
                     detail: tr("مكرر \(Num.format(list.count)) مرات", "\(list.count) times") + " · " + list.map(\.workplace).filter { !$0.isEmpty }.joined(separator: "، "),
                     amount: list.reduce(0) { $0 + ($1.netSalary ?? 0) })
        }.sorted { $0.name < $1.name }
        checks.append(AuditCheck(id: "duplicates", title: tr("أسماء مكررة في الرواتب", "Duplicate names"),
                                 subtitle: dupes.isEmpty ? tr("لا يوجد اسم مكرر", "No duplicates") : tr("نفس الاسم أكثر من مرة في نفس الشهر", "Same name more than once"),
                                 ok: dupes.isEmpty, rows: dupes, amountTitle: "مجموع الصافي"))

        // ٣. استقطاعات أكبر من النسبة.
        let limit = Double(threshold) / 100
        var heavy: [(row: AuditRow, ratio: Double)] = []
        for (i, emp) in employees.enumerated() {
            let items = PayrollItems.decode(emp.itemsData)
            let total = items["مجموع الاستقطاعات"] ?? dedByKey[emp.matchKey] ?? 0
            guard total > 0 else { continue }
            let net = emp.netSalary ?? 0
            let gross = emp.details["الاستحقاق"].flatMap { ArabicText.number(from: $0) } ?? (net + total)
            let baseValue = base == .net ? net : gross
            guard baseValue > 0 else { continue }
            let ratio = total / baseValue
            guard ratio > limit else { continue }
            heavy.append((AuditRow(id: i, name: emp.name,
                                   detail: tr("\(Num.format(Int((ratio * 100).rounded())))% · استقطاعات \(money(total)) \(base == .net ? "من صافي" : "من استحقاق") \(money(baseValue))",
                                              "\(Int((ratio * 100).rounded()))% · \(money(total)) of \(money(baseValue))"),
                                   amount: total), ratio))
        }
        heavy.sort { $0.ratio > $1.ratio }
        checks.append(AuditCheck(id: "heavy", title: tr("استقطاعات أكبر من \(Num.format(threshold))%", "Deductions over \(threshold)%"),
                                 subtitle: base.title, ok: heavy.isEmpty, rows: heavy.map(\.row), amountTitle: "مجموع الاستقطاعات"))

        // ٤. الرعاية الصحية.
        var odd: [AuditRow] = []
        for (i, emp) in employees.enumerated() {
            for (title, amount) in PayrollItems.decode(emp.itemsData) where HealthCareReport.applies(to: title) && amount != 0 && !HealthCareReport.isExact(amount) {
                odd.append(AuditRow(id: i, name: emp.name, detail: tr("\(money(amount)) ÷ 3,500 = \(HealthCareReport.familyText(amount))", "\(money(amount)) ÷ 3,500"), amount: amount))
            }
        }
        checks.append(AuditCheck(id: "health", title: tr("الرعاية الصحية", "Health care"),
                                 subtitle: odd.isEmpty ? tr("كل المبالغ تقبل القسمة على 3,500", "All amounts divisible by 3,500") : tr("مبالغ لا تقبل القسمة على 3,500", "Amounts not divisible by 3,500"),
                                 ok: odd.isEmpty, rows: odd))

        // ٥. مطابقة المجاميع.
        let dedTotal = deductions.reduce(0) { $0 + ($1.amount ?? 0) }
        let bankTotal = employees.reduce(0) { sum, emp in sum + (PayrollItems.decode(emp.itemsData).first { $0.key.contains("استقطاعات المصارف") }?.value ?? 0) }
        if bankTotal == 0 || deductions.isEmpty {
            checks.append(AuditCheck(id: "totals", title: tr("مطابقة المجاميع", "Totals match"),
                                     subtitle: tr("يحتاج ملف رواتب فيه \"مجموع استقطاعات المصارف\" وملف استقطاعات لنفس الشهر", "Needs both files for this month"), ok: true, rows: []))
        } else {
            let diff = dedTotal - bankTotal
            checks.append(AuditCheck(id: "totals", title: tr("مطابقة المجاميع", "Totals match"),
                                     subtitle: abs(diff) < 1
                                        ? tr("مجموع الجهات = مجموع استقطاعات المصارف (\(money(dedTotal)))", "Entities = bank deductions (\(money(dedTotal)))")
                                        : tr("الجهات \(money(dedTotal)) · الرواتب \(money(bankTotal)) · الفرق \(money(diff))", "Entities \(money(dedTotal)) · payroll \(money(bankTotal))"),
                                     ok: abs(diff) < 1, rows: []))
        }

        // ٦. صافي صفر أو سالب.
        let zero = employees.enumerated().filter { ($0.element.netSalary ?? 0) <= 0 }
            .map { AuditRow(id: $0.offset, name: $0.element.name, detail: [$0.element.rank, $0.element.workplace].filter { !$0.isEmpty }.joined(separator: " · "), amount: $0.element.netSalary ?? 0) }
        checks.append(AuditCheck(id: "net", title: tr("صافي الراتب", "Net salary"),
                                 subtitle: zero.isEmpty ? tr("لا يوجد صافٍ صفر أو سالب", "No zero or negative net") : tr("صافٍ صفر أو سالب أو فارغ", "Zero, negative or empty net"),
                                 ok: zero.isEmpty, rows: zero, amountTitle: "الصافي"))

        // الملاحظات أولاً.
        let ordered = checks.filter { !$0.ok } + checks.filter(\.ok)
        return AuditReport(month: month, employees: employees.count, deductions: deductions.count, checks: ordered)
    }

    func compare(from a: String, to b: String) throws -> MonthComparison {
        func load(_ month: String) throws -> [String: (name: String, net: Double, line: String)] {
            var e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
            e.propertiesToFetch = [\.name, \.matchKey, \.netSalary, \.rank, \.workplace]
            var map: [String: (name: String, net: Double, line: String)] = [:]
            for emp in try modelContext.fetch(e) where map[emp.matchKey] == nil {
                map[emp.matchKey] = (emp.name, emp.netSalary ?? 0, [emp.rank, emp.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
            }
            return map
        }
        let old = try load(a), new = try load(b)
        let added = new.filter { old[$0.key] == nil }.enumerated()
            .map { AuditRow(id: $0.offset, name: $0.element.value.name, detail: $0.element.value.line, amount: $0.element.value.net) }
            .sorted { $0.name < $1.name }
        let removed = old.filter { new[$0.key] == nil }.enumerated()
            .map { AuditRow(id: $0.offset, name: $0.element.value.name, detail: $0.element.value.line, amount: $0.element.value.net) }
            .sorted { $0.name < $1.name }
        var changed: [AuditRow] = []
        for (i, (key, n)) in new.enumerated() {
            guard let o = old[key], abs(n.net - o.net) >= 1 else { continue }
            changed.append(AuditRow(id: i, name: n.name, detail: "\(Money.format(o.net)) ← \(Money.format(n.net))", amount: n.net - o.net))
        }
        changed.sort { abs($0.amount) > abs($1.amount) }

        let oldGroups = Dictionary(try deductionGroups(month: a, byEntity: false).map { ($0.title, $0.total) }, uniquingKeysWith: +)
        let newGroups = Dictionary(try deductionGroups(month: b, byEntity: false).map { ($0.title, $0.total) }, uniquingKeysWith: +)
        let titles = Set(oldGroups.keys).union(newGroups.keys)
        let entities = titles.enumerated().compactMap { i, t -> AuditRow? in
            let o = oldGroups[t] ?? 0, n = newGroups[t] ?? 0
            guard abs(n - o) >= 1 else { return nil }
            return AuditRow(id: i, name: t, detail: "\(Money.format(o)) ← \(Money.format(n))", amount: n - o)
        }.sorted { abs($0.amount) > abs($1.amount) }

        return MonthComparison(from: a, to: b, netFrom: old.values.reduce(0) { $0 + $1.net }, netTo: new.values.reduce(0) { $0 + $1.net },
                               added: added, removed: removed, changed: changed, entities: entities)
    }
}

// MARK: - التصدير

nonisolated enum AuditExport {
    static func sheet(title: String, rows: [AuditRow], detailTitle: String = "التفاصيل", amountTitle: String = "المبلغ") -> ReportSheet {
        let header = UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader
        return ReportSheet(
            header: header, title: title,
            columns: [
                .init(title: "ت", width: 5, kind: .serial),
                .init(title: "الاسم", width: 34, kind: .text),
                .init(title: detailTitle, width: 40, kind: .text),
                .init(title: amountTitle, width: 16, kind: .amount),
            ],
            rows: rows.enumerated().map { i, r in [String(i + 1), r.name, r.detail, ""] },
            amounts: rows.map(\.amount)
        ).fitted()
    }

    static func files(name: String, sheets: [(name: String, sheet: ReportSheet)]) async throws -> (excel: URL, pdf: URL) {
        let safe = name.replacingOccurrences(of: "/", with: "-")
        let names = BulkExport.sheetNames(sheets.map(\.name))
        let named = zip(names, sheets).map { ($0, $1.sheet) }
        return try await Task.detached(priority: .userInitiated) {
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("report-export", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let excel = folder.appendingPathComponent(safe + ".xlsx")
            let pdf = folder.appendingPathComponent(safe + ".pdf")
            try XLSXWriter.writeWorkbook(named, to: excel)
            try PDFReportWriter.write(named.map(\.1), to: pdf)
            try PDFDecor.apply(to: pdf)
            return (excel, pdf)
        }.value
    }

    static func report(_ r: AuditReport) async throws -> (excel: URL, pdf: URL) {
        let month = ReportSheet.easternDigits(r.month)
        let summaryRows = r.checks.enumerated().map { i, c in
            AuditRow(id: i, name: c.title, detail: (c.ok ? "✓ " : "⚠︎ ") + c.subtitle, amount: Double(c.rows.count))
        }
        var sheets: [(String, ReportSheet)] = [("الخلاصة", sheet(title: "تدقيق شهر \(month)", rows: summaryRows, detailTitle: "النتيجة", amountTitle: "العدد"))]
        for c in r.checks where !c.rows.isEmpty {
            sheets.append((c.title, sheet(title: "\(c.title) - شهر \(month)", rows: c.rows, detailTitle: c.detailTitle, amountTitle: c.amountTitle)))
        }
        return try await files(name: "تدقيق - \(r.month)", sheets: sheets)
    }

    static func check(_ c: AuditCheck, month: String) async throws -> (excel: URL, pdf: URL) {
        try await files(name: "\(c.title) - \(month)",
                        sheets: [(c.title, sheet(title: "\(c.title) - شهر \(ReportSheet.easternDigits(month))", rows: c.rows, detailTitle: c.detailTitle, amountTitle: c.amountTitle))])
    }

    static func comparison(_ m: MonthComparison) async throws -> (excel: URL, pdf: URL) {
        let span = "\(ReportSheet.easternDigits(m.from)) ← \(ReportSheet.easternDigits(m.to))"
        let summary = [
            AuditRow(id: 0, name: "موظفون جدد", detail: "", amount: Double(m.added.count)),
            AuditRow(id: 1, name: "خرجوا", detail: "", amount: Double(m.removed.count)),
            AuditRow(id: 2, name: "تغيّر راتبهم", detail: "", amount: Double(m.changed.count)),
            AuditRow(id: 3, name: "مجموع الصافي \(m.from)", detail: "", amount: m.netFrom),
            AuditRow(id: 4, name: "مجموع الصافي \(m.to)", detail: "", amount: m.netTo),
            AuditRow(id: 5, name: "الفرق", detail: "", amount: m.netTo - m.netFrom),
        ]
        let sheets: [(String, ReportSheet)] = [
            ("الخلاصة", sheet(title: "مقارنة \(span)", rows: summary, detailTitle: " ", amountTitle: "القيمة")),
            ("تغيّر الرواتب", sheet(title: "تغيّر الرواتب \(span)", rows: m.changed, detailTitle: "السابق ← الحالي", amountTitle: "الفرق")),
            ("الجدد", sheet(title: "الموظفون الجدد \(span)", rows: m.added, amountTitle: "الصافي")),
            ("الخارجون", sheet(title: "الخارجون \(span)", rows: m.removed, amountTitle: "الصافي")),
            ("الجهات", sheet(title: "استقطاعات الجهات \(span)", rows: m.entities, detailTitle: "السابق ← الحالي", amountTitle: "الفرق")),
        ]
        return try await files(name: "مقارنة \(m.from) و \(m.to)", sheets: sheets)
    }
}

/// إعادة فتح التقارير تشترك في نفس عملية التدقيق بدل تكرار قراءة الشهر.
actor AuditCache {
    static let shared = AuditCache()
    private var tasks: [String: Task<AuditReport, Error>] = [:]
    private var completed: [String: AuditReport] = [:]
    private var generation = 0

    private func cacheKey(month: String, threshold: Int, base: AuditBase, container: ModelContainer) throws -> String {
        let context = ModelContext(container)
        let descriptor = FetchDescriptor<PayrollMonth>(predicate: #Predicate { $0.name == month })
        let revision = try context.fetch(descriptor).first?.importedAt.timeIntervalSince1970 ?? 0
        return "\(month)|\(revision)|\(threshold)|\(base.rawValue)|\(AppSettings.locale.identifier)"
    }

    /// بطاقة التقارير تعرض نتيجة جاهزة فقط، ولا تبدأ تدقيقاً ثقيلاً أثناء التنقل.
    func cachedIssues(month: String, threshold: Int, base: AuditBase, container: ModelContainer) throws -> Int? {
        let key = try cacheKey(month: month, threshold: threshold, base: base, container: container)
        return completed[key]?.issues
    }

    func report(month: String, threshold: Int, base: AuditBase, container: ModelContainer) async throws -> AuditReport {
        let key = try cacheKey(month: month, threshold: threshold, base: base, container: container)
        if let report = completed[key] { return report }
        if let task = tasks[key] { return try await task.value }
        if tasks.count >= 3 { tasks.removeAll(); completed.removeAll() }
        let revision = generation
        let task = Task.detached(priority: .utility) {
            let started = Date()
            let result = try await ReportEngine(modelContainer: container).audit(month: month, threshold: threshold, base: base)
            PerfLog.record("audit", since: started)
            return result
        }
        tasks[key] = task
        do {
            let result = try await task.value
            guard revision == generation else { throw CancellationError() }
            completed[key] = result
            return result
        } catch {
            if revision == generation { tasks.removeValue(forKey: key) }
            throw error
        }
    }

    func invalidate() {
        generation += 1
        for task in tasks.values { task.cancel() }
        tasks.removeAll()
        completed.removeAll()
    }
}

// MARK: - الواجهة

/// صفحة التدقيق: فحوص الشهر، والمقارنة مع شهر آخر، وإعدادات الفحص.
struct AuditView: View {
    let month: String
    let months: [String]
    @AppStorage(AuditSettings.thresholdKey) private var threshold = 50
    @AppStorage(AuditSettings.baseKey) private var baseRaw = AuditBase.net.rawValue
    @State private var report: AuditReport?
    @State private var importState = StaffImportState.shared
    @Query private var savedMonths: [PayrollMonth]
    @State private var exporting = false
    @State private var error: String?

    private var base: AuditBase { AuditBase(rawValue: baseRaw) ?? .net }

    var body: some View {
        List {
            if let report {
                Section {
                    HStack(spacing: 12) {
                        Image(systemName: report.issues == 0 ? "checkmark.shield.fill" : "exclamationmark.triangle.fill")
                            .font(.title3.weight(.bold))
                            .foregroundStyle(.white)
                            .frame(width: 44, height: 44)
                            .background((report.issues == 0 ? Color.green : Color.orange).gradient, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                        VStack(alignment: .leading, spacing: 2) {
                            Text(report.issues == 0 ? tr("لا توجد ملاحظات", "No issues") : tr("\(Num.format(report.issues)) ملاحظات تحتاج مراجعة", "\(report.issues) issues to review"))
                                .font(.headline)
                            Text(tr("\(Num.format(report.employees)) موظف · \(Num.format(report.deductions)) استقطاع", "\(report.employees) employees · \(report.deductions) deductions"))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .padding(.vertical, 4)
                    .accessibilityIdentifier("auditSummary")
                }
                Section(tr("فحوص \(month)", "Checks for \(month)")) {
                    ForEach(report.checks) { check in
                        if check.rows.isEmpty {
                            AuditCheckRow(check: check)
                        } else {
                            NavigationLink { AuditCheckView(check: check, month: month) } label: { AuditCheckRow(check: check) }
                        }
                    }
                }
            } else {
                ProgressView(tr("جارٍ التدقيق…", "Checking…")).frame(maxWidth: .infinity).listRowBackground(Color.clear)
            }

            Section(tr("المقارنة", "Comparison")) {
                NavigationLink {
                    CompareView(months: months, to: month)
                } label: {
                    Label {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(tr("مقارنة الأشهر", "Compare months"))
                            Text(tr("الجدد، الخارجون، تغيّر الرواتب، الجهات", "New, removed, salary changes, entities")).font(.caption).foregroundStyle(.secondary)
                        }
                    } icon: {
                        Image(systemName: "arrow.left.arrow.right").foregroundStyle(.teal)
                    }
                }
                .accessibilityIdentifier("compareMonths")
            }

            Section {
                Picker(tr("حد الاستقطاعات", "Deduction limit"), selection: $threshold) {
                    ForEach([30, 40, 50, 60, 70, 80], id: \.self) { Text("\(Num.format($0))%").tag($0) }
                }
                Picker(tr("النسبة", "Ratio"), selection: $baseRaw) {
                    ForEach(AuditBase.allCases) { Text($0.title).tag($0.rawValue) }
                }
            } header: {
                Text(tr("إعدادات الفحص", "Check settings"))
            } footer: {
                Text(tr("يُفحص كل موظف: هل مجموع استقطاعاته أكبر من هذه النسبة؟", "Flags employees whose deductions exceed this share."))
            }
        }
        .navigationTitle(tr("التدقيق", "Audit"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button(tr("تصدير Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export(excel: true) }
                    Button(tr("تصدير PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export(excel: false) }
                } label: {
                    ReportToolbarLabel(isWorking: exporting)
                }
                .disabled(report == nil || exporting)
            }
        }
        .alert(tr("التصدير", "Export"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
        .task(id: "\(month)|\(savedMonths.first { $0.name == month }?.importedAt.timeIntervalSince1970 ?? 0)|\(threshold)|\(baseRaw)|\(importState.version)") {
            report = nil
            let loaded = await Self.run(month: month, threshold: threshold, base: base)
            guard !Task.isCancelled else { return }
            report = loaded
        }
    }

    static func run(month: String, threshold: Int, base: AuditBase) async -> AuditReport? {
        let container = SharedModel.container
        return try? await AuditCache.shared.report(month: month, threshold: threshold, base: base, container: container)
    }

    private func export(excel: Bool) {
        guard let report else { return }
        exporting = true
        Task {
            do {
                let files = try await AuditExport.report(report)
                exporting = false
                ActivityShare.present([excel ? files.excel : files.pdf])
            } catch {
                exporting = false
                self.error = error.localizedDescription
            }
        }
    }
}

private struct AuditCheckRow: View {
    let check: AuditCheck
    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: check.ok ? "checkmark" : "exclamationmark")
                .font(.footnote.weight(.bold))
                .foregroundStyle(.white)
                .frame(width: 30, height: 30)
                .background((check.ok ? Color.green : Color.orange).gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 2) {
                Text(check.title).font(.subheadline.weight(.semibold))
                Text(check.subtitle).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            if !check.rows.isEmpty {
                Text(Num.format(check.rows.count)).font(.subheadline.weight(.bold).monospacedDigit()).foregroundStyle(check.ok ? Color.secondary : Color.red)
            }
        }
        .padding(.vertical, 2)
    }
}

/// أسماء ملاحظة واحدة مع السبب، والتصدير.
struct AuditCheckView: View {
    let check: AuditCheck
    let month: String
    @State private var search = ""
    @State private var exporting = false

    private var filtered: [AuditRow] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        guard !tokens.isEmpty else { return check.rows }
        return check.rows.filter { r in tokens.allSatisfy { ArabicText.normalize(r.name + " " + r.detail).contains($0) } }
    }

    var body: some View {
        List {
            Section(tr("\(Num.format(filtered.count)) · \(check.subtitle)", "\(filtered.count) · \(check.subtitle)")) {
                ForEach(filtered) { row in
                    HStack(alignment: .top, spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(row.name).font(.subheadline.weight(.semibold))
                            if !row.detail.isEmpty { Text(Num.digits(row.detail)).font(.caption).foregroundStyle(.secondary) }
                        }
                        Spacer(minLength: 8)
                        Text(Money.format(row.amount)).font(.subheadline.weight(.semibold).monospacedDigit()).foregroundStyle(check.ok ? Color.primary : Color.red)
                    }
                    .contextMenu {
                        Button(tr("نسخ الاسم", "Copy name"), systemImage: "doc.on.doc") { copyToClipboard(row.name) }
                        Button(tr("بحث عنه", "Search"), systemImage: "magnifyingglass") {
                            ShortcutRouter.shared.pendingSearch = row.name
                            AppRouter.shared.tab = .search
                        }
                    }
                }
            }
        }
        .adaptiveSearchable(text: $search, prompt: tr("ابحث بالاسم", "Search name"))
        .navigationTitle(check.title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button(tr("تصدير Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export(excel: true) }
                    Button(tr("تصدير PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export(excel: false) }
                } label: {
                    ReportToolbarLabel(isWorking: exporting)
                }
                .disabled(exporting)
            }
        }
    }

    private func export(excel: Bool) {
        exporting = true
        Task {
            let files = try? await AuditExport.check(check, month: month)
            exporting = false
            if let files { ActivityShare.present([excel ? files.excel : files.pdf]) }
        }
    }
}

/// مقارنة شهرين: الجدد، الخارجون، تغيّر الرواتب، استقطاعات الجهات.
struct CompareView: View {
    let months: [String]
    @State var to: String
    @State private var from = ""
    @State private var result: MonthComparison?
    @State private var part = 0
    @State private var exporting = false

    init(months: [String], to: String) {
        self.months = months
        _to = State(initialValue: to)
        let i = months.firstIndex(of: to) ?? 0
        _from = State(initialValue: months.indices.contains(i + 1) ? months[i + 1] : (months.first { $0 != to } ?? to))
    }

    private var rows: [AuditRow] {
        guard let result else { return [] }
        switch part {
        case 1: return result.added
        case 2: return result.removed
        case 3: return result.entities
        default: return result.changed
        }
    }

    var body: some View {
        List {
            Section {
                HStack(spacing: 8) {
                    monthMenu($from)
                    Image(systemName: "arrow.left").foregroundStyle(.secondary)
                    monthMenu($to)
                }
                .listRowBackground(Color.clear)
                .listRowInsets(EdgeInsets())
            }
            if let result {
                Section {
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 10), GridItem(.flexible(), spacing: 10)], spacing: 10) {
                        tile("+\(Num.format(result.added.count))", tr("موظف جديد", "New"), .green)
                        tile("−\(Num.format(result.removed.count))", tr("خرجوا", "Removed"), .red)
                        tile(Num.format(result.changed.count), tr("تغيّر راتبهم", "Changed"), .blue)
                        let diff = result.netTo - result.netFrom
                        tile((diff >= 0 ? "+" : "") + Money.format(diff), tr("فرق مجموع الصافي", "Net difference"), diff >= 0 ? .green : .red)
                    }
                    .listRowBackground(Color.clear)
                    .listRowInsets(EdgeInsets())
                }
                Section {
                    Picker("", selection: $part) {
                        Text(tr("الرواتب", "Salaries")).tag(0)
                        Text(tr("الجدد", "New")).tag(1)
                        Text(tr("الخارجون", "Removed")).tag(2)
                        Text(tr("الجهات", "Entities")).tag(3)
                    }
                    .pickerStyle(.segmented)
                    .listRowBackground(Color.clear)
                    .listRowInsets(EdgeInsets())
                }
                Section {
                    if rows.isEmpty {
                        Text(tr("لا يوجد", "None")).foregroundStyle(.secondary)
                    }
                    ForEach(rows.prefix(500)) { row in
                        HStack(alignment: .top, spacing: 10) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(row.name).font(.subheadline.weight(.semibold))
                                if !row.detail.isEmpty { Text(row.detail).font(.caption).foregroundStyle(.secondary) }
                            }
                            Spacer(minLength: 8)
                            let signed = part == 0 || part == 3
                            Text((signed && row.amount > 0 ? "+" : "") + Money.format(row.amount))
                                .font(.subheadline.weight(.semibold).monospacedDigit())
                                .foregroundStyle(signed ? (row.amount >= 0 ? Color.green : Color.red) : Color.primary)
                        }
                        .contextMenu {
                            Button(tr("نسخ الاسم", "Copy name"), systemImage: "doc.on.doc") { copyToClipboard(row.name) }
                        }
                    }
                } footer: {
                    if rows.count > 500 { Text(tr("يظهر أول 500، والكل في التصدير.", "First 500 shown; export has all.")) }
                }
            } else {
                ProgressView().frame(maxWidth: .infinity).listRowBackground(Color.clear)
            }
        }
        .listSectionSpacing(.compact)
        .navigationTitle(tr("مقارنة الأشهر", "Compare months"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button(tr("تصدير Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export(excel: true) }
                    Button(tr("تصدير PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export(excel: false) }
                } label: {
                    ReportToolbarLabel(isWorking: exporting)
                }
                .disabled(result == nil || exporting)
            }
        }
        .task(id: "\(from)|\(to)") {
            result = nil
            let a = from, b = to
            let container = SharedModel.container
            result = try? await Task.detached(priority: .userInitiated) {
                try await ReportEngine(modelContainer: container).compare(from: a, to: b)
            }.value
        }
    }

    private func monthMenu(_ selection: Binding<String>) -> some View {
        Menu {
            Picker("", selection: selection) {
                ForEach(months, id: \.self) { Text($0).tag($0) }
            }
        } label: {
            HStack(spacing: 4) {
                Text(selection.wrappedValue).lineLimit(1)
                Image(systemName: "chevron.down").font(.caption.weight(.bold))
            }
            .font(.subheadline.weight(.semibold))
            .frame(maxWidth: .infinity)
            .padding(.vertical, 9)
            .background(Color(.secondarySystemGroupedBackground), in: Capsule())
        }
    }

    private func tile(_ value: String, _ label: String, _ color: Color) -> some View {
        VStack(spacing: 2) {
            Text(value).font(.title3.weight(.bold).monospacedDigit()).foregroundStyle(color).lineLimit(1).minimumScaleFactor(0.6)
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 12)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
    }

    private func export(excel: Bool) {
        guard let result else { return }
        exporting = true
        Task {
            let files = try? await AuditExport.comparison(result)
            exporting = false
            if let files { ActivityShare.present([excel ? files.excel : files.pdf]) }
        }
    }
}
