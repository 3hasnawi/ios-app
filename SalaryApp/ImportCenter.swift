import SwiftUI
import SwiftData
import UniformTypeIdentifiers

/// نوع الملف المستورد.
nonisolated enum ImportKind: String, Sendable, CaseIterable, Identifiable {
    case payrollAndDeductions, payroll, deductions, staff, skip
    var id: String { rawValue }

    var title: String {
        switch self {
        case .payrollAndDeductions: tr("رواتب واستقطاعات", "Payroll + deductions")
        case .payroll: tr("رواتب", "Payroll")
        case .deductions: tr("استقطاعات", "Deductions")
        case .staff: tr("ملاك", "Staff")
        case .skip: tr("تجاهل", "Skip")
        }
    }

    var systemImage: String {
        switch self {
        case .payrollAndDeductions: "doc.on.doc.fill"
        case .payroll: "banknote.fill"
        case .deductions: "minus.circle.fill"
        case .staff: "person.3.fill"
        case .skip: "xmark.circle"
        }
    }

    var color: Color {
        switch self {
        case .payrollAndDeductions: .indigo
        case .payroll: .blue
        case .deductions: .orange
        case .staff: .green
        case .skip: .gray
        }
    }

    var needsMonth: Bool { self == .payroll || self == .deductions || self == .payrollAndDeductions }
}

/// ملف بعد فحصه: نوعه وشهره كما تعرّف عليهما التطبيق، ويمكن تعديلهما قبل الاستيراد.
struct ImportCandidate: Identifiable {
    let id = UUID()
    let url: URL
    let fileName: String
    var kind: ImportKind
    var month: String
    /// الأنواع الممكنة لهذا الملف بحسب محتواه.
    let allowedKinds: [ImportKind]
    let result: ImportResult?
    let staffCount: Int?
    let error: String?
    /// الشهر مفهوم من اسم الملف.
    let monthDetected: Bool

    var summary: String {
        if let error { return error }
        if let staffCount { return tr("\(Num.format(staffCount)) موظف في الملاك", "\(Num.format(staffCount)) staff records") }
        guard let result else { return "" }
        var parts: [String] = []
        if kind != .deductions, !result.employees.isEmpty {
            parts.append(tr("\(Num.format(result.employees.count)) موظف", "\(Num.format(result.employees.count)) employees"))
        }
        if kind != .payroll, !result.deductions.isEmpty {
            parts.append(tr("\(Num.format(result.deductions.count)) استقطاع", "\(Num.format(result.deductions.count)) deductions"))
        }
        return parts.joined(separator: " · ")
    }
}

/// زر استيراد واحد: يختار المستخدم أي ملفات (رواتب، استقطاعات، ملاك)، والتطبيق يتعرّف على نوع كل ملف
/// وشهره من محتواه واسمه، ثم تُعرض للمراجعة قبل الاستيراد.
@MainActor @Observable
final class ImportCenter {
    static let shared = ImportCenter()

    static let fileTypes: [UTType] = [
        UTType(filenameExtension: "xlsx") ?? .spreadsheet,
        .commaSeparatedText,
    ] + accessFileTypes

    var showPicker = false
    var showReview = false
    var candidates: [ImportCandidate] = []
    var isBusy = false
    var message: String?
    /// أحدث شهر رواتب استُورد (تختاره الشاشة الرئيسية).
    var lastPayrollMonth: String?
    /// للاختبار: يستورد مباشرة بعد الفحص بدون نافذة المراجعة.
    private var autoRun = false
    private var pickedURLs: [URL] = []

    func pick() {
        guard !isBusy else { return }
        showPicker = true
    }

    func picked(_ urls: [URL]) {
        pickedURLs = urls
        showPicker = false
    }

    /// بعد إغلاق نافذة الملفات.
    func pickerDismissed() {
        let urls = pickedURLs
        pickedURLs = []
        guard !urls.isEmpty else { return }
        Task { await analyze(urls) }
    }

    // MARK: - الفحص

    func analyze(_ urls: [URL], autoRun: Bool = false, review: Bool = true) async {
        guard !isBusy else { return }
        isBusy = true
        self.autoRun = autoRun
        let progress = ImportProgress.shared
        progress.begin(urls.count == 1 ? tr("فحص الملف", "Checking file")
                       : urls.count == 2 ? tr("فحص ملفين", "Checking 2 files")
                       : tr("فحص \(Num.format(urls.count)) ملفات", "Checking \(urls.count) files"))

        let existing = existingMonths()
        var found: [ImportCandidate] = []
        for (index, url) in urls.enumerated() {
            let share = 1.0 / Double(urls.count)
            let name = url.deletingPathExtension().lastPathComponent
            let sink = progress.sink(Double(index) * share, Double(index + 1) * share, tr("قراءة \(name)", "Reading \(name)"))
            let ext = url.pathExtension.lowercased()
            if ext == "accdb" || ext == "mdb" {
                do {
                    let count = try await Task.detached(priority: .userInitiated) {
                        let granted = url.startAccessingSecurityScopedResource()
                        defer { if granted { url.stopAccessingSecurityScopedResource() } }
                        return try StaffImporter.peekCount(at: url)
                    }.value
                    found.append(ImportCandidate(url: url, fileName: name, kind: .staff, month: "", allowedKinds: [.staff, .skip],
                                                 result: nil, staffCount: count, error: nil, monthDetected: true))
                } catch {
                    found.append(Self.failed(url, name, error))
                }
                sink(1)
                continue
            }
            do {
                let result = try await Task.detached(priority: .userInitiated) {
                    let granted = url.startAccessingSecurityScopedResource()
                    defer { if granted { url.stopAccessingSecurityScopedResource() } }
                    return try ExcelImporter.load(url: url, progress: sink)
                }.value
                let hasPay = !result.employees.isEmpty
                let hasDed = !result.deductions.isEmpty
                let kind: ImportKind = hasPay && hasDed ? .payrollAndDeductions : (hasPay ? .payroll : .deductions)
                let allowed: [ImportKind] = hasPay && hasDed ? [.payrollAndDeductions, .payroll, .deductions, .skip] : [kind, .skip]
                let (month, detected) = Self.month(for: url, existing: existing)
                found.append(ImportCandidate(url: url, fileName: name, kind: kind, month: month, allowedKinds: allowed,
                                             result: result, staffCount: nil, error: nil, monthDetected: detected))
            } catch {
                found.append(Self.failed(url, name, error))
            }
        }
        progress.end()
        candidates = found
        isBusy = false
        if autoRun {
            await run()
        } else if review {
            showReview = true
        }
    }

    private static func failed(_ url: URL, _ name: String, _ error: Error) -> ImportCandidate {
        ImportCandidate(url: url, fileName: name, kind: .skip, month: "", allowedKinds: [.skip],
                        result: nil, staffCount: nil, error: error.localizedDescription, monthDetected: false)
    }

    private func existingMonths() -> [PayrollMonth] {
        (try? SharedModel.container.mainContext.fetch(FetchDescriptor<PayrollMonth>())) ?? []
    }

    /// الشهر من اسم الملف بعد حذف كلمات النوع ("استقطاعات تشرين الاول 2026" ← "تشرين الاول 2026").
    /// إذا كان الشهر محفوظاً باسم آخر (نفس السنة والشهر) يُستخدم الاسم المحفوظ حتى يرتبط الملفان.
    static func month(for url: URL, existing: [PayrollMonth]) -> (String, Bool) {
        let label = MonthName.label(for: url)
        let typeWords: Set<String> = ["رواتب", "راتب", "الرواتب", "استقطاعات", "استقطاع", "الاستقطاعات", "ملف", "كشف", "قسيمة"]
            .reduce(into: Set<String>()) { $0.insert(ArabicText.normalize($1)) }
        let words = label.replacingOccurrences(of: "_", with: " ").split(separator: " ").map(String.init)
        let kept = words.filter { !typeWords.contains(ArabicText.normalize($0)) }
        let cleaned = kept.isEmpty ? label : kept.joined(separator: " ")
        let key = MonthName.sortKey(cleaned)
        if key != 0, let match = existing.first(where: { $0.sortKey == key }) {
            return (match.name, true)
        }
        return (cleaned, key != 0)
    }

    /// كل الأشهر المتاحة في قائمة الاختيار: المحفوظة والمقروءة من الملفات.
    var monthChoices: [String] {
        let saved = existingMonths().sorted { $0.sortKey > $1.sortKey }.map(\.name)
        var all = saved
        for c in candidates where c.kind.needsMonth && !all.contains(c.month) { all.insert(c.month, at: 0) }
        return all
    }

    /// ما سيُستبدل من البيانات المحفوظة.
    var replacements: [String] {
        let saved = Dictionary(existingMonths().map { ($0.name, $0) }, uniquingKeysWith: { a, _ in a })
        var lines: [String] = []
        for c in candidates {
            switch c.kind {
            case .payrollAndDeductions, .payroll:
                if (saved[c.month]?.employeeCount ?? 0) > 0 { lines.append(tr("رواتب \(c.month)", "payroll of \(c.month)")) }
                if c.kind == .payrollAndDeductions, (saved[c.month]?.deductionCount ?? 0) > 0 {
                    lines.append(tr("استقطاعات \(c.month)", "deductions of \(c.month)"))
                }
            case .deductions:
                if (saved[c.month]?.deductionCount ?? 0) > 0 { lines.append(tr("استقطاعات \(c.month)", "deductions of \(c.month)")) }
            case .staff:
                if StaffInfo.count > 0 { lines.append(tr("الملاك كاملاً", "all staff data")) }
            case .skip:
                break
            }
        }
        var seen = Set<String>()
        return lines.filter { seen.insert($0).inserted }
    }

    /// استقطاعات لشهر لا توجد له رواتب (محفوظة أو في نفس الاستيراد).
    var deductionsWithoutPayroll: [String] {
        let saved = Dictionary(existingMonths().map { ($0.name, $0.employeeCount) }, uniquingKeysWith: { a, _ in a })
        let payrollMonths = Set(candidates.filter { $0.kind == .payroll || $0.kind == .payrollAndDeductions }.map(\.month))
        return candidates.filter { $0.kind == .deductions && (saved[$0.month] ?? 0) == 0 && !payrollMonths.contains($0.month) }.map(\.month)
    }

    var importableCount: Int { candidates.filter { $0.kind != .skip }.count }

    // MARK: - الاستيراد

    func cancel() {
        for c in candidates { try? FileManager.default.removeItem(at: c.url) }
        candidates = []
        showReview = false
    }

    func run() async {
        guard !isBusy else { return }
        let jobs = candidates.filter { $0.kind != .skip }
            // الملف الجامع أولاً حتى لا يمحو ملفاً منفصلاً لنفس الشهر.
            .sorted { Self.order($0.kind) < Self.order($1.kind) }
        showReview = false
        guard !jobs.isEmpty else { cancel(); return }
        let fromTest = autoRun
        isBusy = true
        let progress = ImportProgress.shared
        progress.begin(jobs.count == 1 ? tr("استيراد ملف", "Importing file") : tr("استيراد \(Num.format(jobs.count)) ملفات", "Importing \(jobs.count) files"))
        let container = SharedModel.container
        let start = Date()
        var lines: [String] = []
        var failures: [String] = []
        var warnings: [String] = []
        var totals = (employees: 0, deductions: 0)
        var months = Set<String>()
        var newestPayroll: (key: Int, name: String)?

        for (index, job) in jobs.enumerated() {
            let share = 1.0 / Double(jobs.count)
            let counter = jobs.count > 1 ? tr(" (\(Num.format(index + 1)) من \(Num.format(jobs.count)))", " (\(index + 1) of \(jobs.count))") : ""
            let sink = progress.sink(Double(index) * share, Double(index + 1) * share, tr("حفظ \(job.fileName)\(counter)", "Saving \(job.fileName)\(counter)"))
            do {
                switch job.kind {
                case .staff:
                    StaffImportState.shared.isImporting = true
                    let url = job.url
                    let count = try await Task.detached(priority: .userInitiated) {
                        let granted = url.startAccessingSecurityScopedResource()
                        defer { if granted { url.stopAccessingSecurityScopedResource() } }
                        return try await StaffImporter(modelContainer: container).importDatabase(at: url, progress: sink)
                    }.value
                    StaffImportState.shared.isImporting = false
                    StaffImportState.shared.version += 1
                    lines.append(tr("الملاك: \(Num.format(count)) موظف", "Staff: \(Num.format(count)) employees"))
                case .payrollAndDeductions, .payroll, .deductions:
                    guard let result = job.result else { continue }
                    let month = job.month
                    let kind = job.kind
                    try await Task.detached(priority: .userInitiated) {
                        let importer = DataImporter(modelContainer: container)
                        switch kind {
                        case .payroll: try await importer.replacePayroll(month: month, with: result.employees, progress: sink)
                        case .deductions: try await importer.replaceDeductions(month: month, with: result.deductions, progress: sink)
                        default: try await importer.replace(month: month, with: result, progress: sink)
                        }
                    }.value
                    let e = kind == .deductions ? 0 : result.employees.count
                    let d = kind == .payroll ? 0 : result.deductions.count
                    totals.employees += e
                    totals.deductions += d
                    months.insert(month)
                    var parts: [String] = []
                    if e > 0 { parts.append(tr("\(Num.format(e)) موظف", "\(Num.format(e)) employees")) }
                    if d > 0 { parts.append(tr("\(Num.format(d)) استقطاع", "\(Num.format(d)) deductions")) }
                    lines.append("\(month): " + parts.joined(separator: tr(" و", ", ")))
                    if kind != .deductions {
                        let odd = HealthCareReport.irregularCount(in: result.employees)
                        if odd > 0 {
                            warnings.append(tr("⚠️ \(month): \(Num.format(odd)) مبلغ رعاية صحية لا يقبل القسمة على \(Num.format(3500)). راجعه في التقارير ← الرعاية الصحية.",
                                               "⚠️ \(month): \(odd) health care amounts are not divisible by 3,500. Check Reports → Health care."))
                        }
                        let key = MonthName.sortKey(month)
                        if newestPayroll == nil || key >= newestPayroll!.key { newestPayroll = (key, month) }
                    }
                case .skip:
                    break
                }
            } catch {
                StaffImportState.shared.isImporting = false
                failures.append("\(job.fileName): \(error.localizedDescription)")
            }
        }
        // نسخ مؤقتة من نافذة الملفات تُحذف بعد الاستيراد (ملفات الاختبار تبقى).
        if !fromTest { for c in candidates { try? FileManager.default.removeItem(at: c.url) } }
        candidates = []
        progress.end()
        isBusy = false
        if let newestPayroll { lastPayrollMonth = newestPayroll.name }

        var parts: [String] = []
        if !lines.isEmpty { parts += [tr("تم الاستيراد:", "Imported:")] + lines }
        if !failures.isEmpty { parts += ["", tr("تعذّر:", "Failed:")] + failures }
        if !warnings.isEmpty { parts += [""] + warnings }
        if autoRun {
            // اختبار المحاكي يقرأ هذا التقرير.
            let status = failures.isEmpty ? "" : " failed=\(failures.count)"
            LaunchOptions.writeReport("employees=\(totals.employees) deductions=\(totals.deductions) months=\(months.count)\(status)"
                + String(format: " seconds=%.1f\n", Date().timeIntervalSince(start)))
            autoRun = false
        } else {
            message = parts.joined(separator: "\n")
        }
    }

    private static func order(_ kind: ImportKind) -> Int {
        switch kind {
        case .staff: 0
        case .payrollAndDeductions: 1
        case .payroll: 2
        case .deductions: 3
        case .skip: 4
        }
    }
}

// MARK: - نافذة المراجعة

struct ImportReviewSheet: View {
    @Bindable var center: ImportCenter

    var body: some View {
        NavigationStack {
            List {
                Section {
                    ForEach($center.candidates) { $item in
                        CandidateRow(item: $item, months: center.monthChoices)
                    }
                } header: {
                    VStack(spacing: 4) {
                        Text(center.candidates.count == 1 ? tr("تم التعرّف على الملف", "File recognised")
                             : center.candidates.count == 2 ? tr("تم التعرّف على ملفين", "2 files recognised")
                             : tr("تم التعرّف على \(Num.format(center.candidates.count)) ملفات", "\(center.candidates.count) files recognised"))
                            .font(.title3.bold())
                            .foregroundStyle(Color(.label))
                        Text(tr("راجع النوع والشهر قبل الاستيراد", "Check the type and month before importing"))
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    .textCase(nil)
                    .frame(maxWidth: .infinity)
                    .padding(.bottom, 8)
                }

                let replaced = center.replacements
                if !replaced.isEmpty {
                    Section {
                        Label {
                            Text(tr("سيُستبدل: \(replaced.joined(separator: "، "))", "Will replace: \(replaced.joined(separator: ", "))"))
                                .font(.subheadline)
                        } icon: {
                            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                        }
                        .listRowBackground(Color.orange.opacity(0.12))
                    }
                }
                let orphan = center.deductionsWithoutPayroll
                if !orphan.isEmpty {
                    Section {
                        Label {
                            Text(tr("لا توجد رواتب لـ \(orphan.joined(separator: "، ")) بعد: تُحفظ الاستقطاعات وتظهر عند استيراد رواتب الشهر.",
                                    "No payroll yet for \(orphan.joined(separator: ", ")): deductions are saved and appear once that month's payroll is imported."))
                                .font(.subheadline)
                        } icon: {
                            Image(systemName: "info.circle.fill").foregroundStyle(.blue)
                        }
                    }
                }
            }
            .safeAreaInset(edge: .bottom) {
                Button {
                    Task { await center.run() }
                } label: {
                    Text(center.importableCount <= 1 ? tr("استيراد", "Import") : tr("استيراد الكل", "Import all"))
                        .font(.headline)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 6)
                }
                .glassButtonStyle(prominent: true)
                .buttonBorderShape(.capsule)
                .disabled(center.importableCount == 0)
                .padding(.horizontal, 20)
                .padding(.bottom, 8)
                .accessibilityIdentifier("importAll")
            }
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(tr("إلغاء", "Cancel"), systemImage: "xmark") { center.cancel() }
                }
            }
        }
        .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
        .interactiveDismissDisabled()
    }
}

private struct CandidateRow: View {
    @Binding var item: ImportCandidate
    let months: [String]

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: item.error == nil ? item.kind.systemImage : "exclamationmark.triangle.fill")
                .font(.body.weight(.semibold))
                .foregroundStyle(.white)
                .frame(width: 38, height: 38)
                .background((item.error == nil ? item.kind.color : .red).gradient, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            VStack(alignment: .leading, spacing: 6) {
                Text(item.fileName).font(.body.weight(.semibold)).lineLimit(2)
                Text(item.summary)
                    .font(.caption)
                    .foregroundStyle(item.error == nil ? Color.secondary : Color.red)
                if item.error == nil {
                    HStack(spacing: 6) {
                        Menu {
                            Picker(tr("النوع", "Type"), selection: $item.kind) {
                                ForEach(item.allowedKinds) { Label($0.title, systemImage: $0.systemImage).tag($0) }
                            }
                        } label: {
                            chip(item.kind.title, color: item.kind.color)
                        }
                        if item.kind.needsMonth {
                            Menu {
                                Picker(tr("الشهر", "Month"), selection: $item.month) {
                                    ForEach(months, id: \.self) { Text($0).tag($0) }
                                }
                            } label: {
                                chip(item.month, color: item.monthDetected ? .secondary : .red)
                            }
                        }
                    }
                    if item.kind.needsMonth && !item.monthDetected {
                        Text(tr("لم يُعرف الشهر من اسم الملف، اختره من القائمة.", "Couldn't tell the month from the file name; pick it."))
                            .font(.caption2)
                            .foregroundStyle(.red)
                    }
                }
            }
        }
        .padding(.vertical, 4)
    }

    private func chip(_ title: String, color: Color) -> some View {
        HStack(spacing: 4) {
            Text(title).lineLimit(1)
            Image(systemName: "chevron.down").font(.caption2.weight(.bold))
        }
        .font(.caption.weight(.semibold))
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .foregroundStyle(color == .secondary ? Color.primary : color)
        .background((color == .secondary ? Color.gray : color).opacity(0.14), in: Capsule())
    }
}
