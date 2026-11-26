using AnbarPayroll;
using A=SalaryDesktop.Core;

internal static class PreferenceExchange
{
    public static void Run(string mode,string file)
    {
        string root=Path.Combine(Path.GetTempPath(),"preference-check-"+Guid.NewGuid().ToString("N"));
        try {
            var store=new Store(root);
            const string month="تشرين الثاني 2026";
            const string refundId="ABC00000-0000-4000-8000-000000000001";
            const string appleId="ABC00000-0000-4000-8000-000000000002";
            var payee=new RefundPayee("اسم تجريبي","موظف","قسم تجريبي",month,AccountingTests.Iban("RSHD",1),"مصرف الرشيد","RSHDIQBA123",1500);
            if(mode=="--write-preference-archive"){
                A.Employee row=new(month,"اسم تجريبي","موظف","قسم تجريبي",15000,null,null,null);
                A.Deduction deduction=new(month,row.Name,"جهة تجريبية",250,"١ / ٢٠٢٦",row.Rank);
                store.ApplyExcel(new(month,[row],[deduction],[]),null);
                store.SaveRefund(new RefundBatch(refundId,"جهة تجريبية",new(2026,11,1),"قسم الحسابات",AccountingTests.Iban("RAFB",9),[payee]),store.Active);
                store.SaveWorkPreferences(store.Active!,new(){["homePinnedWorkItems"]="salaryTrust|bankStatement",["appNumerals"]="arabicIndic",["appTextSize"]="4",["windowsContentTextLevel"]="2"});
                store.ExportBackup(file);
            }else{
                store.Import(file);
                var batches=store.Refunds().ToDictionary(b=>b.Id);
                if(batches.Count!=2 || batches[refundId].People.Single().Amount!=1500 ||
                    batches[appleId].People.Single().Amount!=2450 || batches.Values.Any(b=>
                        b.People.Single().Iban!=payee.Iban || b.Groups().Single().Code!="RSHD" || b.People.Single().Bic!=payee.Bic))
                    throw new InvalidDataException("لم تنتقل الدفعتان بمبالغهما وحساباتهما.");
                var prefs=store.ReadWorkPreferences();
                if(prefs["homePinnedWorkItems"]!="health|bankStatement" || prefs["appNumerals"]!="latin"
                    || prefs["windowsContentTextLevel"]!="2" || prefs["appTextSize"]!="4"
                    || store.Summary(new()).Total!=250 || store.Snapshot(month).Employees.Single().Net!=15000)
                    throw new InvalidDataException("تعذّر استرجاع تعديلات الآيفون أو تغيرت المبالغ.");
            }
            Console.WriteLine("PASS "+mode);
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
