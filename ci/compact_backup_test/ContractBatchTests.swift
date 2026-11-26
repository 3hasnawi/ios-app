import Foundation
import SwiftData

@MainActor func testContractBatches(_ container: ModelContainer, destination: ModelContainer) async throws {
    let month = "تشرين الأول 2026", context = ModelContext(container)
    let date = Date(timeIntervalSince1970: 1_800_000_000)
    let p = "عقد تجريبي", q = "عقد ثان", x = "منتسب تجريبي", officer = "شرطي تجريبي"
    context.insert(PayrollMonth(name: month, employeeCount: 4, deductionCount: 9))
    for (name, rank, bank, fund) in [(p,"عقود",270000,2000),(q,"عقود",130000,2000),(x,"عقد",1150000,2000),(officer,"شرطي",100000,2000)] {
        context.insert(Employee(month: month, name: name, rank: rank, workplace: "", netSalary: 100,
                                items: ["مجموع استقطاعات المصارف":Double(bank),"صندوق الشهداء":Double(fund)]))
    }
    for (name,title,amount,note) in [(p,"أ",60000,"1"),(p,"أ",60000,"1"),(q,"أ",80000,"2"),(x,"أ",700000,"3"),(officer,"أ",100000,"7"),
                                      (p,"ب",150000,"4"),(x,"ب",225000,"5"),(x,"ب",225000,"5"),(q,"ج",50000,"6")] {
        context.insert(Deduction(month: month, employeeName: name, title: title, amount: Double(amount), note: note,
                                 rank: name == x && title == "أ" ? "عقد" : ""))
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
    let totals = [("أ",1000000,4,TransferCategory.rafidain),("ب",600000,2,.otherBanks),("ج",50000,1,.anbar),
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
    // «عقد» من ملف التنفيذ في «أ»، ومن رتبة الرواتب في «ب»، وكذلك تبرع الصندوق.
    for (title, expected) in [("أ", 700000.0), ("ب", 450000.0), (TransferPayrollRecipient.fundDonations.arabicTitle, 2000.0)] {
        let part = batch.parts.first { $0.entity.group.title == title }!
        precondition(part.current.filter { $0.name == x }.reduce(0) { $0 + $1.amount } == expected)
        precondition(!part.deferred.contains { $0.name == x })
    }
    precondition(batch.held==574000)
    precondition(batch.parts(.deferred,category:.rafidain).map(\.group.total)==[200000])
    precondition(batch.parts(.current,category:.anbar).isEmpty)
    precondition(batch.parts(.deferred,category:.anbar).map(\.group.total)==[50000])
    let impacts = ContractExecutionLetterImpact.affected(in: batch)
    precondition(impacts.map(\.id) == [.group(.rafidain), .group(.otherBanks), .individual(entities[2].id)])
    precondition(impacts.map(\.original) == [1000000, 608000, 50000])
    precondition(impacts.map(\.current) == [800000, 454000, 0])
    precondition(impacts.map(\.deferred) == [200000, 154000, 50000])
    for impact in impacts {
        precondition(impact.id.originalTotal(in: batch) == impact.original)
        precondition(impact.original == impact.current + impact.deferred)
        precondition(impact.current == impact.id.total(batch, .current))
        precondition(impact.deferred == impact.id.total(batch, .deferred))
        precondition(impact.recipients.map(\.id) == impact.id.entities(batch, .deferred).map(\.id))
    }
    // A book can include transfers that stay entirely in the current phase.
    let unchanged = batch.parts.first { $0.entity.id == entities[1].id }!
    let fund = batch.parts.first { $0.entity.payrollRecipient != nil }!
    let noExecutionHold = ContractTransferPart(entity: unchanged.entity, current: unchanged.current + unchanged.deferred, deferred: [])
    let onlyFund = ContractTransferBatch(id: batch.id, month: month, importedAt: date, created: date, figures: figures,
        payer: batch.payer, iban: pay, links: batch.links, parts: [noExecutionHold, fund])
    precondition(ContractExecutionLetterImpact.affected(in: onlyFund).isEmpty)
    let otherBanks = ContractTransferDestination.group(.otherBanks)
    precondition(otherBanks.originalTotal(in: onlyFund) == 608000)
    precondition(otherBanks.total(onlyFund, .current) == 604000)
    precondition(otherBanks.total(onlyFund, .deferred) == 4000)
    func checkRepeatedRows(_ snapshot: ContractTransferBatch) {
        let contracts = snapshot.parts.first { $0.entity.group.title == "أ" }!.deferred.filter { $0.name == p }
        let regular = snapshot.parts.first { $0.entity.group.title == "ب" }!.current.filter { $0.name == x }
        precondition(contracts.map(\.amount) == [60000,60000] && Set(contracts.map(\.id)).count == 2)
        precondition(regular.map(\.amount) == [225000,225000] && Set(regular.map(\.id)).count == 2)
    }
    checkRepeatedRows(batch)
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
    var untouched = archive
    try untouched.cancel(untouched.records[0], at: date)
    precondition(untouched.records.isEmpty)
    archive.records[0].prepared["deferred|group|otherBanks"] = date
    let prepared = archive.records[0]
    var preparedOnly = archive
    try preparedOnly.cancel(prepared, at: date)
    precondition(preparedOnly.activeRecord(for: month) == nil && preparedOnly.records[0].prepared == prepared.prepared)
    archive.records[0].confirmed["deferred|group|otherBanks"] = date
    let activeArchive = try ContractTransferArchive.read(archive.encoded())
    precondition(activeArchive.activeRecord(for: month)?.id == batch.id && activeArchive.version == 1)
    do { try archive.cancel(prepared); preconditionFailure("Ignored newer confirmation") } catch TransferIndividualPlan.Failure.invalidSource {}
    try archive.cancel(archive.records[0], at: date)
    precondition(archive.activeRecord(for: month) == nil && archive.records[0].confirmed == activeArchive.records[0].confirmed)
    do { try activeArchive.checkRestore(over: archive); preconditionFailure("Restored a cancelled deferral as active") } catch is ContractTransferIssue {}
    let replacement = try await engine.prepare(month:month, importedAt:date, figures:figures, entities:entities, payer:"دافع تجريبي",iban:pay,links:Data())
    var repeated = archive
    try repeated.insert(replacement)
    let reloaded = try ContractTransferArchive.read(repeated.encoded())
    precondition(reloaded.records.count == 2 && reloaded.activeRecord(for: month)?.id == replacement.id)
    let data = try archive.encoded(), settings = [ContractTransferArchive.key:try PropertyListSerialization.data(fromPropertyList:data,format:.binary,options:0)]
    let (file, manifest) = try await BackupActor(modelContainer:container).export(settings:settings,progress:nil)
    defer { BackupFormat.removeExport(file) }
    precondition(manifest.version == 13)
    let folder = try await Restore.extractForRestore(file)
    defer { try? FileManager.default.removeItem(at:folder) }
    let restored = try await BackupActor(modelContainer:destination).restore(from:folder,progress:nil)
    let roundTrip = try ContractTransferArchive.fromSettings(restored)
    try roundTrip.checkRestore(over:archive)
    precondition(roundTrip.records[0].cancelledAt == date && roundTrip.activeRecord(for: month) == nil)
    precondition(roundTrip.executionMarkers == archive.executionMarkers && roundTrip.version == 2)
    let after = try roundTrip.records[0].batch()
    checkRepeatedRows(after)
    precondition(after.held==batch.held && after.parts(.deferred,category:.anbar).count==1)
    do { try ContractTransferArchive().checkRestore(over:archive); preconditionFailure("Allowed a rollback") } catch is ContractTransferIssue {}
    context.insert(Deduction(month:month, employeeName:p,title:"أ",amount:120000,note:"1",rank:"شرطي"))
    try context.save()
    do {
        _ = try await engine.prepare(month:month, importedAt:date, figures:figures, entities:entities,payer:"test",iban:pay,links:Data())
        preconditionFailure("Accepted conflicting rank")
    } catch is ContractTransferIssue {}
}

