import Foundation

nonisolated enum ArabicText {
    /// يوحّد الكتابة العربية حتى يطابق البحث "احمد" مع "أحمد" و"فاطمة" مع "فاطمه".
    static func normalize(_ text: String) -> String {
        var out = String.UnicodeScalarView()
        for scalar in text.unicodeScalars {
            switch scalar.value {
            case 0x064B...0x065F, 0x0670, 0x0640: continue          // التشكيل والتطويل
            case 0x0622, 0x0623, 0x0625, 0x0671: out.append("ا")    // آ أ إ ٱ
            case 0x0629: out.append("ه")                            // ة
            case 0x0649, 0x0626: out.append("ي")                    // ى ئ
            case 0x0624: out.append("و")                            // ؤ
            default: out.append(scalar)
            }
        }
        return String(out)
            .lowercased()
            .split(whereSeparator: \.isWhitespace)
            .joined(separator: " ")
    }

    /// نص البحث كما يكتبه المستخدم أو يمليه بالصوت: الإملاء الصوتي يضيف نقطة أو فاصلة أو علامة استفهام
    /// ("أحمد علي.") فلا يطابق أي اسم. تُحذف علامات الترقيم والرموز وتُوحّد المسافات.
    static func cleanQuery(_ text: String) -> String {
        var scalars = String.UnicodeScalarView()
        for scalar in text.unicodeScalars {
            if CharacterSet.punctuationCharacters.contains(scalar) || CharacterSet.symbols.contains(scalar)
                || scalar.properties.generalCategory == .format {
                // "/" و"-" تبقيان (تظهر في أسماء الجهات والأقسام).
                if scalar == "/" || scalar == "-" { scalars.append(scalar) } else { scalars.append(" ") }
            } else {
                scalars.append(scalar)
            }
        }
        return String(scalars).split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    /// الأرقام الهندية (٠١٢) والفارسية ← لاتينية، لمطابقة البحث بالأرقام.
    static func latinDigits(_ text: String) -> String {
        String(String.UnicodeScalarView(text.unicodeScalars.map { s in
            switch s.value {
            case 0x0660...0x0669: Unicode.Scalar(s.value - 0x0660 + 48)!
            case 0x06F0...0x06F9: Unicode.Scalar(s.value - 0x06F0 + 48)!
            default: s
            }
        }))
    }

    static func matchKey(_ text: String) -> String {
        normalize(text).replacingOccurrences(of: " ", with: "")
    }

    /// يقرأ رقماً من نص الخلية، ويقبل الأرقام العربية (٠١٢) والفواصل.
    static func number(from text: String) -> Double? {
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return nil }
        if let value = Double(trimmed) { return value }

        var latin = ""
        for scalar in trimmed.unicodeScalars {
            switch scalar.value {
            case 0x0660...0x0669: latin.append(Character(UnicodeScalar(UInt8(scalar.value - 0x0660 + 48))))
            case 0x06F0...0x06F9: latin.append(Character(UnicodeScalar(UInt8(scalar.value - 0x06F0 + 48))))
            case 0x066B: latin.append(".")
            case 0x066C, 0x2C: continue                             // فواصل الآلاف
            default: latin.unicodeScalars.append(scalar)
            }
        }
        let cleaned = latin.filter { "0123456789.-".contains($0) }
        return cleaned.isEmpty ? nil : Double(cleaned)
    }
}

enum Money {
    static func format(_ value: Double?) -> String {
        guard let value else { return "—" }
        return value.formatted(.number.precision(.fractionLength(0...2)).locale(AppSettings.locale))
    }
}

extension String {
    var orDash: String { isEmpty ? "—" : self }
}
