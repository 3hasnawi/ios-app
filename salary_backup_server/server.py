import hashlib
import hmac
import json
import os
import re
import secrets
import tempfile
import threading
import zipfile
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

DATA = Path(os.environ.get("SALARY_BACKUP_DIR", "/data/backups"))
MAX_SIZE = 2 * 1024 * 1024 * 1024
LOCK = threading.Lock()


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


class Handler(BaseHTTPRequestHandler):
    server_version = "SalaryBackup/1"
    def log_message(self, format, *args):
        pass
    def response(self, status, obj):
        body = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
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
        if not self.authorized():
            return self.response(401, {"error": "unauthorized"})
        if self.path == "/v1/status":
            return self.response(200, {"service": "salary-backup", "version": 1})
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
                valid_archive(name)
                stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
                filename = f"backup-{stamp}-{secrets.token_hex(4)}.zip"
                with LOCK:
                    os.replace(name, DATA / filename)
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
    httpd.serve_forever()

if __name__ == "__main__":
    main()
