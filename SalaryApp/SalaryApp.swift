import SwiftUI
import SwiftData

@main
struct SalaryApp: App {
    @Environment(\.scenePhase) private var scenePhase
    @AppStorage(AppSettings.languageKey) private var language = AppLanguage.arabic.rawValue
    @AppStorage(AppSettings.appearanceKey) private var appearance = AppAppearance.system.rawValue
    @AppStorage(AppSettings.numeralsKey) private var numerals = AppNumerals.latin.rawValue
    @AppStorage(AppSettings.textSizeKey) private var textSize = -1

    init() {
        // قياس السلاسة يعمل في اختبار المحاكي فقط (-perfLog YES).
        PerfLog.startIfEnabled()
        // النسخ من «الملفات» يدوي. تُلغى مهام النسخ المجدولة من الإصدارات السابقة.
        UserDefaults.standard.set(false, forKey: BackupServerConfig.enabledKey)
        BackupBackground.register()
        BackupBackground.schedule()
        AuditNotifications.registerDefaults()
    }

    var body: some Scene {
        WindowGroup {
            AppLockGate {
                DatabaseLaunchView()
                    .safeAreaInset(edge: .top, spacing: 0) {
                        if WorkspaceSync.shared.showingProgress, scenePhase == .active,
                           !AppLock.shared.isLocked, !AppLock.shared.pendingForegroundCheck {
                            WorkspaceSyncProgress(sync: WorkspaceSync.shared)
                        }
                    }
                    .background {
                        if WorkspaceSync.shared.transferring, scenePhase == .active,
                           !AppLock.shared.isLocked, !AppLock.shared.pendingForegroundCheck {
                            SyncTransferIndicator().frame(width: 0, height: 0).allowsHitTesting(false)
                        }
                    }
            }
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
