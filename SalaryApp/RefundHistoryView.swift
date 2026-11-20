import SwiftUI

struct RefundHistoryView: View {
    let storage: RefundStorage
    @State private var records: [RefundStorage.Archived] = []
    @State private var error: String?
    var body: some View {
        List {
            if let error { Text(error).foregroundStyle(.red) }
            else if records.isEmpty { Text(tr("لا توجد دفعات محفوظة.", "No saved batches.")) }
            ForEach(records) { record in
                NavigationLink {
                    RefundArchivedDetail(snapshot: record.snapshot)
                } label: {
                    VStack(alignment: .leading) {
                        Text(record.snapshot.date, format: .dateTime.year().month().day())
                        Text([record.snapshot.source, record.snapshot.month ?? "",
                              tr("عدد المنتسبين", "Employees") + " " + String(record.snapshot.rows.count)]
                            .filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        }
        .navigationTitle(tr("الدفعات المحفوظة", "Saved batches"))
        .task {
            do { records = try await Task.detached { [storage] in try storage.archived() }.value }
            catch { self.error = error.localizedDescription }
        }
    }
}

private struct RefundArchivedDetail: View {
    let snapshot: RefundSnapshot
    @State private var exporting = false
    @State private var error: String?
    var body: some View {
        List {
            LabeledContent(tr("تاريخ التحويل", "Transfer date")) { Text(snapshot.date, format: .dateTime.year().month().day()) }
            if !snapshot.source.isEmpty { LabeledContent(tr("جهة الإعادة", "Refund source"), value: snapshot.source) }
            if let month = snapshot.month { LabeledContent(tr("شهر الرواتب", "Payroll month"), value: month) }
            Section(tr("المنتسبون", "Employees")) {
                ForEach(snapshot.rows) { row in
                    VStack(alignment: .leading, spacing: 4) {
                        LabeledContent(row.candidate.person.name, value: Money.format(Double(EmployeeRefund.amount(row.amount) ?? 0)))
                        Text(row.candidate.person.bank).font(.caption)
                        Text(row.candidate.person.iban).font(.caption.monospaced()).privacySensitive()
                    }
                }
            }
            if let batch = try? snapshot.plan() {
                LabeledContent(tr("المجموع الكلي", "Grand total"), value: Money.format(Double(batch.total)))
                Button(tr("تصدير ملفات الدفعة", "Export batch files"), systemImage: "square.and.arrow.up") {
                    exporting = true
                    Task {
                        defer { exporting = false }
                        do {
                            let urls = try await PDFWork.run { try EmployeeRefundExport.make(batch, date: snapshot.date, letters: snapshot.letters) }
                            ActivityShare.present(urls) { _ in
                                if let folder = urls.first?.deletingLastPathComponent() { try? FileManager.default.removeItem(at: folder) }
                            }
                        } catch { self.error = error.localizedDescription }
                    }
                }.disabled(exporting)
            }
            if let error { Text(error).foregroundStyle(.red) }
        }
        .navigationTitle(tr("تفاصيل الدفعة", "Batch details"))
    }
}
