import SwiftUI

/// إعدادات العرض: اللغة والمظهر والأرقام. تُحفظ في UserDefaults وتُقرأ من أي مكان.
nonisolated enum AppLanguage: String, CaseIterable, Identifiable {
    case arabic = "ar"
    case english = "en"
    var id: String { rawValue }
}

nonisolated enum AppAppearance: String, CaseIterable, Identifiable {
    case light, dark, system
    var id: String { rawValue }

    var colorScheme: ColorScheme? {
        switch self {
        case .light: .light
        case .dark: .dark
        case .system: nil
        }
    }
}

/// latin = 123 (الأرقام العربية)، arabicIndic = ١٢٣ (الأرقام الهندية).
nonisolated enum AppNumerals: String, CaseIterable, Identifiable {
    case latin, arabicIndic
    var id: String { rawValue }
}

nonisolated enum AppSettings {
    static let languageKey = "appLanguage"
    static let appearanceKey = "appAppearance"
    static let numeralsKey = "appNumerals"

    static var language: AppLanguage {
        AppLanguage(rawValue: UserDefaults.standard.string(forKey: languageKey) ?? "") ?? .arabic
    }

    static var numerals: AppNumerals {
        AppNumerals(rawValue: UserDefaults.standard.string(forKey: numeralsKey) ?? "") ?? .latin
    }

    static var isEnglish: Bool { language == .english }

    /// لغة الواجهة مع نظام الأرقام المختار، يُستخدم لتنسيق كل الأرقام.
    static var locale: Locale {
        let base = isEnglish ? "en_US" : "ar_IQ"
        let digits = numerals == .arabicIndic ? "arab" : "latn"
        return Locale(identifier: "\(base)@numbers=\(digits)")
    }
}

/// النص بالعربية أو الإنجليزية حسب لغة التطبيق.
nonisolated func tr(_ arabic: String, _ english: String) -> String {
    AppSettings.isEnglish ? english : arabic
}

/// تنسيق الأعداد حسب إعداد الأرقام (123 أو ١٢٣).
nonisolated enum Num {
    static func format(_ value: Int) -> String {
        value.formatted(.number.locale(AppSettings.locale))
    }

    /// الأرقام داخل نص (تواريخ وأرقام هويات) بحسب إعداد الأرقام، بدون فواصل.
    static func digits(_ text: String) -> String {
        guard AppSettings.numerals == .arabicIndic else { return text }
        return String(String.UnicodeScalarView(text.unicodeScalars.map { s in
            (48...57).contains(s.value) ? Unicode.Scalar(s.value - 48 + 0x0660)! : s
        }))
    }

    /// حجم بالبايت ← كيلوبايت أو ميغابايت أو غيغابايت حسب الحجم.
    static func bytes(_ count: Int64) -> String {
        let units = AppSettings.isEnglish
            ? ["bytes", "KB", "MB", "GB"]
            : ["بايت", "كيلوبايت", "ميغابايت", "غيغابايت"]
        var value = Double(count)
        var unit = 0
        while value >= 1024, unit < units.count - 1 {
            value /= 1024
            unit += 1
        }
        let digits = unit == 0 ? 0 : 1
        let number = value.formatted(.number.precision(.fractionLength(digits)).locale(AppSettings.locale))
        return "\(number) \(units[unit])"
    }
}
