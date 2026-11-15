"""Type-check and execute production automatic search on a fictional SwiftData store."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
def source(name):
    return (root / 'SalaryApp' / name).read_text()
def block(text, needle):
    start = text.index(needle)
    opening = text.index('{', start)
    level = 1
    cursor = opening + 1
    while level:
        level += (text[cursor] == '{') - (text[cursor] == '}')
        cursor += 1
    return text[start:cursor]

unified = source('UnifiedSearch.swift')
staff = source('StaffModel.swift').split('/// معلومات آخر استيراد')[0]
types = '\n'.join(block(unified, name) for name in ['struct UnifiedPerson:', 'enum UnifiedScope:', 'nonisolated struct UnifiedHit:', 'enum UnifiedFinder {'])
records = '\n'.join(block(source('ExcelImporter.swift'), 'nonisolated ' + name) for name in [
    'struct ImportedEmployee:', 'struct ImportedDeduction:', 'enum DeductionStatus {', 'struct ImportedExecutionAccount:'])
accounts = '\n'.join(block(source('ExecutionAccounts.swift'), name) for name in ['nonisolated struct SavedExecutionAccount:', 'nonisolated enum ExecutionAccountStore {'])
report = source('Reports.swift').split('struct ReportDetailView:')[0].replace('import SwiftUI','').replace('import UIKit','')
books = block(source('OfficialBooks.swift'), 'nonisolated struct OfficialBook:')
predicate = 'enum StaffResultsList {\n' + block(source('StaffView.swift'), 'nonisolated static func predicate(mode:') + '\n}'
lookup = 'enum EmployeeLookup {\n' + block(source('Shortcuts.swift'), 'nonisolated static func find(_ text:') + '\n}'
stubs = '''import Foundation
import SwiftData
func tr(_ a: String, _ b: String) -> String { a }
typealias ProgressSink = @Sendable (Double) -> Void
struct ImportResult: Sendable { var employees: [ImportedEmployee] = []; var deductions: [ImportedDeduction] = [] }
enum AppSettings { static let isEnglish = false; static let locale = Locale(identifier: "ar_IQ") }
enum Num { static func digits(_ value: String) -> String { value } }
enum StaffSearchMode: String, Sendable { case name, mother, statNumber, unifiedNumber
 var title: String { switch self { case .name: "الاسم"; case .mother: "اسم الأم"; case .statNumber: "الرقم الإحصائي"; case .unifiedNumber: "الرقم الموحد" } }
}
enum StaffInfo { static var importedAt: Date? { Date(timeIntervalSince1970: 1) } }
@MainActor enum SharedModel { static var container: ModelContainer! }
@MainActor final class OfficialBookStore { static let shared = OfficialBookStore(); var books: [OfficialBook] = [] }
enum PerfLog { static func record(_ name: String, since: Date) {} }
'''
with tempfile.TemporaryDirectory() as temp:
    path = Path(temp)
    generated = [('Types.swift', 'import Foundation\nimport SwiftData\n' + types), ('Records.swift', 'import Foundation\n' + records),
                 ('Accounts.swift', 'import Foundation\n' + accounts), ('Books.swift', 'import Foundation\n' + books),
                 ('Predicate.swift', 'import Foundation\nimport SwiftData\n' + predicate + '\n' + lookup),
                 ('Stubs.swift', stubs), ('Staff.swift', staff), ('Reports.swift', report),
                 ('Arabic.swift', source('ArabicText.swift').split('\nenum Money')[0]),
                 ('Test.swift', (root / 'ci/automatic_search_test/Test.swift').read_text())]
    for name, text in generated: (path / name).write_text(text)
    for name in ['Models.swift', 'SmartSearch.swift', 'SearchIndex.swift', 'AutomaticSearchQuery.swift', 'SalaryTrust.swift',
                 'SalaryTrustSearch.swift', 'UnifiedHitMerge.swift', 'SmartPersonResolver.swift']:
        (path / name).write_text(source(name))
    subprocess.run(['swiftc', *[p.name for p in path.glob('*.swift')], '-o', 'test'], cwd=path, check=True)
    subprocess.run([str(path / 'test')], cwd=path, check=True)
