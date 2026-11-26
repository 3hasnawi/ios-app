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
            if(mode=="--write-preference-archive"){
                A.Employee row=new(month,"اسم تجريبي","موظف","قسم تجريبي",15000,null,null,null);
                A.Deduction deduction=new(month,row.Name,"جهة تجريبية",250,"١ / ٢٠٢٦",row.Rank);
                store.ApplyExcel(new(month,[row],[deduction],[]),null);
                store.SaveWorkPreferences(store.Active!,new(){["homePinnedWorkItems"]="salaryTrust|bankStatement",["appNumerals"]="arabicIndic",["appTextSize"]="4",["windowsContentTextLevel"]="2"});
                store.ExportBackup(file);
            }else{
                store.Import(file);
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
