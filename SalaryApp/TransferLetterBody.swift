import Foundation

/// يحرر النص مع الحفاظ على الحقول المحاسبية المرتبطة ببيانات الشهر.
nonisolated enum TransferLetterBody {
    enum Failure: LocalizedError {
        case invalid
        var errorDescription: String? {
            tr("راجع متن الكتاب: احتفظ بكل الحقول بين الأقواس كما هي، مرة واحدة لكل حقل.", "Review the letter body: keep each placeholder exactly once.")
        }
    }
    static func template(for id: String) -> String? {
        let start = "نرجو تحويل مبلغ قدره ({{المبلغ}}) ({{المبلغ كتابة}}) لا غيرها من حسابنا المرقم ({{حسابنا}}) المفتوح لديكم "
        let end = " لشهر ({{الشهر}} / {{السنة}}) وتزويدنا بالإشعار اللازم بذلك."
        switch id {
        case "rafidain_group", "other_banks_group":
            return start + "إلى الحسابات المدرجة تفاصيلها في القائمة المرفقة مع قرص بصيغة (CSV) عن استقطاعات شهر ({{الشهر}} / {{السنة}}) وتزويدنا بالإشعار اللازم بذلك."
        case "food_company", "food_tax", "tax_social", "contracts", "cash_salary":
            let reason: String
            switch id {
            case "food_company": reason = "عن مستحقات الشركة عقد تجهيز الأرزاق الجافة والطرية المرقم ({{عقد الإطعام}})"
            case "food_tax": reason = "وذلك عن أمانات ضريبة عقد تجهيز الأرزاق الجافة والطرية المرقم ({{عقد الإطعام}})"
            case "tax_social": reason = "وذلك عن مبالغ رسوم الضريبة والحماية الاجتماعية لرواتب قيادة شرطة الأنبار المبينة تفاصيلها أدناه"
            case "contracts": reason = "وذلك عن المساهمات التقاعدية بنسبة ١٧٪ الخاصة بالمتعاقدين بصفة رجل أمن ضمن تشكيلات قيادتنا حسب الجدول أدناه"
            default: reason = "وذلك عن أمانات رواتب منتسبي قيادتنا المبينة تفاصيلها بالقائمة المرفقة"
            }
            return start + "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) (مصرف الرافدين / فرع الأنبار) " + reason + end
        case "ramadi", "housing", "water":
            let target = "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) والمفتوح لدى ({{المصرف}}) "
            let reason: String
            switch id {
            case "ramadi": reason = "وذلك عن المبالغ المستقطعة والمحسومة من منتسبي قيادة شرطة الأنبار وحسب القوائم المرفقة طياً"
            case "housing": reason = "وذلك عن مبالغ السلف والقروض المستقطعة والمحسومة من منتسبي قيادة شرطة الأنبار المدرجة أسماؤهم في القائمة المرفقة طياً"
            default: reason = "وذلك عن مبالغ استقطاع منسوبنا {{الرتبة}} ({{الاسم}})"
            }
            return start + target + reason + end
        case "rafidain_anbar":
            return start + "إلى حساب مصرفكم (مصرف الرافدين / فرع الأنبار) وذلك عن مبالغ السلف والقروض المستقطعة والمحسومة من منتسبينا المدرجة أسماؤهم في القائمة المرفقة" + end
        default: return nil
        }
    }
    private static func tokens(_ text: String) -> [String] {
        let pattern = try! NSRegularExpression(pattern: "\\{\\{[^{}]+\\}\\}")
        let range = NSRange(text.startIndex..., in: text)
        return pattern.matches(in: text, range: range).compactMap { Range($0.range, in: text).map { String(text[$0]) } }
    }
    static func validate(_ text: String, for id: String) throws {
        guard let original = template(for: id), text.count <= 4000,
              !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !text.unicodeScalars.contains(where: { $0.value < 32 && $0.value != 10 }),
              tokens(text).sorted() == tokens(original).sorted() else { throw Failure.invalid }
        var stripped = text
        for token in tokens(text) { stripped = stripped.replacingOccurrences(of: token, with: "") }
        guard !stripped.contains("{{"), !stripped.contains("}}") else { throw Failure.invalid }
    }
    static func render(id: String, custom: String?, values: [String: String]) throws -> String {
        guard let original = template(for: id) else { throw Failure.invalid }
        let template = custom ?? original
        try validate(template, for: id)
        let result = NSMutableString(string: template)
        let pattern = try NSRegularExpression(pattern: "\\{\\{[^{}]+\\}\\}")
        let range = NSRange(template.startIndex..., in: template)
        for match in pattern.matches(in: template, range: range).reversed() {
            let token = (template as NSString).substring(with: match.range)
            let key = String(token.dropFirst(2).dropLast(2))
            guard let value = values[key], !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw Failure.invalid }
            result.replaceCharacters(in: match.range, with: value)
        }
        return result as String
    }
}
