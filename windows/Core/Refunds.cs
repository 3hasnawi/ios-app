using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;
public sealed record RefundPayee(string Name,string Rank,string Workplace,string Month,string Iban,string Bank,string Bic,long Amount,JsonObject? Original=null);
public sealed record RefundBank(string Code,string Name,string Bic,List<RefundPayee> People){public long Total=>People.Sum(p=>p.Amount);}
public sealed record RefundBatch(string Id,string Source,DateOnly Date,string Payer,string Account,List<RefundPayee> People,JsonObject? Original=null,string? ArchiveId=null)
{
    public static RefundPayee Employee(A.Employee e){
        var fields=WorkReports.Fields(e);
        string Get(string label)=>fields.GetValueOrDefault(Arabic.Key(label))??"";
        string Format(string v)=>A.TransferPreparation.NormalizeAccount(string.Concat(v.Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.Format)));
        string HeaderKey(string t)=>string.Concat(Arabic.Key(t).Where(char.IsLetterOrDigit));
        var bicKeys=new[]{"الرفرنس", "رفرنس", "SWIFT", "BIC", "SWIFT Code", "BIC Code", "SWIFT/BIC", "BIC/SWIFT", "Receiver BIC", "Bank BIC",
        "Bank Swift Code", "Branch BIC", "Branch Swift Code", "Beneficiary BIC",
        "سويفت", "سوفت", "سويفت كود", "سوفت كود", "رمز السويفت", "رمز السوفت",
        "رمز سويفت", "رمز سوفت", "كود السويفت", "كود السوفت", "السويفت كود", "السوفت كود",
        "السويفت", "السوفت", "سويفت المصرف", "سوفت المصرف", "سويفت الفرع", "سوفت الفرع",
        "رمز السويفت للمصرف", "رمز السويفت للفرع", "رمز SWIFT", "رمز BIC"}.Select(HeaderKey).ToHashSet();
        var codes=new HashSet<string>();
        void Add(string key,string value){if(bicKeys.Contains(HeaderKey(key))&&Format(value) is {Length:>0} code&&code!="0")codes.Add(code);}
        if(e.RawColumns!=null){using var doc=JsonDocument.Parse(e.RawColumns);foreach(var (k,v) in doc.RootElement.GetProperty("headers").EnumerateArray().Zip(doc.RootElement.GetProperty("values").EnumerateArray()))Add(k.GetString()!,v.GetString()!);}
        if(e.Details!=null)foreach(var (k,v) in JsonSerializer.Deserialize<Dictionary<string,string>>(e.Details)!)Add(k,v);
        return new(e.Name,e.Rank,e.Workplace,e.Month,Format(Get("IBAN")),Get("توطين المصرف"),codes.Count==1?codes.Single():"",0);
    }
    internal const string Copies="نسخة منه إلى\nشعبة المصروفات / شعبة الموازنة / شعبة الرواتب / إجراء اللازم والمتابعة.";
    static JsonObject DefaultLetter()=>new(){["header"]="جمهورية العراق\nوزارة الداخلية\nقيادة شرطة محافظة الأنبار\nقسم الحسابات",["number"]="١‏/٢٢/ح/",["recipient"]="مصرف الرافدين / فرع الأنبار",["first"]="",["second"]="",["third"]="",["signatureOffset"]=0,["footer"]=Copies};
    internal static JsonObject DefaultLetters()=>new(){["rafidain"]=DefaultLetter(),["otherBanks"]=DefaultLetter()};
    public A.GroupLetterSettings LetterStyle(bool rafidain,A.GroupLetterSettings fallback){
        if(Original?["letters"]?[rafidain?"rafidain":"otherBanks"] is not JsonObject saved)return fallback with{Footer=Copies};
        return fallback with{Header=(string?)saved["header"]??fallback.Header,First=(string?)saved["first"]??fallback.First,Second=(string?)saved["second"]??fallback.Second,
            Third=(string?)saved["third"]??fallback.Third,SignatureOffset=(int?)saved["signatureOffset"]??fallback.SignatureOffset,Footer=Copies};
    }
    public RefundBatch WithLetter(bool rafidain,A.GroupLetterSettings style){
        var data=Original?.DeepClone().AsObject()??new();data["letters"]??=DefaultLetters();var letters=data["letters"]!.AsObject();
        var letter=letters[rafidain?"rafidain":"otherBanks"]!.AsObject();
        letter["header"]=style.Header;letter["first"]=style.First;letter["second"]=style.Second;letter["third"]=style.Third;letter["signatureOffset"]=style.SignatureOffset;letter["footer"]=Copies;
        return this with{Original=data};
    }
    public List<RefundBank> Groups(){
        static bool Safe(string s)=>s.Trim().Length>0&&s.IndexOfAny(['|','\n','\r','"'])<0;
        if(!A.TransferPreparation.ValidIban(Account)||!Safe(Payer)||Source.Length>0&&!Safe(Source)||People.Count==0)throw new InvalidDataException("أكمل بيانات الجهة الدافعة والمستفيدين.");
        var rows=new Dictionary<string,List<RefundPayee>>();var seen=new HashSet<string>();long sum=0;
        foreach(var p in People){
            if(!Safe(p.Name)||!A.TransferPreparation.ValidIban(p.Iban)||p.Iban==Account||!seen.Add(p.Iban)||!A.TransferPreparation.ValidSwift(p.Bic)||!p.Bic.StartsWith(p.Iban.Substring(4,4)+"IQ")||p.Amount<=0||p.Amount>999_999_999_999_999L-sum)throw new InvalidDataException("راجع المبلغ والسويفت وتفرّد آيبان "+p.Name);
            sum+=p.Amount;var bank=p.Iban.Substring(4,4);if(!rows.TryGetValue(bank,out var list))rows[bank]=list=[];list.Add(p);
        }
        return rows.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(pair=>{
            var saved=Original?["banks"]?[pair.Key];var preferred=(string?)saved?["bic"];
            var name=pair.Key=="RAFB"?"مصرف الرافدين / فرع المستنصر":(string?)saved?["name"]??pair.Value.Select(p=>p.Bank).FirstOrDefault(Safe)??"";
            if(!Safe(name))throw new InvalidDataException("راجع اسم المصرف "+pair.Key);
            var bic=pair.Key=="RAFB"?"RAFBIQB1098":pair.Value.GroupBy(p=>p.Bic).OrderByDescending(g=>g.Count()).ThenByDescending(g=>g.Key==preferred).ThenBy(g=>g.Key,StringComparer.Ordinal).First().Key;
            return new RefundBank(pair.Key,name,bic,pair.Value);
        }).ToList();
    }
    public byte[] Csv(RefundBank group){
        var day=Date.ToString("yyyyMMdd",CultureInfo.InvariantCulture);var reason="إعادة مبالغ زائدة في التسديد"+(Source.Length>0?" - "+Source:"");
        var lines=new List<string>{"Reference|Value Date|Payer Name|Payer Account|Amount|Currency|Receiver BIC|Beneficiary Name|Beneficiary Account|Remittance Information|Details of Charges"};
        foreach(var p in group.People){
            var amount=p.Amount.ToString(CultureInfo.InvariantCulture);
            var hash=SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',day,Account,p.Iban,amount,reason,p.Bic)));
            string reference=day+(group.Code=="RAFB"?"30000":"20000")+"0"+BinaryPrimitives.ReadUInt64BigEndian(hash).ToString(CultureInfo.InvariantCulture);
            var recovered=p.Original?["recoveredReference"];if((string?)recovered?["computed"]==reference&&((string?)recovered?["original"]) is {} original&&original.All(char.IsAsciiDigit))reference=original;
            lines.Add(string.Join('|',reference,day,Payer,Account,amount,"IQD",p.Bic,p.Name,p.Iban,"اعادة مبالغ تنفيذ","SLEV"));
        }
        return Encoding.UTF8.GetBytes(string.Join("\r\n",lines));
    }
    public A.ReportSheet ListSheet(string header){
        var rows=Groups().SelectMany(g=>g.People.Select(p=>(g.Name,p))).ToArray();
        return new(header,"قائمة إعادة المبالغ · "+Date.ToString("yyyy/MM/dd"),[new("ت",5,A.ReportColumnKind.Serial),new("اسم المنتسب",32,A.ReportColumnKind.Text),new("المبلغ (د.ع)",16,A.ReportColumnKind.Amount),new("رقم الحساب (IBAN)",33,A.ReportColumnKind.Text),new("المصرف",26,A.ReportColumnKind.Text)],
            rows.Select((r,i)=>new[]{(i+1).ToString(),r.p.Name,Arabic.Money(r.p.Amount),r.p.Iban,r.Name}).ToArray(),rows.Select(r=>(decimal)r.p.Amount).ToArray());
    }
    public byte[] Letter(IReadOnlyList<RefundBank> groups,A.GroupLetterSettings style,A.LetterFonts fonts){
        long sum=groups.Sum(g=>g.Total);int count=groups.Sum(g=>g.People.Count);bool raf=groups[0].Code=="RAFB";
        string Dest()=>raf?"إلى حساب "+groups[0].Name+" المرقم ("+groups[0].Bic+") وحسب ما مؤشر في قرص CD":groups.Count==1?"إلى حساب "+groups[0].Name+"، رمز السويفت ("+groups[0].Bic+")، وحسب ما مؤشر في ملف CSV المرفق":"إلى حسابات المصارف المبينة في الجدول أدناه وحسب ما مؤشر في ملفات CSV المرفقة";
        string date=A.AdaptiveLetterPdf.Eastern(" / "+Date.Month+" / "+Date.Year);
        var body="نرجو تحويل مبلغ قدره ("+Arabic.Eastern(Arabic.Money(sum))+") ("+A.ArabicAmountWords.Dinars(sum)+") لا غيرها من حسابنا المرقم ("+Account+") المفتوح لديكم "+Dest()+"، والذي يمثل إعادة مبالغ"+(Source.Length>0?" من "+Source:"")+" لمنتسبي قيادة شرطة محافظة الأنبار والبالغ عددهم ("+Arabic.Eastern(count.ToString())+")، وتزويدنا بالإشعار اللازم بذلك.";
        var blocks=A.AdaptiveLetterPdf.Opening(style with{Number="١‏/٢٢/ح/"},date,body,(string?)Original?["letters"]?[raf?"rafidain":"otherBanks"]?["recipient"]??"مصرف الرافدين / فرع الأنبار");
        if(!raf){var rows=groups.Select((g,i)=>new[]{Arabic.Eastern((i+1).ToString()),g.Name,Arabic.Eastern(Arabic.Money(g.Total)),Arabic.Eastern(g.People.Count.ToString()),g.Bic}).ToList();
            if(groups.Count>1)rows.Add(["","المجموع",Arabic.Eastern(Arabic.Money(sum)),Arabic.Eastern(count.ToString()),""]);
            blocks.Add(A.LetterBlock.Of(new A.LetterTable(["ت","اسم المصرف","المبلغ","عدد المنتسبين","Receiver BIC"],rows,"Arial",19,[1,8,5,4,5],BoldLastRow:groups.Count>1)));}
        blocks.Add(A.LetterBlock.Of(new A.LetterParagraph("المرفقات:\n١. قرص CD",HalfPoints:22)));
        blocks.Add(A.AdaptiveLetterPdf.Signatures(style,date));blocks.Add(A.AdaptiveLetterPdf.Footer(style with{Footer="نسخة منه إلى\nشعبة المصروفات / شعبة الموازنة / شعبة الرواتب / إجراء اللازم والمتابعة."}));
        return A.AdaptiveLetterPdf.Write(blocks,fonts,false,style.SignatureOffset,[Account,sum.ToString(CultureInfo.InvariantCulture)]);
    }
}
public sealed partial class Store
{
    public List<RefundBatch> Refunds(){
        var result=new List<RefundBatch>();using var zip=System.IO.Compression.ZipFile.OpenRead(Archive);
        Records(zip,"refunds.jsonl",line=>{
            var data=JsonNode.Parse(line.GetProperty("contents").GetBytesFromBase64())!.AsObject();
            var entries=new List<RefundPayee>();foreach(var row in data["rows"]!.AsArray()){
                var c=row!["candidate"]!;var p=c["person"]!;var parsed=A.PayrollSpreadsheet.ParseMoney((string?)row["amount"]??"");long value=parsed is >0 and <=999_999_999_999_999m && parsed==decimal.Truncate(parsed.Value)?(long)parsed.Value:0;
                entries.Add(new((string)p["name"]!,(string)p["rank"]!,(string)p["workplace"]!,(string)c["month"]!,(string)p["iban"]!,(string)p["bank"]!,(string?)p["payrollBIC"]?["value"]?["_0"]??"",value,row.AsObject()));
            }
            var date=AppleEpoch.AddSeconds((double)data["date"]!).ToLocalTime();
            result.Add(new((string)data["batchID"]!,(string)data["source"]!,DateOnly.FromDateTime(date),(string)data["payer"]!,(string)data["account"]!,entries,data,line.GetProperty("id").GetString()));
        });return result.OrderByDescending(b=>b.Date).ToList();
    }
    public void SaveRefund(RefundBatch batch,string? revision,bool remove=false){
        var key=batch.ArchiveId??batch.Id;
        if(remove){Rewrite(revision,null,null,refunds:new Dictionary<string,byte[]?>{{key,null}});return;}
        var groups=batch.Groups();var data=batch.Original?.DeepClone().AsObject()??new();
        data["batchID"]=batch.Id;data["payer"]=batch.Payer;data["account"]=batch.Account;data["source"]=batch.Source;
        data["date"]=(batch.Date.ToDateTime(new TimeOnly(12,0)).ToUniversalTime()-AppleEpoch).TotalSeconds;
        var rows=new JsonArray();int serial=0;string storage=Guid.NewGuid().ToString().ToUpperInvariant();
        foreach(var p in batch.People){
            var row=p.Original?.DeepClone().AsObject()??new();
            if(row["candidate"]==null){
                string pk="p"+(++serial);var identity=SwiftIdentity(storage,pk);
                row["candidate"]=JsonSerializer.SerializeToNode(new{id=identity,month=p.Month,person=new{name=p.Name,rank=p.Rank,workplace=p.Workplace,iban=p.Iban,bank=p.Bank,payrollBIC=new{value=new{_0=p.Bic}}}});
            }
            row["amount"]=p.Amount.ToString(CultureInfo.InvariantCulture);rows.Add(row);
        }
        data["rows"]=rows;
        data["banks"]=JsonSerializer.SerializeToNode(groups.ToDictionary(g=>g.Code,g=>new{name=g.Name,bic=g.Bic}));
        data["letters"]??=RefundBatch.DefaultLetters();
        Rewrite(revision,null,null,refunds:new Dictionary<string,byte[]?>{{key,JsonSerializer.SerializeToUtf8Bytes(data,IosJson)}});
    }
    static object SwiftIdentity(string storage,string pk)=>new{implementation=new{storeIdentifier=storage,entityName="Employee",primaryKey=pk,isTemporary=false,uriRepresentation="x-coredata://"+storage+"/Employee/"+pk}};
}
