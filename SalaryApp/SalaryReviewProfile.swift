import Foundation

/// القيم من ورقة الراتب. غياب العمود يختلف عن وجود خلية فارغة فيه.
nonisolated struct SalaryReviewProfile {
    let children: String?
    let maritalStatus: String?
    let maritalAllowance: String?
    let degree: String?
    let degreeAllowance: String?

    struct Columns {
        private var headers: [String] = []
        private var selected: [(Int, String)] = []
        private static let keys: [String: String] = Dictionary(uniqueKeysWithValues: SheetParser.Synonyms.reviewFields.map {
            (ArabicText.normalize($0), $0)
        })

        mutating func supplement(_ fields: inout [String: String], from raw: PayrollColumnSnapshot) {
            if headers != raw.headers {
                headers = raw.headers
                selected = headers.enumerated().compactMap { index, title in
                    Self.keys[ArabicText.normalize(title)].map { (index, $0) }
                }
            }
            for (index, key) in selected where fields[key] == nil {
                fields[key] = raw.values[index].trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }
    }

    init(details: [String: String], raw: @autoclosure () -> PayrollColumnSnapshot?, columns: inout Columns) {
        var fields = details
        // الاستيراد الأقدم لم يحفظ عدد الأطفال في تفاصيل الموظف.
        if fields["عدد الاطفال"] == nil, let snapshot = raw() {
            columns.supplement(&fields, from: snapshot)
        }
        func first(_ keys: [String]) -> String? {
            let values = keys.compactMap { fields[$0] }
            return values.first { !$0.isEmpty } ?? values.first
        }
        children = fields["عدد الاطفال"]
        maritalStatus = first(["الحالة الزوجية", "الحالة الاجتماعية"])
        maritalAllowance = fields["الزوجية"]
        degree = first(["اسم الشهادة", "اسم الشهدة", "التحصيل الدراسي"])
        degreeAllowance = fields["الشهادة"]
    }

    func changes(from old: Self, kind: SalaryChangeKind) -> [SalaryFieldChange] {
        func field(_ before: String?, _ after: String?, label: String, english: String, numeric: Bool, allowance: Bool = false) -> SalaryFieldChange? {
            guard let before, let after, before != after else { return nil }
            // خلية المخصصات المالية الفارغة في عمود موجود تعني عدم الصرف.
            if allowance, (before.isEmpty || after.isEmpty),
               ArabicText.number(from: before.isEmpty ? after : before) == 0 { return nil }
            func display(_ text: String) -> String {
                if text.isEmpty && allowance { return Money.format(0) }
                if text.isEmpty { return tr("غير مسجل", "Not recorded") }
                if numeric, let amount = ArabicText.number(from: text) { return Money.format(amount) }
                return text
            }
            if numeric, let a = ArabicText.number(from: before), let b = ArabicText.number(from: after), a == b { return nil }
            guard ArabicText.matchKey(before) != ArabicText.matchKey(after) else { return nil }
            return SalaryFieldChange(label: tr(label, english), before: display(before), after: display(after))
        }

        switch kind {
        case .children:
            return [field(old.children, children, label: "عدد الأطفال", english: "Child count", numeric: true)].compactMap { $0 }
        case .marital:
            return [field(old.maritalStatus, maritalStatus, label: "الحالة الزوجية", english: "Marital status", numeric: false),
                    field(old.maritalAllowance, maritalAllowance, label: "مخصصات الزوجية", english: "Marriage allowance", numeric: true, allowance: true)].compactMap { $0 }
        case .education:
            return [field(old.degree, degree, label: "اسم الشهادة", english: "Qualification", numeric: false),
                    field(old.degreeAllowance, degreeAllowance, label: "مخصصات الشهادة", english: "Qualification allowance", numeric: true, allowance: true)].compactMap { $0 }
        default: return []
        }
    }
}
