"""Exercise device authorization without contacting Google or using real credentials."""
import json
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "salary_backup_server"))
import google_drive

with tempfile.TemporaryDirectory() as directory:
    state = Path(directory) / "google.json"
    drive = google_drive.DriveSync({}, state, directory)
    drive.enabled = True
    drive.client_id = "test-client"
    drive.client_secret = "test-secret"
    responses = [
        {"device_code": "private-code", "user_code": "FAKE-CODE", "expires_in": 60, "interval": 5},
        {"refresh_token": "fake-refresh"},
    ]
    with patch.object(google_drive, "request_json", side_effect=responses):
        start = drive.start()
        assert start["user_code"] == "FAKE-CODE" and "device_code" not in start
        assert drive.poll() == {"status": "connected"}
    saved = json.loads(state.read_text())
    assert saved["refresh_token"] == "fake-refresh"
    assert drive.info()["status"] == "connected" and "refresh_token" not in drive.info()
    assert state.stat().st_mode & 0o077 == 0
    print("Google device flow test passed")

# Simulate a Google upload and an update to verify that the same preallocated ID is reused.
import io
import urllib.error

class Response:
    def __init__(self, payload=b"", headers=None, status=200):
        self.payload, self.headers, self.status = payload, headers or {}, status
    def __enter__(self): return self
    def __exit__(self, *args): return False
    def read(self, *args):
        payload, self.payload = self.payload, b""
        return payload
    def getheader(self, key): return self.headers.get(key)

methods = []
created = [False]
def fake_open(req, timeout=None):
    methods.append((req.get_method(), req.full_url))
    if "generateIds" in req.full_url:
        return Response(b'{"ids":["fake-id"]}')
    if req.get_method() == "PATCH" and not created[0]:
        raise urllib.error.HTTPError(req.full_url, 404, "not found", {}, io.BytesIO())
    if req.get_method() == "POST":
        assert json.loads(req.data)["id"] == "fake-id"
        created[0] = True
    return Response(headers={"Location": "https://www.googleapis.com/upload/session?upload_id=fake"})

class Connection:
    def __init__(self, host, timeout): assert host == "www.googleapis.com"
    def request(self, method, path, body, headers):
        assert method == "PUT" and headers["Content-Range"].startswith("bytes 0-")
    def getresponse(self): return Response(b'{"id":"fake-id"}')
    def close(self): pass

with tempfile.TemporaryDirectory() as directory:
    drive = google_drive.DriveSync({}, Path(directory) / "google.json", directory)
    drive.state = {"client_id": "test", "refresh_token": "fake"}
    backup = Path(directory) / "backup.zip"
    backup.write_bytes(b"synthetic-backup")
    with patch.object(google_drive.urllib.request, "urlopen", side_effect=fake_open), patch.object(google_drive.http.client, "HTTPSConnection", Connection):
        with backup.open("rb") as file: assert drive.upload(file, "fake-access") == "fake-id"
        with backup.open("rb") as file: assert drive.upload(file, "fake-access") == "fake-id"
    assert sum("generateIds" in url for _, url in methods) == 1
    assert sum(method == "POST" for method, _ in methods) == 1
    assert json.loads((Path(directory) / "google.json").read_text())["file_id"] == "fake-id"
    print("Google single-file upload test passed")
