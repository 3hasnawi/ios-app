"""Exercise the production XLSX importer, ledger and backup contract with fictional fixtures."""
from pathlib import Path
import argparse,json,os,subprocess,tempfile,zipfile
from xml.sax.saxutils import escape
root=Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser();p.add_argument('--prepare',type=Path);opt=p.parse_args()
def block(s,token):
 a=s.index(token);i=s.index('{',a)+1;depth=1
 while depth:
  depth+=(s[i]=='{')-(s[i]=='}');i+=1
 return s[a:i]
def fixture(path,rows,numeric=False,epoch=False):
 headers=['الرتبة','الاسم الكامل','صافي الراتب','ملاحظات4','الفوج او المديرية','الشهر','السنة','الحالة','تاريخ الصرف']
 xml=['<worksheet><sheetData>']
 for n,row in enumerate([headers]+rows,1):
  xml.append(f'<row r="{n}">')
  for i,v in enumerate(row):
   ref=chr(65+i)+str(n)
   xml.append(f'<c r="{ref}"><v>{v}</v></c>' if numeric and i==8 and n>1 else f'<c r="{ref}" t="inlineStr"><is><t>{escape(str(v))}</t></is></c>')
  xml.append('</row>')
 xml.append('</sheetData></worksheet>')
 with zipfile.ZipFile(path,'w') as z:
  z.writestr('xl/worksheets/sheet1.xml',''.join(xml));z.writestr('xl/workbook.xml',f'<workbook><workbookPr date1904="{int(epoch)}"/></workbook>')
def prepare(work):
 src=work/'Sources/TrustImportTests';src.mkdir(parents=True,exist_ok=True)
 vendor=os.environ.get('SALARY_ZIPFOUNDATION_PATH')
 dep=f'.package(path:{json.dumps(vendor)})' if vendor else '.package(url:"https://github.com/weichsel/ZIPFoundation.git",exact:"0.9.20")'
 (work/'Package.swift').write_text('// swift-tools-version:5.9\nimport PackageDescription\nlet package=Package(name:"TrustImportTests",dependencies:['+dep+'],targets:[.executableTarget(name:"TrustImportTests",dependencies:[.product(name:"ZIPFoundation",package:"ZIPFoundation")])])\n')
 importer=(root/'SalaryApp/ExcelImporter.swift').read_text();parser=importer[importer.index('private nonisolated func localName'):]
 models=(root/'SalaryApp/Models.swift').read_text();month=models[models.index('nonisolated enum MonthName'):models.index('extension PayrollMonth {')]
 arabic=(root/'SalaryApp/ArabicText.swift').read_text().split('\nenum Money')[0]
 backup=(root/'SalaryApp/Backup.swift').read_text()
 report=(root/'SalaryApp/SalaryTrustReport.swift').read_text();report=report[report.index('nonisolated struct SalaryTrustReportRequest'):report.index('    static func makePDF')]+'\n}\n'
 parts=[parser,month,arabic]+[(root/f'SalaryApp/{name}.swift').read_text() for name in ['SalaryTrust','SalaryTrustImport','SalaryTrustSpreadsheet']]
 parts+=[block(backup,'nonisolated enum BackupFormat'),'enum BackupReplacement {\n'+block(backup,'    nonisolated static func checkMissingData(')+'\n}',report]
 production='import Foundation\n#if canImport(FoundationXML)\nimport FoundationXML\n#endif\n'+'\n'.join(parts)
 (src/'Production.swift').write_text(production.replace('nonisolated ',''))
 for name in ['Stubs.swift','main.swift']:(src/name).write_text((root/'ci/salary_trust_import_test'/name).read_text())
 fixture(work/'new.xlsx',[
  ['رتبة','منتسب وهمي أ',1000,'ملاحظة الملف','مديرية وهمية','تشرين الثاني',2027,'صرف','تشرين الثاني2027'],
  ['رتبة','منتسب وهمي ب',2000,'ملاحظة أخرى','مديرية وهمية','تشرين الثاني',2027,'',''],
  ['رتبة','منتسب وهمي ج',3000,'تحتاج تدقيقاً','مديرية وهمية','كانون الأول',2027,'حالة غير معروفة','']])
 fixture(work/'dates.xlsx',[['رتبة','منتسب وهمي',1000,'','قسم','تشرين الثاني',2026,'صرف',46334]],True)
 fixture(work/'dates1904.xlsx',[['رتبة','منتسب وهمي',1000,'','قسم','تشرين الثاني',2026,'صرف',44872]],True,True)
 fixture(work/'bad.xlsx',[['رتبة','منتسب وهمي','3500/7000','','قسم','تشرين الثاني',2026,'صرف','']])
 fixture(work/'duplicate.xlsx',[['رتبة','منتسب وهمي',1000,'','قسم','تشرين الثاني',2026,'','']]*2)
if opt.prepare:prepare(opt.prepare)
else:
 with tempfile.TemporaryDirectory(prefix='trust-import-') as temp:
  work=Path(temp);prepare(work);subprocess.run(['swift','run','TrustImportTests'],cwd=work,check=True)
