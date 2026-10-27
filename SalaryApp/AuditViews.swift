import SwiftUI
import SwiftData

// MARK: - صفحة التدقيق: بطاقتان

/// التدقيق: بطاقة الرواتب وبطاقة الاستقطاعات، كل واحدة تقارن آخر شهرين تلقائياً.
struct AuditView: View {
    /// الأشهر المحفوظة (الأحدث أولاً).
    let months: [String]
    @Query private var savedMonths: [PayrollMonth]
    @State private var importState = StaffImportState.shared
    @State private var digest: AuditDigest?
    @State private var exporting = false
    @State private var error: String?

    init(month: String = "", months: [String]) {
        self.months = months
    }

    private var to: String { months.first ?? "" }
    private var from: String { months.count > 1 ? months[1] : "" }
    private var loadKey: String {
        let stamps = savedMonths.filter { $0.name == to || $0.name == from }
            .map { "\($0.name):\($0.importedAt.timeIntervalSince1970)" }.sorted().joined(separator: "|")
        return "\(from)|\(to)|\(stamps)|\(importState.version)"
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                if months.isEmpty {
                    ContentUnavailableView(tr("لا توجد بيانات", "No data"), systemImage: "checkmark.shield")
                } else {
                    salaryCard
                    deductionCard
                    Text(tr("الضغط المطول على أي بطاقة: تصدير الكشف الشامل Excel / PDF", "Long-press a card to export the full report"))
                        .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
                }
            }
            .padding(16)
        }
        .background(Color(.systemGroupedBackground))
        .navigationTitle(tr("التدقيق", "Audit"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                if exporting { ProgressView() }
            }
        }
        .alert(tr("التصدير", "Export"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
        .task(id: loadKey) {
            guard !to.isEmpty else { return }
            digest = nil
            let loaded = await AuditDigest.run(month: to, previous: from, container: SharedModel.container)
            guard !Task.isCancelled else { return }
            digest = loaded
        }
    }

    private var salaryCard: some View {
        NavigationLink {
            SalaryAuditView(months: months)
        } label: {
            AuditHubCard(title: tr("الرواتب", "Payroll"),
                         subtitle: tr("فحوص الشهر · مقارنة الأشهر\nالجدد والخارجون وتغيّر الرواتب", "Monthly checks · month comparison\nNew, removed and salary changes"),
                         systemImage: "banknote.fill", color: .green, tags: salaryTags, loading: digest == nil)
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("auditSalaryCard")
        .contextMenu {
            Button(tr("تصدير الكشف الشامل Excel", "Export full report (Excel)"), systemImage: ReportSymbols.excel) { exportSalary(excel: true) }
            Button(tr("تصدير الكشف الشامل PDF", "Export full report (PDF)"), systemImage: ReportSymbols.pdf) { exportSalary(excel: false) }
            Button(tr("تصدير فحوص الشهر", "Export monthly checks"), systemImage: "checklist") { exportChecks() }
        }
    }

    private var deductionCard: some View {
        NavigationLink {
            DeductionAuditView(months: months)
        } label: {
            AuditHubCard(title: tr("الاستقطاعات", "Deductions"),
                         subtitle: tr("مقارنة آخر شهرين تلقائياً\nتوقف · جديد · تغيّر المبلغ", "Compares the last two months\nStopped · new · changed"),
                         systemImage: "arrow.left.arrow.right", color: .orange, tags: deductionTags, loading: digest == nil)
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("auditDeductionCard")
        .contextMenu {
            Button(tr("تصدير الكشف الشامل Excel", "Export full report (Excel)"), systemImage: ReportSymbols.excel) { exportDeductions(excel: true) }
            Button(tr("تصدير الكشف الشامل PDF", "Export full report (PDF)"), systemImage: ReportSymbols.pdf) { exportDeductions(excel: false) }
        }
    }

    private var salaryTags: [AuditTag] {
        guard let s = digest?.salary else { return [] }
        var tags = [AuditTag(text: s.issues == 0 ? tr("لا ملاحظات", "No issues") : tr("\(Num.format(s.issues)) ملاحظات", "\(s.issues) issues"),
                             style: s.issues == 0 ? .ok : .warning)]
        if s.compared {
            tags.append(AuditTag(text: tr("جدد \(Num.format(s.rows(.added).count))", "New \(s.rows(.added).count)"), style: .plain))
            tags.append(AuditTag(text: tr("خارجون \(Num.format(s.rows(.removed).count))", "Removed \(s.rows(.removed).count)"), style: .plain))
            let bank = s.rows(.bank).count
            if bank > 0 { tags.append(AuditTag(text: tr("تغيّر الحساب \(Num.format(bank))", "Bank \(bank)"), style: .warning)) }
        }
        return tags
    }

    private var deductionTags: [AuditTag] {
        guard let digest else { return [] }
        var tags: [AuditTag] = []
        if let d = digest.deductions {
            tags.append(AuditTag(text: tr("توقف \(Num.format(d.rows(.stopped).count))", "Stopped \(d.rows(.stopped).count)"), style: .warning))
            tags.append(AuditTag(text: tr("جديد \(Num.format(d.rows(.started).count))", "New \(d.rows(.started).count)"), style: .ok))
            tags.append(AuditTag(text: tr("تغيّر \(Num.format(d.rows(.changed).count))", "Changed \(d.rows(.changed).count)"), style: .plain))
        }
        if digest.deductionIssues > 0 {
            tags.append(AuditTag(text: tr("\(Num.format(digest.deductionIssues)) ملاحظات", "\(digest.deductionIssues) issues"), style: .warning))
        }
        return tags
    }

    private func exportSalary(excel: Bool) {
        Task {
            guard let s = await loadedDigest()?.salary else { return }
            await run(excel: excel) { try await AuditExport.salary(s) }
        }
    }

    private func exportChecks() {
        Task {
            guard let s = await loadedDigest()?.salary else { return }
            await run(excel: false) { try await AuditExport.checks(s.checks + s.staffChecks, title: tr("تدقيق الرواتب", "Payroll audit"), month: s.to) }
        }
    }

    private func exportDeductions(excel: Bool) {
        Task {
            guard let d = await loadedDigest()?.deductions else {
                error = tr("تحتاج شهرين فيهما استقطاعات للمقارنة.", "Two months with deductions are needed.")
                return
            }
            await run(excel: excel) { try await AuditExport.deductions(d) }
        }
    }

    private func loadedDigest() async -> AuditDigest? {
        if let digest { return digest }
        exporting = true
        defer { exporting = false }
        let loaded = await AuditDigest.run(month: to, previous: from, container: SharedModel.container)
        digest = loaded
        return loaded
    }

    private func run(excel: Bool, _ make: @escaping () async throws -> (excel: URL, pdf: URL)) async {
        exporting = true
        defer { exporting = false }
        do {
            let files = try await make()
            ActivityShare.present([excel ? files.excel : files.pdf])
        } catch {
            self.error = error.localizedDescription
        }
    }
}

struct AuditTag: Hashable {
    enum Style { case plain, ok, warning }
    let text: String
    let style: Style
}

private struct AuditHubCard: View {
    let title: String
    let subtitle: String
    let systemImage: String
    let color: Color
    let tags: [AuditTag]
    let loading: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 14) {
                Image(systemName: systemImage)
                    .font(.title2.weight(.semibold))
                    .foregroundStyle(.white)
                    .frame(width: 52, height: 52)
                    .background(color.gradient, in: RoundedRectangle(cornerRadius: 15, style: .continuous))
                VStack(alignment: .leading, spacing: 3) {
                    Text(title).font(.title3.weight(.bold)).foregroundStyle(.primary)
                    Text(subtitle).font(.footnote).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                Image(systemName: "chevron.forward").font(.footnote.weight(.bold)).foregroundStyle(.tertiary)
            }
            if loading {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text(tr("جارٍ التدقيق…", "Checking…")).font(.caption).foregroundStyle(.secondary)
                }
            } else if !tags.isEmpty {
                FlowTags(tags: tags)
            }
        }
        .padding(16)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        .contentShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
    }
}

private struct FlowTags: View {
    let tags: [AuditTag]
    var body: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 6) { ForEach(tags, id: \.self, content: tag) }
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 6) { ForEach(tags.prefix(2), id: \.self, content: tag) }
                HStack(spacing: 6) { ForEach(tags.dropFirst(2), id: \.self, content: tag) }
            }
        }
    }

    private func tag(_ t: AuditTag) -> some View {
        let fg: Color = switch t.style {
        case .plain: .primary
        case .ok: .green
        case .warning: .orange
        }
        return Text(t.text)
            .font(.caption.weight(.semibold))
            .foregroundStyle(fg)
            .padding(.horizontal, 9).padding(.vertical, 4)
            .background(fg.opacity(t.style == .plain ? 0.08 : 0.14), in: Capsule())
    }
}

// MARK: - أدوات مشتركة

/// اختيار الشهرين: الضغط يفتح قائمة الأشهر، والضغط المطول: آخر شهرين أو تبديلهما.
struct MonthPairPicker: View {
    let months: [String]
    @Binding var from: String
    @Binding var to: String

    var body: some View {
        HStack(spacing: 8) {
            chip(tr("من", "From"), $from)
            Image(systemName: "arrow.left").foregroundStyle(.secondary)
            chip(tr("إلى", "To"), $to)
        }
    }

    private func chip(_ label: String, _ selection: Binding<String>) -> some View {
        HStack(spacing: 6) {
            Menu {
                Section {
                    Button(tr("آخر شهرين تلقائياً", "Last two months"), systemImage: "arrow.counterclockwise") {
                        to = months.first ?? to
                        from = months.count > 1 ? months[1] : from
                    }
                    Button(tr("تبديل الشهرين", "Swap months"), systemImage: "arrow.left.arrow.right") {
                        let a = from
                        from = to
                        to = a
                    }
                }
                Picker(label, selection: selection) {
                    ForEach(months, id: \.self) { Text($0).tag($0) }
                }
            } label: {
                Image(systemName: "calendar")
                    .accessibilityLabel(tr("اختيار شهر \(label): \(selection.wrappedValue)", "Choose \(label) month: \(selection.wrappedValue)"))
            }
            .glassButtonStyle()
            .buttonBorderShape(.circle)
            VStack(alignment: .leading, spacing: 2) {
                Text(label).font(.caption2).foregroundStyle(.secondary)
                Text(selection.wrappedValue)
                    .font(.caption.weight(.semibold))
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(maxWidth: .infinity)
    }

    static func defaults(_ months: [String]) -> (from: String, to: String) {
        (months.count > 1 ? months[1] : (months.first ?? ""), months.first ?? "")
    }
}

/// مجموع الشهرين والفرق.
struct TotalsStrip: View {
    let fromTitle: String
    let toTitle: String
    let before: Double
    let after: Double

    var body: some View {
        HStack(spacing: 4) {
            cell(fromTitle, Money.format(before), .primary)
            cell(toTitle, Money.format(after), .primary)
            let diff = after - before
            cell(tr("الفرق", "Difference"), (diff > 0 ? "+" : "") + Money.format(diff), diff == 0 ? .secondary : .orange)
        }
        .padding(.vertical, 10)
        .padding(.horizontal, 6)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .contextMenu {
            Button(tr("نسخ المجاميع", "Copy totals"), systemImage: "doc.on.doc") {
                copyToClipboard("\(fromTitle): \(Money.format(before))\n\(toTitle): \(Money.format(after))\n\(tr("الفرق", "Difference")): \(Money.format(after - before))")
            }
        }
    }

    private func cell(_ title: String, _ value: String, _ color: Color) -> some View {
        VStack(spacing: 2) {
            Text(title).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            Text(value).font(.subheadline.weight(.bold).monospacedDigit()).foregroundStyle(color)
                .lineLimit(1).minimumScaleFactor(0.6)
        }
        .frame(maxWidth: .infinity)
    }
}

/// بطاقة حالة (العدد والمبلغ) مع ضغط مطول: عرض، تصدير، نسخ.
struct ChangeTile: View {
    let count: Int
    let title: String
    var amount: Double?
    let color: Color
    var selected = false
    let open: () -> Void
    let export: (Bool) -> Void

    var body: some View {
        Button(action: open) {
            VStack(spacing: 2) {
                Text(Num.format(count)).font(.title2.weight(.bold).monospacedDigit()).foregroundStyle(color)
                Text(title).font(.caption).foregroundStyle(.primary).lineLimit(1).minimumScaleFactor(0.8)
                if let amount {
                    Text((amount > 0 ? "+" : "") + Money.format(amount))
                        .font(.caption2.monospacedDigit()).foregroundStyle(.secondary).lineLimit(1).minimumScaleFactor(0.6)
                }
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 10)
            .padding(.horizontal, 4)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(selected ? color : .clear, lineWidth: 2)
            }
            .contentShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
        .buttonStyle(.plain)
        .contextMenu {
            Button(tr("عرض القائمة", "Show list"), systemImage: "list.bullet", action: open)
            Button(tr("تصدير هذه الحالة Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export(true) }
            Button(tr("تصدير هذه الحالة PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export(false) }
            Divider()
            Button(tr("نسخ العدد والمبلغ", "Copy count and amount"), systemImage: "doc.on.doc") {
                copyToClipboard("\(title): \(Num.format(count))" + (amount.map { " · " + Money.format($0) } ?? ""))
            }
        }
    }
}

/// صف فحص مع ضغط مطول للتصدير.
struct AuditCheckLink: View {
    let check: AuditCheck
    let month: String
    let export: (Bool) -> Void

    var body: some View {
        Group {
            if check.rows.isEmpty {
                AuditCheckRow(check: check)
            } else {
                NavigationLink { AuditCheckView(check: check, month: month) } label: { AuditCheckRow(check: check) }
            }
        }
        .accessibilityIdentifier("check-\(check.id)")
        .contextMenu {
            if !check.rows.isEmpty {
                Button(tr("تصدير Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export(true) }
                Button(tr("تصدير PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export(false) }
            }
            Button(tr("نسخ النتيجة", "Copy result"), systemImage: "doc.on.doc") {
                copyToClipboard("\(check.title): \(check.subtitle)" + (check.rows.isEmpty ? "" : " (\(Num.format(check.rows.count)))"))
            }
        }
    }
}

// MARK: - قسيمة الراتب من أي قائمة

struct PayslipTarget: Identifiable, Hashable {
    let month: String
    let key: String
    var id: String { month + "|" + key }
}

struct EmployeeLookupView: View {
    @Query private var employees: [Employee]

    init(month: String, key: String) {
        _employees = Query(filter: #Predicate<Employee> { $0.month == month && $0.matchKey == key })
    }

    var body: some View {
        if let employee = employees.first {
            EmployeeDetailView(employee: employee)
        } else {
            ContentUnavailableView(tr("غير موجود في رواتب هذا الشهر", "Not in this month's payroll"), systemImage: "person.slash")
        }
    }
}

struct CopyItem: Hashable {
    let label: String
    let value: String
}

/// الضغط المطول على اسم موظف: قسيمة الراتب، البحث عنه، النسخ.
private struct PersonActions: ViewModifier {
    let name: String
    let month: String
    let copies: [CopyItem]
    @State private var payslip: PayslipTarget?

    func body(content: Content) -> some View {
        content
            .contextMenu {
                if !month.isEmpty {
                    Button(tr("قسيمة الراتب", "Payslip"), systemImage: "doc.text") {
                        payslip = PayslipTarget(month: month, key: ArabicText.matchKey(name))
                    }
                }
                Button(tr("بحث عنه", "Search"), systemImage: "magnifyingglass") {
                    ShortcutRouter.shared.pendingSearch = name
                    AppRouter.shared.tab = .search
                }
                Divider()
                Button(tr("نسخ الاسم", "Copy name"), systemImage: "doc.on.doc") { copyToClipboard(name) }
                ForEach(copies, id: \.self) { item in
                    Button(item.label, systemImage: "number") { copyToClipboard(item.value) }
                }
            }
            .sheet(item: $payslip) { target in
                NavigationStack {
                    EmployeeLookupView(month: target.month, key: target.key)
                        .toolbar {
                            ToolbarItem(placement: .cancellationAction) {
                                Button(tr("إغلاق", "Close")) { payslip = nil }
                            }
                        }
                }
            }
    }
}

extension View {
    func personActions(name: String, month: String, copies: [CopyItem] = []) -> some View {
        modifier(PersonActions(name: name, month: month, copies: copies))
    }
}

// MARK: - نافذة التصدير

struct ExportChoice: Identifiable, Hashable {
    let id: String
    let title: String
    let subtitle: String
    let count: Int
}

/// كشف شامل أو حالة واحدة، ثم Excel أو PDF.
struct ChangeExportSheet: View {
    let title: String
    let span: String
    let choices: [ExportChoice]
    let make: (String, Bool) async throws -> URL
    @Environment(\.dismiss) private var dismiss
    @State private var selected = ""
    @State private var working: Bool?
    @State private var error: String?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    ForEach(choices) { choice in
                        Button {
                            selected = choice.id
                        } label: {
                            HStack(spacing: 12) {
                                Image(systemName: (selected.isEmpty ? choices.first?.id : selected) == choice.id ? "largecircle.fill.circle" : "circle")
                                    .font(.title3).foregroundStyle(.tint)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(choice.title).font(.body.weight(.semibold)).foregroundStyle(.primary)
                                    Text(choice.subtitle).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer(minLength: 8)
                                Text(Num.format(choice.count)).font(.subheadline.monospacedDigit()).foregroundStyle(.secondary)
                            }
                        }
                        .buttonStyle(.plain)
                    }
                } header: {
                    Text(tr("ماذا تصدّر؟", "What to export?"))
                } footer: {
                    Text(tr("Excel الشامل: ورقة للخلاصة وورقة لكل حالة. PDF الشامل: ملف واحد، الخلاصة أولاً ثم كل حالة بجدولها.",
                            "Full Excel: a summary sheet and one sheet per case. Full PDF: one file, summary first."))
                }
                if let error {
                    Text(error).foregroundStyle(.red)
                }
            }
            .safeAreaInset(edge: .bottom) {
                HStack(spacing: 10) {
                    ReportActionButton(title: "Excel", systemImage: ReportSymbols.excel, prominent: true, tint: .green,
                                       isWorking: working == true) { run(excel: true) }
                    ReportActionButton(title: "PDF", systemImage: ReportSymbols.pdf, prominent: true, tint: .red,
                                       isWorking: working == false) { run(excel: false) }
                }
                .disabled(working != nil)
                .padding(.horizontal, 16)
                .padding(.bottom, 8)
            }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .principal) {
                    VStack(spacing: 0) {
                        Text(title).font(.headline)
                        Text(span).font(.caption).foregroundStyle(.secondary)
                    }
                }
                ToolbarItem(placement: .cancellationAction) {
                    Button(tr("إغلاق", "Close"), systemImage: "xmark") { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }

    private func run(excel: Bool) {
        let choice = selected.isEmpty ? (choices.first?.id ?? "") : selected
        working = excel
        error = nil
        Task {
            do {
                let url = try await make(choice, excel)
                working = nil
                dismiss()
                try? await Task.sleep(for: .milliseconds(450))
                ActivityShare.present([url])
            } catch {
                working = nil
                self.error = error.localizedDescription
            }
        }
    }
}

// MARK: - تدقيق الرواتب

struct SalaryAuditView: View {
    let months: [String]
    @State private var from: String
    @State private var to: String
    @State private var audit: SalaryAudit?
    @State private var loadError: String?
    @State private var showExport = false
    @State private var exporting = false
    @State private var error: String?
    @State private var openKind: SalaryChangeKind?
    @Query private var savedMonths: [PayrollMonth]
    @State private var importState = StaffImportState.shared

    init(months: [String]) {
        self.months = months
        let d = MonthPairPicker.defaults(months)
        _from = State(initialValue: d.from)
        _to = State(initialValue: d.to)
    }

    private var loadKey: String {
        let stamps = savedMonths.filter { $0.name == to || $0.name == from }
            .map { "\($0.name):\($0.importedAt.timeIntervalSince1970)" }.sorted().joined(separator: "|")
        return "\(from)|\(to)|\(stamps)|\(importState.version)"
    }

    var body: some View {
        List {
            Section {
                MonthPairPicker(months: months, from: $from, to: $to)
                    .listRowBackground(Color.clear)
                    .listRowInsets(EdgeInsets())
            }
            if let loadError {
                Text(loadError).foregroundStyle(.red)
            } else if let audit {
                if audit.compared {
                    Section {
                        TotalsStrip(fromTitle: tr("صافي \(audit.from)", "Net \(audit.from)"), toTitle: tr("صافي \(audit.to)", "Net \(audit.to)"),
                                    before: audit.netFrom, after: audit.netTo)
                            .accessibilityIdentifier("salaryAuditSummary")
                            .listRowBackground(Color.clear)
                            .listRowInsets(EdgeInsets(top: 0, leading: 0, bottom: 8, trailing: 0))
                        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: 3), spacing: 8) {
                            ForEach(SalaryChangeKind.allCases) { kind in
                                ChangeTile(count: audit.rows(kind).count, title: kind.title, color: color(kind),
                                           open: { openKind = kind },
                                           export: { excel in export(kind: kind, excel: excel) })
                                    .accessibilityIdentifier("salaryTile-\(kind.rawValue)")
                            }
                        }
                        .listRowBackground(Color.clear)
                        .listRowInsets(EdgeInsets())
                    }
                } else {
                    Section {
                        Text(from == to ? tr("اختر شهرين مختلفين للمقارنة.", "Choose two different months.")
                                        : tr("لا توجد رواتب في شهر \(from) للمقارنة.", "No payroll in \(from) to compare."))
                            .foregroundStyle(.secondary)
                    }
                }
                Section(tr("فحوص \(audit.to)", "Checks for \(audit.to)")) {
                    ForEach(audit.checks) { check in
                        AuditCheckLink(check: check, month: audit.to) { excel in exportCheck(check, excel: excel) }
                    }
                }
                Section {
                    if audit.staffChecks.isEmpty {
                        Text(tr("لم يُستورد ملف الملاك بعد.", "No staff file imported yet.")).foregroundStyle(.secondary)
                    }
                    ForEach(audit.staffChecks) { check in
                        AuditCheckLink(check: check, month: audit.to) { excel in exportCheck(check, excel: excel) }
                    }
                } header: {
                    Text(tr("مطابقة الملاك", "Staff match"))
                }
            } else {
                ProgressView(tr("جارٍ تدقيق الرواتب…", "Checking payroll…"))
                    .frame(maxWidth: .infinity).listRowBackground(Color.clear)
            }
        }
        .listSectionSpacing(.compact)
        .navigationTitle(tr("تدقيق الرواتب", "Payroll audit"))
        .navigationBarTitleDisplayMode(.inline)
        .navigationDestination(item: $openKind) { kind in
            if let audit { SalaryChangeListView(audit: audit, kind: kind) }
        }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    showExport = true
                } label: {
                    ReportToolbarLabel(isWorking: exporting)
                }
                .disabled(audit == nil || exporting)
                .accessibilityIdentifier("salaryAuditExport")
            }
        }
        .sheet(isPresented: $showExport) {
            if let audit {
                ChangeExportSheet(title: tr("تصدير متغيرات الرواتب", "Export payroll changes"),
                                  span: tr("من \(audit.from) إلى \(audit.to)", "From \(audit.from) to \(audit.to)"),
                                  choices: [ExportChoice(id: "all", title: tr("كشف شامل بكل المتغيرات", "Full report"),
                                                         subtitle: tr("خلاصة + جميع حالات التغيّر", "Summary + all change cases"),
                                                         count: SalaryChangeKind.allCases.reduce(0) { $0 + audit.rows($1).count })]
                                    + SalaryChangeKind.allCases.map {
                                        ExportChoice(id: $0.rawValue, title: $0.reportTitle, subtitle: tr("كشف منفصل", "Separate report"), count: audit.rows($0).count)
                                    }) { id, excel in
                    let kinds = SalaryChangeKind(rawValue: id).map { [$0] }
                    let files = try await AuditExport.salary(audit, kinds: kinds)
                    return excel ? files.excel : files.pdf
                }
            }
        }
        .alert(tr("التصدير", "Export"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
        .task(id: loadKey) {
            audit = nil
            loadError = nil
            guard !to.isEmpty else { return }
            do {
                let loaded = try await AuditResults.shared.salary(from: from, to: to, container: SharedModel.container)
                guard !Task.isCancelled else { return }
                audit = loaded
                // اختبار المحاكي: -salaryAuditKind net يفتح قائمة، -auditExportSheet YES نافذة التصدير.
                let defaults = UserDefaults.standard
                if let raw = defaults.string(forKey: "salaryAuditKind"), let kind = SalaryChangeKind(rawValue: raw) {
                    defaults.removeObject(forKey: "salaryAuditKind")
                    openKind = kind
                } else if defaults.bool(forKey: "auditExportSheet") {
                    defaults.removeObject(forKey: "auditExportSheet")
                    showExport = true
                }
            } catch {
                guard !Task.isCancelled, !(error is CancellationError) else { return }
                loadError = error.localizedDescription
            }
        }
    }

    private func color(_ kind: SalaryChangeKind) -> Color {
        switch kind {
        case .added: .green
        case .removed: .red
        case .net: .blue
        case .rank, .workplace: .primary
        case .bank: .red
        }
    }

    private func export(kind: SalaryChangeKind, excel: Bool) {
        guard let audit else { return }
        run(excel: excel) { try await AuditExport.salary(audit, kinds: [kind]) }
    }

    private func exportCheck(_ check: AuditCheck, excel: Bool) {
        run(excel: excel) { try await AuditExport.check(check, month: to) }
    }

    private func run(excel: Bool, _ make: @escaping () async throws -> (excel: URL, pdf: URL)) {
        exporting = true
        Task {
            defer { exporting = false }
            do {
                let files = try await make()
                ActivityShare.present([excel ? files.excel : files.pdf])
            } catch { self.error = error.localizedDescription }
        }
    }
}

/// قائمة حالة واحدة، مع بطاقات الحالات في الأعلى للتنقل بينها.
struct SalaryChangeListView: View {
    let audit: SalaryAudit
    @State var kind: SalaryChangeKind
    @State private var search = ""
    @State private var exporting = false

    private var filtered: [SalaryChange] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        let rows = audit.rows(kind)
        guard !tokens.isEmpty else { return rows }
        return rows.filter { r in
            let text = ArabicText.normalize(r.name + " " + r.line + " " + r.reasons.joined(separator: " "))
            return tokens.allSatisfy { text.contains($0) }
        }
    }

    var body: some View {
        List {
            Section {
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: 3), spacing: 8) {
                    ForEach(SalaryChangeKind.allCases) { k in
                        ChangeTile(count: audit.rows(k).count, title: k.title, color: k == .removed || k == .bank ? .red : k == .added ? .green : .blue,
                                   selected: k == kind, open: { kind = k; search = "" },
                                   export: { excel in export([k], excel: excel) })
                    }
                }
                .listRowBackground(Color.clear)
                .listRowInsets(EdgeInsets())
            }
            Section {
                let rows = filtered
                if rows.isEmpty { Text(tr("لا يوجد", "None")).foregroundStyle(.secondary) }
                ForEach(rows.prefix(500)) { row in
                    SalaryChangeRow(row: row, kind: kind, from: audit.from, to: audit.to)
                        .personActions(name: row.name, month: kind == .removed ? audit.from : audit.to)
                }
            } header: {
                Text(tr("\(Num.format(filtered.count)) · \(kind.reportTitle)", "\(filtered.count) · \(kind.reportTitle)"))
            } footer: {
                if filtered.count > 500 { Text(tr("يظهر أول 500؛ التصدير يشمل الجميع.", "First 500 shown; export has all.")) }
            }
        }
        .accessibilityIdentifier("salaryChangeList")
        .listSectionSpacing(.compact)
        .adaptiveSearchable(text: $search, prompt: tr("بحث بالاسم أو الرتبة أو البند", "Name, rank or item"))
        .navigationTitle(kind.title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button(tr("تصدير هذه الحالة Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export([kind], excel: true) }
                    Button(tr("تصدير هذه الحالة PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export([kind], excel: false) }
                } label: { ReportToolbarLabel(isWorking: exporting) }
                .disabled(exporting)
            }
        }
    }

    private func export(_ kinds: [SalaryChangeKind], excel: Bool) {
        exporting = true
        Task {
            defer { exporting = false }
            if let files = try? await AuditExport.salary(audit, kinds: kinds) {
                ActivityShare.present([excel ? files.excel : files.pdf])
            }
        }
    }
}

private struct SalaryChangeRow: View {
    let row: SalaryChange
    let kind: SalaryChangeKind
    let from: String
    let to: String

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(alignment: .firstTextBaseline) {
                Text(row.name).font(.subheadline.weight(.semibold))
                Spacer(minLength: 8)
                Text(amountText).font(.subheadline.weight(.bold).monospacedDigit()).foregroundStyle(amountColor)
            }
            if !row.line.isEmpty { Text(row.line).font(.caption).foregroundStyle(.secondary) }
            if row.before != nil && row.after != nil && kind == .net {
                Text("\(Money.format(row.before)) ← \(Money.format(row.after))").font(.caption.monospacedDigit())
            }
            if !row.reasons.isEmpty {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 5) { chips }
                    VStack(alignment: .leading, spacing: 4) { chips }
                }
            }
        }
        .padding(.vertical, 2)
    }

    @ViewBuilder private var chips: some View {
        ForEach(row.reasons, id: \.self) { reason in
            Text(reason)
                .font(.caption2)
                .padding(.horizontal, 7).padding(.vertical, 3)
                .background(Color(.tertiarySystemFill), in: RoundedRectangle(cornerRadius: 7, style: .continuous))
        }
    }

    private var amountText: String {
        switch kind {
        case .added: Money.format(row.after)
        case .removed: Money.format(row.before)
        default: row.difference == 0 ? "0" : (row.difference > 0 ? "+" : "") + Money.format(row.difference)
        }
    }

    private var amountColor: Color {
        switch kind {
        case .added: .green
        case .removed: .red
        default: row.difference > 0 ? .orange : row.difference < 0 ? .red : .secondary
        }
    }
}

// MARK: - تدقيق الاستقطاعات

struct DeductionAuditView: View {
    let months: [String]
    @State private var from: String
    @State private var to: String
    @State private var result: DeductionComparison?
    @State private var checks: [AuditCheck]?
    @State private var loadError: String?
    @State private var confirmedEmpty = false
    @State private var showExport = false
    @State private var exporting = false
    @State private var error: String?
    @State private var openKind: DeductionGroupKind?
    @Query private var savedMonths: [PayrollMonth]
    @State private var importState = StaffImportState.shared

    init(months: [String], to: String = "") {
        self.months = months
        let d = MonthPairPicker.defaults(months)
        _from = State(initialValue: d.from)
        _to = State(initialValue: d.to)
    }

    private var loadKey: String {
        let stamps = savedMonths.filter { $0.name == to || $0.name == from }
            .map { "\($0.name):\($0.importedAt.timeIntervalSince1970)" }.sorted().joined(separator: "|")
        return "\(from)|\(to)|\(stamps)|\(importState.version)"
    }
    private var ready: Bool {
        guard let result else { return false }
        return result.emptyMonths.isEmpty || confirmedEmpty
    }

    var body: some View {
        List {
            Section {
                MonthPairPicker(months: months, from: $from, to: $to)
                    .listRowBackground(Color.clear)
                    .listRowInsets(EdgeInsets())
            }
            if months.count < 2 || from == to {
                Text(tr("اختر شهرين مختلفين للمقارنة.", "Choose two different months.")).foregroundStyle(.secondary)
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
                    Section {
                        TotalsStrip(fromTitle: result.from, toTitle: result.to, before: result.before, after: result.after)
                            .accessibilityIdentifier("deductionAuditSummary")
                            .listRowBackground(Color.clear)
                            .listRowInsets(EdgeInsets(top: 0, leading: 0, bottom: 8, trailing: 0))
                        HStack(spacing: 8) {
                            ForEach(DeductionGroupKind.allCases) { kind in
                                let rows = result.rows(kind)
                                ChangeTile(count: rows.count, title: kind.title, amount: rows.reduce(0) { $0 + $1.difference },
                                           color: color(kind), open: { openKind = kind },
                                           export: { excel in export([kind], excel: excel) })
                                    .accessibilityIdentifier("deductionTile-\(kind.rawValue)")
                            }
                        }
                        .listRowBackground(Color.clear)
                        .listRowInsets(EdgeInsets())
                    } footer: {
                        Text(tr("كل استقطاع = الموظف + الجهة + رقم الإضبارة. تغيّر الإضبارة يظهر كتوقف واستقطاع جديد.",
                                "Each deduction = employee + entity + case number. A changed case number shows as stopped and new."))
                    }
                }
            } else {
                ProgressView(tr("جارٍ مقارنة الاستقطاعات…", "Comparing deductions…"))
                    .frame(maxWidth: .infinity).listRowBackground(Color.clear)
            }
            if let checks, !to.isEmpty {
                Section(tr("فحوص \(to)", "Checks for \(to)")) {
                    ForEach(checks) { check in
                        AuditCheckLink(check: check, month: to) { excel in
                            run(excel: excel) { try await AuditExport.check(check, month: to) }
                        }
                    }
                }
            }
        }
        .listSectionSpacing(.compact)
        .navigationTitle(tr("تدقيق الاستقطاعات", "Deduction audit"))
        .navigationBarTitleDisplayMode(.inline)
        .navigationDestination(item: $openKind) { kind in
            if let result { DeductionChangeListView(result: result, kind: kind) }
        }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    showExport = true
                } label: {
                    ReportToolbarLabel(isWorking: exporting)
                }
                .disabled(!ready || exporting)
                .accessibilityIdentifier("deductionAuditExport")
            }
        }
        .sheet(isPresented: $showExport) {
            if let result {
                ChangeExportSheet(title: tr("تصدير المقارنة", "Export comparison"),
                                  span: tr("من \(result.from) إلى \(result.to)", "From \(result.from) to \(result.to)"),
                                  choices: [ExportChoice(id: "all", title: tr("كشف شامل بكل المتغيرات", "Full report"),
                                                         subtitle: tr("خلاصة + توقف + جديد + تغيّر المبلغ", "Summary + stopped + new + changed"),
                                                         count: DeductionGroupKind.allCases.reduce(0) { $0 + result.rows($1).count })]
                                    + DeductionGroupKind.allCases.map {
                                        ExportChoice(id: $0.rawValue, title: $0.title,
                                                     subtitle: $0 == .changed ? tr("نفس الجهة والإضبارة", "Same entity and case") : tr("كشف منفصل", "Separate report"),
                                                     count: result.rows($0).count)
                                    }) { id, excel in
                    let kinds = DeductionGroupKind(rawValue: id).map { [$0] }
                    let files = try await AuditExport.deductions(result, kinds: kinds)
                    return excel ? files.excel : files.pdf
                }
            }
        }
        .alert(tr("التصدير", "Export"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
        .task(id: loadKey) {
            result = nil
            checks = nil
            loadError = nil
            confirmedEmpty = false
            guard !to.isEmpty else { return }
            let container = SharedModel.container
            let month = to
            checks = try? await AuditResults.shared.deductionChecks(month: month, container: container)
            guard months.count >= 2, from != to else { return }
            do {
                let loaded = try await AuditResults.shared.deductions(from: from, to: to, container: container)
                guard !Task.isCancelled else { return }
                result = loaded
                let defaults = UserDefaults.standard
                if let raw = defaults.string(forKey: "deductionKind"), let kind = DeductionGroupKind(rawValue: raw) {
                    defaults.removeObject(forKey: "deductionKind")
                    openKind = kind
                } else if defaults.bool(forKey: "auditExportSheet") {
                    defaults.removeObject(forKey: "auditExportSheet")
                    showExport = true
                }
            } catch {
                guard !Task.isCancelled, !(error is CancellationError) else { return }
                loadError = error.localizedDescription
            }
        }
    }

    private func color(_ kind: DeductionGroupKind) -> Color {
        switch kind {
        case .stopped: .red
        case .started: .green
        case .changed: .orange
        }
    }

    private func export(_ kinds: [DeductionGroupKind], excel: Bool) {
        guard ready, let result else { return }
        run(excel: excel) { try await AuditExport.deductions(result, kinds: kinds) }
    }

    private func run(excel: Bool, _ make: @escaping () async throws -> (excel: URL, pdf: URL)) {
        exporting = true
        Task {
            defer { exporting = false }
            do {
                let files = try await make()
                ActivityShare.present([excel ? files.excel : files.pdf])
            } catch { self.error = error.localizedDescription }
        }
    }
}

struct DeductionChangeListView: View {
    let result: DeductionComparison
    @State var kind: DeductionGroupKind
    @State private var search = ""
    @State private var exporting = false

    private var filtered: [DeductionChange] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        let rows = result.rows(kind)
        guard !tokens.isEmpty else { return rows }
        return rows.filter { r in tokens.allSatisfy { ArabicText.normalize(r.name + " " + r.entity + " " + ArabicText.latinDigits(r.note)).contains($0) } }
    }

    var body: some View {
        List {
            Section {
                HStack(spacing: 8) {
                    ForEach(DeductionGroupKind.allCases) { k in
                        let rows = result.rows(k)
                        ChangeTile(count: rows.count, title: k.title, amount: rows.reduce(0) { $0 + $1.difference },
                                   color: k == .stopped ? .red : k == .started ? .green : .orange,
                                   selected: k == kind, open: { kind = k; search = "" },
                                   export: { excel in export([k], excel: excel) })
                    }
                }
                .listRowBackground(Color.clear)
                .listRowInsets(EdgeInsets())
            }
            Section {
                let rows = filtered
                if rows.isEmpty { Text(tr("لا يوجد", "None")).foregroundStyle(.secondary) }
                ForEach(rows.prefix(500)) { row in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack(alignment: .firstTextBaseline) {
                            Text(row.name).font(.subheadline.weight(.semibold))
                            Spacer(minLength: 8)
                            Text((row.difference > 0 ? "+" : "") + Money.format(row.difference))
                                .font(.subheadline.weight(.bold).monospacedDigit())
                                .foregroundStyle(row.difference > 0 ? (kind == .started ? Color.green : Color.orange) : Color.red)
                        }
                        Text(row.entity + " · " + (row.note.isEmpty ? tr("الإضبارة غير متوفرة", "No case number") : tr("إضبارة \(row.note)", "Case \(row.note)")))
                            .font(.caption).foregroundStyle(.secondary)
                        Text("\(result.from) \(Money.format(row.before)) ← \(result.to) \(Money.format(row.after))")
                            .font(.caption.monospacedDigit())
                    }
                    .padding(.vertical, 2)
                    .personActions(name: row.name, month: kind == .stopped ? result.from : result.to,
                                   copies: row.note.isEmpty ? [] : [CopyItem(label: tr("نسخ رقم الإضبارة", "Copy case number"), value: row.note)])
                }
            } header: {
                Text(tr("\(Num.format(filtered.count)) · \(kind.reportTitle)", "\(filtered.count) · \(kind.reportTitle)"))
            } footer: {
                if filtered.count > 500 { Text(tr("يظهر أول 500؛ التصدير يشمل الجميع.", "First 500 shown; export has all.")) }
            }
        }
        .accessibilityIdentifier("deductionChangeList")
        .listSectionSpacing(.compact)
        .adaptiveSearchable(text: $search, prompt: tr("بحث بالاسم أو الجهة أو الإضبارة", "Name, entity or case"))
        .navigationTitle(kind.title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button(tr("تصدير هذه الحالة Excel", "Export Excel"), systemImage: ReportSymbols.excel) { export([kind], excel: true) }
                    Button(tr("تصدير هذه الحالة PDF", "Export PDF"), systemImage: ReportSymbols.pdf) { export([kind], excel: false) }
                } label: { ReportToolbarLabel(isWorking: exporting) }
                .disabled(exporting)
            }
        }
    }

    private func export(_ kinds: [DeductionGroupKind], excel: Bool) {
        exporting = true
        Task {
            defer { exporting = false }
            if let files = try? await AuditExport.deductions(result, kinds: kinds) {
                ActivityShare.present([excel ? files.excel : files.pdf])
            }
        }
    }
}
