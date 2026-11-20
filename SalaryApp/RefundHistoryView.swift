import SwiftUI
import SwiftData

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
                    RefundArchivedDetail(record: record, storage: storage) { updated in
                        records.removeAll { $0.id == record.id }
                        if let updated { records.append(updated); records.sort(by: RefundStorage.Archived.newestFirst) }
                    }
                } label: {
                    VStack(alignment: .leading) {
                        Text(record.snapshot.date, format: .dateTime.year().month().day())
                        Text([record.snapshot.source, record.snapshot.payrollMonths,
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
    @State private var record: RefundStorage.Archived
    let storage: RefundStorage
    let onChange: (RefundStorage.Archived?) -> Void
    @Environment(\.dismiss) private var dismiss
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse)]) private var months: [PayrollMonth]
    @State private var editor: RefundDraft?
    @State private var confirmDelete = false
    @State private var deleting = false
    private var snapshot: RefundSnapshot { record.snapshot }

    init(record: RefundStorage.Archived, storage: RefundStorage, onChange: @escaping (RefundStorage.Archived?) -> Void) {
        _record = State(initialValue: record)
        self.storage = storage
        self.onChange = onChange
    }
    @State private var exporting = false
    @State private var error: String?
    var body: some View {
        List {
            LabeledContent(tr("تاريخ التحويل", "Transfer date")) { Text(snapshot.date, format: .dateTime.year().month().day()) }
            if !snapshot.source.isEmpty { LabeledContent(tr("جهة الإعادة", "Refund source"), value: snapshot.source) }
            LabeledContent(tr("شهر الرواتب", "Payroll month"), value: snapshot.payrollMonths)
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
            Button(role: .destructive) { confirmDelete = true } label: {
                Label(tr("حذف الدفعة المحفوظة", "Delete saved batch"), systemImage: "trash")
            }
            if let error { Text(error).foregroundStyle(.red) }
        }
        .navigationTitle(tr("تفاصيل الدفعة", "Batch details"))
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button(tr("تعديل الدفعة", "Edit batch"), systemImage: "pencil") {
                    editor = RefundDraft(storage: storage, editing: record)
                }
            }
        }
        .disabled(exporting || deleting)
        .sheet(item: $editor) { session in
            NavigationStack {
                EmployeeRefundView(editing: session, months: months.filter { $0.employeeCount > 0 }.map(\.name)) { updated in
                    record = updated
                    onChange(updated)
                }
            }
        }
        .alert(tr("حذف الدفعة المحفوظة؟", "Delete saved batch?"), isPresented: $confirmDelete) {
            Button(tr("إلغاء", "Cancel"), role: .cancel) {}
            Button(tr("حذف الدفعة", "Delete batch"), role: .destructive) {
                deleting = true
                let original = record
                Task {
                    defer { deleting = false }
                    do {
                        try await Task.detached { [storage] in try storage.delete(original) }.value
                        dismiss()
                        onChange(nil)
                    } catch { self.error = error.localizedDescription }
                }
            }
        } message: {
            Text(tr("سيُحذف سجل هذه الدفعة من الأرشيف نهائياً.", "This batch will be permanently removed from history."))
        }
    }
}
