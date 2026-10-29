"""ملفات وهمية لاختبار تجزئة الاستقطاعات السنوية وحالات المبلغ."""
from pathlib import Path
from openpyxl import Workbook

folder = Path('test-files')
folder.mkdir(exist_ok=True)
headers = ['ت', 'الرتبة', 'الاسم الكامل', 'المبلغ', 'جهة التنفيذ', 'رقم وتاريخ الاضبارة', 'شهر']

def save(name, rows, year='2024'):
    book = Workbook()
    sheet = book.active
    sheet.title = year
    sheet.append(headers)
    for row in rows:
        sheet.append(row)
    book.save(folder / name)

rows = [
    [1, 'موظف', 'أحمد اختبار وهمي', 100, 'جهة وهمية', 'ملف تجريبي', 'كانون الثاني'],
    [2, 'موظف', 'أحمد اختبار وهمي', 'موقوف', 'جهة وهمية', 'ملف تجريبي', 'شباط'],
    [3, 'موظف', 'أحمد اختبار وهمي', 50, 'جهة وهمية', '', 'شباط'],
    [4, 'موظف', 'شخص وهمي آخر', 'هارب', 'جهة وهمية', '', 'كانون الأول'],
    [5, 'موظف', 'شخص وهمي آخر', 'محكوم', 'جهة وهمية', '', 'كانون الأول'],
]
save('annual-fixture.xlsx', rows)
save('annual-invalid.xlsx', rows + [[6, 'موظف', 'شخص وهمي', 'حالة مجهولة', 'جهة وهمية', '', 'آذار']])
save('annual-2025.xlsx', rows)
print('Generated annual test workbooks with fictional data')
