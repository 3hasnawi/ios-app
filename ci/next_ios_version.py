"""احسب إصدار الآيفون التالي من آخر إصدار منشور في مصدر AltStore."""
import argparse
import json
import re
from pathlib import Path

# البناء ١٤٠ هو آخر بناء استعمل الترقيم القديم (1.104).
LAST_LEGACY_BUILD = 140
FIRST_VERSION = "1.4"


def next_version(source: dict, build: int) -> str:
    if build <= LAST_LEGACY_BUILD:
        raise ValueError("رقم البناء يسبق سلسلة الإصدارات الجديدة")
    app, = (app for app in source["apps"] if app["bundleIdentifier"] == "com.example.SalaryApp")
    latest = max(app["versions"], key=lambda entry: int(entry["buildVersion"]))
    last_build = int(latest["buildVersion"])
    if last_build > build:
        raise ValueError("يوجد بناء أحدث منشور لهذا التطبيق")
    if last_build <= LAST_LEGACY_BUILD:
        return FIRST_VERSION
    match = re.fullmatch(r"([1-9][0-9]*)\.([0-9])", latest["version"])
    if not match:
        raise ValueError("رقم الإصدار المنشور لا يطابق صيغة الإصدار المختصرة")
    if last_build == build:
        return latest["version"]
    major, minor = map(int, match.groups())
    return f"{major + 1}.0" if minor == 9 else f"{major}.{minor + 1}"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("build", type=int)
    args = parser.parse_args()
    print(next_version(json.loads(args.source.read_text(encoding="utf-8")), args.build))
