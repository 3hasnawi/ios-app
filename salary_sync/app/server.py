"""Private store for the app's existing نسخة الرواتب.zip backup."""
import asyncio
import hashlib
import hmac
import html
import json
import math
import os
import secrets
import sqlite3
import tempfile
import threading
import uuid
import zipfile
import zlib
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path

import uvicorn
from fastapi import FastAPI, Header, HTTPException, Request
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse

DATA_DIR = Path(os.environ.get("SALARY_SYNC_DATA", "/data"))
MAX_BYTES = 512 * 1024 * 1024
MAX_EXPANDED_BYTES = 2 * 1024 * 1024 * 1024
FILES = frozenset({"manifest.json", "settings.json", "months.jsonl", "employees.jsonl",
                   "deductions.jsonl", "staff.jsonl"})
admin = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
client = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
publish_lock = threading.Lock()


@contextmanager
def connection():
    DATA_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)
    db = sqlite3.connect(DATA_DIR / "backup.sqlite3", timeout=30)
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("PRAGMA synchronous=FULL")
    db.execute("""CREATE TABLE IF NOT EXISTS tokens (
        digest TEXT PRIMARY KEY, role TEXT NOT NULL CHECK(role IN ('writer', 'reader')),
        created_at TEXT NOT NULL)""")
    db.execute("""CREATE TABLE IF NOT EXISTS backups (
        id INTEGER PRIMARY KEY AUTOINCREMENT, filename TEXT NOT NULL,
        sha256 TEXT NOT NULL, size INTEGER NOT NULL, uploaded_at TEXT NOT NULL,
        created_at REAL NOT NULL, format_version INTEGER NOT NULL,
        months INTEGER NOT NULL, employees INTEGER NOT NULL,
        deductions INTEGER NOT NULL, staff INTEGER NOT NULL)""")
    try:
        yield db
        db.commit()
    finally:
        db.close()


def latest(db):
    return db.execute("""SELECT id, filename, sha256, size, uploaded_at, created_at,
        format_version, months, employees, deductions, staff
        FROM backups ORDER BY id DESC LIMIT 1""").fetchone()


def info(row):
    if row is None:
        return None
    return dict(zip(("revision", "filename", "sha256", "size", "uploaded_at", "created_at",
                     "format_version", "months", "employees", "deductions", "staff"), row))


def validate_archive(path):
    try:
        with zipfile.ZipFile(path) as archive:
            files = archive.infolist()
            if len(files) != len(FILES) or {item.filename for item in files} != FILES:
                raise ValueError("أسماء ملفات النسخة غير مطابقة")
            if any(item.is_dir() or item.file_size > MAX_EXPANDED_BYTES for item in files):
                raise ValueError("النسخة تحتوي ملفات غير صالحة")
            if sum(item.file_size for item in files) > MAX_EXPANDED_BYTES:
                raise ValueError("حجم النسخة بعد فك الضغط كبير")
            manifest_info = archive.getinfo("manifest.json")
            if manifest_info.file_size > 64 * 1024:
                raise ValueError("ملف وصف النسخة كبير")
            manifest = json.loads(archive.read(manifest_info))
            if not isinstance(manifest, dict) or manifest.get("version") != 1:
                raise ValueError("إصدار النسخة غير مدعوم")
            if not isinstance(manifest.get("createdAt"), (int, float)) or not math.isfinite(manifest["createdAt"]):
                raise ValueError("تاريخ النسخة غير صالح")
            months = manifest.get("months")
            if not isinstance(months, list) or len(months) > 1000 or not all(isinstance(m, str) for m in months):
                raise ValueError("قائمة الأشهر غير صالحة")
            for name in ("employees", "deductions", "staff"):
                value = manifest.get(name)
                if not isinstance(value, int) or isinstance(value, bool) or not 0 <= value <= 1_000_000:
                    raise ValueError("أعداد النسخة غير صالحة")
            settings_info = archive.getinfo("settings.json")
            if settings_info.file_size > 4 * 1024 * 1024 or not isinstance(json.loads(archive.read(settings_info)), dict):
                raise ValueError("إعدادات النسخة غير صالحة")
            expected = {"months.jsonl": len(months), "employees.jsonl": manifest["employees"],
                        "deductions.jsonl": manifest["deductions"], "staff.jsonl": manifest["staff"]}
            for name, count in expected.items():
                seen = 0
                with archive.open(name) as records:
                    for line in records:
                        if not line.strip() or not isinstance(json.loads(line), dict):
                            raise ValueError("سجلات النسخة غير صالحة")
                        seen += 1
                        if seen > count:
                            raise ValueError("عدد سجلات النسخة لا يطابق الوصف")
                if seen != count:
                    raise ValueError("عدد سجلات النسخة لا يطابق الوصف")
            return manifest
    except (zipfile.BadZipFile, zlib.error, json.JSONDecodeError, UnicodeDecodeError,
            KeyError, ValueError, RuntimeError, EOFError, OSError) as error:
        raise HTTPException(400, f"ملف النسخة غير صالح: {error}") from error


def authorize(value, *, write=False):
    if not value or not value.startswith("Bearer "):
        raise HTTPException(401, "رمز الوصول مطلوب", headers={"WWW-Authenticate": "Bearer"})
    digest = hashlib.sha256(value[7:].encode()).hexdigest()
    with connection() as db:
        row = db.execute("SELECT digest,role FROM tokens WHERE digest=?", (digest,)).fetchone()
    if not row or not hmac.compare_digest(row[0], digest) or (write and row[1] != "writer"):
        raise HTTPException(403, "رمز الوصول غير صالح أو بلا صلاحية")


@admin.get("/", response_class=HTMLResponse)
def dashboard():
    with connection() as db:
        backup = info(latest(db))
        roles = dict(db.execute("SELECT role,COUNT(*) FROM tokens GROUP BY role").fetchall())
    if backup:
        detail = (f"<strong>آخر نسخة: {html.escape(backup['uploaded_at'])}</strong>"
                  f"<p>{backup['months']} شهر · {backup['employees']} راتب · "
                  f"{backup['deductions']} استقطاع · {backup['staff']} سجل ملاك</p>"
                  f"<small>الإصدار {backup['revision']} · {backup['size']:,} بايت</small>")
    else:
        detail = "<strong>لم تُحفظ نسخة احتياطية بعد</strong>"
    return ("<!doctype html><html lang='ar' dir='rtl'><meta charset='utf-8'>"
            "<meta name='viewport' content='width=device-width,initial-scale=1,viewport-fit=cover'>"
            "<style>*{box-sizing:border-box}html{-webkit-text-size-adjust:100%}"
            "body{font-family:system-ui;margin:0 auto;padding:40px 16px 56px;max-width:650px;"
            "background:#f5f7f8;color:#173a37;line-height:1.5;overflow-wrap:anywhere}"
            "h1{font-size:clamp(24px,7vw,32px);line-height:1.3;margin:0 0 18px}"
            "h2{font-size:clamp(20px,6vw,26px);line-height:1.3;margin:0 0 14px}"
            "section{background:white;padding:20px;border-radius:18px;margin:18px 0}"
            "p{margin:8px 0 14px}small{color:#596968}"
            "button{padding:11px 18px;background:#13745e;color:white;border:0;"
            "border-radius:11px;font:inherit;cursor:pointer;min-height:44px}"
            "form{margin-top:14px}.revoke{background:#edeff0;color:#425452}"
            "@media(max-width:430px){section{padding:18px 16px}}"
            "</style><h1>النسخة الاحتياطية</h1>"
            "<p>يحفظ السيرفر ملف «نسخة الرواتب.zip» الكامل من التطبيق؛ لا حاجة لرفع الأشهر يدوياً.</p>"
            f"<section><h2>حالة النسخة</h2>{detail}</section>"
            "<section><h2>ربط الأجهزة</h2>"
            "<p>رمز الكاتب للآيفون الذي يرفع النسخة، ورمز القارئ للأجهزة التي تعرضها أو تسترجعها."
            " يظهر الرمز مرة واحدة فقط.</p>"
            f"<small>رموز الكتابة: {roles.get('writer', 0)} · رموز القراءة: {roles.get('reader', 0)}</small>"
            "<form action='tokens/writer' method='post'><button>إنشاء رمز للآيفون</button></form>"
            "<form action='tokens/reader' method='post'><button>إنشاء رمز لجهاز قارئ</button></form>"
            "<form action='tokens/writer/revoke' method='post' onsubmit=\"return confirm('إلغاء رموز الكتابة؟')\">"
            "<button class='revoke'>إلغاء رموز الكتابة</button></form>"
            "<form action='tokens/reader/revoke' method='post' onsubmit=\"return confirm('إلغاء رموز القراءة؟')\">"
            "<button class='revoke'>إلغاء رموز القراءة</button></form>"
            "</section><section><h2>الاتصال</h2>"
            "<p>واجهة الأجهزة تحتاج عنوان HTTPS خاصاً وآمناً. لا تنشر المنفذ 8080 مباشرة على الإنترنت.</p>"
            "</section></html>")


@admin.post("/tokens/{role}", response_class=HTMLResponse)
def create_token(role: str):
    if role not in ("writer", "reader"):
        raise HTTPException(404)
    token = secrets.token_urlsafe(40)
    digest = hashlib.sha256(token.encode()).hexdigest()
    with connection() as db:
        db.execute("INSERT INTO tokens VALUES (?,?,?)", (digest, role, datetime.now(timezone.utc).isoformat()))
    label = "الآيفون الكاتب" if role == "writer" else "الجهاز القارئ"
    return HTMLResponse("<html lang='ar' dir='rtl'><meta charset='utf-8'>"
                        "<meta name='viewport' content='width=device-width,initial-scale=1'>"
                        f"<p>رمز {label}؛ انسخه الآن، لن يظهر ثانية:</p><code style='overflow-wrap:anywhere'>"
                        + token + "</code><p><a href='../'>رجوع</a></p></html>",
                        headers={"Cache-Control": "no-store", "Referrer-Policy": "no-referrer"})


@admin.post("/tokens/{role}/revoke", response_class=HTMLResponse)
def revoke_tokens(role: str):
    if role not in ("writer", "reader"):
        raise HTTPException(404)
    with connection() as db:
        db.execute("DELETE FROM tokens WHERE role=?", (role,))
    return HTMLResponse("<html lang='ar' dir='rtl'><meta charset='utf-8'>"
                        "<p>أُلغيت الرموز. الأجهزة المرتبطة تحتاج رمزاً جديداً.</p>"
                        "<a href='../../'>رجوع</a></html>", headers={"Cache-Control": "no-store"})


@client.get("/healthz")
def healthz():
    return {"status": "ok"}


@client.get("/v1/backup/meta")
def backup_meta(authorization: str | None = Header(default=None)):
    authorize(authorization)
    with connection() as db:
        backup = info(latest(db))
    if backup is None:
        raise HTTPException(404, "لا توجد نسخة بعد")
    backup.pop("filename")
    return JSONResponse(backup, headers={"Cache-Control": "no-store"})


@client.get("/v1/backup")
def download(authorization: str | None = Header(default=None)):
    authorize(authorization)
    with connection() as db:
        backup = info(latest(db))
    if backup is None:
        raise HTTPException(404, "لا توجد نسخة بعد")
    path = DATA_DIR / backup["filename"]
    if not path.is_file():
        raise HTTPException(503, "ملف النسخة غير متاح")
    return FileResponse(path, media_type="application/zip", filename="نسخة الرواتب.zip",
                        headers={"ETag": '"' + backup["sha256"] + '"', "Cache-Control": "no-store"})


@client.put("/v1/backup")
async def upload(request: Request, authorization: str | None = Header(default=None)):
    authorize(authorization, write=True)
    length = request.headers.get("content-length")
    if length and (not length.isdecimal() or int(length) > MAX_BYTES):
        raise HTTPException(413, "ملف النسخة أكبر من الحد المسموح")
    digest = hashlib.sha256()
    size = 0
    DATA_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=DATA_DIR, prefix=".upload-", suffix=".zip", delete=False) as temp:
        temp_path = Path(temp.name)
        try:
            async for chunk in request.stream():
                size += len(chunk)
                if size > MAX_BYTES:
                    raise HTTPException(413, "ملف النسخة أكبر من الحد المسموح")
                digest.update(chunk)
                temp.write(chunk)
            if size == 0:
                raise HTTPException(400, "الملف فارغ")
            temp.flush()
            os.fsync(temp.fileno())
        except BaseException:
            temp_path.unlink(missing_ok=True)
            raise
    try:
        manifest = await asyncio.to_thread(validate_archive, temp_path)
        with publish_lock:
            with connection() as db:
                previous = info(latest(db))
                if previous and sum(previous[name] for name in ("months", "employees", "deductions", "staff")) > 0 and not (
                    len(manifest["months"]) + manifest["employees"] + manifest["deductions"] + manifest["staff"]
                ):
                    raise HTTPException(409, "نسخة فارغة لا يمكنها استبدال نسخة تحتوي بيانات")
                filename = f"backup-{uuid.uuid4().hex}.zip"
                final = DATA_DIR / filename
                os.replace(temp_path, final)
                directory = os.open(DATA_DIR, os.O_RDONLY)
                try:
                    os.fsync(directory)
                finally:
                    os.close(directory)
                try:
                    cursor = db.execute("""INSERT INTO backups
                        (filename,sha256,size,uploaded_at,created_at,format_version,months,employees,deductions,staff)
                        VALUES (?,?,?,?,?,?,?,?,?,?)""",
                        (filename, digest.hexdigest(), size, datetime.now(timezone.utc).isoformat(),
                         manifest["createdAt"], manifest["version"], len(manifest["months"]),
                         manifest["employees"], manifest["deductions"], manifest["staff"]))
                    revision = cursor.lastrowid
                except BaseException:
                    final.unlink(missing_ok=True)
                    raise
            # نحتفظ بآخر نسختين لاسترجاع السابقة إذا رُفعت بيانات خاطئة.
            with connection() as db:
                old = db.execute("SELECT id,filename FROM backups ORDER BY id DESC LIMIT -1 OFFSET 2").fetchall()
                for old_id, _ in old:
                    db.execute("DELETE FROM backups WHERE id=?", (old_id,))
            for _, old_name in old:
                (DATA_DIR / old_name).unlink(missing_ok=True)
        return JSONResponse({"revision": revision, "sha256": digest.hexdigest(), "size": size},
                            headers={"Cache-Control": "no-store"})
    finally:
        temp_path.unlink(missing_ok=True)


def main():
    threading.Thread(target=lambda: uvicorn.run(admin, host="0.0.0.0", port=8099, access_log=False), daemon=True).start()
    uvicorn.run(client, host="0.0.0.0", port=8080, access_log=False)


if __name__ == "__main__":
    main()
