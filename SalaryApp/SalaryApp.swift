import SwiftUI
import SwiftData

@main
struct SalaryApp: App {
    @AppStorage(AppSettings.languageKey) private var language = AppLanguage.arabic.rawValue
    @AppStorage(AppSettings.appearanceKey) private var appearance = AppAppearance.system.rawValue
    @AppStorage(AppSettings.numeralsKey) private var numerals = AppNumerals.latin.rawValue
    @AppStorage(AppSettings.textSizeKey) private var textSize = -1

    init() {
        // قياس السلاسة يعمل في اختبار المحاكي فقط (-perfLog YES).
        PerfLog.startIfEnabled()
        // العمل الحالي يعتمد الملفات وiCloud فقط؛ لا نمس نسخة السيرفر السابقة أو بيانات الاتصال.
        UserDefaults.standard.set(false, forKey: BackupServerConfig.enabledKey)
        if UserDefaults.standard.string(forKey: "backupDestinationChoice") == "server" {
            UserDefaults.standard.set("iCloud Drive", forKey: "backupDestinationChoice")
        }
        // النسخ التلقائي مفعّل افتراضياً (إلى مجلد iCloud بعد اختياره).
        UserDefaults.standard.register(defaults: [BackupManager.autoKey: true, BackupServerConfig.notifyKey: true])
        BackupBackground.register()
        BackupBackground.schedule()
        AuditNotifications.registerDefaults()
    }

    var body: some Scene {
        WindowGroup {
            AppLockGate { DatabaseLaunchView() }
                .environment(\.layoutDirection, language == AppLanguage.english.rawValue ? .leftToRight : .rightToLeft)
                .environment(\.locale, AppSettings.locale)
                .preferredColorScheme(AppAppearance(rawValue: appearance)?.colorScheme)
                // حجم النص والأيقونات داخل التطبيق (من الإعدادات ← العرض)، أو حجم الآيفون.
                .modifier(TextSizeModifier(index: textSize))
        }
    }
}

/// يطبّق حجم النص المختار على كل الشاشات والنوافذ، أو يترك حجم الآيفون.
struct TextSizeModifier: ViewModifier {
    let index: Int
    func body(content: Content) -> some View {
        if let size = AppSettings.textSize(index) {
            content.dynamicTypeSize(size)
        } else {
            content
        }
    }
}
