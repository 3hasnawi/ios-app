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

    /// ترتيب الأسماء العربية بعد توحيد الهمزات والتشكيل، مع كسر التعادل دون دمج السجلات.
    static func alphabeticalOrder(_ lhs: String, _ rhs: String) -> ComparisonResult {
        let left = normalize(lhs), right = normalize(rhs)
        let order = left.compare(right, options: [.numeric, .caseInsensitive], locale: Locale(identifier: "ar_IQ"))
        if order != .orderedSame { return order }
        if lhs == rhs { return .orderedSame }
        return lhs < rhs ? .orderedAscending : .orderedDescending
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
        var value = latinDigits(text).trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "٬", with: ",").replacingOccurrences(of: "٫", with: ".")
        for currency in ["د.ع", "د.ع.", "IQD"] where value.hasSuffix(currency) {
            value = String(value.dropLast(currency.count)).trimmingCharacters(in: .whitespaces)
        }
        // لا نحذف رموزاً مجهولة: 3500/7000 ليس مبلغاً واحداً.
        let pattern = #"^[+-]?(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$"#
        guard value.range(of: pattern, options: .regularExpression) != nil,
              let amount = Double(value.replacingOccurrences(of: ",", with: "")), amount.isFinite else { return nil }
        return amount

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
