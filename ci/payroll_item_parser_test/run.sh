#!/usr/bin/env bash
# Exercise the actual SheetParser with synthetic rows, without the iOS frameworks.
set -euo pipefail
repo_dir=$(cd "$(dirname "$0")/../.." && pwd)
test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT
python3 - "$repo_dir" "$test_dir" <<'PY'
import pathlib, sys
root, out = map(pathlib.Path, sys.argv[1:])
s = (root / 'SalaryApp/ExcelImporter.swift').read_text()
parser = s[:s.index('nonisolated enum ImportError')] + s[s.index('nonisolated final class SheetParser'):s.index('// MARK: - قراءة XML')]
(out / 'Parser.swift').write_text(parser.replace('import ZIPFoundation', '').replace('nonisolated ', ''))
s = (root / 'SalaryApp/ArabicText.swift').read_text()
(out / 'Arabic.swift').write_text(s[:s.index('\nenum Money')].replace('nonisolated ', ''))
s = (root / 'SalaryApp/Reports.swift').read_text()
(out / 'ReportRow.swift').write_text('import Foundation\n' + s[s.index('nonisolated struct ReportRow'):s.index('/// حسابات التقارير في الخلفية.')].replace('nonisolated ', ''))
PY
cp "$repo_dir/ci/payroll_item_parser_test/main.swift" "$repo_dir/ci/payroll_item_parser_test/Stubs.swift" "$test_dir/"
docker run --rm --network none -v "$test_dir:/tests" -w /tests swift:6.0 bash -c 'swiftc Arabic.swift Stubs.swift Parser.swift ReportRow.swift main.swift -o /tests/run && /tests/run'
