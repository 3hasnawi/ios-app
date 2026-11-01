import SwiftUI

/// دفتر محلي للمراجعة مقابل كشف المصرف الورقي، لا يثبت حصول الحركات آلياً.
struct BankReconciliationView: View {
    let month: String
    let summary: BankStatementSummary
    let manual: BankManualAmounts
    let figures: TransferLetterFigures?
    let figuresFailure: String?
    let entities: [TransferEntity]?
    @AppStorage(BankReconciliation.settingsKey) private var stored = Data()
    @AppStorage("bankStatementAccountName") private var accountName = ""
    @AppStorage("bankStatementIBAN") private var accountIBAN = ""
    @AppStorage("bankStatementBranch") private var accountBranch = "الأنبار"
    @AppStorage("bankStatementLocalAccount") private var localAccount = ""
    @State private var period: BankReconciliation.Period
    @State private var isLoaded = false
    @State private var editor: MovementDraft?
    @State private var pdf: URL?
    @State private var pdfPeriod: BankReconciliation.Period?
    @State private var pdfAccount: BankReconciliation.AccountDetails?
    @State private var pdfSources: [Suggestion] = []
    @State private var pdfComplete = false
    @State private var preparing = false
    @State private var error: String?
    @State private var showConfirm = false
    @State private var showSuggestions = false
    @State private var showContractReview = false
    @State private var showManualApproval = false
    @State private var manualApproval: ApprovalContext?
    @State private var pdfManualApproval = false

    init(month: String, summary: BankStatementSummary, manual: BankManualAmounts, figures: TransferLetterFigures?, figuresFailure: String?, entities: [TransferEntity]?) {
        self.month = month; self.summary = summary; self.manual = manual; self.figures = figures; self.figuresFailure = figuresFailure; self.entities = entities
        let now = Date()
        var initial = BankReconciliation.Period(month: month, start: now, end: now)
        initial.memberFirst = "باسم فواز نواف"; initial.memberSecond = "حميد زيدان خلف"
        initial.signatureDefaultsInitialized = true
        _period = State(initialValue: initial)
    }
    private var account: BankReconciliation.AccountDetails {
        .init(name: accountName, branch: accountBranch, localAccount: localAccount, iban: accountIBAN)
    }
    private var totals: BankReconciliation.Totals? { try? BankReconciliation.calculate(period) }
    private var unmatched: Int { period.movements.filter { !$0.matched }.count }
    private var unresolvedCount: Int { entities?.filter { $0.category == .unresolved }.count ?? 0 }
    private var incompleteSources: Int {
        entities?.filter { $0.category != .unresolved && !TransferRules.issues($0).isEmpty }.count ?? 0
    }
    private var missingSuggestions: Int {
        let used = Set(period.movements.compactMap(\.sourceID))
        return suggestedAmounts.filter { !used.contains($0.id) }.count
    }
    private var staleSources: [BankReconciliation.Movement] {
        let current = Dictionary(suggestedAmounts.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        return period.movements.filter { movement in
            guard let id = movement.sourceID else { return false }
            guard let item = current[id] else { return true }
            return item.amount != movement.amount || Set(item.sourceLineIDs) != Set(movement.sourceLineIDs ?? []) || movement.direction != .debit
        }
    }
    private var availableContractAmount: Int64? {
        guard let figures, figures.missingContractNominal > 0, let share = figures.availableContractNominalShare else { return nil }
        let value = share + figures.contractRetirement
        guard value.isFinite, value >= 0, value.rounded() == value, value <= Double(BankReconciliation.maximumAmount) else { return nil }
        return Int64(value)
    }
    private var contractReviewAccepted: Bool {
        guard let availableContractAmount else { return false }
        return period.acceptedContractMissing == summary.missingContractNominal && period.acceptedContractAmount == availableContractAmount
    }
    private var fundingDatesMatch: Bool {
        let funding = period.movements.filter { $0.isFunding == true }
        return !funding.isEmpty && funding.allSatisfy { BankReconciliation.day($0.date) == BankReconciliation.day(period.start) }
    }
    private var isComplete: Bool {
        totals?.balance == 0 && (totals?.funding ?? 0) > 0 && fundingDatesMatch && unmatched == 0 && missingSuggestions == 0 &&
        (totals?.outstandingRefunds.isEmpty == true) && summary.missingNet == 0 &&
        (summary.missingContractNominal == 0 || contractReviewAccepted) &&
        (summary.executionMatched || (summary.executionCount == 0 && summary.bankDeductions == 0)) &&
        unresolvedCount == 0 && incompleteSources == 0 && staleSources.isEmpty && figuresFailure == nil && entities != nil
    }
    private struct ApprovalContext: Equatable {
        let period: BankReconciliation.Period
        let account: BankReconciliation.AccountDetails
        let suggestions: [Suggestion]
        let alerts: [String]
    }
    private var currentApprovalContext: ApprovalContext {
        ApprovalContext(period: period, account: account, suggestions: suggestedAmounts, alerts: [
            "\(unresolvedCount)", "\(incompleteSources)", "\(missingSuggestions)",
            "\(summary.missingNet)", "\(summary.missingContractNominal)", "\(summary.executionMatched)",
            "\(summary.executionCount)", "\(summary.bankDeductions)",
            figuresFailure ?? "", entities == nil ? "missing" : "loaded",
            staleSources.map { $0.id.uuidString }.sorted().joined(separator: ",")
        ])
    }
    private var manuallyApproved: Bool { manualApproval == currentApprovalContext }
    private var previewIsCurrent: Bool {
        pdfPeriod == period && pdfAccount == account && pdfSources == suggestedAmounts &&
        (pdfManualApproval ? manuallyApproved : (isComplete && pdfComplete))
    }
    private var canEdit: Bool { isLoaded && !BackupManager.shared.isWorking && !ImportCenter.shared.isBusy && !StaffImportState.shared.isImporting }
    private var visibleMovements: [BankReconciliation.Movement] {
        totals?.lines.map(\.movement) ?? period.movements
    }
    private var savedPeriods: [BankReconciliation.Period] {
        ((try? BankReconciliation.decode(stored))?.periods ?? []).filter { $0.month == month }
    }
    var body: some View {
        List {
            periodSection
            summarySection
            addMovementSection
            movementSection
            finishSection
        }
        .environment(\.timeZone, TimeZone(identifier: "Asia/Baghdad") ?? .current)
        .navigationTitle(tr("مطابقة حساب المصرف", "Bank account reconciliation"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { ToolbarItem(placement: .topBarTrailing) { EditButton() } }
        .onAppear(perform: load)
        .onChange(of: currentApprovalContext) { manualApproval = nil }
        .sheet(item: $editor) { draft in
            NavigationStack {
                BankReconciliationMovementEditor(draft: draft, period: period, error: $error) { movement in
                    var candidate = period
                    if let index = candidate.movements.firstIndex(where: { $0.id == movement.id }) {
                        candidate.movements[index] = BankReconciliation.replacing(candidate.movements[index], with: movement)
                    } else { candidate.movements.append(movement) }
                    return persist(candidate)
                }
            }
        }
        .sheet(isPresented: $showSuggestions) { NavigationStack { suggestions } }
        .sheet(isPresented: Binding(get: { pdf != nil }, set: { if !$0 { if let pdf { try? FileManager.default.removeItem(at: pdf) }; pdf = nil } })) {
            if let pdf { BankReconciliationPreview(url: pdf, current: previewIsCurrent) }
        }
        .confirmationDialog(tr("معاينة كشف المطابقة", "Preview reconciliation"), isPresented: $showConfirm, titleVisibility: .visible) {
            Button(tr("معاينة PDF", "Preview PDF")) { preview() }
        } message: { Text(tr("راجع الرصيد والحركات غير المؤشرة مع كشف المصرف الورقي قبل اعتماد الكشف.", "Review the balance and unchecked movements against the paper bank statement before accepting the statement.")) }
        .confirmationDialog(tr("تأكيد المطابقة اليدوية", "Confirm manual reconciliation"), isPresented: $showManualApproval, titleVisibility: .visible) {
            Button(tr("أكدت المطابقة يدوياً وتجاوز التنبيهات", "Confirm manual reconciliation and bypass alerts")) {
                if persist(period) { manualApproval = currentApprovalContext }
            }
        } message: { Text(tr("أؤكد أنني راجعت الحركات والمبالغ مع كشف المصرف الورقي، وأتحمل مسؤولية التنبيهات الظاهرة. يُلغى التأكيد عند تعديل بيانات الكشف أو تغير التنبيهات، وعند الخروج من هذه الصفحة.", "I confirm that I reviewed the movements and amounts against the paper bank statement and accept the displayed alerts. Changing the statement data or alerts, or leaving this screen, cancels the confirmation.")) }
        .confirmationDialog(tr("مراجعة مبلغ العقود", "Review contract amount"), isPresented: $showContractReview, titleVisibility: .visible) {
            Button(tr("اعتماد المبلغ المتوفر", "Accept available amount")) {
                guard let amount = availableContractAmount else { return }
                var candidate = period; candidate.acceptedContractMissing = summary.missingContractNominal; candidate.acceptedContractAmount = amount
                persist(candidate)
            }
        } message: { Text(tr("المبلغ يحسب من الاسمي المتوفر فقط، ولا يحتسب اسمي العقود الناقصة. تأكد من مطابقته لكشف المصرف قبل اعتماده.", "This uses available nominal amounts only and excludes missing contract nominal amounts. Verify it against the bank statement before accepting.")) }
        .alert(tr("تعذّرت العملية", "Operation failed"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
    }
    @ViewBuilder private var periodSection: some View {
        Section {
            if savedPeriods.count > 1 {
                Picker(tr("فترة محفوظة", "Saved period"), selection: Binding(get: { period.id }, set: { id in
                    if let saved = savedPeriods.first(where: { $0.id == id }) { period = BankReconciliation.preparedSignatures(saved) }
                })) { ForEach(savedPeriods) { value in Text("\(value.start.formatted(date: .numeric, time: .omitted)) – \(value.end.formatted(date: .numeric, time: .omitted))").tag(value.id) } }
            }
            LabeledContent(tr("شهر التمويل والبيانات", "Funding and data month"), value: month)
            DatePicker(tr("من", "From"), selection: $period.start, displayedComponents: .date)
            DatePicker(tr("إلى", "To"), selection: $period.end, displayedComponents: .date)
            BankReconciliationIntegerField(title: tr("الرصيد الافتتاحي", "Opening balance"), amount: $period.openingBalance)
            Button(tr("حفظ الفترة", "Save period"), systemImage: "checkmark.circle") { persist(period) }.disabled(!canEdit)
            if let saved = savedPeriods.first(where: { $0.id == period.id }), saved != period {
                Button(tr("استعادة الفترة المحفوظة", "Restore saved period"), systemImage: "arrow.uturn.backward") { period = BankReconciliation.preparedSignatures(saved) }
            }
        } header: { Text(tr("الفترة", "Period")) } footer: {
            Text(tr("ابدأ بتمويل الشهر السابق وانتهِ قبل التمويل الجديد؛ يمكنك إدخال حركات يوم التمويل الجديد السابقة له. أدخل التمويل كدائن، ولا تضفه إلى الرصيد الافتتاحي مرة أخرى.", "Start at the previous funding and stop before the new funding; earlier movements on the new funding date may be included. Enter funding as a credit and do not count it again in the opening balance."))
        }
    }
    @ViewBuilder private var summarySection: some View {
        if let totals {
            Section(tr("ملخص الحساب", "Account summary")) {
                if account.isIncomplete {
                    Label(tr("أكمل اسم الحساب والفرع ورقم الحساب المحلي وIBAN من إعداد الحساب لتظهر في الكشف.", "Complete the account name, branch, local account number and IBAN in Account settings so they appear on the statement."), systemImage: "info.circle")
                        .foregroundStyle(.orange)
                }
                LabeledContent(tr("التمويل", "Funding"), value: Money.format(Double(totals.funding)))
                LabeledContent(tr("مجموع الدائن", "Total credits"), value: Money.format(Double(totals.credits)))
                LabeledContent(tr("مجموع المدين", "Total debits"), value: Money.format(Double(totals.debits)))
                LabeledContent(tr("الرصيد الختامي", "Closing balance"), value: Money.format(Double(totals.balance)))
                ForEach(totals.outstandingRefunds) { item in
                    Label(tr("متبقي من \(item.movement.description): \(Money.format(Double(item.remaining)))", "Remaining from \(item.movement.description): \(Money.format(Double(item.remaining)))"), systemImage: "arrow.uturn.backward.circle")
                        .foregroundStyle(.orange)
                }
                if totals.funding > 0 && !fundingDatesMatch { Label(tr("تاريخ التمويل لا يوافق بداية الفترة؛ راجعه قبل الاعتماد.", "The funding date does not match the period start; review it before accepting."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                if totals.funding == 0 { Label(tr("لم تُسجّل حركة التمويل بعد.", "No funding movement has been recorded."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                if totals.balance != 0 { Label(tr("الرصيد الختامي ليس صفراً.", "The closing balance is not zero."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                if unresolvedCount > 0 { Label(tr("\(Num.format(unresolvedCount)) جهة لم تُصنّف؛ مجموعاتها غير مكتملة.", "\(Num.format(unresolvedCount)) entities are unclassified; their groups are incomplete."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                if incompleteSources > 0 { Label(tr("\(Num.format(incompleteSources)) جهة تحتاج مراجعة بياناتها في ملفات التحويل.", "\(Num.format(incompleteSources)) entities need data review in transfer files."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                if missingSuggestions > 0 { Label(tr("\(Num.format(missingSuggestions)) مبالغ مقترحة لم تُدرج؛ راجعها قبل اعتماد الكشف.", "\(Num.format(missingSuggestions)) suggested amounts were not added; review them before accepting the statement."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                if !summary.executionMatched && (summary.executionCount > 0 || summary.bankDeductions != 0) { Label(tr("استقطاعات المصارف لا تطابق ملف جهات التنفيذ أو بيانات المطابقة ناقصة.", "Bank deductions do not match the execution data, or reconciliation data is incomplete."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                if summary.missingNet > 0 || summary.missingContractNominal > 0 { Label(tr("بعض بيانات الرواتب أو اسمي العقود ناقصة؛ راجع المبالغ قبل الاعتماد.", "Some payroll or contract nominal data is missing; review amounts before accepting."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                if !staleSources.isEmpty { Label(tr("تغيّرت مصادر مبالغ مقترحة؛ احذف الحركات المتأثرة وأضفها بعد المراجعة.", "Suggested source amounts changed; remove affected movements and add them again after review."), systemImage: "exclamationmark.triangle").foregroundStyle(.red) }
                if unmatched > 0 { Label(tr("\(Num.format(unmatched)) حركة لم تؤشر مطابقتها مع الكشف الورقي.", "\(Num.format(unmatched)) movements are not checked against the paper statement."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
            }
        } else {
            Section { Text(tr("الفترة أو بيانات الحركات غير صالحة؛ راجعها قبل التصدير.", "The period or movement data is invalid; review it before exporting.")).foregroundStyle(.red) }
        }
    }
    @ViewBuilder private var addMovementSection: some View {
        if let figuresFailure {
            Section { Text(tr("تعذّر تحميل بعض مبالغ الكتب: \(figuresFailure)", "Some letter amounts could not be loaded: \(figuresFailure)")).foregroundStyle(.orange) }
        }
        Section {
            Button(tr("إضافة حركة", "Add movement"), systemImage: "plus.circle") { editor = MovementDraft(date: period.start) }.disabled(!canEdit)
            Button(tr("المبالغ المقترحة من التطبيق", "Suggested amounts from the app"), systemImage: "tray.and.arrow.down") { showSuggestions = true }.disabled(!canEdit)
        } footer: { Text(tr("أدخل تاريخ التنفيذ كما يظهر في كشف المصرف؛ تأكيد كتاب أو احتساب راتب لا يثبت تنفيذ حركة مصرفية.", "Enter the execution date shown on the bank statement; a confirmed letter or calculated salary is not proof of a bank movement.")) }
    }
    @ViewBuilder private var movementSection: some View {
        Section {
            if period.movements.isEmpty { Text(tr("لا توجد حركات بعد.", "No movements yet.")).foregroundStyle(.secondary) }
            ForEach(visibleMovements) { movement in
                HStack(alignment: .top) {
                    Button {
                        var candidate = period
                        guard let index = candidate.movements.firstIndex(where: { $0.id == movement.id }) else { return }
                        candidate.movements[index].matched.toggle()
                        persist(candidate)
                    } label: { Image(systemName: movement.matched ? "checkmark.circle.fill" : "circle").foregroundStyle(movement.matched ? Color.green : Color.secondary) }
                    .buttonStyle(.plain).disabled(!canEdit)
                    Button { editor = MovementDraft(movement) } label: {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(movement.description).foregroundStyle(.primary)
                            if staleSources.contains(where: { $0.id == movement.id }) { Text(tr("تغيّر المصدر؛ احذف الحركة وأعد إضافتها.", "Source changed; delete and re-add this movement.")).font(.caption).foregroundStyle(.red) }
                            Text("\(movement.date.formatted(date: .numeric, time: .omitted)) · \(movement.direction.title)")
                                .font(.caption).foregroundStyle(.secondary)
                        }.frame(maxWidth: .infinity, alignment: .leading)
                    }.buttonStyle(.plain).disabled(!canEdit)
                    Text(Money.format(Double(movement.amount))).font(.subheadline.monospacedDigit())
                }
                .swipeActions {
                    Button(role: .destructive) {
                        var candidate = period; candidate.movements.removeAll { $0.id == movement.id }; persist(candidate)
                    } label: { Label(tr("حذف", "Delete"), systemImage: "trash") }.disabled(!canEdit)
                }
            }
            .onMove { offsets, destination in
                guard canEdit, let lines = totals?.lines else { return }
                var sorted = lines.map(\.movement)
                sorted.move(fromOffsets: offsets, toOffset: destination)
                var candidate = period; candidate.movements = sorted
                persist(candidate)
            }
        } header: { Text(tr("الحركات", "Movements")) } footer: { Text(tr("اضغط الدائرة بعد التأكد من الحركة في كشف المصرف الورقي. استخدم تعديل لترتيب حركات اليوم نفسه كما ظهرت في الكشف.", "Check the circle after verifying the movement on paper. Use Edit to order movements on the same day as they appear on the statement.")) }
    }
    @ViewBuilder private var finishSection: some View {
        Section(tr("عضو اللجنة الأول · اليمين", "First committee member · right")) {
            TextField(tr("الرتبة", "Rank"), text: Binding(get: { period.firstRank ?? "عقيد الشرطة" }, set: { period.firstRank = $0 }))
            TextField(tr("الاسم", "Name"), text: $period.memberFirst)
            TextField(tr("المنصب", "Position"), text: Binding(get: { period.firstPosition ?? "مدير قسم التدقيق والرقابة الداخلية" }, set: { period.firstPosition = $0 }))
            TextField(tr("التاريخ", "Date"), text: Binding(get: { period.firstDate ?? BankReconciliation.signatureDate(period.end) }, set: { period.firstDate = $0 }))
        }
        Section(tr("عضو اللجنة الثاني · اليسار", "Second committee member · left")) {
            TextField(tr("الرتبة", "Rank"), text: Binding(get: { period.secondRank ?? "العميد" }, set: { period.secondRank = $0 }))
            TextField(tr("الاسم", "Name"), text: $period.memberSecond)
            TextField(tr("المنصب", "Position"), text: Binding(get: { period.secondPosition ?? "مدير الحسابات" }, set: { period.secondPosition = $0 }))
            TextField(tr("التاريخ", "Date"), text: Binding(get: { period.secondDate ?? BankReconciliation.signatureDate(period.end) }, set: { period.secondDate = $0 }))
            Button(tr("حفظ بيانات اللجنة", "Save committee details"), systemImage: "checkmark.circle") { persist(period) }.disabled(!canEdit)
        }
        Section {
            if !period.movements.isEmpty && !isComplete {
                if manuallyApproved {
                    Label(tr("تم تأكيد المطابقة يدوياً لهذه البيانات. تبقى التنبيهات ظاهرة للمراجعة.", "Manual reconciliation confirmed for these data. Alerts remain visible for review."), systemImage: "checkmark.shield")
                        .foregroundStyle(.orange)
                } else {
                    Text(tr("راجع التنبيهات والكشف الورقي، ثم يمكنك تأكيد المطابقة يدوياً لتصدير الكشف.", "Review the alerts and paper statement, then confirm manually to export."))
                        .font(.footnote).foregroundStyle(.orange)
                    Button(tr("تأكيد المطابقة اليدوية", "Confirm manual reconciliation"), systemImage: "checkmark.shield") { showManualApproval = true }
                        .disabled(!canEdit || totals == nil || preparing)
                }
            }
            Button(tr("معاينة الكشف PDF", "Preview PDF"), systemImage: "doc.text.magnifyingglass") { showConfirm = true }
                .disabled(!canEdit || totals == nil || period.movements.isEmpty || preparing || (!isComplete && !manuallyApproved))
            if preparing { ProgressView() }
        }
    }
    private var suggestions: some View {
        List {
            Section {
                ForEach(suggestedAmounts, id: \.id) { suggestion in
                    Button {
                        showSuggestions = false
                        let next = MovementDraft(date: period.start, description: suggestion.title, amount: suggestion.amount, sourceID: suggestion.id, sourceLineIDs: suggestion.sourceLineIDs)
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { editor = next }
                    } label: {
                        LabeledContent(suggestion.title, value: Money.format(Double(suggestion.amount)))
                    }.disabled(period.movements.contains { $0.sourceID == suggestion.id })
                }
                if suggestedAmounts.isEmpty { Text(tr("لا توجد مبالغ مقترحة متاحة.", "No suggested amounts are available.")).foregroundStyle(.secondary) }
            } footer: { Text(tr("مبالغ مقترحة للمراجعة فقط. المجموعات والجهات من بيانات الشهر، وتاريخ التنفيذ يُختار من كشف المصرف. بعض الكتب قد تعتمد أكثر من بند؛ راجع الكشف وتجنب إضافة مبلغ مسجل سابقاً يدوياً.", "Suggestions are for review only. Groups and entities use monthly data; choose the execution date from the bank statement. Some books may cover multiple items; avoid re-adding manually recorded amounts.")) }
        }.navigationTitle(tr("المبالغ المقترحة", "Suggested amounts"))
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button(tr("تم", "Done")) { showSuggestions = false } } }
    }
    private struct Suggestion: Identifiable, Equatable { let id: String; let title: String; let amount: Int64; var sourceLineIDs: [String] = [] }
    private var suggestedAmounts: [Suggestion] {
        var values: [Suggestion] = []
        func add(_ id: String, _ title: String, _ value: Double, sourceLines: [String] = []) {
            guard value.isFinite, value > 0, value.rounded() == value, value <= Double(BankReconciliation.maximumAmount) else { return }
            values.append(.init(id: "\(month)|\(id)", title: title, amount: Int64(value), sourceLineIDs: sourceLines))
        }
        add("card", tr("رواتب البطاقة", "Card salaries"), summary.cardNet)
        add("cash", tr("رواتب الكاش", "Cash salaries"), summary.cashNet, sourceLines: ["payroll|\(month)|cashSalary"])
        add("unclassified", tr("رواتب غير مصنفة", "Unclassified salaries"), summary.unclassifiedNet)
        add("leave", tr("الإجازات المتراكمة", "Accumulated leave"), manual.accumulatedLeave)
        add("rewards", tr("مكافآت منتسبين", "Employee rewards"), manual.employeeRewards)
        add("single", tr("قائمة مفردة", "Single list"), manual.singleList)
        if let figures {
            func payrollIDs(_ values: [String]) -> [String] { values.map { "payroll|\(month)|\($0)" } }
            add("foodCompany", tr("شركة عراق الجوال", "Iraq Al-Jawwal Company"), figures.foodCompany, sourceLines: payrollIDs(["foodCompany"]))
            add("foodTax", tr("ضريبة عقد الإطعام", "Food contract tax"), figures.foodTax, sourceLines: payrollIDs(["foodTax"]))
            add("treasury", tr("الضريبة والحماية الاجتماعية", "Tax and social protection"), figures.treasuryTotal, sourceLines: payrollIDs(["incomeTax", "socialProtection"]))
            let contractAmount = figures.contractTotal ?? (contractReviewAccepted ? availableContractAmount.map { Double($0) } : nil)
            if let contractAmount { add("contracts", tr("ضمان العقود", "Contract social security"), contractAmount, sourceLines: payrollIDs(["contractNominal", "contractRetirement"])) }
        }
        if let entities {
            let unresolved = entities.contains { $0.category == .unresolved }
            for category in [TransferCategory.rafidain, .otherBanks] {
                let group = entities.filter { $0.category == category }
                if !unresolved, !group.isEmpty, group.allSatisfy({ TransferRules.issues($0).isEmpty }) {
                    add("group|\(category.rawValue)", category.title, group.reduce(0.0) { $0 + $1.group.total }, sourceLines: group.map(\.id))
                }
            }
            for entity in entities where entity.category == .anbar && entity.missingAmounts == 0 {
                add("individual|\(entity.id)", entity.group.title, entity.group.total, sourceLines: [entity.id])
            }
        }
        return values
    }
    private func load() {
        do {
            let data = try BankReconciliation.decode(stored)
            if let value = data.periods.last(where: { $0.month == month }) {
                period = BankReconciliation.preparedSignatures(value)
            }
            isLoaded = true
        } catch { self.error = error.localizedDescription; isLoaded = false }
    }
    @discardableResult private func persist(_ value: BankReconciliation.Period) -> Bool {
        guard canEdit else { return false }
        do {
            _ = try BankReconciliation.calculate(value)
            var ledger = try BankReconciliation.decode(stored)
            if let index = ledger.periods.firstIndex(where: { $0.id == value.id }) { ledger.periods[index] = value }
            else { ledger.periods.append(value) }
            stored = try JSONEncoder().encode(ledger); period = value
            BackupManager.shared.markChanged(); return true
        } catch { self.error = error.localizedDescription; return false }
    }
    private func preview() {
        guard persist(period) else { return }
        let snapshot = period
        let accountSnapshot = account
        let manual = !isComplete && manuallyApproved
        let complete = isComplete || manual
        let approval = currentApprovalContext
        let sources = suggestedAmounts
        preparing = true
        Task {
            defer { preparing = false }
            do {
                let url = try await BankReconciliationPDF.make(snapshot, complete: complete, account: accountSnapshot)
                guard snapshot == period, accountSnapshot == account, sources == suggestedAmounts, approval == currentApprovalContext,
                      (manual ? manuallyApproved : isComplete) else {
                    try? FileManager.default.removeItem(at: url); throw BankReconciliation.Failure.invalid
                }
                pdfPeriod = snapshot; pdfAccount = accountSnapshot; pdfSources = sources; pdfComplete = complete; pdfManualApproval = manual; pdf = url
            } catch { self.error = error.localizedDescription }
        }
    }
}

private struct MovementDraft: Identifiable {
    var id = UUID()
    var date: Date
    var description = ""
    var amount: Int64 = 0
    var direction = BankReconciliation.Direction.debit
    var sourceID: String? = nil
    var sourceLineIDs: [String]? = nil
    var refundOf: UUID? = nil
    var isRefund = false
    var isFunding = false
    var dateReviewed = false
    init(date: Date, description: String = "", amount: Int64 = 0, sourceID: String? = nil, sourceLineIDs: [String]? = nil) {
        self.date = date; self.description = description; self.amount = amount; self.sourceID = sourceID; self.sourceLineIDs = sourceLineIDs
    }
    init(_ value: BankReconciliation.Movement) {
        id = value.id; date = value.date; description = value.description; amount = value.amount; direction = value.direction
        sourceID = value.sourceID; sourceLineIDs = value.sourceLineIDs; refundOf = value.refundOf; isRefund = value.isRefund == true; isFunding = value.isFunding == true; dateReviewed = true
    }
    var movement: BankReconciliation.Movement {
        .init(id: id, date: date, description: description, amount: amount, direction: direction, sourceID: sourceID, sourceLineIDs: sourceLineIDs, refundOf: refundOf, isRefund: isRefund, isFunding: isFunding)
    }
}

private struct BankReconciliationMovementEditor: View {
    @Environment(\.dismiss) private var dismiss
    @State var draft: MovementDraft
    let period: BankReconciliation.Period
    @Binding var error: String?
    let save: (BankReconciliation.Movement) -> Bool
    var body: some View {
        Form {
            DatePicker(tr("تاريخ المصرف", "Bank date"), selection: $draft.date, displayedComponents: .date)
                .onChange(of: draft.date) { draft.dateReviewed = false }
            Toggle(tr("تأكدت من التاريخ في كشف المصرف", "I checked the date on the bank statement"), isOn: $draft.dateReviewed)
            TextField(tr("البيان", "Description"), text: $draft.description)
            Picker(tr("نوع الحركة", "Direction"), selection: $draft.direction) { ForEach(BankReconciliation.Direction.allCases, id: \.self) { Text($0.title).tag($0) } }
                .onChange(of: draft.direction) { if draft.direction == .credit { draft.refundOf = nil } else { draft.isRefund = false; draft.isFunding = false } }
            BankReconciliationIntegerField(title: tr("المبلغ", "Amount"), amount: $draft.amount)
            if draft.direction == .credit {
                Toggle(tr("تمويل الشهر السابق", "Previous month's funding"), isOn: $draft.isFunding)
                    .onChange(of: draft.isFunding) { if draft.isFunding { draft.isRefund = false } }
                Toggle(tr("مبلغ مسترجع من جهة", "Refund from an entity"), isOn: $draft.isRefund)
                    .onChange(of: draft.isRefund) { if draft.isRefund { draft.isFunding = false } }
            }
            if draft.direction == .debit {
                Picker(tr("مرتبط بمبلغ مسترجع", "Related refund"), selection: $draft.refundOf) {
                    Text(tr("غير مرتبط", "Not linked")).tag(Optional<UUID>.none)
                    ForEach(period.movements.filter { $0.direction == .credit && $0.isRefund == true }) { value in
                        Text("\(value.description) · \(Money.format(Double(value.amount)))").tag(Optional(value.id))
                    }
                }
            }
            if BankReconciliation.day(draft.date) == BankReconciliation.day(period.end) {
                Text(tr("أدرج في اليوم الأخير الحركات التي سبقت التمويل الجديد فقط؛ التمويل الجديد لا يدخل في الكشف.", "On the final day, include only movements before the new funding; the new funding does not belong to this statement.")).font(.footnote).foregroundStyle(.orange)
            }
            Text(tr("تأكد من التاريخ والمبلغ وفق كشف المصرف الورقي.", "Confirm the date and amount against the paper bank statement.")).font(.footnote).foregroundStyle(.secondary)
        }
        .navigationTitle(tr("حركة الحساب", "Account movement"))
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button(tr("إلغاء", "Cancel")) { dismiss() } }
            ToolbarItem(placement: .confirmationAction) { Button(tr("حفظ", "Save")) { if save(draft.movement) { dismiss() } }.disabled(!draft.dateReviewed) }
        }
        .alert(tr("تعذّر حفظ الحركة", "Could not save movement"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
    }
}

private struct BankReconciliationIntegerField: View {
    let title: String
    @Binding var amount: Int64
    @State private var text: String
    @FocusState private var focused: Bool
    init(title: String, amount: Binding<Int64>) {
        self.title = title; self._amount = amount
        self._text = State(initialValue: amount.wrappedValue == 0 ? "" : String(amount.wrappedValue))
    }
    var body: some View {
        TextField(title, text: $text)
            .keyboardType(.numberPad)
            .focused($focused)
            .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
            .contentShape(Rectangle())
            .simultaneousGesture(TapGesture().onEnded { focused = true })
            .onChange(of: text) { previous, value in
                let digits = ArabicText.latinDigits(value)
                if digits.isEmpty { amount = 0; return }
                guard digits.utf8.allSatisfy({ (48...57).contains($0) }), let parsed = Int64(digits), parsed <= BankReconciliation.maximumAmount else { text = previous; return }
                amount = parsed
            }
            .onChange(of: amount) { _, value in
                if !focused { text = value == 0 ? "" : String(value) }
            }
    }
}

private struct BankReconciliationPreview: View {
    let url: URL
    let current: Bool
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                if !current { Text(tr("تغيّرت البيانات؛ أغلق المعاينة وأعد إعداد الكشف.", "Data changed; close the preview and prepare the statement again.")).foregroundStyle(.orange).padding() }
                PDFPreview(url: url)
            }
                .navigationTitle(tr("كشف المطابقة", "Reconciliation statement"))
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button(tr("تم", "Done")) { dismiss() } }
                    ToolbarItem(placement: .primaryAction) { Button(tr("تصدير", "Export"), systemImage: "square.and.arrow.up") { ActivityShare.present([url]) }.disabled(!current) }
                }
        }
    }
}
