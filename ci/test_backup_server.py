"""Smoke test: fake backups only; no Home Assistant or simulator required."""
import hashlib
import http.client
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import threading
import zipfile
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "salary_backup_server"))

spec = importlib.util.spec_from_file_location("backup_server", Path(__file__).resolve().parents[1] / "salary_backup_server/server.py")
server_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server_module)

with tempfile.TemporaryDirectory() as directory:
    server_module.DATA = Path(directory)
    server = server_module.ThreadingHTTPServer(("127.0.0.1", 0), server_module.Handler)
    server.token = "a" * 32
    server.drive = server_module.DriveSync({}, Path(directory) / "google.json", server_module.DATA)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    def request(method, path, body=None, headers=None):
        conn = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=10)
        conn.request(method, path, body, headers or {})
        response = conn.getresponse()
        result = response.status, response.read()
        conn.close()
        return result
    sample = io.BytesIO()
    with zipfile.ZipFile(sample, "w") as archive:
        archive.writestr("manifest.json", json.dumps({"version": 4, "months": ["test"], "employees": 1}))
    data = sample.getvalue()
    headers = {"Authorization": "Bearer " + server.token, "X-Content-SHA256": hashlib.sha256(data).hexdigest()}
    assert request("GET", "/")[0] == 200
    assert request("GET", "/v1/status")[0] == 401
    assert request("POST", "/v1/backups", data, {**headers, "Authorization": "Bearer wrong"})[0] == 401
    assert request("POST", "/v1/backups", data, {**headers, "X-Content-SHA256": "0" * 64})[0] == 400
    assert request("POST", "/v1/backups", b"bad", {**headers, "X-Content-SHA256": hashlib.sha256(b"bad").hexdigest()})[0] == 400
    assert request("POST", "/v1/backups", data, headers)[0] == 201
    assert request("POST", "/v1/backups", data, headers)[0] == 201
    assert len(list(server_module.DATA.glob("*.zip"))) == 1
    # النسخة التي لا تتضمن الشهر القديم لا تستبدل الملف الموجود.
    missing = io.BytesIO()
    with zipfile.ZipFile(missing, "w") as archive:
        archive.writestr("manifest.json", json.dumps({"version": 4, "months": ["other"], "employees": 1}))
    missing_data = missing.getvalue()
    missing_headers = {**headers, "X-Content-SHA256": hashlib.sha256(missing_data).hexdigest()}
    assert request("POST", "/v1/backups", missing_data, missing_headers)[0] == 400
    assert (server_module.DATA / "backup-current.zip").read_bytes() == data
    # ترحيل النسخ السابقة يتم بعد نجاح النسخة الجديدة فقط.
    (server_module.DATA / "backup-legacy.zip").write_bytes(data)
    assert request("POST", "/v1/backups", data, headers)[0] == 201
    assert len(list(server_module.DATA.glob("backup-*.zip"))) == 1
    status, body = request("GET", "/v1/status", headers={"Authorization": "Bearer " + server.token})
    details = json.loads(body)
    assert status == 200 and details["backup_count"] == 1 and details["backup_bytes"] == len(data)
    assert len(details["backups"]) == 1 and details["disk_total"] >= details["disk_free"] > 0
    assert request("GET", "/v1/backups/latest", headers={"Authorization": "Bearer " + server.token}) == (200, data)
    assert request("GET", "/v1/backups/latest")[0] == 401
    # لا يُفعّل استقبال التعديلات بالمراجعات افتراضياً، ولا يغيّر سلوك الآيفون الحالي.
    base = hashlib.sha256(data).hexdigest()
    conditional = {**headers, "X-Base-SHA256": base}
    assert request("POST", "/v2/backups", data, conditional)[0] == 403
    server.edit_sync_enabled = True
    assert request("POST", "/v2/backups", data, {**conditional, "X-Base-SHA256": "0" * 64})[0] == 409
    changed = io.BytesIO()
    with zipfile.ZipFile(changed, "w") as archive:
        archive.writestr("manifest.json", json.dumps({"version": 4, "months": ["test"], "employees": 1}))
        archive.writestr("employees.jsonl", "{\"name\":\"test only\"}\n")
    changed_data = changed.getvalue()
    changed_headers = {**conditional, "X-Content-SHA256": hashlib.sha256(changed_data).hexdigest()}
    assert request("POST", "/v2/backups", changed_data, changed_headers)[0] == 201
    assert request("POST", "/v2/backups", data, conditional)[0] == 409  # جهاز لديه نسخة قديمة
    assert request("POST", "/v1/backups", data, headers)[0] == 409  # لا يسمح لتطبيق قديم بمحو التعديل
    conn = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=10)
    conn.request("GET", "/v2/backups/latest", headers={"Authorization": "Bearer " + server.token})
    response = conn.getresponse()
    assert response.status == 200 and response.getheader("X-Revision-SHA256") == hashlib.sha256(changed_data).hexdigest()
    assert response.read() == changed_data
    conn.close()
    assert (server_module.DATA / "backup-current.zip").read_bytes() == changed_data
    # يحمي القفل طلبين متزامنين لهما المراجعة نفسها: واحد فقط ينجح.
    other = io.BytesIO()
    with zipfile.ZipFile(other, "w") as archive:
        archive.writestr("manifest.json", json.dumps({"version": 4, "months": ["test"], "employees": 1}))
        archive.writestr("employees.jsonl", "{\"name\":\"different fictional record\"}\n")
    other_data = other.getvalue()
    revision = hashlib.sha256(changed_data).hexdigest()
    outcomes = []
    def concurrent_upload(payload):
        h = {"Authorization": "Bearer " + server.token, "X-Content-SHA256": hashlib.sha256(payload).hexdigest(), "X-Base-SHA256": revision}
        outcomes.append(request("POST", "/v2/backups", payload, h)[0])
    threads = [threading.Thread(target=concurrent_upload, args=(payload,)) for payload in (data, other_data)]
    for thread in threads: thread.start()
    for thread in threads: thread.join()
    assert sorted(outcomes) == [201, 409]
    server.edit_sync_enabled = False
    assert request("POST", "/v1/backups", data, headers)[0] == 409
    server.shutdown()
    print("Backup server test passed")
