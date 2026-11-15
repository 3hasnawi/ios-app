import SwiftUI
import SwiftData

/// تنبيهات داخل التقارير فقط، دون إشعارات نظام جديدة أو تغيير البيانات المحاسبية.
struct DeductionReportAlerts: View {
    let month: String
    @Query private var savedMonths: [PayrollMonth]
    @State private var comparison: DeductionComparison?
    @State private var summary: DeductionEntityAlertSummary?
    @State private var error: String?
    @State private var openEntities: DeductionEntityAlertKind?
    @State private var importer = ImportCenter.shared
    @State private var backup = BackupManager.shared

    private var months: [PayrollMonth] {
        savedMonths.sorted {
            $0.sortKey == $1.sortKey ? $0.name < $1.name : $0.sortKey > $1.sortKey
        }
    }
    private var previous: String? {
        DeductionEntityAlerts.previousMonth(selected: month, months: months.map {
            DeductionAlertMonth(name: $0.name, order: $0.sortKey, deductions: $0.deductionCount)
        })
    }
    private var loadKey: String {
        let stamps = months.filter { $0.name == month || $0.name == previous }
            .map { "\($0.name)|\($0.deductionCount)|\($0.importedAt.timeIntervalSince1970)" }.joined(separator: ";")
        return "\(month)|\(previous ?? "")|\(stamps)|\(importer.isBusy)|\(backup.isWorking)|\(backup.version)|\(AppSettings.locale.identifier)"
    }
    private var imported: Bool { (months.first { $0.name == month }?.deductionCount ?? 0) > 0 }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(tr("تنبيهات الاستقطاعات", "Deduction alerts"), systemImage: "bell.badge")
                .font(.headline).foregroundStyle(.primary)
            if importer.isBusy || backup.isWorking {
                Text(tr("تُحدّث التنبيهات بعد اكتمال عملية البيانات الجارية.", "Alerts refresh after the current data operation finishes."))
                    .font(.subheadline).foregroundStyle(.secondary)
            } else if !imported {
                Text(tr("لا توجد قيود استقطاعات مستوردة لهذا الشهر؛ لا تُعدّ الجهات غائبة قبل مراجعة الملف.",
                        "There are no imported deduction entries for this month. Entities are not marked absent without checking the file."))
                    .font(.subheadline).foregroundStyle(.secondary)
            } else if previous == nil {
                Text(tr("يلزم شهر استقطاعات سابق بتاريخ واضح للمقارنة.", "A previous dated deduction month is needed for comparison."))
                    .font(.subheadline).foregroundStyle(.secondary)
            } else if let error {
                Label(error, systemImage: "exclamationmark.triangle").font(.subheadline).foregroundStyle(.orange)
            } else if let comparison, let summary {
                Text(tr("\(comparison.to) مقارنة بـ \(comparison.from)", "\(comparison.to) compared with \(comparison.from)"))
                    .font(.caption).foregroundStyle(.secondary)
                ForEach(DeductionEntityAlertKind.allCases) { kind in
                    Button { openEntities = kind } label: {
                        entityLine(kind, rows: summary.rows(kind))
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("entityAlert-\(kind.rawValue)")
                }
                Divider()
                HStack(alignment: .top, spacing: 10) {
                    ForEach(DeductionGroupKind.allCases) { kind in
                        NavigationLink {
                            DeductionChangeListView(result: comparison, kind: kind)
                        } label: {
                            VStack(spacing: 4) {
                                Text(Num.format(comparison.rows(kind).count)).font(.headline.monospacedDigit())
                                Text(kind.title).font(.caption)
                            }.frame(maxWidth: .infinity).foregroundStyle(.primary)
                        }
                        .buttonStyle(.plain)
                    }
                }
                Text(tr("الغياب يعني عدم وجود استقطاع للجهة في هذا الشهر، ولا يعني حذفها من الأشهر السابقة.",
                        "Absent means no deduction for this entity this month. Earlier months remain unchanged."))
                    .font(.caption).foregroundStyle(.secondary)
            } else {
                ProgressView(tr("جارٍ مراجعة تغيّرات الاستقطاعات…", "Checking deduction changes…"))
            }
            NavigationLink {
                DeductionAuditView(months: months.filter { $0.deductionCount > 0 || $0.name == month }.map(\.name), to: month)
            } label: {
                Label(tr("فتح تدقيق الاستقطاعات", "Open deduction audit"), systemImage: "checkmark.shield")
                    .font(.subheadline)
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .accessibilityIdentifier("deductionReportAlerts")
        .navigationDestination(item: $openEntities) { kind in
            if let comparison, let summary { DeductionEntityAlertsList(comparison: comparison, summary: summary, kind: kind) }
        }
        .task(id: loadKey) {
            let expectedKey = loadKey
            comparison = nil; summary = nil; error = nil
            guard !importer.isBusy, !backup.isWorking, let previous else { return }
            let container = SharedModel.container
            do {
                let result = try await AuditResults.shared.deductions(from: previous, to: month, container: container)
                let work = Task.detached(priority: .userInitiated) { try DeductionEntityAlerts.summarize(result) }
                let alerts = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
                try Task.checkCancellation()
                guard expectedKey == loadKey else { return }
                comparison = result; summary = alerts
                #if targetEnvironment(simulator)
                if UserDefaults.standard.bool(forKey: "deductionEntityAlertsPreview") { openEntities = .added }
                #endif
            } catch {
                guard !Task.isCancelled, !(error is CancellationError), expectedKey == loadKey else { return }
                self.error = error.localizedDescription
            }
        }
    }

    private func entityLine(_ kind: DeductionEntityAlertKind, rows: [DeductionEntityAlert]) -> some View {
        HStack(spacing: 10) {
            Image(systemName: kind == .added ? "plus.circle" : "exclamationmark.triangle")
                .foregroundStyle(rows.isEmpty ? Color.secondary : kind == .added ? Color.green : Color.orange)
            VStack(alignment: .leading, spacing: 3) {
                Text(kind.title).font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                if !rows.isEmpty {
                    Text((kind == .missing ? tr("المبلغ السابق: ", "Previous amount: ") : tr("مبلغ الشهر: ", "This month's amount: "))
                         + Money.format(rows.reduce(0) { $0 + $1.amount }))
                        .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 6)
            Text(Num.format(rows.count)).font(.title3.weight(.bold).monospacedDigit()).foregroundStyle(.primary)
            Image(systemName: "chevron.forward").font(.caption.weight(.bold)).foregroundStyle(.tertiary)
        }
        .padding(.vertical, 3)
        .contentShape(Rectangle())
    }
}

private struct DeductionEntityAlertsList: View {
    let comparison: DeductionComparison
    let summary: DeductionEntityAlertSummary
    let kind: DeductionEntityAlertKind
    @State private var search = ""
    private var rows: [DeductionEntityAlert] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        return summary.rows(kind).filter { row in tokens.allSatisfy { ArabicText.normalize(row.title).contains($0) } }
    }
    var body: some View {
        List {
            Section {
                if rows.isEmpty { Text(tr("لا توجد جهات بهذه الحالة.", "No entities with this status.")).foregroundStyle(.secondary) }
                ForEach(rows) { alert in
                    NavigationLink {
                        DeductionEntityAlertDetail(comparison: comparison, alert: alert)
                    } label: {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(alert.title).font(.headline)
                            HStack {
                                Text(tr("\(Num.format(alert.people)) منتسب", "\(alert.people) employees"))
                                Spacer()
                                Text(Money.format(alert.amount)).fontWeight(.semibold).monospacedDigit()
                            }.font(.subheadline).foregroundStyle(.secondary)
                        }.padding(.vertical, 4)
                    }
                }
            } header: {
                Text(tr("\(comparison.to) مقارنة بـ \(comparison.from)", "\(comparison.to) compared with \(comparison.from)"))
            } footer: {
                if kind == .missing { Text(tr("المبلغ والعدد من الشهر السابق. تبقى قيود الأشهر السابقة محفوظة.", "Amounts and counts are from the previous month. Historical entries remain saved.")) }
            }
        }
        .navigationTitle(kind.title).navigationBarTitleDisplayMode(.inline)
        .adaptiveSearchable(text: $search, prompt: tr("بحث باسم الجهة", "Search entities"))
        .accessibilityIdentifier("deductionEntityAlertsList")
    }
}

private struct DeductionEntityAlertDetail: View {
    let comparison: DeductionComparison
    let alert: DeductionEntityAlert
    @State private var search = ""
    private var month: String { alert.kind == .added ? comparison.to : comparison.from }
    private var rows: [DeductionChange] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        return comparison.entries.filter { row in
            DeductionEntityAlerts.key(row.entity) == alert.key && tokens.allSatisfy {
                ArabicText.normalize(row.name + " " + ArabicText.latinDigits(row.note)).contains($0)
            }
        }.sorted { ArabicText.alphabeticalOrder($0.name, $1.name) == .orderedAscending }
    }
    var body: some View {
        List {
            Section {
                LabeledContent(tr("الشهر", "Month"), value: month)
                LabeledContent(tr("المنتسبون", "Employees"), value: Num.format(alert.people))
                LabeledContent(tr("المبلغ", "Amount"), value: Money.format(alert.amount))
            }
            Section {
                ForEach(rows.prefix(500)) { row in
                    VStack(alignment: .leading, spacing: 5) {
                        HStack {
                            Text(row.name).fontWeight(.semibold)
                            Spacer(minLength: 8)
                            Text(Money.format(alert.kind == .added ? row.after : row.before)).monospacedDigit()
                        }.font(.subheadline)
                        if !row.note.isEmpty { Text(row.note).font(.caption).foregroundStyle(.secondary) }
                    }
                    .personActions(name: row.name, month: month,
                        copies: row.note.isEmpty ? [] : [CopyItem(label: tr("نسخ رقم الإضبارة", "Copy case number"), value: row.note)])
                }
            } header: { Text(tr("قيود الاستقطاع", "Deduction entries")) }
            footer: {
                if rows.count > 500 { Text(tr("يظهر أول ٥٠٠ قيد؛ استخدم البحث للوصول إلى البقية.", "Showing the first 500 entries. Search to find other entries.")) }
                if rows.isEmpty { Text(tr("لا توجد نتائج للبحث.", "No search results.")) }
            }
        }
        .navigationTitle(alert.title).navigationBarTitleDisplayMode(.inline)
        .adaptiveSearchable(text: $search, prompt: tr("بحث بالاسم أو الإضبارة", "Name or case number"))
    }
}
