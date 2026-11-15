"""Compare fictional monthly entries with production deduction and entity-alert rules."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
review = (root / 'SalaryApp/DeductionAudit.swift').read_text().split('extension ReportEngine {')[0]
review = review.replace('import SwiftUI', 'import Foundation').replace('import SwiftData', '')
audit = (root / 'SalaryApp/Audit.swift').read_text().split('extension ReportEngine {')[0]
audit = audit.replace('import SwiftUI', 'import Foundation').replace('import SwiftData', '')
arabic = (root / 'SalaryApp/ArabicText.swift').read_text().split('\nenum Money')[0]
stubs = '''import Foundation
func tr(_ arabic: String, _ english: String) -> String { arabic }
enum Num { static func format(_ value: Int) -> String { String(value) } }
enum Money { static func format(_ value: Double?) -> String { value.map { String($0) } ?? "—" } }
'''
with tempfile.TemporaryDirectory() as temp:
    path = Path(temp)
    for name, text in [('Review.swift', review), ('AuditTypes.swift', audit), ('Arabic.swift', arabic), ('Stubs.swift', stubs),
                       ('EntityAlerts.swift', (root / 'SalaryApp/DeductionEntityAlerts.swift').read_text()),
                       ('Test.swift', (root / 'ci/deduction_entity_alert_test/Test.swift').read_text())]:
        (path / name).write_text(text)
    subprocess.run(['swiftc', *[p.name for p in path.glob('*.swift')], '-o', 'test'], cwd=path, check=True)
    subprocess.run([str(path / 'test')], cwd=path, check=True)
