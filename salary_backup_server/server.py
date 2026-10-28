import hashlib
import hmac
import json
import os
import re
import shutil
import tempfile
import threading
import zipfile
from google_drive import DriveSync
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

DATA = Path(os.environ.get("SALARY_BACKUP_DIR", "/data/backups"))
MAX_SIZE = 2 * 1024 * 1024 * 1024
LOCK = threading.Lock()
DASHBOARD = Path(__file__).with_name("dashboard.html")


def valid_archive(path):
    with zipfile.ZipFile(path) as archive:
        info = archive.getinfo("manifest.json")
        if info.file_size > 1000000:
            raise ValueError("manifest too large")
        manifest = json.loads(archive.read(info))
        if not isinstance(manifest, dict) or not manifest.get("version"):
            raise ValueError("invalid manifest")
        if not manifest.get("months") and not any(manifest.get(k, 0) for k in ("employees", "deductions", "staff", "accounts", "books")):
            raise ValueError("empty backup")
        if archive.testzip() is not None:
            raise ValueError("corrupt backup")
        return manifest


def ensure_no_missing_data(current, previous):
    if not set(previous.get("months", [])).issubset(set(current.get("months", []))):
        raise ValueError("previous backup contains missing months")
    for key in ("employees", "deductions", "staff", "accounts", "books"):
        if (previous.get(key) or 0) > 0 and not (current.get(key) or 0):
            raise ValueError("previous backup contains missing data")


class Handler(BaseHTTPRequestHandler):
    server_version = "SalaryBackup/1"
    def log_message(self, format, *args):
        pass
    def response(self, status, obj):
        body = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def authorized(self):
        value = self.headers.get("Authorization", "")
        try:
            return hmac.compare_digest(value, "Bearer " + self.server.token)
        except TypeError:
            return False
    def do_GET(self):
        if self.path in ("/", "/index.html"):
            content = DASHBOARD.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)
            return
        if not self.authorized():
            return self.response(401, {"error": "unauthorized"})
        if self.path == "/v1/status":
            try:
                DATA.mkdir(parents=True, exist_ok=True, mode=0o700)
                disk = shutil.disk_usage(DATA)
                backups = []
                for file in DATA.glob("backup-*.zip"):
                    try:
                        stat = file.stat()
                    except FileNotFoundError:
                        continue
                    backups.append({"id": file.name, "size": stat.st_size,
                                    "created_at": datetime.fromtimestamp(stat.st_mtime, timezone.utc).isoformat()})
                backups.sort(key=lambda b: b["created_at"], reverse=True)
                return self.response(200, {"service": "salary-backup", "version": 1,
                    "disk_total": disk.total, "disk_used": disk.used, "disk_free": disk.free,
                    "backup_count": len(backups), "backup_bytes": sum(b["size"] for b in backups),
                    "backups": backups[:200], "google_drive": self.server.drive.info()})
            except OSError:
                return self.response(503, {"error": "storage unavailable"})
        if self.path != "/v1/backups/latest":
            return self.response(404, {"error": "not found"})
        files = sorted(DATA.glob("backup-*.zip"), key=lambda p: p.stat().st_mtime_ns, reverse=True)
        if not files:
            return self.response(404, {"error": "no backups"})
        with files[0].open("rb") as source:
            self.send_response(200)
            self.send_header("Content-Type", "application/zip")
            self.send_header("Content-Length", str(os.fstat(source.fileno()).st_size))
            self.end_headers()
            while chunk := source.read(1024 * 1024):
                self.wfile.write(chunk)
    def do_POST(self):
        if not self.authorized():
            self.close_connection = True
            return self.response(401, {"error": "unauthorized"})
        if self.path in ("/v1/google/start", "/v1/google/poll"):
            try:
                result = self.server.drive.start() if self.path.endswith("/start") else self.server.drive.poll()
                return self.response(200, result)
            except (ValueError, OSError, KeyError):
                return self.response(400, {"error": "google_authorization_failed"})
        if self.path != "/v1/backups":
            self.close_connection = True
            return self.response(404, {"error": "not found"})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            digest = self.headers.get("X-Content-SHA256", "")
            if length <= 0 or length > MAX_SIZE or not re.fullmatch(r"[0-9a-f]{64}", digest):
                self.close_connection = True
                return self.response(400, {"error": "invalid length or checksum"})
            DATA.mkdir(parents=True, exist_ok=True, mode=0o700)
            fd, name = tempfile.mkstemp(prefix=".upload-", dir=DATA)
            try:
                sha = hashlib.sha256()
                remaining = length
                with os.fdopen(fd, "wb") as out:
                    while remaining:
                        chunk = self.rfile.read(min(1024 * 1024, remaining))
                        if not chunk:
                            raise ValueError("incomplete upload")
                        out.write(chunk)
                        sha.update(chunk)
                        remaining -= len(chunk)
                    out.flush()
                    os.fsync(out.fileno())
                if not hmac.compare_digest(sha.hexdigest(), digest):
                    raise ValueError("checksum mismatch")
                manifest = valid_archive(name)
                filename = "backup-current.zip"
                with LOCK:
                    previous = list(DATA.glob("backup-*.zip"))
                    # لا نستبدل نسخة سليمة ببيانات ينقصها شهر أو قسم كامل.
                    for old in previous:
                        ensure_no_missing_data(manifest, valid_archive(old))
                    os.replace(name, DATA / filename)
                    directory = os.open(DATA, os.O_RDONLY)
                    try:
                        os.fsync(directory)
                    finally:
                        os.close(directory)
                    # النسخ القديمة تُحذف بعد تأكيد استبدال النسخة الحالية فقط.
                    for old in previous:
                        if old.name != filename:
                            old.unlink()
                self.server.drive.event.set()
                return self.response(201, {"id": filename, "size": length, "sha256": digest})
            finally:
                if os.path.exists(name):
                    os.unlink(name)
        except (OSError, ValueError, KeyError, zipfile.BadZipFile, json.JSONDecodeError):
            self.close_connection = True
            return self.response(400, {"error": "invalid or incomplete backup"})


def main():
    with open("/data/options.json", encoding="utf-8") as f:
        token = json.load(f).get("token", "")
    if not isinstance(token, str) or len(token) < 32 or not token.isascii() or any(c.isspace() for c in token):
        raise SystemExit("Set a unique token of at least 32 ASCII characters in add-on configuration")
    httpd = ThreadingHTTPServer(("0.0.0.0", 8765), Handler)
    httpd.token = token
    httpd.drive = DriveSync(json.load(open("/data/options.json", encoding="utf-8")), "/data/google-drive.json", DATA)
    httpd.serve_forever()

if __name__ == "__main__":
    main()
