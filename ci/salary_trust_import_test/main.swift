import Foundation
func rejected(_ action:()throws->Void) {
    do { try action();fatalError("Unsafe input was accepted") } catch {}
}
let root=URL(fileURLWithPath:FileManager.default.currentDirectoryPath)
let ledger=try SalaryTrustSpreadsheet.read(root.appendingPathComponent("new.xlsx"))
precondition(ledger.version==2 && ledger.entries.count==3)
let paid=ledger.entries[0],unpaid=ledger.entries[1],review=ledger.entries[2]
precondition(paid.year==2027 && paid.month==11 && paid.legacyPaid && paid.remaining==0)
precondition(paid.caseStatus=="ملاحظة الملف" && paid.importedStatus=="صرف" && paid.importedPaymentDate=="تشرين الثاني2027")
precondition(unpaid.remaining==2000 && review.reviewPending && SalaryTrust.outstanding(ledger.entries)==2000)
let restored=try SalaryTrust.decode(SalaryTrust.encode(ledger));precondition(restored==ledger)
var old=unpaid
old.id=UUID().uuidString;old.note="ملاحظة قديمة";old.caseStatus="حالة قديمة";old.importedPaymentDate=nil
old.payments=[.init(id:UUID().uuidString,amount:300,bookNumber:"",bookDate:"2026-11-02",note:"")]
let book=UUID().uuidString;old.bookIds=[book]
let legacy=SalaryTrust.Ledger(entries:[old],openingImported:true)
let oldData=try SalaryTrust.encode(legacy)
precondition(!String(decoding:oldData,as:UTF8.self).contains("importedPaymentDate"))
let legacyRestored=try SalaryTrust.decode(oldData)
precondition(legacyRestored==legacy)
let replacement=try SalaryTrust.replacing(with:ledger,existing:SalaryTrust.decode(oldData))
precondition(replacement.entries[1].id==old.id && replacement.entries[1].bookIds==[book])
precondition(replacement.entries[1].payments.isEmpty && replacement.entries[1].note.isEmpty && replacement.entries[1].caseStatus==unpaid.caseStatus)
precondition(replacement.entries[1].remaining==2000 && old.payments.count==1)
rejected {var bad=ledger;bad.entries[0].payments=old.payments;try SalaryTrust.validate(bad)}
rejected {_ = try SalaryTrustSpreadsheet.read(root.appendingPathComponent("bad.xlsx"))}
rejected {_ = try SalaryTrustSpreadsheet.read(root.appendingPathComponent("duplicate.xlsx"))}
let numeric=try SalaryTrustSpreadsheet.read(root.appendingPathComponent("dates.xlsx"))
let numeric1904=try SalaryTrustSpreadsheet.read(root.appendingPathComponent("dates1904.xlsx"))
precondition(numeric.entries[0].importedPaymentDate=="2026/11/08")
precondition(numeric1904.entries[0].importedPaymentDate=="2026/11/08")
precondition(SalaryTrustExcelDate.text("46334",cellType:"s",uses1904:false)=="46334")
precondition(SalaryTrust.importedDisposition("تم الصرف") == .paid)
precondition(SalaryTrust.importedDisposition("غير مصروف") == .unpaid)
precondition(SalaryTrust.importedDisposition("صرف جزئي") == .review)
var dateOnly=SalaryTrustImportRows()
dateOnly.append(["الاسم","المبلغ","الشهر","السنة","الحالة","ملاحظات4","تاريخ الصرف"])
dateOnly.append(["منتسب وهمي", "1000", "تشرين الثاني", "2027", "", "صرف", "تشرين الثاني2027"])
let dateOnlyLedger=try dateOnly.result()
precondition(!dateOnlyLedger.entries[0].legacyPaid && dateOnlyLedger.entries[0].remaining==1000)
var missingStatus=SalaryTrustImportRows()
missingStatus.append(["الاسم","المبلغ","الشهر","السنة"])
missingStatus.append(["منتسب وهمي","1000","تشرين الثاني","2027"])
rejected {_ = try missingStatus.result()}
let request=SalaryTrustReportRequest(entries:ledger.entries,month:"كل الأشهر",status:"كل الحالات",query:"",ledgerData:try SalaryTrust.encode(ledger),preview:true)
let sheet=try SalaryTrustReport.sheet(request)
precondition(sheet.columns.count==10 && sheet.rows.count==4 && sheet.rows.allSatisfy{$0.count==10})
precondition(sheet.rows[0][4]=="صرف" && sheet.rows[0][8]=="ملاحظة الملف" && sheet.rows[0][9]=="تشرين الثاني2027")
precondition(sheet.rows[3][7]=="2,000", "Unexpected fictional total: \(sheet.rows[3][7])")
let property=try PropertyListSerialization.data(fromPropertyList:SalaryTrust.encode(ledger),format:.binary,options:0)
precondition(BackupFormat.exportVersion(for:[SalaryTrust.settingsKey:property])==11)
precondition(BackupFormat.exportVersion(for:[:])==5 && BackupFormat.supports(5) && BackupFormat.supports(10) && BackupFormat.supports(11) && !BackupFormat.supports(7))
let manifest=BackupFormat.Manifest(version:11,createdAt:Date(),months:[],employees:0,deductions:0,staff:0,accounts:0,books:0,salaryTrustEntries:3)
precondition(manifest.hasData);try BackupFormat.validateTrust(ledger,manifest:manifest)
rejected {var bad=manifest;bad.salaryTrustEntries=2;try BackupFormat.validateTrust(ledger,manifest:bad)}
rejected {var bad=manifest;bad.version=10;try BackupFormat.validateTrust(ledger,manifest:bad)}
rejected {try BackupFormat.validateTrust(nil,manifest:manifest)}
rejected {var empty=manifest;empty.salaryTrustEntries=0;try BackupReplacement.checkMissingData(empty,previous:manifest)}
let savedManifest=try JSONEncoder().encode(manifest)
let roundTrip=try JSONDecoder().decode(BackupFormat.Manifest.self,from:savedManifest)
precondition(roundTrip.salaryTrustEntries==3 && roundTrip.hasData)
let oldManifest=BackupFormat.Manifest(version:5,createdAt:Date(),months:[],employees:0,deductions:0,staff:0)
let oldManifestData=try JSONEncoder().encode(oldManifest)
let oldManifestRestored=try JSONDecoder().decode(BackupFormat.Manifest.self,from:oldManifestData)
precondition(oldManifestRestored.salaryTrustEntries==nil)
try BackupFormat.validateTrust(legacy,manifest:oldManifestRestored)
// التجميع لا يدمج القيود المالية أو يسقط شهراً، حتى عند اختلاف المسافات أو السنة.
var priorUnpaid=unpaid
priorUnpaid.id=UUID().uuidString;priorUnpaid.name="  منتسب  وهمي أ  "
priorUnpaid.year=2026;priorUnpaid.amount=2500
var priorReview=review
priorReview.id=UUID().uuidString;priorReview.name=paid.name
let groupedLedger=SalaryTrust.Ledger(version:2,entries:[priorUnpaid,paid,unpaid,priorReview],openingImported:true)
let groupedData=try SalaryTrust.encode(groupedLedger)
let people=SalaryTrust.people(groupedLedger.entries)
precondition(people.count==2)
let person=people.first{$0.id==ArabicText.matchKey(paid.name)}!
precondition(person.entries.map(\.id)==[priorReview.id,paid.id,priorUnpaid.id])
precondition(person.total==6500 && person.paid==1000 && person.remaining==2500 && person.reviewTotal==3000)
precondition(person.entryIDs==Set([priorUnpaid.id,paid.id,priorReview.id]))
precondition(Set(people.flatMap(\.entries).map(\.id))==Set(groupedLedger.entries.map(\.id)))
precondition(people.reduce(Int64(0)){$0+$1.remaining}==SalaryTrust.outstanding(groupedLedger.entries))
let monthlyPeople=SalaryTrust.people(SalaryTrust.filtered(groupedLedger.entries,month:202711,status:.paid,search:""))
precondition(monthlyPeople.count==1 && monthlyPeople[0].entries.count==1 && monthlyPeople[0].total==1000)
precondition(people.first{$0.id==monthlyPeople[0].id}!.entries.count==3)
let groupedRoundTrip=try SalaryTrust.decode(groupedData)
precondition(groupedRoundTrip==groupedLedger)
let legacyPerson=SalaryTrust.people(legacy.entries)[0]
precondition(legacyPerson.paid==300 && legacyPerson.remaining==1700)
precondition(SalaryTrust.people([]).isEmpty)
print("Person grouping, per-month details, filtered selection, review totals and legacy partial payments passed")
print("Trust XLSX import, future months, notes/date, status, replacement, legacy, report and standalone backup checks passed")
if FileManager.default.fileExists(atPath:root.appendingPathComponent("private.xlsx").path) {
    struct Expected:Decodable {let name:String,rank:String,workplace:String;let amount:Int64;let note:String,status:String,date:String}
    do {
        let actual=try SalaryTrustSpreadsheet.read(root.appendingPathComponent("private.xlsx"))
        let expected=try JSONDecoder().decode([Expected].self,from:Data(contentsOf:root.appendingPathComponent("private-expected.json")))
        guard actual.entries.count==expected.count else {throw ImportError.cannotOpen("private row count")}
        for (e,x) in zip(actual.entries,expected) {
            guard e.name==x.name,e.rank==x.rank,e.workplace==x.workplace,e.amount==x.amount,e.caseStatus==x.note,e.importedStatus==x.status,(e.importedPaymentDate ?? "")==x.date else {throw ImportError.cannotOpen("private fields mismatch")}
        }
        print("Private workbook verified locally: \(actual.entries.count) entries; all fields match; no personal data printed")
    } catch {fputs("Private workbook validation failed; no personal data printed\n",stderr);exit(1)}
}
