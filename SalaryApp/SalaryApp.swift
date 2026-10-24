import SwiftUI
import SwiftData

@main
struct SalaryApp: App {
    @AppStorage(AppSettings.languageKey) private var language = AppLanguage.arabic.rawValue
    @AppStorage(AppSettings.appearanceKey) private var appearance = AppAppearance.system.rawValue
    @AppStorage(AppSettings.numeralsKey) private var numerals = AppNumerals.latin.rawValue

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(\.layoutDirection, language == AppLanguage.english.rawValue ? .leftToRight : .rightToLeft)
                .environment(\.locale, AppSettings.locale)
                .preferredColorScheme(AppAppearance(rawValue: appearance)?.colorScheme)
        }
        .modelContainer(SharedModel.container)
    }
}
