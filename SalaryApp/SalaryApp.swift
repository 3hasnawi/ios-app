import SwiftUI
import SwiftData

@main
struct SalaryApp: App {
    @AppStorage(AppSettings.languageKey) private var language = AppLanguage.arabic.rawValue
    @AppStorage(AppSettings.appearanceKey) private var appearance = AppAppearance.system.rawValue
    @AppStorage(AppSettings.numeralsKey) private var numerals = AppNumerals.latin.rawValue

    init() {
        // قياس السلاسة يعمل في اختبار المحاكي فقط (-perfLog YES).
        PerfLog.startIfEnabled()
        // النسخ التلقائي مفعّل افتراضياً إلى مجلد التطبيق على الآيفون.
        UserDefaults.standard.register(defaults: [BackupManager.autoKey: true, BackupManager.destinationKey: BackupDestination.device.rawValue])
    }

    var body: some Scene {
        WindowGroup {
            AppLockGate { RootView() }
                .environment(\.layoutDirection, language == AppLanguage.english.rawValue ? .leftToRight : .rightToLeft)
                .environment(\.locale, AppSettings.locale)
                .preferredColorScheme(AppAppearance(rawValue: appearance)?.colorScheme)
        }
        .modelContainer(SharedModel.container)
    }
}
