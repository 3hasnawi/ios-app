using System.Text.Json;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;
public static class WorkReports
{
    static Sheet Amounts(string name,string title,IEnumerable<(string,decimal)> source)
    {
        var data=source.ToList();
        return new(name,title,["ت","البيان","المبلغ"],data.Select((v,i)=>new object?[]{i+1,v.Item1,v.Item2}).ToList(),2,data.Sum(x=>x.Item2));
    }
    public static Statement Funding(A.BackupSnapshot snapshot,string month)
    {
        if(!snapshot.Employees.Any(p=>p.Month==month))throw new InvalidDataException("لا توجد رواتب لهذا الشهر.");
        var b=A.BankStatementCalculator.Calculate(snapshot,month);var groups=A.PayrollAnalysis.Groups(snapshot,month);
        var food=new A.FoodContractReport.Split(b.Breakdown.GetValueOrDefault("شركة عراق الجوال"));
        var lines=new List<(string,decimal)>{("رواتب البطاقة",b.Card),("رواتب الكاش",b.Cash),("الإجازات المتراكمة",b.Manual.AccumulatedLeave),("مكافآت المنتسبين",b.Manual.EmployeeRewards)};
        if(b.UnclassifiedCount>0)lines.Add(("رواتب غير مصنفة",b.Unclassified));
        lines.AddRange(b.Breakdown.Where(k=>k.Key!="شركة عراق الجوال").Select(k=>(k.Key,k.Value)));
        lines.AddRange([("عقد عراق الجوال · حصة الصندوق",food.Fund),("عقد عراق الجوال · الضريبة",food.Tax),("عقد عراق الجوال · صافي الشركة",food.Company)]);
        if(b.ContractCount>0||b.ContractContribution!=0)lines.Add(("الضمان الاجتماعي للعقود",b.ContractContribution));
        if(b.Manual.SingleList!=0)lines.Add(("المفردة",b.Manual.SingleList));
        lines.AddRange(groups.Select(g=>(g.Title,g.Total)));
        if(lines.Any(x=>x.Item2<0||x.Item2!=decimal.Truncate(x.Item2))||groups.Sum(g=>g.Total)!=b.ExecutionDeductions)throw new InvalidDataException("راجع مصادر مبالغ الشهر.");
        var details=Amounts("المصروفات","المصروفات والتمويل · "+month,lines);
        string status=b.BankTotal==details.Total?"تطابق المبالغ":"يوجد فرق";
        if(b.ExecutionCount==0||b.MissingNet+b.MissingExecutionAmounts+b.MissingContractNominal+b.UnclassifiedCount>0||b.ContractOverrideStale)status+=" · بيانات تحتاج مراجعة";
        var comparison=Amounts("المطابقة",status,[("مجموع المصروفات",details.Total),("مجموع كشف البنك",b.BankTotal),("الفرق",b.BankTotal-details.Total)]) with{ShowTotal=false};
        return new(A.ReportSheet.HeaderFor(snapshot),[details,comparison]);
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
        var sums=new decimal[3];var found=new bool[3];
        foreach(var e in snapshot.Employees.Where(p=>p.Month==month)){
            var fields=Fields(e);var items=e.Items==null?new():JsonSerializer.Deserialize<Dictionary<string,decimal>>(e.Items)!;
            for(int i=0;i<3;i++){
                var values=aliases[i].Select(Arabic.Key).Where(fields.ContainsKey).Select(k=>string.IsNullOrWhiteSpace(fields[k])?0m:A.PayrollSpreadsheet.ParseMoney(fields[k])??throw new InvalidDataException(titles[i])).Distinct().ToList();
                if(values.Count==0)values=items.Where(k=>aliases[i].Any(a=>Arabic.Key(a)==Arabic.Key(k.Key))).Select(k=>k.Value).Distinct().ToList();
                if(values.Count>1||values.Any(v=>v<0||v!=decimal.Truncate(v))||e.RawColumns!=null&&values.Count==0)throw new InvalidDataException("راجع عمود "+titles[i]+" في رواتب الشهر.");
                if(values.Count==1){found[i]=true;sums[i]+=values[0];}
            }
        }
        if(found.Contains(false))throw new InvalidDataException("لم تُستكمل أعمدة استقطاعات القيادة.");
        List<object?[]> rows=[
            [1,titles[0],5000,4000,4000,2000,sums[0]],
            [2,titles[1],4000,2000,2000,0,sums[1]],
            [3,titles[2],"لكل فرد بالعائلة ٣٬٥٠٠",null,null,null,sums[2]]];
        return new(A.ReportSheet.HeaderFor(snapshot),[new("استقطاعات القيادة","جدول استقطاعات قيادة شرطة الانبار لشهر ( "+Arabic.Eastern(month)+" )",
            ["ت","نوع الاستقطاع","ضباط","موظفين","مراتب","عقود","مجموع التسديد الشهري"],rows,6,sums.Sum(),ShowTotal:false,Command:true)]);
    }
}
