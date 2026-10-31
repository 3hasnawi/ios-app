import SwiftUI
import SwiftData

/// قوائم منفصلة عن الكتب، مصدرها لقطة الشهر المحدد.
struct TransferListsView: View {
    let month: String
    let sourceKey: String
    let entities: [TransferEntity]
    @State private var query = ""

    private var groups: [TransferEntity] {
        entities.filter { $0.payrollRecipient == nil && $0.group.kind == .deductions && !$0.group.byEntity }
            .filter { query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || ArabicText.cleanQuery($0.group.title).contains(ArabicText.cleanQuery(query)) }
            .sorted { $0.group.title.localizedStandardCompare($1.group.title) == .orderedAscending }
    }
    var body: some View {
        List {
            Section {
                NavigationLink { TransferCashListView(month: month, sourceKey: sourceKey) } label: {
                    Label(tr("قائمة رواتب الكاش", "Cash salary list"), systemImage: "person.text.rectangle")
                }
            }
            Section(tr("جهات الاستقطاع", "Deduction entities")) {
                if groups.isEmpty { Text(tr("لا توجد جهات مطابقة.", "No matching entities.")).foregroundStyle(.secondary) }
                ForEach(groups) { entity in
                    NavigationLink {
                        TransferEntityListView(entity: entity, sourceKey: sourceKey)
                    } label: {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(entity.group.title).font(.body.weight(.semibold))
                            Text(tr("\(Num.format(entity.group.count)) منتسب · \(Money.format(entity.group.total))", "\(Num.format(entity.group.count)) people · \(Money.format(entity.group.total))"))
                                .font(.caption).foregroundStyle(.secondary)
                        }.padding(.vertical, 3)
                    }
                }
            }
        }
        .searchable(text: $query, prompt: tr("ابحث عن جهة", "Search entities"))
        .navigationTitle(tr("القوائم", "Lists"))
        .navigationBarTitleDisplayMode(.inline)
    }
}

private struct TransferEntityListView: View {
    let entity: TransferEntity
    let sourceKey: String
    @Query private var months: [PayrollMonth]
    @State private var rows: [ReportRow]?
    @State private var error: String?
    @State private var showPreview = false
    @State private var showExport = false

    private var current: Bool {
        let record = months.first { $0.name == entity.group.month }
        return record != nil && "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    var body: some View {
        List {
            if !current {
                Text(tr("تغيّرت بيانات الشهر؛ ارجع إلى ملفات التحويل لإعادة التحميل.", "The month's data changed; return to transfer files to reload."))
                    .foregroundStyle(.orange)
            } else if let rows {
                Section {
                    LabeledContent(tr("الشهر", "Month"), value: entity.group.month)
                    LabeledContent(tr("عدد السطور", "Rows"), value: Num.format(rows.count))
                    LabeledContent(tr("المجموع", "Total"), value: Money.format(entity.group.total))
                    Button(tr("معاينة القائمة", "Preview list"), systemImage: "doc.text.magnifyingglass") { showPreview = true }
                    Button(tr("تصدير القائمة", "Export list"), systemImage: "square.and.arrow.up") { showExport = true }
                }
                Section(tr("الأسماء", "Names")) {
                    ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(row.name).font(.body.weight(.semibold)).privacySensitive()
                            if !row.rank.isEmpty { Text(row.rank).font(.caption).foregroundStyle(.secondary) }
                            Text(Money.format(row.amount)).font(.subheadline.weight(.semibold)).monospacedDigit()
                        }.padding(.vertical, 4)
                    }
                }
            } else if let error {
                ContentUnavailableView(tr("تعذّر إعداد القائمة", "Couldn't prepare list"), systemImage: "exclamationmark.triangle", description: Text(error))
            } else { ProgressView().frame(maxWidth: .infinity) }
        }
        .navigationTitle(entity.group.title).navigationBarTitleDisplayMode(.inline)
        .task(id: sourceKey) {
            guard current else { return }
            let group = entity.group, missing = entity.missingAmounts, container = SharedModel.container
            let task = Task.detached(priority: .userInitiated) {
                let loaded = try await ReportEngine(modelContainer: container).rows(for: group)
                return try TransferIndividualPlan(kind: .ramadi, group: group, rows: loaded, missingAmounts: missing).rows
            }
            do {
                let loaded = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                guard !Task.isCancelled, current else { return }
                rows = loaded
            } catch is CancellationError { return }
            catch { guard !Task.isCancelled, current else { return }; self.error = error.localizedDescription }
        }
        .onChange(of: current) { if !current { showPreview = false; showExport = false } }
        .sheet(isPresented: $showPreview) {
            if current, let rows { ReportPreviewSheet(group: entity.group, rows: rows) }
        }
        .sheet(isPresented: $showExport) {
            if current, let rows { ReportExportSheet(group: entity.group, rows: rows) }
        }
    }
}
