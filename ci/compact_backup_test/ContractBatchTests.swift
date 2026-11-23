import Foundation
import SwiftData

@MainActor func testContractBatches(_ container: ModelContainer, destination: ModelContainer) async throws {
    let month = "تشرين الأول 2026", context = ModelContext(container)
    let date = Date(timeIntervalSince1970: 1_800_000_000)
    let p = "عقد تجريبي", q = "عقد ثان", x = "موظف تجريبي"
    context.insert(PayrollMonth(name: month, employeeCount: 3, deductionCount: 6))
    for (name, rank, bank, fund) in [(p,"عقود",270000,2000),(q,"عقود",130000,2000),(x,"شرطي",1250000,4000)] {
        context.insert(Employee(month: month, name: name, rank: rank, workplace: "", netSalary: 100,
                                items: ["مجموع استقطاعات المصارف":Double(bank),"صندوق الشهداء":Double(fund)]))
    }
    for (name,title,amount,note) in [(p,"أ",120000,"1"),(q,"أ",80000,"2"),(x,"أ",800000,"3"),
                                      (p,"ب",150000,"4"),(x,"ب",450000,"5"),(q,"ج",50000,"6")] {
        context.insert(Deduction(month: month, employeeName: name, title: title, amount: Double(amount), note: note))
    }
    try context.save()
    func iban(_ serial: Int) -> String {
        let bban = "RAFB" + String(format: "%015d", serial)
        let rotated = bban + "IQ00"
        var rem = 0
        for b in rotated.utf8 {
            let digits = b >= 65 ? String(Int(b)-55) : String(Int(b)-48)
            for digit in digits.utf8 { rem = (rem * 10 + Int(digit)-48) % 97 }
        }
        return String(format: "IQ%02d",98-rem) + bban
    }
    let totals = [("أ",1000000,3,TransferCategory.rafidain),("ب",600000,2,.otherBanks),("ج",50000,1,.anbar),
                  (TransferPayrollRecipient.fundDonations.arabicTitle,8000,1,.otherBanks)]
    let entities = totals.enumerated().map { i, v in
        TransferEntity(group: .init(kind:.deductions, month:month,title:v.0,count:v.2,total:Double(v.1),byEntity:false),
            account:.init(entity:v.0,iban:iban(i+10),branch:v.3 == .rafidain ? "مصرف الرافدين فرع تجريبي" : (v.3 == .anbar ? "مصرف الرافدين فرع الانبار" : "مصرف الرشيد"),swift:"BBBBIQBA"),
            category:v.3, missingAmounts:0, payrollRecipient:i==3 ? TransferPayrollRecipient.fundDonations.rawValue : nil)
    }
    let pay = iban(1), figures = TransferLetterFigures(month:month, foodFund:0, foodTax:0, foodCompany:0, healthcare:0,
        policeFundDonations:8000,sports:0,incomeTax:0,socialProtection:0,contractNominalShare:120000,contractRetirement:50000,payrollCash:nil)
    let engine = ContractTransferEngine(modelContainer: container)
    let batch = try await engine.prepare(month:month, importedAt:date, figures:figures, entities:entities, payer:"دافع تجريبي",iban:pay,links:Data())
    precondition(batch.held==574000)
    precondition(batch.parts(.deferred,category:.rafidain).map(\.group.total)==[200000])
    precondition(batch.parts(.current,category:.anbar).isEmpty)
    precondition(batch.parts(.deferred,category:.anbar).map(\.group.total)==[50000])
    var references = Set<String>()
    for phase in ContractTransferPhase.allCases {
        let lines = batch.parts(phase,category:.otherBanks)
        let scope = phase.referenceScope(month:month)
        let csv = try TransferRules.csv(lines,category:.otherBanks,month:month,payer:batch.payer,payerIBAN:pay,date:date,referenceScope:scope)
        let verified = try TransferGroupCSVCheck.validate(csv,entities:lines,category:.otherBanks,month:month,payer:batch.payer,payerIBAN:pay,referenceScope:scope)
        precondition(verified.reduce(0){$0+$1.amount} == (phase == .current ? 454000 : 154000))
        for line in String(decoding:csv,as:UTF8.self).components(separatedBy:"\r\n").dropFirst() {
            precondition(references.insert(String(line.split(separator:"|")[0])).inserted)
        }
    }
    var archive = ContractTransferArchive()
    try archive.insert(batch)
    archive.records[0].prepared["deferred|group|otherBanks"] = date
    archive.records[0].confirmed["deferred|group|otherBanks"] = date
    let data = try archive.encoded(), settings = [ContractTransferArchive.key:try PropertyListSerialization.data(fromPropertyList:data,format:.binary,options:0)]
    let (file, manifest) = try await BackupActor(modelContainer:container).export(settings:settings,progress:nil)
    defer { BackupFormat.removeExport(file) }
    precondition(manifest.version == 13)
    let folder = try await Restore.extractForRestore(file)
    defer { try? FileManager.default.removeItem(at:folder) }
    let restored = try await BackupActor(modelContainer:destination).restore(from:folder,progress:nil)
    let roundTrip = try ContractTransferArchive.fromSettings(restored)
    try roundTrip.checkRestore(over:archive)
    let after = try roundTrip.records[0].batch()
    precondition(after.held==batch.held && after.parts(.deferred,category:.anbar).count==1)
    do { try ContractTransferArchive().checkRestore(over:archive); preconditionFailure("Allowed a rollback") } catch is ContractTransferIssue {}
    context.insert(Deduction(month:month, employeeName:p,title:"أ",amount:120000,note:"1",rank:"شرطي"))
    try context.save()
    do {
        _ = try await engine.prepare(month:month, importedAt:date, figures:figures, entities:entities,payer:"test",iban:pay,links:Data())
        preconditionFailure("Accepted conflicting rank")
    } catch is ContractTransferIssue {}
}

