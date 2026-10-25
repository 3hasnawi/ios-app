import SwiftUI
import PDFKit

/// معاينة الجدول كما سيُصدَّر (PDF بحجم A4)، مع زر التصدير.
/// للكشوف الخاصة (صندوق الشهداء، عقد الإطعام، الرعاية الصحية) يمكن التبديل بين الكشف وقائمة الأسماء.
struct ReportPreviewSheet: View {
    let group: ReportGroup
    let rows: [ReportRow]
    var fundRows: [FundReport.Row]? = nil
    var foodRows: [FoodContractReport.Row]? = nil
    var healthLists: HealthCareReport.Lists? = nil

    enum Part: String { case special, list }
    @State private var part: Part = .special
    @State private var listPDF: URL?
    @State private var specialPDF: URL?
    @State private var failed: String?
    @State private var showExport = false
    @Environment(\.dismiss) private var dismiss

    private var hasSpecial: Bool { fundRows != nil || foodRows != nil || healthLists != nil }
    private var specialTitle: String {
        if fundRows != nil { return tr("كشف حسب الفئة", "By category") }
        if foodRows != nil { return tr("كشف عقد الإطعام", "Food contract") }
        return tr("المشتركون وغير المشتركين", "Subscribers")
    }
    private var current: URL? { hasSpecial && part == .special ? specialPDF : listPDF }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                if hasSpecial {
                    Picker("", selection: $part) {
                        Text(specialTitle).tag(Part.special)
                        Text(tr("قائمة الأسماء", "Name list")).tag(Part.list)
                    }
                    .pickerStyle(.segmented)
                    .padding(.horizontal, 16)
                    .padding(.vertical, 8)
                }
                ZStack {
                    Color(.systemGray5)
                    if let current {
                        PDFPreview(url: current)
                            .id(current)
                            .accessibilityIdentifier("reportPDFPreview")
                    } else if let failed {
                        ContentUnavailableView(failed, systemImage: "exclamationmark.triangle")
                    } else {
                        ProgressView(tr("جارٍ تجهيز الجدول…", "Preparing the table…"))
                    }
                }
            }
            .navigationTitle(group.title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(tr("إغلاق", "Close"), systemImage: "xmark") { dismiss() }
                }
                ToolbarItem(placement: .primaryAction) {
                    Button(tr("تصدير", "Export"), systemImage: ReportSymbols.export) { showExport = true }
                        .labelStyle(.iconOnly)
                        .accessibilityIdentifier("previewExport")
                    .disabled(current == nil)
                }
            }
            .sheet(isPresented: $showExport) {
                ReportExportSheet(group: group, rows: rows, fundRows: fundRows, foodRows: foodRows, healthLists: healthLists)
            }
        }
        .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
        .task(id: part) {
            failed = nil
            do {
                // تجهيز ما يراه المستخدم فقط، دون إنشاء Excel أو كشف مخفي.
                if hasSpecial && part == .special {
                    if specialPDF == nil {
                        let url = try await ReportFiles.preview(group: group, rows: rows,
                                                               fundRows: fundRows, foodRows: foodRows, healthLists: healthLists)
                        guard !Task.isCancelled else { return }
                        specialPDF = url
                    }
                } else if listPDF == nil {
                    let url = try await ReportFiles.preview(group: group, rows: rows)
                    guard !Task.isCancelled else { return }
                    listPDF = url
                }
            } catch {
                guard !Task.isCancelled else { return }
                failed = tr("تعذّر تجهيز الجدول: \(error.localizedDescription)", "Couldn't prepare the table: \(error.localizedDescription)")
            }
        }
    }
}

/// عرض PDF بكل صفحاته، مع التكبير بالأصابع.
struct PDFPreview: UIViewRepresentable {
    let url: URL

    func makeUIView(context: Context) -> PDFView {
        let view = PDFView()
        view.autoScales = true
        view.displayMode = .singlePageContinuous
        view.displayDirection = .vertical
        view.backgroundColor = .systemGray5
        view.pageShadowsEnabled = true
        context.coordinator.load(url, into: view)
        return view
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func updateUIView(_ view: PDFView, context: Context) {
        context.coordinator.load(url, into: view)
    }

    static func dismantleUIView(_ view: PDFView, coordinator: Coordinator) {
        coordinator.task?.cancel()
        view.document = nil
    }

    @MainActor final class Coordinator {
        var task: Task<Void, Never>?
        private var loadedURL: URL?

        func load(_ url: URL, into view: PDFView) {
            guard loadedURL != url else { return }
            loadedURL = url
            task?.cancel()
            // فتح ملف متعدد الصفحات لا يحجز خيط التنقل واللمس.
            task = Task {
                let started = Date()
                let document = await Task.detached(priority: .userInitiated) { PDFDocument(url: url) }.value
                guard !Task.isCancelled else { return }
                view.document = document
                PerfLog.record("pdfOpen", since: started)
            }
        }
    }
}

/// يحمّل صفوف الكشف (والكشف الخاص إن وُجد) ثم يعرض المعاينة: يُفتح من البطاقات والقوائم مباشرة.
struct ReportPreviewLoader: View {
    let group: ReportGroup
    @State private var rows: [ReportRow]?
    @State private var fundRows: [FundReport.Row]?
    @State private var healthLists: HealthCareReport.Lists?

    private var foodRows: [FoodContractReport.Row]? {
        guard group.kind == .payroll, FoodContractReport.applies(to: group.title), let rows else { return nil }
        return FoodContractReport.rows(from: rows)
    }

    var body: some View {
        Group {
            if let rows {
                ReportPreviewSheet(group: group, rows: rows, fundRows: fundRows, foodRows: foodRows, healthLists: healthLists)
            } else {
                ProgressView(tr("جارٍ تجهيز الجدول…", "Preparing the table…"))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .presentationDetents([.large])
            }
        }
        .task {
            let container = SharedModel.container
            let group = group
            if group.kind == .payroll, HealthCareReport.applies(to: group.title) {
                healthLists = try? await Task.detached(priority: .userInitiated) {
                    try await ReportEngine(modelContainer: container).healthLists(month: group.month, title: group.title)
                }.value
            }
            if group.kind == .payroll, FundReport.applies(to: group.title) {
                fundRows = try? await Task.detached(priority: .userInitiated) {
                    try await ReportEngine(modelContainer: container).fundSummary(month: group.month, title: group.title)
                }.value
            }
            rows = (try? await Task.detached(priority: .userInitiated) {
                try await ReportEngine(modelContainer: container).rows(for: group)
            }.value) ?? []
        }
    }
}
