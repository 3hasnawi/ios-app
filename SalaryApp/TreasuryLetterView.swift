import SwiftUI
import SwiftData
import PDFKit

struct TreasuryLetterView: View {
    enum Kind { case guarantee, schools }
    let kind: Kind
    let monthName: String
    @Query private var months: [PayrollMonth]
    @AppStorage("bankStatementIBAN") private var payerIBAN = ""
    @AppStorage(ExecutionAccountStore.storageKey) private var accountsData = Data()
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var customizationData = Data()
    @State private var bankCache = BankStatementCache.shared
    @State private var firstItem = ""
    @State private var secondItem = ""
    @State private var loadedItems = ""
    @State private var firstRows: [ReportRow] = []
    @State private var secondRows: [ReportRow] = []
    @State private var notes: [Int: String] = [:]
    @State private var beneficiaryIBAN = ""
    @State private var guaranteeReason = "مبالغ الإيراد النهائي لتضمين الأسلحة لمنسوبي قيادتنا المدرجة أسماؤهم أدناه"
    @State private var header = ""
    @State private var number = ""
    @State private var firstSigner = ""
    @State private var secondSigner = ""
    @State private var thirdSigner = ""
    @State private var pendingFirstItem: String?
    @State private var confirmDiscardNotes = false
    @State private var initialized = false
    @State private var loading = false
    @State private var working = false
    @State private var error: String?
    @State private var preview: URL?
    @State private var previewRevision = ""

    private var month: PayrollMonth? { months.first { $0.name == monthName } }
    private var summary: [String: PayrollItems.Summary]? { PayrollItems.decodeSummary(month?.itemSummaryData) }
    private var itemNames: [String] { (summary.map { Array($0.keys) } ?? []).sorted() }
    private var accounts: [SavedExecutionAccount] { (try? ExecutionAccountStore.decode(accountsData)) ?? [] }
    private var sourceRevision: String {
        guard let month else { return "" }
        return "\(month.name)|\(String(describing: month.persistentModelID))|\(month.importedAt.timeIntervalSince1970)|\(bankCache.revision)"
    }
    private var loadKey: String { sourceRevision + "|" + firstItem + "|" + secondItem }
    private var previewKey: String { loadKey + "|" + payerIBAN + "|" + beneficiaryIBAN }
    private var isReady: Bool {
        !sourceRevision.isEmpty && loadedItems == loadKey && !firstRows.isEmpty &&
            (kind == .guarantee || (!secondRows.isEmpty && firstItem != secondItem))
    }
    private var title: String { kind == .guarantee ? tr("كتاب التضمين", "Guarantee letter") : tr("رسم الطابع وبناء المدارس", "Stamp duty and school construction") }
    var body: some View {
        Form {
            amountSection
            namesSection
            accountsSection
            detailsSection
            previewSection
        }
        .navigationTitle(title).navigationBarTitleDisplayMode(.inline)
        .overlay { if working { ProgressView().padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16)) } }
        .confirmationDialog(tr("تغيير البند يمسح الإضبارات المدخلة لهذه المسودة.", "Changing the item clears the file numbers entered for this draft."), isPresented: $confirmDiscardNotes, titleVisibility: .visible) {
            Button(tr("تغيير البند", "Change item"), role: .destructive) { if let pendingFirstItem { firstItem = pendingFirstItem }; pendingFirstItem = nil }
            Button(tr("إلغاء", "Cancel"), role: .cancel) { pendingFirstItem = nil }
        }
        .onAppear { initialize() }
        .task(id: loadKey) { await loadRows() }
        .sheet(isPresented: Binding(get: { preview != nil }, set: { if !$0 { closePreview() } })) {
            NavigationStack {
                if let preview {
                    VStack(spacing: 0) {
                        Text(tr("مسودة للمراجعة؛ تحقق من المبلغ والحساب وجدول الكتاب قبل اعتماده.", "Review the draft amount, account and table before approval."))
                            .font(.footnote).foregroundStyle(.orange).padding()
                        PDFPreview(url: preview)
                    }
                    .navigationTitle(tr("معاينة الكتاب", "Letter preview"))
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) { Button(tr("تم", "Done")) { closePreview() } }
                        ToolbarItem(placement: .bottomBar) {
                            Button(tr("مشاركة PDF", "Share PDF"), systemImage: "square.and.arrow.up") { ActivityShare.present([preview]) }
                                .disabled(previewRevision != previewKey || BackupManager.shared.isWorking || ImportCenter.shared.isBusy || StaffImportState.shared.isImporting)
                        }
                    }
                }
            }
        }
    }
    @ViewBuilder private var amountSection: some View {
            Section(tr("الشهر ومصدر المبلغ", "Month and amount source")) {
                LabeledContent(tr("الشهر", "Month"), value: monthName.isEmpty ? "—" : monthName)
                itemPicker(kind == .guarantee ? tr("بند التضمين", "Guarantee item") : tr("بند رسم الطابع", "Stamp duty item"), selection: Binding(get: { firstItem }, set: { value in
                    if kind == .guarantee && value != firstItem && notes.values.contains(where: { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) {
                        pendingFirstItem = value; confirmDiscardNotes = true
                    } else { firstItem = value }
                }))
                if kind == .schools { itemPicker(tr("بند بناء المدارس", "School construction item"), selection: $secondItem) }
                if loading { ProgressView() }
                if isReady {
                    LabeledContent(kind == .guarantee ? tr("مبلغ التضمين", "Guarantee amount") : tr("رسم الطابع", "Stamp duty"), value: Money.format(firstRows.reduce(0) { $0 + $1.amount }))
                    if kind == .schools {
                        LabeledContent(tr("بناء المدارس", "School construction"), value: Money.format(secondRows.reduce(0) { $0 + $1.amount }))
                        LabeledContent(tr("مجموع الكتاب", "Letter total"), value: Money.format((firstRows + secondRows).reduce(0) { $0 + $1.amount }))
                    }
                }
                if kind == .schools {
                    Text(tr("اختر بنداً مستقلاً لكل مبلغ؛ لا تُحسب نسبة تلقائية.", "Select a separate payroll item for each amount; no percentage is applied.")).font(.footnote).foregroundStyle(.secondary)
                    if !firstItem.isEmpty && firstItem == secondItem { Text(tr("اختر بندين مختلفين لتجنب تكرار المبلغ.", "Choose two different items to avoid counting the same amount twice.")).foregroundStyle(.red) }
                }
                if summary == nil { Text(tr("ملخص بنود هذا الشهر غير متاح؛ راجع استيراد الرواتب.", "The payroll item summary is unavailable for this month; review the payroll import.")).foregroundStyle(.orange) }
                if let error { Text(error).foregroundStyle(.red) }
            }
    }

    @ViewBuilder private var namesSection: some View {
            if kind == .guarantee && isReady {
                Section {
                    ForEach(firstRows) { row in
                        VStack(alignment: .leading) {
                            Text("\(row.rank) · \(row.name)")
                            LabeledContent(tr("المبلغ", "Amount"), value: Money.format(row.amount))
                            TextField(tr("رقم وتاريخ الإضبارة أو الملاحظة", "File number, date or note"), text: Binding(get: { notes[row.id] ?? "" }, set: { notes[row.id] = $0 }))
                        }
                    }
                } header: { Text(tr("الأسماء والإضبارة", "Names and file numbers")) }
                footer: { Text(tr("الإضبارات التي تدخلها تخص هذه المسودة وتبقى أثناء فتح الصفحة فقط؛ راجعها قبل المشاركة.", "File numbers apply to this draft and remain while this page is open; review them before sharing.")) }
            }
    }

    @ViewBuilder private var accountsSection: some View {
            Section(tr("حسابات الكتاب", "Letter accounts")) {
                LabeledContent(tr("حساب الجهة الدافعة", "Payer account"), value: payerIBAN.isEmpty ? "—" : payerIBAN).privacySensitive()
                TextField(tr("IBAN خزينة الأنبار", "Treasury IBAN"), text: $beneficiaryIBAN).textInputAutocapitalization(.characters).autocorrectionDisabled().privacySensitive()
                if !accounts.isEmpty {
                    Menu(tr("اختيار من الحسابات المحفوظة", "Choose saved account")) {
                        ForEach(accounts) { account in Button(account.entity) { beneficiaryIBAN = account.iban } }
                    }
                }
            }
    }

    @ViewBuilder private var detailsSection: some View {
            Section(tr("بيانات الكتاب والتواقيع", "Letter details and signatures")) {
                if kind == .guarantee { TextField(tr("سبب التضمين في متن الكتاب", "Reason in the letter"), text: $guaranteeReason, axis: .vertical).lineLimit(2...5) }
                TextField(tr("الترويسة", "Header"), text: $header, axis: .vertical).lineLimit(2...6)
                TextField(tr("العدد", "Reference number"), text: $number)
                TextField(tr("اسم العضو الأول", "First member"), text: $firstSigner)
                TextField(tr("اسم العضو الثاني", "Second member"), text: $secondSigner)
                TextField(tr("اسم العضو الثالث", "Third member"), text: $thirdSigner)
            }
    }

    @ViewBuilder private var previewSection: some View {
            Section {
                Button(tr("معاينة مسودة PDF", "Preview PDF draft"), systemImage: "doc.text.magnifyingglass") { prepare() }
                    .disabled(!isReady || loading || working)
                Text(tr("راجع الشهر والبنود والمبالغ والحسابات والأسماء والتواقيع قبل مشاركة المسودة.", "Review the month, items, amounts, accounts, names and signatures before sharing the draft."))
                    .font(.footnote).foregroundStyle(.secondary)
            }
    }

    @ViewBuilder private func itemPicker(_ label: String, selection: Binding<String>) -> some View {
        let options = kind == .guarantee ? itemNames.filter { ArabicText.normalize($0).contains(ArabicText.normalize("تضمين")) } : itemNames
        Picker(label, selection: selection) {
            Text(tr("اختر البند", "Choose item")).tag("")
            if !selection.wrappedValue.isEmpty && !options.contains(selection.wrappedValue) { Text(selection.wrappedValue + " ⚠").tag(selection.wrappedValue) }
            ForEach(options, id: \.self) { Text($0).tag($0) }
        }
    }
    private func initialize() {
        guard !initialized else { return }; initialized = true
        if let store = try? JSONDecoder().decode(TransferLetterCustomizationStore.self, from: customizationData) {
            // نستخدم ترويسة وتواقيع كتاب الخزينة الموجود مع الإعدادات المشتركة.
            let common = store.effective(for: "food_tax")
            header = common.header ?? ""; number = common.fixedNumber ?? ""
            firstSigner = common.committeeFirst ?? ""; secondSigner = common.committeeSecond ?? ""; thirdSigner = common.committeeThird ?? ""
        }
        if kind == .guarantee {
            let matches = itemNames.filter { ArabicText.normalize($0) == ArabicText.normalize("تضمين") }
            if matches.count == 1 { firstItem = matches[0] }
        }
    }
    private func loadRows() async {
        let key = loadKey, selected = kind == .schools ? [firstItem, secondItem] : [firstItem]
        firstRows = []; secondRows = []; notes = [:]; loadedItems = ""; error = nil
        guard !sourceRevision.isEmpty, !selected.contains(""), Set(selected).count == selected.count,
              let summary, selected.allSatisfy({ summary[$0] != nil }) else { return }
        loading = true
        defer { loading = false }
        do {
            let container = SharedModel.container, targetMonth = monthName, requireRank = kind == .guarantee
            let worker = Task.detached(priority: .userInitiated) {
                let engine = ReportEngine(modelContainer: container)
                var all: [[ReportRow]] = []
                for name in selected {
                    try Task.checkCancellation()
                    guard let item = summary[name] else { throw TreasuryLetterRules.Failure.amounts }
                    let group = ReportGroup(kind: .payroll, month: targetMonth, title: name, count: item.count, total: item.total, byEntity: false)
                    let rows = try await engine.rows(for: group)
                    _ = try TreasuryLetterRules.validateRows(rows, summary: item, requireRank: requireRank)
                    all.append(rows.sorted(by: ReportRow.nameOrder))
                }
                return all
            }
            let result = try await withTaskCancellationHandler { try await worker.value } onCancel: { worker.cancel() }
            guard !Task.isCancelled, key == loadKey else { return }
            firstRows = result[0]; secondRows = result.count > 1 ? result[1] : []; loadedItems = key
        } catch is CancellationError { return }
        catch { guard !Task.isCancelled, key == loadKey else { return }; self.error = error.localizedDescription }
    }
    private func closePreview() {
        if let preview { try? FileManager.default.removeItem(at: preview.deletingLastPathComponent()) }
        preview = nil
    }
    private func prepare() {
        guard isReady, !working, !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else { return }
        do {
            let draft = try TreasuryLetterDraft.make(kind: kind, monthName: monthName, payerIBAN: payerIBAN, beneficiaryIBAN: beneficiaryIBAN,
                header: header, number: number, firstSigner: firstSigner, secondSigner: secondSigner, thirdSigner: thirdSigner,
                guaranteeReason: guaranteeReason, firstRows: firstRows, secondRows: secondRows, notes: notes)
            let blocks = draft.blocks, required = draft.required
            let key = loadKey, payerSnapshot = payerIBAN, accountSnapshot = beneficiaryIBAN
            working = true; error = nil
            Task {
                defer { working = false }
                var temporary: URL?
                do {
                    let worker = Task.detached(priority: .userInitiated) {
                        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("treasury-letter-\(UUID().uuidString)", isDirectory: true)
                        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                        let url = dir.appendingPathComponent("كتاب الخزينة.pdf")
                        do {
                            try TransferAdaptivePDF.write(blocks: blocks, destination: url, sakkalBold: nil, group: false)
                            guard let document = PDFDocument(url: url), document.pageCount == 1 else { throw TreasuryLetterRules.Failure.pageCount }
                            try TransferAdaptivePDF.verify(url, required: required)
                            return url
                        } catch { try? FileManager.default.removeItem(at: dir); throw error }
                    }
                    let url = try await withTaskCancellationHandler { try await worker.value } onCancel: { worker.cancel() }
                    temporary = url
                    guard !Task.isCancelled, key == loadKey, payerSnapshot == payerIBAN, accountSnapshot == beneficiaryIBAN,
                          !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else { throw TreasuryLetterRules.Failure.sourceChanged }
                    previewRevision = previewKey; preview = url; temporary = nil
                } catch {
                    if let temporary { try? FileManager.default.removeItem(at: temporary.deletingLastPathComponent()) }
                    guard !Task.isCancelled else { return }
                    self.error = error.localizedDescription
                }
            }
        } catch { self.error = error.localizedDescription }
    }
}

/// يبني محتوى الكتاب نفسه للمعاينة والاختبارات دون قراءة بيانات حقيقية.
@MainActor enum TreasuryLetterDraft {
    static func make(kind: TreasuryLetterView.Kind, monthName: String, payerIBAN: String, beneficiaryIBAN: String,
                     header: String, number: String, firstSigner: String, secondSigner: String, thirdSigner: String,
                     guaranteeReason: String, firstRows: [ReportRow], secondRows: [ReportRow], notes: [Int: String]) throws
        -> (blocks: [TransferWordDocument.Block], required: [String]) {
        let values = [header, number, firstSigner, secondSigner, thirdSigner] + (kind == .guarantee ? [guaranteeReason] : [])
        guard values.allSatisfy({ !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }),
              TransferRules.validIBAN(payerIBAN), TransferRules.validIBAN(beneficiaryIBAN) else { throw TreasuryLetterRules.Failure.incomplete }
        let payer = ArabicText.latinDigits(payerIBAN).uppercased().filter { !$0.isWhitespace && $0 != "-" }
        let beneficiary = ArabicText.latinDigits(beneficiaryIBAN).uppercased().filter { !$0.isWhitespace && $0 != "-" }
        guard payer != beneficiary else { throw TreasuryLetterRules.Failure.incomplete }
        let firstAmount = try TreasuryLetterRules.total(firstRows.map(\.amount))
        let secondAmount = kind == .schools ? try TreasuryLetterRules.total(secondRows.map(\.amount)) : 0
        let total = kind == .schools ? try TreasuryLetterRules.total([firstAmount, secondAmount]) : firstAmount
        let period = try TransferLetterPeriod(monthName)
        let formatted = try TransferWordValues.make(month: monthName, amount: total, custom: .init(), requested: ["AMOUNT", "AMOUNT_WORDS", "MONTH", "YEAR"])
        guard let amount = formatted["AMOUNT"], let words = formatted["AMOUNT_WORDS"],
              let month = formatted["MONTH"], let year = formatted["YEAR"] else { throw TreasuryLetterRules.Failure.incomplete }
        func display(_ value: Double) throws -> String { ReportSheet.easternDigits(try TransferWordValues.groupedAmount(value)) }
        let reason = kind == .guarantee
            ? "عن \(guaranteeReason)"
            : "عن مبالغ الإيراد النهائي لرسم الطابع الضريبي وبناء المدارس المحسومة من منتسبي قيادتنا وحسب الجدول أدناه"
        let body = "نرجو تحويل مبلغ قدره (\(ReportSheet.easternDigits(amount))) (\(words)) لا غيرها من حسابنا المرقم (\(payer)) المفتوح لديكم إلى حساب (مديرية خزينة الأنبار) المرقم (\(beneficiary)) (مصرف الرافدين / فرع الأنبار) وذلك \(reason) لشهر (\(month) / \(ReportSheet.easternDigits(year))) وتزويدنا بالإشعار اللازم بذلك."
        let table: TransferWordDocument.Table
        if kind == .guarantee {
            var rows: [[String]] = []
            for (i, row) in firstRows.enumerated() {
                rows.append([ReportSheet.easternDigits(String(i + 1)), row.rank, row.name, try display(row.amount), "خزينة الأنبار", notes[row.id] ?? ""])
            }
            rows.append(["", "", "المجموع", try display(total), "", ""])
            table = .init(headers: ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "جهة التنفيذ", "رقم وتاريخ الإضبارة"], rows: rows,
                          font: "Times New Roman", halfPoints: 20, columnWeights: [30, 70, 150, 80, 110, 130], boldLastRow: true)
        } else {
            table = .init(headers: [], rows: [["رسم طابع ضريبي", try display(firstAmount)],
                                               ["بناء مدارس", try display(secondAmount)],
                                               ["المجموع", try display(total)]],
                          font: "Times New Roman", halfPoints: 28, columnWeights: [4, 3], boldLastRow: true, widthFraction: 0.5, alignRight: true)
        }
        let date = ReportSheet.easternDigits(period.signatureDate)
        let signatures = TransferWordGroupDraft.signatureCells([firstSigner, secondSigner, thirdSigner], date: date, adaptivePDF: true)
        let footer = "نسخة منه إلى:\nمديرية خزينة الأنبار للاطلاع وإعلامنا الاستلام ... مع التقدير.\nشعبة المصروفات / شعبة الموازنة / شعبة الرواتب / إجراء اللازم والمتابعة.\nالأرشيف إجراء اللازم."
        let blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: header, font: "Times New Roman", halfPoints: 32, bold: true)),
            .paragraph(.init(text: TransferWordGroupDraft.referenceText(number: ReportSheet.easternDigits(number), date: date, adaptivePDF: true), font: "Times New Roman", halfPoints: 30, bold: true)),
            .paragraph(.init(text: "إلى / مصرف الرافدين / فرع الأنبار", font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(.init(text: "م / تحويل مبلغ", font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(.init(text: body, halfPoints: 28)),
            .table(table),
            .table(.init(headers: [], rows: [signatures], font: "Times New Roman", halfPoints: 32, columnWeights: [1, 1, 1], showBorders: false)),
            .paragraph(.init(text: footer, halfPoints: 24, bold: true))
        ]
        let required = [payer, beneficiary, amount] + (kind == .schools ? [try TransferWordValues.groupedAmount(firstAmount), try TransferWordValues.groupedAmount(secondAmount)] : [])
        return (blocks, required)
    }
}
