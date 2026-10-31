import SwiftUI
import SwiftData

/// مراجعة البيانات ثم معاينة كتاب الشركة قبل مشاركته بصيغة PDF.
struct TransferCompanyLetterView: View {
    let figures: TransferLetterFigures
    let sourceKey: String
    let payerIBAN: String
    let references: Data
    @Query private var months: [PayrollMonth]
    @AppStorage("bankStatementIBAN") private var currentPayer = ""
    @AppStorage(TransferReferencePack.settingsKey) private var currentReferences = Data()
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var currentCustomization = Data()
    @AppStorage(TransferHeaderFont.boldSettingsKey) private var currentBoldFont = Data()
    @AppStorage(TransferHeaderFont.regularSettingsKey) private var currentRegularFont = Data()
    @State private var working = false
    @State private var error: String?
    @State private var previewURL: URL?
    @State private var showPreview = false
    @State private var wordResult: TransferWordDraftGenerator.Result?
    @State private var showWordPreview = false
    @State private var wordCustomizationSnapshot = Data()
    @State private var wordBoldSnapshot = Data()
    @State private var generation: Task<Void, Never>?

    private var sourceIsCurrent: Bool {
        let record = months.first { $0.name == figures.month }
        return record != nil && currentPayer == payerIBAN && currentReferences == references
            && "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    var body: some View {
        List {
            Section(tr("بيانات الكتاب", "Letter details")) {
                LabeledContent(tr("الجهة", "Beneficiary"), value: tr("شركة عراق الجوال", "Iraq Al-Jawwal Company"))
                LabeledContent(tr("الشهر", "Month"), value: figures.month)
                LabeledContent(tr("المبلغ", "Amount"), value: Money.format(figures.foodCompany))
                LabeledContent("IBAN", value: payerIBAN.isEmpty ? "—" : payerIBAN).privacySensitive()
            }
            Section {
                NavigationLink {
                    TransferLetterSettingsView(bookID: "food_company")
                } label: {
                    Label(tr("تخصيص بيانات الكتب", "Customize letter details"), systemImage: "slider.horizontal.3")
                }
                Button(tr("معاينة مسودة PDF", "Preview PDF draft"), systemImage: "doc.text.magnifyingglass") { prepareWord() }
                    .disabled(!sourceIsCurrent || references.isEmpty || !TransferRules.validIBAN(payerIBAN) || working)
                if references.isEmpty { Text(tr("استورد حزمة مراجع الكتب من صفحة ملفات التحويل.", "Import the letter reference package from transfer files.")).font(.footnote).foregroundStyle(.secondary) }
                if !TransferRules.validIBAN(payerIBAN) { Text(tr("أدخل IBAN الجهة الدافعة من إعدادات ملفات التحويل.", "Enter the payer IBAN in transfer settings.")).font(.footnote).foregroundStyle(.secondary) }
                if !sourceIsCurrent { Text(tr("تغيّرت البيانات؛ ارجع إلى ملفات التحويل لإعادة التحميل.", "Data changed; return to transfer files to reload.")).font(.footnote).foregroundStyle(.orange) }
                if currentBoldFont.isEmpty { Text(tr("استورد خط ساكال العريض من تخصيص الكتب لإعداد العدد والتاريخ.", "Import the bold Sakkal font in letter settings to prepare the reference and date.")).font(.footnote).foregroundStyle(.orange) }
                Text(tr("اضبط العدد والترويسة واللجنة والهامش ورقم عقد الإطعام من تخصيص الكتب. المشاركة معطّلة حتى اكتمال التدقيق.", "Set the reference, header, committee, footer and food contract reference in letter settings. Sharing remains disabled until verification is complete.")).font(.footnote).foregroundStyle(.secondary)
            }
        }
        .navigationTitle(tr("كتاب شركة عراق الجوال", "Company transfer letter"))
        .navigationBarTitleDisplayMode(.inline)
        .overlay { if working { ProgressView(tr("إعداد الكتاب", "Preparing letter")).padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16)) } }
        .alert(tr("تعذّر إعداد الكتاب", "Couldn't prepare letter"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
        .sheet(isPresented: $showPreview, onDismiss: {
            if let previewURL { try? FileManager.default.removeItem(at: previewURL.deletingLastPathComponent()) }
            previewURL = nil
        }) {
            NavigationStack {
                if let previewURL {
                    PDFPreview(url: previewURL)
                        .navigationTitle(tr("معاينة الكتاب", "Letter preview"))
                        .navigationBarTitleDisplayMode(.inline)
                        .toolbar {
                            ToolbarItem(placement: .cancellationAction) { Button(tr("تم", "Done")) { showPreview = false } }
                        }
                }
            }
        }
        .sheet(isPresented: $showWordPreview, onDismiss: {
            if let wordResult { try? FileManager.default.removeItem(at: wordResult.letter.deletingLastPathComponent()) }
            wordResult = nil
        }) {
            NavigationStack {
                if let wordResult {
                    VStack(spacing: 0) {
                        Label(tr("مسودة تجريبية؛ راجع النص والمبلغ والحسابات والتواقيع قبل اعتمادها.", "Experimental draft: review the text, amount, accounts and signatures before approval."), systemImage: "exclamationmark.triangle")
                            .font(.footnote).foregroundStyle(.orange).padding(10).frame(maxWidth: .infinity).background(.thinMaterial)
                        if !sourceIsCurrent || currentCustomization != wordCustomizationSnapshot || currentBoldFont != wordBoldSnapshot { Text(tr("تغيّرت البيانات؛ أغلق المعاينة وأعد تحميل الشهر.", "Data changed; close the preview and reload the month.")).foregroundStyle(.orange).padding(8) }
                        PDFPreview(url: wordResult.letter)
                    }
                    .navigationTitle(tr("مسودة PDF", "PDF draft"))
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("تم", "Done")) { showWordPreview = false } } }
                }
            }
        }
        .onDisappear { generation?.cancel() }
    }
    private func prepareWord() {
        guard sourceIsCurrent, !working else { return }
        let snapshot = currentCustomization
        let boldSnapshot = currentBoldFont
        let customization: TransferLetterCustomization
        let referenceHashes: [String: String]?
        do {
            let store = try (snapshot.isEmpty ? TransferLetterCustomizationStore() : JSONDecoder().decode(TransferLetterCustomizationStore.self, from: snapshot))
            customization = store.effective(for: "food_company")
            referenceHashes = store.wordReferenceHashes
        } catch { self.error = tr("تعذّر قراءة تخصيص الكتاب.", "Could not read letter customization."); return }
        let snapshotFigures = figures, snapshotReferences = references, snapshotPayer = payerIBAN
        working = true; error = nil
        generation = Task {
            defer { working = false }
            do {
                let worker = Task.detached(priority: .userInitiated) {
                    if let referenceHashes { try TransferWordDefaults.verifyHashes(referenceHashes, data: snapshotReferences) }
                    return try TransferWordDraftGenerator.make(bookID: "food_company", figures: snapshotFigures, entities: [],
                        entity: nil, individualPlan: nil, cashList: nil, references: snapshotReferences,
                        accountLinks: Data(), payerName: "", payerIBAN: snapshotPayer, customization: customization,
                        sakkalBold: boldSnapshot.isEmpty ? nil : boldSnapshot, adaptivePDF: true)
                }
                let result = try await withTaskCancellationHandler { try await worker.value } onCancel: { worker.cancel() }
                guard !Task.isCancelled, sourceIsCurrent, snapshot == currentCustomization, boldSnapshot == currentBoldFont else {
                    try? FileManager.default.removeItem(at: result.letter.deletingLastPathComponent()); return
                }
                wordCustomizationSnapshot = snapshot; wordBoldSnapshot = boldSnapshot
                wordResult = result; generation = nil; showWordPreview = true
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled else { return }; self.error = error.localizedDescription }
        }
    }
    private func prepare() {
        guard sourceIsCurrent, !working else { return }
        let snapshot = currentCustomization
        let fontSnapshot = currentBoldFont
        let regularFontSnapshot = currentRegularFont
        let customization: TransferLetterCustomization
        do {
            let store = try (snapshot.isEmpty ? TransferLetterCustomizationStore() : JSONDecoder().decode(TransferLetterCustomizationStore.self, from: snapshot))
            customization = store.effective(for: "food_company")
        } catch {
            self.error = tr("تعذّر قراءة تخصيص الكتاب؛ لم تُنشأ المعاينة.", "Could not read letter customization; no preview was created.")
            return
        }
        working = true; error = nil
        generation = Task {
            defer { working = false }
            do {
                let url = try await TransferCompanyLetter.make(figures: figures, payerIBAN: payerIBAN, references: references, customization: customization, monthFontData: fontSnapshot.isEmpty ? nil : fontSnapshot)
                guard !Task.isCancelled, sourceIsCurrent, snapshot == currentCustomization,
                      fontSnapshot == currentBoldFont, regularFontSnapshot == currentRegularFont else {
                    try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
                    return
                }
                previewURL = url
                generation = nil
                showPreview = true
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled else { return }; self.error = error.localizedDescription }
        }
    }
}
