"""Fictional archive encoded independently with Apple's field order and binary plists."""
import base64
import hashlib
import json
import pathlib
import plistlib
import struct
import sys
import uuid
import zipfile

dest = pathlib.Path(sys.argv[1])
dest.mkdir(parents=True, exist_ok=True)


def pack(obj):
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":")).encode()


def b64(raw):
    return base64.b64encode(raw).decode()


def plist(obj):
    return b64(plistlib.dumps(obj, fmt=plistlib.FMT_BINARY))


def lines(rows):
    return b"".join(pack(r) + b"\n" for r in rows)


names = ["شباط 2025", "أيلول 2026", "تشرين الأول 2026", "تشرين الثاني 2026"]
people = ["آدم نوري مثال", "بتول علي مثال", "جاسم سعد مثال", "دعاء باسم مثال", "رنا سعد مثال",
          "سالم قاسم مثال", "شهد وليد مثال", "علي ناصر مثال", "ليلى عادل مثال", "يوسف كريم مثال"]
entities = ["مديرية تنفيذ النور", "مصرف الأفق / فرع المدينة", "مديرية تنفيذ الربيع"]
cuts = []
months = []
for index, month in enumerate(names):
    for i, name in enumerate(people):
        entity = entities[1 if i % 3 else (2 if index == 3 else 0)]
        cuts.append(dict(month=month, name=name, title=entity, note=f"{170+i} / {2024+i%2} في ١٢/٣",
                         rank=["موظف", "مفوض", "رقيب"][i%3], amount=10000*(1+i)+1000*index))
    months.append(dict(name=month, importedAt=0, employeeCount=1 if index==3 else 0, deductionCount=len(people)))

u64 = lambda n: struct.pack(">Q", n)
field = lambda b: u64(len(b)) + b if b is not None else b"\xff"*8
header=b'["A"]'
raw=b'{"headers":,"values":["B"]}'
offset=raw.index(b",")
employee=dict(month=names[-1],name=people[0],rank="موظف",workplace="مركز تجريبي",net=100000,
              rawColumns=b64(raw),headerIndex=0,headerOffset=offset)
digest=hashlib.sha256(b"".join(field(employee[k].encode()) for k in ("month","name","rank","workplace"))+
                      u64(1)+struct.pack(">d",employee["net"])+field(None)+field(None)+field(raw[:offset]+header+raw[offset:])).digest()

def movement(description, amount, direction, date):
    return dict(id=str(uuid.uuid4()),description=description,amount=amount,direction=direction,date=date,matched=True)


period=dict(id=str(uuid.uuid4()),month=names[-1],start=815000000,end=817500000,openingBalance=0,
            memberFirst="",memberSecond="", movements=[movement("تمويل الشهر",580000,"credit",815000000),
            movement("تحويل استقطاعات الجهات",580000,"debit",815086400)])
bank=dict(version=1,periods=[period])
book_id = str(uuid.UUID(int=42)).upper()
# Build a one-page PDF with a valid cross-reference table.
pdf = bytearray(b"%PDF-1.4\n")
offsets = [0]
for number, body in enumerate((b"<< /Type /Catalog /Pages 2 0 R >>",
                               b"<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
                               b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>"), 1):
    offsets.append(len(pdf))
    pdf.extend(f"{number} 0 obj\n".encode()+body+b"\nendobj\n")
xref=len(pdf)
pdf.extend(b"xref\n0 4\n0000000000 65535 f \n")
for pos in offsets[1:]:
    pdf.extend(f"{pos:010} 00000 n \n".encode())
pdf.extend(f"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF".encode())
(dest/"original-book.pdf").write_bytes(pdf)
books=[dict(id=book_id,title="كتاب تجريبي",month=names[-1],pages=1,createdAt=0,entity=entities[0])]
entries={"books/index.json":pack(books),"books/"+book_id+".pdf":pdf,"deductions.jsonl":lines(cuts),"months.jsonl":lines(months),
         "employees.jsonl":lines([employee]),"staff.jsonl":b"","accounts.jsonl":b"",
         "payroll-headers.json":pack([b64(header)]),
         "settings.json":pack({"bankReconciliations":plist(pack(bank)),"reportHeader":plist("قيادة شرطة محافظة الانبار")})}
support=hashlib.sha256()
for name, data in sorted(entries.items()):
    if name!="employees.jsonl":
        support.update(name.encode()+b"\0"+hashlib.sha256(data).digest())
entries["manifest.json"]=pack(dict(version=12,contentVersion=5,createdAt=0,months=names,employees=1,deductions=len(cuts),
                                    staff=0,accounts=0,books=1,payrollDigest=b64(digest),supportingDigest=b64(support.digest())))
for label in ("valid","damaged"):
    with zipfile.ZipFile(dest/(label+".zip"),"w",zipfile.ZIP_DEFLATED) as archive:
        for name,data in entries.items():
            if label=="damaged" and name=="deductions.jsonl":
                data=data.replace(b'10000',b'90000',1)
            archive.writestr(name,data)
