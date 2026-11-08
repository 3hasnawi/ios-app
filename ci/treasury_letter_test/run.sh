#!/usr/bin/env bash
set -euo pipefail
repo_dir=$(cd "$(dirname "$0")/../.." && pwd)
test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT
python3 - "$repo_dir" "$test_dir" <<'PY'
import pathlib, sys
root, out = map(pathlib.Path, sys.argv[1:])
source = (root / 'SalaryApp/ArabicText.swift').read_text()
(out / 'Arabic.swift').write_text(source[:source.index('\nenum Money')].replace('nonisolated ', ''))
source = (root / 'SalaryApp/Reports.swift').read_text()
(out / 'ReportRow.swift').write_text('import Foundation\n' + source[source.index('nonisolated struct ReportRow'):source.index('/// حسابات التقارير في الخلفية.')].replace('nonisolated ', ''))
source = (root / 'SalaryApp/TreasuryLetterRules.swift').read_text()
(out / 'TreasuryRules.swift').write_text(source.replace('nonisolated ', ''))
PY
cp "$repo_dir/ci/treasury_letter_test/main.swift" "$repo_dir/ci/treasury_letter_test/Stubs.swift" "$test_dir/"
if command -v swiftc >/dev/null 2>&1; then
  (cd "$test_dir" && swiftc Arabic.swift ReportRow.swift Stubs.swift TreasuryRules.swift main.swift -o test && ./test)
else
  docker run --rm --network none -v "$test_dir:/tests" -w /tests swift:6.0 bash -c 'swiftc Arabic.swift ReportRow.swift Stubs.swift TreasuryRules.swift main.swift -o /tests/test && /tests/test'
fi
