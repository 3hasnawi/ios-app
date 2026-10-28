"""Google Drive device authorization and resumable backup uploads."""
import http.client
import json
import os
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

TOKEN_URL = "https://oauth2.googleapis.com/token"
DEVICE_URL = "https://oauth2.googleapis.com/device/code"
SCOPE = "https://www.googleapis.com/auth/drive.file"
CHUNK = 8 * 1024 * 1024


def request_json(url, values):
    data = urllib.parse.urlencode(values).encode()
    request = urllib.request.Request(url, data=data, headers={"Content-Type": "application/x-www-form-urlencoded"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        try:
            result = json.load(error)
        except (ValueError, OSError):
            result = {}
        raise ValueError(result.get("error", "google_request_failed")) from error


class DriveSync:
    def __init__(self, options, state_file, backup_dir):
        self.enabled = bool(options.get("google_drive_enabled"))
        self.client_id = options.get("google_client_id", "").strip()
        self.client_secret = options.get("google_client_secret", "").strip()
        self.state_file = Path(state_file)
        self.backup_dir = Path(backup_dir)
        self.lock = threading.RLock()
        self.upload_lock = threading.Lock()
        self.event = threading.Event()
        self.pending = None
        self.status = "disabled" if not self.enabled else "not_connected"
        self.last_error = None
        try:
            self.state = json.loads(self.state_file.read_text())
        except (OSError, ValueError):
            self.state = {}
        if self.state.get("client_id") != self.client_id:
            self.state = {}
        if self.enabled and self.state.get("refresh_token"):
            self.status = "connected"
        if self.enabled:
            threading.Thread(target=self.worker, daemon=True).start()
            self.event.set()

    def save(self):
        self.state_file.parent.mkdir(parents=True, exist_ok=True)
        path = self.state_file.with_suffix(".tmp")
        fd = os.open(path, os.O_CREAT | os.O_WRONLY | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as file:
            json.dump(self.state, file)
            file.flush()
            os.fsync(file.fileno())
        os.replace(path, self.state_file)

    def info(self):
        with self.lock:
            return {"enabled": self.enabled, "status": self.status, "error": self.last_error,
                    "last_success": self.state.get("last_success")}

    def start(self):
        if not self.enabled or not self.client_id or not self.client_secret:
            raise ValueError("google_not_configured")
        result = request_json(DEVICE_URL, {"client_id": self.client_id, "scope": SCOPE})
        with self.lock:
            self.pending = {"device_code": result["device_code"], "expires": time.monotonic() + int(result["expires_in"]),
                            "interval": max(5, int(result.get("interval", 5))), "next_poll": 0}
        return {"user_code": result["user_code"], "verification_url": result.get("verification_url", "https://www.google.com/device"),
                "expires_in": result["expires_in"], "interval": self.pending["interval"]}

    def poll(self):
        with self.lock:
            p = self.pending
            if not p or time.monotonic() >= p["expires"]:
                self.pending = None
                raise ValueError("expired_token")
            if time.monotonic() < p["next_poll"]:
                return {"status": "pending"}
            p["next_poll"] = time.monotonic() + p["interval"]
        try:
            result = request_json(TOKEN_URL, {"client_id": self.client_id, "client_secret": self.client_secret,
                                  "device_code": p["device_code"], "grant_type": "urn:ietf:params:oauth:grant-type:device_code"})
        except ValueError as error:
            if str(error) in ("authorization_pending", "slow_down"):
                if str(error) == "slow_down":
                    with self.lock:
                        p["interval"] += 5
                return {"status": "pending"}
            raise
        if not result.get("refresh_token"):
            raise ValueError("missing_refresh_token")
        with self.lock:
            existing_file_id = self.state.get("file_id")
            self.state = {"client_id": self.client_id, "refresh_token": result["refresh_token"]}
            if existing_file_id:
                self.state["file_id"] = existing_file_id
            self.save()
            self.pending = None
            self.status = "connected"
            self.last_error = None
        self.event.set()
        return {"status": "connected"}

    def access_token(self):
        with self.lock:
            refresh = self.state.get("refresh_token")
        if not refresh:
            raise ValueError("google_not_connected")
        result = request_json(TOKEN_URL, {"client_id": self.client_id, "client_secret": self.client_secret,
                              "refresh_token": refresh, "grant_type": "refresh_token"})
        return result["access_token"]

    def upload(self, file, access):
        with self.lock:
            file_id = self.state.get("file_id")
        if not file_id:
            ids = urllib.request.Request("https://www.googleapis.com/drive/v3/files/generateIds?count=1&space=drive&type=files",
                                         headers={"Authorization": "Bearer " + access})
            with urllib.request.urlopen(ids, timeout=30) as response:
                file_id = json.load(response)["ids"][0]
            # حفظ المعرف قبل الرفع يمنع إنشاء ملف إضافي عند انقطاع التأكيد.
            with self.lock:
                self.state["file_id"] = file_id
                self.save()
        endpoint = "https://www.googleapis.com/upload/drive/v3/files"
        size = os.fstat(file.fileno()).st_size
        def start_upload(create):
            metadata = {"name": "نسخة الرواتب.zip"}
            target = endpoint
            if create:
                metadata["id"] = file_id
            else:
                target += "/" + urllib.parse.quote(file_id, safe="")
            target += "?uploadType=resumable&fields=id"
            req = urllib.request.Request(target, data=json.dumps(metadata, ensure_ascii=False).encode(),
                method="POST" if create else "PATCH", headers={
                    "Authorization": "Bearer " + access, "Content-Type": "application/json; charset=UTF-8",
                    "X-Upload-Content-Type": "application/zip", "X-Upload-Content-Length": str(size)})
            with urllib.request.urlopen(req, timeout=60) as response:
                return response.headers["Location"]
        try:
            session = start_upload(False)
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            session = start_upload(True)
        parsed = urllib.parse.urlsplit(session)
        if parsed.scheme != "https" or parsed.hostname != "www.googleapis.com":
            raise ValueError("invalid_google_upload_session")
        offset = 0
        while offset < size:
            chunk = file.read(min(CHUNK, size - offset))
            if not chunk:
                raise ValueError("incomplete_local_backup")
            end = offset + len(chunk) - 1
            path = parsed.path + ("?" + parsed.query if parsed.query else "")
            conn = http.client.HTTPSConnection(parsed.hostname, timeout=180)
            try:
                conn.request("PUT", path, body=chunk, headers={"Content-Length": str(len(chunk)),
                    "Content-Type": "application/zip", "Content-Range": f"bytes {offset}-{end}/{size}"})
                response = conn.getresponse()
                body = response.read()
                if response.status not in (200, 201, 308):
                    raise ValueError("google_upload_failed")
                if response.status in (200, 201):
                    result = json.loads(body)
                    if result["id"] != file_id:
                        raise ValueError("unexpected_google_file")
                    return result["id"]
                if response.getheader("Range") != f"bytes=0-{end}":
                    raise ValueError("google_upload_range_mismatch")
            finally:
                conn.close()
            offset = end + 1
        raise ValueError("google_upload_incomplete")

    def worker(self):
        while True:
            self.event.wait(timeout=300)
            self.event.clear()
            if not self.enabled or not self.state.get("refresh_token"):
                continue
            file = self.backup_dir / "backup-current.zip"
            try:
                if not file.exists():
                    existing = sorted(self.backup_dir.glob("backup-*.zip"), key=lambda item: item.stat().st_mtime_ns, reverse=True)
                    if not existing:
                        continue
                    file = existing[0]
                with self.upload_lock:
                    with file.open("rb") as source:
                        fingerprint = os.fstat(source.fileno()).st_mtime_ns
                        with self.lock:
                            if self.state.get("last_mtime_ns") == fingerprint:
                                continue
                            self.status = "uploading"
                        identifier = self.upload(source, self.access_token())
                    with self.lock:
                        self.state.update(file_id=identifier, last_mtime_ns=fingerprint, last_success=int(time.time()))
                        self.save()
                        self.status = "connected"
                        self.last_error = None
                if file.stat().st_mtime_ns != fingerprint:
                    self.event.set()
            except Exception:
                with self.lock:
                    self.status = "error"
                    self.last_error = "google_upload_failed"
