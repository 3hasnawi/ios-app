using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AnbarPayroll;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Content;
using ClosedXML.Excel;
using A=SalaryDesktop.Core;

static class MonthlyCycleTests
{
    public static void Run(string dir,Store seed,RefundBatch refund,A.BankPeriod period,Action<bool,string> check)
    {
        var store=new Store(Path.Combine(dir,"monthly-cycle"));store.Import(seed.Archive);
        string month=period.Month;
        var records=refund.People.Select(p=>p.Name).ToArray();string name=records[0];
        var paths=refund.Groups().Select(g=>{var path=Path.Combine(dir,g.Code+".csv");File.WriteAllBytes(path,refund.Csv(g));return path;}).ToArray();
        var recovered=RefundRecovery.Read(paths,month,store.Snapshot(month).Employees);
        check(recovered.Date==refund.Date&&recovered.People.Sum(p=>p.Amount)==3003&&recovered.Groups().All(g=>recovered.Csv(g).SequenceEqual(File.ReadAllBytes(Path.Combine(dir,g.Code+".csv")))),"recovered bank CSVs preserve every original reference, BIC and amount");
        try{RefundRecovery.Read(paths.Concat(paths),month,[]);throw new Exception("duplicate acceptance");}catch(InvalidDataException){check(true,"duplicate refund rows rejected before saving");}
        store.SaveRefund(recovered,store.Active);
        var unlocked=period with{Movements=[]};
        var choices=BankSuggestions.For(store.Snapshot(month),unlocked);
        var card=choices.Single(c=>c.Id==month+"|card");var partial=choices.Where(c=>c.Id.Contains("|group|")).ToList();
        check(card.Amount==15000&&partial.Sum(c=>c.Amount)==500&&choices.All(c=>c.Amount>0),"bank suggestions respect split current and deferred shares");
        var posted=new A.BankMovement(Guid.NewGuid(),period.Start,card.Title,card.Amount,A.MovementDirection.Debit,card.Id,card.Lines);
        check(!BankSuggestions.For(store.Snapshot(month),unlocked with{Movements=[posted]}).Any(c=>c.Id==card.Id),"posted source cannot be suggested twice");
        var source=store.Snapshot(month);var claim=WorkReports.FundingData(source,month);
        store.ChangeFunding(source,month,false);
        check(Store.FundingHistory(store.Snapshot(),month).Single().Snapshot.BankTotal==claim.BankTotal,"funding approval records the reviewed bank total");
        var staff=StaffInput.Member(new Dictionary<string,string>{{"الاسم الكامل",name},{"الرقم الاحصائي","900123"},{"اسم الام","والدة تجريبية"},{"الرتبة","عقود"}});
        store.ImportStaff([staff],"test.accdb",store.Active);
        check(store.SearchPeople("900123").Any(p=>p.Name==name)&&A.StaffRecords.Read(store.Snapshot()).Single().Name==name,"staff fields indexed and readable from archive");
        string file=Path.Combine(dir,"trust-cycle.xlsx");
        using(var wb=new XLWorkbook()){
            var ws=wb.AddWorksheet("أمانات");
            string[] heads=["الاسم","المبلغ","الشهر","السنة","الحالة","تاريخ الصرف","الملاحظات"];
            for(int i=0;i<heads.Length;i++)ws.Cell(1,i+1).Value=heads[i];
            for(int i=0;i<2;i++){int r=i+2;ws.Cell(r,1).Value=name;ws.Cell(r,2).Value=100+i;ws.Cell(r,3).Value=10+i;ws.Cell(r,4).Value=2026;ws.Cell(r,5).Value=i==0?"مصروف":"لم يصرف";ws.Cell(r,7).Value="ملاحظة تجريبية";}
            ws.Cell(2,6).Value=new DateTime(2026,11,1).ToOADate();wb.SaveAs(file);
        }
        var trust=A.SalaryTrustSpreadsheet.Read(file);store.ImportTrust(trust,store.Active);
        var ids=A.SalaryTrust.ReadFromBackup(store.Snapshot()).Entries.Select(e=>e.Id).ToArray();
        store.ImportTrust(A.SalaryTrustSpreadsheet.Read(file),store.Active);
        var entries=A.SalaryTrust.ReadFromBackup(store.Snapshot()).Entries;
        check(entries.Count==2&&entries.Sum(e=>e.Amount)==201&&entries[0].ImportedPaymentDate=="2026/11/01"&&ids.SequenceEqual(entries.Select(e=>e.Id)),"Excel trust replaces ledger and preserves person/month links and payment date");
        var hits=store.SearchPeople(name);
        check(hits[0].Name==name&&hits.Where(h=>h.Name==name).ToArray() is [{Payroll:true,Staff:true,Deductions:true,Trust:true}],"single result across four sources and multiple periods");
        check(store.SearchPeople("والدة تجريبية") is [{Staff:true} mother]&&mother.Name==name,"mother search leads to the same person");
        var details=store.EmployeeDetails(name);
        check(details.Staff.Single().Stat=="900123"&&details.Months.Length==2&&details.Trust.Entries.Sum(e=>e.Amount)==201&&details.Deductions.All(e=>e.Name==name),
            "profile links the salary, roster, deductions and imported trusts of one employee");
        var beforeMigration=SHA256.HashData(File.ReadAllBytes(store.Archive));
        var originalDir=store.Active;
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{
            DataSource=Path.Combine(originalDir!,"lookup.db"),Pooling=false}.ToString())){
            db.Open();using var downgrade=db.CreateCommand();
            downgrade.CommandText="DROP TABLE profile_rows; DELETE FROM meta WHERE key LIKE 'profileTrust%'; PRAGMA user_version=2";
            downgrade.ExecuteNonQuery();
        }
        var upgraded=store.EmployeeDetails(name);
        check(store.Active==originalDir&&beforeMigration.SequenceEqual(SHA256.HashData(File.ReadAllBytes(store.Archive)))&&
            upgraded.Months.Length==2&&upgraded.Staff.Single().Stat==staff.Stat&&upgraded.Trust.Entries.Select(e=>e.Id).SequenceEqual(details.Trust.Entries.Select(e=>e.Id)),
            "older index upgrades in place with original archive and person data intact");
        var person=store.PersonSnapshot(name);check(person.Employees.Count==2&&person.Employees.All(e=>e.Name==name)&&person.Deductions.All(d=>d.Name==name),"unified profile only expands matching employee rows");
        var slip=A.PersonProfiles.Payslip(A.PersonProfiles.Build(person,name,[staff]).Months[0]);check(slip.Net==5000&&slip.Info.Any(i=>i.Title=="طريقة الدفع"&&i.Value=="بطاقة"),"profile payslip shows payment method from payroll");
        var signed=File.ReadAllBytes(Path.Combine(dir,"original-book.pdf"));var beforeLink=store.Snapshot();
        var trustEntries=A.SalaryTrust.ReadFromBackup(beforeLink).Entries;
        var linkIds=trustEntries.Select(e=>e.Id).ToHashSet();
        var rev=store.Active;
        try{store.ArchivePdf(beforeLink,"كتاب تجريبي",month,null,signed,new HashSet<string>{"missing"});throw new Exception("accepted stale trust rows");}
        catch(InvalidDataException){check(store.Active==rev,"failed link does not create an orphan PDF");}
        store.ArchivePdf(beforeLink,"كتاب تجريبي",month,null,signed,linkIds);
        var linked=store.Snapshot();var bookId=linked.Books.Single().Id;
        var firstId=new HashSet<string>{trustEntries[0].Id};
        store.UpdateTrustBook(linked,firstId,bookId,unlink:true);
        var removed=store.Snapshot();var partialLinks=A.SalaryTrust.ReadFromBackup(removed).Entries;
        check(partialLinks.All(e=>(e.BookIds?.Count??0)==(firstId.Contains(e.Id)?0:1)),"unlink only the selected trust rows");
        store.UpdateTrustBook(removed,firstId,bookId);
        var relinked=A.SalaryTrust.ReadFromBackup(store.Snapshot()).Entries;
        check(relinked.All(e=>e.BookIds?.Count==1) && relinked.Select(e=>(e.Id,e.Amount,e.ImportedStatus,e.ImportedPaymentDate)).SequenceEqual(trustEntries.Select(e=>(e.Id,e.Amount,e.ImportedStatus,e.ImportedPaymentDate))),"scan and link share one PDF and preserve trust amounts and payment fields");
        var refs=new MemoryStream();var manifest=new List<object>();
        using(var package=new ZipArchive(refs,ZipArchiveMode.Create,true)){
            foreach(string id in new[]{"rafidain_group","other_banks_group","food_company","food_tax","tax_social","cash_salary","contracts","water","housing","rafidain_anbar","ramadi"}){
                int pages=id.EndsWith("_group")?2:1;var pdf=new PdfDocumentBuilder();
                for(int i=0;i<pages;i++)pdf.AddPage(PageSize.A4);
                var data=pdf.Build();var path="templates/"+id+".pdf";
                using(var entry=package.CreateEntry(path).Open())entry.Write(data);
                manifest.Add(new{id,path,sha256=Convert.ToHexString(SHA256.HashData(data)),pages});
            }
            using var index=package.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(index,new{version=1,templates=manifest});
        }
        store.ImportReferences(refs.ToArray(),store.Active);
        check(A.LetterReferencePack.Read(store.Snapshot()).Count==11,"validated reference PDFs available locally after archive rewrite");
        var src=store.Snapshot(month);
        string agency=src.Accounts.First().Entity;string label="جهة موحدة تجريبية";
        var before=src.Deductions.Where(d=>d.Title==agency).Sum(d=>d.Amount);
        store.SaveWorkPreferences(store.Active!,new(){["homePinnedWorkItems"]="salaryTrust|bankStatement",["appTextSize"]="4",["windowsContentTextLevel"]="2"});
        store.RenameAgency(src,agency,label);
        var renamed=store.Snapshot(month);
        check(renamed.Deductions.All(d=>d.Title!=agency)&&renamed.Deductions.Where(d=>d.Title==label).Sum(d=>d.Amount)==before&&A.TransferPreparation.Prepare(renamed,month).Entries.Single(e=>e.Title==label).Account?.Iban==src.Accounts.First().Iban,"renaming keeps historical amounts and links the original account");
        var exchange=Path.Combine(dir,"exchange");Directory.CreateDirectory(exchange);var exchangeFile=Path.Combine(exchange,"windows-month.zip");
        var beforeAppearance=Statement.Entities(store.Find(new()),"اختبار",true).Html();
        store.SaveWorkPreferences(store.Active!,new(){["appNumerals"]="arabicIndic",["appAppearance"]="dark"});
        store.ExportBackup(exchangeFile);
        var firstExport=File.ReadAllBytes(exchangeFile);
        store.ExportBackup(exchangeFile);
        check(firstExport.SequenceEqual(File.ReadAllBytes(exchangeFile)),"repeat export does not duplicate settings or attachments");
        var archived=new Store(Path.Combine(dir,"restored-month"));archived.Import(exchangeFile);
        var prefs=archived.ReadWorkPreferences();
        check(prefs["appAppearance"]=="dark" && prefs["homePinnedWorkItems"]=="salaryTrust|bankStatement" && prefs["appNumerals"]=="arabicIndic" && prefs["appTextSize"]=="4" && prefs["windowsContentTextLevel"]=="2","restored pins, numeral style and both text size settings survive a data edit and export");
        var savedRefund=archived.Refunds().Single(b=>b.Id==refund.Id);
        check(Statement.Entities(archived.Find(new()),"اختبار",true).Html()==beforeAppearance &&
            savedRefund.Groups().All(g=>savedRefund.Csv(g).SequenceEqual(File.ReadAllBytes(Path.Combine(dir,g.Code+".csv")))),
            "display theme and numeral preferences preserve statement layout and bank CSV bytes");
        var restored=archived.Snapshot(month);
        string outPdf=Path.Combine(dir,"restored-book.pdf");archived.SaveBook(archived.Books().Single(b=>b.Title=="كتاب تجريبي"),outPdf);
        check(File.ReadAllBytes(outPdf).SequenceEqual(signed)&&archived.Refunds().Count==store.Refunds().Count&&A.SalaryTrust.ReadFromBackup(restored).Entries.Count==2&&WorkReports.FundingData(restored,month).BankTotal==claim.BankTotal&&Store.ActiveBatch(restored,month)!.Held==Store.ActiveBatch(src,month)!.Held,"backup round trip preserves PDF, trusts, transfers, refunds and totals");
        foreach(var m in new[]{"كانون الأول 2026","كانون الثاني 2027"})archived.ApplyExcel(archived.PreviewExcel(Path.Combine(dir,"monthly.xlsx"),m),archived.Active);
        var historical=archived.Snapshot(month);var approved=Store.FundingHistory(historical,month).Single();
        check(!historical.Employees.Any()&&approved.Snapshot.Report(approved.Header,true).Sheets[0].Total==claim.Rows.Sum(r=>r.Amount),"historical approval exports after payroll ages out");
        archived.ChangeFunding(historical,month,true);check(Store.FundingHistory(archived.Snapshot(),month).Single().RevokedAt.HasValue,"revocation preserves archived approval");
    }
}
