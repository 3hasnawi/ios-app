import Foundation

/// تفقيط الدينار لمبالغ التحويل الصحيحة، حتى تراجع مطابقة الرقم والنص قبل التصدير.
nonisolated enum ArabicAmountWords {
    enum Failure: LocalizedError {
        case outOfRange
        var errorDescription: String? { tr("المبلغ خارج النطاق المدعوم للتفقيط.", "Amount is outside the supported range.") }
    }
    private static let units = ["", "واحد", "اثنان", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية", "تسعة"]
    private static let tens = ["", "", "عشرون", "ثلاثون", "أربعون", "خمسون", "ستون", "سبعون", "ثمانون", "تسعون"]
    private static let hundreds = ["", "مائة", "مائتان", "ثلاثمائة", "أربعمائة", "خمسمائة", "ستمائة", "سبعمائة", "ثمانمائة", "تسعمائة"]
    private static let scales: [(single: String, dual: String, plural: String)] = [
        ("", "", ""), ("ألف", "ألفان", "آلاف"), ("مليون", "مليونان", "ملايين"),
        ("مليار", "ملياران", "مليارات"), ("تريليون", "تريليونان", "تريليونات")
    ]
    private static func underThousand(_ n: Int) -> String {
        var parts: [String] = []
        let h = n / 100
        if h > 0 { parts.append(hundreds[h]) }
        let r = n % 100
        if r > 0 {
            if r < 10 { parts.append(units[r]) }
            else if r == 10 { parts.append("عشرة") }
            else if r == 11 { parts.append("أحد عشر") }
            else if r == 12 { parts.append("اثنا عشر") }
            else if r < 20 { parts.append(units[r - 10] + " عشر") }
            else if r % 10 == 0 { parts.append(tens[r / 10]) }
            else { parts.append(units[r % 10] + " و" + tens[r / 10]) }
        }
        return parts.joined(separator: " و")
    }
    static func dinars(_ amount: Int64) throws -> String {
        guard amount >= 0, amount < 1_000_000_000_000_000 else { throw Failure.outOfRange }
        if amount == 0 { return "صفر دينار" }
        var value = amount
        var chunks: [String] = []
        var scale = 0
        var unitEmbedded = false
        let lastTwo = Int(amount % 100)
        while value > 0 {
            let n = Int(value % 1000)
            if n > 0 {
                if scale == 0 && (lastTwo == 1 || lastTwo == 2) {
                    let unit = lastTwo == 1 ? "دينار واحد" : "ديناران"
                    let h = n / 100
                    chunks.append(h > 0 ? hundreds[h] + " و" + unit : unit)
                    unitEmbedded = true
                }
                else if scale == 0 { chunks.append(underThousand(n)) }
                else {
                    let unit = scales[scale]
                    if n == 1 { chunks.append(unit.single) }
                    else if n == 2 { chunks.append(unit.dual) }
                    else if n <= 10 { chunks.append(underThousand(n) + " " + unit.plural) }
                    else { chunks.append(underThousand(n) + " " + unit.single) }
                }
            }
            value /= 1000
            scale += 1
        }
        let result = chunks.reversed().joined(separator: " و")
        if unitEmbedded { return result }
        return result + ((3...10).contains(lastTwo) ? " دنانير" : " دينار")
    }
}
