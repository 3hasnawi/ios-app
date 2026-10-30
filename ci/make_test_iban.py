"""ملفا IBAN وهميان لاختبار الإضافة والتحديث ورفض رقم غير صالح."""

from openpyxl import Workbook


def iban(bban: str) -> str:
    base = bban + "182600"  # IQ00 بعد نقله إلى آخر الرقم
    digits = "".join(str(ord(c) - 55) if c.isalpha() else c for c in base)
    return f"IQ{98 - int(digits) % 97:02d}{bban}"


def save(path: str, rows: list[tuple[str, str, str, str]], entity_header: str = "جهة التنفيذ") -> None:
    book = Workbook()
    sheet = book.active
    sheet.append(["ت", entity_header, "رقم الايبان", "الفرع", "SWIFT", "المبلغ"])
    for index, (name, number, branch, swift) in enumerate(rows, 1):
        sheet.append([index, name, number, branch, swift, None])
    book.save(path)


save("test-files/IBAN-1.xlsx", [
    ("جهة تجريبية أ", iban("TEST123000000000001"), "فرع أ", "TESTIQBAAAA"),
    ("جهة تجريبية ب", iban("TEST123000000000002"), "فرع ب", "TESTIQBBBBB"),
], entity_header="المديرية")
save("test-files/IBAN-2.xlsx", [
    ("جهة تجريبية أ", iban("TEST123000000000003"), "فرع أ", "TESTIQBAAAA"),
    ("جهة تجريبية ج", iban("TEST123000000000004"), "فرع ج", "TESTIQCCCCC"),
    ("جهة خاطئة", "123456789012345", "فرع خاطئ", "TESTIQXXXXX"),
    ("جهة ناقصة", iban("TEST123000000000005")[:-1], "فرع خاطئ", "TESTIQXXXXX"),
    ("جهة تحقق خاطئ", "IQ00" + iban("TEST123000000000006")[4:], "فرع خاطئ", "TESTIQXXXXX"),
])
