using System.Text.Json;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;
public static class WorkReports
{
    public static Statement Funding(A.BackupSnapshot snapshot,string month)=>FundingData(snapshot,month).Report(A.ReportSheet.HeaderFor(snapshot),false);
    public static FundingSnapshot FundingData(A.BackupSnapshot snapshot,string month)
    {
        if(!snapshot.Employees.Any(p=>p.Month==month))throw new InvalidDataException("لا توجد رواتب لهذا الشهر.");
        var b=A.BankStatementCalculator.Calculate(snapshot,month);var groups=A.PayrollAnalysis.Groups(snapshot,month);
        var food=new A.FoodContractReport.Split(b.Breakdown.GetValueOrDefault("شركة عراق الجوال"));
        var lines=new List<FundingRow>();
        void Add(string title,decimal amount)=>lines.Add(new(lines.Count,title,amount));
        Add("رواتب البطاقة",b.Card);Add("رواتب الكاش",b.Cash);
        if(b.UnclassifiedCount>0)Add("رواتب غير مصنفة حسب طريقة الدفع",b.Unclassified);
        Add("الإجازات المتراكمة",b.Manual.AccumulatedLeave);Add("مكافآت المنتسبين",b.Manual.EmployeeRewards);
        string[] known=["الرعاية الصحية","صندوق شهداء الشرطة","الضريبة","الحماية الاجتماعية"];
        foreach(var title in known)Add(title,b.Breakdown.GetValueOrDefault(title));
        Add("عقد عراق الجوال - حصة صندوق الشهداء ٣٪",food.Fund);Add("عقد عراق الجوال - الضريبة ١٫٨٪ من الباقي",food.Tax);Add("عقد عراق الجوال - صافي الشركة",food.Company);
        foreach(var kv in b.Breakdown.OrderBy(k=>k.Key,StringComparer.Ordinal))if(kv.Key!="شركة عراق الجوال"&&!known.Contains(kv.Key))Add(kv.Key,kv.Value);
        if(b.ContractCount>0||b.ContractContribution!=0)Add("الضمان الاجتماعي للعقود",b.ContractContribution);
        if(b.Manual.SingleList!=0)Add("المفردة",b.Manual.SingleList);
        foreach(var g in groups.OrderByDescending(g=>g.Total).ThenBy(g=>g.Title,StringComparer.Ordinal))Add("جهة تنفيذ: "+g.Title,g.Total);
        if(lines.Any(r=>r.Amount<0||r.Amount!=decimal.Truncate(r.Amount))||lines.Sum(r=>r.Amount)>A.SalaryTrust.Maximum||b.BankTotal>A.SalaryTrust.Maximum||groups.Sum(g=>g.Total)!=b.ExecutionDeductions)throw new InvalidDataException("راجع مصادر مبالغ الشهر.");
        return new(month,lines,b.BankTotal,b.UnclassifiedCount,b.ExecutionCount>0,b.MissingNet,b.MissingContractNominal,b.MissingExecutionAmounts,b.ContractOverrideStale);
    }
    internal static Dictionary<string,string> Fields(A.Employee e)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        if(e.RawColumns!=null){using var doc=JsonDocument.Parse(e.RawColumns);var h=doc.RootElement.GetProperty("headers").EnumerateArray().ToArray();
            var v=doc.RootElement.GetProperty("values").EnumerateArray().ToArray();if(h.Length!=v.Length)throw new InvalidDataException("أعمدة الراتب غير مكتملة.");
            for(int i=0;i<h.Length;i++)result.TryAdd(Arabic.Key(h[i].GetString()??""),v[i].GetString()??"");}
        if(e.Details!=null)foreach(var (key,value) in JsonSerializer.Deserialize<Dictionary<string,string>>(e.Details)!)result.TryAdd(Arabic.Key(key),value);
        return result;
    }
    public static Statement Command(A.BackupSnapshot snapshot,string month)
    {
        string[] titles=["صندوق شهداء الشرطة","مديرية التدريب البدني والعاب الشرطة","مديرية شؤون الرعاية الصحية"];
        string[][] aliases=[["صندوق الشهداء","صندوق شهداء الشرطة"],["نشاط رياضي","النشاط الرياضي"],["رعاية صحية","الرعاية الصحية"]];
        var kinds=aliases.SelectMany((names,i)=>names.Select(name=>(name,i))).ToDictionary(x=>Arabic.Key(x.name),x=>x.i);
        var sums=new long[3];var found=new bool[3];
        foreach(var employee in snapshot.Employees.Where(p=>p.Month==month)){
            var row=new long?[3];
            void Record(int kind,decimal? value){
                if(value is null or <0 or >long.MaxValue || value!=decimal.Truncate(value.Value) || (row[kind] is {} old&&old!=value))
                    throw new InvalidDataException("راجع مبلغ "+titles[kind]+" في رواتب الشهر.");
                row[kind]=(long)value.Value;
            }
            void Read(int kind,string? text)=>Record(kind,string.IsNullOrWhiteSpace(text)?0m:A.PayrollSpreadsheet.ParseMoney(text));
            if(employee.RawColumns!=null){
                using var doc=JsonDocument.Parse(employee.RawColumns);var headers=doc.RootElement.GetProperty("headers");var values=doc.RootElement.GetProperty("values");
                if(headers.GetArrayLength()!=values.GetArrayLength())throw new InvalidDataException("أعمدة الراتب غير مكتملة.");
                foreach(var (h,v) in headers.EnumerateArray().Zip(values.EnumerateArray()))
                    if(kinds.TryGetValue(Arabic.Key(h.GetString()??""),out int kind))Read(kind,v.GetString());
            }
            if(row.Contains(null)){
                var absent=row.Select(v=>v==null).ToArray();
                if(employee.Details!=null)foreach(var (title,value) in JsonSerializer.Deserialize<Dictionary<string,string>>(employee.Details)!)
                    if(kinds.TryGetValue(Arabic.Key(title),out int kind)&&absent[kind])Read(kind,value);
                if(employee.Items!=null)foreach(var (title,value) in JsonSerializer.Deserialize<Dictionary<string,decimal>>(employee.Items)!)
                    if(kinds.TryGetValue(Arabic.Key(title),out int kind)&&absent[kind])Record(kind,value);
            }
            for(int i=0;i<row.Length;i++){
                if(row[i] is {} value){found[i]=true;sums[i]=checked(sums[i]+value);}
                else if(employee.RawColumns!=null)throw new InvalidDataException("راجع عمود "+titles[i]+" في رواتب الشهر.");
            }
        }
        if(found.Contains(false))throw new InvalidDataException("لم تُستكمل أعمدة استقطاعات القيادة.");
        List<object?[]> rows=[
            [1,titles[0],5000,4000,4000,2000,(decimal)sums[0]],
            [2,titles[1],4000,2000,2000,0,(decimal)sums[1]],
            [3,titles[2],"لكل فرد بالعائلة ٣٬٥٠٠",null,null,null,(decimal)sums[2]]];
        return new(A.ReportSheet.HeaderFor(snapshot),[new("استقطاعات القيادة","جدول استقطاعات قيادة شرطة الانبار لشهر ( "+Arabic.Eastern(month)+" )",
            ["ت","نوع الاستقطاع","ضباط","موظفين","مراتب","عقود","مجموع التسديد الشهري"],rows,6,sums.Sum(),ShowTotal:false,Command:true)]);
    }
}
