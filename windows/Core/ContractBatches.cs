using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;

public sealed record SplitRow(int Id,string Name,string Rank,string Workplace,decimal Amount,string Note,string Entity,string PersonKey);
public sealed record SplitGroup(string Kind,string Month,string Title,int Count,decimal Total,bool ByEntity=false);
public sealed record SplitEntity(SplitGroup Group,A.ExecutionAccount? Account,string Category,int MissingAmounts,string? BeneficiaryName,string? PayrollRecipient,bool DuplicateSource);
public sealed record SplitPart(SplitEntity Entity,SplitRow[] Current,SplitRow[] Deferred)
{
    public A.TransferEntry? Portion(bool deferred){
        var entity=Entity;var group=entity.Group;var rows=deferred?Deferred:Current;
        bool fixedAmount=entity.PayrollRecipient!=null&&entity.PayrollRecipient!="fundDonations";
        if(fixedAmount?deferred:rows.Length==0)return null;
        return new((entity.PayrollRecipient==null?"deduction|"+group.Title:"payroll|"+entity.PayrollRecipient),group.Month,group.Title,
            fixedAmount?group.Total:rows.Sum(x=>x.Amount),fixedAmount?group.Count:rows.DistinctBy(x=>x.PersonKey).Count(),entity.MissingAmounts,
            entity.Account,Enum.Parse<A.TransferGroup>(entity.Category,true),entity.BeneficiaryName,entity.DuplicateSource);
    }
}
public sealed record ContractBatch(string Id,string Month,double ImportedAt,double Created,JsonObject Figures,string Payer,string Iban,byte[] Links,SplitPart[] Parts)
{
    [System.Text.Json.Serialization.JsonIgnore] public decimal Guarantee=>(decimal?)Figures["contractOverrideTotal"]??((decimal?)Figures["contractNominalShare"]??(decimal?)Figures["availableContractNominalShare"]??0)+(decimal)Figures["contractRetirement"]!;
    [System.Text.Json.Serialization.JsonIgnore] public decimal Held=>Guarantee+Parts.Sum(p=>p.Deferred.Sum(r=>r.Amount));
    public A.TransferSnapshot Plan(bool deferred)=>new(Month,Payer,Iban,Parts.Select(p=>p.Portion(deferred)).OfType<A.TransferEntry>().ToArray(),[],"0"+Arabic.Period(Month)+(deferred?"2":"1"));
}
public sealed partial class Store
{
    const string ContractKey="contractTransferBatchesV1";
    internal static readonly DateTime AppleEpoch=new(2001,1,1,0,0,0,DateTimeKind.Utc);
    [DllImport("lzfse",CallingConvention=CallingConvention.Cdecl)] static extern nuint lzfse_decode_buffer([Out]byte[] dst,nuint dstLength,byte[] src,nuint srcLength,IntPtr scratch);
    [DllImport("lzfse",CallingConvention=CallingConvention.Cdecl)] static extern nuint lzfse_encode_buffer([Out]byte[] dst,nuint dstLength,byte[] src,nuint srcLength,IntPtr scratch);
    static byte[] Packed(byte[] bytes,bool encode){
        const int limit=64*1024*1024;
        if(encode){
            if(bytes.Length==0||bytes.Length>limit)throw new InvalidDataException();
            var buf=new byte[checked(bytes.Length+4096)];int size=checked((int)lzfse_encode_buffer(buf,(nuint)buf.Length,bytes,(nuint)bytes.Length,IntPtr.Zero));
            if(size<=0)throw new InvalidDataException("تعذّر ضغط الدفعة.");
            var result=new byte[36+size];BinaryPrimitives.WriteInt32BigEndian(result,bytes.Length);SHA256.HashData(bytes,result.AsSpan(4,32));buf.AsSpan(0,size).CopyTo(result.AsSpan(36));return result;
        }
        if(bytes.Length<=36)throw new InvalidDataException();int length=BinaryPrimitives.ReadInt32BigEndian(bytes);
        if(length<=0||length>limit)throw new InvalidDataException();
        var outbuf=new byte[length+1];var input=bytes[36..];var count=lzfse_decode_buffer(outbuf,(nuint)outbuf.Length,input,(nuint)input.Length,IntPtr.Zero);
        if(count!=(nuint)length||!SHA256.HashData(outbuf.AsSpan(0,length)).AsSpan().SequenceEqual(bytes.AsSpan(4,32)))throw new InvalidDataException("بصمة دفعة العقود غير مطابقة.");
        return outbuf[..length];
    }
    static byte[] Plist(NSObject value){using var stream=new MemoryStream();PropertyListParser.SaveAsBinary(value,stream);return stream.ToArray();}
    public void SaveAccounts(A.BackupSnapshot s,string payer,string iban,IReadOnlyCollection<A.ExecutionAccount> accounts,IReadOnlyDictionary<string,string>? addedLinks=null){
        string normal=A.TransferPreparation.NormalizeAccount(iban);if(string.IsNullOrWhiteSpace(payer)||payer.IndexOfAny(['|','\n','\r','"'])>=0||!A.TransferPreparation.ValidIban(normal))throw new InvalidDataException("راجع اسم وآيبان الجهة الدافعة.");
        var keys=accounts.Select(a=>A.PayrollAnalysis.Normalize(a.Entity)).ToHashSet();if(keys.Contains("")||keys.Count!=accounts.Count)throw new InvalidDataException("اسم حساب مكرر أو فارغ.");
        var settings=new Dictionary<string,byte[]>{{"bankStatementAccountName",Plist(new NSString(payer.Trim()))},{"bankStatementIBAN",Plist(new NSString(normal))}};
        if(addedLinks!=null){
            var links=s.SettingsPlists.TryGetValue("transferPayrollAccountLinks",out var old)?JsonSerializer.Deserialize<Dictionary<string,string>>(((NSData)PropertyListParser.Parse(old)).Bytes)!:new();
            foreach(var (k,v) in addedLinks){if(!keys.Contains(v))throw new InvalidDataException("حساب الربط غير موجود.");links[k]=v;}
            settings["transferPayrollAccountLinks"]=Plist(new NSData(JsonSerializer.SerializeToUtf8Bytes(links)));
        }
        Rewrite(Path.GetDirectoryName(s.SourcePath),null,settings,accounts:accounts);
    }
    static List<NSDictionary> ContractRecords(A.BackupSnapshot s){
        if(!s.SettingsPlists.TryGetValue(ContractKey,out var bytes))return [];
        var root=(NSDictionary)PropertyListParser.Parse(((NSData)PropertyListParser.Parse(bytes)).Bytes);
        return ((NSArray)root["records"]).Cast<NSDictionary>().ToList();
    }
    static NSDictionary? ActiveRecord(IEnumerable<NSDictionary> records,string month)=>records.SingleOrDefault(r=>r["month"].ToString()==month&&!r.ContainsKey("cancelledAt"));
    public static ContractBatch? ActiveBatch(A.BackupSnapshot s,string month){
        var r=ActiveRecord(ContractRecords(s),month);if(r==null)return null;
        var b=JsonSerializer.Deserialize<ContractBatch>(Packed(((NSData)r["compressed"]).Bytes,false),IosJson)??throw new InvalidDataException();
        if(b.Month!=month||b.Id!=r["id"].ToString()||b.Held!=decimal.Parse(r["held"].ToString()!,System.Globalization.CultureInfo.InvariantCulture))throw new InvalidDataException("دفعة العقود لا تطابق سجلها.");
        return b;
    }
    public static ContractBatch PrepareBatch(A.BackupSnapshot s,string month){
        var t=A.TransferPreparation.Prepare(s,month);var bank=A.BankStatementCalculator.Calculate(s,month);
        if(!A.TransferPreparation.ValidIban(t.PayerAccount)||string.IsNullOrWhiteSpace(t.Payer)||bank.ContractOverrideStale||bank.MissingContractNominal>0&&bank.ManualContractOverride==null)throw new InvalidDataException("راجع الحساب الدافع واعتماد ضمان العقود.");
        if(t.SourceWarnings.Count>0)throw new InvalidDataException(string.Join("\n",t.SourceWarnings));
        if(!bank.ExecutionMatched||s.Employees.All(p=>p.Month!=month))throw new InvalidDataException("راجع تطابق استقطاعات المصارف مع جهات التنفيذ.");
        var employees=s.Employees.Where(p=>p.Month==month).ToLookup(p=>Arabic.Match(p.Name));
        bool Contract(string rank){
            var key=Arabic.Key(rank);var words=key.Split(' ');if(words.Contains("عقود"))return true;
            if(words.Contains("عقد")||new[]{"موظف","مفوض","شرطي","رئيس عرفاء","نائب عريف","عريف","وكيل"}.Select(Arabic.Key).Any(v=>key.StartsWith(v,StringComparison.Ordinal)))return false;
            if(new[]{"لواء","فريق","فريق اول","عميد","عقيد","مقدم","رائد","نقيب","ملازم","ملازم اول","م اول","ر ع","ع م","ن ع","ش1 م","ش م"}.Contains(key.Replace(".","").Trim()))return false;
            throw new InvalidDataException("رتبة غير معروفة عند الفصل: "+rank);
        }
        var cuts=s.Deductions.Where(d=>d.Month==month&&!A.PayrollAnalysis.IsInactive(d)).ToLookup(d=>d.Title);
        var parts=new List<SplitPart>();int serial=0;
        foreach(var e in t.Entries){
            if(e.Group==A.TransferGroup.Unresolved||A.TransferPreparation.Issues(e).Count>0)throw new InvalidDataException("راجع حساب ومبلغ "+e.Title);
            var current=new List<SplitRow>();var deferred=new List<SplitRow>();
            void Add(string name,string rank,string place,decimal? amount,string note){
                if(amount is null or <=0||amount!=decimal.Truncate(amount.Value))throw new InvalidDataException("راجع مبلغ "+name);
                (Contract(rank)?deferred:current).Add(new(++serial,name,rank,place,amount.Value,note,e.Title,Arabic.Match(name)));
            }
            string? recipient=e.Id.StartsWith("payroll|")?e.Id[8..]:null;
            if(recipient==null)foreach(var d in cuts[e.Title]){
                var matches=employees[Arabic.Match(d.Name)].ToArray();if(matches.Length>1)throw new InvalidDataException("هوية مكررة: "+d.Name);
                var p=matches.FirstOrDefault();string rank=string.IsNullOrWhiteSpace(d.Rank)?p?.Rank??"":d.Rank;
                if(!string.IsNullOrWhiteSpace(p?.Rank)&&Contract(rank)!=Contract(p.Rank))throw new InvalidDataException("تعارض رتبة "+d.Name);
                Add(d.Name,rank,p?.Workplace??"",d.Amount,d.Note);
            }
            if(recipient=="fundDonations")foreach(var p in s.Employees.Where(p=>p.Month==month)){
                var f=WorkReports.Fields(p);var aliases=new[]{"صندوق الشهداء","صندوق شهداء الشرطة"};
                var raw=aliases.Select(a=>f.GetValueOrDefault(Arabic.Key(a))).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x));
                var amount=raw==null?(p.Items==null?0:JsonSerializer.Deserialize<Dictionary<string,decimal>>(p.Items)!.Where(k=>aliases.Any(a=>Arabic.Key(a)==Arabic.Key(k.Key))).Select(k=>k.Value).FirstOrDefault()):A.PayrollSpreadsheet.ParseMoney(raw)??throw new InvalidDataException("مبلغ صندوق غير صالح.");
                if(amount!=0)Add(p.Name,p.Rank,p.Workplace,amount,"");
            }
            if((recipient==null||recipient=="fundDonations")&&current.Concat(deferred).Sum(r=>r.Amount)!=e.Amount)throw new InvalidDataException("مجموع الجزأين لا يطابق "+e.Title);
            var entity=new SplitEntity(new("deductions",month,e.Title,e.People,e.Amount),e.Account,JsonNamingPolicy.CamelCase.ConvertName(e.Group.ToString()),0,e.Beneficiary,recipient,e.DuplicateSource);
            parts.Add(new(entity,current.OrderBy(r=>r.Name,StringComparer.Create(new("ar-IQ"),false)).ToArray(),deferred.OrderBy(r=>r.Name,StringComparer.Create(new("ar-IQ"),false)).ToArray()));
        }
        var food=new A.FoodContractReport.Split(bank.Breakdown["شركة عراق الجوال"]);
        var figures=JsonSerializer.SerializeToNode(new{month,foodFund=food.Fund,foodTax=food.Tax,foodCompany=food.Company,healthcare=bank.Breakdown["الرعاية الصحية"],policeFundDonations=bank.Breakdown["صندوق شهداء الشرطة"],sports=bank.Breakdown["النشاط الرياضي"],incomeTax=bank.Breakdown["الضريبة"],socialProtection=bank.Breakdown["الحماية الاجتماعية"],contractNominalShare=bank.ContractNominalShare,contractRetirement=bank.ContractRetirement,payrollCash=bank.Cash,contractOverrideTotal=bank.ManualContractOverride?.Total,contractOverrideSnapshot=bank.ManualContractOverride,contractOverrideStale=false,missingContractNominal=bank.MissingContractNominal,availableContractNominalShare=bank.ContractNominalShare,contractNominalReviewed=bank.MissingContractNominal>0&&bank.ManualContractOverride!=null},IosJson)!.AsObject();
        var b=new ContractBatch(Guid.NewGuid().ToString().ToUpperInvariant(),month,s.Months.Single(m=>m.Name==month).ImportedAt,NowIos,figures,t.Payer,t.PayerAccount,
            s.SettingsPlists.TryGetValue("transferPayrollAccountLinks",out var links)?((NSData)PropertyListParser.Parse(links)).Bytes:[],parts.ToArray());
        if(b.Held<=0)throw new InvalidDataException("لا توجد استقطاعات عقود للتأجيل.");return b;
    }
    public static DateTime? PreparedDate(A.BackupSnapshot s,string month,string key){
        var record=ActiveRecord(ContractRecords(s),month);if(record==null)return null;
        var map=(NSDictionary)record["prepared"];return map.ContainsKey(key)?((NSDate)map[key]).Date.ToLocalTime():null;
    }
    public void UpdateBatch(A.BackupSnapshot s,string month,ContractBatch? insert=null,string? prepared=null,DateTime? date=null){
        var records=ContractRecords(s);var active=ActiveRecord(records,month);
        if(insert!=null){
            if(active!=null||insert.Month!=month)throw new InvalidDataException();
            var rec=new NSDictionary();rec["month"]=new NSString(month);rec["id"]=new NSString(insert.Id);rec["held"]=new NSNumber((long)insert.Held);
            rec["importedAt"]=new NSDate(AppleEpoch.AddSeconds(insert.ImportedAt));rec["prepared"]=new NSDictionary();rec["confirmed"]=new NSDictionary();
            rec["compressed"]=new NSData(Packed(JsonSerializer.SerializeToUtf8Bytes(insert,IosJson),true));records.Insert(0,rec);
        }else if(active==null)throw new InvalidDataException();
        else if(prepared!=null){var issued=(NSDictionary)active["prepared"];if(!issued.ContainsKey(prepared))issued[prepared]=new NSDate(date!.Value.ToUniversalTime());}
        else if(((NSDictionary)active["prepared"]).Count==0)records.Remove(active);
        else active["cancelledAt"]=new NSDate(DateTime.UtcNow);
        var data=new NSDictionary();data["version"]=new NSNumber(records.Any(r=>r.ContainsKey("cancelledAt"))?2:1);data["records"]=new NSArray(records.ToArray());
        var markers=new List<string>();
        foreach(var r in records){var id=r["id"].ToString();if(r.ContainsKey("cancelledAt"))markers.Add(id+"|cancelled");
            foreach(var k in ((NSDictionary)r["prepared"]).Keys)markers.Add(id+"|P|"+k);foreach(var k in ((NSDictionary)r["confirmed"]).Keys)markers.Add(id+"|C|"+k);}
        var ledger=Plist(data);
        var dates=s.SettingsPlists.TryGetValue(ContractKey,out var original)?ContractDates.Values(((NSData)PropertyListParser.Parse(original)).Bytes):new();
        if(insert!=null)dates[(insert.Id,"importedAt")]=BitConverter.DoubleToInt64Bits(insert.ImportedAt);
        ContractDates.Restore(ledger,dates);
        Rewrite(Path.GetDirectoryName(s.SourcePath),null,new Dictionary<string,byte[]>{{ContractKey,records.Count==0?[]:Plist(new NSData(ledger))}},m=>{
            m["contractBatchCount"]=records.Count;m["contractExecutionMarkers"]=JsonSerializer.SerializeToNode(markers.Order(StringComparer.Ordinal));if((int)m["version"]!<14)m["version"]=records.Count==0?12:13;});
    }
}
