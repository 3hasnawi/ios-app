"""Home Assistant ingress administers snapshots; a separate port serves read-only clients."""
import hashlib
import hmac
import html
import json
import os
import secrets
import sqlite3
import threading
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path

import uvicorn
from fastapi import FastAPI, File, Form, Header, HTTPException, UploadFile
from fastapi.responses import HTMLResponse, JSONResponse, Response

DATA_DIR = Path(os.environ.get("SALARY_SYNC_DATA", "/data"))
MAX_BYTES = 64 * 1024 * 1024
admin = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
client = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)


@contextmanager
def connection():
    DATA_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)
    db = sqlite3.connect(DATA_DIR / "snapshots.sqlite3", timeout=30)
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("PRAGMA synchronous=FULL")
    db.execute("""CREATE TABLE IF NOT EXISTS snapshots (
        version INTEGER PRIMARY KEY AUTOINCREMENT, month TEXT NOT NULL,
        sha256 TEXT NOT NULL, employee_count INTEGER NOT NULL,
        deduction_count INTEGER NOT NULL, published_at TEXT NOT NULL,
        payload BLOB NOT NULL)""")
    db.execute("CREATE INDEX IF NOT EXISTS snapshots_month ON snapshots(month, version DESC)")
    db.execute("CREATE TABLE IF NOT EXISTS reader_tokens (digest TEXT PRIMARY KEY, created_at TEXT NOT NULL)")
    try:
        yield db
        db.commit()
    finally:
        db.close()


def validated(data):
    if len(data) > MAX_BYTES:
        raise HTTPException(413, "الملف أكبر من الحد المسموح")
    try:
        value = json.loads(data, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
    except (ValueError, UnicodeDecodeError):
        raise HTTPException(400, "ملف JSON غير صالح")
    if not isinstance(value, dict) or value.get("format") != 1:
        raise HTTPException(400, "صيغة البيانات غير معروفة")
    month = value.get("month")
    if not isinstance(month, str) or not 1 <= len(month.strip()) <= 80 or any(ord(c) < 32 for c in month):
        raise HTTPException(400, "اسم الشهر غير صالح")
    for key in ("employees", "deductions"):
        if not isinstance(value.get(key), list) or len(value[key]) > 150_000:
            raise HTTPException(400, f"قائمة {key} غير صالحة")
        if not all(isinstance(row, dict) for row in value[key]):
            raise HTTPException(400, f"سجلات {key} غير صالحة")
    if not value["employees"] and not value["deductions"]:
        raise HTTPException(400, "الشهر خالٍ من البيانات")
    return month.strip(), len(value["employees"]), len(value["deductions"])


def latest_rows(db):
    return db.execute("""SELECT s.month, s.version, s.sha256, s.employee_count,
        s.deduction_count, s.published_at FROM snapshots s WHERE s.version =
        (SELECT MAX(t.version) FROM snapshots t WHERE t.month = s.month)
        ORDER BY s.version DESC""").fetchall()


def metadata(rows):
    return [dict(zip(("month", "version", "sha256", "employees", "deductions", "published_at"), r)) for r in rows]


@admin.get("/", response_class=HTMLResponse)
def dashboard():
    with connection() as db:
        rows = metadata(latest_rows(db))
    cards = "".join(
        f"<li>{html.escape(r['month'])} — {r['employees']} راتب، {r['deductions']} استقطاع"
        f" — إصدار {r['version']}</li>" for r in rows
    ) or "<li>لم يُنشر شهر بعد</li>"
    return ("<!doctype html><html lang='ar' dir='rtl'><meta charset='utf-8'>"
            "<meta name='viewport' content='width=device-width,initial-scale=1,viewport-fit=cover'>"
            "<style>*{box-sizing:border-box}html{-webkit-text-size-adjust:100%}"
            "body{font-family:system-ui;margin:0 auto;padding:32px 16px 56px;max-width:650px;"
            "background:#f5f7f8;color:#173a37;line-height:1.5;overflow-wrap:anywhere}"
            "h1{font-size:clamp(24px,7vw,32px);line-height:1.3;margin:0 0 18px}"
            "h2{font-size:clamp(20px,6vw,26px);line-height:1.3;margin:0 0 14px}"
            "p{margin:0 0 18px}section{background:white;padding:20px;border-radius:18px;margin:18px 0}"
            "ul{padding-right:22px}li{margin:8px 0}"
            "button,.file-choice{display:inline-block;padding:11px 18px;background:#13745e;color:white;"
            "border:0;border-radius:11px;font:inherit;font-size:16px;cursor:pointer;min-height:44px}"
            "button{margin-top:16px}.file-wrap{position:relative;display:inline-block}"
            ".file-input{position:absolute;inset:0;width:100%;height:100%;opacity:0;cursor:pointer}"
            ".file-input:focus-visible+.file-choice{outline:3px solid #245ed9}"
            ".chosen{display:block;font-size:14px;color:#596968;margin:9px 0 16px}"
            ".check{display:flex;align-items:flex-start;gap:9px;font-size:15px}"
            ".check input{width:19px;height:19px;flex:none;margin:3px 0 0}"
            "@media(max-width:430px){section{padding:18px 16px}body{padding-top:40px}}"
            "</style><h1>مزامنة الرواتب</h1>"
            "<p>مرحلة تجريبية: استخدم بيانات وهمية فقط حتى يكتمل ربط تطبيق الآيفون والتحقق من الخصوصية.</p>"
            f"<section><h2>الأشهر المنشورة</h2><ul>{cards}</ul></section>"
            "<section><h2>نشر شهر بعد مراجعته</h2><form action='publish' method='post' enctype='multipart/form-data'>"
            "<span class='file-wrap'><input class='file-input' id='month-file' type='file' name='file' "
            "accept='.json,application/json' required><label class='file-choice' for='month-file'>"
            "اختيار ملف JSON</label></span>"
            "<span class='chosen' id='chosen-file'>لم يُحدّد ملف</span>"
            "<label class='check'><input type='checkbox' name='approved' value='yes' required>"
            " راجعت أعداد الشهر ومجاميعه</label>"
            "<button type='submit'>نشر النسخة</button></form></section>"
            "<section><h2>رمز جهاز قارئ</h2><form action='reader-token' method='post'>"
            "<button type='submit'>إنشاء رمز جديد</button></form>"
            "<p>يعرض الرمز مرة واحدة. احفظه في مدير كلمات مرور موثوق.</p></section>"
            "<script>document.getElementById('month-file').addEventListener('change',function(){"
            "document.getElementById('chosen-file').textContent=this.files.length?this.files[0].name:'لم يُحدّد ملف'"
            "})</script></html>")


@admin.post("/publish")
async def publish(file: UploadFile = File(...), approved: str = Form("")):
    if approved != "yes":
        raise HTTPException(400, "تحتاج مراجعة الشهر قبل النشر")
    data = await file.read(MAX_BYTES + 1)
    month, employees, deductions = validated(data)
    stamp = datetime.now(timezone.utc).isoformat()
    digest = hashlib.sha256(data).hexdigest()
    with connection() as db:
        cursor = db.execute("""INSERT INTO snapshots
            (month,sha256,employee_count,deduction_count,published_at,payload)
            VALUES (?,?,?,?,?,?)""", (month, digest, employees, deductions, stamp, data))
        version = cursor.lastrowid
    return HTMLResponse(f"<html lang='ar' dir='rtl'><meta charset='utf-8'><p>نُشر {html.escape(month)} بالإصدار {version}.</p><a href='./'>رجوع</a></html>")


@admin.post("/reader-token")
def reader_token():
    token = secrets.token_urlsafe(40)
    digest = hashlib.sha256(token.encode()).hexdigest()
    with connection() as db:
        db.execute("INSERT INTO reader_tokens VALUES (?,?)", (digest, datetime.now(timezone.utc).isoformat()))
    return HTMLResponse("<html lang='ar' dir='rtl'><meta charset='utf-8'>"
                        "<p>انسخ الرمز الآن؛ لن يظهر ثانية:</p><code>" + token + "</code><p><a href='./'>رجوع</a></p></html>",
                        headers={"Cache-Control": "no-store"})


def authorize(value):
    if not value or not value.startswith("Bearer "):
        raise HTTPException(401, "رمز الوصول مطلوب", headers={"WWW-Authenticate": "Bearer"})
    digest = hashlib.sha256(value[7:].encode()).hexdigest()
    with connection() as db:
        found = db.execute("SELECT digest FROM reader_tokens WHERE digest=?", (digest,)).fetchone()
    if not found or not hmac.compare_digest(found[0], digest):
        raise HTTPException(401, "رمز الوصول غير صالح")


@client.get("/healthz")
def healthz():
    return {"status": "ok"}


@client.get("/v1/months")
def months(authorization: str | None = Header(default=None)):
    authorize(authorization)
    with connection() as db:
        rows = metadata(latest_rows(db))
    return JSONResponse({"months": rows}, headers={"Cache-Control": "no-store"})


@client.get("/v1/months/{month}")
def month(month: str, authorization: str | None = Header(default=None)):
    authorize(authorization)
    with connection() as db:
        row = db.execute("SELECT payload,sha256,version FROM snapshots WHERE month=? ORDER BY version DESC LIMIT 1", (month,)).fetchone()
    if row is None:
        raise HTTPException(404, "الشهر غير موجود")
    return Response(row[0], media_type="application/json", headers={"ETag": '"' + row[1] + '"',
                     "X-Snapshot-Version": str(row[2]), "Cache-Control": "no-store"})


def main():
    threading.Thread(target=lambda: uvicorn.run(admin, host="0.0.0.0", port=8099, access_log=False), daemon=True).start()
    uvicorn.run(client, host="0.0.0.0", port=8080, access_log=False)


if __name__ == "__main__":
    main()
