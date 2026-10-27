import SwiftUI
import SwiftData

/// كشف البنك من بنود الرواتب المحددة، بلا استخدام «مجموع الاستقطاعات».
nonisolated struct BankStatementSummary: Sendable {
    let month: String
    var payrollCount = 0
    var cashNet = 0.0
    var cardNet = 0.0
    var unclassifiedNet = 0.0
    var unclassifiedCount = 0
    var bankDeductions = 0.0
    var contractNominal = 0.0
    var contractRetirement = 0.0
    var contractCount = 0
    var missingContractNominal = 0
    var executionDeductions = 0.0
    var executionCount = 0
    var missingExecutionAmount = 0
    var missingNet = 0
    var breakdown: [String: Double] = [:]

    var netSalary: Double { cashNet + cardNet + unclassifiedNet }
    var contractContribution: Double { contractNominal * 0.12 + contractRetirement }
    var executionDifference: Double { bankDeductions - executionDeductions }
    var executionMatched: Bool { executionCount > 0 && missingExecutionAmount == 0 && abs(executionDifference) < 1 }
    var bankTotal: Double { netSalary + bankDeductions + breakdown.values.reduce(0, +) + contractContribution }
}

/// مبالغ تُدخل يدوياً لكل شهر ولا تتأثر بإعادة استيراد ملف الرواتب.
nonisolated struct BankManualAmounts: Codable, Equatable {
    var accumulatedLeave = 0.0
    var employeeRewards = 0.0
    var singleList = 0.0

    var total: Double { accumulatedLeave + employeeRewards + singleList }
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
            if let net = employee.netSalary {
                let payment = ArabicText.normalize(details["طريقة الدفع"] ?? "")
                if payment.contains("بطاق") || payment.contains("كارت") || payment.contains("ماستر") { result.cardNet += net }
                else if payment.contains("كاش") || payment.contains("نقد") { result.cashNet += net }
                else { result.unclassifiedNet += net; result.unclassifiedCount += 1 }
            } else { result.missingNet += 1 }
            result.bankDeductions += amount(["مجموع استقطاعات المصارف"]) ?? 0
            // «عقد» غير مشمول؛ المعادلة تخص الرتبة المكتوبة «عقود» فقط.
            if ArabicText.normalize(employee.rank) == ArabicText.normalize("عقود") {
                result.contractCount += 1
                if let nominal = amount(["الاسمي"]) { result.contractNominal += nominal }
                else { result.missingContractNominal += 1 }
                result.contractRetirement += amount(["توقيفات تقاعدية", "التوقيفات التقاعدية"]) ?? 0
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
        deductions.propertiesToFetch = [\.amount]
        for (index, item) in try modelContext.fetch(deductions).enumerated() {
            if index.isMultiple(of: 512) { try Task.checkCancellation() }
            if let value = item.amount { result.executionDeductions += value }
            else { result.missingExecutionAmount += 1 }
            result.executionCount += 1
        }
        return result
    }
}

struct BankStatementView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @AppStorage("bankStatementMonth") private var selectedMonth = ""
    @AppStorage("bankStatementAccountName") private var accountName = ""
    @AppStorage("bankStatementIBAN") private var iban = ""
    @AppStorage("bankStatementManualAmounts") private var manualAmountsData = Data()
    @State private var summary: BankStatementSummary?
    @State private var loadError: String?
    @State private var showSettings = false

    private var month: PayrollMonth? { months.first { $0.name == selectedMonth } ?? months.first }
    private var loadKey: String { "\(month?.name ?? "")|\(month?.importedAt.timeIntervalSince1970 ?? 0)" }
    private var manualAmounts: BankManualAmounts { amounts(for: month?.name ?? "") }

    private func amounts(for name: String) -> BankManualAmounts {
        guard !name.isEmpty else { return BankManualAmounts() }
        let saved = (try? JSONDecoder().decode([String: BankManualAmounts].self, from: manualAmountsData)) ?? [:]
        return saved[name] ?? BankManualAmounts()
    }

    private func amountBinding(_ field: WritableKeyPath<BankManualAmounts, Double>, month name: String) -> Binding<Double> {
        Binding(
            get: { amounts(for: name)[keyPath: field] },
            set: { value in
                guard !name.isEmpty, value.isFinite else { return }
                var saved = (try? JSONDecoder().decode([String: BankManualAmounts].self, from: manualAmountsData)) ?? [:]
                var entry = saved[name] ?? BankManualAmounts()
                entry[keyPath: field] = max(0, value)
                saved[name] = entry
                if let encoded = try? JSONEncoder().encode(saved) { manualAmountsData = encoded }
            }
        )
    }

    var body: some View {
        NavigationStack {
            List {
                if let summary {
                    Section {
                        VStack(alignment: .leading, spacing: 10) {
                            Text(accountName.isEmpty ? tr("حساب البنك", "Bank account") : accountName)
                                .font(.headline).foregroundStyle(.white)
                            Text(summary.executionMatched
                                 ? tr("المبلغ المطلوب للشهر", "Monthly amount")
                                 : tr("مبلغ أولي · يحتاج مطابقة التنفيذ", "Provisional amount · reconcile execution deductions"))
                                .font(.caption).foregroundStyle(.white.opacity(0.8))
                            Text(Money.format(summary.bankTotal + manualAmounts.total))
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
                    Section(tr("صافي الرواتب", "Net payroll")) {
                        LabeledContent(tr("كاش", "Cash"), value: Money.format(summary.cashNet))
                        LabeledContent(tr("بطاقة", "Card"), value: Money.format(summary.cardNet))
                        if summary.unclassifiedCount > 0 {
                            LabeledContent(tr("غير مصنف", "Unclassified"), value: Money.format(summary.unclassifiedNet))
                        }
                        LabeledContent(tr("مجموع الصافي", "Total net pay"), value: Money.format(summary.netSalary))
                    }
                    Section(tr("مبالغ تُضاف للرواتب", "Amounts added to payroll")) {
                        LabeledContent(tr("الإجازات المتراكمة", "Accumulated leave"), value: Money.format(manualAmounts.accumulatedLeave))
                        LabeledContent(tr("مكافآت منتسبين", "Employee rewards"), value: Money.format(manualAmounts.employeeRewards))
                        LabeledContent(tr("قائمة مفردة", "Single list"), value: Money.format(manualAmounts.singleList))
                        Text(tr("مبالغ هذا الشهر تُدخل من إعداد الحساب.", "Enter this month's amounts in Account settings."))
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                    Section(tr("الاستقطاعات المحتسبة", "Included deductions")) {
                        LabeledContent(tr("مجموع استقطاعات المصارف", "Bank deductions"), value: Money.format(summary.bankDeductions))
                        ForEach(["الرعاية الصحية", "صندوق شهداء الشرطة", "النشاط الرياضي", "الضريبة", "الحماية الاجتماعية", "شركة عراق الجوال"], id: \.self) { key in
                            LabeledContent(key, value: Money.format(summary.breakdown[key] ?? 0))
                        }
                    }
                    Section(tr("التقاعد والضمان الاجتماعي", "Pension and social security")) {
                        LabeledContent(tr("مجموع الاسمي للعقود", "Contract nominal total"), value: Money.format(summary.contractNominal))
                        LabeledContent(tr("12% من اسمي العقود", "12% of contract nominal pay"), value: Money.format(summary.contractNominal * 0.12))
                        LabeledContent(tr("توقيفات عقود التقاعدية", "Contract retirement deductions"), value: Money.format(summary.contractRetirement))
                        LabeledContent(tr("المبلغ المحتسب", "Included amount"), value: Money.format(summary.contractContribution))
                        Text(tr("مجموع الاسمي أساس للحساب فقط · رتبة عقود حصراً · عددهم \(Num.format(summary.contractCount))",
                                "Nominal total is a calculation base only · عقود rank only · \(summary.contractCount) employees"))
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                    Section(tr("مطابقة الاستقطاعات التنفيذية", "Execution deduction reconciliation")) {
                        LabeledContent(tr("ملف جهات التنفيذ", "Execution file"), value: Money.format(summary.executionDeductions))
                        if summary.executionCount == 0 {
                            Label(tr("لا توجد قيود تنفيذية للمطابقة في هذا الشهر.", "No execution entries are available for this month."), systemImage: "exclamationmark.triangle")
                                .foregroundStyle(.orange)
                        } else if summary.missingExecutionAmount > 0 {
                            Label(tr("المطابقة غير مكتملة", "Reconciliation is incomplete"), systemImage: "exclamationmark.triangle")
                                .foregroundStyle(.red)
                            Text(tr("توجد \(Num.format(summary.missingExecutionAmount)) قيود تنفيذية بلا مبلغ.",
                                    "\(summary.missingExecutionAmount) execution entries have no amount."))
                                .font(.footnote).foregroundStyle(.red)
                        } else if abs(summary.executionDifference) >= 1 {
                            Label(tr("لا تتطابق المبالغ · الفرق \(Money.format(summary.executionDifference))", "Amounts differ · difference \(Money.format(summary.executionDifference))"), systemImage: "exclamationmark.triangle")
                                .foregroundStyle(.red)
                        } else {
                            Label(tr("المبلغان متطابقان", "Amounts match"), systemImage: "checkmark.circle.fill")
                                .foregroundStyle(.green)
                        }
                        Text(tr("يُحتسب مبلغ عمود المصارف مرة واحدة فقط؛ ملف جهات التنفيذ للمطابقة.",
                                "The payroll bank-deduction amount is counted once; the execution file is used for reconciliation."))
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                    if summary.missingNet > 0 || summary.missingContractNominal > 0 || summary.unclassifiedCount > 0 {
                        Section {
                            Text(tr("توجد \(Num.format(summary.missingNet)) رواتب بلا صافي، و\(Num.format(summary.missingContractNominal)) عقود بلا راتب اسمي، و\(Num.format(summary.unclassifiedCount)) رواتب بلا طريقة دفع محفوظة. أعد استيراد الشهر لتصنيف الكاش والبطاقة.",
                                    "\(summary.missingNet) payroll rows lack net pay, \(summary.missingContractNominal) contract rows lack nominal pay, and \(summary.unclassifiedCount) lack a saved payment method. Reimport the month to split cash and card."))
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
                        if let month {
                            Section(tr("مبالغ يدوية · \(month.name)", "Manual amounts · \(month.name)")) {
                                TextField(tr("الإجازات المتراكمة", "Accumulated leave"), value: amountBinding(\.accumulatedLeave, month: month.name), format: .number.locale(AppSettings.locale))
                                    .keyboardType(.decimalPad)
                                TextField(tr("مكافآت منتسبين", "Employee rewards"), value: amountBinding(\.employeeRewards, month: month.name), format: .number.locale(AppSettings.locale))
                                    .keyboardType(.decimalPad)
                                TextField(tr("قائمة مفردة", "Single list"), value: amountBinding(\.singleList, month: month.name), format: .number.locale(AppSettings.locale))
                                    .keyboardType(.decimalPad)
                            } footer: {
                                Text(tr("تُحفظ هذه المبالغ لهذا الشهر فقط وتُضاف إلى إجمالي كشف البنك والنسخة الاحتياطية.",
                                        "These amounts belong to this month only and are included in the bank total and backup."))
                            }
                        }
                        Section(tr("طريقة الاحتساب", "Calculation")) {
                            Text(tr("صافي الرواتب (كاش وبطاقة) + المبالغ اليدوية للشهر + الرعاية الصحية + صندوق الشهداء + النشاط الرياضي + الضريبة + الحماية الاجتماعية + شركة عراق الجوال + مجموع استقطاعات المصارف + التقاعد والضمان الاجتماعي (12% من اسمي رتبة عقود فقط + توقيفاتهم التقاعدية). لا يُستخدم مجموع الاستقطاعات. يجب أن يطابق عمود المصارف ملف الاستقطاعات التنفيذية.",
                                    "Cash and card net payroll + this month's manual amounts + health care + martyrs fund + sport + tax + social protection + Iraq Mobile + bank deductions + pension/social security (12% of nominal pay for the عقود rank only, plus their retirement deductions). The overall deduction total is not used. Bank deductions must match the execution file."))
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
            .onChange(of: manualAmountsData) { BackupManager.shared.markChanged() }
            .task {
                if UserDefaults.standard.bool(forKey: "openBankSettings") { showSettings = true }
            }
        }
    }
}
