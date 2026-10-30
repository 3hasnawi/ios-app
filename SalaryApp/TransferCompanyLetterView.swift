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
    @State private var working = false
    @State private var error: String?
    @State private var previewURL: URL?
    @State private var showPreview = false
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
                Button(tr("معاينة كتاب PDF", "Preview PDF letter"), systemImage: "doc.text.magnifyingglass") { prepare() }
                    .disabled(!sourceIsCurrent || references.isEmpty || !TransferRules.validIBAN(payerIBAN) || working)
                if references.isEmpty { Text(tr("استورد حزمة مراجع الكتب من صفحة ملفات التحويل.", "Import the letter reference package from transfer files.")).font(.footnote).foregroundStyle(.secondary) }
                if !TransferRules.validIBAN(payerIBAN) { Text(tr("أدخل IBAN الجهة الدافعة من إعدادات ملفات التحويل.", "Enter the payer IBAN in transfer settings.")).font(.footnote).foregroundStyle(.secondary) }
                if !sourceIsCurrent { Text(tr("تغيّرت البيانات؛ ارجع إلى ملفات التحويل لإعادة التحميل.", "Data changed; return to transfer files to reload.")).font(.footnote).foregroundStyle(.orange) }
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
                            ToolbarItem(placement: .confirmationAction) {
                                Button(tr("مشاركة PDF", "Share PDF"), systemImage: "square.and.arrow.up") { ActivityShare.present([previewURL]) }
                                    .disabled(!sourceIsCurrent)
                            }
                            ToolbarItem(placement: .cancellationAction) { Button(tr("تم", "Done")) { showPreview = false } }
                        }
                }
            }
        }
        .onDisappear { generation?.cancel() }
    }
    private func prepare() {
        guard sourceIsCurrent, !working else { return }
        working = true; error = nil
        generation = Task {
            defer { working = false }
            do {
                let url = try await TransferCompanyLetter.make(figures: figures, payerIBAN: payerIBAN, references: references)
                guard !Task.isCancelled, sourceIsCurrent else {
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
