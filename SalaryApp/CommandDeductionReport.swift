import SwiftData
import SwiftUI

extension ReportEngine {
    func commandDeductionAmounts(month: String) throws -> [CommandDeduction: Int64] {
        var request = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        request.propertiesToFetch = [\.rawColumnsData, \.detailsData, \.itemsData]
        var totals = CommandDeductionTotals()
        try modelContext.enumerate(request, batchSize: 256) { employee in
            try Task.checkCancellation()
            try totals.add(raw: employee.rawColumns, details: employee.details, items: employee.items)
        }
        return try totals.amounts()
    }
}

struct CommandDeductionReportView: View {
    let month: PayrollMonth
    @State private var preview: URL?
    @State private var failure: String?

    var body: some View {
        Group {
            if let preview { PDFPreview(url: preview) }
            else if let failure {
                ContentUnavailableView(tr("تعذّر إعداد الكشف", "Could not prepare statement"),
                                       systemImage: "doc.text", description: Text(failure))
            } else { ProgressView(tr("جارٍ تجهيز الكشف…", "Preparing statement…")) }
        }
        .navigationTitle(WorkReport.commandDeductions.title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if let preview {
                ShareLink(item: preview) { Label(tr("مشاركة PDF", "Share PDF"), systemImage: "square.and.arrow.up") }
            }
        }
        .task(id: "\(month.name)|\(month.importedAt.timeIntervalSince1970)") {
            preview = nil
            failure = nil
            let name = month.name
            do {
                let container = SharedModel.container
                let worker = Task.detached(priority: .userInitiated) {
                    try await ReportEngine(modelContainer: container).commandDeductionAmounts(month: name)
                }
                let amounts = try await withTaskCancellationHandler { try await worker.value } onCancel: { worker.cancel() }
                try Task.checkCancellation()
                let url = try await PDFWork.run {
                    let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
                    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                    let filename = "كشف استقطاعات القيادة - " + name.replacingOccurrences(of: "/", with: "-")
                    let url = folder.appendingPathComponent(filename).appendingPathExtension("pdf")
                    try UIGraphicsPDFRenderer(bounds: CommandDeductionPDF.page).writePDF(to: url) { context in
                        CommandDeductionPDF.render(month: name, amounts: amounts, in: context)
                    }
                    return url
                }
                try Task.checkCancellation()
                preview = url
            } catch {
                if !Task.isCancelled { failure = error.localizedDescription }
            }
        }
    }
}
