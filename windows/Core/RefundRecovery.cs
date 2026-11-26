using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using A=SalaryDesktop.Core;
namespace AnbarPayroll;
public static class RefundRecovery
{
    internal const string Header="Reference|Value Date|Payer Name|Payer Account|Amount|Currency|Receiver BIC|Beneficiary Name|Beneficiary Account|Remittance Information|Details of Charges";
    public static RefundBatch Read(IEnumerable<string> files,string month,IReadOnlyList<A.Employee> employees){
        const string legacy="إعادة مبالغ زائدة في التسديد";
        var people=new List<RefundPayee>();var references=new HashSet<string>();var beneficiaries=new HashSet<string>();
        var roster=employees.Where(e=>e.Month==month).Select(RefundBatch.Employee).ToLookup(p=>p.Iban);
        string? payer=null,account=null,reason=null;DateOnly day=default;long bytes=0;
        foreach(var file in files){
            using var stream=File.OpenRead(file);bytes+=stream.Length;if(bytes>64*1024*1024)throw new InvalidDataException("ملفات CSV أكبر من الحد المدعوم.");
            using var input=new StreamReader(stream,new UTF8Encoding(false,true),false);if(input.ReadLine()?.TrimStart('\uFEFF')!=Header)throw new InvalidDataException("عناوين ملف الإعادة غير مطابقة.");
            int start=people.Count;
            while(input.ReadLine() is {} line){
                if(string.IsNullOrWhiteSpace(line))continue;
                var f=line.Split('|');
                if(f.Length!=11||f[5]!="IQD"||f[10]!="SLEV"||!DateOnly.TryParseExact(f[1],"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
                    ||!long.TryParse(f[4],NumberStyles.None,CultureInfo.InvariantCulture,out var amount)||amount<=0||!A.TransferPreparation.ValidIban(f[3])||!A.TransferPreparation.ValidIban(f[8])||f[3]==f[8]
                    ||(f[9]!="اعادة مبالغ تنفيذ"&&f[9]!=legacy&&!f[9].StartsWith(legacy+" - "))||!beneficiaries.Add(f[8])||!references.Add(f[0]))throw new InvalidDataException("قيد إعادة غير صالح أو متكرر في "+Path.GetFileName(file));
                var code=f[8].Substring(4,4);var prefix=f[1]+(code=="RAFB"?"30000":"20000")+"0";
                if(!f[0].StartsWith(prefix,StringComparison.Ordinal)||f[0].Length>35||!ulong.TryParse(f[0][prefix.Length..],NumberStyles.None,CultureInfo.InvariantCulture,out _))throw new InvalidDataException("مرجع التحويل غير صالح.");
                if(payer!=null&&(day!=date||payer!=f[2]||account!=f[3]||reason!=f[9]))throw new InvalidDataException("الملفات من دفعات مختلفة.");
                payer=f[2];account=f[3];day=date;reason=f[9];
                var matches=roster[f[8]].Where(p=>Arabic.Match(p.Name)==Arabic.Match(f[7])).ToArray();var match=matches.Length==1?matches[0]:null;
                var source=reason.StartsWith(legacy+" - ")?reason[(legacy.Length+3)..]:"";
                var refComputed=RefundBatch.Reference(day,account,f[8],amount,source,f[6]);
                var original=refComputed==f[0]?null:new JsonObject{["recoveredReference"]=new JsonObject{["computed"]=refComputed,["original"]=f[0]}};
                people.Add(new(f[7],match?.Rank??"",match?.Workplace??"",month,f[8],match?.Bank is {Length:>0} name?name:code,f[6],amount,original));
            }
            if(people.Count==start)throw new InvalidDataException("ملف CSV بلا مستفيدين.");
        }
        if(reason==null)throw new InvalidDataException("اختر ملف إعادة واحداً على الأقل.");
        var result=new RefundBatch(Guid.NewGuid().ToString().ToUpperInvariant(),reason.StartsWith(legacy+" - ")?reason[(legacy.Length+3)..]:"",day,payer!,account!,people);
        _=result.Groups();return result;
    }
}
