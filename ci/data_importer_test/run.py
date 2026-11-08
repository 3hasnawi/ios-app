from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
models=(root/'SalaryApp/Models.swift').read_text()
source=(root/'SalaryApp/ExcelImporter.swift').read_text()
records=source[:source.index('/// تحفظ الحالة')].replace('import ZIPFoundation','')
arabic=(root/'SalaryApp/ArabicText.swift').read_text().split('\nenum Money')[0]
main='''import Foundation
import SwiftData
func tr(_ a:String,_ b:String)->String { a }
typealias ProgressSink = @Sendable (Double)->Void
struct ImportResult: Sendable { var employees:[ImportedEmployee]=[]; var deductions:[ImportedDeduction]=[] }
@main struct Test {
 @MainActor static func main() async throws {
  let schema=Schema([PayrollMonth.self,Employee.self,Deduction.self])
  let container=try ModelContainer(for:schema,configurations:[ModelConfiguration(isStoredInMemoryOnly:true)])
  let importer=DataImporter(modelContainer:container)
  let sep="أيلول 2026", oct="تشرين الأول 2026", aug="آب 2026"
  let original=ImportedEmployee(name:"وهمي أصلي",rank:"موظف",workplace:"اختبار",netSalary:1000)
  let replacement=ImportedEmployee(name:"وهمي معدل",rank:"موظف",workplace:"اختبار",netSalary:1500)
  let deduction=ImportedDeduction(employeeName:replacement.name,title:"جهة وهمية",amount:100,note:"")
  try await importer.replacePayroll(month:sep,with:[original])
  try await importer.replaceDeductions(month:sep,with:[deduction])
  try await importer.replacePayroll(month:oct,with:[original])
  // ملف رواتب مستقل لنفس الشهر لا يمحو استقطاعاته.
  try await importer.replacePayroll(month:sep,with:[replacement])
  var verify=ModelContext(container)
  precondition(try verify.fetchCount(FetchDescriptor<Deduction>()) == 1)
  // ملف استقطاعات مستقل لا يمحو الرواتب.
  try await importer.replaceDeductions(month:sep,with:[deduction,deduction])
  verify=ModelContext(container)
  let employees=try verify.fetch(FetchDescriptor<Employee>())
  precondition(employees.count == 2 && employees.contains { $0.month==sep && $0.name==replacement.name })
  do { try await importer.replacePayroll(month:aug,with:[original]); fatalError("Older payroll replaced retained months") }
  catch PayrollReplacementError.olderMonth { }
  verify=ModelContext(container)
  precondition(try verify.fetchCount(FetchDescriptor<Employee>()) == 2)
  precondition(try verify.fetchCount(FetchDescriptor<Deduction>()) == 2)
  print("Split payroll/deduction import preserves the other dataset; old payroll rejected without mutation")
 }
}
'''
# precondition uses a nonthrowing autoclosure; evaluate database reads first.
main=main.replace('precondition(try verify.fetchCount(FetchDescriptor<Deduction>()) == 1)','let firstDeductions=try verify.fetchCount(FetchDescriptor<Deduction>())\n  precondition(firstDeductions == 1)').replace('precondition(try verify.fetchCount(FetchDescriptor<Employee>()) == 2)','let finalEmployees=try verify.fetchCount(FetchDescriptor<Employee>())\n  precondition(finalEmployees == 2)').replace('precondition(try verify.fetchCount(FetchDescriptor<Deduction>()) == 2)','let finalDeductions=try verify.fetchCount(FetchDescriptor<Deduction>())\n  precondition(finalDeductions == 2)')
with tempfile.TemporaryDirectory() as temp:
 p=Path(temp)
 for file,text in [('Models.swift',models),('Records.swift',records),('Arabic.swift',arabic),('Test.swift',main)]: (p/file).write_text(text)
 subprocess.run(['swiftc','Models.swift','Records.swift','Arabic.swift','Test.swift','-o','test'],cwd=p,check=True)
 subprocess.run([str(p/'test')],cwd=p,check=True)
