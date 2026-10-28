"""Static checks for the widget extension and App Group configuration."""
from pathlib import Path
import plistlib
import re
import yaml

root = Path(__file__).resolve().parents[1]
project = yaml.safe_load((root / "project.yml").read_text())
app, widget = project["targets"]["SalaryApp"], project["targets"]["SalaryWidget"]
assert widget["type"] == "app-extension"
assert any(item.get("target") == "SalaryWidget" and item.get("embed") for item in app["dependencies"])
assert widget["settings"]["base"]["PRODUCT_BUNDLE_IDENTIFIER"].startswith(app["settings"]["base"]["PRODUCT_BUNDLE_IDENTIFIER"] + ".")
entitlements = []
for path in ("SalaryApp/SalaryApp.entitlements", "WidgetExtension/SalaryWidget.entitlements"):
    entitlements.append(plistlib.loads((root / path).read_bytes())["com.apple.security.application-groups"])
assert entitlements[0] == entitlements[1] and len(entitlements[0]) == 1
assert entitlements[0][0] in (root / "WidgetShared/WidgetModels.swift").read_text()
source = (root / "WidgetExtension/SalaryWidget.swift").read_text()
enum = source.split("enum SalaryWidgetMetric:", 1)[1].split("static let typeDisplayRepresentation", 1)[0]
cases = set()
for line in enum.splitlines():
    line = line.strip()
    if line.startswith("case "):
        cases.update(item.strip() for item in line[5:].split(","))
representations = set(re.findall(r"\.(\w+)\s*:", source.split("static let caseDisplayRepresentations", 1)[1].split("\n}", 1)[0]))
switch = source.split("switch metric {", 1)[1].split("\n    }", 1)[0]
handled = set(re.findall(r"case\s+\.(\w+)", switch))
assert cases == representations == handled, (cases - representations, cases - handled, handled - cases)
assert ".privacySensitive()" in source
assert ".systemSmall" in source and ".systemMedium" in source and ".systemLarge" in source
print(f"Widget configuration OK: {len(cases)} choices, three sizes, matching App Group")
