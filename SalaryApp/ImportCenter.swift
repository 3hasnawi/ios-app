import SwiftUI
import SwiftData
import UniformTypeIdentifiers

/// نوع الملف المستورد.
nonisolated enum ImportKind: String, Sendable, CaseIterable, Identifiable {
    case payrollAndDeductions, payroll, deductions, staff, executionAccounts, skip
    var id: String { rawValue }

    var title: String {
        switch self {
        case .payrollAndDeductions: tr("رواتب واستقطاعات", "Payroll + deductions")
        case .payroll: tr("رواتب", "Payroll")
        case .deductions: tr("استقطاعات", "Deductions")
        case .staff: tr("ملاك", "Staff")
        case .executionAccounts: tr("حسابات جهات التنفيذ", "Execution accounts")
        case .skip: tr("تجاهل", "Skip")
        }
    }

    var systemImage: String {
        switch self {
        case .payrollAndDeductions: "doc.on.doc.fill"
        case .payroll: "banknote.fill"
        case .deductions: "minus.circle.fill"
        case .staff: "person.3.fill"
        case .executionAccounts: "building.columns.fill"
        case .skip: "xmark.circle"
        }
    }

    var color: Color {
        switch self {
        case .payrollAndDeductions: .indigo
        case .payroll: .blue
        case .deductions: .orange
        case .staff: .green
        case .executionAccounts: .teal
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
        if kind == .executionAccounts || (result.invalidAccounts > 0 && result.employees.isEmpty && result.deductions.isEmpty) {
            let valid = tr("\(Num.format(result.accounts.count)) حساب صالح", "\(result.accounts.count) valid accounts")
            let invalid = result.invalidAccounts > 0
                ? tr(" · \(Num.format(result.invalidAccounts)) IBAN غير صالح سيُتجاهل", " · \(result.invalidAccounts) invalid IBANs skipped") : ""
            return valid + invalid
        }
        var parts: [String] = []
        if kind != .deductions, !result.employees.isEmpty {
            parts.append(tr("\(Num.format(result.employees.count)) موظف", "\(Num.format(result.employees.count)) employees"))
        }
        if kind != .payroll, !result.deductions.isEmpty {
            parts.append(tr("\(Num.format(result.deductions.count)) استقطاع", "\(Num.format(result.deductions.count)) deductions"))
        }
        if !result.accounts.isEmpty {
            parts.append(tr("\(Num.format(result.accounts.count)) حساب جهة", "\(result.accounts.count) entity accounts"))
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
        .zip,
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

    func pick() { pick(types: Self.fileTypes, multiple: true) }

    /// إعادة استيراد الملاك من ملف Access فقط، حتى لا يُختار كشف رواتب بالخطأ.
    func pickStaff() { pick(types: accessFileTypes, multiple: false) }

    private func pick(types: [UTType], multiple: Bool) {
        guard !isBusy else {
            message = tr("يوجد استيراد قيد العمل، انتظر حتى ينتهي.", "An import is already running; wait for it to finish.")
            return
        }
        // تبدأ قراءة الملفات فور اختيارها، مع شريط التقدّم.
        FilePicker.present(types: types, multiple: multiple) { [weak self] urls in
            guard let self, !urls.isEmpty else { return }
            // ملف نسخة احتياطية (zip): استرجاع بعد التأكيد، مثلاً على جهاز آخر.
            if let backup = urls.first(where: { $0.pathExtension.lowercased() == "zip" }) {
                Task { await BackupManager.shared.openForRestore(backup) }
                return
            }
            Task { await self.analyze(urls) }
        }
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
        LaunchOptions.writeImportStage("analyze")
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
                        // قد يستدعي مزود الملفات نافذة الاختيار قبل اكتمال نسخته المؤقتة.
                        for attempt in 0..<3 {
                            do { return try StaffImporter.peekCount(at: url) }
                            catch AccessReader.ReadError.notAccess where attempt < 2 {
                                try await Task.sleep(for: .milliseconds(attempt == 0 ? 300 : 700))
                            }
                        }
                        return try StaffImporter.peekCount(at: url)
                    }.value
                    found.append(ImportCandidate(url: url, fileName: name, kind: .staff, month: "", allowedKinds: [.staff, .skip],
                                                 result: nil, staffCount: count, error: nil, monthDetected: true))
                } catch {
                    let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize
                    let detail = error.localizedDescription + (size.map {
                        tr(" حجم الملف: \(ByteCountFormatter.string(fromByteCount: Int64($0), countStyle: .file)).", " File size: \(ByteCountFormatter.string(fromByteCount: Int64($0), countStyle: .file)).")
                    } ?? "")
                    found.append(Self.failed(url, url.lastPathComponent, detail))
                }
                sink(1)
                continue
            }
            do {
                LaunchOptions.writeImportStage("read-xlsx")
                let result = try await Task.detached(priority: .userInitiated) {
                    let granted = url.startAccessingSecurityScopedResource()
                    defer { if granted { url.stopAccessingSecurityScopedResource() } }
                    return try ExcelImporter.load(url: url, progress: sink)
                }.value
                LaunchOptions.writeImportStage("read-complete")
                let hasPay = !result.employees.isEmpty
                let hasDed = !result.deductions.isEmpty
                let hasAccounts = !result.accounts.isEmpty
                let kind: ImportKind
                if hasAccounts && !hasPay && !hasDed { kind = .executionAccounts }
                else if result.invalidAccounts > 0 && !hasPay && !hasDed { kind = .skip }
                else if hasPay && hasDed { kind = .payrollAndDeductions }
                else { kind = hasPay ? .payroll : .deductions }
                let allowed: [ImportKind] = kind == .skip ? [.skip]
                    : hasPay && hasDed ? [.payrollAndDeductions, .payroll, .deductions, .skip] : [kind, .skip]
                let (month, detected) = Self.month(for: url, existing: existing)
                found.append(ImportCandidate(url: url, fileName: name, kind: kind, month: month, allowedKinds: allowed,
                                             result: result, staffCount: nil, error: nil, monthDetected: detected))
            } catch {
                found.append(Self.failed(url, url.lastPathComponent, error))
            }
        }
        progress.end()
        candidates = found
        isBusy = false
        if autoRun {
            if found.allSatisfy({ $0.kind == .skip }) {
                let details = found.map { candidate in
                    "\(candidate.fileName): \(candidate.error ?? candidate.result?.problems.joined(separator: "; ") ?? "no importable rows")"
                }
                LaunchOptions.writeReport("import=failed \(details.joined(separator: " | "))")
                self.autoRun = false
                return
            }
            await run()
            return
        }
        guard review else { return }
        await FilePicker.waitUntilSettled()
        let failed = found.filter { $0.error != nil }
        if !failed.isEmpty && failed.count == found.count {
            // لا يوجد ملف صالح: تنبيه بالسبب بدل نافذة مراجعة فارغة.
            message = ([tr("لم يُقبل أي ملف:", "No file was accepted:")] + failed.map { "\n\($0.fileName)\n\($0.error ?? "")" }).joined(separator: "\n")
            for c in found { Self.removeCopy(c.url) }
            candidates = []
        } else {
            showReview = true
        }
    }

    /// يحذف نسخة نافذة الملفات المؤقتة فقط، ولا يلمس الملف الأصلي (مثلاً ملف في مجلد التطبيق).
    nonisolated static func removeCopy(_ url: URL) {
        let path = url.resolvingSymlinksInPath().path
        let temp = FileManager.default.temporaryDirectory.resolvingSymlinksInPath().path
        guard path.hasPrefix(temp) || path.contains("-Inbox/") || path.contains("/Inbox/") else { return }
        try? FileManager.default.removeItem(at: url)
    }

    private static func failed(_ url: URL, _ name: String, _ error: Error) -> ImportCandidate {
        failed(url, name, error.localizedDescription)
    }

    private static func failed(_ url: URL, _ name: String, _ error: String) -> ImportCandidate {
        ImportCandidate(url: url, fileName: name, kind: .skip, month: "", allowedKinds: [.skip],
                        result: nil, staffCount: nil, error: error, monthDetected: false)
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
            case .executionAccounts:
                break // تُحدَّث الجهات الموجودة ولا تُحذف الجهات الغائبة من الملف.
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

    var reviewActionTitle: String {
        if importableCount == 1, let candidate = candidates.first(where: { $0.kind == .executionAccounts }),
           let result = candidate.result, result.invalidAccounts > 0 {
            return tr("استيراد \(Num.format(result.accounts.count)) حساباً صالحاً", "Import \(result.accounts.count) valid accounts")
        }
        return importableCount <= 1 ? tr("استيراد", "Import") : tr("استيراد الكل", "Import all")
    }

    // MARK: - الاستيراد

    func cancel() {
        for c in candidates { Self.removeCopy(c.url) }
        candidates = []
        showReview = false
    }

    func run() async {
        LaunchOptions.writeImportStage("run")
        guard !isBusy else { return }
        guard !BackupManager.shared.isWorking else {
            message = tr("انتظر انتهاء النسخ أو الاسترجاع قبل الاستيراد.", "Wait for backup or restore to finish before importing.")
            return
        }
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
        var accountTotals = (added: 0, updated: 0, invalid: 0)
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
                case .executionAccounts:
                    guard let result = job.result else { continue }
                    LaunchOptions.writeImportStage("save-accounts")
                    let counts = try ExecutionAccountStore.merge(result.accounts)
                    LaunchOptions.writeImportStage("saved-accounts")
                    accountTotals.added += counts.added
                    accountTotals.updated += counts.updated
                    accountTotals.invalid += result.invalidAccounts
                    lines.append(tr("حسابات جهات التنفيذ: \(Num.format(counts.added)) جديدة، \(Num.format(counts.updated)) محدّثة",
                                    "Execution accounts: \(counts.added) added, \(counts.updated) updated"))
                    if result.invalidAccounts > 0 {
                        warnings.append(Self.accountWarning(result))
                    }
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
                    if !result.accounts.isEmpty {
                        let counts = try ExecutionAccountStore.merge(result.accounts)
                        accountTotals.added += counts.added
                        accountTotals.updated += counts.updated
                        accountTotals.invalid += result.invalidAccounts
                        if result.invalidAccounts > 0 {
                            warnings.append(Self.accountWarning(result))
                        }
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
        if !fromTest { for c in candidates { Self.removeCopy(c.url) } }
        candidates = []
        progress.end()
        isBusy = false
        await BackupManager.shared.refreshIntegrityWarning()
        if let newestPayroll { lastPayrollMonth = newestPayroll.name }

        var parts: [String] = []
        if !lines.isEmpty { parts += [tr("تم الاستيراد:", "Imported:")] + lines }
        if !failures.isEmpty { parts += ["", tr("تعذّر:", "Failed:")] + failures }
        if !warnings.isEmpty { parts += [""] + warnings }
        // التدقيق التلقائي لأحدث شهر مستورد.
        if !fromTest, !lines.isEmpty, let auditMonth = newestPayroll?.name ?? months.sorted(by: { MonthName.sortKey($0) > MonthName.sortKey($1) }).first,
           let pair = AuditDigest.months(container: SharedModel.container, latest: auditMonth) {
            let audit = await AuditDigest.run(month: pair.month, previous: pair.previous, container: SharedModel.container)
            await AuditNotifications.post(audit)
            parts += ["", audit.issues == 0
                      ? tr("🔎 التدقيق (\(auditMonth)): لا ملاحظات.", "🔎 Audit (\(auditMonth)): no issues.")
                      : tr("🔎 التدقيق (\(auditMonth)): \(Num.format(audit.issues)) ملاحظات — راجعها في التقارير ← التدقيق.", "🔎 Audit (\(auditMonth)): \(audit.issues) issues — see Reports › Audit.")]
        }
        // نسخ احتياطي تلقائي بعد الاستيراد الناجح (إذا كان مفعّلاً).
        if !fromTest, !lines.isEmpty, let backupLine = await BackupManager.shared.autoBackup() {
            parts += ["", backupLine]
        }
        if autoRun {
            LaunchOptions.writeImportStage("complete")
            // اختبار المحاكي يقرأ هذا التقرير.
            let status = failures.isEmpty ? "" : " failed=\(failures.count)"
            LaunchOptions.writeReport("employees=\(totals.employees) deductions=\(totals.deductions) months=\(months.count) accountsAdded=\(accountTotals.added) accountsUpdated=\(accountTotals.updated) accountsInvalid=\(accountTotals.invalid)\(status)"
                + String(format: " seconds=%.1f\n", Date().timeIntervalSince(start)))
            autoRun = false
        } else {
            message = parts.joined(separator: "\n")
        }
    }

    private static func order(_ kind: ImportKind) -> Int {
        switch kind {
        case .staff: 0
        case .executionAccounts: 1
        case .payrollAndDeductions: 2
        case .payroll: 3
        case .deductions: 4
        case .skip: 5
        }
    }

    private static func accountWarning(_ result: ImportResult) -> String {
        let title = tr("⚠️ \(Num.format(result.invalidAccounts)) IBAN غير صالح لم يُحفظ:",
                       "⚠️ \(result.invalidAccounts) invalid IBANs weren't saved:")
        let details = result.invalidAccountDetails.map { item in
            "\(item.location): \(item.displayValue)\n\(item.reason.explanation) \(item.reason.solution)"
        }
        return ([title] + details).joined(separator: "\n")
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
                        Text(tr("راجع نوع الملف والبيانات قبل الاستيراد", "Check the file type and data before importing"))
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
                    Text(center.reviewActionTitle)
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
                if let invalid = item.result?.invalidAccountDetails, !invalid.isEmpty {
                    ForEach(invalid) { account in
                        VStack(alignment: .leading, spacing: 5) {
                            Text(account.location).font(.caption.weight(.bold))
                            Text(account.displayValue)
                                .font(.caption.monospaced())
                                .foregroundStyle(.red)
                                .textSelection(.enabled)
                                .environment(\.layoutDirection, .leftToRight)
                            Text(account.reason.explanation).font(.caption).foregroundStyle(.red)
                            Text(account.reason.solution).font(.caption).foregroundStyle(.primary)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(9)
                        .background(Color.red.opacity(0.09), in: RoundedRectangle(cornerRadius: 10))
                        .accessibilityIdentifier("invalidIBANRow")
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
