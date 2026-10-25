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
    @State private var listFiles: (excel: URL, pdf: URL)?
    @State private var specialFiles: (excel: URL, pdf: URL)?
    @State private var failed: String?
    @State private var showExport = false
    @Environment(\.dismiss) private var dismiss

    private var hasSpecial: Bool { fundRows != nil || foodRows != nil || healthLists != nil }
    private var specialTitle: String {
        if fundRows != nil { return tr("كشف حسب الفئة", "By category") }
        if foodRows != nil { return tr("كشف عقد الإطعام", "Food contract") }
        return tr("المشتركون وغير المشتركين", "Subscribers")
    }
    private var current: URL? { hasSpecial && part == .special ? specialFiles?.pdf : listFiles?.pdf }

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
                    .disabled(listFiles == nil)
                }
            }
            .sheet(isPresented: $showExport) {
                ReportExportSheet(group: group, rows: rows, fundRows: fundRows, foodRows: foodRows, healthLists: healthLists)
            }
        }
        .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
        .task {
            do {
                if let fundRows { specialFiles = try await FundReport.files(fundRows, month: group.month) }
                if let foodRows { specialFiles = try await FoodContractReport.files(foodRows, month: group.month) }
                if let healthLists { specialFiles = try await HealthCareReport.files(healthLists, month: group.month) }
                listFiles = try await ReportFiles.make(group: group, rows: rows)
            } catch {
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
        view.document = PDFDocument(url: url)
        return view
    }

    func updateUIView(_ view: PDFView, context: Context) {
        if view.document?.documentURL != url { view.document = PDFDocument(url: url) }
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
