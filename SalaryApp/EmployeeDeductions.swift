import SwiftUI
import SwiftData
import UIKit

/// الكشف يستخدم الاسم الموحّد كما في ملفات الاستقطاعات؛ لا يغيّر البيانات المحفوظة.
nonisolated struct DeductionPerson: Identifiable, Hashable, Sendable {
    let key: String
    let name: String
    let detail: String
    var id: String { key }
}

nonisolated struct DeductionPeriodRow: Identifiable, Sendable {
    enum State: Sendable { case amount(Double), none, unavailable }
    let year: Int
    let month: Int
    let state: State
    var id: Int { year * 100 + month }
}

nonisolated struct EmployeeDeductionStatement: Sendable {
    let person: DeductionPerson
    let rank: String
    let workplace: String
    let entity: String
    let rows: [DeductionPeriodRow]
    var total: Double { rows.reduce(0) { result, row in
        if case .amount(let value) = row.state { return result + value }
        return result
    } }
    var incomplete: Bool { rows.contains { if case .unavailable = $0.state { return true }; return false } }
}

nonisolated enum EmployeeDeductionError: LocalizedError {
    case invalidRange, tooManyMonths, ambiguousName, ambiguousMonth, invalidAmount, textTooLong
    var errorDescription: String? {
        switch self {
        case .invalidRange: tr("يجب أن يكون شهر البداية قبل شهر النهاية أو مساوياً له.", "The start month must not be after the end month.")
        case .tooManyMonths: tr("الفترة طويلة جداً؛ اختر مدة لا تتجاوز ١٠٠ سنة.", "Choose a period of at most 100 years.")
        case .ambiguousName: tr("يوجد أكثر من سجل راتب بالاسم نفسه في شهر واحد. لا يمكن التأكد من تطابق الاستقطاعات؛ راجع الأسماء قبل إصدار الكشف.", "Multiple payroll records share this name in one month. Check the identities before generating a statement.")
        case .ambiguousMonth: tr("يوجد أكثر من ملف محفوظ للشهر نفسه بأسماء مختلفة. راجع الأشهر المستوردة قبل إصدار الكشف.", "More than one saved month has the same date. Check the imported months first.")
        case .invalidAmount: tr("يوجد مبلغ استقطاع فارغ أو سالب أو غير صالح لهذه الجهة. راجع ملف الاستقطاعات قبل إصدار الكشف.", "A deduction amount is empty, negative, or invalid. Check the deduction file before generating the statement.")
        case .textTooLong: tr("بعض البيانات أطول من مساحة الكشف؛ تعذّر إنشاء PDF دون اقتطاعها.", "Some details do not fit in the report without truncation.")
        }
    }
}

nonisolated enum DeductionCalendar {
    static let months = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"]
    static func label(_ year: Int, _ month: Int) -> String { "\(months[month - 1]) \(Num.digits(String(year)))" }
    static func span(from: Int, to: Int) throws -> [(Int, Int)] {
        guard (1...12).contains(from % 100), (1...12).contains(to % 100), from <= to else { throw EmployeeDeductionError.invalidRange }
        let start = (from / 100) * 12 + from % 100 - 1
        let end = (to / 100) * 12 + to % 100 - 1
        guard end - start < 1200 else { throw EmployeeDeductionError.tooManyMonths }
        return (start...end).map { ($0 / 12, $0 % 12 + 1) }
    }
}

@ModelActor
actor EmployeeDeductionEngine {
    func search(_ query: String) throws -> [DeductionPerson] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(query)).split(separator: " ").map(String.init)
        guard let first = tokens.first, tokens.joined().count >= 2 else { return [] }
        let second = tokens.count > 1 ? tokens[1] : first
        let third = tokens.count > 2 ? tokens[2] : first
        var employees = FetchDescriptor<Employee>(predicate: #Predicate { $0.matchKey.contains(first) && $0.matchKey.contains(second) && $0.matchKey.contains(third) })
        employees.fetchLimit = 300
        employees.propertiesToFetch = [\.name, \.matchKey, \.rank, \.workplace, \.month]
        var records = FetchDescriptor<Deduction>(predicate: #Predicate { $0.matchKey.contains(first) && $0.matchKey.contains(second) && $0.matchKey.contains(third) })
        records.fetchLimit = 300
        records.propertiesToFetch = [\.employeeName, \.matchKey, \.rank]
        var found: [String: DeductionPerson] = [:]
        for employee in try modelContext.fetch(employees).filter({ e in tokens.allSatisfy { e.matchKey.contains($0) } }).sorted(by: { MonthName.sortKey($0.month) > MonthName.sortKey($1.month) }) {
            if found[employee.matchKey] == nil {
                found[employee.matchKey] = DeductionPerson(key: employee.matchKey, name: employee.name, detail: [employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
            }
        }
        for row in try modelContext.fetch(records) where tokens.allSatisfy({ row.matchKey.contains($0) }) && found[row.matchKey] == nil {
            found[row.matchKey] = DeductionPerson(key: row.matchKey, name: row.employeeName, detail: row.rank)
        }
        return found.values.sorted { $0.name < $1.name }.prefix(100).map { $0 }
    }

    func entities(person: DeductionPerson) throws -> [String] {
        let key = person.key
        var d = FetchDescriptor<Deduction>(predicate: #Predicate { $0.matchKey == key })
        d.propertiesToFetch = [\.title]
        return Set(try modelContext.fetch(d).map(\.title).filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }).sorted()
    }

    func statement(person: DeductionPerson, entity: String, from: Int, to: Int) throws -> EmployeeDeductionStatement {
        let span = try DeductionCalendar.span(from: from, to: to)
        let key = person.key
        var ed = FetchDescriptor<Employee>(predicate: #Predicate { $0.matchKey == key })
        ed.propertiesToFetch = [\.month, \.rank, \.workplace]
        let employees = try modelContext.fetch(ed)
        var byMonth: [String: Int] = [:]
        for e in employees { byMonth[e.month, default: 0] += 1 }
        if byMonth.contains(where: { name, count in
            let date = MonthName.sortKey(name)
            return count > 1 && date >= from && date <= to
        }) { throw EmployeeDeductionError.ambiguousName }
        let latest = employees.filter { let k = MonthName.sortKey($0.month); return k >= from && k <= to }
            .sorted { MonthName.sortKey($0.month) > MonthName.sortKey($1.month) }.first
        let allMonths = try modelContext.fetch(FetchDescriptor<PayrollMonth>())
        var monthMap: [Int: PayrollMonth] = [:]
        for m in allMonths where m.sortKey >= from && m.sortKey <= to {
            if monthMap[m.sortKey] != nil { throw EmployeeDeductionError.ambiguousMonth }
            monthMap[m.sortKey] = m
        }
        let title = entity
        var dd = FetchDescriptor<Deduction>(predicate: #Predicate { $0.matchKey == key && $0.title == title })
        dd.propertiesToFetch = [\.month, \.amount]
        var totals: [String: Double] = [:]
        for d in try modelContext.fetch(dd) {
            let date = MonthName.sortKey(d.month)
            guard date >= from && date <= to else { continue }
            guard let amount = d.amount, amount.isFinite, amount >= 0 else { throw EmployeeDeductionError.invalidAmount }
            totals[d.month, default: 0] += amount
            guard totals[d.month]!.isFinite else { throw EmployeeDeductionError.invalidAmount }
        }
        var rows: [DeductionPeriodRow] = []
        for (year, month) in span {
            let key = year * 100 + month
            guard let source = monthMap[key], source.deductionCount > 0 else {
                rows.append(DeductionPeriodRow(year: year, month: month, state: .unavailable))
                continue
            }
            let name = source.name
            let count = try modelContext.fetchCount(FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == name }))
            if count != source.deductionCount { rows.append(DeductionPeriodRow(year: year, month: month, state: .unavailable)) }
            else if let total = totals[name] { rows.append(DeductionPeriodRow(year: year, month: month, state: .amount(total))) }
            else { rows.append(DeductionPeriodRow(year: year, month: month, state: .none)) }
        }
        let statement = EmployeeDeductionStatement(person: person, rank: latest?.rank ?? "", workplace: latest?.workplace ?? "", entity: entity, rows: rows)
        guard statement.total.isFinite else { throw EmployeeDeductionError.invalidAmount }
        return statement
    }
}

struct EmployeeDeductionsView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse)]) private var months: [PayrollMonth]
    @State private var person: DeductionPerson?
    @State private var entities: [String] = []
    @State private var entity = ""
    @State private var fromYear = Calendar.current.component(.year, from: .now)
    @State private var fromMonth = 1
    @State private var toYear = Calendar.current.component(.year, from: .now)
    @State private var toMonth = Calendar.current.component(.month, from: .now)
    @State private var initialized = false
    @State private var showingSearch = false
    @State private var report: EmployeeDeductionStatement?
    @State private var error: String?
    @State private var loading = false
    @State private var pdf: StatementPDF?
    @State private var generating = false
    private var selectionKey: String { "\(person?.key ?? "")|\(entity)|\(fromYear)|\(fromMonth)|\(toYear)|\(toMonth)|\(months.map { "\($0.name):\($0.importedAt.timeIntervalSince1970)" }.joined(separator: ","))" }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text(tr("بيانات الكشف", "Statement details")).font(.headline).foregroundStyle(.secondary)
                VStack(spacing: 0) {
                    Button { showingSearch = true } label: {
                        selectionRow(tr("الموظف", "Employee"), person?.name ?? tr("اختر موظفاً", "Choose an employee"))
                    }
                    Divider()
                    Menu {
                        ForEach(entities, id: \.self) { name in Button(name) { entity = name } }
                    } label: { selectionRow(tr("جهة الاستقطاع", "Deduction entity"), entity.isEmpty ? tr("اختر الجهة", "Choose an entity") : entity) }
                    .disabled(entities.isEmpty)
                }.background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18))
                Text(tr("الفترة المطلوبة", "Date range")).font(.headline).foregroundStyle(.secondary)
                HStack(spacing: 12) {
                    periodPicker(tr("من شهر", "From"), year: $fromYear, month: $fromMonth)
                    periodPicker(tr("إلى شهر", "To"), year: $toYear, month: $toMonth)
                }
                if loading { ProgressView(tr("جارٍ إعداد الكشف…", "Preparing statement…")).frame(maxWidth: .infinity).padding() }
                if let error { Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.orange).font(.subheadline) }
                if let report {
                    Text(tr("تُربط الاستقطاعات بالاسم؛ راجع الأسماء المتشابهة قبل اعتماد الكشف.", "Deductions are matched by name; check similar names before relying on the statement."))
                        .font(.caption).foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 8) {
                        Text(report.incomplete ? tr("مجموع القيود المتوفرة", "Total available entries") : tr("مجموع الاستقطاعات", "Total deductions")).font(.subheadline)
                        Text(Money.format(report.total)).font(.system(size: 28, weight: .bold, design: .rounded)).monospacedDigit().minimumScaleFactor(0.5).lineLimit(1)
                        Text(report.entity).font(.caption).opacity(0.8)
                    }.frame(maxWidth: .infinity, alignment: .leading).padding(18).foregroundStyle(.white)
                        .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 18))
                    if report.incomplete {
                        Label(tr("بعض الأشهر غير مستوردة أو لا يمكن التأكد من ملف استقطاعاتها؛ المجموع للقيود المتوفرة فقط.", "Some months are missing or their deduction file cannot be verified; the total covers available entries only."), systemImage: "exclamationmark.triangle.fill")
                            .font(.subheadline).foregroundStyle(.orange)
                    }
                    VStack(alignment: .leading, spacing: 0) {
                        Text(tr("تفاصيل الأشهر", "Monthly details")).font(.headline).padding()
                        ForEach(report.rows) { row in
                            HStack {
                                Text(DeductionCalendar.label(row.year, row.month))
                                Spacer()
                                Text(value(row.state)).foregroundStyle(isUnavailable(row.state) ? Color.orange : Color.primary)
                            }.font(.subheadline).padding(.horizontal).padding(.vertical, 9)
                            Divider().padding(.horizontal)
                        }
                    }.background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18))
                    Button { Task { await preparePDF(report) } } label: {
                        HStack { if generating { ProgressView().tint(.white) }; Text(tr("معاينة الكشف ومشاركته", "Preview and share")) }
                            .font(.headline).frame(maxWidth: .infinity).padding(15)
                    }.buttonStyle(.borderedProminent).disabled(generating)
                }
            }.padding(16)
        }
        .background(Color(.systemGroupedBackground))
        .navigationTitle(tr("كشف الاستقطاعات", "Deduction statement"))
        .navigationBarTitleDisplayMode(.inline)
        .sheet(isPresented: $showingSearch) { DeductionPersonPicker { person = $0; entity = "" } }
        .sheet(item: $pdf) { StatementPreview(file: $0) }
        .task {
            guard !initialized else { return }; initialized = true
            if let key = months.first(where: { $0.sortKey > 0 })?.sortKey {
                fromYear = key / 100; toYear = key / 100; toMonth = key % 100
            }
        }
        .task(id: person?.key) {
            entities = []; entity = ""
            guard let person else { return }
            do {
                let container = SharedModel.container
                let names = try await Task.detached(priority: .userInitiated) { try await EmployeeDeductionEngine(modelContainer: container).entities(person: person) }.value
                guard !Task.isCancelled else { return }
                entities = names
                if names.count == 1 { entity = names[0] }
            } catch { self.error = error.localizedDescription }
        }
        .task(id: selectionKey) {
            report = nil; error = nil; loading = false
            guard let person, !entity.isEmpty else { return }
            loading = true
            let title = entity, from = fromYear * 100 + fromMonth, to = toYear * 100 + toMonth
            do {
                let container = SharedModel.container
                let result = try await Task.detached(priority: .userInitiated) { try await EmployeeDeductionEngine(modelContainer: container).statement(person: person, entity: title, from: from, to: to) }.value
                guard !Task.isCancelled else { return }
                report = result
            } catch {
                guard !Task.isCancelled else { return }
                self.error = error.localizedDescription
            }
            loading = false
        }
    }

    private func selectionRow(_ label: String, _ value: String) -> some View {
        HStack { VStack(alignment: .leading, spacing: 4) { Text(label).font(.caption).foregroundStyle(.secondary); Text(value).foregroundStyle(.primary) }; Spacer(); Image(systemName: "chevron.forward").foregroundStyle(.tertiary) }.padding(16).contentShape(Rectangle())
    }
    private func periodPicker(_ title: String, year: Binding<Int>, month: Binding<Int>) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Menu {
                Picker(tr("الشهر", "Month"), selection: month) { ForEach(1...12, id: \.self) { Text(DeductionCalendar.months[$0 - 1]).tag($0) } }
                Picker(tr("السنة", "Year"), selection: year) { ForEach(1900...2200, id: \.self) { Text(Num.digits(String($0))).tag($0) } }
            } label: { HStack { Text(DeductionCalendar.label(year.wrappedValue, month.wrappedValue)).lineLimit(1).minimumScaleFactor(0.65); Image(systemName: "chevron.down") }.foregroundStyle(.primary) }
        }.frame(maxWidth: .infinity, alignment: .leading).padding(12).background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14))
    }
    private func isUnavailable(_ state: DeductionPeriodRow.State) -> Bool { if case .unavailable = state { return true }; return false }
    private func value(_ state: DeductionPeriodRow.State) -> String {
        switch state { case .amount(let amount): return Money.format(amount); case .none: return tr("لا يوجد", "None"); case .unavailable: return tr("غير متحقق", "Unverified") }
    }
    private func preparePDF(_ report: EmployeeDeductionStatement) async {
        generating = true; defer { generating = false }
        let requested = selectionKey
        do {
            let logo = UIImage(named: "HomeLogo")?.pngData()
            let url = try await EmployeeDeductionPDF.make(report, logo: logo)
            guard requested == selectionKey else { return }
            pdf = StatementPDF(url: url)
        } catch { self.error = error.localizedDescription }
    }
}

struct DeductionPersonPicker: View {
    let select: (DeductionPerson) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var people: [DeductionPerson] = []
    @State private var error: String?
    var body: some View {
        NavigationStack {
            List {
                Text(tr("اكتب اسم الموظف بوضوح لتضييق النتائج.", "Enter more of the employee's name to narrow the results.")).font(.caption).foregroundStyle(.secondary)
                if let error { Text(error).foregroundStyle(.red) }
                ForEach(people) { person in
                    Button { select(person); dismiss() } label: {
                        VStack(alignment: .leading) { Text(person.name).foregroundStyle(.primary); if !person.detail.isEmpty { Text(person.detail).font(.caption).foregroundStyle(.secondary) } }
                    }
                }
                if query.count >= 2 && people.isEmpty && error == nil { Text(tr("لا توجد نتائج", "No results")).foregroundStyle(.secondary) }
            }
            .searchable(text: $query, prompt: tr("اسم الموظف", "Employee name"))
            .navigationTitle(tr("اختيار الموظف", "Choose employee"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إلغاء", "Cancel")) { dismiss() } } }
            .task(id: query) {
                people = []; error = nil
                guard query.count >= 2 else { return }
                do {
                    try await Task.sleep(for: .milliseconds(250))
                    let text = query, container = SharedModel.container
                    let result = try await Task.detached(priority: .userInitiated) { try await EmployeeDeductionEngine(modelContainer: container).search(text) }.value
                    guard !Task.isCancelled else { return }; people = result
                } catch { if !Task.isCancelled { self.error = error.localizedDescription } }
            }
        }.environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
    }
}

struct StatementPDF: Identifiable { let id = UUID(); let url: URL }
struct StatementPreview: View {
    let file: StatementPDF
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack {
            PDFPreview(url: file.url).navigationTitle(tr("معاينة الكشف", "Statement preview")).navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button(tr("إغلاق", "Close")) { dismiss() } }
                    ToolbarItem(placement: .primaryAction) { Button(tr("مشاركة PDF", "Share PDF"), systemImage: "square.and.arrow.up") { ActivityShare.present([file.url]) } }
                }
        }.environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
    }
}

/// تنسيق الكشف المعتمد: حتى ثلاث سنوات في الصفحة، بحدود لكل خلية ومجموع مستقل.
nonisolated enum EmployeeDeductionPDF {
    static func make(_ statement: EmployeeDeductionStatement, logo: Data?) async throws -> URL {
        try await PDFWork.run {
            let dir = FileManager.default.temporaryDirectory.appendingPathComponent("deduction-statement-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let url = dir.appendingPathComponent("كشف استقطاعات موظف.pdf")
            do {
                try write(statement, logo: logo, url: url)
                try PDFDecor.apply(to: url, footer: "قسم الحسابات / CT")
                return url
            } catch {
                try? FileManager.default.removeItem(at: dir)
                throw error
            }
        }
    }

    private static func write(_ statement: EmployeeDeductionStatement, logo: Data?, url: URL) throws {
        let page = CGRect(x: 0, y: 0, width: 595, height: 842)
        let margin: CGFloat = 30
        let width = page.width - margin * 2
        let gap: CGFloat = 6
        let green = UIColor(red: 0.08, green: 0.26, blue: 0.20, alpha: 1)
        let light = UIColor(red: 0.90, green: 0.94, blue: 0.91, alpha: 1)
        let border = UIColor(red: 0.36, green: 0.46, blue: 0.39, alpha: 1)
        let yearGroups = Dictionary(grouping: statement.rows, by: \.year)
        let years = yearGroups.keys.sorted()
        let pages = stride(from: 0, to: years.count, by: 3).map { Array(years[$0..<min($0 + 3, years.count)]) }
        guard let first = statement.rows.first, let last = statement.rows.last, !pages.isEmpty else { throw EmployeeDeductionError.invalidRange }
        let moneyFormatter = NumberFormatter()
        moneyFormatter.numberStyle = .decimal
        moneyFormatter.locale = Locale(identifier: "en_US")
        moneyFormatter.maximumFractionDigits = 2
        func number(_ value: Double) -> String { ReportSheet.easternDigits(moneyFormatter.string(from: NSNumber(value: value)) ?? String(value)) }
        func fits(_ text: String, width: CGFloat, size: CGFloat) -> Bool {
            (text as NSString).size(withAttributes: [.font: ReportFont.bold(size)]).width <= width - 8
        }
        func fitSize(_ text: String, width: CGFloat, sizes: [CGFloat] = [10, 9, 8, 7]) throws -> CGFloat {
            guard let size = sizes.first(where: { fits(text, width: width, size: $0) }) else { throw EmployeeDeductionError.textTooLong }
            return size
        }
        let half = width / 2
        let details = [statement.person.name, statement.rank.isEmpty ? "—" : statement.rank,
                       statement.entity, statement.workplace.isEmpty ? "—" : statement.workplace,
                       DeductionCalendar.label(first.year, first.month), DeductionCalendar.label(last.year, last.month)]
        let detailSizes = try details.map { try fitSize($0, width: half * 0.68) }
        let colWidth = (width - 2 * gap) / 3
        let valueWidth = colWidth * 0.47
        for row in statement.rows {
            if case .amount(let amount) = row.state { _ = try fitSize(number(amount), width: valueWidth, sizes: [9, 8, 7]) }
        }
        for year in years {
            let sum = yearGroups[year, default: []].reduce(0.0) { result, row in
                if case .amount(let amount) = row.state { return result + amount }; return result
            }
            _ = try fitSize(number(sum), width: valueWidth, sizes: [9, 8, 7])
        }
        _ = try fitSize(number(statement.total), width: width * 0.45, sizes: [13, 11, 9, 7])
        let renderer = UIGraphicsPDFRenderer(bounds: page)
        try renderer.writePDF(to: url) { context in
            for (pageIndex, pageYears) in pages.enumerated() { autoreleasepool {
                context.beginPage()
                let cg = context.cgContext
                func text(_ value: String, _ rect: CGRect, size: CGFloat = 9, bold: Bool = false, color: UIColor = .black, align: NSTextAlignment = .right) {
                    PDFText.draw(value, in: rect.insetBy(dx: 4, dy: 0), font: bold ? ReportFont.bold(size) : ReportFont.regular(size), color: color, align: align)
                }
                func cell(_ rect: CGRect, fill: UIColor? = nil) {
                    if let fill { cg.setFillColor(fill.cgColor); cg.fill(rect) }
                    cg.setStrokeColor(border.cgColor); cg.setLineWidth(0.6); cg.stroke(rect)
                }
                if let logo, let image = UIImage(data: logo) { image.draw(in: CGRect(x: margin + 5, y: 26, width: 52, height: 52)) }
                let header = ["جمهورية العراق", "وزارة الداخلية", "قيادة شرطة محافظة الأنبار", "قسم الحسابات"]
                for (i, line) in header.enumerated() { text(line, CGRect(x: 275, y: 25 + CGFloat(i) * 13, width: width - 245, height: 13), size: 10, bold: true) }
                cg.setStrokeColor(green.cgColor); cg.setLineWidth(1.5); cg.move(to: CGPoint(x: margin, y: 83)); cg.addLine(to: CGPoint(x: page.width - margin, y: 83)); cg.strokePath()
                text("كشف استقطاعات موظف", CGRect(x: margin, y: 92, width: width, height: 25), size: 15, bold: true, color: green, align: .center)
                text("تفاصيل الاستقطاع لصالح الجهة خلال الفترة المختارة", CGRect(x: margin, y: 118, width: width, height: 16), size: 9, color: .darkGray, align: .center)
                let labels = ["اسم الموظف", "الرتبة", "جهة الاستقطاع", "محل العمل", "من", "إلى"]
                for index in 0..<6 {
                    let row = index / 2
                    let x = index % 2 == 0 ? margin + half : margin
                    let rect = CGRect(x: x, y: 142 + CGFloat(row) * 25, width: half, height: 25)
                    cell(rect)
                    text(labels[index], CGRect(x: rect.maxX - half * 0.30, y: rect.minY, width: half * 0.30, height: rect.height), size: 8, color: .darkGray)
                    text(details[index], CGRect(x: rect.minX, y: rect.minY, width: half * 0.68, height: rect.height), size: detailSizes[index], bold: true, align: .left)
                }
                text("الاستقطاعات الشهرية", CGRect(x: margin, y: 226, width: width, height: 22), size: 11, bold: true, color: green)
                let top: CGFloat = 252
                let yearHeight: CGFloat = 26, headingHeight: CGFloat = 23, rowHeight: CGFloat = 23, sumHeight: CGFloat = 27
                for (column, year) in pageYears.enumerated() {
                    let x = page.width - margin - colWidth - CGFloat(column) * (colWidth + gap)
                    let headerRect = CGRect(x: x, y: top, width: colWidth, height: yearHeight)
                    cell(headerRect, fill: green); text(ReportSheet.easternDigits(String(year)), headerRect, size: 11, bold: true, color: .white, align: .center)
                    let titleY = top + yearHeight
                    let monthWidth = colWidth - valueWidth
                    let valueRect = CGRect(x: x, y: titleY, width: valueWidth, height: headingHeight)
                    let monthRect = CGRect(x: x + valueWidth, y: titleY, width: monthWidth, height: headingHeight)
                    cell(valueRect, fill: light); cell(monthRect, fill: light)
                    text("المبلغ", valueRect, size: 8.5, bold: true, align: .center)
                    text("الشهر", monthRect, size: 8.5, bold: true, align: .center)
                    let rows = yearGroups[year, default: []]
                    for month in 1...12 {
                        let y = titleY + headingHeight + CGFloat(month - 1) * rowHeight
                        let vr = CGRect(x: x, y: y, width: valueWidth, height: rowHeight)
                        let mr = CGRect(x: x + valueWidth, y: y, width: monthWidth, height: rowHeight)
                        cell(vr); cell(mr)
                        guard let row = rows.first(where: { $0.month == month }) else { continue }
                        text(DeductionCalendar.months[month - 1], mr, size: 8.5)
                        switch row.state {
                        case .amount(let amount):
                            let s = (try? fitSize(number(amount), width: valueWidth, sizes: [9, 8, 7])) ?? 7
                            text(number(amount), vr, size: s, align: .center)
                        case .none: text("لا يوجد", vr, size: 8, align: .center)
                        case .unavailable: text("غير متحقق", vr, size: 7.5, color: .systemOrange, align: .center)
                        }
                    }
                    let sumY = titleY + headingHeight + 12 * rowHeight
                    let left = CGRect(x: x, y: sumY, width: valueWidth, height: sumHeight)
                    let right = CGRect(x: x + valueWidth, y: sumY, width: monthWidth, height: sumHeight)
                    cell(left, fill: light); cell(right, fill: light)
                    let incomplete = rows.contains { if case .unavailable = $0.state { return true }; return false }
                    let subtotalTitle = incomplete ? "مجموع المتوفر" : (rows.count == 12 ? "مجموع السنة" : "مجموع الفترة")
                    text(subtotalTitle, right, size: 8, bold: true, align: .center)
                    let subtotal = rows.reduce(0.0) { value, row in if case .amount(let amount) = row.state { return value + amount }; return value }
                    let size = (try? fitSize(number(subtotal), width: valueWidth, sizes: [9, 8, 7])) ?? 7
                    text(number(subtotal), left, size: size, bold: true, align: .center)
                }
                let endY = top + yearHeight + headingHeight + 12 * rowHeight + sumHeight
                if pageIndex == pages.count - 1 {
                    let totalRect = CGRect(x: margin, y: endY + 14, width: width, height: 30)
                    cg.setFillColor(green.cgColor); cg.fill(totalRect)
                    text(statement.incomplete ? "مجموع القيود المتوفرة" : "المجموع الكلي للفترة", CGRect(x: totalRect.midX, y: totalRect.minY, width: width / 2, height: 30), size: 10, bold: true, color: .white)
                    let amountSize = (try? fitSize(number(statement.total), width: width * 0.45, sizes: [13, 11, 9, 7])) ?? 7
                    text(number(statement.total), CGRect(x: margin, y: totalRect.minY, width: width * 0.45, height: 30), size: amountSize, bold: true, color: .white, align: .left)
                    if statement.incomplete {
                        text("غير متحقق: الشهر غير مستورد أو ملف الاستقطاعات غير مؤكد؛ المجموع للقيود المتوفرة فقط.", CGRect(x: margin, y: totalRect.maxY + 4, width: width, height: 24), size: 8, color: .darkGray)
                    }
                    text("التدقيق: ................................", CGRect(x: page.width - margin - 200, y: totalRect.maxY + 34, width: 200, height: 20), size: 9)
                }
            } }
        }
    }
}
