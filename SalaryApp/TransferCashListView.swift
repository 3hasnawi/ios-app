import SwiftUI
import SwiftData

/// مراجعة قائمة الكاش بصافي الشهر نفسه قبل مشاركة PDF.
struct TransferCashListView: View {
    let month: String
    let sourceKey: String
    @Query private var months: [PayrollMonth]
    @State private var list: TransferCashList?
    @State private var error: String?
    @State private var exporting = false

    private var sourceIsCurrent: Bool {
        let record = months.first { $0.name == month }
        return record != nil && "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    var body: some View {
        List {
            if !sourceIsCurrent {
                Text(tr("تغيّرت بيانات الشهر؛ ارجع إلى ملفات التحويل لإعادة التحميل.", "The month's data changed; return to transfer files to reload."))
                    .foregroundStyle(.orange)
            } else if let list {
                Section {
                    LabeledContent(tr("الشهر", "Month"), value: month)
                    LabeledContent(tr("عدد الرواتب", "Salary records"), value: Num.format(list.employees.count))
                    LabeledContent(tr("مجموع الكاش", "Cash total"), value: Money.format(list.total))
                    if list.repeatedNameRows > 0 {
                        Text(tr("توجد \(Num.format(list.repeatedNameRows)) أسماء مكررة محتملة؛ راجع السجلات قبل المشاركة.", "There are \(Num.format(list.repeatedNameRows)) potentially repeated names; review them before sharing."))
                            .font(.footnote).foregroundStyle(.orange)
                    }
                }
                Section(tr("المنتسبون", "Employees")) {
                    if list.employees.isEmpty {
                        Text(tr("لا توجد رواتب كاش لهذا الشهر.", "No cash salaries for this month.")).foregroundStyle(.secondary)
                    }
                    ForEach(list.employees) { employee in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(employee.name).font(.body.weight(.semibold))
                            Text([employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
                                .font(.caption).foregroundStyle(.secondary)
                            Text(Money.format(employee.amount)).font(.subheadline.weight(.semibold)).monospacedDigit()
                        }.padding(.vertical, 4)
                    }
                }
            } else if let error {
                ContentUnavailableView(tr("تعذّر إعداد قائمة الكاش", "Couldn't prepare cash list"), systemImage: "exclamationmark.triangle", description: Text(error))
            } else { ProgressView().frame(maxWidth: .infinity) }
        }
        .navigationTitle(tr("قائمة رواتب الكاش", "Cash salary list"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button(tr("مشاركة PDF", "Share PDF"), systemImage: "square.and.arrow.up") { export() }
                    .disabled(!sourceIsCurrent || list?.employees.isEmpty != false || exporting)
            }
        }
        .overlay { if exporting { ProgressView(tr("إعداد القائمة", "Preparing list")).padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16)) } }
        .task(id: sourceKey) {
            guard sourceIsCurrent else { return }
            let container = SharedModel.container
            let task = Task.detached(priority: .userInitiated) { try await TransferCashListEngine(modelContainer: container).list(month: month) }
            do {
                let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                guard !Task.isCancelled, sourceIsCurrent else { return }
                list = result
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled, sourceIsCurrent else { return }; error = error.localizedDescription }
        }
    }
    private func export() {
        guard let list, sourceIsCurrent, !exporting else { return }
        exporting = true
        Task {
            defer { exporting = false }
            do {
                let url = try await TransferCashListPDF.make(list)
                guard sourceIsCurrent else {
                    try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
                    error = tr("تغيّرت بيانات الشهر أثناء إعداد القائمة؛ أعد فتحها.", "The month's data changed while preparing the list; reopen it.")
                    return
                }
                ActivityShare.present([url])
            } catch { self.error = error.localizedDescription; self.list = nil }
        }
    }
}
