import SwiftUI
import SwiftData

struct DeductionComparisonView: View {
    let months: [String]
    @State private var from: String
    @State private var to: String
    @State private var result: DeductionComparison?
    @State private var loadError: String?
    @State private var exportError: String?
    @State private var exporting = false
    @State private var confirmedEmpty = false
    @State private var byDeduction = false
    @State private var selectedKind: DeductionChangeKind = .stopped
    @State private var search = ""
    @Query private var savedMonths: [PayrollMonth]
    @State private var importState = StaffImportState.shared

    init(months: [String], to: String) {
        self.months = months
        _to = State(initialValue: to)
        let i = months.firstIndex(of: to) ?? 0
        _from = State(initialValue: months.indices.contains(i + 1) ? months[i + 1] : (months.first { $0 != to } ?? to))
    }

    private var loadKey: String {
        let revisions = savedMonths.filter { $0.name == from || $0.name == to }
            .map { "\($0.name):\($0.importedAt.timeIntervalSince1970)" }.sorted().joined(separator: "|")
        return "\(from)|\(to)|\(revisions)|\(importState.version)"
    }
    private var ready: Bool {
        guard let result else { return false }
        return result.emptyMonths.isEmpty || confirmedEmpty
    }
    private var rows: [DeductionChange] {
        guard let result else { return [] }
        return byDeduction ? result.entries : result.people
    }
    private var filtered: [DeductionChange] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        return rows.filter { row in
            row.kind == selectedKind && tokens.allSatisfy { ArabicText.normalize(row.name + " " + row.entity).contains($0) }
        }
    }

    var body: some View {
        List {
            Section {
                monthPicker(tr("من", "From"), selection: $from)
                monthPicker(tr("إلى", "To"), selection: $to)
            } footer: {
                Text(tr("المقارنة من ملف الاستقطاعات. الربط بالاسم الموحّد، والاستقطاع بالجهة والإضبارة. تغيير الإضبارة يظهر كتوقف وبدء؛ تشابه الأسماء يحتاج مراجعة.",
                        "Uses deduction files. Matches normalized names and identifies deductions by entity and case reference. A reference change appears as a stop and start; identical names need review."))
            }
            if months.count < 2 || from == to {
                Text(tr("اختر شهرين مختلفين للمقارنة.", "Choose two different months."))
            } else if let loadError {
                Text(loadError).foregroundStyle(.red)
            } else if let result {
                if !result.emptyMonths.isEmpty {
                    Section {
                        Label(tr("لا توجد قيود في: ", "No entries in: ") + result.emptyMonths.joined(separator: "، "), systemImage: "exclamationmark.triangle")
                        Toggle(tr("أؤكد أن الشهر الخالي بلا استقطاعات فعلًا", "Confirm the empty month truly has no deductions"), isOn: $confirmedEmpty)
                    } footer: {
                        Text(tr("قد يكون ملف الاستقطاعات غير مستورد. أكّد فقط إذا كان الشهر خاليًا فعلًا، وإلا استورد الملف أولًا.",
                                "The deduction file may not have been imported. Confirm only if the month is truly empty; otherwise import the file first."))
                    }
                }
                if ready {
                    summary(result)
                    Section {
                        Picker(tr("العرض", "View"), selection: $byDeduction) {
                            Text(tr("الموظفون", "Employees")).tag(false)
                            Text(tr("الاستقطاعات", "Deductions")).tag(true)
                        }.pickerStyle(.segmented)
                        Picker(tr("التغيّر", "Change"), selection: $selectedKind) {
                            ForEach(DeductionChangeKind.allCases) { kind in
                                Text("\(kind.title) (\(Num.format(rows.filter { $0.kind == kind }.count)))").tag(kind)
                            }
                        }
                        .accessibilityIdentifier("deductionChangeFilter")
                    } footer: {
                        Text(byDeduction
                             ? tr("كل نتيجة تخص موظفًا وجهة وإضبارة؛ القيود المتعددة لنفس المفتاح تُجمع. قد يظهر الموظف في أكثر من حالة.", "Each result is a person, entity and case reference; repeated entries for the same key are summed. A person may appear in multiple categories.")
                             : tr("كل موظف يُحسب مرة واحدة حسب مجموع استقطاعاته. «توقف» يعني أن المجموع الحالي صفر؛ راجع الاستقطاعات لكشف التغيّرات التي تعادل بعضها.", "Each employee is counted once by total deductions. Stopped means the current total is zero; check deductions for offsetting changes."))
                    }
                    Section {
                        let visible = filtered
                        Text(tr("\(Num.format(visible.count)) نتيجة · \(Num.format(Set(visible.map(\.person)).count)) موظف", "\(visible.count) results · \(Set(visible.map(\.person)).count) employees"))
                            .font(.caption).foregroundStyle(.secondary)
                        if visible.isEmpty { Text(tr("لا توجد نتائج لهذه الحالة", "No results for this category")).foregroundStyle(.secondary) }
                        ForEach(visible.prefix(500)) { row in
                            changeRow(row)
                        }
                        if visible.count > 500 {
                            Text(tr("يظهر أول 500؛ التصدير يشمل جميع النتائج والحالات.", "First 500 shown; export includes all results and categories."))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            } else {
                ProgressView(tr("جارٍ مقارنة الاستقطاعات…", "Comparing deductions…"))
            }
        }
        .navigationTitle(tr("مقارنة الاستقطاعات", "Compare deductions"))
        .navigationBarTitleDisplayMode(.inline)
        .adaptiveSearchable(text: $search, prompt: tr("اسم الموظف أو الجهة", "Employee or entity"))
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button(tr("تصدير Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export(excel: true) }
                    Button(tr("تصدير PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export(excel: false) }
                } label: { ReportToolbarLabel(isWorking: exporting) }
                .disabled(!ready || exporting)
            }
        }
        .alert(tr("التصدير", "Export"), isPresented: Binding(get: { exportError != nil }, set: { if !$0 { exportError = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(exportError ?? "") }
        .task(id: loadKey) {
            result = nil
            loadError = nil
            confirmedEmpty = false
            guard months.count >= 2, from != to else { return }
            let a = from, b = to, container = SharedModel.container
            let task = Task.detached(priority: .userInitiated) {
                try await ReportEngine(modelContainer: container).deductionComparison(from: a, to: b)
            }
            do {
                let loaded = try await withTaskCancellationHandler {
                    try await task.value
                } onCancel: { task.cancel() }
                guard !Task.isCancelled else { return }
                result = loaded
            } catch {
                guard !Task.isCancelled else { return }
                loadError = error.localizedDescription
            }
        }
    }

    private func monthPicker(_ label: String, selection: Binding<String>) -> some View {
        Picker(label, selection: selection) {
            ForEach(months, id: \.self) { Text($0).tag($0) }
        }
    }

    private func summary(_ result: DeductionComparison) -> some View {
        Section {
            LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 12) {
                countTile(byDeduction ? tr("توقفت", "Stopped") : tr("توقف عنهم بالكامل", "Fully stopped"), .stopped)
                countTile(byDeduction ? tr("بدأت", "Started") : tr("بدأ عليهم", "Started"), .started)
                countTile(byDeduction ? tr("زادت", "Increased") : tr("زاد مجموعهم", "Total increased"), .increased)
                countTile(byDeduction ? tr("نقصت", "Decreased") : tr("نقص مجموعهم", "Total decreased"), .decreased)
            }
            LabeledContent(tr("ثابت المجموع", "Unchanged total"), value: Num.format(rows.filter { $0.kind == .unchanged }.count))
            LabeledContent(tr("مجموع السابق", "Previous total"), value: Money.format(result.before))
            LabeledContent(tr("مجموع الحالي", "Current total"), value: Money.format(result.after))
            LabeledContent(tr("الفرق", "Difference"), value: Money.format(result.after - result.before))
        } header: { Text(byDeduction ? tr("أعداد الاستقطاعات حسب الموظف والجهة والإضبارة", "Deductions by person, entity and case reference") : tr("أعداد الموظفين", "Employee counts")) }
        .accessibilityIdentifier("deductionComparisonSummary")
    }

    private func countTile(_ label: String, _ kind: DeductionChangeKind) -> some View {
        Button {
            selectedKind = kind
            search = ""
        } label: {
            VStack(spacing: 3) {
                Text(Num.format(rows.filter { $0.kind == kind }.count)).font(.title2.bold().monospacedDigit())
                Text(label).font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity).padding(.vertical, 10)
            .background(Color(.tertiarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 12))
        }
        .buttonStyle(.plain)
    }

    private func changeRow(_ row: DeductionChange) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(row.name).font(.subheadline.weight(.semibold))
            if !row.entity.isEmpty { Text(row.entity).font(.caption).foregroundStyle(.secondary) }
            if byDeduction {
                Text(row.note.isEmpty ? tr("الإضبارة غير متوفرة", "Case reference missing") : row.note)
                    .font(.caption).foregroundStyle(.secondary)
            }
            Text(tr("السابق: \(Money.format(row.before)) · الحالي: \(Money.format(row.after))", "Previous: \(Money.format(row.before)) · Current: \(Money.format(row.after))"))
                .font(.caption).monospacedDigit()
            Text(tr("الفرق: ", "Difference: ") + (row.difference > 0 ? "+" : "") + Money.format(row.difference))
                .font(.subheadline.weight(.semibold)).monospacedDigit()
                .foregroundStyle(row.difference > 0 ? Color.orange : Color.primary)
        }
        .padding(.vertical, 3)
        .contextMenu {
            Button(tr("نسخ الاسم", "Copy name"), systemImage: "doc.on.doc") { copyToClipboard(row.name) }
            Button(tr("بحث عنه", "Search"), systemImage: "magnifyingglass") {
                ShortcutRouter.shared.pendingSearch = row.name
                AppRouter.shared.tab = .search
            }
        }
    }

    private func export(excel: Bool) {
        guard ready, let result else { return }
        exporting = true
        Task {
            defer { exporting = false }
            do {
                let files = try await AuditExport.deductions(result)
                ActivityShare.present([excel ? files.excel : files.pdf])
            } catch { exportError = error.localizedDescription }
        }
    }
}
