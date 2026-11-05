"""Synthetic branch-deduction files, alone and with a payroll worksheet."""
import sys
from pathlib import Path
from openpyxl import Workbook

output = Path(sys.argv[1])
output.mkdir(parents=True, exist_ok=True)
for combined in (False, True):
    book = Workbook()
    sheet = book.active
    if combined:
        sheet.title = "رواتب وهمية"
        sheet.append(["Name_m", "الرتبة", "صافي الراتب"])
        sheet.append(["موظف وهمي", "شرطي", 1000])
        sheet = book.create_sheet("استقطاعات وهمية")
    sheet.append(["رقم الموظف", "الرتبة", "الاسم", "اسم المصرف", "اسم الفرع", "المبلغ", "الملاحظات", "المديرية"])
    sheet.append([1, "شرطي", "موظف وهمي", "مصرف وهمي", "فرع وهمي", 250, "إضبارة وهمية", "مديرية وهمية"])
    sheet.append([2, "شرطي", "صفر وهمي", "مصرف وهمي", "فرع وهمي", 0, "", "مديرية وهمية"])
    sheet.append([3, "شرطي", "فارغ وهمي", "مصرف وهمي", "فرع وهمي", None, "", "مديرية وهمية"])
    book.save(output / ("branch-combined.xlsx" if combined else "branch-only.xlsx"))
