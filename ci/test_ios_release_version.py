import json
import plistlib
import tempfile
import unittest
import zipfile
from pathlib import Path

from next_ios_version import next_version
from update_altstore_source import update_source


class ReleaseVersionTests(unittest.TestCase):
    def test_numbering_and_published_metadata(self):
        bundle = "com.example.SalaryApp"
        source = {"apps": [{"bundleIdentifier": bundle, "appPermissions": {},
                            "versions": [{"version": "1.104", "buildVersion": "140",
                                          "downloadURL": "previous.ipa"}]}]}
        with tempfile.TemporaryDirectory() as directory:
            feed = Path(directory) / "source.json"
            ipa = Path(directory) / "SalaryApp.ipa"
            feed.write_text(json.dumps(source))
            for build, expected in ((142, "1.4"), (144, "1.5"), (145, "1.6"),
                                    (146, "1.7"), (147, "1.8"), (148, "1.9"), (149, "2.0")):
                version = next_version(source, build)
                self.assertEqual(version, expected)
                with zipfile.ZipFile(ipa, "w") as archive:
                    archive.writestr("Payload/SalaryApp.app/Info.plist", plistlib.dumps({
                        "CFBundleIdentifier": bundle, "CFBundleShortVersionString": version,
                        "CFBundleVersion": str(build), "MinimumOSVersion": "26.0",
                    }))
                previous = source["apps"][0]["versions"]
                update_source(ipa, feed, f"build-{build}", version, "2026-11-27")
                source = json.loads(feed.read_text())
                latest, *history = source["apps"][0]["versions"]
                self.assertEqual(history, previous)
                self.assertEqual((latest["version"], latest["marketingVersion"], latest["buildVersion"]),
                                 (expected, expected, str(build)))
                self.assertEqual(next_version(source, build), version)
                with self.assertRaises(ValueError):
                    next_version(source, build - 1)


if __name__ == "__main__":
    unittest.main()
