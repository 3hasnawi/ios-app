import SwiftUI
import SwiftData

/// مصادر كشف حساب البنك للشهر. ملف الاستقطاعات يُعرض مستقلاً حتى لا يختلط
/// بسطر «مجموع الاستقطاعات» القادم من ورقة الرواتب.
nonisolated struct BankStatementSummary: Sendable {
    let month: String
    var payrollCount = 0
    var netSalary = 0.0
    var payrollDeductions = 0.0
    var retirement = 0.0
    var executionDeductions = 0.0
    var executionCount = 0
    var missingNet = 0
    var missingDeductionTotal = 0
    var breakdown: [String: Double] = [:]

    var nonRetirementDeductions: Double { payrollDeductions - retirement }
    var totalWithoutSeparateExecution: Double { netSalary + nonRetirementDeductions }
    var totalWithSeparateExecution: Double { totalWithoutSeparateExecution + executionDeductions }
}

@ModelActor
actor BankStatementEngine {
    func summary(month: String) throws -> BankStatementSummary {
        var result = BankStatementSummary(month: month)
        var payroll = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        payroll.propertiesToFetch = [\.netSalary, \.detailsData, \.itemsData]
        for (index, employee) in try modelContext.fetch(payroll).enumerated() {
            if index.isMultiple(of: 512) { try Task.checkCancellation() }
            result.payrollCount += 1
            if let net = employee.netSalary { result.netSalary += net }
            else { result.missingNet += 1 }
            let details = employee.details
            let items = employee.items
            func amount(_ keys: [String]) -> Double? {
                for key in keys {
                    if let value = details[key].flatMap(ArabicText.number(from:)) ?? items[key] { return value }
                    let normalized = ArabicText.normalize(key)
                    if let raw = details.first(where: { ArabicText.normalize($0.key) == normalized })?.value,
                       let value = ArabicText.number(from: raw) { return value }
                    if let value = items.first(where: { ArabicText.normalize($0.key) == normalized })?.value { return value }
                }
                return nil
            }
            if let total = amount(["مجموع الاستقطاعات"]) { result.payrollDeductions += total }
            else { result.missingDeductionTotal += 1 }
            result.retirement += amount(["توقيفات تقاعدية", "التوقيفات التقاعدية"]) ?? 0
            for (sources, label) in [(["صندوق الشهداء", "صندوق شهداء الشرطة"], "صندوق شهداء الشرطة"),
                                     (["الرعاية الصحية", "رعاية صحية"], "الرعاية الصحية"),
                                     (["نشاط رياضي", "النشاط الرياضي"], "النشاط الرياضي"),
                                     (["الضريبة", "ضريبة"], "الضريبة"),
                                     (["حماية الاجتماعية", "الحماية الاجتماعية"], "الحماية الاجتماعية")] {
                result.breakdown[label, default: 0] += amount(sources) ?? 0
            }
            for (title, value) in items where FoodContractReport.applies(to: title) {
                result.breakdown["شركة عراق الجوال", default: 0] += value
            }
        }
        var deductions = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
        deductions.propertiesToFetch = [\.title, \.amount]
        for (index, item) in try modelContext.fetch(deductions).enumerated() {
            if index.isMultiple(of: 512) { try Task.checkCancellation() }
            let key = ArabicText.normalize(item.title)
            if key.contains(ArabicText.normalize("توقيفات تقاعدية")) { continue }
            result.executionDeductions += item.amount ?? 0
            result.executionCount += 1
        }
        let named = result.breakdown.values.reduce(0, +)
        let other = result.nonRetirementDeductions - named
        if abs(other) >= 1 { result.breakdown["بنود أخرى / فرق المطابقة"] = other }
        return result
    }
}

/// طريقة إضافة ملف جهات التنفيذ إلى مجموع ورقة الرواتب.
enum BankExecutionRule: String, CaseIterable, Identifiable {
    case review, included, separate
    var id: String { rawValue }
    var title: String {
        switch self {
        case .review: tr("أحتاج مطابقة المصدر", "Source needs review")
        case .included: tr("ضمن مجموع الاستقطاعات", "Included in payroll deductions")
        case .separate: tr("يُضاف من ملف منفصل", "Added from a separate file")
        }
    }
}

struct BankStatementView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @AppStorage("bankStatementMonth") private var selectedMonth = ""
    @AppStorage("bankStatementAccountName") private var accountName = ""
    @AppStorage("bankStatementIBAN") private var iban = ""
    @AppStorage("bankStatementExecutionRule") private var ruleRaw = BankExecutionRule.review.rawValue
    @State private var summary: BankStatementSummary?
    @State private var loadError: String?
    @State private var showSettings = false

    private var month: PayrollMonth? { months.first { $0.name == selectedMonth } ?? months.first }
    private var rule: BankExecutionRule { BankExecutionRule(rawValue: ruleRaw) ?? .review }
    private var loadKey: String { "\(month?.name ?? "")|\(month?.importedAt.timeIntervalSince1970 ?? 0)" }

    var body: some View {
        NavigationStack {
            List {
                if let summary {
                    Section {
                        VStack(alignment: .leading, spacing: 10) {
                            Text(accountName.isEmpty ? tr("حساب البنك", "Bank account") : accountName)
                                .font(.headline).foregroundStyle(.white)
                            Text(rule == .review ? tr("اختر مصدر جهات التنفيذ لاعتماد المبلغ", "Choose the execution source to confirm the amount")
                                                  : tr("المبلغ المطلوب للشهر", "Monthly amount"))
                                .font(.caption).foregroundStyle(.white.opacity(0.8))
                            if rule == .review {
                                Text(tr("دون إضافة ملف الجهات: \(Money.format(summary.totalWithoutSeparateExecution))",
                                        "Without separate execution file: \(Money.format(summary.totalWithoutSeparateExecution))"))
                                    .font(.subheadline.weight(.semibold)).foregroundStyle(.white)
                                Text(tr("مع إضافة ملف الجهات: \(Money.format(summary.totalWithSeparateExecution))",
                                        "With separate execution file: \(Money.format(summary.totalWithSeparateExecution))"))
                                    .font(.subheadline.weight(.semibold)).foregroundStyle(.white)
                            } else {
                                Text(Money.format(rule == .separate ? summary.totalWithSeparateExecution : summary.totalWithoutSeparateExecution))
                                    .font(.system(size: 30, weight: .bold, design: .rounded))
                                    .monospacedDigit().foregroundStyle(.white)
                            }
                            if !iban.isEmpty { Text(iban).font(.footnote.monospaced()).foregroundStyle(.white.opacity(0.8)).privacySensitive() }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(18)
                        .background(Color(red: 0.07, green: 0.25, blue: 0.26), in: RoundedRectangle(cornerRadius: 22))
                        .listRowInsets(EdgeInsets())
                        .listRowBackground(Color.clear)
                    }
                    Section(tr("مصادر المبلغ", "Amount sources")) {
                        LabeledContent(tr("صافي الرواتب", "Net payroll"), value: Money.format(summary.netSalary))
                        LabeledContent(tr("استقطاعات ورقة الرواتب", "Payroll deductions"), value: Money.format(summary.payrollDeductions))
                        LabeledContent(tr("التوقيفات التقاعدية المستبعدة", "Excluded retirement deductions"), value: Money.format(summary.retirement))
                        LabeledContent(tr("استقطاعات جهات التنفيذ", "Execution deductions"), value: Money.format(summary.executionDeductions))
                        Text(rule == .review
                             ? tr("حدد هل مبالغ ملف الجهات داخلة في مجموع ورقة الرواتب قبل اعتماد الكشف.", "Check whether execution records are already included in payroll before confirming the statement.")
                             : rule == .included
                             ? tr("جهات التنفيذ معروضة للمطابقة، ولا تُضاف مرتين إلى المجموع.", "Execution deductions are shown for reconciliation and aren't added twice.")
                             : tr("جهات التنفيذ من الملف المنفصل مضافة إلى المجموع.", "Execution deductions from the separate file are added to the total."))
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                    Section(tr("تفصيل الاستقطاعات", "Deduction breakdown")) {
                        ForEach(summary.breakdown.keys.sorted(), id: \.self) { key in
                            LabeledContent(key, value: Money.format(summary.breakdown[key]))
                        }
                    }
                    if summary.missingNet > 0 || summary.missingDeductionTotal > 0 {
                        Section {
                            Text(tr("توجد \(Num.format(summary.missingNet)) رواتب بلا صافي و\(Num.format(summary.missingDeductionTotal)) بلا مجموع استقطاعات؛ راجع مصدر البيانات قبل اعتماد المبلغ.",
                                    "\(summary.missingNet) payroll rows lack net pay and \(summary.missingDeductionTotal) lack deduction totals; review the source before using this amount."))
                                .foregroundStyle(.orange)
                        }
                    }
                } else if let loadError {
                    ContentUnavailableView(tr("تعذّر حساب الكشف", "Couldn't calculate statement"), systemImage: "exclamationmark.triangle",
                                           description: Text(loadError))
                } else if months.isEmpty {
                    ContentUnavailableView(tr("لا توجد بيانات رواتب", "No payroll data"), systemImage: "building.columns",
                                           description: Text(tr("استورد الرواتب أولاً من الإعدادات.", "Import payroll from Settings first.")))
                } else {
                    ProgressView().frame(maxWidth: .infinity)
                }
            }
            .listStyle(.insetGrouped)
            .navigationTitle(tr("كشف حساب البنك", "Bank account statement"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Menu {
                        ForEach(months) { candidate in
                            Button(candidate.name) { selectedMonth = candidate.name }
                        }
                    } label: {
                        Label(month?.name ?? tr("الشهر", "Month"), systemImage: "calendar")
                    }
                    .disabled(months.isEmpty)
                }
                ToolbarItem(placement: .topBarLeading) {
                    Button(tr("إعداد الحساب", "Account settings"), systemImage: "slider.horizontal.3") { showSettings = true }
                }
            }
            .sheet(isPresented: $showSettings) {
                NavigationStack {
                    Form {
                        Section(tr("الحساب", "Account")) {
                            TextField(tr("اسم الحساب", "Account name"), text: $accountName)
                            TextField("IBAN", text: $iban).textInputAutocapitalization(.characters)
                        }
                        Section(tr("استقطاعات جهات التنفيذ", "Execution deductions")) {
                            Picker(tr("طريقة الاحتساب", "Calculation rule"), selection: $ruleRaw) {
                                ForEach(BankExecutionRule.allCases) { option in Text(option.title).tag(option.rawValue) }
                            }
                        } footer: {
                            Text(tr("الصافي ومجموع الاستقطاعات والتوقيفات التقاعدية من ورقة الرواتب. ملف جهات التنفيذ يُضاف فقط إذا لم تكن مبالغه ضمن مجموع الاستقطاعات.",
                                    "Net pay, deduction total and retirement deductions come from payroll. Add execution records only if they aren't included in the payroll deduction total."))
                        }
                    }
                    .navigationTitle(tr("مصادر كشف البنك", "Bank statement sources"))
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar { ToolbarItem(placement: .confirmationAction) { Button(tr("تم", "Done")) { showSettings = false } } }
                }
            }
            .task(id: loadKey) {
                guard let month else { summary = nil; return }
                summary = nil
                let container = SharedModel.container
                let name = month.name
                do {
                    let value = try await Task.detached(priority: .userInitiated) {
                        try await BankStatementEngine(modelContainer: container).summary(month: name)
                    }.value
                    guard !Task.isCancelled else { return }
                    summary = value
                    loadError = nil
                } catch is CancellationError {
                    return
                } catch {
                    guard !Task.isCancelled else { return }
                    loadError = error.localizedDescription
                }
            }
            .onChange(of: accountName) { BackupManager.shared.markChanged() }
            .onChange(of: iban) { BackupManager.shared.markChanged() }
            .onChange(of: ruleRaw) { BackupManager.shared.markChanged() }
        }
    }
}
