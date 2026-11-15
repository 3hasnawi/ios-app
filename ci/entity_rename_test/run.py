"""Exercise production rename rules and real SwiftData transactions using fictional data."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
records = (root / 'SalaryApp/ExcelImporter.swift').read_text().split('/// تحفظ الحالة')[0].replace('import ZIPFoundation', '')
arabic = (root / 'SalaryApp/ArabicText.swift').read_text().split('\nenum Money')[0]
stubs = '''import Foundation
func tr(_ a: String, _ b: String) -> String { a }
typealias ProgressSink = @Sendable (Double) -> Void
struct ImportResult: Sendable { var employees: [ImportedEmployee] = []; var deductions: [ImportedDeduction] = [] }
'''
with tempfile.TemporaryDirectory() as temp:
    directory = Path(temp)
    for name, source in [('Models.swift', (root / 'SalaryApp/Models.swift').read_text()), ('Records.swift', records), ('Arabic.swift', arabic), ('Stubs.swift', stubs), ('Test.swift', (root / 'ci/entity_rename_test/Test.swift').read_text())]:
        (directory / name).write_text(source)
    for name in ['EntityRenamePlan.swift', 'DeductionEntityEditor.swift']:
        (directory / name).write_text((root / 'SalaryApp' / name).read_text())
    subprocess.run(['swiftc', *[file.name for file in directory.glob('*.swift')], '-o', 'test'], cwd=directory, check=True)
    subprocess.run([str(directory / 'test')], cwd=directory, check=True)
