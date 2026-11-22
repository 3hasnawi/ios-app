import SwiftUI

struct EntityWorklist: View {
    let group: ReportGroup
    let rows: [ReportRow]
    @State private var query = ""
    @State private var statementRow: ReportRow?

    private var visible: [ReportRow] {
        let terms = AutomaticSearchQuery.normalize(query).split(separator: " ")
        guard !terms.isEmpty else { return rows }
        return rows.filter { row in
            let text = AutomaticSearchQuery.normalize([row.name, row.note].joined(separator: " "))
            return terms.allSatisfy { text.contains($0) }
        }
    }

    var body: some View {
        List {
            Section {
                LabeledContent(group.month, value: Money.format(group.total))
                Text(tr("\(Num.format(group.count)) منتسب · \(Num.format(rows.count)) قيد",
                        "\(group.count) people · \(rows.count) entries")).font(.caption)
            }
            ForEach(visible) { row in
                NavigationLink {
                    List {
                        LabeledContent(tr("المبلغ", "Amount"), value: Money.format(row.amount))
                        LabeledContent(tr("الشهر", "Month"), value: group.month)
                        LabeledContent(tr("جهة التنفيذ", "Execution entity"), value: row.entity)
                        if !row.note.isEmpty { LabeledContent(tr("رقم وتاريخ الإضبارة", "Case reference and date")) { Text(row.note).textSelection(.enabled) } }
                        NavigationLink { statement(for: row) } label: { Text(tr("كشف استقطاعات المنتسب", "Employee deduction statement")) }
                    }.navigationTitle(row.name)
                } label: {
                    VStack(alignment: .leading, spacing: 3) {
                        HStack(alignment: .firstTextBaseline) {
                            Text(row.name).font(.body.weight(.semibold))
                            Spacer(minLength: 6)
                            Text(Money.format(row.amount)).monospacedDigit().fixedSize(horizontal: true, vertical: false)
                        }
                        if !row.rank.isEmpty { Text(row.rank).font(.caption).foregroundStyle(.secondary) }
                        if !row.note.isEmpty {
                            Text(row.note).font(.subheadline).fixedSize(horizontal: false, vertical: true)
                        }
                    }.padding(.vertical, 2)
                }
                .accessibilityIdentifier("entityWorklistRow")
                .contextMenu {
                    Button(tr("كشف استقطاعات المنتسب", "Employee deduction statement"), systemImage: "list.bullet.rectangle") { statementRow = row }
                    if !row.note.isEmpty {
                        Button(tr("نسخ رقم وتاريخ الإضبارة", "Copy case reference and date"), systemImage: "doc.on.doc") { copyToClipboard(row.note) }
                    }
                }
            }
            if visible.isEmpty && !query.isEmpty { ContentUnavailableView.search(text: query) }
        }
        .listSectionSpacing(.compact)
        .adaptiveSearchable(text: $query, prompt: tr("ابحث بالاسم أو رقم الإضبارة", "Search by name or case reference"))
        .navigationTitle(group.title).navigationBarTitleDisplayMode(.inline)
        .navigationDestination(item: $statementRow) { row in statement(for: row) }
    }

    private func statement(for row: ReportRow) -> some View {
        let key = row.personKey.isEmpty ? ArabicText.matchKey(row.name) : row.personKey
        return EmployeeDeductionsView(initialPerson: DeductionPerson(key: key, name: row.name, detail: row.rank),
                                      initialEntity: row.entity)
    }

}

struct EntityMonthComparison: View {
    let group: ReportGroup
    let rows: [ReportRow]
    let previous: String
    let revision: Date
    @State private var comparison: DeductionComparison?
    @State private var error: String?

    var body: some View {
        Group {
            if let c = comparison {
                LabeledContent(previous, value: Money.format(c.before))
                NavigationLink {
                    List {
                        ForEach(c.entries.filter { $0.kind != .unchanged }) { change in
                            VStack(alignment: .leading, spacing: 4) {
                                Text(change.name).font(.headline)
                                LabeledContent(change.kind.title, value: Money.format(change.difference))
                                if !change.note.isEmpty { Text(change.note).font(.caption) }
                            }
                        }
                        if c.entries.allSatisfy({ $0.kind == .unchanged }) { Text(tr("لا فروقات", "No differences")) }
                    }.navigationTitle(tr("تفاصيل الفرق", "Difference details"))
                } label: {
                    LabeledContent(tr("الفرق مقارنةً بـ\(previous)", "Difference vs \(previous)"), value: Money.format(c.after - c.before))
                }
            } else if let error { Text(error).font(.footnote).foregroundStyle(.secondary) }
            else { ProgressView() }
        }
        .task(id: "\(group.id)|\(previous)|\(revision.timeIntervalSince1970)") {
            comparison = nil; error = nil
            let container = SharedModel.container
            let oldGroup = ReportGroup(kind: .deductions, month: previous, title: group.title, count: 0, total: 0, byEntity: group.byEntity)
            do {
                let result = try await Task.detached {
                    let older = try await ReportEngine(modelContainer: container).rows(for: oldGroup)
                    func snapshot(_ row: ReportRow) -> DeductionSnapshot {
                        DeductionSnapshot(name: row.name, entity: row.entity, note: row.note, amount: row.amount)
                    }
                    return try DeductionReview.compare(from: previous, to: group.month, old: older.map(snapshot), new: rows.map(snapshot))
                }.value
                guard !Task.isCancelled else { return }
                comparison = result
            } catch { if !Task.isCancelled { self.error = error.localizedDescription } }
        }
    }
}
