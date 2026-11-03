"""Smoke test: fake backups only; no Home Assistant or simulator required."""
import hashlib
import base64
import http.client
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import threading
import zipfile
import sys
import uuid
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "salary_backup_server"))

spec = importlib.util.spec_from_file_location("backup_server", Path(__file__).resolve().parents[1] / "salary_backup_server/server.py")
server_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server_module)

# النسخة القديمة لا تستبدل نسخة تتضمن حقول العقود المحمية.
try:
    server_module.ensure_no_missing_data({"version": 5, "months": ["test"], "employees": 1}, {"version": 6, "months": ["test"], "employees": 1})
    raise AssertionError("legacy backup replaced v6")
except ValueError:
    pass
server_module.ensure_no_missing_data({"version": 6, "months": ["test"], "employees": 1}, {"version": 6, "months": ["test"], "employees": 1})
# صيغة مستقبلية: لا يُسمح لنسخة v6 باستبدال v7 حتى إن تطابقت الأعداد.
try:
    server_module.ensure_no_missing_data({"version": 6, "months": ["test"], "employees": 1}, {"version": 7, "months": ["test"], "employees": 1})
    raise AssertionError("legacy backup replaced v7")
except ValueError:
    pass
server_module.ensure_no_missing_data({"version": 7, "months": ["test"], "employees": 1}, {"version": 7, "months": ["test"], "employees": 1})

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
    # فحص الاتصال لا يرفع ملفاً؛ حتى رد عدم دعم HEAD يعني أن السيرفر وصل.
    assert request("HEAD", "/")[0] in (200, 405, 501)
    assert not list(server_module.DATA.glob("*.zip"))
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
    device_id = str(uuid.uuid4())
    device_headers = {**headers, "X-Backup-Device-ID": device_id,
                      "X-Backup-Device-Name": base64.b64encode("مكتب تجريبي".encode()).decode(),
                      "X-Backup-Device-Platform": "windows"}
    code, body = request("POST", "/v1/backups", data, device_headers)
    receipt = json.loads(body)
    assert code == 201 and uuid.UUID(receipt["upload_id"])
    _, body = request("GET", "/v1/status", headers={"Authorization": "Bearer " + server.token})
    entry = json.loads(body)["backups"][0]
    assert entry["upload_id"] == receipt["upload_id"] and entry["uploaded_at"] == receipt["uploaded_at"]
    assert entry["device"] == {"id": device_id, "name": "مكتب تجريبي", "platform": "windows"}
    assert request("POST", "/v1/backups", data, {**device_headers, "X-Backup-Device-ID": "invalid"})[0] == 400
    assert request("POST", "/v1/backups", data, {**headers, "X-Backup-Device-ID": device_id})[0] == 400
    _, body = request("GET", "/v1/status", headers={"Authorization": "Bearer " + server.token})
    assert json.loads(body)["backups"][0]["upload_id"] == receipt["upload_id"]
    # فقد الربط بين بيانات الجهاز والملف لا ينسب الملف إلى جهاز خطأ.
    metadata_path = server_module.DATA / server_module.METADATA
    metadata = json.loads(metadata_path.read_text())
    metadata["file_stamp"][1] += 1
    metadata_path.write_text(json.dumps(metadata))
    _, body = request("GET", "/v1/status", headers={"Authorization": "Bearer " + server.token})
    assert "device" not in json.loads(body)["backups"][0]
    # الأجهزة القديمة تبقى قادرة على الرفع ويكون المصدر غير معروف.
    code, body = request("POST", "/v1/backups", data, headers)
    assert code == 201 and json.loads(body)["upload_id"] != receipt["upload_id"]
    _, body = request("GET", "/v1/status", headers={"Authorization": "Bearer " + server.token})
    assert json.loads(body)["backups"][0]["device"] is None
    # رفعان متنافسان قد يستبدلان بعضهما في v1، لكن المصدر لا يجوز أن يُنسب للملف الخطأ.
    second = io.BytesIO()
    with zipfile.ZipFile(second, "w") as archive:
        archive.writestr("manifest.json", json.dumps({"version": 4, "months": ["test"], "employees": 1}))
        archive.writestr("fixture.txt", "fictional second device")
    other_data = second.getvalue()
    other_id = str(uuid.uuid4())
    uploads = [(data, device_id), (other_data, other_id)]
    results = []
    def upload_with_identity(item):
        payload, identity = item
        h = {"Authorization": "Bearer " + server.token, "X-Content-SHA256": hashlib.sha256(payload).hexdigest(),
             "X-Backup-Device-ID": identity, "X-Backup-Device-Name": base64.b64encode(b"fictional").decode(),
             "X-Backup-Device-Platform": "windows"}
        results.append(request("POST", "/v1/backups", payload, h)[0])
    threads = [threading.Thread(target=upload_with_identity, args=(item,)) for item in uploads]
    for thread in threads: thread.start()
    for thread in threads: thread.join()
    assert results == [201, 201]
    _, body = request("GET", "/v1/status", headers={"Authorization": "Bearer " + server.token})
    published = (server_module.DATA / "backup-current.zip").read_bytes()
    expected_device = next(identity for payload, identity in uploads if payload == published)
    assert json.loads(body)["backups"][0]["device"]["id"] == expected_device
    assert request("POST", "/v1/backups", data, headers)[0] == 201
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
    # تحقق من الحماية عبر HTTP: نسخة v5 لا تستبدل نسخة v6 حتى مع بصمة المراجعة الصحيحة.
    prior = (server_module.DATA / "backup-current.zip").read_bytes()
    protected = io.BytesIO()
    with zipfile.ZipFile(protected, "w") as archive:
        archive.writestr("manifest.json", json.dumps({"version": 6, "months": ["test"], "employees": 1}))
    protected_data = protected.getvalue()
    auth = {"Authorization": "Bearer " + server.token}
    protected_headers = {**auth, "X-Content-SHA256": hashlib.sha256(protected_data).hexdigest(), "X-Base-SHA256": hashlib.sha256(prior).hexdigest()}
    assert request("POST", "/v2/backups", protected_data, protected_headers)[0] == 201
    downgrade_headers = {**auth, "X-Content-SHA256": hashlib.sha256(data).hexdigest(), "X-Base-SHA256": hashlib.sha256(protected_data).hexdigest()}
    assert request("POST", "/v2/backups", data, downgrade_headers)[0] == 400
    assert (server_module.DATA / "backup-current.zip").read_bytes() == protected_data
    future = io.BytesIO()
    with zipfile.ZipFile(future, "w") as archive:
        archive.writestr("manifest.json", json.dumps({"version": 7, "months": ["test"], "employees": 1}))
    future_data = future.getvalue()
    future_headers = {**auth, "X-Content-SHA256": hashlib.sha256(future_data).hexdigest(), "X-Base-SHA256": hashlib.sha256(protected_data).hexdigest()}
    assert request("POST", "/v2/backups", future_data, future_headers)[0] == 201
    old_headers = {**auth, "X-Content-SHA256": hashlib.sha256(protected_data).hexdigest(), "X-Base-SHA256": hashlib.sha256(future_data).hexdigest()}
    assert request("POST", "/v2/backups", protected_data, old_headers)[0] == 400
    assert (server_module.DATA / "backup-current.zip").read_bytes() == future_data
    assert json.loads(request("GET", "/v1/status", headers=auth)[1])["backup_format_guard"] is True
    server.edit_sync_enabled = False
    assert request("POST", "/v1/backups", data, headers)[0] == 409
    server.shutdown()
    # خدمة v1 الفعلية ترفض أيضاً استبدال نسخة v6 بنسخة من عميل قديم.
    server_module.DATA = Path(directory) / "v1-guard"
    server_module.DATA.mkdir()
    guarded_server = server_module.ThreadingHTTPServer(("127.0.0.1", 0), server_module.Handler)
    guarded_server.token = server.token
    guarded_server.drive = server.drive
    threading.Thread(target=guarded_server.serve_forever, daemon=True).start()
    def guarded_post(payload):
        conn = http.client.HTTPConnection("127.0.0.1", guarded_server.server_port, timeout=10)
        conn.request("POST", "/v1/backups", payload, {"Authorization": "Bearer " + guarded_server.token, "X-Content-SHA256": hashlib.sha256(payload).hexdigest()})
        response = conn.getresponse(); code = response.status; response.read(); conn.close(); return code
    assert guarded_post(protected_data) == 201
    assert guarded_post(data) == 400
    assert (server_module.DATA / "backup-current.zip").read_bytes() == protected_data
    guarded_server.shutdown()
    print("Backup server test passed")
