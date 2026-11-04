import SwiftUI
import SwiftData

/// صفوف المصروفات محسوبة مرة واحدة، مع المقارنة بمجموع كشف البنك لا بحركة تمويل خارجية.
nonisolated struct FundingExpenses: Sendable, Equatable, Codable {
    struct Row: Sendable, Identifiable, Equatable, Codable { let id: Int; let title: String; let amount: Double }
    let month: String
    let rows: [Row]
    let bankTotal: Double
    let unclassifiedCount: Int
    let hasExecutionRows: Bool
    let missingNet: Int
    let missingContractNominal: Int
    let missingExecutionAmount: Int
    let contractOverrideStale: Bool
    var isProvisional: Bool { missingNet > 0 || missingContractNominal > 0 || missingExecutionAmount > 0 || contractOverrideStale || unclassifiedCount > 0 || !hasExecutionRows }
    var total: Double { rows.reduce(0) { $0 + $1.amount } }
    var difference: Double { bankTotal - total }

    static func build(summary: BankStatementSummary, manual: BankManualAmounts, groups: [ReportGroup]) throws -> Self {
        func fail(_ text: String) -> NSError { NSError(domain: "FundingExpenses", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }
        guard summary.payrollCount > 0 else { throw fail(tr("لا توجد رواتب محفوظة للشهر المختار.", "No payroll is saved for the selected month.")) }
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
        let report = Self(month: summary.month, rows: result, bankTotal: summary.bankTotal + manual.total, unclassifiedCount: summary.unclassifiedCount, hasExecutionRows: summary.executionCount > 0,
                          missingNet: summary.missingNet, missingContractNominal: summary.missingContractNominal, missingExecutionAmount: summary.missingExecutionAmount, contractOverrideStale: summary.contractOverrideStale)
        guard report.total.isFinite, report.bankTotal.isFinite, report.total <= 9_007_199_254_740_991, report.bankTotal <= 9_007_199_254_740_991 else { throw fail(tr("إجمالي الكشف يتجاوز الحد المدعوم.", "The statement total exceeds the supported limit.")) }
        return report
    }

    func sheets(confirmedAt: Date? = nil, headerOverride: String? = nil) -> (ReportSheet, ReportSheet) {
        let columns: [ReportSheet.Column] = [.init(title: "ت", width: 5, kind: .serial), .init(title: "البيان", width: 42, kind: .text), .init(title: "المبلغ", width: 20, kind: .amount)]
        let header = headerOverride ?? UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader
        let prefix: String
        if confirmedAt != nil {
            prefix = isProvisional && difference != 0 ? "معتمد يدوياً مع نواقص وفرق - " : isProvisional ? "معتمد يدوياً مع نواقص - " : difference != 0 ? "معتمد يدوياً مع فرق - " : "معتمد يدوياً - "
        } else { prefix = isProvisional ? "مسودة غير مكتملة - " : "غير معتمد - " }
        let detail = ReportSheet(header: header, title: "\(prefix)تفصيل المصروفات - \(month)", columns: columns,
                                 rows: rows.enumerated().map { [String($0.offset + 1), $0.element.title, ""] }, amounts: rows.map(\.amount), entityDeduction: true).fitted()
        let status = difference == 0 ? (isProvisional ? "تطابق البيانات المتوفرة فقط" : "متطابق") : "غير متطابق - الفرق يحتاج مراجعة"
        let message = unclassifiedCount > 0 ? "\(status) - توجد رواتب غير مصنفة" : (!hasExecutionRows ? "\(status) - لا توجد استقطاعات محفوظة" : status)
        var comparisonRows = [["1", "مجموع المصروفات", ""], ["2", "مجموع كشف البنك", ""], ["3", "الفرق (البنك ناقص المصروفات)", ""]]
        var comparisonValues = [total, bankTotal, difference]
        if unclassifiedCount > 0 { comparisonRows.append(["", "تنبيه: عدد الرواتب غير المصنفة", ""]); comparisonValues.append(Double(unclassifiedCount)) }
        if !hasExecutionRows { comparisonRows.append(["", "تنبيه: عدد سجلات الاستقطاعات المحفوظة", ""]); comparisonValues.append(0) }
        if missingNet > 0 { comparisonRows.append(["", "تنبيه: عدد الرواتب بلا صافي", ""]); comparisonValues.append(Double(missingNet)) }
        if missingContractNominal > 0 { comparisonRows.append(["", "تنبيه: عدد العقود بلا اسمي", ""]); comparisonValues.append(Double(missingContractNominal)) }
        if missingExecutionAmount > 0 { comparisonRows.append(["", "تنبيه: عدد الاستقطاعات بلا مبلغ", ""]); comparisonValues.append(Double(missingExecutionAmount)) }
        if contractOverrideStale { comparisonRows.append(["", "تنبيه: تعديل العقود السابق يحتاج مراجعة", ""]); comparisonValues.append(1) }
        if let confirmedAt {
            let stamp = confirmedAt.formatted(.dateTime.year().month().day().hour().minute().locale(Locale(identifier: "ar_IQ")))
            comparisonRows.append(["", "اعتماد يدوي بتاريخ \(stamp)", ""]); comparisonValues.append(0)
        }
        let comparisonColumns: [ReportSheet.Column] = [.init(title: "ت", width: 5, kind: .serial), .init(title: "البيان", width: 42, kind: .text), .init(title: "المبلغ / العدد", width: 20, kind: .amount)]
        let summary = ReportSheet(header: header, title: "\(prefix)مطابقة كشف البنك - \(month) - \(message)", columns: comparisonColumns,
                                  rows: comparisonRows, amounts: comparisonValues, showTotal: false, entityDeduction: true).fitted()
        return (detail, summary)
    }
}

struct FundingExpensesView: View {
    let month: String
    let revision: String
    @Query private var months: [PayrollMonth]
    @AppStorage("bankStatementManualAmounts") private var manualData = Data()
    @AppStorage(FundingApproval.settingsKey) private var approvalData = Data()
    @State private var report: FundingExpenses?
    @State private var error: String?
    @State private var loading = true
    @State private var savingApproval = false
    @State private var showConfirmApproval = false
    @State private var showConfirmRevocation = false
    @State private var showSavedApproval = false
    @State private var exporter = QuickExporter.shared
    @State private var cache = BankStatementCache.shared

    private var currentRevision: String? {
        guard let record = months.first(where: { $0.name == month }) else { return nil }
        return "\(record.importedAt.timeIntervalSince1970)|\(record.employeeCount)|\(record.deductionCount)"
    }
    private var ledger: FundingApproval.Ledger? {
        _ = approvalData
        return try? FundingApproval.current()
    }
    private var latestApproval: FundingApproval.Record? {
        guard let ledger else { return nil }
        return FundingApproval.latest(in: ledger, month: month)
    }
    private var currentIsApproved: Bool {
        guard let report, let latestApproval, let currentRevision else { return false }
        let header = UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader
        return report == latestApproval.snapshot && currentRevision == latestApproval.sourceRevision && header == latestApproval.header
    }
    private var displayedReport: FundingExpenses? {
        if showSavedApproval { return latestApproval?.snapshot }
        return report ?? latestApproval?.snapshot
    }
    private var displayedApproval: FundingApproval.Record? {
        guard let latestApproval else { return nil }
        return (showSavedApproval || report == nil || currentIsApproved) ? latestApproval : nil
    }
    private var approvalWarning: String {
        guard let report else { return "" }
        var notes: [String] = []
        if report.missingNet > 0 { notes.append(tr("\(Num.format(report.missingNet)) رواتب بلا صافي", "\(report.missingNet) payroll records lack net pay")) }
        if report.missingContractNominal > 0 { notes.append(tr("\(Num.format(report.missingContractNominal)) عقود بلا اسمي", "\(report.missingContractNominal) contracts lack nominal pay")) }
        if report.missingExecutionAmount > 0 { notes.append(tr("\(Num.format(report.missingExecutionAmount)) استقطاعات بلا مبلغ", "\(report.missingExecutionAmount) deductions lack an amount")) }
        if report.unclassifiedCount > 0 { notes.append(tr("\(Num.format(report.unclassifiedCount)) رواتب بلا تصنيف للدفع", "\(report.unclassifiedCount) salaries have unclassified payment methods")) }
        if report.contractOverrideStale { notes.append(tr("تعديل العقود السابق غير مستخدم ويحتاج مراجعة", "The previous contract adjustment is not used and requires review")) }
        if !report.hasExecutionRows { notes.append(tr("لا توجد استقطاعات محفوظة للشهر", "No deductions are saved for the month")) }
        if report.difference != 0 { notes.append(tr("فرق بين البنك والمصروفات: \(Money.format(report.difference))", "Bank-to-expense difference: \(Money.format(report.difference))")) }
        if notes.isEmpty { return tr("أؤكد أنني راجعت تفاصيل هذا الكشف وأعتمد المبالغ المعروضة.", "I have reviewed this statement and manually confirm the displayed amounts.") }
        return tr("يحتوي الكشف على: \(notes.joined(separator: "، ")). المبالغ من البيانات المتوفرة فقط. بتأكيدك تعتمد هذه اللقطة يدوياً رغم الملاحظات.", "This statement has: \(notes.joined(separator: ", ")). Amounts reflect available data only. Confirming manually approves this snapshot despite these issues.")
    }

    var body: some View {
        List {
            if loading { ProgressView(tr("جارٍ حساب المصروفات", "Calculating expenses")) }
            else if ledger == nil { Text(FundingApproval.Failure.invalid.localizedDescription).foregroundStyle(.red) }
            else if let report = displayedReport {
                Section(tr("اعتماد الكشف", "Statement approval")) {
                    if let approval = displayedApproval {
                        Label(tr("معتمد يدوياً \(approval.confirmedAt.formatted(date: .numeric, time: .shortened))", "Manually approved \(approval.confirmedAt.formatted(date: .numeric, time: .shortened))"), systemImage: "checkmark.seal.fill").foregroundStyle(.green)
                        if showSavedApproval || self.report == nil {
                            Text(tr("هذه لقطة محفوظة وقت الاعتماد، وقد لا تعكس البيانات الحالية.", "This snapshot was saved at approval and may not reflect current data.")).font(.footnote).foregroundStyle(.orange)
                        }
                        if report.isProvisional || report.difference != 0 { Text(tr("اعتُمدت اللقطة يدوياً مع الملاحظات والفرق الموضحين أدناه.", "This snapshot was manually approved with the issues and difference shown below.")).foregroundStyle(.orange) }
                        if showSavedApproval, self.report != nil { Button(tr("عرض البيانات الحالية", "View current data")) { showSavedApproval = false } }
                        Button(tr("إلغاء الاعتماد", "Revoke approval"), role: .destructive) { showConfirmRevocation = true }.disabled(savingApproval)
                    } else {
                        Label(tr("غير معتمد", "Not approved"), systemImage: "doc.text").foregroundStyle(.secondary)
                        if latestApproval != nil {
                            Text(tr("تغيّرت بيانات الكشف أو مصدره بعد الاعتماد؛ يلزم مراجعته واعتماده من جديد.", "The statement or its source changed after approval; review and approve it again.")).foregroundStyle(.orange)
                            Button(tr("عرض آخر نسخة معتمدة", "View last approved snapshot")) { showSavedApproval = true }
                        }
                        Button(tr("تأكيد صحة الكشف واعتماده", "Review and approve statement"), systemImage: "checkmark.seal") { showConfirmApproval = true }
                            .disabled(savingApproval || self.report == nil)
                    }
                    if self.report == nil, let error { Text(tr("تُعرض النسخة المعتمدة المحفوظة: \(error)", "Showing the saved approved snapshot: \(error)")).font(.footnote).foregroundStyle(.secondary) }
                }
                if report.isProvisional {
                    Section(tr("بيانات تحتاج مراجعة", "Data requiring review")) {
                        Label(displayedApproval == nil ? tr("مسودة غير مكتملة: المبالغ المعروضة للبيانات المتوفرة فقط ولا تمثل مبلغاً نهائياً.", "Incomplete draft: amounts reflect available data only and are not final.") : tr("اعتماد يدوي ببيانات غير مكتملة: المبالغ للبيانات المتوفرة فقط.", "Manual approval with incomplete data: amounts reflect available data only."), systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.orange)
                        if report.missingNet > 0 { Text(tr("\(Num.format(report.missingNet)) سجل رواتب بلا صافي. راجع ملف الرواتب وأعد استيراده.", "\(report.missingNet) payroll records have no net amount. Review and reimport the payroll file.")) }
                        if report.missingContractNominal > 0 {
                            Text(tr("\(Num.format(report.missingContractNominal)) عقد بلا اسمي؛ حصة ١٢٪ محسوبة من الاسمي المتوفر فقط.", "\(report.missingContractNominal) contracts have no nominal pay; the 12% uses available nominal pay only."))
                            if !showSavedApproval, self.report != nil, let record = months.first(where: { $0.name == month }) {
                                NavigationLink(tr("عرض العقود بلا اسمي", "View contracts with missing nominal pay")) {
                                    ContractNominalReviewView(month: month,
                                        sourceKey: "\(String(describing: Optional(record.persistentModelID)))|\(record.name)|\(record.importedAt.timeIntervalSince1970)",
                                        expected: report.missingContractNominal)
                                }
                            }
                        }
                        if report.missingExecutionAmount > 0 { Text(tr("\(Num.format(report.missingExecutionAmount)) استقطاع بلا مبلغ؛ راجع ملف جهات التنفيذ.", "\(report.missingExecutionAmount) deductions have no amount; review the execution file.")) }
                        if !report.hasExecutionRows { Text(tr("لا توجد سجلات استقطاعات محفوظة للشهر.", "No deduction records are saved for this month.")) }
                        if report.contractOverrideStale { Text(tr("تغيّر أساس حساب العقود؛ المبلغ اليدوي السابق غير مستخدم. راجعه من كشف البنك.", "The contract basis changed; the previous manual amount is not used. Review it in the bank statement.")) }
                    }
                }
                Section(tr("المطابقة مع كشف البنك", "Bank statement comparison")) {
                    LabeledContent(report.isProvisional ? tr("مجموع المصروفات المتوفرة", "Available expenses") : tr("مجموع المصروفات", "Total expenses"), value: Money.format(report.total))
                    LabeledContent(report.isProvisional ? tr("مجموع البنك المتوفر", "Available bank total") : tr("مجموع كشف البنك", "Bank statement total"), value: Money.format(report.bankTotal))
                    LabeledContent(tr("الفرق: البنك ناقص المصروفات", "Difference: bank minus expenses"), value: Money.format(report.difference))
                    Label(report.difference == 0 ? (report.isProvisional ? tr("تطابق حسابي للبيانات المتوفرة فقط", "Numerical match for available data only") : tr("المبلغان متطابقان", "Amounts match")) : tr("المبلغان غير متطابقين؛ راجع الجهات والمبالغ.", "Amounts differ; review entities and amounts."), systemImage: report.difference == 0 && !report.isProvisional ? "checkmark.circle" : "exclamationmark.triangle")
                        .foregroundStyle(report.difference == 0 && !report.isProvisional ? .green : .orange)
                    if report.unclassifiedCount > 0 { Text(tr("توجد رواتب غير مصنفة حسب طريقة الدفع؛ راجعها.", "Some salaries have no classified payment method; review them.")).foregroundStyle(.orange) }
                    if !report.hasExecutionRows { Text(tr("لا توجد سجلات استقطاعات محفوظة للشهر؛ تأكد من ملف جهات التنفيذ.", "No deduction records are saved for this month; check the execution file.")).foregroundStyle(.orange) }
                    Text(tr("تساوي المبلغين حسابياً لا يؤكد اكتمال ملفات المصدر؛ راجع بيانات الشهر.", "A numerical match does not verify source-file completeness; review the monthly data.")).font(.footnote).foregroundStyle(.secondary)
                }
                .accessibilityIdentifier("fundingExpensesComparison")
                Section(tr("تفاصيل المصروفات", "Expense details")) {
                    ForEach(report.rows) { row in LabeledContent(row.title, value: Money.format(row.amount)) }
                }
                Section {
                    Button(displayedApproval != nil ? tr("تصدير المعتمد Excel", "Export approved Excel") : report.isProvisional ? tr("تصدير مسودة Excel", "Export Excel draft") : tr("تصدير Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export(.excel) }.accessibilityIdentifier("fundingExpensesExcel")
                    Button(displayedApproval != nil ? tr("تصدير المعتمد PDF", "Export approved PDF") : report.isProvisional ? tr("تصدير مسودة PDF", "Export PDF draft") : tr("تصدير PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export(.pdf) }.accessibilityIdentifier("fundingExpensesPDF")
                }.disabled(exporter.isWorking)
            } else if let error { Text(error).foregroundStyle(.red) }
        }
        .navigationTitle(tr("تفصيل المصروفات", "Expense details"))
        .navigationBarTitleDisplayMode(.inline)
        .task(id: "\(month)|\(revision)|\(cache.revision)|\(manualData.base64EncodedString())") { await load() }
        .overlay { if exporter.isWorking { ProgressView(tr("جارٍ تجهيز الكشف", "Preparing statement")).padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16)) } }
        .alert(tr("الكشف", "Statement"), isPresented: Binding(get: { exporter.error != nil }, set: { if !$0 { exporter.error = nil } })) { Button(tr("حسناً", "OK"), role: .cancel) {} } message: { Text(exporter.error ?? "") }
        .confirmationDialog(tr("تأكيد اعتماد الكشف", "Confirm statement approval"), isPresented: $showConfirmApproval, titleVisibility: .visible) {
            Button(tr("راجعت الكشف وأعتمده يدوياً", "I reviewed and manually approve")) { Task { await approve() } }
            Button(tr("إلغاء", "Cancel"), role: .cancel) { }
        } message: { Text(approvalWarning) }
        .confirmationDialog(tr("إلغاء اعتماد الكشف؟", "Revoke statement approval?"), isPresented: $showConfirmRevocation, titleVisibility: .visible) {
            Button(tr("إلغاء الاعتماد", "Revoke approval"), role: .destructive) { revokeApproval() }
            Button(tr("رجوع", "Back"), role: .cancel) { }
        } message: { Text(tr("سيُلغى اعتماد النسخة، وتبقى بياناتها في سجل الاعتمادات.", "The approval will be revoked; its snapshot remains in the approval ledger.")) }
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
    @MainActor private func approve() async {
        guard !savingApproval, !showSavedApproval, let reviewed = report, let sourceRevision = currentRevision else { return }
        savingApproval = true
        defer { savingApproval = false }
        do {
            let previousData = try FundingApproval.currentData()
            let header = UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader
            let fresh = try await calculate()
            guard fresh == reviewed, currentRevision == sourceRevision, previousData == (try FundingApproval.currentData()),
                  header == (UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader) else {
                report = fresh
                throw NSError(domain: "FundingExpenses", code: 6, userInfo: [NSLocalizedDescriptionKey: tr("تغيّرت البيانات بعد المراجعة؛ راجع الكشف المحدّث قبل الاعتماد.", "Data changed after review; review the updated statement before approving.")])
            }
            var ledger = try FundingApproval.current()
            ledger.records.append(.init(id: UUID(), confirmedAt: .now, snapshot: fresh, sourceRevision: sourceRevision, header: header))
            let data = try FundingApproval.encoded(ledger)
            UserDefaults.standard.set(data, forKey: FundingApproval.settingsKey)
            BackupManager.shared.markSettingChanged(FundingApproval.settingsKey)
            showSavedApproval = false
        } catch { exporter.error = error.localizedDescription }
    }
    @MainActor private func revokeApproval() {
        guard !savingApproval, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting, !BackupManager.shared.isWorking, let approval = displayedApproval else { return }
        do {
            var ledger = try FundingApproval.current()
            guard let last = FundingApproval.latest(in: ledger, month: month), last.id == approval.id,
                  let index = ledger.records.firstIndex(where: { $0.id == approval.id }) else { throw FundingApproval.Failure.invalid }
            ledger.records[index].revokedAt = max(Date(), approval.confirmedAt)
            UserDefaults.standard.set(try FundingApproval.encoded(ledger), forKey: FundingApproval.settingsKey)
            BackupManager.shared.markSettingChanged(FundingApproval.settingsKey)
            showSavedApproval = false
        } catch { exporter.error = error.localizedDescription }
    }
    @MainActor private func export(_ format: ExportFormat) {
        exporter.run {
            let currentApprovalData = try FundingApproval.currentData()
            let displayed = displayedReport
            let approved = displayedApproval
            let fresh: FundingExpenses
            if (showSavedApproval || report == nil), let approved {
                fresh = approved.snapshot
            } else {
                fresh = try await calculate()
                guard fresh == report, approved == displayedApproval, currentApprovalData == (try FundingApproval.currentData()) else {
                    report = fresh
                    throw NSError(domain: "FundingExpenses", code: 5, userInfo: [NSLocalizedDescriptionKey: tr("تغيّرت بيانات الكشف؛ راجع المبالغ المحدّثة ثم أعد التصدير.", "Statement data changed; review the updated amounts and export again.")])
                }
            }
            guard fresh == displayed, currentApprovalData == (try FundingApproval.currentData()) else { throw FundingApproval.Failure.invalid }
            let (detail, comparison) = fresh.sheets(confirmedAt: approved?.confirmedAt, headerOverride: approved?.header)
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
