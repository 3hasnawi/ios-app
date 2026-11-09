import SwiftUI
import UIKit
import CoreText

/// لقطة ثابتة لما تعرضه الفلاتر؛ لا تتغير أثناء إنشاء الكشف.
nonisolated struct SalaryTrustReportRequest: Identifiable, Sendable {
    let id = UUID()
    let entries: [SalaryTrust.Entry]
    let month: String
    let status: String
    let query: String
    let ledgerData: Data
    let preview: Bool
}

nonisolated enum SalaryTrustReport {
    private static func amount(_ n: Int64) -> String {
        let f = NumberFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.numberStyle = .decimal; f.maximumFractionDigits = 0
        return f.string(from: NSNumber(value: n)) ?? String(n)
    }
    private static func clean(_ text: String) -> String {
        text.components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.joined(separator: " ")
    }
    static func sheet(_ request: SalaryTrustReportRequest) throws -> ReportSheet {
        let ledger = try SalaryTrust.decode(request.ledgerData)
        guard !request.entries.isEmpty,
              Set(request.entries.map(\.id)).count == request.entries.count else { throw SalaryTrust.invalid(tr("لا توجد قيود صالحة للتصدير.", "There are no valid entries to export.")) }
        let byID = Dictionary(uniqueKeysWithValues: ledger.entries.map { ($0.id, $0) })
        guard request.entries.allSatisfy({ byID[$0.id] == $0 }) else { throw SalaryTrust.invalid(tr("تغيّرت بيانات الأمانات؛ أعد فتح الكشف.", "Trust data changed; reopen the report.")) }
        let filters = [request.month, request.status, request.query.isEmpty ? "" : tr("بحث: \(request.query)", "Search: \(request.query)")].filter { !$0.isEmpty }.joined(separator: " · ")
        let columns: [ReportSheet.Column] = [
            .init(title: tr("ت", "No."), width: 5, kind: .text),
            .init(title: tr("الاسم", "Name"), width: 30, kind: .text),
            .init(title: tr("الشهر", "Month"), width: 15, kind: .text),
            .init(title: tr("الصرف", "Payment"), width: 17, kind: .text),
            .init(title: tr("الحالة بالملف", "File status"), width: 20, kind: .text),
            .init(title: tr("الأمانة", "Deposit"), width: 18, kind: .text),
            .init(title: tr("المصروف", "Paid"), width: 18, kind: .text),
            .init(title: tr("المتبقي", "Remaining"), width: 18, kind: .text),
            .init(title: tr("الملاحظات", "Notes"), width: 35, kind: .text),
            .init(title: tr("تاريخ الصرف", "Payment date"), width: 18, kind: .text),
        ]
        var rows = request.entries.enumerated().map { index, entry in
            [String(index + 1), clean(entry.name), SalaryTrust.monthLabel(entry.year * 100 + entry.month),
             SalaryTrust.paymentStatus(entry), clean(entry.importedStatus), amount(entry.amount),
             entry.reviewPending ? "—" : amount(entry.paid), entry.reviewPending ? "—" : amount(entry.remaining), clean(entry.displayNotes), clean(entry.displayPaymentDate)]
        }
        let reviewCount = request.entries.filter(\.reviewPending).count
        rows.append(["", tr("المتبقي المؤكد", "Confirmed outstanding"), "", reviewCount > 0 ? tr("للمراجعة: \(Num.format(reviewCount))", "Review: \(reviewCount)") : "", "", "", "", amount(SalaryTrust.outstanding(request.entries)), "", ""])
        return ReportSheet(header: UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader,
            title: tr("أمانات الرواتب · \(filters)", "Salary trust · \(filters)"), columns: columns,
            rows: rows, amounts: Array(repeating: 0, count: rows.count), showTotal: false)
    }

    static func makePDF(_ request: SalaryTrustReportRequest) async throws -> URL {
        try await PDFWork.run { try SalaryTrustPDF.write(sheet(request)) }
    }
    static func makeExcel(_ request: SalaryTrustReportRequest) async throws -> URL {
        try await PDFWork.run {
            let report = try sheet(request)
            let last = XLSXWriter.letter(report.columns.count - 1)
            var body = "<row r=\"1\">" + XLSXWriter.textCell("A1", report.header, style: 1) + "</row>"
            body += "<row r=\"2\">" + XLSXWriter.textCell("A2", report.title, style: 2) + "</row>"
            body += "<row r=\"3\">" + report.columns.enumerated().map { XLSXWriter.textCell("\(XLSXWriter.letter($0.offset))3", $0.element.title, style: 3) }.joined() + "</row>"
            for (index, values) in report.rows.enumerated() {
                let row = index + 4
                var cells = ""
                for column in values.indices {
                    let reference = XLSXWriter.letter(column) + String(row)
                    var value: Int64?
                    if index < request.entries.count {
                        let entry = request.entries[index]
                        if column == 0 { value = Int64(index + 1) }
                        if column == 5 { value = entry.amount }
                        if column == 6 && !entry.reviewPending { value = entry.paid }
                        if column == 7 && !entry.reviewPending { value = entry.remaining }
                    } else if column == 7 { value = SalaryTrust.outstanding(request.entries) }
                    if let value { cells += "<c r=\"\(reference)\" s=\"6\"><v>\(value)</v></c>" }
                    else { cells += XLSXWriter.textCell(reference, values[column], style: 4) }
                }
                body += "<row r=\"\(row)\">\(cells)</row>"
            }
            let widths = report.columns.enumerated().map { "<col min=\"\($0.offset + 1)\" max=\"\($0.offset + 1)\" width=\"\($0.element.width)\" customWidth=\"1\"/>" }.joined()
            let xml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
            <sheetPr><pageSetUpPr fitToPage="1"/></sheetPr><sheetViews><sheetView rightToLeft="true" workbookViewId="0"><pane ySplit="3" topLeftCell="A4" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>
            <cols>\(widths)</cols><sheetData>\(body)</sheetData><mergeCells count="2"><mergeCell ref="A1:\(last)1"/><mergeCell ref="A2:\(last)2"/></mergeCells>
            <pageMargins left="0.4" right="0.4" top="0.5" bottom="0.5" header="0.3" footer="0.3"/><pageSetup paperSize="9" orientation="landscape" fitToWidth="1" fitToHeight="0"/>
            </worksheet>
            """
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("trust-report-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let file = folder.appendingPathComponent("كشف أمانات الرواتب.xlsx")
            do { try XLSXWriter.package(worksheet: xml, styles: XLSXWriter.styles, printTitles: "$1:$3", to: file); return file }
            catch { try? FileManager.default.removeItem(at: folder); throw error }
        }
    }
}

/// يظل الكشف مرتبطاً بلقطة الدفتر التي اختارها المستخدم.
struct SalaryTrustPreviewSheet: View {
    let request: SalaryTrustReportRequest
    @Environment(\.dismiss) private var dismiss
    @AppStorage(SalaryTrust.settingsKey) private var stored = Data()
    @State private var pdf: URL?
    @State private var busy = false
    @State private var message = ""

    var body: some View {
        NavigationStack {
            VStack {
                if stored != request.ledgerData {
                    Text(tr("تغيّرت بيانات الأمانات؛ أغلق الكشف وأعد فتحه.", "Trust data changed; close and reopen the report."))
                        .foregroundStyle(.orange).padding()
                } else if let pdf {
                    PDFPreview(url: pdf)
                } else if !message.isEmpty {
                    Text(message).foregroundStyle(.secondary).padding()
                } else {
                    ProgressView(tr("جارٍ تجهيز المعاينة…", "Preparing preview…"))
                }
            }
            .navigationTitle(tr("معاينة الأمانات", "Trust preview"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(tr("إغلاق", "Close")) { dismiss() } }
                ToolbarItem(placement: .primaryAction) {
                    Menu(tr("تصدير", "Export")) {
                        Button("PDF") { share(excel: false) }
                        Button("Excel") { share(excel: true) }
                    }.disabled(busy || stored != request.ledgerData)
                }
            }
            .task {
                do { pdf = try await SalaryTrustReport.makePDF(request) }
                catch { message = error.localizedDescription }
            }
            .alert(tr("تعذّر التصدير", "Export failed"), isPresented: Binding(get: { !message.isEmpty && pdf != nil }, set: { if !$0 { message = "" } })) {
                Button(tr("حسناً", "OK"), role: .cancel) { message = "" }
            } message: { Text(message) }
        }
    }
    @MainActor private func share(excel: Bool) {
        guard !busy, stored == request.ledgerData else { return }
        busy = true
        Task {
            defer { busy = false }
            do {
                let file: URL
                if excel { file = try await SalaryTrustReport.makeExcel(request) }
                else if let pdf { file = pdf }
                else { file = try await SalaryTrustReport.makePDF(request) }
                guard stored == request.ledgerData else { message = tr("تغيّرت بيانات الأمانات؛ أعد فتح الكشف.", "Trust data changed; reopen the report."); return }
                await FilePicker.waitUntilSettled()
                ActivityShare.present([file])
            } catch { message = error.localizedDescription }
        }
    }
}

struct SalaryTrustExportSheet: View {
    let request: SalaryTrustReportRequest
    @Environment(\.dismiss) private var dismiss
    @AppStorage(SalaryTrust.settingsKey) private var stored = Data()
    @State private var busy = false
    @State private var message = ""

    var body: some View {
        NavigationStack {
            List {
                if stored != request.ledgerData {
                    Text(tr("تغيّرت بيانات الأمانات؛ أغلق التصدير وأعد فتحه.", "Trust data changed; close and reopen export."))
                        .foregroundStyle(.orange)
                }
                Button { export(excel: false) } label: { Label(tr("تصدير PDF", "Export PDF"), systemImage: "doc.richtext") }
                Button { export(excel: true) } label: { Label(tr("تصدير Excel", "Export Excel"), systemImage: "tablecells") }
                if busy { ProgressView(tr("جارٍ تجهيز الكشف…", "Preparing report…")) }
            }
            .disabled(busy || stored != request.ledgerData)
            .navigationTitle(tr("تصدير الأمانات", "Export trust"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إغلاق", "Close")) { dismiss() } } }
            .alert(tr("تعذّر التصدير", "Export failed"), isPresented: Binding(get: { !message.isEmpty }, set: { if !$0 { message = "" } })) {
                Button(tr("حسناً", "OK"), role: .cancel) { message = "" }
            } message: { Text(message) }
        }
    }
    @MainActor private func export(excel: Bool) {
        guard !busy, stored == request.ledgerData else { return }
        busy = true
        Task {
            defer { busy = false }
            do {
                let file = try await (excel ? SalaryTrustReport.makeExcel(request) : SalaryTrustReport.makePDF(request))
                guard stored == request.ledgerData else { message = tr("تغيّرت بيانات الأمانات؛ أعد فتح التصدير.", "Trust data changed; reopen export."); return }
                await FilePicker.waitUntilSettled()
                ActivityShare.present([file])
            } catch { message = error.localizedDescription }
        }
    }
}
