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
        return value.formatted(.number.precision(.fractionLength(0...2)))
    }
}

extension String {
    var orDash: String { isEmpty ? "—" : self }
}
