import SwiftUI
import UserNotifications

/// إشعارات التدقيق: تُصدر من الجهاز نفسه بعد كل استيراد (بلا خادم ولا حساب مطوّر).
@MainActor
enum AuditNotifications {
    static let enabledKey = "notifyAudit"
    static let errorsKey = "notifyErrors"
    static let bankKey = "notifyBank"
    static let salaryKey = "notifySalary"
    static let deductionsKey = "notifyDeductions"
    static let reminderKey = "notifyReminder"
    static let reminderDayKey = "notifyReminderDay"
    static let reminderHourKey = "notifyReminderHour"
    static let reminderID = "import-reminder"

    static func registerDefaults() {
        UserDefaults.standard.register(defaults: [
            enabledKey: false, errorsKey: true, bankKey: true, salaryKey: true, deductionsKey: true,
            reminderKey: false, reminderDayKey: 20, reminderHourKey: 9,
        ])
        UNUserNotificationCenter.current().delegate = NotificationDelegate.shared
    }

    /// يطلب الإذن من النظام؛ false إذا رفضه المستخدم سابقاً.
    static func requestAuthorization() async -> Bool {
        let center = UNUserNotificationCenter.current()
        let settings = await center.notificationSettings()
        switch settings.authorizationStatus {
        case .authorized, .provisional, .ephemeral: return true
        case .denied: return false
        default: return (try? await center.requestAuthorization(options: [.alert, .sound, .badge])) ?? false
        }
    }

    private static func allowed() async -> Bool {
        guard UserDefaults.standard.bool(forKey: enabledKey) else { return false }
        let status = await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
        return status == .authorized || status == .provisional || status == .ephemeral
    }

    private struct Note {
        let id: String
        let title: String
        let body: String
        let route: String
    }

    /// إشعارات خلاصة التدقيق بعد الاستيراد.
    static func post(_ d: AuditDigest) async {
        guard await allowed() else { return }
        let defaults = UserDefaults.standard
        var notes: [Note] = []
        func signed(_ v: Double) -> String { (v > 0 ? "+" : "") + Money.format(v) }

        if defaults.bool(forKey: errorsKey), d.issues > 0 {
            let failing = ((d.salary?.checks ?? []) + (d.salary?.staffChecks ?? []) + d.deductionChecks).filter { !$0.ok }
            let body = failing.prefix(4).map { $0.rows.isEmpty ? $0.title : "\($0.title) \(Num.format($0.rows.count))" }.joined(separator: " · ")
            notes.append(Note(id: "audit-errors", title: tr("⚠︎ تدقيق \(d.month): \(Num.format(d.issues)) ملاحظات", "⚠︎ \(d.month) audit: \(d.issues) issues"),
                              body: body, route: "audit"))
        }
        if let s = d.salary, s.compared {
            let bank = s.rows(.bank)
            if defaults.bool(forKey: bankKey), !bank.isEmpty {
                let names = bank.prefix(3).map(\.name).joined(separator: "، ") + (bank.count > 3 ? tr(" وآخرون", " and others") : "")
                notes.append(Note(id: "audit-bank", title: tr("تغيّر الحساب المصرفي لـ \(Num.format(bank.count)) موظف", "Bank account changed for \(bank.count)"),
                                  body: names, route: "salary"))
            }
            if defaults.bool(forKey: salaryKey) {
                notes.append(Note(id: "audit-salary",
                                  title: tr("متغيرات رواتب \(d.month)", "\(d.month) payroll changes"),
                                  body: tr("مقارنة بـ\(d.previous): جدد \(Num.format(s.rows(.added).count)) · خارجون \(Num.format(s.rows(.removed).count)) · تغيّر الصافي \(Num.format(s.rows(.net).count)) · الفرق \(signed(s.netTo - s.netFrom))",
                                           "vs \(d.previous): new \(s.rows(.added).count) · removed \(s.rows(.removed).count) · net changed \(s.rows(.net).count) · \(signed(s.netTo - s.netFrom))"),
                                  route: "salary"))
            }
        }
        if defaults.bool(forKey: deductionsKey), let c = d.deductions, c.emptyMonths.isEmpty {
            notes.append(Note(id: "audit-deductions",
                              title: tr("الاستقطاعات: توقف \(Num.format(c.rows(.stopped).count)) · جديد \(Num.format(c.rows(.started).count)) · تغيّر \(Num.format(c.rows(.changed).count))",
                                        "Deductions: stopped \(c.rows(.stopped).count) · new \(c.rows(.started).count) · changed \(c.rows(.changed).count)"),
                              body: tr("الفرق بين \(d.previous) و\(d.month): \(signed(c.after - c.before)) · اضغط للتفاصيل",
                                       "\(d.previous) → \(d.month): \(signed(c.after - c.before))"),
                              route: "deductions"))
        }

        let center = UNUserNotificationCenter.current()
        for note in notes {
            let content = UNMutableNotificationContent()
            content.title = note.title
            content.body = note.body
            content.sound = .default
            content.threadIdentifier = "audit"
            content.userInfo = ["route": note.route]
            // نفس المعرّف يستبدل إشعار الاستيراد السابق بدل تكديسه.
            try? await center.add(UNNotificationRequest(identifier: note.id, content: content, trigger: nil))
        }
    }

    /// تذكير شهري باستيراد الرواتب في اليوم والساعة المختارين.
    static func updateReminder() async {
        let center = UNUserNotificationCenter.current()
        center.removePendingNotificationRequests(withIdentifiers: [reminderID])
        let defaults = UserDefaults.standard
        guard defaults.bool(forKey: reminderKey), await allowed() else { return }
        let content = UNMutableNotificationContent()
        content.title = tr("تذكير: رواتب الشهر الجديد", "Reminder: new month's payroll")
        content.body = tr("استورد ملفي الرواتب والاستقطاعات ليُدقَّق الشهر ويُقارن بالذي قبله.", "Import the payroll and deduction files to audit the month.")
        content.sound = .default
        content.userInfo = ["route": "import"]
        let trigger = UNCalendarNotificationTrigger(
            dateMatching: DateComponents(day: defaults.integer(forKey: reminderDayKey), hour: defaults.integer(forKey: reminderHourKey)),
            repeats: true)
        try? await center.add(UNNotificationRequest(identifier: reminderID, content: content, trigger: trigger))
    }

    fileprivate static func open(route: String) {
        let router = AppRouter.shared
        switch route {
        case "import":
            router.tab = .settings
        case "salary":
            router.tab = .payroll
            router.pendingReport = .salaryAudit
            router.showReports = true
        case "deductions":
            router.tab = .payroll
            router.pendingReport = .deductionComparison
            router.showReports = true
        default:
            router.tab = .payroll
            router.pendingReport = .audit
            router.showReports = true
        }
    }
}

/// يعرض الإشعار حتى والتطبيق مفتوح، ويفتح صفحة التدقيق عند الضغط عليه.
final class NotificationDelegate: NSObject, UNUserNotificationCenterDelegate, @unchecked Sendable {
    static let shared = NotificationDelegate()

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let route = response.notification.request.content.userInfo["route"] as? String ?? "audit"
        await MainActor.run { AuditNotifications.open(route: route) }
    }
}

// MARK: - صفحة الإعدادات

struct NotificationSettingsView: View {
    @AppStorage(AuditNotifications.enabledKey) private var enabled = false
    @AppStorage(AuditNotifications.errorsKey) private var errors = true
    @AppStorage(AuditNotifications.bankKey) private var bank = true
    @AppStorage(AuditNotifications.salaryKey) private var salary = true
    @AppStorage(AuditNotifications.deductionsKey) private var deductions = true
    @AppStorage(AuditNotifications.reminderKey) private var reminder = false
    @AppStorage(AuditNotifications.reminderDayKey) private var day = 20
    @AppStorage(AuditNotifications.reminderHourKey) private var hour = 9
    @State private var denied = false

    var body: some View {
        Form {
            Section {
                Toggle(isOn: $enabled) {
                    SettingRow(title: tr("إشعارات التدقيق", "Audit notifications"), systemImage: "bell.badge.fill", color: .red,
                               subtitle: tr("بعد كل استيراد لشهر جديد", "After each import")) { EmptyView() }
                }
                .accessibilityIdentifier("notifyToggle")
            } footer: {
                if denied {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(tr("الإشعارات مرفوضة في إعدادات الآيفون لهذا التطبيق.", "Notifications are turned off for this app in iPhone Settings."))
                            .foregroundStyle(.red)
                        Button(tr("فتح إعدادات الآيفون", "Open iPhone Settings")) {
                            if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
                        }
                    }
                }
            }

            Section(tr("ماذا يصلك", "What you get")) {
                Toggle(isOn: $errors) {
                    SettingRow(title: tr("أخطاء التدقيق", "Audit issues"), systemImage: "exclamationmark", color: .orange,
                               subtitle: tr("حساب · IBAN · تكرار · الملاك", "Calculation · IBAN · duplicates · staff")) { EmptyView() }
                }
                Toggle(isOn: $bank) {
                    SettingRow(title: tr("تغيّر الحساب المصرفي", "Bank account changes"), systemImage: "arrow.left.arrow.right", color: .blue,
                               subtitle: tr("تنبيه منفصل لأهميته", "A separate alert")) { EmptyView() }
                }
                Toggle(isOn: $salary) {
                    SettingRow(title: tr("متغيرات الرواتب", "Payroll changes"), systemImage: "banknote.fill", color: .green,
                               subtitle: tr("جدد · خارجون · فرق الصافي", "New · removed · net difference")) { EmptyView() }
                }
                Toggle(isOn: $deductions) {
                    SettingRow(title: tr("متغيرات الاستقطاعات", "Deduction changes"), systemImage: "minus.circle.fill", color: .purple,
                               subtitle: tr("توقف · جديد · تغيّر المبلغ", "Stopped · new · changed")) { EmptyView() }
                }
            }
            .disabled(!enabled)

            Section {
                Toggle(isOn: $reminder) {
                    SettingRow(title: tr("تذكير باستيراد الرواتب", "Import reminder"), systemImage: "calendar.badge.clock", color: .indigo,
                               subtitle: tr("كل شهر في اليوم المختار", "Monthly on the chosen day")) { EmptyView() }
                }
                Picker(selection: $day) {
                    ForEach(1...28, id: \.self) { Text(tr("يوم \(Num.format($0))", "Day \($0)")).tag($0) }
                } label: {
                    SettingRow(title: tr("يوم التذكير", "Reminder day"), systemImage: "calendar", color: .gray) { EmptyView() }
                }
                .disabled(!reminder)
                Picker(selection: $hour) {
                    ForEach([8, 9, 10, 11, 12, 13, 14, 18, 20], id: \.self) { h in
                        Text(hourText(h)).tag(h)
                    }
                } label: {
                    SettingRow(title: tr("الساعة", "Time"), systemImage: "clock", color: .gray) { EmptyView() }
                }
                .disabled(!reminder)
            } header: {
                Text(tr("تذكير شهري", "Monthly reminder"))
            }
            .disabled(!enabled)

            Section {
                Button(tr("إرسال إشعار تجريبي", "Send a test notification"), systemImage: "paperplane") { sendTest() }
                    .disabled(!enabled)
            } footer: {
                Text(tr("الإشعارات تُصدر من التطبيق نفسه على جهازك (بلا إنترنت ولا خادم)، وتصلك فور انتهاء الاستيراد والتدقيق.",
                        "Notifications are created on your device after each import and audit."))
            }
        }
        .navigationTitle(tr("الإشعارات", "Notifications"))
        .navigationBarTitleDisplayMode(.inline)
        .onChange(of: enabled) { _, on in
            guard on else { Task { await AuditNotifications.updateReminder() }; return }
            Task {
                let ok = await AuditNotifications.requestAuthorization()
                denied = !ok
                if !ok { enabled = false }
                await AuditNotifications.updateReminder()
            }
        }
        .onChange(of: reminder) { Task { await AuditNotifications.updateReminder() } }
        .onChange(of: day) { Task { await AuditNotifications.updateReminder() } }
        .onChange(of: hour) { Task { await AuditNotifications.updateReminder() } }
    }

    private func hourText(_ h: Int) -> String {
        var c = DateComponents()
        c.hour = h
        c.minute = 0
        let date = Calendar.current.date(from: c) ?? .now
        return date.formatted(.dateTime.hour().minute().locale(AppSettings.locale))
    }

    private func sendTest() {
        Task {
            let content = UNMutableNotificationContent()
            content.title = tr("تطبيق الرواتب", "Payroll")
            content.body = tr("الإشعارات تعمل. ستصلك خلاصة التدقيق بعد كل استيراد.", "Notifications work. You'll get the audit summary after each import.")
            content.sound = .default
            content.userInfo = ["route": "audit"]
            try? await UNUserNotificationCenter.current().add(
                UNNotificationRequest(identifier: "audit-test", content: content,
                                      trigger: UNTimeIntervalNotificationTrigger(timeInterval: 2, repeats: false)))
        }
    }
}
