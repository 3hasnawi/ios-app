#!/usr/bin/env python3
"""حدّث مصدر AltStore من بيانات ملف IPA المنشور نفسه."""

import argparse
import json
import plistlib
import zipfile
from datetime import datetime, timezone
from pathlib import Path


REPO = "https://github.com/3hasnawi/ios-app"
ICON = "https://raw.githubusercontent.com/3hasnawi/ios-app/main/SalaryApp/Assets.xcassets/AppIcon.appiconset/AppIcon.png"


def update_source(ipa: Path, source_path: Path, tag: str, title: str, date: str) -> None:
    with zipfile.ZipFile(ipa) as archive:
        info = plistlib.loads(archive.read("Payload/SalaryApp.app/Info.plist"))

    privacy = {key: value for key, value in info.items() if key.endswith("UsageDescription")}
    bundle = info["CFBundleIdentifier"]
    version = {
        "version": info["CFBundleShortVersionString"],
        "buildVersion": info["CFBundleVersion"],
        "marketingVersion": title,
        "date": date,
        "downloadURL": f"{REPO}/releases/download/{tag}/SalaryApp.ipa",
        "size": ipa.stat().st_size,
        "minOSVersion": info["MinimumOSVersion"],
    }
    if source_path.exists():
        source = json.loads(source_path.read_text(encoding="utf-8"))
    else:
        source = {
            "name": "تطبيق الرواتب",
            "subtitle": "تطبيق الرواتب والتقارير المحاسبية",
            "description": "المصدر الرسمي لإصدارات تطبيق الرواتب.",
            "iconURL": ICON,
            "website": REPO,
            "tintColor": "#173A46",
            "apps": [{
                "name": "الرواتب",
                "bundleIdentifier": bundle,
                "developerName": "3hasnawi",
                "subtitle": "الرواتب والتقارير المحاسبية",
                "localizedDescription": "إدارة بيانات الرواتب والاستقطاعات، التدقيق، والبحث وتصدير الكشوفات.",
                "iconURL": ICON,
                "tintColor": "#173A46",
                "category": "utilities",
                "versions": [],
                "appPermissions": {"entitlements": [], "privacy": privacy},
            }],
            "news": [],
        }

    app = source["apps"][0]
    if app["bundleIdentifier"] != bundle:
        raise ValueError("Bundle ID in source differs from IPA")
    app["appPermissions"]["privacy"] = privacy
    app["versions"] = [version] + [
        old for old in app["versions"] if old["downloadURL"] != version["downloadURL"]
    ]
    source_path.write_text(json.dumps(source, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--ipa", type=Path, required=True)
    parser.add_argument("--source", type=Path, default=Path("source.json"))
    parser.add_argument("--tag", required=True)
    parser.add_argument("--title", required=True)
    parser.add_argument("--date", default=datetime.now(timezone.utc).isoformat(timespec="seconds"))
    args = parser.parse_args()
    update_source(args.ipa, args.source, args.tag, args.title, args.date)
