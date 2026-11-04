import SwiftUI
import SwiftData

/// صفوف المصروفات محسوبة مرة واحدة، مع المقارنة بمجموع كشف البنك لا بحركة تمويل خارجية.
nonisolated struct FundingExpenses: Sendable, Equatable {
    struct Row: Sendable, Identifiable, Equatable { let id: Int; let title: String; let amount: Double }
    let month: String
    let rows: [Row]
    let bankTotal: Double
    let unclassifiedCount: Int
    let hasExecutionRows: Bool
    var total: Double { rows.reduce(0) { $0 + $1.amount } }
    var difference: Double { bankTotal - total }

    static func build(summary: BankStatementSummary, manual: BankManualAmounts, groups: [ReportGroup]) throws -> Self {
        func fail(_ text: String) -> NSError { NSError(domain: "FundingExpenses", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }
        guard summary.payrollCount > 0 else { throw fail(tr("لا توجد رواتب محفوظة للشهر المختار.", "No payroll is saved for the selected month.")) }
        guard summary.missingNet == 0, summary.missingContractNominal == 0, !summary.contractOverrideStale else {
            throw fail(tr("توجد بيانات صافي أو اسمي عقود ناقصة، أو تعديل عقود يحتاج مراجعة. أكملها قبل إعداد الكشف.", "Net pay or contract nominal data is missing, or a contract override requires review."))
        }
        guard summary.missingExecutionAmount == 0 else { throw fail(tr("توجد استقطاعات جهات بمبالغ ناقصة؛ راجع الملف قبل إعداد الكشف.", "Some execution deductions have missing amounts; review the file.")) }
        guard groups.allSatisfy({ $0.kind == .deductions && $0.month == summary.month }) else { throw fail(tr("بيانات الجهات لا تطابق الشهر المختار.", "The entity data does not match the selected month.")) }
        let values = [summary.cashNet, summary.cardNet, summary.unclassifiedNet, summary.bankDeductions,
                      summary.contractContribution, manual.accumulatedLeave, manual.employeeRewards, manual.singleList] + Array(summary.breakdown.values) + groups.map(\.total)
        guard values.allSatisfy({ $0.isFinite && $0 >= 0 && $0 <= 9_007_199_254_740_991 && $0.rounded() == $0 }) else { throw fail(tr("يوجد مبلغ غير صالح أو غير صحيح بالدينار؛ راجع بيانات الشهر.", "A monthly amount is invalid or not a whole dinar.")) }
        guard groups.reduce(0, { $0 + $1.total }) == summary.executionDeductions else { throw fail(tr("تغيّرت بيانات الجهات أثناء الحساب؛ أعد المحاولة.", "Entity data changed during calculation; try again.")) }
        let split = FoodContractReport.Split(total: summary.breakdown["شركة عراق الجوال"] ?? 0)
        var result: [Row] = []
        func add(_ title: String, _ amount: Double) { result.append(Row(id: result.count, title: title, amount: amount)) }
        add("رواتب البطاقة", summary.cardNet)
        add("رواتب الكاش", summary.cashNet)
        if summary.unclassifiedCount > 0 { add("رواتب غير مصنفة حسب طريقة الدفع", summary.unclassifiedNet) }
        add("الإجازات المتراكمة", manual.accumulatedLeave)
        add("مكافآت المنتسبين", manual.employeeRewards)
        add("الرعاية الصحية", summary.breakdown["الرعاية الصحية"] ?? 0)
        add("صندوق شهداء الشرطة", summary.breakdown["صندوق شهداء الشرطة"] ?? 0)
        add("الضريبة", summary.breakdown["الضريبة"] ?? 0)
        add("الحماية الاجتماعية", summary.breakdown["الحماية الاجتماعية"] ?? 0)
        add("عقد عراق الجوال - حصة صندوق الشهداء ٣٪", split.fund)
        add("عقد عراق الجوال - الضريبة ١٫٨٪ من الباقي", split.tax)
        add("عقد عراق الجوال - صافي الشركة", split.company)
        let known: Set<String> = ["الرعاية الصحية", "صندوق شهداء الشرطة", "الضريبة", "الحماية الاجتماعية", "شركة عراق الجوال"]
        for key in summary.breakdown.keys.sorted() where !known.contains(key) { add(key, summary.breakdown[key] ?? 0) }
        if summary.contractCount > 0 || summary.contractContribution != 0 { add("الضمان الاجتماعي للعقود", summary.contractContribution) }
        if manual.singleList != 0 { add("المفردة", manual.singleList) }
        for group in groups.sorted(by: { $0.total == $1.total ? $0.title < $1.title : $0.total > $1.total }) {
            add("جهة تنفيذ: \(group.title)", group.total)
        }
        let report = Self(month: summary.month, rows: result, bankTotal: summary.bankTotal + manual.total, unclassifiedCount: summary.unclassifiedCount, hasExecutionRows: summary.executionCount > 0)
        guard report.total.isFinite, report.bankTotal.isFinite, report.total <= 9_007_199_254_740_991, report.bankTotal <= 9_007_199_254_740_991 else { throw fail(tr("إجمالي الكشف يتجاوز الحد المدعوم.", "The statement total exceeds the supported limit.")) }
        return report
    }

    func sheets() -> (ReportSheet, ReportSheet) {
        let columns: [ReportSheet.Column] = [.init(title: "ت", width: 5, kind: .serial), .init(title: "البيان", width: 42, kind: .text), .init(title: "المبلغ", width: 20, kind: .amount)]
        let header = UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader
        let detail = ReportSheet(header: header, title: "تفصيل المصروفات - \(month)", columns: columns,
                                 rows: rows.enumerated().map { [String($0.offset + 1), $0.element.title, ""] }, amounts: rows.map(\.amount), entityDeduction: true).fitted()
        let status = difference == 0 ? "متطابق" : "غير متطابق - الفرق يحتاج مراجعة"
        let message = unclassifiedCount > 0 ? "\(status) - توجد رواتب غير مصنفة" : (!hasExecutionRows ? "\(status) - لا توجد استقطاعات محفوظة" : status)
        var comparisonRows = [["1", "مجموع المصروفات", ""], ["2", "مجموع كشف البنك", ""], ["3", "الفرق (البنك ناقص المصروفات)", ""]]
        var comparisonValues = [total, bankTotal, difference]
        if unclassifiedCount > 0 { comparisonRows.append(["", "تنبيه: عدد الرواتب غير المصنفة", ""]); comparisonValues.append(Double(unclassifiedCount)) }
        if !hasExecutionRows { comparisonRows.append(["", "تنبيه: عدد سجلات الاستقطاعات المحفوظة", ""]); comparisonValues.append(0) }
        let comparisonColumns: [ReportSheet.Column] = [.init(title: "ت", width: 5, kind: .serial), .init(title: "البيان", width: 42, kind: .text), .init(title: "المبلغ / العدد", width: 20, kind: .amount)]
        let summary = ReportSheet(header: header, title: "مطابقة كشف البنك - \(month) - \(message)", columns: comparisonColumns,
                                  rows: comparisonRows, amounts: comparisonValues, showTotal: false, entityDeduction: true).fitted()
        return (detail, summary)
    }
}

struct FundingExpensesView: View {
    let month: String
    let revision: String
    @AppStorage("bankStatementManualAmounts") private var manualData = Data()
    @State private var report: FundingExpenses?
    @State private var error: String?
    @State private var loading = true
    @State private var exporter = QuickExporter.shared
    @State private var cache = BankStatementCache.shared

    var body: some View {
        List {
            if loading { ProgressView(tr("جارٍ حساب المصروفات", "Calculating expenses")) }
            else if let error { Text(error).foregroundStyle(.red) }
            else if let report {
                Section(tr("المطابقة مع كشف البنك", "Bank statement comparison")) {
                    LabeledContent(tr("مجموع المصروفات", "Total expenses"), value: Money.format(report.total))
                    LabeledContent(tr("مجموع كشف البنك", "Bank statement total"), value: Money.format(report.bankTotal))
                    LabeledContent(tr("الفرق: البنك ناقص المصروفات", "Difference: bank minus expenses"), value: Money.format(report.difference))
                    Label(report.difference == 0 ? tr("المبلغان متطابقان", "Amounts match") : tr("المبلغان غير متطابقين؛ راجع الجهات والمبالغ.", "Amounts differ; review entities and amounts."), systemImage: report.difference == 0 ? "checkmark.circle" : "exclamationmark.triangle")
                        .foregroundStyle(report.difference == 0 ? .green : .orange)
                    if report.unclassifiedCount > 0 { Text(tr("توجد رواتب غير مصنفة حسب طريقة الدفع؛ راجعها.", "Some salaries have no classified payment method; review them.")).foregroundStyle(.orange) }
                    if !report.hasExecutionRows { Text(tr("لا توجد سجلات استقطاعات محفوظة للشهر؛ تأكد من ملف جهات التنفيذ.", "No deduction records are saved for this month; check the execution file.")).foregroundStyle(.orange) }
                    Text(tr("تساوي المبلغين حسابياً لا يؤكد اكتمال ملفات المصدر؛ راجع بيانات الشهر.", "A numerical match does not verify source-file completeness; review the monthly data.")).font(.footnote).foregroundStyle(.secondary)
                }
                .accessibilityIdentifier("fundingExpensesComparison")
                Section(tr("تفاصيل المصروفات", "Expense details")) {
                    ForEach(report.rows) { row in LabeledContent(row.title, value: Money.format(row.amount)) }
                }
                Section {
                    Button(tr("تصدير Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export(.excel) }.accessibilityIdentifier("fundingExpensesExcel")
                    Button(tr("تصدير PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export(.pdf) }.accessibilityIdentifier("fundingExpensesPDF")
                }.disabled(exporter.isWorking)
            }
        }
        .navigationTitle(tr("تفصيل المصروفات", "Expense details"))
        .navigationBarTitleDisplayMode(.inline)
        .task(id: "\(month)|\(revision)|\(cache.revision)|\(manualData.base64EncodedString())") { await load() }
        .overlay { if exporter.isWorking { ProgressView(tr("جارٍ تجهيز الكشف", "Preparing statement")).padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16)) } }
        .alert(tr("التصدير", "Export"), isPresented: Binding(get: { exporter.error != nil }, set: { if !$0 { exporter.error = nil } })) { Button(tr("حسناً", "OK"), role: .cancel) {} } message: { Text(exporter.error ?? "") }
    }

    @MainActor private func calculate() async throws -> FundingExpenses {
        guard !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting, !BackupManager.shared.isWorking else { throw NSError(domain: "FundingExpenses", code: 2, userInfo: [NSLocalizedDescriptionKey: tr("انتظر انتهاء الاستيراد أو النسخ قبل حساب الكشف.", "Wait for import or backup to finish.")]) }
        let data = manualData
        let overrides = try ContractOverrides.currentData()
        func monthMarker() throws -> String {
            let name = month
            var request = FetchDescriptor<PayrollMonth>(predicate: #Predicate { $0.name == name })
            request.fetchLimit = 1
            guard let record = try SharedModel.container.mainContext.fetch(request).first else { throw NSError(domain: "FundingExpenses", code: 4, userInfo: [NSLocalizedDescriptionKey: tr("الشهر المختار غير موجود.", "The selected month is unavailable.")]) }
            return "\(record.importedAt.timeIntervalSince1970)|\(record.employeeCount)|\(record.deductionCount)"
        }
        let marker = try monthMarker()
        let saved = data.isEmpty ? [:] : try JSONDecoder().decode([String: BankManualAmounts].self, from: data)
        let manual = saved[month] ?? .init()
        let container = SharedModel.container
        let selectedMonth = month
        let job = Task.detached(priority: .userInitiated) {
            let summary = try await BankStatementEngine(modelContainer: container).summary(month: selectedMonth)
            let groups = try await ReportEngine(modelContainer: container).deductionGroups(month: selectedMonth, byEntity: false)
            return try FundingExpenses.build(summary: summary, manual: manual, groups: groups)
        }
        let result = try await withTaskCancellationHandler { try await job.value } onCancel: { job.cancel() }
        guard !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting, !BackupManager.shared.isWorking, data == manualData, overrides == (try ContractOverrides.currentData()), marker == (try monthMarker()) else { throw NSError(domain: "FundingExpenses", code: 3, userInfo: [NSLocalizedDescriptionKey: tr("تغيّرت البيانات أثناء حساب الكشف؛ أعد المحاولة.", "Data changed during calculation; try again.")]) }
        return result
    }
    @MainActor private func load() async {
        loading = true; report = nil; error = nil
        do { let value = try await calculate(); guard !Task.isCancelled else { return }; report = value }
        catch { guard !Task.isCancelled else { return }; self.error = error.localizedDescription }
        loading = false
    }
    @MainActor private func export(_ format: ExportFormat) {
        exporter.run {
            let fresh = try await calculate()
            guard fresh == report else {
                report = fresh
                throw NSError(domain: "FundingExpenses", code: 5, userInfo: [NSLocalizedDescriptionKey: tr("تغيّرت بيانات الكشف؛ راجع المبالغ المحدّثة ثم أعد التصدير.", "Statement data changed; review the updated amounts and export again.")])
            }
            let (detail, comparison) = fresh.sheets()
            return try await PDFWork.run {
                let folder = FileManager.default.temporaryDirectory.appendingPathComponent("expenses-\(UUID().uuidString)", isDirectory: true)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                var finished = false
                defer { if !finished { try? FileManager.default.removeItem(at: folder) } }
                let file = folder.appendingPathComponent(format == .excel ? "تفصيل المصروفات.xlsx" : "تفصيل المصروفات.pdf")
                if format == .excel { try XLSXWriter.writeWorkbook([(name: "المصروفات", sheet: detail), (name: "المطابقة", sheet: comparison)], to: file) }
                else { try PDFReportWriter.write([detail, comparison], to: file); try PDFDecor.apply(to: file) }
                finished = true
                return file
            }
        }
    }
}
