import SwiftUI
import SwiftData

struct RefundHistoryView: View {
    let storage: RefundStorage
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse)]) private var months: [PayrollMonth]
    @State private var records: [RefundStorage.Archived] = []
    @State private var editor: RefundDraft?
    @State private var pendingDelete: RefundStorage.Archived?
    @State private var deleting = false
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
                        Text([record.snapshot.source, record.snapshot.payrollMonths,
                              record.snapshot.enteredTotal.map { Money.format(Double($0)) } ?? "",
                              tr("عدد المنتسبين", "Employees") + " " + String(record.snapshot.rows.count)]
                            .filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                    }
                }
                .accessibilityIdentifier("refundHistoryRow")
                .contextMenu {
                    Button(tr("تعديل الدفعة", "Edit batch"), systemImage: "pencil") {
                        editor = RefundDraft(storage: storage, editing: record)
                    }.accessibilityIdentifier("editSavedRefund")
                    Button(role: .destructive) { pendingDelete = record } label: {
                        Label(tr("حذف الدفعة", "Delete batch"), systemImage: "trash")
                    }.accessibilityIdentifier("deleteSavedRefund")
                }
            }
        }
        .navigationTitle(tr("الدفعات المحفوظة", "Saved batches"))
        .disabled(deleting)
        .task {
            do { records = try await Task.detached { [storage] in try storage.archived() }.value }
            catch { self.error = error.localizedDescription }
        }
        .sheet(item: $editor) { session in
            NavigationStack {
                EmployeeRefundView(editing: session, months: months.filter { $0.employeeCount > 0 }.map(\.name)) { updated in
                    if let index = records.firstIndex(where: { $0.id == updated.id }) {
                        records[index] = updated
                        records.sort(by: RefundStorage.Archived.newestFirst)
                    }
                }
            }
        }
        .alert(tr("حذف الدفعة المحفوظة؟", "Delete saved batch?"),
               isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
               presenting: pendingDelete) { record in
            Button(tr("إلغاء", "Cancel"), role: .cancel) {}
            Button(tr("حذف الدفعة", "Delete batch"), role: .destructive) {
                deleting = true
                Task {
                    defer { deleting = false }
                    do {
                        try await Task.detached { [storage] in try storage.delete(record) }.value
                        BackupManager.shared.markChanged()
                        records.removeAll { $0.id == record.id }
                    } catch { self.error = error.localizedDescription }
                }
            }
        } message: { record in
            Text(tr("سيُحذف سجل الدفعة المؤرخة", "Permanently delete the batch dated") + " " + record.snapshot.date.formatted(date: .numeric, time: .omitted) + "؟")
        }
    }
}

struct RefundArchivedDetail: View {
    let snapshot: RefundSnapshot
    @State private var exporting = false
    @State private var error: String?
    @State private var preview: StatementPDF?
    @State private var previewFolder: URL?

    private func output(_ batch: EmployeeRefund.Batch, _ choice: EmployeeRefundExport.Output? = nil, previewPDF: Bool = false) {
        exporting = true
        Task {
            defer { exporting = false }
            do {
                let urls = try await PDFWork.run { try EmployeeRefundExport.make(batch, date: snapshot.date, letters: snapshot.letters, only: choice) }
                guard let first = urls.first else { return }
                let folder = first.deletingLastPathComponent()
                if previewPDF {
                    previewFolder = folder
                    preview = StatementPDF(url: first)
                } else {
                    ActivityShare.present(urls) { _ in try? FileManager.default.removeItem(at: folder) }
                }
            } catch { self.error = error.localizedDescription }
        }
    }

    private func bankFiles(_ batch: EmployeeRefund.Batch) -> some View {
        Section(tr("ملفات المصارف", "Bank files")) {
            ForEach(batch.groups, id: \.code) { group in
                let title = "\(group.bank.name) · \(group.code).csv"
                Button { output(batch, .csv(group.code)) } label: {
                    VStack(alignment: .leading, spacing: 4) {
                        Label(title, systemImage: "square.and.arrow.up")
                        Text(Money.format(Double(group.total)) + " " + tr("د.ع", "IQD") + " · " + tr("عدد المنتسبين", "Employees") + " " + Money.format(Double(group.payments.count)))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            Button(tr("قائمة منتسبي إعادة المبالغ", "Refund roster"), systemImage: "doc.text.magnifyingglass") { output(batch, .roster, previewPDF: true) }
            Button(tr("تصدير جميع الملفات", "Export all files"), systemImage: "square.and.arrow.up") { output(batch) }
        }
    }

    var body: some View {
        List {
            LabeledContent(tr("تاريخ التحويل", "Transfer date")) { Text(snapshot.date, format: .dateTime.year().month().day()) }
            if !snapshot.source.isEmpty { LabeledContent(tr("جهة الإعادة", "Refund source"), value: snapshot.source) }
            LabeledContent(tr("شهر الرواتب", "Payroll month"), value: snapshot.payrollMonths)
            if let batch = try? snapshot.plan() {
                LabeledContent(tr("المجموع الكلي", "Grand total"), value: Money.format(Double(batch.total)))
                Section(tr("كتب الدفعة", "Batch letters")) {
                    ForEach([true, false], id: \.self) { raf in
                        if batch.groups.contains(where: { $0.isRafidain == raf }) {
                            Button(raf ? tr("كتاب الرافدين", "Rafidain letter") : tr("كتاب الرشيد والمصارف الأخرى", "Other banks letter"), systemImage: "doc.text.magnifyingglass") {
                                output(batch, .letter(rafidain: raf), previewPDF: true)
                            }
                        }
                    }
                }
                bankFiles(batch)
            } else {
                Text(tr("راجع بيانات الدفعة من خيار التعديل لتجهيز ملفاتها.", "Review the saved batch details to generate its files.")).foregroundStyle(.secondary)
            }
            Section(tr("المنتسبون", "Employees")) {
                ForEach(snapshot.rows) { row in
                    VStack(alignment: .leading, spacing: 4) {
                        LabeledContent(row.candidate.person.name, value: Money.format(Double(EmployeeRefund.amount(row.amount) ?? 0)))
                        Text(row.candidate.person.bank).font(.caption)
                        Text(row.candidate.person.iban).font(.caption.monospaced()).privacySensitive()
                    }
                }
            }
            if let error { Text(error).foregroundStyle(.red) }
        }
        .disabled(exporting)
        .sheet(item: $preview, onDismiss: {
            if let folder = previewFolder { try? FileManager.default.removeItem(at: folder); previewFolder = nil }
        }) { StatementPreview(file: $0) }
        .navigationTitle(tr("تفاصيل الدفعة", "Batch details"))
    }
}
