#!/usr/bin/env python3
"""Run actual health grouping rules with fictional data, without an app build."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = (root / 'SalaryApp/HealthCareReport.swift').read_text()
start = source.index('    struct Lists: Sendable {')
end = source.index('    static func applies(', start)
family_start = source.index('    static func isExact(')
family_end = source.index('    /// عدد أفراد الأسرة', family_start)
rules = source[start:end] + source[family_start:family_end]
stubs = '''import Foundation
struct ReportRow: Sendable { let amount: Double }
enum HealthCareReport {
    static let perMember = 3500.0
''' + rules + '}\n'
main = '''import Foundation
let lists = HealthCareReport.Lists(subscribers: [ReportRow(amount: 3500), ReportRow(amount: 7000), ReportRow(amount: 3700), ReportRow(amount: 7200)], others: [])
precondition(lists.regularFamilies == [1: 1, 2: 1])
precondition(lists.irregular.count == 2 && lists.irregularTotal == 10900)
precondition(lists.regularFamilies.values.reduce(0, +) + lists.irregular.count == lists.subscribers.count)
let clean = HealthCareReport.Lists(subscribers: [ReportRow(amount: 3500)], others: [])
precondition(clean.irregular.isEmpty && clean.irregularTotal == 0)
print("Health care summary counts and irregular totals passed")
'''
with tempfile.TemporaryDirectory(prefix='health-summary-') as temp:
    path = Path(temp)
    (path / 'Rules.swift').write_text(stubs)
    (path / 'main.swift').write_text(main)
    subprocess.run(['swiftc', 'Rules.swift', 'main.swift', '-o', 'health-summary'], cwd=path, check=True)
    subprocess.run([str(path / 'health-summary')], cwd=path, check=True)
