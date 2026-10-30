import Foundation
import SwiftData

nonisolated struct TransferLetterPeriod: Sendable {
    let name: String
    let year: Int
    let month: Int

    init(_ name: String) throws {
        let key = MonthName.sortKey(name)
        guard key > 0, (1...12).contains(key % 100) else {
            throw NSError(domain: "TransferLetter", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("تعذّر تحديد شهر الكتاب وسنته من اسم الشهر.", "Could not determine the letter month and year.")])
        }
        self.name = name
        year = key / 100
        month = key % 100
    }
    /// يُترك يوم التوقيع فارغاً؛ العدد الثابت يبقى كما في المرجع.
    var signatureDate: String { " / \(month) / \(year)" }
}

/// مصادر مبالغ الكتب الثابتة؛ لا تستبدل أي بيانات محفوظة ولا تُنشئ تحويلات.
nonisolated struct TransferLetterFigures: Sendable {
    let month: String
    let foodFund: Double
    let foodTax: Double
    let foodCompany: Double
    let healthcare: Double
    let policeFundDonations: Double
    let sports: Double
    let incomeTax: Double
    let socialProtection: Double
    let contractNominalShare: Double?
    let contractRetirement: Double
    /// لا يُعتمد مبلغ الكاش إذا وُجدت رواتب لم تُعرف طريقة دفعها.
    let payrollCash: Double?
    var treasuryTotal: Double { incomeTax + socialProtection }
    var contractTotal: Double? { contractNominalShare.map { $0 + contractRetirement } }
    var missingContractNominal: Int = 0
}

nonisolated enum TransferLetterFigureError: LocalizedError {
    case missingPayroll
    case missingColumns([String])
    case invalidAmount(String)
    case missingContractNominal(Int)
    var errorDescription: String? {
        switch self {
        case .missingPayroll: tr("لا توجد رواتب للشهر المختار.", "No payroll exists for the selected month.")
        case .missingColumns(let columns): tr("تعذّر التأكد من أعمدة الرواتب التالية: \(columns.joined(separator: "، ")). أعد استيراد ملف الرواتب وتحقق من الأعمدة.", "Missing payroll columns: \(columns.joined(separator: ", ")). Reimport and check the payroll file.")
        case .invalidAmount(let name): tr("المبلغ غير صالح في مصدر \(name).", "Invalid amount in \(name).")
        case .missingContractNominal(let count): tr("توجد \(Num.format(count)) عقود بلا راتب اسمي.", "\(count) contracts lack nominal pay.")
        }
    }
}

nonisolated enum TransferLetterFigureRules {
    /// يستخدم التقسيم المحاسبي المعتمد في كشف عقد الإطعام وكشف البنك نفسه.
    static func make(summary: BankStatementSummary, available: Set<String>) throws -> TransferLetterFigures {
        guard summary.payrollCount > 0 else { throw TransferLetterFigureError.missingPayroll }
        var required = ["الرعاية الصحية", "صندوق شهداء الشرطة", "النشاط الرياضي", "الضريبة", "الحماية الاجتماعية", "شركة عراق الجوال"]
        if summary.contractCount > 0 { required += ["الاسمي", "التوقيفات التقاعدية"] }
        let missing = required.filter { !available.contains($0) }
        guard missing.isEmpty else { throw TransferLetterFigureError.missingColumns(missing) }
        func checked(_ name: String, _ value: Double) throws -> Double {
            guard value.isFinite, value >= 0, value.rounded() == value else { throw TransferLetterFigureError.invalidAmount(name) }
            return value
        }
        let food = try checked("شركة عراق الجوال", summary.breakdown["شركة عراق الجوال"] ?? 0)
        let split = FoodContractReport.Split(total: food)
        return try TransferLetterFigures(month: summary.month,
            foodFund: checked("حصة صندوق الشهداء", split.fund),
            foodTax: checked("ضريبة عقد الإطعام", split.tax),
            foodCompany: checked("شركة عراق الجوال", split.company),
            healthcare: checked("الرعاية الصحية", summary.breakdown["الرعاية الصحية"] ?? 0),
            policeFundDonations: checked("صندوق الشهداء", summary.breakdown["صندوق شهداء الشرطة"] ?? 0),
            sports: checked("النشاط الرياضي", summary.breakdown["النشاط الرياضي"] ?? 0),
            incomeTax: checked("الضريبة", summary.breakdown["الضريبة"] ?? 0),
            socialProtection: checked("الحماية الاجتماعية", summary.breakdown["الحماية الاجتماعية"] ?? 0),
            contractNominalShare: summary.missingContractNominal == 0 ? checked("حصة اسمي العقود", summary.contractNominalShare) : nil,
            contractRetirement: checked("توقيفات العقود", summary.contractRetirement),
            payrollCash: available.contains("طريقة الدفع") && summary.unclassifiedCount == 0 && summary.missingNet == 0
                ? checked("الرواتب كاش", summary.cashNet) : nil,
            missingContractNominal: summary.missingContractNominal)
    }
}

@ModelActor
actor TransferLetterFigureEngine {
    func figures(month: String) async throws -> TransferLetterFigures {
        let summary = try await BankStatementEngine(modelContainer: modelContainer).summary(month: month)
        var descriptor = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        descriptor.propertiesToFetch = [\.rawColumnsData, \.detailsData, \.itemsData]
        let sources: [(String, [String])] = [
            ("الرعاية الصحية", ["الرعاية الصحية", "رعاية صحية"]),
            ("صندوق شهداء الشرطة", ["صندوق الشهداء", "صندوق شهداء الشرطة"]),
            ("النشاط الرياضي", ["نشاط رياضي", "النشاط الرياضي"]),
            ("الضريبة", ["الضريبة", "ضريبة"]),
            ("الحماية الاجتماعية", ["حماية الاجتماعية", "الحماية الاجتماعية"]),
            ("الاسمي", ["الاسمي"]),
            ("التوقيفات التقاعدية", ["توقيفات تقاعدية", "التوقيفات التقاعدية"])
        ]
        let matching = sources.map { ($0.0, Set($0.1.map(ArabicText.normalize))) }
        var available: Set<String> = []
        for (index, employee) in try modelContext.fetch(descriptor).enumerated() {
            if index.isMultiple(of: 512) { try Task.checkCancellation() }
            let raw = employee.rawColumns
            let headers = (raw?.headers ?? []) + Array(employee.details.keys) + Array(employee.items.keys)
            for header in headers {
                let normalized = ArabicText.normalize(header)
                for (label, choices) in matching where choices.contains(normalized) { available.insert(label) }
                if FoodContractReport.applies(to: header) { available.insert("شركة عراق الجوال") }
                if normalized == ArabicText.normalize("طريقة الدفع") { available.insert("طريقة الدفع") }
            }
            if let raw {
                for i in raw.headers.indices {
                    let normalized = ArabicText.normalize(raw.headers[i])
                    let label = matching.first { $0.1.contains(normalized) }?.0
                        ?? (FoodContractReport.applies(to: raw.headers[i]) ? "شركة عراق الجوال" : nil)
                    guard let label else { continue }
                    let value = raw.values[i].trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !value.isEmpty else { continue }
                    guard let amount = AnnualDeductionAmount.parse(value), amount.rounded() == amount else {
                        throw TransferLetterFigureError.invalidAmount(label)
                    }
                }
            }
            for (header, rawValue) in employee.details {
                let normalized = ArabicText.normalize(header)
                let label = matching.first { $0.1.contains(normalized) }?.0
                    ?? (FoodContractReport.applies(to: header) ? "شركة عراق الجوال" : nil)
                guard let label else { continue }
                let value = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !value.isEmpty else { continue }
                guard let amount = AnnualDeductionAmount.parse(value), amount.rounded() == amount else {
                    throw TransferLetterFigureError.invalidAmount(label)
                }
            }
            for (header, amount) in employee.items {
                let normalized = ArabicText.normalize(header)
                let label = matching.first { $0.1.contains(normalized) }?.0
                    ?? (FoodContractReport.applies(to: header) ? "شركة عراق الجوال" : nil)
                guard let label else { continue }
                guard amount.isFinite, amount >= 0, amount.rounded() == amount else {
                    throw TransferLetterFigureError.invalidAmount(label)
                }
            }
        }
        return try TransferLetterFigureRules.make(summary: summary, available: available)
    }
}

nonisolated struct TransferCashEmployee: Sendable, Identifiable {
    let id: Int
    let name: String
    let rank: String
    let workplace: String
    let amount: Double
}

nonisolated struct TransferCashList: Sendable {
    let month: String
    let employees: [TransferCashEmployee]
    var total: Double { employees.reduce(0) { $0 + $1.amount } }
    var repeatedNameRows: Int { max(0, employees.count - Set(employees.map { ArabicText.matchKey($0.name) }).count) }
}

@ModelActor
actor TransferCashListEngine {
    /// يُرفض الكشف إن تعذّر تصنيف أي راتب، لأن ذلك قد يُسقط موظفاً يستحق الصرف كاش.
    func list(month: String) throws -> TransferCashList {
        var descriptor = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        descriptor.propertiesToFetch = [\.name, \.rank, \.workplace, \.netSalary, \.detailsData]
        let employees = try modelContext.fetch(descriptor)
        guard !employees.isEmpty else { throw TransferLetterFigureError.missingPayroll }
        var result: [TransferCashEmployee] = []
        for (index, employee) in employees.enumerated() {
            if index.isMultiple(of: 512) { try Task.checkCancellation() }
            guard let net = employee.netSalary, net.isFinite, net >= 0, net.rounded() == net else {
                throw TransferLetterFigureError.invalidAmount(tr("صافي الراتب", "net pay"))
            }
            let payment = ArabicText.normalize(employee.details["طريقة الدفع"] ?? "")
            let cash = payment.contains("كاش") || payment.contains("نقد")
            let card = payment.contains("بطاق") || payment.contains("كارت") || payment.contains("ماستر")
            guard cash != card else {
                throw NSError(domain: "TransferCash", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("توجد رواتب بلا طريقة دفع واضحة؛ تحقق منها قبل إنشاء قائمة الكاش.", "Some payroll rows have an unclear payment method; review them before creating the cash list.")])
            }
            if cash {
                result.append(TransferCashEmployee(id: index, name: employee.name, rank: employee.rank, workplace: employee.workplace, amount: net))
            }
        }
        result.sort { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        return TransferCashList(month: month, employees: result)
    }
}
