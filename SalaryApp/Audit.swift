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

// MARK: - فحوص الاستقطاعات

extension ReportEngine {
    /// فحوص ملف الاستقطاعات لشهر: استقطاعات بلا راتب، مطابقة المجاميع، التكرار المحتمل.
    func deductionChecks(month: String) throws -> [AuditCheck] {
        var e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        e.propertiesToFetch = [\.matchKey, \.itemsData]
        let employees = try modelContext.fetch(e)
        var d = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
        d.propertiesToFetch = [\.employeeName, \.matchKey, \.title, \.amount, \.note]
        let deductions = try modelContext.fetch(d)
        try Task.checkCancellation()
        func money(_ v: Double) -> String { Money.format(v) }
        var checks: [AuditCheck] = []

        // ١. استقطاعات بلا راتب.
        if employees.isEmpty {
            checks.append(AuditCheck(id: "orphans", title: tr("استقطاعات بلا راتب", "Deductions without payroll"),
                                     subtitle: tr("لم يُستورد ملف رواتب هذا الشهر بعد", "No payroll file for this month yet"), ok: true, rows: []))
        } else {
            let payKeys = Set(employees.map(\.matchKey))
            let orphans = deductions.enumerated().filter { !payKeys.contains($0.element.matchKey) }
                .map { AuditRow(id: $0.offset, name: $0.element.employeeName, detail: $0.element.title, amount: $0.element.amount ?? 0) }
                .sorted { $0.name < $1.name }
            checks.append(AuditCheck(id: "orphans", title: tr("استقطاعات بلا راتب", "Deductions without payroll"),
                                     subtitle: orphans.isEmpty ? tr("كل الأسماء موجودة في الرواتب", "Every name is in the payroll")
                                                               : tr("أسماء في الاستقطاعات وليست في الرواتب", "Names in deductions but not in payroll"),
                                     ok: orphans.isEmpty, rows: orphans, detailTitle: "جهة التنفيذ"))
        }

        // ٢. مطابقة المجاميع: مجموع الجهات = مجموع استقطاعات المصارف في الرواتب.
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

        // ٣. تكرار محتمل.
        checks.append(DeductionReview.duplicates(deductions.map {
            DeductionSnapshot(name: $0.employeeName, entity: $0.title, note: $0.note, amount: $0.amount)
        }))
        return checks
    }
}

// MARK: - خلاصة بعد الاستيراد

/// تدقيق أحدث شهر ومقارنته بالشهر الذي قبله: لرسالة الاستيراد والإشعارات وبطاقة التقارير.
nonisolated struct AuditDigest: Sendable {
    let month: String
    let previous: String
    let salary: SalaryAudit?
    let deductionChecks: [AuditCheck]
    let deductions: DeductionComparison?
    var salaryIssues: Int { salary?.issues ?? 0 }
    var deductionIssues: Int { deductionChecks.filter { !$0.ok }.count }
    var issues: Int { salaryIssues + deductionIssues }

    /// الشهر والشهر الذي قبله (بترتيب الأشهر المحفوظة).
    static func months(container: ModelContainer, latest: String? = nil) -> (month: String, previous: String)? {
        let context = ModelContext(container)
        let d = FetchDescriptor<PayrollMonth>(sortBy: [SortDescriptor(\.sortKey, order: .reverse), SortDescriptor(\.importedAt, order: .reverse)])
        let names = ((try? context.fetch(d)) ?? []).map(\.name)
        guard let month = latest ?? names.first else { return nil }
        let i = names.firstIndex(of: month) ?? 0
        return (month, names.indices.contains(i + 1) ? names[i + 1] : "")
    }

    private static func comparison(previous: String, month: String, container: ModelContainer) async -> DeductionComparison? {
        if previous.isEmpty { return nil }
        return try? await AuditResults.shared.deductions(from: previous, to: month, container: container)
    }

    static func run(month: String, previous: String, container: ModelContainer) async -> AuditDigest {
        let results = AuditResults.shared
        async let salary = try? results.salary(from: previous, to: month, container: container)
        async let checks = (try? results.deductionChecks(month: month, container: container)) ?? []
        async let deductions = comparison(previous: previous, month: month, container: container)
        return await AuditDigest(month: month, previous: previous, salary: salary, deductionChecks: checks, deductions: deductions)
    }
}

// MARK: - التصدير

nonisolated enum AuditExport {
    static var header: String { UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader }

    static func sheet(title: String, rows: [AuditRow], detailTitle: String = "التفاصيل", amountTitle: String = "المبلغ") -> ReportSheet {
        ReportSheet(
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

    /// جدول خلاصة (الحالة، العدد، المبلغ) بلا سطر مجموع.
    static func summary(title: String, rows: [(String, String, Double)]) -> ReportSheet {
        var sheet = ReportSheet(
            header: header, title: title,
            columns: [
                .init(title: "الحالة", width: 34, kind: .text),
                .init(title: "العدد", width: 10, kind: .text),
                .init(title: "المبلغ", width: 18, kind: .amount),
            ],
            rows: rows.map { [$0.0, $0.1, ""] },
            amounts: rows.map { $0.2 }
        ).fitted()
        sheet.showTotal = false
        return sheet
    }

    static func files(name: String, sheets: [(name: String, sheet: ReportSheet)]) async throws -> (excel: URL, pdf: URL) {
        let safe = name.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        let names = BulkExport.sheetNames(sheets.map(\.name))
        let named = zip(names, sheets).map { ($0, $1.sheet) }
        return try await PDFWork.run {
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("report-export", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let excel = folder.appendingPathComponent(safe + ".xlsx")
            let pdf = folder.appendingPathComponent(safe + ".pdf")
            try XLSXWriter.writeWorkbook(named, to: excel)
            try PDFReportWriter.write(named.map(\.1), to: pdf)
            try PDFDecor.apply(to: pdf)
            return (excel, pdf)
        }
    }

    static func check(_ c: AuditCheck, month: String) async throws -> (excel: URL, pdf: URL) {
        try await files(name: "\(c.title) - \(month)",
                        sheets: [(c.title, sheet(title: "\(c.title) - شهر \(ReportSheet.easternDigits(month))", rows: c.rows, detailTitle: c.detailTitle, amountTitle: c.amountTitle))])
    }

    /// كل فحوص قائمة في ملف واحد: خلاصة ثم ورقة لكل فحص فيه ملاحظات.
    static func checks(_ list: [AuditCheck], title: String, month: String) async throws -> (excel: URL, pdf: URL) {
        let m = ReportSheet.easternDigits(month)
        var sheets: [(String, ReportSheet)] = [("الخلاصة", summary(title: "\(title) - شهر \(m)",
                                                                   rows: list.map { ($0.title + ($0.ok ? " ✓" : ""), Num.format($0.rows.count), Double(0)) }))]
        for c in list where !c.rows.isEmpty {
            sheets.append((c.title, sheet(title: "\(c.title) - شهر \(m)", rows: c.rows, detailTitle: c.detailTitle, amountTitle: c.amountTitle)))
        }
        return try await files(name: "\(title) - \(month)", sheets: sheets)
    }

    // MARK: الرواتب

    private static func salarySheet(_ kind: SalaryChangeKind, _ rows: [SalaryChange], from: String, to: String) -> ReportSheet {
        let span = "\(ReportSheet.easternDigits(from)) ← \(ReportSheet.easternDigits(to))"
        return ReportSheet(
            header: header, title: "\(kind.reportTitle) · \(span)",
            columns: [
                .init(title: "ت", width: 5, kind: .serial),
                .init(title: "الاسم الكامل", width: 30, kind: .text),
                .init(title: "الرتبة", width: 11, kind: .text),
                .init(title: "محل العمل", width: 26, kind: .text),
                .init(title: "صافي \(from)", width: 14, kind: .text),
                .init(title: "صافي \(to)", width: 14, kind: .text),
                .init(title: "التفاصيل", width: 34, kind: .text),
                .init(title: "الفرق", width: 14, kind: .amount),
            ],
            rows: rows.enumerated().map { i, r in
                [String(i + 1), r.name, r.rank, r.workplace,
                 r.before.map { Money.format($0) } ?? "—", r.after.map { Money.format($0) } ?? "—",
                 r.reasons.joined(separator: " · "), ""]
            },
            amounts: rows.map(\.difference)
        ).fitted()
    }

    /// مقارنة البنود: الأثر على الصافي (المخصصات +، الاستقطاعات −)، فمجموعها = فرق الصافي.
    private static func itemsSheet(_ a: SalaryAudit) -> ReportSheet {
        let rows = a.items.filter { !$0.isTotal }
        return ReportSheet(
            header: header, title: "مقارنة البنود · \(ReportSheet.easternDigits(a.from)) ← \(ReportSheet.easternDigits(a.to))",
            columns: [
                .init(title: "ت", width: 5, kind: .serial),
                .init(title: "البند", width: 26, kind: .text),
                .init(title: a.from, width: 18, kind: .text),
                .init(title: a.to, width: 18, kind: .text),
                .init(title: "الفرق", width: 16, kind: .text),
                .init(title: "الأثر على الصافي", width: 18, kind: .amount),
            ],
            rows: rows.enumerated().map { i, r in
                [String(i + 1), r.title, Money.format(r.before), Money.format(r.after), Money.format(r.difference), ""]
            },
            amounts: rows.map { $0.group == .allowance ? $0.difference : -$0.difference }
        ).fitted()
    }

    /// kinds = nil: كشف شامل بكل المتغيرات (خلاصة + كل حالة + مقارنة البنود).
    static func salary(_ a: SalaryAudit, kinds: [SalaryChangeKind]? = nil) async throws -> (excel: URL, pdf: URL) {
        let span = "\(ReportSheet.easternDigits(a.from)) ← \(ReportSheet.easternDigits(a.to))"
        var sheets: [(String, ReportSheet)] = []
        if let kinds, kinds.count == 1, let kind = kinds.first {
            sheets.append((kind.title, salarySheet(kind, a.rows(kind), from: a.from, to: a.to)))
            return try await files(name: "\(kind.reportTitle) - \(a.from) - \(a.to)", sheets: sheets)
        }
        var summary: [(String, String, Double)] = [
            ("مجموع صافي \(a.from)", "—", a.netFrom),
            ("مجموع صافي \(a.to)", "—", a.netTo),
        ]
        for kind in SalaryChangeKind.allCases {
            let rows = a.rows(kind)
            summary.append((kind.reportTitle, Num.format(rows.count), rows.reduce(0) { $0 + $1.difference }))
        }
        summary.append(("الفرق بين الشهرين", "—", a.netTo - a.netFrom))
        sheets.append(("الخلاصة", self.summary(title: "متغيرات الرواتب · \(span)", rows: summary)))
        for kind in kinds ?? SalaryChangeKind.allCases where !a.rows(kind).isEmpty {
            sheets.append((kind.title, salarySheet(kind, a.rows(kind), from: a.from, to: a.to)))
        }
        if kinds == nil, !a.items.isEmpty { sheets.append(("مقارنة البنود", itemsSheet(a))) }
        return try await files(name: "متغيرات الرواتب - \(a.from) - \(a.to)", sheets: sheets)
    }

    /// موظفو بند واحد تغيّر عندهم.
    static func item(_ item: ItemDelta, from: String, to: String) async throws -> (excel: URL, pdf: URL) {
        let sheet = ReportSheet(
            header: header, title: "\(item.title) · \(ReportSheet.easternDigits(from)) ← \(ReportSheet.easternDigits(to))",
            columns: [
                .init(title: "ت", width: 5, kind: .serial),
                .init(title: "الاسم الكامل", width: 30, kind: .text),
                .init(title: "الرتبة", width: 11, kind: .text),
                .init(title: from, width: 14, kind: .text),
                .init(title: to, width: 14, kind: .text),
                .init(title: "الفرق", width: 14, kind: .amount),
            ],
            rows: item.people.enumerated().map { i, r in
                [String(i + 1), r.name + (r.reasons.isEmpty ? "" : " (\(r.reasons.joined(separator: "، ")))"), r.rank,
                 Money.format(r.before ?? 0), Money.format(r.after ?? 0), ""]
            },
            amounts: item.people.map(\.difference)
        ).fitted()
        return try await files(name: "\(item.title) - \(from) - \(to)", sheets: [(item.title, sheet)])
    }

    // MARK: الاستقطاعات

    private static func deductionSheet(_ kind: DeductionGroupKind, _ rows: [DeductionChange], from: String, to: String) -> ReportSheet {
        ReportSheet(
            header: header, title: "\(kind.reportTitle) · \(ReportSheet.easternDigits(from)) ← \(ReportSheet.easternDigits(to))",
            columns: [
                .init(title: "ت", width: 5, kind: .serial),
                .init(title: "الاسم الكامل", width: 30, kind: .text),
                .init(title: "الجهة", width: 28, kind: .text),
                .init(title: "الإضبارة", width: 18, kind: .text),
                .init(title: from, width: 14, kind: .text),
                .init(title: to, width: 14, kind: .text),
                .init(title: "الفرق", width: 14, kind: .amount),
            ],
            rows: rows.enumerated().map { i, r in
                [String(i + 1), r.name, r.entity, r.note, Money.format(r.before), Money.format(r.after), ""]
            },
            amounts: rows.map(\.difference)
        ).fitted()
    }

    /// kinds = nil: كشف شامل بكل المتغيرات (خلاصة + توقف + جديد + تغيّر المبلغ).
    static func deductions(_ result: DeductionComparison, kinds: [DeductionGroupKind]? = nil) async throws -> (excel: URL, pdf: URL) {
        let span = "\(ReportSheet.easternDigits(result.from)) ← \(ReportSheet.easternDigits(result.to))"
        if let kinds, kinds.count == 1, let kind = kinds.first {
            return try await files(name: "\(kind.reportTitle) - \(result.from) - \(result.to)",
                                   sheets: [(kind.title, deductionSheet(kind, result.rows(kind), from: result.from, to: result.to))])
        }
        var summary: [(String, String, Double)] = [
            ("مجموع \(result.from)", "—", result.before),
            ("مجموع \(result.to)", "—", result.after),
        ]
        for kind in DeductionGroupKind.allCases {
            let rows = result.rows(kind)
            summary.append((kind.reportTitle, Num.format(rows.count), rows.reduce(0) { $0 + $1.difference }))
        }
        summary.append(("الفرق بين الشهرين", "—", result.after - result.before))
        var sheets: [(String, ReportSheet)] = [("الخلاصة", self.summary(title: "متغيرات الاستقطاعات · \(span)", rows: summary))]
        for kind in kinds ?? DeductionGroupKind.allCases where !result.rows(kind).isEmpty {
            sheets.append((kind.title, deductionSheet(kind, result.rows(kind), from: result.from, to: result.to)))
        }
        if !result.emptyMonths.isEmpty {
            sheets.append((tr("تنبيه", "Notice"), sheet(title: span, rows: [
                AuditRow(id: 0, name: tr("شهر بلا قيود", "Month without entries"),
                         detail: tr("اعتُبرت الأشهر التالية خالية بتأكيد المستخدم: ", "Treated as empty with user confirmation: ") + result.emptyMonths.joined(separator: "، "), amount: 0)
            ])))
        }
        return try await files(name: "متغيرات الاستقطاعات - \(result.from) - \(result.to)", sheets: sheets)
    }
}

// MARK: - الواجهة المشتركة

struct AuditCheckRow: View {
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
                    .personActions(name: row.name, month: month)
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
