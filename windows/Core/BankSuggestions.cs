using A=SalaryDesktop.Core;
namespace AnbarPayroll;

public sealed record MovementSuggestion(string Id,string Title,long Amount,string[] Lines);
public static class BankSuggestions
{
    public static List<MovementSuggestion> For(A.BackupSnapshot source,A.BankPeriod period)
    {
        var result=new List<MovementSuggestion>();string month=period.Month;
        if(period.IsConfirmed||!source.Employees.Any(e=>e.Month==month))return result;
        var used=period.Movements.Select(m=>m.SourceId).OfType<string>().ToHashSet();
        var bookedLines=period.Movements.SelectMany(m=>m.SourceLineIds??[]).ToHashSet();
        void Add(string id,string title,decimal amount,string[]? lines=null,string phase=""){
            if(amount<=0||amount>A.SalaryTrust.Maximum||decimal.Truncate(amount)!=amount)return;
            var prefix=month+"|"+id;string suffix=phase.Length==0?"":"|"+phase;
            lines??=[];
            // Full and partial transfers share the same underlying sources. Booking either
            // side of a split must still allow the other side to be booked separately.
            if(used.Contains(prefix)||used.Contains(prefix+suffix)||phase.Length==0&&(used.Contains(prefix+"|current")||used.Contains(prefix+"|deferred")))return;
            if(lines.Any(l=>bookedLines.Contains(l)||bookedLines.Contains(l+suffix)||phase.Length==0&&(bookedLines.Contains(l+"|current")||bookedLines.Contains(l+"|deferred"))))return;
            result.Add(new(prefix+suffix,title+(phase=="current"?" · الحالي":phase=="deferred"?" · المؤجل":""),(long)amount,lines.Select(l=>l+suffix).ToArray()));
        }
        string[] Payroll(params string[] keys)=>keys.Select(k=>"payroll|"+month+"|"+k).ToArray();
        var bank=A.BankStatementCalculator.Calculate(source,month);
        var batch=Store.ActiveBatch(source,month);
        Add("card","رواتب البطاقة",bank.Card);Add("cash","رواتب الكاش",bank.Cash,Payroll("cashSalary"));
        Add("unclassified","رواتب غير مصنفة",bank.Unclassified);Add("leave","الإجازات المتراكمة",bank.Manual.AccumulatedLeave);
        Add("rewards","مكافآت المنتسبين",bank.Manual.EmployeeRewards);Add("single","قائمة مفردة",bank.Manual.SingleList);
        var food=new A.FoodContractReport.Split(bank.Breakdown.GetValueOrDefault("شركة عراق الجوال"));
        Add("foodCompany","شركة عراق الجوال",food.Company,Payroll("foodCompany"));Add("foodTax","ضريبة عقد الإطعام",food.Tax,Payroll("foodTax"));
        Add("treasury","الضريبة والحماية الاجتماعية",bank.Breakdown.GetValueOrDefault("الضريبة")+bank.Breakdown.GetValueOrDefault("الحماية الاجتماعية"),Payroll("incomeTax","socialProtection"));
        if(batch!=null)Add("contracts","الضمان الاجتماعي للعقود",batch.Guarantee,Payroll("contractNominal","contractRetirement"),"deferred");
        else if(!bank.ContractOverrideStale&&(bank.MissingContractNominal==0||bank.ManualContractOverride!=null||period.AcceptedContractMissing==bank.MissingContractNominal&&period.AcceptedContractAmount==bank.CalculatedContractContribution))
            Add("contracts","الضمان الاجتماعي للعقود",bank.ContractContribution,Payroll("contractNominal","contractRetirement"));
        void Entities(A.TransferSnapshot plan,string phase){
            string Line(A.TransferEntry e)=>e.Id.StartsWith("deduction|")?"deductions|"+month+"|false|"+e.Title+"|execution":"payroll|"+month+"|false|"+e.Title+"|"+e.Id["payroll|".Length..];
            if(!plan.Entries.Any(e=>e.Group==A.TransferGroup.Unresolved))
                foreach(var group in plan.Entries.Where(e=>e.Group is A.TransferGroup.Rafidain or A.TransferGroup.OtherBanks).GroupBy(e=>e.Group))
                    if(group.All(e=>A.TransferPreparation.Issues(e).Count==0)){
                        bool raf=group.Key==A.TransferGroup.Rafidain;
                        Add("group|"+(raf?"rafidain":"otherBanks"),raf?"مجموعة الرافدين":"مجموعة الرشيد والمصارف الأخرى",group.Sum(e=>e.Amount),group.Select(Line).ToArray(),phase);
                    }
            foreach(var e in plan.Entries.Where(e=>e.Group==A.TransferGroup.Anbar&&e.MissingAmounts==0))Add("individual|"+Line(e),e.Title,e.Amount,[Line(e)],phase);
        }
        if(batch==null)Entities(A.TransferPreparation.Prepare(source,month),"");else{Entities(batch.Plan(false),"current");Entities(batch.Plan(true),"deferred");}
        return result;
    }
}
