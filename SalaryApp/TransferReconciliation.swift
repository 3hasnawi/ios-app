import Foundation

/// هوية المصدر تميّز جهة التنفيذ عن أي بند رواتب له اسم مشابه.
nonisolated struct TransferReconcileLine: Sendable, Hashable {
    let sourceID: String
    let title: String
    let amount: Int64
}

nonisolated enum TransferReconcileTarget: Sendable {
    case grouped
    case individual
}

nonisolated struct TransferReconcileIssue: Sendable, Identifiable {
    enum Kind: String, Sendable {
        case missingBook, missingFile, missingEntityInBook, missingEntityInFile
        case unexpectedEntityInBook, unexpectedEntityInFile, bookAmount, fileAmount
        case duplicateSource, invalidData, employeeCount, employeeNames, employeeAmounts, countChanged
    }
    let id = UUID()
    let kind: Kind
    let sourceID: String?
    let title: String
}

/// تقارن لقطة الشهر بمحتوى ملف وكتاب من نفس عملية التصدير، دون إجراء أي تعديل على السجلات.
nonisolated enum TransferReconciliation {
    static func check(expected: [TransferReconcileLine], book: [TransferReconcileLine]?,
                      file: [TransferReconcileLine]?, target: TransferReconcileTarget) -> [TransferReconcileIssue] {
        var issues: [TransferReconcileIssue] = []
        func issue(_ kind: TransferReconcileIssue.Kind, _ line: TransferReconcileLine? = nil) {
            issues.append(TransferReconcileIssue(kind: kind, sourceID: line?.sourceID, title: line?.title ?? ""))
        }
        func indexed(_ rows: [TransferReconcileLine]) -> [String: TransferReconcileLine] {
            var result: [String: TransferReconcileLine] = [:]
            for row in rows {
                guard !row.sourceID.isEmpty, !row.title.isEmpty, row.amount >= 0 else { issue(.invalidData, row); continue }
                guard result[row.sourceID] == nil else { issue(.duplicateSource, row); continue }
                result[row.sourceID] = row
            }
            return result
        }
        let reference = indexed(expected)
        if case .individual = target, book == nil {
            issue(.missingBook)
            if file != nil { issue(.invalidData) }
            return issues
        }
        if expected.isEmpty { issue(.invalidData) }
        if case .individual = target, file != nil { issue(.invalidData) }
        if book == nil { issue(.missingBook) }
        if case .grouped = target, file == nil { issue(.missingFile) }
        func compare(_ rows: [TransferReconcileLine]?, missing: TransferReconcileIssue.Kind,
                     unexpected: TransferReconcileIssue.Kind, amount: TransferReconcileIssue.Kind) {
            guard let rows else { return }
            let actual = indexed(rows)
            for original in expected where reference[original.sourceID] != nil {
                guard let entry = actual[original.sourceID] else { issue(missing, original); continue }
                if original.amount != entry.amount { issue(amount, original) }
            }
            for entry in rows where reference[entry.sourceID] == nil { issue(unexpected, entry) }
        }
        compare(book, missing: .missingEntityInBook, unexpected: .unexpectedEntityInBook, amount: .bookAmount)
        if case .grouped = target {
            compare(file, missing: .missingEntityInFile, unexpected: .unexpectedEntityInFile, amount: .fileAmount)
        }
        return issues
    }
}

nonisolated struct TransferReconcileEmployee: Sendable {
    let name: String
    let amount: Int64
}

/// يقارن تكرار كل اسم ومبالغه دون دمج السجلات المتعددة للشخص نفسه.
nonisolated enum TransferEmployeeReconciliation {
    static func check(expected: [TransferReconcileEmployee], book: [TransferReconcileEmployee],
                      previousCount: Int? = nil) -> [TransferReconcileIssue] {
        var issues: [TransferReconcileIssue] = []
        func issue(_ kind: TransferReconcileIssue.Kind) {
            issues.append(TransferReconcileIssue(kind: kind, sourceID: nil, title: ""))
        }
        func index(_ rows: [TransferReconcileEmployee]) -> [String: [Int64]]? {
            var result: [String: [Int64]] = [:]
            for row in rows {
                let key = ArabicText.matchKey(row.name)
                guard !key.isEmpty, row.amount >= 0 else { return nil }
                result[key, default: []].append(row.amount)
            }
            return result.mapValues { $0.sorted() }
        }
        guard let source = index(expected), let written = index(book) else { issue(.invalidData); return issues }
        if expected.count != book.count { issue(.employeeCount) }
        // عدد الأشخاص مستقل عن عدد السطور؛ قد يملك الشخص أكثر من قيد.
        if let previousCount, previousCount >= 0, source.count != previousCount { issue(.countChanged) }
        let sourceCounts = source.mapValues(\.count)
        let bookCounts = written.mapValues(\.count)
        if sourceCounts != bookCounts { issue(.employeeNames) }
        if source.keys.contains(where: { key in
            guard let other = written[key], source[key]?.count == other.count else { return false }
            return source[key] != other
        }) { issue(.employeeAmounts) }
        return issues
    }
}
