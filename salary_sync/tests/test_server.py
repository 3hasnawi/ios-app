import hashlib
import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from fastapi.testclient import TestClient
from app import server


def fake_backup(*, employees=1, months=None):
    months = ["شهر وهمي 2026"] if months is None else months
    manifest = {"version": 1, "createdAt": 815000000.0, "months": months,
                "employees": employees, "deductions": 0, "staff": 0}
    files = {"manifest.json": json.dumps(manifest), "settings.json": "{}",
             "months.jsonl": "\n".join(json.dumps({"name": month, "importedAt": 815000000.0,
                                                   "employeeCount": employees, "deductionCount": 0,
                                                   "summary": None}) for month in months),
             "employees.jsonl": "\n".join(json.dumps({"month": months[0], "name": f"موظف وهمي {i}",
                                                      "rank": "موظف", "workplace": "مركز وهمي",
                                                      "net": 1000, "items": None, "details": None}) for i in range(employees)),
             "deductions.jsonl": "", "staff.jsonl": ""}
    data = io.BytesIO()
    with zipfile.ZipFile(data, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, content in files.items():
            archive.writestr(name, content)
    return data.getvalue()


class BackupStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        server.DATA_DIR = Path(self.temp.name)
        self.admin = TestClient(server.admin)
        self.client = TestClient(server.client)
        self.writer = self.token("writer")
        self.reader = self.token("reader")

    def tearDown(self):
        self.temp.cleanup()

    def token(self, role):
        response = self.admin.post(f"/tokens/{role}")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.headers["cache-control"], "no-store")
        return response.text.split("<code style='overflow-wrap:anywhere'>")[1].split("</code>")[0]

    def auth(self, token):
        return {"Authorization": "Bearer " + token}

    def put(self, data, token=None):
        return self.client.put("/v1/backup", content=data, headers=self.auth(token or self.writer))

    def test_admin_page_is_mobile_and_has_no_month_upload(self):
        page = self.admin.get("/")
        self.assertEqual(page.status_code, 200)
        self.assertIn("viewport-fit=cover", page.text)
        self.assertIn("لم تُحفظ نسخة احتياطية بعد", page.text)
        self.assertNotIn("اختيار ملف JSON", page.text)

    def test_writer_upload_and_reader_download_exact_archive(self):
        data = fake_backup()
        self.assertEqual(self.client.get("/v1/backup/meta").status_code, 401)
        self.assertEqual(self.put(data, self.reader).status_code, 403)
        result = self.put(data)
        self.assertEqual(result.status_code, 200)
        self.assertEqual(result.json()["sha256"], hashlib.sha256(data).hexdigest())
        meta = self.client.get("/v1/backup/meta", headers=self.auth(self.reader)).json()
        self.assertEqual(meta["months"], 1)
        self.assertEqual(meta["employees"], 1)
        self.assertNotIn("filename", meta)
        download = self.client.get("/v1/backup", headers=self.auth(self.reader))
        self.assertEqual(download.status_code, 200)
        self.assertEqual(download.content, data)
        self.assertEqual(download.headers["etag"], '"' + hashlib.sha256(data).hexdigest() + '"')

    def test_invalid_or_empty_archive_preserves_current_backup(self):
        first = fake_backup()
        self.assertEqual(self.put(first).status_code, 200)
        self.assertEqual(self.put(b"not a zip").status_code, 400)
        self.assertEqual(self.put(fake_backup(employees=0, months=[])).status_code, 409)
        self.assertEqual(self.client.get("/v1/backup", headers=self.auth(self.reader)).content, first)
        with server.connection() as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM backups").fetchone()[0], 1)

    def test_revoke_reader_token_keeps_writer_active(self):
        self.assertEqual(self.put(fake_backup()).status_code, 200)
        self.assertEqual(self.admin.post("/tokens/reader/revoke").status_code, 200)
        self.assertEqual(self.client.get("/v1/backup", headers=self.auth(self.reader)).status_code, 403)
        self.assertEqual(self.client.get("/v1/backup", headers=self.auth(self.writer)).status_code, 200)

    def test_archive_with_wrong_entries_is_rejected(self):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as archive:
            archive.writestr("../manifest.json", "{}")
        self.assertEqual(self.put(data.getvalue()).status_code, 400)
        self.assertEqual(self.client.get("/v1/backup", headers=self.auth(self.writer)).status_code, 404)

    def test_manifest_count_must_match_records(self):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as archive:
            for name in server.FILES:
                value = json.dumps({"version": 1, "createdAt": 815000000.0, "months": [],
                                    "employees": 1, "deductions": 0, "staff": 0}) if name == "manifest.json" else "{}" if name == "settings.json" else ""
                archive.writestr(name, value)
        self.assertEqual(self.put(data.getvalue()).status_code, 400)

    def test_latest_revision_and_two_copy_retention(self):
        for count in (1, 2, 3):
            self.assertEqual(self.put(fake_backup(employees=count)).status_code, 200)
        meta = self.client.get("/v1/backup/meta", headers=self.auth(self.reader)).json()
        self.assertEqual(meta["employees"], 3)
        with server.connection() as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM backups").fetchone()[0], 2)
        self.assertEqual(len(list(server.DATA_DIR.glob("backup-*.zip"))), 2)


if __name__ == "__main__":
    unittest.main()
