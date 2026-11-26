using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using A=SalaryDesktop.Core;
namespace AnbarPayroll;
public sealed record FundingRow(int Id,string Title,decimal Amount);
public sealed record FundingSnapshot(string Month,List<FundingRow> Rows,decimal BankTotal,int UnclassifiedCount,bool HasExecutionRows,int MissingNet,int MissingContractNominal,int MissingExecutionAmount,bool ContractOverrideStale)
{
    public Statement Report(string header,bool approved){
        decimal total=Rows.Sum(r=>r.Amount);
        bool incomplete=!HasExecutionRows||UnclassifiedCount+MissingNet+MissingContractNominal+MissingExecutionAmount>0||ContractOverrideStale;
        string state=approved?"معتمد يدوياً":"غير معتمد";
        if(incomplete)state+=" · بيانات تحتاج مراجعة";
        var details=new Sheet("المصروفات",state+" · المصروفات والتمويل · "+Month,["ت","البيان","المبلغ"],Rows.Select(r=>new object?[]{r.Id+1,r.Title.StartsWith("جهة تنفيذ: ")?r.Title["جهة تنفيذ: ".Length..]:r.Title,r.Amount}).ToList(),2,total);
        var totals=new Sheet("المطابقة",state+" · "+(BankTotal==total?"تطابق المبالغ":"يوجد فرق"),["ت","البيان","المبلغ"],[[1,"مجموع المصروفات",total],[2,"مجموع كشف البنك",BankTotal],[3,"الفرق",BankTotal-total]],2,0,ShowTotal:false);
        return new(header,[details,totals]);
    }
}
public sealed record FundingRecord(Guid Id,double ConfirmedAt,FundingSnapshot Snapshot,string SourceRevision,string Header,double? RevokedAt=null);
public sealed partial class Store
{
    const string FundingKey="fundingExpenseApprovalsV1";
    static JsonObject FundingLedger(A.BackupSnapshot s){
        if(!s.SettingsPlists.TryGetValue(FundingKey,out var bytes))return new(){["version"]=1,["records"]=new JsonArray()};
        var raw=PlistData.Unwrap(bytes);if(raw.Length>16000000)throw new InvalidDataException("سجل الاعتمادات أكبر من الحد المدعوم.");
        var root=JsonNode.Parse(raw)!.AsObject();
        if((int?)root["version"]!=1||root["records"] is not JsonArray rows||rows.Count>5000)throw new InvalidDataException("سجل الاعتمادات غير صالح.");
        var records=rows.Deserialize<List<FundingRecord>>(IosJson)!;
        if(records.Select(r=>r.Id).Distinct().Count()!=records.Count)throw new InvalidDataException("تكرار معرّف اعتماد المصروفات.");
        foreach(var r in records){
            var f=r.Snapshot;
            bool Money(decimal x)=>x>=0&&x<=A.SalaryTrust.Maximum&&x==decimal.Truncate(x);
            if(r.Id==Guid.Empty||r.ConfirmedAt is <0 or >7_000_000_000||r.RevokedAt<r.ConfirmedAt||r.RevokedAt>7_000_000_000||
                string.IsNullOrWhiteSpace(f.Month)||f.Month.Length>200||string.IsNullOrEmpty(r.SourceRevision)||r.SourceRevision.Length>200||r.Header.Length>400||
                f.Rows.Count is 0 or >20000||f.Rows.Where((v,i)=>v.Id!=i||string.IsNullOrEmpty(v.Title)||v.Title.Length>1000||!Money(v.Amount)).Any()||
                !Money(f.Rows.Sum(v=>v.Amount))||!Money(f.BankTotal)||new[]{f.MissingNet,f.UnclassifiedCount,f.MissingExecutionAmount,f.MissingContractNominal}.Min()<0)
                throw new InvalidDataException("قيم غير صالحة في سجل اعتماد المصروفات.");
        }
        return root;
    }
    public static List<FundingRecord> FundingHistory(A.BackupSnapshot s,string? month)=>FundingLedger(s)["records"]!.Deserialize<List<FundingRecord>>(IosJson)!.Where(r=>month==null||r.Snapshot.Month==month).ToList();
    static string FundingRevision(A.BackupSnapshot s,string month){
        var m=s.Months.Single(x=>x.Name==month);
        var time=(m.ImportedAt+978307200d).ToString("R",CultureInfo.InvariantCulture);
        if(!time.Contains('.')&&!time.Contains('E'))time+=".0";
        return time+"|"+m.EmployeeCount+"|"+m.DeductionCount;
    }
    public static bool IsFundingApproved(A.BackupSnapshot s,FundingSnapshot current){
        var last=FundingHistory(s,current.Month).LastOrDefault();
        return last!=null&&last.RevokedAt==null&&last.SourceRevision==FundingRevision(s,current.Month)&&last.Header==A.ReportSheet.HeaderFor(s)&&
            JsonNode.DeepEquals(JsonSerializer.SerializeToNode(last.Snapshot,IosJson),JsonSerializer.SerializeToNode(current,IosJson));
    }
    public void ChangeFunding(A.BackupSnapshot s,string month,bool revoke){
        var ledger=FundingLedger(s);var records=ledger["records"]!.AsArray();
        var current=records.LastOrDefault(r=>(string?)r?["snapshot"]?["month"]==month);
        if(revoke){
            if(current==null||current["revokedAt"]!=null)throw new InvalidDataException("لا يوجد اعتماد نافذ.");
            current["revokedAt"]=NowIos;
        }else{
            var snap=WorkReports.FundingData(s,month);var revision=FundingRevision(s,month);
            if(records.Count>=5000)throw new InvalidDataException("بلغ سجل الاعتمادات الحد المدعوم.");
            var record=new FundingRecord(Guid.NewGuid(),NowIos,snap,revision,A.ReportSheet.HeaderFor(s));
            if(current!=null&&current["revokedAt"]==null&&JsonNode.DeepEquals(current["snapshot"],JsonSerializer.SerializeToNode(snap,IosJson))&&(string?)current["sourceRevision"]==revision&&(string?)current["header"]==record.Header)throw new InvalidDataException("الكشف الحالي معتمد بالفعل.");
            records.Add(JsonSerializer.SerializeToNode(record,IosJson));
        }
        var data=JsonSerializer.SerializeToUtf8Bytes(ledger);if(data.Length>16000000)throw new InvalidDataException("سجل الاعتمادات أكبر من الحد المدعوم.");
        Rewrite(Path.GetDirectoryName(s.SourcePath),null,new Dictionary<string,byte[]>{{FundingKey,Plist(new NSData(data))}},m=>m["contentVersion"]=Math.Max(8,(int?)m["contentVersion"]??5));
    }
}
