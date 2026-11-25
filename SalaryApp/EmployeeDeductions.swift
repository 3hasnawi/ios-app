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
    enum State: Sendable { case amount(Double), status(Double?, [String]), none, unavailable }
    let year: Int
    let month: Int
    let state: State
    var id: Int { year * 100 + month }
}

nonisolated struct DeductionAvailableMonths: Sendable {
    let keys: [Int]
    let unrecognizedCount: Int
}

nonisolated struct DeductionCaseReference: Identifiable, Hashable, Sendable {
    let title: String
    let id: String

    init?(note: String, amount: Double?) {
        var text = note[...]
        if DeductionStatus.read(note, amount: amount) != nil, let end = text.firstIndex(of: "⟧") {
            text = text[text.index(after: end)...]
        }
        title = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty else { return nil }
        // نحفظ الرقم وتاريخه كاملين؛ نساوي فقط اختلاف الأرقام والمسافات حول الفاصل.
        let normalized = ArabicText.latinDigits(title).split(whereSeparator: \.isWhitespace).joined(separator: " ")
        id = normalized.replacingOccurrences(of: " /", with: "/").replacingOccurrences(of: "/ ", with: "/")
    }
}

nonisolated struct DeductionPeriodOptions: Sendable {
    let all: DeductionAvailableMonths
    let references: [DeductionCaseReference]
    let byReference: [String: DeductionAvailableMonths]
}

nonisolated struct EmployeeDeductionStatement: Sendable {
    let person: DeductionPerson
    let rank: String
    let workplace: String
    let entity: String
    let rows: [DeductionPeriodRow]
    var hasUnrecognizedMonths = false
    var references: [String] = []
    var total: Double { rows.reduce(0) { result, row in
        if case .amount(let value) = row.state { return result + value }
        if case .status(let amount, _) = row.state { return result + (amount ?? 0) }
        return result
    } }
    var incomplete: Bool { hasUnrecognizedMonths || rows.contains { if case .unavailable = $0.state { return true }; return false } }
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

/// إلغاء المهمة عند تغيير الاختيار يمنع استمرار الاستعلام القديم في الخلفية.
private nonisolated func awaitDeductionWork<T: Sendable>(_ work: Task<T, Error>) async throws -> T {
    try await withTaskCancellationHandler {
        try await work.value
    } onCancel: {
        work.cancel()
    }
}

@ModelActor
actor EmployeeDeductionEngine {
    func search(_ query: String) throws -> [DeductionPerson] {
        try Task.checkCancellation()
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
            try Task.checkCancellation()
            if found[employee.matchKey] == nil {
                found[employee.matchKey] = DeductionPerson(key: employee.matchKey, name: employee.name, detail: [employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
            }
        }
        for row in try modelContext.fetch(records) where tokens.allSatisfy({ row.matchKey.contains($0) }) && found[row.matchKey] == nil {
            try Task.checkCancellation()
            found[row.matchKey] = DeductionPerson(key: row.matchKey, name: row.employeeName, detail: row.rank)
        }
        return found.values.sorted { $0.name < $1.name }.prefix(100).map { $0 }
    }

    func entities(person: DeductionPerson) throws -> [String] {
        try Task.checkCancellation()
        let key = person.key
        var d = FetchDescriptor<Deduction>(predicate: #Predicate { $0.matchKey == key })
        d.propertiesToFetch = [\.title]
        let records = try modelContext.fetch(d)
        try Task.checkCancellation()
        return Set(records.map(\.title).filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }).sorted()
    }

    /// نطاق الاختيار من قيود الشخص والجهة فقط، بما فيها الحالات النصية.
    func availableMonths(person: DeductionPerson, entity: String) throws -> DeductionPeriodOptions {
        try Task.checkCancellation()
        let key = person.key, title = entity
        var d = FetchDescriptor<Deduction>(predicate: #Predicate { $0.matchKey == key && $0.title == title })
        d.propertiesToFetch = [\.month, \.note, \.amount]
        var names = Set<String>()
        var monthsByReference: [String: Set<String>] = [:]
        var references: [String: DeductionCaseReference] = [:]
        for record in try modelContext.fetch(d) {
            try Task.checkCancellation()
            names.insert(record.month)
            if let ref = DeductionCaseReference(note: record.note, amount: record.amount) {
                monthsByReference[ref.id, default: []].insert(record.month)
                if references[ref.id].map({ ref.title < $0.title }) ?? true { references[ref.id] = ref }
            }
        }
        func periods(_ names: Set<String>) -> DeductionAvailableMonths {
            let keys = names.map(MonthName.sortKey)
            let valid = keys.filter { (190001...220012).contains($0) && (1...12).contains($0 % 100) }
            return DeductionAvailableMonths(keys: Set(valid).sorted(), unrecognizedCount: keys.count - valid.count)
        }
        return DeductionPeriodOptions(all: periods(names), references: references.values.sorted {
            ArabicText.alphabeticalOrder($0.title, $1.title) == .orderedAscending
        }, byReference: monthsByReference.mapValues(periods))
    }

    func statement(person: DeductionPerson, entity: String, from: Int, to: Int,
                   reference: DeductionCaseReference? = nil) throws -> EmployeeDeductionStatement {
        try Task.checkCancellation()
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
        dd.propertiesToFetch = [\.month, \.amount, \.note, \.rank]
        var totals: [String: Double] = [:]
        var statuses: [String: Set<String>] = [:]
        var hasUnrecognizedMonths = false
        var references = Set<String>()
        var deductionRank = ""
        var latestRankDate = 0
        for d in try modelContext.fetch(dd) {
            try Task.checkCancellation()
            let rowReference = DeductionCaseReference(note: d.note, amount: d.amount)
            if let reference, rowReference?.id != reference.id { continue }
            let date = MonthName.sortKey(d.month)
            if date == 0 { hasUnrecognizedMonths = true; continue }
            guard date >= from && date <= to else { continue }
            if date >= latestRankDate && !d.rank.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                deductionRank = d.rank; latestRankDate = date
            }
            if let rowReference { references.insert(reference?.title ?? rowReference.title) }
            if let status = DeductionStatus.read(d.note, amount: d.amount) {
                statuses[d.month, default: []].insert(status)
                continue
            }
            guard let amount = d.amount, amount.isFinite, amount >= 0 else { throw EmployeeDeductionError.invalidAmount }
            totals[d.month, default: 0] += amount
            guard totals[d.month]!.isFinite else { throw EmployeeDeductionError.invalidAmount }
        }
        var rows: [DeductionPeriodRow] = []
        for (year, month) in span {
            try Task.checkCancellation()
            let key = year * 100 + month
            guard let source = monthMap[key], source.deductionCount > 0 else {
                rows.append(DeductionPeriodRow(year: year, month: month, state: .unavailable))
                continue
            }
            let name = source.name
            let count = try modelContext.fetchCount(FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == name }))
            if count != source.deductionCount { rows.append(DeductionPeriodRow(year: year, month: month, state: .unavailable)) }
            else if let names = statuses[name], !names.isEmpty {
                rows.append(DeductionPeriodRow(year: year, month: month, state: .status(totals[name], names.sorted())))
            }
            else if let total = totals[name] { rows.append(DeductionPeriodRow(year: year, month: month, state: .amount(total))) }
            else { rows.append(DeductionPeriodRow(year: year, month: month, state: .none)) }
        }
        let statement = EmployeeDeductionStatement(person: person, rank: (latest?.rank.isEmpty == false ? latest!.rank : deductionRank), workplace: latest?.workplace ?? "", entity: entity, rows: rows, hasUnrecognizedMonths: hasUnrecognizedMonths, references: references.sorted())
        guard statement.total.isFinite else { throw EmployeeDeductionError.invalidAmount }
        return statement
    }
}

struct EmployeeDeductionsView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse)]) private var months: [PayrollMonth]
    @State private var person: DeductionPerson?
    @State private var entities: [String] = []
    @State private var entity = ""
    @State private var options: DeductionPeriodOptions?
    @State private var selectedReference: DeductionCaseReference?
    private var periods: DeductionAvailableMonths? {
        if let selectedReference { return options?.byReference[selectedReference.id] }
        return options?.all
    }
    private var availableMonths: [Int] { periods?.keys ?? [] }
    private var unrecognizedMonths: Int { periods?.unrecognizedCount ?? 0 }
    @State private var fromMonth = 0
    @State private var toMonth = 0
    @State private var loadedPeriodKey = ""
    @State private var periodError: String?
    @State private var entityError: String?
    @State private var loadingPeriods = false
    @State private var showingSearch = false
    @State private var report: EmployeeDeductionStatement?
    @State private var reportKey = ""
    @State private var error: String?
    @State private var loading = false
    @State private var pdf: StatementPDF?
    @State private var generating = false

    init(initialPerson: DeductionPerson? = nil, initialEntity: String = "") {
        _entity = State(initialValue: initialEntity)
        _person = State(initialValue: initialPerson)
    }
    private var dataVersion: String { months.map { "\($0.name):\($0.importedAt.timeIntervalSince1970)" }.joined(separator: ",") }
    private var periodKey: String { "\(person?.key ?? "")|\(entity)|\(dataVersion)" }
    private var selectionKey: String { "\(periodKey)|\(loadedPeriodKey)|\(selectedReference?.id ?? "")|\(fromMonth)|\(toMonth)" }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text(tr("بيانات الكشف", "Statement details")).font(.headline).foregroundStyle(.secondary)
                VStack(spacing: 0) {
                    Button { showingSearch = true } label: {
                        selectionRow(tr("الموظف", "Employee"), person?.name ?? tr("اختر موظفاً", "Choose an employee"))
                    }
                    .accessibilityIdentifier("deductionSelectedPerson")
                    Divider()
                    Menu {
                        ForEach(entities, id: \.self) { name in Button(name) { entity = name } }
                    } label: { selectionRow(tr("جهة الاستقطاع", "Deduction entity"), entity.isEmpty ? tr("اختر الجهة", "Choose an entity") : entity) }
                    .disabled(entities.isEmpty)
                    if loadedPeriodKey == periodKey, let options, !options.references.isEmpty {
                        Divider()
                        Menu {
                            Button { selectReference(nil) } label: {
                                if selectedReference == nil { Label(tr("كل الإضبارات", "All cases"), systemImage: "checkmark") }
                                else { Text(tr("كل الإضبارات", "All cases")) }
                            }
                            ForEach(options.references) { reference in
                                Button { selectReference(reference) } label: {
                                    if selectedReference?.id == reference.id { Label(reference.title, systemImage: "checkmark") }
                                    else { Text(verbatim: reference.title) }
                                }
                            }
                        } label: {
                            selectionRow(tr("رقم الإضبارة", "Case reference"), selectedReference?.title ?? tr("كل الإضبارات", "All cases"))
                        }
                    }

                }.background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18))
                if let entityError { Label(entityError, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.orange) }
                else if person != nil && entities.isEmpty {
                    Text(tr("لا توجد جهات استقطاع لهذا الموظف أو ما زال البحث جارياً.", "No deduction entities found for this employee, or search is still in progress.")).font(.caption).foregroundStyle(.secondary)
                }
                Text(tr("الفترة المطلوبة", "Date range")).font(.headline).foregroundStyle(.secondary)
                HStack(spacing: 12) {
                    periodPicker(tr("من شهر", "From"), isStart: true)
                    periodPicker(tr("إلى شهر", "To"), isStart: false)
                }
                if loadingPeriods { ProgressView(tr("جارٍ تحديد الأشهر المتوفرة…", "Finding available months…")).font(.caption) }
                else if let periodError { Label(periodError, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.orange) }
                else if !entity.isEmpty && loadedPeriodKey == periodKey && availableMonths.isEmpty {
                    Text(tr("لا توجد أشهر مفهومة لهذه الجهة.", "No recognizable months for this entity.")).font(.caption).foregroundStyle(.secondary)
                }
                if unrecognizedMonths > 0 && loadedPeriodKey == periodKey {
                    Label(tr("توجد أشهر بأسماء غير مفهومة لا تظهر في الاختيار؛ راجع أسماء الأشهر المستوردة.", "Some month names could not be recognized and are not selectable; check the imported months."), systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.orange)
                }
                if loading { ProgressView(tr("جارٍ إعداد الكشف…", "Preparing statement…")).frame(maxWidth: .infinity).padding() }
                if let error { Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.orange).font(.subheadline) }
                if let report, reportKey == selectionKey {
                    Text(tr("تُربط الاستقطاعات بالاسم؛ راجع الأسماء المتشابهة قبل اعتماد الكشف.", "Deductions are matched by name; check similar names before relying on the statement."))
                        .font(.caption).foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 8) {
                        Text(report.incomplete ? tr("مجموع القيود المتوفرة", "Total available entries") : tr("مجموع الاستقطاعات", "Total deductions")).font(.subheadline)
                        Text(Money.format(report.total)).font(.system(size: 28, weight: .bold, design: .rounded)).monospacedDigit().minimumScaleFactor(0.5).lineLimit(1)
                        Text(report.entity).font(.caption).opacity(0.8)
                    }.frame(maxWidth: .infinity, alignment: .leading).padding(18).foregroundStyle(.white)
                        .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 18))
                    if report.incomplete {
                        Label(tr("بعض الأشهر غير مستوردة أو غير متحققة أو أسماؤها غير مفهومة؛ المجموع للقيود المتوفرة فقط.", "Some months are missing, unverified, or have unrecognized names; the total covers available entries only."), systemImage: "exclamationmark.triangle.fill")
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
        .task(id: "\(person?.key ?? "")|\(dataVersion)") {
            let previous = entity
            entities = []; entityError = nil
            guard let person else { return }
            do {
                let container = SharedModel.container
                let work = Task.detached(priority: .userInitiated) { try await EmployeeDeductionEngine(modelContainer: container).entities(person: person) }
                let names = try await awaitDeductionWork(work)
                guard !Task.isCancelled else { return }
                entities = names
                if names.contains(previous) { entity = previous }
                else { entity = names.count == 1 ? names[0] : "" }
            } catch { if !Task.isCancelled { entityError = error.localizedDescription } }
        }
        .task(id: periodKey) {
            options = nil; selectedReference = nil; fromMonth = 0; toMonth = 0
            loadedPeriodKey = ""; periodError = nil; loadingPeriods = false
            guard let person, !entity.isEmpty else { return }
            loadingPeriods = true
            let requested = periodKey, title = entity, container = SharedModel.container
            do {
                let work = Task.detached(priority: .userInitiated) {
                    try await EmployeeDeductionEngine(modelContainer: container).availableMonths(person: person, entity: title)
                }
                let result = try await awaitDeductionWork(work)
                guard !Task.isCancelled, requested == periodKey else { return }
                options = result
                selectReference(nil)
                loadedPeriodKey = requested
            } catch {
                guard !Task.isCancelled, requested == periodKey else { return }
                periodError = error.localizedDescription
            }
            loadingPeriods = false
        }
        .task(id: selectionKey) {
            report = nil; error = nil; loading = false
            guard let person, !entity.isEmpty, loadedPeriodKey == periodKey,
                  availableMonths.contains(fromMonth), availableMonths.contains(toMonth) else { return }
            loading = true
            let title = entity, from = fromMonth, to = toMonth, reference = selectedReference, requested = selectionKey
            do {
                let container = SharedModel.container
                let work = Task.detached(priority: .userInitiated) { try await EmployeeDeductionEngine(modelContainer: container).statement(person: person, entity: title, from: from, to: to, reference: reference) }
                let result = try await awaitDeductionWork(work)
                guard !Task.isCancelled, requested == selectionKey else { return }
                reportKey = requested
                report = result
            } catch {
                guard !Task.isCancelled else { return }
                self.error = error.localizedDescription
            }
            loading = false
        }
    }

    private func selectReference(_ reference: DeductionCaseReference?) {
        selectedReference = reference
        fromMonth = availableMonths.first ?? 0
        toMonth = availableMonths.last ?? 0
    }

    private func selectionRow(_ label: String, _ value: String) -> some View {
        HStack { VStack(alignment: .leading, spacing: 4) { Text(label).font(.caption).foregroundStyle(.secondary); Text(value).foregroundStyle(.primary) }; Spacer(); Image(systemName: "chevron.forward").foregroundStyle(.tertiary) }.padding(16).contentShape(Rectangle())
    }
    private func periodPicker(_ title: String, isStart: Bool) -> some View {
        let selected = isStart ? fromMonth : toMonth
        let years = Array(Set(availableMonths.map { $0 / 100 })).sorted()
        return VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Menu {
                ForEach(years, id: \.self) { year in
                    Section {
                        ForEach(availableMonths.filter { $0 / 100 == year }, id: \.self) { key in
                            Button {
                                if isStart { fromMonth = key; if toMonth < key { toMonth = key } }
                                else { toMonth = key; if fromMonth > key { fromMonth = key } }
                            } label: {
                                if key == selected { Label(DeductionCalendar.label(key / 100, key % 100), systemImage: "checkmark") }
                                else { Text(DeductionCalendar.label(key / 100, key % 100)) }
                            }
                        }
                    } header: { Text(Num.digits(String(year))) }
                }
            } label: {
                HStack {
                    Text(selected == 0 ? tr("اختر الجهة أولاً", "Choose an entity first") : DeductionCalendar.label(selected / 100, selected % 100)).lineLimit(1).minimumScaleFactor(0.65)
                    Image(systemName: "chevron.down")
                }.foregroundStyle(.primary)
            }.disabled(availableMonths.isEmpty || loadedPeriodKey != periodKey)
        }.frame(maxWidth: .infinity, alignment: .leading).padding(12).background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14))
    }
    private func isUnavailable(_ state: DeductionPeriodRow.State) -> Bool { if case .unavailable = state { return true }; return false }
    private func value(_ state: DeductionPeriodRow.State) -> String {
        switch state {
        case .amount(let amount): return Money.format(amount)
        case .status(let amount, let names): return ([amount.map { Money.format($0) }].compactMap { $0 } + names).joined(separator: " · ")
        case .none: return tr("لا يوجد", "None")
        case .unavailable: return tr("غير متحقق", "Unverified")
        }
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
                    let work = Task.detached(priority: .userInitiated) { try await EmployeeDeductionEngine(modelContainer: container).search(text) }
                    let result = try await awaitDeductionWork(work)
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

/// كشف أفقي حتى أربع سنوات في الصفحة مع بيان المراجع والمجاميع.
nonisolated enum EmployeeDeductionPDF {
    static func make(_ statement: EmployeeDeductionStatement, logo: Data?) async throws -> URL {
        try await PDFWork.run {
            let dir = FileManager.default.temporaryDirectory.appendingPathComponent("deduction-statement-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let url = dir.appendingPathComponent("كشف استقطاعات موظف.pdf")
            do {
                try write(statement, logo: logo, url: url)
                try PDFDecor.apply(to: url, footer: "كشف الاستقطاعات")
                return url
            } catch {
                try? FileManager.default.removeItem(at: dir)
                throw error
            }
        }
    }

    private static func write(_ statement: EmployeeDeductionStatement, logo: Data?, url: URL) throws {
        let page = CGRect(x: 0, y: 0, width: 842, height: 595)
        let margin: CGFloat = 22, width: CGFloat = 798
        let navy = UIColor(red: 0.11, green: 0.23, blue: 0.31, alpha: 1)
        let light = UIColor(red: 0.90, green: 0.94, blue: 0.96, alpha: 1)
        let stripe = UIColor(red: 0.93, green: 0.95, blue: 0.96, alpha: 1)
        let border = UIColor(red: 0.65, green: 0.72, blue: 0.77, alpha: 1)
        let grouped = Dictionary(grouping: statement.rows, by: \.year)
        let years = grouped.keys.sorted()
        let pages = stride(from: 0, to: years.count, by: 4).map { Array(years[$0..<min($0 + 4, years.count)]) }
        guard !pages.isEmpty else { throw EmployeeDeductionError.invalidRange }
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal; formatter.locale = Locale(identifier: "en_US"); formatter.maximumFractionDigits = 2
        func number(_ value: Double) -> String { formatter.string(from: NSNumber(value: value)) ?? String(value) }
        func fits(_ value: String, width: CGFloat, size: CGFloat) -> Bool {
            (value as NSString).size(withAttributes: [.font: ReportFont.bold(size)]).width <= width - 8
        }
        func fitSize(_ value: String, width: CGFloat, sizes: [CGFloat] = [11, 10, 9, 8, 7]) throws -> CGFloat {
            guard let size = sizes.first(where: { fits(value, width: width, size: $0) }) else { throw EmployeeDeductionError.textTooLong }
            return size
        }
        let reference = statement.references.isEmpty ? "—" : statement.references.joined(separator: " | ")
        // لا نختصر رقم الإضبارة أو نرسمه فوق الجدول عند كثرة المراجع.
        let referenceFont = ReportFont.bold(9)
        let referenceBox = (reference as NSString).boundingRect(with: CGSize(width: width * 0.48 - 8, height: 1000), options: [.usesLineFragmentOrigin, .usesFontLeading], attributes: [.font: referenceFont], context: nil)
        let referenceHeight = max(29, ceil(referenceBox.height + 8))
        guard referenceHeight <= 65 else { throw EmployeeDeductionError.textTooLong }
        _ = try fitSize(statement.person.name, width: width * 0.74, sizes: [12, 11, 10, 9, 8])
        _ = try fitSize(statement.rank.isEmpty ? "—" : statement.rank, width: width * 0.25)
        _ = try fitSize("جهة الاستقطاع: " + statement.entity, width: width * 0.5)
        let groupWidth = width / 4, valueWidth = groupWidth * 0.51, monthWidth = groupWidth - valueWidth
        func statusText(_ value: Double?, _ names: [String]) -> String {
            ([value.map(number)].compactMap { $0 } + names).joined(separator: " / ")
        }
        for month in DeductionCalendar.months { _ = try fitSize(month, width: monthWidth, sizes: [12]) }
        for row in statement.rows {
            if case .amount(let amount) = row.state { _ = try fitSize(number(amount), width: valueWidth, sizes: [12]) }
            if case .status(let amount, let names) = row.state {
                _ = try fitSize(statusText(amount, names), width: valueWidth, sizes: [12])
            }
        }
        func total(_ rows: [DeductionPeriodRow]) -> Double {
            rows.reduce(0) { result, row in
                if case .amount(let value) = row.state { return result + value }
                if case .status(let value, _) = row.state { return result + (value ?? 0) }
                return result
            }
        }
        for year in years { _ = try fitSize(number(total(grouped[year, default: []])), width: valueWidth, sizes: [12]) }
        _ = try fitSize(number(statement.total), width: width / 2, sizes: [13, 12, 11, 10, 9])
        let count = statement.rows.filter { row in
            if case .amount = row.state { return true }
            if case .status(let value, _) = row.state { return value != nil }
            return false
        }.count
        let renderer = UIGraphicsPDFRenderer(bounds: page)
        try renderer.writePDF(to: url) { context in
            for (pageIndex, pageYears) in pages.enumerated() { autoreleasepool {
                context.beginPage()
                let cg = context.cgContext
                func text(_ value: String, _ rect: CGRect, size: CGFloat = 10, bold: Bool = true, color: UIColor = .black, wrap: Bool = false) {
                    PDFText.draw(value, in: rect.insetBy(dx: 4, dy: 0), font: bold ? ReportFont.bold(size) : ReportFont.regular(size), color: color, align: .center, wrap: wrap)
                }
                func cell(_ rect: CGRect, fill: UIColor? = nil) {
                    if let fill { cg.setFillColor(fill.cgColor); cg.fill(rect) }
                    cg.setStrokeColor(border.cgColor); cg.setLineWidth(ReportTableStyle.borderWidth); cg.stroke(rect)
                }
                let header = CGRect(x: margin, y: 18, width: width, height: 31)
                cell(header, fill: navy)
                text("قيادة شرطة محافظة الأنبار / قسم الحسابات", CGRect(x: header.midX, y: header.minY, width: width / 2, height: header.height), size: 10, color: .white)
                text("كشف استقطاعات", CGRect(x: margin, y: header.minY, width: width / 2, height: header.height), size: 14, color: .white)
                let labelWidth = width * 0.25
                func info(_ label: String, _ value: String, y: CGFloat, x: CGFloat, w: CGFloat, valueSize: CGFloat = 10) {
                    let labelRect = CGRect(x: x + w - labelWidth / 2, y: y, width: labelWidth / 2, height: 25)
                    let valueRect = CGRect(x: x, y: y, width: w - labelWidth / 2, height: 25)
                    cell(labelRect, fill: light); cell(valueRect)
                    text(label, labelRect, size: 9); text(value, valueRect, size: valueSize)
                }
                info("الاسم الكامل", statement.person.name, y: 49, x: margin, w: width, valueSize: (try? fitSize(statement.person.name, width: width * 0.74, sizes: [12, 11, 10, 9, 8])) ?? 8)
                info("الرتبة", statement.rank.isEmpty ? "—" : statement.rank, y: 74, x: margin + width * 0.5, w: width * 0.5, valueSize: (try? fitSize(statement.rank.isEmpty ? "—" : statement.rank, width: width * 0.25)) ?? 7)
                let entityRect = CGRect(x: margin, y: 74, width: width * 0.5, height: 25)
                cell(entityRect, fill: stripe); text("جهة الاستقطاع: " + statement.entity, entityRect, size: (try? fitSize("جهة الاستقطاع: " + statement.entity, width: entityRect.width)) ?? 7)
                let refY: CGFloat = 99
                let refRect = CGRect(x: margin, y: refY, width: width * 0.48, height: referenceHeight)
                let refLabel = CGRect(x: refRect.maxX, y: refY, width: width * 0.15, height: referenceHeight)
                let countRect = CGRect(x: refLabel.maxX, y: refY, width: width * 0.25, height: referenceHeight)
                let countLabel = CGRect(x: countRect.maxX, y: refY, width: width * 0.12, height: referenceHeight)
                cell(refRect, fill: stripe); cell(refLabel, fill: light); cell(countRect, fill: stripe); cell(countLabel, fill: light)
                text(reference, refRect, size: 9, wrap: true); text("رقم وتاريخ الإضبارة", refLabel, size: 8)
                text(String(count), countRect, size: 13); text("عدد الأشهر", countLabel, size: 8)
                let top = refY + referenceHeight + 12
                let headingHeight: CGFloat = 24, rowHeight: CGFloat = 22.1, sumHeight: CGFloat = 24
                for (column, year) in pageYears.enumerated() {
                    let x = page.width - margin - groupWidth * CGFloat(column + 1)
                    let amountHeader = CGRect(x: x, y: top, width: valueWidth, height: headingHeight)
                    let monthHeader = CGRect(x: x + valueWidth, y: top, width: monthWidth, height: headingHeight)
                    cell(amountHeader, fill: navy); cell(monthHeader, fill: navy)
                    text(String(year), amountHeader, size: 12, color: .white); text("الشهر", monthHeader, size: 12, color: .white)
                    let rows = grouped[year, default: []]
                    for month in 1...12 {
                        let y = top + headingHeight + CGFloat(month - 1) * rowHeight
                        let vr = CGRect(x: x, y: y, width: valueWidth, height: rowHeight)
                        let mr = CGRect(x: x + valueWidth, y: y, width: monthWidth, height: rowHeight)
                        let fill: UIColor = month.isMultiple(of: 2) ? stripe : .white
                        cell(vr, fill: fill); cell(mr, fill: fill)
                        text(DeductionCalendar.months[month - 1], mr, size: 12)
                        guard let row = rows.first(where: { $0.month == month }) else { continue }
                        switch row.state {
                        case .amount(let value): text(number(value), vr, size: 12)
                        case .none: text("—", vr, size: 12)
                        case .unavailable: text("غير متحقق", vr, size: 12, color: .systemOrange)
                        case .status(let value, let names):
                            text(statusText(value, names), vr, size: 12)

                        }
                    }
                    let y = top + headingHeight + 12 * rowHeight
                    let amountCell = CGRect(x: x, y: y, width: valueWidth, height: sumHeight)
                    let labelCell = CGRect(x: x + valueWidth, y: y, width: monthWidth, height: sumHeight)
                    cell(amountCell, fill: light); cell(labelCell, fill: light)
                    text("المجموع", labelCell, size: 12)
                    text(number(total(rows)), amountCell, size: 12)
                }
                if pageIndex == pages.count - 1 {
                    let y = top + headingHeight + 12 * rowHeight + sumHeight + 7
                    let totalRect = CGRect(x: margin, y: y, width: width, height: 28)
                    cell(totalRect, fill: navy)
                    text(statement.incomplete ? "مجموع القيود المتوفرة" : "المجموع الكلي للاستقطاعات", CGRect(x: totalRect.midX, y: y, width: width / 2, height: 28), size: 11, color: .white)
                    text(number(statement.total), CGRect(x: margin, y: y, width: width / 2, height: 28), size: (try? fitSize(number(statement.total), width: width / 2, sizes: [13, 12, 11, 10, 9])) ?? 9, color: .white)
                    if statement.incomplete { text("توجد أشهر غير متحققة أو غير مفهومة؛ المجموع للقيود المتوفرة فقط.", CGRect(x: margin, y: y + 29, width: width, height: 18), size: 8, color: .systemOrange) }
                    text("التدقيق", CGRect(x: page.width - margin - 170, y: y + 51, width: 170, height: 18), size: 10)
                }
            } }
        }
    }
}
