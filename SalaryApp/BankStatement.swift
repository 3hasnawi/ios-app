import SwiftUI
import SwiftData

/// ملف جهات التنفيذ داخل مجموع استقطاعات الرواتب؛ يُعرض للمطابقة فقط.
nonisolated struct BankStatementSummary: Sendable {
    let month: String
    var payrollCount = 0
    var netSalary = 0.0
    var payrollDeductions = 0.0
    var retirement = 0.0
    var contractNominal = 0.0
    var contractCount = 0
    var missingContractNominal = 0
    var executionDeductions = 0.0
    var executionCount = 0
    var missingNet = 0
    var missingDeductionTotal = 0
    var breakdown: [String: Double] = [:]

    var nonRetirementDeductions: Double { payrollDeductions - retirement }
    var contractContribution: Double { contractNominal * 0.17 }
    var bankTotal: Double { netSalary + nonRetirementDeductions + contractContribution }
}

@ModelActor
actor BankStatementEngine {
    func summary(month: String) throws -> BankStatementSummary {
        var result = BankStatementSummary(month: month)
        var payroll = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        payroll.propertiesToFetch = [\.rank, \.netSalary, \.detailsData, \.itemsData]
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
            if FundReport.category(of: employee.rank) == 3 {
                result.contractCount += 1
                if let nominal = amount(["الاسمي"]) { result.contractNominal += nominal }
                else { result.missingContractNominal += 1 }
            }
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

struct BankStatementView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @AppStorage("bankStatementMonth") private var selectedMonth = ""
    @AppStorage("bankStatementAccountName") private var accountName = ""
    @AppStorage("bankStatementIBAN") private var iban = ""
    @State private var summary: BankStatementSummary?
    @State private var loadError: String?
    @State private var showSettings = false

    private var month: PayrollMonth? { months.first { $0.name == selectedMonth } ?? months.first }
    private var loadKey: String { "\(month?.name ?? "")|\(month?.importedAt.timeIntervalSince1970 ?? 0)" }

    var body: some View {
        NavigationStack {
            List {
                if let summary {
                    Section {
                        VStack(alignment: .leading, spacing: 10) {
                            Text(accountName.isEmpty ? tr("حساب البنك", "Bank account") : accountName)
                                .font(.headline).foregroundStyle(.white)
                            Text(tr("المبلغ المطلوب للشهر", "Monthly amount"))
                                .font(.caption).foregroundStyle(.white.opacity(0.8))
                            Text(Money.format(summary.bankTotal))
                                .font(.system(size: 30, weight: .bold, design: .rounded))
                                .monospacedDigit().foregroundStyle(.white)
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
                        LabeledContent(tr("مجموع الاسمي للعقود", "Contract nominal total"), value: Money.format(summary.contractNominal))
                        LabeledContent(tr("التقاعد والضمان الاجتماعي", "Pension and social security"), value: Money.format(summary.contractContribution))
                        Text(tr("17% من اسمي العقود · عددهم \(Num.format(summary.contractCount))", "17% of contract nominal pay · \(summary.contractCount) employees"))
                            .font(.footnote).foregroundStyle(.secondary)
                        LabeledContent(tr("استقطاعات جهات التنفيذ", "Execution deductions"), value: Money.format(summary.executionDeductions))
                        Text(tr("جهات التنفيذ ضمن مجموع الاستقطاعات؛ مبلغ الملف معروض للمطابقة فقط ولا يُضاف مرة ثانية.",
                                "Execution deductions are included in payroll deductions; the file amount is shown only for reconciliation."))
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                    Section(tr("تفصيل الاستقطاعات", "Deduction breakdown")) {
                        ForEach(summary.breakdown.keys.sorted(), id: \.self) { key in
                            LabeledContent(key, value: Money.format(summary.breakdown[key]))
                        }
                    }
                    if summary.missingNet > 0 || summary.missingDeductionTotal > 0 || summary.missingContractNominal > 0 {
                        Section {
                            Text(tr("توجد \(Num.format(summary.missingNet)) رواتب بلا صافي، و\(Num.format(summary.missingDeductionTotal)) بلا مجموع استقطاعات، و\(Num.format(summary.missingContractNominal)) عقود بلا راتب اسمي؛ راجع مصدر البيانات قبل اعتماد المبلغ.",
                                    "\(summary.missingNet) payroll rows lack net pay, \(summary.missingDeductionTotal) lack deduction totals, and \(summary.missingContractNominal) contract rows lack nominal pay; review the source before using this amount."))
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
                        Section(tr("طريقة الاحتساب", "Calculation")) {
                            Text(tr("صافي الرواتب + مجموع الاستقطاعات − التوقيفات التقاعدية + التقاعد والضمان الاجتماعي (17% من مجموع اسمي العقود). جهات التنفيذ داخلة في مجموع الاستقطاعات.",
                                    "Net payroll + payroll deductions − retirement deductions + pension and social security (17% of contract nominal pay). Execution deductions are already included."))
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
        }
    }
}
