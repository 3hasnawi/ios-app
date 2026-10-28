import SwiftUI
import SwiftData
import WidgetKit

/// حساب الملخصات على سياق خلفي، مع إلغاء الطلب عند تغيّر البيانات أثناء التحديث.
@MainActor enum WidgetSnapshotPublisher {
    static func invalidate() {
        SalaryWidgetStore.clear()
        let defaults = UserDefaults.standard
        defaults.set(defaults.integer(forKey: "widgetRefreshRevision") + 1, forKey: "widgetRefreshRevision")
        WidgetCenter.shared.reloadTimelines(ofKind: "SalaryOverviewWidget")
    }

    static func updateBackupStatus(_ status: String) {
        guard var snapshot = SalaryWidgetStore.read(), snapshot.backupStatus != status else { return }
        snapshot.backupStatus = status
        if SalaryWidgetStore.write(snapshot) { WidgetCenter.shared.reloadTimelines(ofKind: "SalaryOverviewWidget") }
    }

    static func updateGoogleStatus() async {
        guard SalaryWidgetStore.read() != nil else { return }
        let status = await googleStatus()
        guard !Task.isCancelled, var snapshot = SalaryWidgetStore.read(), snapshot.googleDriveStatus != status else { return }
        snapshot.googleDriveStatus = status
        if SalaryWidgetStore.write(snapshot) { WidgetCenter.shared.reloadTimelines(ofKind: "SalaryOverviewWidget") }
    }

    private static func googleStatus() async -> String? {
        guard BackupServerConfig.ready, let base = BackupServerConfig.url(BackupServerConfig.address) else { return nil }
        var request = URLRequest(url: base.appendingPathComponent("v1/status"))
        request.setValue("Bearer " + BackupServerConfig.token, forHTTPHeaderField: "Authorization")
        request.timeoutInterval = 4
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200,
              let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let google = json["google_drive"] as? [String: Any], let status = google["status"] as? String else { return nil }
        switch status {
        case "disabled": return tr("غير مفعّل", "Disabled")
        case "not_connected": return tr("غير مرتبط", "Not connected")
        case "uploading": return tr("جارٍ الرفع", "Uploading")
        case "error": return tr("تعذّر الرفع", "Upload failed")
        case "connected":
            guard let timestamp = (google["last_success"] as? NSNumber)?.doubleValue else { return tr("مرتبط · لم يُرفع بعد", "Connected · no upload yet") }
            let date = Date(timeIntervalSince1970: timestamp).formatted(.dateTime.month(.twoDigits).day(.twoDigits).hour().minute())
            return tr("آخر رفع: \(date)", "Last upload: \(date)")
        default: return nil
        }
    }

    static func refresh(months: [PayrollMonth], manualData: Data, auditData: Data) async {
        guard FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: SalaryWidgetStore.group) != nil else { return }
        let names = months.map(\.name)
        func signature(_ month: PayrollMonth?) -> String {
            guard let month else { return "none" }
            return "\(month.name)|\(month.importedAt.timeIntervalSince1970)|\(month.employeeCount)|\(month.deductionCount)"
        }
        let cache = (try? JSONDecoder().decode([String: HomeAudit].self, from: auditData)) ?? [:]
        var audits: [String: HomeAudit] = [:]
        for (index, month) in months.enumerated() {
            let previous = index + 1 < months.count ? months[index + 1] : nil
            let prefix = "v3|\(signature(month));\(signature(previous));"
            if let item = cache.first(where: { $0.key.hasPrefix(prefix) })?.value, item.salaryAvailable {
                audits[month.name] = item
            }
        }
        let bookCounts = Dictionary(grouping: OfficialBookStore.shared.books, by: \.month).mapValues(\.count)
        let manual = (try? JSONDecoder().decode([String: BankManualAmounts].self, from: manualData)) ?? [:]
        let arabicDigits = AppSettings.numerals == .arabicIndic
        let isEnglish = AppSettings.isEnglish
        let oldSnapshots = SalaryWidgetStore.read()?.months ?? []
        let buildVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "unknown"
        var sourceKeys: [String: String] = [:]
        for (index, month) in months.enumerated() {
            let previous = index + 1 < months.count ? months[index + 1] : nil
            let amounts = manual[month.name] ?? BankManualAmounts()
            let audit = audits[month.name]
            let auditValues: [Int?] = [audit?.issues.count, audit?.compared == true ? audit?.added : nil,
                                  audit?.compared == true ? audit?.removed : nil, audit?.compared == true ? audit?.net : nil,
                                  audit?.hasDeductions == true ? audit?.stopped : nil, audit?.hasDeductions == true ? audit?.started : nil,
                                  audit?.hasDeductions == true ? audit?.changed : nil, audit?.compared == true ? audit?.bank : nil,
                                  audit?.compared == true ? audit?.rank : nil, audit?.compared == true ? audit?.workplace : nil]
            let auditSignature = auditValues.map { value in value.map { String($0) } ?? "-" }.joined(separator: ",")
            sourceKeys[month.name] = "v1|\(buildVersion)|\(signature(month));\(signature(previous));\(amounts.accumulatedLeave);\(amounts.employeeRewards);\(amounts.singleList);\(bookCounts[month.name] ?? 0);\(auditSignature)"
        }
        let container = SharedModel.container
        let frozenKeys = sourceKeys
        let frozenAudits = audits
        let work = Task.detached(priority: .utility) { () throws -> [SalaryWidgetMonth] in
            let engine = ReportEngine(modelContainer: container)
            let bankEngine = BankStatementEngine(modelContainer: container)
            var summaries: [SalaryWidgetMonth] = []
            for (index, name) in names.enumerated() {
                try Task.checkCancellation()
                let previous = index + 1 < names.count ? names[index + 1] : nil
                let key = frozenKeys[name] ?? ""
                if let cached = oldSnapshots.first(where: { $0.name == name && $0.sourceKey == key }) {
                    summaries.append(cached)
                    continue
                }
                let overview = try await engine.homeOverview(month: name, previous: previous, trendMonths: [])
                let bank = try await bankEngine.summary(month: name)
                let groups = try await WidgetEntityEngine(modelContainer: container).entities(month: name)
                try Task.checkCancellation()
                let previousNet = overview.previousNetTotal
                let change = previousNet.flatMap { $0 > 0 ? (overview.netTotal - $0) / $0 * 100 : nil }
                func category(_ i: Int) -> Int { overview.categories.indices.contains(i) ? overview.categories[i].count : 0 }
                summaries.append(SalaryWidgetMonth(name: name, sourceKey: key, net: overview.netTotal, employees: overview.employees,
                    deductionAmount: overview.entityTotal, deductionCount: bank.executionCount,
                    bankTotal: bank.bankTotal + (manual[name]?.total ?? 0), bankMatched: bank.executionMatched,
                    cash: bank.cashNet, card: bank.cardNet, unclassified: bank.unclassifiedNet,
                    bankDeductions: bank.bankDeductions, executionDifference: bank.executionDifference,
                    leave: manual[name]?.accumulatedLeave ?? 0, rewards: manual[name]?.employeeRewards ?? 0,
                    singleList: manual[name]?.singleList ?? 0, healthcare: bank.breakdown["الرعاية الصحية"] ?? 0,
                    martyrs: bank.breakdown["صندوق شهداء الشرطة"] ?? 0, sport: bank.breakdown["النشاط الرياضي"] ?? 0,
                    tax: bank.breakdown["الضريبة"] ?? 0, protection: bank.breakdown["الحماية الاجتماعية"] ?? 0,
                    mobile: bank.breakdown["شركة عراق الجوال"] ?? 0, pension: bank.contractContribution,
                    bookCount: bookCounts[name] ?? 0, officers: category(0), civil: category(1),
                    ranks: category(2), contracts: category(3), changePercent: change,
                    auditCount: frozenAudits[name]?.issues.count, newEmployees: frozenAudits[name]?.compared == true ? frozenAudits[name]?.added : nil,
                    removedEmployees: frozenAudits[name]?.compared == true ? frozenAudits[name]?.removed : nil,
                    changedSalaries: frozenAudits[name]?.compared == true ? frozenAudits[name]?.net : nil,
                    stoppedDeductions: frozenAudits[name]?.hasDeductions == true ? frozenAudits[name]?.stopped : nil,
                    startedDeductions: frozenAudits[name]?.hasDeductions == true ? frozenAudits[name]?.started : nil,
                    changedDeductions: frozenAudits[name]?.hasDeductions == true ? frozenAudits[name]?.changed : nil,
                    changedBanks: frozenAudits[name]?.compared == true ? frozenAudits[name]?.bank : nil,
                    changedRanks: frozenAudits[name]?.compared == true ? frozenAudits[name]?.rank : nil,
                    changedWorkplaces: frozenAudits[name]?.compared == true ? frozenAudits[name]?.workplace : nil,
                    entities: groups))
            }
            return summaries
        }
        do {
            let records = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
            try Task.checkCancellation()
            let cloud = await googleStatus()
            try Task.checkCancellation()
            if SalaryWidgetStore.write(SalaryWidgetSnapshot(updatedAt: .now, months: records, backupStatus: BackupManager.shared.statusLine, googleDriveStatus: cloud, arabicDigits: arabicDigits, isEnglish: isEnglish)) {
                WidgetCenter.shared.reloadTimelines(ofKind: "SalaryOverviewWidget")
            }
        } catch { /* تبقى آخر قراءة مؤرخة؛ لا تُستبدل ببيانات ناقصة عند الإلغاء. */ }
    }
}

@ModelActor
actor WidgetEntityEngine {
    func entities(month: String) throws -> [SalaryWidgetMonth.Entity] {
        var descriptor = FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })
        descriptor.propertiesToFetch = [\.title, \.amount, \.matchKey]
        var grouped: [String: (amount: Double, keys: Set<String>, rows: Int)] = [:]
        for (index, item) in try modelContext.fetch(descriptor).enumerated() {
            if index.isMultiple(of: 512) { try Task.checkCancellation() }
            var record = grouped[item.title] ?? (0, [], 0)
            record.amount += item.amount ?? 0
            record.keys.insert(item.matchKey)
            record.rows += 1
            grouped[item.title] = record
        }
        return grouped.map { .init(title: $0.key, amount: $0.value.amount, employees: $0.value.keys.count, rows: $0.value.rows) }
            .sorted { $0.title < $1.title }
    }
}

#if targetEnvironment(simulator)
/// يتحقق من مطابقة بيانات الويدجت مع التقرير وكشف البنك باستخدام بيانات الاستيراد الوهمية.
enum WidgetSnapshotSelfTest {
    @MainActor static func runIfRequested() async {
        guard UserDefaults.standard.bool(forKey: "widgetDataTest") else { return }
        do {
            let descriptor = FetchDescriptor<PayrollMonth>(sortBy: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse)])
            guard let month = try SharedModel.container.mainContext.fetch(descriptor).first else { throw ShortcutError.recordMissing }
            let name = month.name
            let container = SharedModel.container
            let overview = try await ReportEngine(modelContainer: container).homeOverview(month: name, previous: nil, trendMonths: [])
            let bank = try await BankStatementEngine(modelContainer: container).summary(month: name)
            let entities = try await WidgetEntityEngine(modelContainer: container).entities(month: name)
            let groups = try await ReportEngine(modelContainer: container).deductionGroups(month: name, byEntity: false)
            guard let group = groups.first(where: { $0.title.contains("تنفيذ الخالدية") }),
                  let widgetEntity = entities.first(where: { $0.title == group.title }) else { throw ShortcutError.recordMissing }
            let rows = try await ReportEngine(modelContainer: container).rows(for: group)
            guard overview.employees > 0, overview.categories.reduce(0, { $0 + $1.count }) == overview.employees,
                  abs(overview.netTotal - bank.netSalary) < 0.01, widgetEntity.rows == rows.count,
                  widgetEntity.employees == group.count, abs(widgetEntity.amount - group.total) < 0.01 else { throw ShortcutError.recordMissing }
            LaunchOptions.writeReport("widgetData=ok groupAccess=\(SalaryWidgetStore.available ? "available" : "unavailable")")
        } catch { LaunchOptions.writeReport("widgetData=failed \(error.localizedDescription)") }
    }
}
#endif
