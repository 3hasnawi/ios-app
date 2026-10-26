import io
import json
import tempfile
import unittest
from pathlib import Path
from fastapi.testclient import TestClient
from app import server


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        server.DATA_DIR = Path(self.temp.name)
        self.admin = TestClient(server.admin)
        self.client = TestClient(server.client)
        self.sample = {"format": 1, "month": "شهر تجريبي 2026",
                       "employees": [{"name": "موظف وهمي", "netSalary": 1000}],
                       "deductions": [{"employeeName": "موظف وهمي", "amount": 100}]}

    def tearDown(self):
        self.temp.cleanup()

    def publish(self, value):
        return self.admin.post('/publish', data={"approved": "yes"},
                               files={"file": ("test.json", json.dumps(value, ensure_ascii=False).encode(), "application/json")})

    def test_private_reads_and_atomic_replacement(self):
        self.assertEqual(self.client.get('/v1/months').status_code, 401)
        self.assertEqual(self.publish(self.sample).status_code, 200)
        token_page = self.admin.post('/reader-token').text
        token = token_page.split('<code>')[1].split('</code>')[0]
        headers = {"Authorization": "Bearer " + token}
        listing = self.client.get('/v1/months', headers=headers)
        self.assertEqual(listing.status_code, 200)
        self.assertEqual(listing.json()['months'][0]['employees'], 1)
        downloaded = self.client.get('/v1/months/شهر تجريبي 2026', headers=headers)
        self.assertEqual(downloaded.status_code, 200)
        self.assertEqual(downloaded.json(), self.sample)
        self.assertEqual(downloaded.headers['x-snapshot-version'], '1')
        changed = dict(self.sample, employees=[{"name": "موظف وهمي ٢", "netSalary": 1200}])
        self.assertEqual(self.publish(changed).status_code, 200)
        self.assertEqual(self.client.get('/v1/months/شهر تجريبي 2026', headers=headers).json(), changed)
        self.assertEqual(self.client.get('/v1/months/شهر تجريبي 2026', headers=headers).headers['x-snapshot-version'], '2')
        self.assertEqual(self.client.get('/v1/months', headers={"Authorization": "Bearer wrong"}).status_code, 401)

    def test_bad_upload_does_not_change_current_month(self):
        self.assertEqual(self.publish(self.sample).status_code, 200)
        self.assertEqual(self.publish(dict(self.sample, employees='invalid')).status_code, 400)
        self.assertEqual(self.admin.post('/publish', data={"approved": ""}, files={"file": ("x.json", b'{}')}).status_code, 400)
        with server.connection() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM snapshots').fetchone()[0], 1)


if __name__ == '__main__':
    unittest.main()
