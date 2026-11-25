import Foundation

nonisolated enum CommandDeduction: CaseIterable, Sendable {
    case fund, sports, health

    var title: String {
        switch self {
        case .fund: "صندوق شهداء الشرطة"
        case .sports: "مديرية التدريب البدني والعاب الشرطة"
        case .health: "مديرية شؤون الرعاية الصحية"
        }
    }

    private static let byHeader: [String: Self] = {
        let aliases: [(Self, [String])] = [
            (.fund, ["صندوق الشهداء", "صندوق شهداء الشرطة"]),
            (.sports, ["نشاط رياضي", "النشاط الرياضي"]),
            (.health, ["رعاية صحية", "الرعاية الصحية"])
        ]
        return Dictionary(uniqueKeysWithValues: aliases.flatMap { kind, names in
            names.map { (ArabicText.normalize($0), kind) }
        })
    }()

    static func column(_ header: String) -> Self? { byHeader[ArabicText.normalize(header)] }
}

/// تُحسب المبالغ من الرواتب الفعلية. أسعار النموذج معروضة للتعريف فقط.
nonisolated struct CommandDeductionTotals {
    private var lastHeaders: [String] = []
    private var columns: [(Int, CommandDeduction)] = []
    private var sums: [CommandDeduction: Int64] = [:]
    private var count = 0

    mutating func add(raw: PayrollColumnSnapshot?, details: @autoclosure () -> [String: String],
                      items: @autoclosure () -> [String: Double]) throws {
        var row: [CommandDeduction: Int64] = [:]
        func record(_ amount: Double?, for kind: CommandDeduction) throws {
            guard let amount, amount >= 0, let value = Int64(exactly: amount),
                  row[kind] == nil || row[kind] == value else {
                throw TransferLetterFigureError.invalidAmount(kind.title)
            }
            row[kind] = value
        }
        func read(_ text: String, _ kind: CommandDeduction) throws {
            let cell = text.trimmingCharacters(in: .whitespacesAndNewlines)
            try record(cell.isEmpty ? 0 : ArabicText.number(from: cell), for: kind)
        }

        if let raw {
            if raw.headers != lastHeaders {
                lastHeaders = raw.headers
                columns = lastHeaders.enumerated().compactMap { index, header in
                    CommandDeduction.column(header).map { (index, $0) }
                }
            }
            for (index, kind) in columns { try read(raw.values[index], kind) }
        }
        // بعض النسخ الأقدم تحفظ البنود في التفاصيل أو في قاموس الاستقطاعات فقط.
        if row.count < CommandDeduction.allCases.count {
            let missing = Set(CommandDeduction.allCases).subtracting(row.keys)
            for (title, text) in details() {
                if let kind = CommandDeduction.column(title), missing.contains(kind) { try read(text, kind) }
            }
            for (title, amount) in items() {
                if let kind = CommandDeduction.column(title), missing.contains(kind) { try record(amount, for: kind) }
            }
            if raw != nil {
                let absent = CommandDeduction.allCases.filter { row[$0] == nil }
                if !absent.isEmpty { throw TransferLetterFigureError.missingColumns(absent.map(\.title)) }
            }
        }
        for (kind, value) in row {
            let result = (sums[kind] ?? 0).addingReportingOverflow(value)
            guard !result.overflow else { throw TransferLetterFigureError.invalidAmount(kind.title) }
            sums[kind] = result.partialValue
        }
        count += 1
    }

    func amounts() throws -> [CommandDeduction: Int64] {
        guard count > 0 else { throw TransferLetterFigureError.missingPayroll }
        let missing = CommandDeduction.allCases.filter { sums[$0] == nil }
        guard missing.isEmpty else { throw TransferLetterFigureError.missingColumns(missing.map(\.title)) }
        return sums
    }
}
