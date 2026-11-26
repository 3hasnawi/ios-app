using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using AnbarPayroll;
using ClosedXML.Excel;
using A=SalaryDesktop.Core;

static class AccountingTests
{
    public static void Volume(string dir,Action<bool,string> check){
        string[] headers=Enumerable.Range(0,72).Select(i=>"بند حسابي تجريبي "+i).ToArray();
        string[] vals=Enumerable.Repeat("0",headers.Length).ToArray();
        var raw=JsonSerializer.SerializeToUtf8Bytes(new{headers,values=vals});
        const int count=20_000;var clock=Stopwatch.StartNew();
        var store=new Store(Path.Combine(dir,"volume"));
        foreach(var month in new[]{"أيلول 2026","تشرين الأول 2026"}){
            var rows=Enumerable.Range(1,count).Select(i=>new A.Employee(month,"اسم اختباري "+i,"موظف","قسم تجريبي",i,null,null,raw)).ToList();
            var cuts=Enumerable.Range(1,count).Select(i=>new A.Deduction(month,"اسم اختباري "+i,"جهة تجريبية",i,"إضبارة طويلة رقم "+i,"موظف")).ToList();
            store.ApplyExcel(new(month,rows,cuts,[]),store.Active);
        }
        using var zip=ZipFile.OpenRead(store.Archive);
        using var schemas=JsonDocument.Parse(zip.GetEntry("payroll-headers.json")!.Open());
        long size=new FileInfo(store.Archive).Length;
        check(schemas.RootElement.GetArrayLength()==1&&size<(long)raw.Length*count/10&&store.Summary(new(Year:2026)).Total==(decimal)count*(count+1),"40,000 payroll + 40,000 deductions, one schema, all amounts verified");
        Console.WriteLine($"Fictional stress archive {size:N0} bytes; {clock.Elapsed.TotalSeconds:F1} seconds");
    }
    public static void Run(string dir, Action<bool,string> check){
        string[] periods=["أيلول 2026","تشرين الأول 2026","تشرين الثاني 2026"];
        string Iban(string code,int number){
            var bban=code+number.ToString("D15");
            var decimalText=string.Concat((bban+"IQ00").Select(c=>char.IsAsciiLetter(c)?(c-'A'+10).ToString():c.ToString()));
            return "IQ"+(98-(int)(BigInteger.Parse(decimalText)%97)).ToString("D2")+bban;
        }
        var bank="مصرف الرشيد / فرع تجريبي";var other="مديرية تنفيذ تجريبية";
        string[] names=["أول مثال","ثاني مثال","ثالث مثال"];string[] ranks=["عقود","عقد","شرطي"];
        string[] bankCodes=["RDBA","RDBA","RAFB"];string[] bics=["RDBAIQBA111","RDBAIQBA222","RAFBIQBA111"];
        string xlsx=Path.Combine(dir,"monthly.xlsx");
        using(var w=new XLWorkbook()){
            var p=w.AddWorksheet("الرواتب");string[] heads=["الاسم","الرتبة","محل العمل","صافي الراتب","IBAN","الرفرنس","توطين المصرف","طريقة الدفع","الاسمي","توقيفات تقاعدية","مجموع استقطاعات المصارف","صندوق الشهداء","الرعاية الصحية","نشاط رياضي","الضريبة","حماية الاجتماعية","عراق الجوال","مبلغ الاستقطاع 2","اسم الاستقطاع 2","تضمين","رسم طابع"];
            for(int c=0;c<heads.Length;c++)p.Cell(1,c+1).Value=heads[c];
            for(int i=0;i<3;i++){
                string[] values=[names[i],ranks[i],"قسم تجريبي","5000",Iban(bankCodes[i],i+1),bics[i],i==2?"مصرف الرافدين":bank,"بطاقة",i==0?"178723001":"10000",i==0?"50":"0",i==0?"300":"100","0","0","0","0","0","0","15","بند ثانٍ","9","11"];
                for(int c=0;c<values.Length;c++)p.Cell(i+2,c+1).Value=values[c];
            }
            var d=w.AddWorksheet("الاستقطاعات");string[] cols=["الرتبة","الاسم","الفرع","المبلغ","الملاحظات"];
            for(int c=0;c<cols.Length;c++)d.Cell(1,c+1).Value=cols[c];
            for(int i=0;i<5;i++){int person=i==1?0:i==0?0:Math.Min(i-1,2);
                string[] row=[ranks[person],names[person],i==1?other:bank,i==4?"0":i==0?"200":"100","٠٠١ / ٢٠٢٥ في ١٢/٣"];
                for(int c=0;c<row.Length;c++)d.Cell(i+2,c+1).Value=row[c];
            }
            w.SaveAs(xlsx);
        }
        ImportWorkbook Inspect(string path){using var input=File.OpenRead(path);return ImportWorkbook.Inspect(input);}
        check(Inspect(xlsx).Kind==(ImportData.Salaries|ImportData.Deductions),"identify payroll and deductions together by worksheet columns");
        using(var single=new XLWorkbook(xlsx)){
            single.Worksheet("الرواتب").Delete();
            string path=Path.Combine(dir,"arbitrary.xlsx");single.SaveAs(path);
            check(Inspect(path).Kind==ImportData.Deductions,"identify deductions without relying on the filename");
        }
        using(var salaryBook=new XLWorkbook(xlsx)){
            salaryBook.Worksheet("الاستقطاعات").Delete();
            string path=Path.Combine(dir,"salary-only.xlsx");salaryBook.SaveAs(path);
            check(Inspect(path).Kind==ImportData.Salaries,"salary columns take precedence over embedded deduction columns");
        }
        using(var staffBook=new XLWorkbook()){
            var tab=staffBook.AddWorksheet("ورقة 1");
            tab.Cell(1,1).Value="عنوان تجريبي";tab.Cell(3,1).Value="الاسم الكامل";tab.Cell(3,2).Value="الرقم الإحصائي";
            tab.Cell(4,1).Value="منتسب تجريبي";tab.Cell(4,2).Value="00021";
            string path=Path.Combine(dir,"roster.xlsx");staffBook.SaveAs(path);
            var staffPreview=Inspect(path);
            check(staffPreview.Kind==ImportData.Staff&&staffPreview.Members.Single().Stat=="00021","recognize roster and preserve text identifiers");
            var trust=staffBook.AddWorksheet("ورقة 2");string[] headers=["الاسم","المبلغ","الشهر","السنة","حالة الصرف"];
            for(int i=0;i<headers.Length;i++)trust.Cell(1,i+1).Value=headers[i];
            staffBook.SaveAs(path);
            try{Inspect(path);throw new Exception("unsafe mixed replacement");}catch(InvalidDataException){check(true,"reject mixed independent replacements");}
            tab.Delete();staffBook.SaveAs(path);
            check(Inspect(path).Kind==ImportData.Trust,"recognize trusts before requesting a payroll month");
        }
        var s=new Store(Path.Combine(dir,"work"));foreach(var m in periods){var preview=s.PreviewExcel(xlsx,m);check(preview.Employees.Count==3&&preview.Deductions.Count==4,"mixed Excel with zero-value rows skipped "+m);s.ApplyExcel(preview,s.Active);}
        string month=periods[^1];var snapshot=s.Snapshot(periods);
        check(snapshot.Employees.Count==6&&snapshot.Deductions.Count==12,"third payroll replaces oldest without deleting historical deductions");
        var employee=snapshot.Employees[0];var items=JsonSerializer.Deserialize<Dictionary<string,decimal>>(employee.Items!)!;
        check(items["بند ثانٍ"]==15&&items["تضمين"]==9&&items["رسم طابع"]==11,"paired and named deduction columns retain actual values");
        using(var zip=ZipFile.OpenRead(s.Archive)){using var schemas=JsonDocument.Parse(zip.GetEntry("payroll-headers.json")!.Open());check(schemas.RootElement.GetArrayLength()==1,"one shared header for two payroll months");}
        var payer=Iban("RAFB",42);s.SaveAccounts(snapshot,"جهة دافعة تجريبية",payer,[new(bank,Iban("RDBA",31),bank,"RDBAIQBA111",null),new(other,Iban("RAFB",32),"مصرف الرافدين / فرع تجريبي","RAFBIQBA111",null)]);
        var snap=s.Snapshot(month);
        var batch=Store.PrepareBatch(snap,month);
        check(batch.Guarantee==21446760m+50&&batch.Held==batch.Guarantee+300,"nearest ten, guarantee plus two deferred agencies");
        check(batch.Plan(false).Entries.Sum(x=>x.Amount)==200&&batch.Plan(true).Entries.Sum(x=>x.Amount)==300,"rank عقد remains current, عقود deferred");
        s.UpdateBatch(snap,month,batch);snap=s.Snapshot(month);
        var saved=Store.ActiveBatch(snap,month)!;check(saved.Id==batch.Id&&saved.Held==batch.Held,"frozen split survives plist + LZFSE + archive replacement");
        var day=new DateOnly(2026,11,20);var inclusive=A.TransferPreparation.Prepare(snap,month);
        var refs=new HashSet<string>();foreach(var plan in new[]{inclusive,saved.Plan(false),saved.Plan(true)}){
            var file=A.GroupLetterPreparation.Prepare(plan,A.TransferGroup.OtherBanks,day);var rows=System.Text.Encoding.UTF8.GetString(file.Csv).Split("\r\n").Skip(1).Select(r=>r.Split('|')).ToArray();
            check(rows.All(r=>refs.Add(r[0]))&&rows.Sum(r=>long.Parse(r[4]))==file.Total,"phase reference and letter equal CSV "+plan.ReferenceScope);
        }
        var funding=WorkReports.Funding(snap,month);check(funding.Sheets[1].Rows[^1][2] is decimal diff&&diff==0,"expense details equal bank funding");
        check(WorkReports.Command(snap,month).Sheets[0].Rows.Count==3,"three command deductions from same period");
        var duplicate=JsonSerializer.SerializeToUtf8Bytes(new{headers=new[]{"صندوق الشهداء","صندوق الشهداء","نشاط رياضي","رعاية صحية"},values=new[]{"10","20","0","0"}});
        try{WorkReports.Command(snap with{Employees=[snap.Employees[0] with{RawColumns=duplicate}]},month);throw new Exception("conflicting command columns");}catch(InvalidDataException){check(true,"command report rejects conflicting duplicate payroll columns");}
        var refund=new RefundBatch(Guid.NewGuid().ToString().ToUpperInvariant(),"جهة تجريبية",day,"جهة دافعة تجريبية",payer,snap.Employees.Select((e,i)=>RefundBatch.Employee(e) with{Amount=1000+i}).ToList());
        s.SaveRefund(refund,s.Active);snap=s.Snapshot(month);var returned=s.Refunds().Single();var groups=returned.Groups();
        check(groups.Count==2&&groups.Sum(g=>g.Total)==3003&&groups.Single(g=>g.Code=="RAFB").Bic=="RAFBIQB1098","refund archive preserves names, individual amounts and letter BIC");
        var csv=System.Text.Encoding.UTF8.GetString(returned.Csv(groups.Single(g=>g.Code=="RDBA")));check(csv.Contains(bics[0])&&csv.Contains(bics[1])&&csv.Contains("اعادة مبالغ تنفيذ"),"per-person payroll BIC remains in bank CSV");
        var refundMovement=new A.BankMovement(Guid.NewGuid(),day,"مبلغ مسترجع",200,A.MovementDirection.Credit,IsRefund:true,Matched:true);
        var movements=new[]{new A.BankMovement(Guid.NewGuid(),day,"تمويل",1000,A.MovementDirection.Credit,IsFunding:true,Matched:true),refundMovement,new(Guid.NewGuid(),day,"صرف",1000,A.MovementDirection.Debit,Matched:true),new(Guid.NewGuid(),day,"صرف مبلغ مسترجع",200,A.MovementDirection.Debit,RefundOf:refundMovement.Id,Matched:true)};
        var period=new A.BankPeriod(Guid.NewGuid(),month,day,day.AddDays(10),0,"أول تجريبي","ثانٍ تجريبي",null,null,movements);
        var approved=A.BankReconciliation.Confirm(period,DateTime.UtcNow);s.SaveBank(snap,[approved]);snap=s.Snapshot(month);
        var reopened=A.BankReconciliation.ReadFromBackup(snap).Single();var totals=A.BankReconciliation.Calculate(reopened);check(reopened.IsConfirmed&&totals.Balance==0&&totals.OutstandingRefunds.Count==0,"approved reconciliation with linked refund survives Windows edit");
        try{s.SaveBank(snap,[reopened with{OpeningBalance=1}]);throw new Exception("modified locked ledger");}catch(InvalidDataException){check(true,"approved period rejects editing");}
        MonthlyCycleTests.Run(dir,s,returned,period,check);
        string exchange=Path.Combine(dir,"exchange");Directory.CreateDirectory(exchange);
        File.Copy(s.Archive,Path.Combine(exchange,"windows-exchange.zip"),true);
        using(var zip=ZipFile.OpenRead(s.Archive)){
            using var line=new StreamReader(zip.GetEntry("refunds.jsonl")!.Open());using var doc=JsonDocument.Parse(line.ReadLine()!);
            File.WriteAllBytes(Path.Combine(exchange,"refund.json"),doc.RootElement.GetProperty("contents").GetBytesFromBase64());
        }
        foreach(var group in groups)File.WriteAllBytes(Path.Combine(exchange,group.Code+".csv"),returned.Csv(group));
        var rev=s.Active;s.UpdateBatch(snap,month,prepared:"current|group|otherBanks",date:day.ToDateTime(TimeOnly.MinValue));snap=s.Snapshot(month);s.UpdateBatch(snap,month);
        check(Store.ActiveBatch(s.Snapshot(),month)==null&&A.TransferPreparation.Prepare(s.Snapshot(month),month).Entries.Sum(e=>e.Amount)==500,"cancel a prepared split restores full amounts");
        try{s.SaveRefund(returned,rev,true);throw new Exception("stale writer");}catch(IOException){check(true,"concurrent stale changes blocked");}
        var cancelled=s.Active;
        try{s.Import(Path.Combine(exchange,"windows-exchange.zip"));throw new Exception("rolled back a prepared batch");}catch(InvalidDataException){check(s.Active==cancelled,"restore cannot erase a prepared or cancelled payment");}
        var firstSize=new FileInfo(s.Archive).Length;for(int i=0;i<2;i++)s.ApplyExcel(s.PreviewExcel(xlsx,month),s.Active);
        check(Math.Abs(new FileInfo(s.Archive).Length-firstSize)<400&&s.Refunds().Count==1,"reimport does not inflate archive or discard saved refund");
        check(s.SearchPeople("ثاني").Count>=2,"global lookup includes both payroll and deductions");
        s.SaveRefund(returned with{People=returned.People.Select(p=>p with{Amount=p.Amount+10}).ToList()},s.Active);
        check(s.Refunds().Single().People.Sum(p=>p.Amount)==3033,"saved refund can be edited without duplication");
        var style=new A.GroupLetterSettings("ترويسة","", "توقيع تجريبي","","","",null,null,10,null);
        var edited=s.Refunds().Single().WithLetter(false,style);
        s.SaveRefund(edited,s.Active);
        check(s.Refunds().Single().LetterStyle(false,style with{First=""}).First==style.First,"edited refund letter signature survives archive");
        s.ApproveContract(s.Snapshot(month),month,12345);
        check(A.BankStatementCalculator.Calculate(s.Snapshot(month),month).ContractContribution==12345,"approved contract amount survives archive");
        var bankSource=s.Snapshot(month);
        var monthly=A.BankStatementCalculator.Calculate(bankSource,month);
        var monthlySheet=A.MonthlyBankReport.Sheet(bankSource,monthly);
        monthlySheet.Validate();
        check(!monthlySheet.ShowTotal && monthlySheet.Amounts.Last()==monthly.Net+monthly.BankDeductions+monthly.Breakdown.Values.Sum()+12345+monthly.Manual.Total
            && monthlySheet.Title.Contains(month),"monthly bank report includes approved contracts once and excludes execution comparison and subtotals");
        var partial=new A.ContractOverride(770,0,20,2,2);
        var encoded=A.ContractOverrides.Set(null,month,partial);
        check(A.ContractOverrides.Read(snap with{SettingsPlists=new Dictionary<string,byte[]>{{A.ContractOverrides.SettingsKey,encoded}}},month)==partial,"iPhone approval with two missing nominal salaries is readable");
        var custom=new A.LetterTemplate{Title="كتاب اختبار",Source="payroll",SourceName="تضمين",Body="مبلغ ({{المبلغ}})",FirstSigner="صفة\nاسم\nمنصب",BodyBold=true,BodyKashida="high",FontSizes=new(){{"body",13}},SignatureHorizontalOffset=-10};
        snap=s.Snapshot(month);s.SaveLetterTemplate(snap,null,custom);
        try{s.SaveLetterTemplate(snap,null,custom);throw new Exception("stale template write");}catch(IOException){check(true,"concurrent template edits preserve archive");}
        snap=s.Snapshot(month);var savedTemplate=Store.LetterLibrary(snap).Single();
        check(savedTemplate.Id==custom.Id&&savedTemplate.FontSizes!["body"]==13&&A.AdaptiveLetterPdf.SignatureCells(savedTemplate.Style()," / ١ / ٢٠٢٦")[2].StartsWith("صفة"),"saved custom layout and left single signature");
        var built=custom with{Title="كتاب المجموعة",Source="none",SourceName="",Body=A.GroupLetterPdf.DefaultBodyTemplate};
        s.SaveLetterTemplate(snap,"rafidain_group",built);snap=s.Snapshot(month);
        check(A.GroupLetterSettingsReader.Read(snap,"rafidain_group").Template?.BodyKashida=="high"&&Store.LetterLibrary(snap).Single().Id==custom.Id,"group edit preserves custom library");
        File.Copy(s.Archive,Path.Combine(exchange,"windows-templates.zip"),true);
        s.SaveLetterTemplate(snap,null,custom,true);check(Store.LetterLibrary(s.Snapshot(month)).Count==0,"delete only the selected custom book");
        s.SaveRefund(s.Refunds().Single(),s.Active,true);check(s.Refunds().Count==0,"delete selected refund only");
    }
}
