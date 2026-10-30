import SwiftUI
import SwiftData
import UIKit

nonisolated struct MissingContractNominalRow: Identifiable, Sendable {
    let id: Int
    let name: String
    let workplace: String
}

@ModelActor
actor ContractNominalReviewEngine {
    func missing(month: String) throws -> [MissingContractNominalRow] {
        var descriptor = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        descriptor.propertiesToFetch = [\.name, \.rank, \.workplace, \.detailsData, \.itemsData]
        var rows: [MissingContractNominalRow] = []
        for (index, employee) in try modelContext.fetch(descriptor).enumerated() {
            if index.isMultiple(of: 512) { try Task.checkCancellation() }
            guard ArabicText.normalize(employee.rank) == ArabicText.normalize("عقود"),
                  ContractNominalValue.read(details: employee.details, items: employee.items) == nil else { continue }
            rows.append(MissingContractNominalRow(id: index, name: employee.name, workplace: employee.workplace))
        }
        return rows.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }
}

struct ContractNominalReviewView: View {
    let month: String
    let sourceKey: String
    let expected: Int
    @Query private var months: [PayrollMonth]
    @State private var rows: [MissingContractNominalRow]?
    @State private var error: String?
    private var current: Bool {
        let record = months.first { $0.name == month }
        return record != nil && "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    var body: some View {
        List {
            if !current {
                Text(tr("تغيّرت بيانات الشهر؛ ارجع وأعد فتح القائمة.", "The month changed; go back and reopen the list.")).foregroundStyle(.orange)
            } else if let error {
                Text(error).foregroundStyle(.red)
            } else if let rows {
                Section {
                    if rows.count != expected { Text(tr("تغيّر عدد السجلات؛ أعد فتح ملفات التحويل.", "The record count changed; reopen transfer files.")).foregroundStyle(.orange) }
                    if rows.isEmpty { Text(tr("لم تُعثر على سجلات ناقصة.", "No missing records found.")).foregroundStyle(.secondary) }
                    ForEach(rows) { row in
                        VStack(alignment: .leading, spacing: 5) {
                            Text(row.name.isEmpty ? tr("اسم غير متوفر", "Name unavailable") : row.name).font(.headline).textSelection(.enabled)
                            if !row.workplace.isEmpty { Text(row.workplace).font(.caption).foregroundStyle(.secondary) }
                            if !row.name.isEmpty { Button(tr("نسخ الاسم", "Copy name"), systemImage: "doc.on.doc") { UIPasteboard.general.string = row.name }.font(.caption) }
                        }.padding(.vertical, 4)
                    }
                } header: { Text(tr("عقود بلا اسمي مقروء · \(month)", "Contracts without readable nominal pay · \(month)")) } footer: { Text(tr("طابق السجل مع ملف الرواتب الأصلي. لا يخمّن التطبيق الراتب أو يعدّل البيانات.", "Check each record against the original payroll file. The app does not guess pay or change data.")) }
            } else { ProgressView().frame(maxWidth: .infinity) }
        }
        .navigationTitle(tr("مراجعة اسمي العقود", "Review contract nominal pay"))
        .navigationBarTitleDisplayMode(.inline)
        .task(id: sourceKey) {
            guard current else { return }
            let container = SharedModel.container
            let work = Task.detached(priority: .utility) { try await ContractNominalReviewEngine(modelContainer: container).missing(month: month) }
            do {
                let result = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
                guard !Task.isCancelled, current else { return }
                rows = result
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled else { return }; self.error = error.localizedDescription }
        }
    }
}
