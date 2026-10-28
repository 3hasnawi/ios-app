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

spec = importlib.util.spec_from_file_location("backup_server", Path(__file__).resolve().parents[1] / "salary_backup_server/server.py")
server_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server_module)

with tempfile.TemporaryDirectory() as directory:
    server_module.DATA = Path(directory)
    server = server_module.ThreadingHTTPServer(("127.0.0.1", 0), server_module.Handler)
    server.token = "a" * 32
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
    assert len(list(server_module.DATA.glob("*.zip"))) == 2
    status, body = request("GET", "/v1/status", headers={"Authorization": "Bearer " + server.token})
    details = json.loads(body)
    assert status == 200 and details["backup_count"] == 2 and details["backup_bytes"] == len(data) * 2
    assert len(details["backups"]) == 2 and details["disk_total"] >= details["disk_free"] > 0
    assert request("GET", "/v1/backups/latest", headers={"Authorization": "Bearer " + server.token}) == (200, data)
    assert request("GET", "/v1/backups/latest")[0] == 401
    server.shutdown()
    print("Backup server test passed")
