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
        // النسخ التلقائي مفعّل افتراضياً (إلى مجلد iCloud بعد اختياره).
        UserDefaults.standard.register(defaults: [BackupManager.autoKey: true])
        BackupBackground.register()
        BackupBackground.schedule()
        AuditNotifications.registerDefaults()
    }

    var body: some Scene {
        WindowGroup {
            AppLockGate { RootView() }
                .environment(\.layoutDirection, language == AppLanguage.english.rawValue ? .leftToRight : .rightToLeft)
                .environment(\.locale, AppSettings.locale)
                .preferredColorScheme(AppAppearance(rawValue: appearance)?.colorScheme)
                // حجم النص والأيقونات داخل التطبيق (من الإعدادات ← العرض)، أو حجم الآيفون.
                .modifier(TextSizeModifier(index: textSize))
        }
        .modelContainer(SharedModel.container)
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
