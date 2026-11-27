using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AnbarPayroll;

internal static class BackupReader
{
    private static InvalidDataException Invalid()=>new("تعذّر التحقق من محتوى نسخة الآيفون.");
    private static string Text(JsonElement r,string key)=>r.GetProperty(key).GetString()??throw Invalid();
    private static byte[]? Data(JsonElement r,string key)=>r.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.Null?v.GetBytesFromBase64():null;
    private static int Count(JsonElement r,string key)=>r.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.Null?v.GetInt32():0;
    private static JsonDocument Read(ZipArchiveEntry entry,int limit)
    {
        if(entry.Length>limit)throw Invalid();
        using var stream=entry.Open();return JsonDocument.Parse(stream);
    }
    // A damaged row cannot cause the whole expanded JSONL entry to be allocated.
    internal static int Lines(ZipArchiveEntry entry,Action<JsonElement> consume,CancellationToken token)
    {
        using var stream=entry.Open();using var line=new MemoryStream();
        var buffer=new byte[65536];int n,count=0;
        void Finish(){if(line.Length>0){using var doc=JsonDocument.Parse(line.GetBuffer().AsMemory(0,(int)line.Length));consume(doc.RootElement);count++;line.SetLength(0);}}
        while((n=stream.Read(buffer))>0){
            token.ThrowIfCancellationRequested();int start=0;
            while(start<n){var end=Array.IndexOf(buffer,(byte)10,start,n-start);var stop=end<0?n:end;
                if(line.Length+stop-start>64L*1024*1024)throw Invalid();
                line.Write(buffer,start,stop-start);start=stop+1;if(end>=0)Finish();}
        }
        Finish();return count;
    }
    internal static void Index(string path,SqliteConnection db,IProgress<string>? progress,CancellationToken token)
    {
        using var zip=ZipFile.OpenRead(path);
        if(zip.Entries.Count>65536)throw Invalid();
        var entries=zip.Entries.ToDictionary(e=>e.FullName,StringComparer.Ordinal);
        ZipArchiveEntry Entry(string name)=>entries.GetValueOrDefault(name)??throw Invalid();
        using var manifest=Read(Entry("manifest.json"),1_000_000);var m=manifest.RootElement;var version=Count(m,"version");
        if(version is <1 or 7 or >14)throw new InvalidDataException("صيغة النسخة غير مدعومة في هذه النسخة.");
        if(version>=12 && Count(m,"contentVersion") is not (5 or 6 or 8 or 9 or 10 or 11))throw Invalid();
        bool hasData=m.GetProperty("months").GetArrayLength()>0||new[]{"employees","deductions","staff","accounts","books","salaryTrustEntries","contractBatchCount","refundBatchCount"}.Any(key=>Count(m,key)>0)||m.TryGetProperty("hasRefundDraft",out var draftFlag)&&draftFlag.ValueKind==JsonValueKind.True;
        if(!hasData)throw new InvalidDataException("النسخة فارغة.");
        if(version>=3){
            using var books=Read(Entry("books/index.json"),10_000_000);
            if(books.RootElement.GetArrayLength()!=Count(m,"books"))throw Invalid();
            foreach(var book in books.RootElement.EnumerateArray())Entry("books/"+Guid.Parse(Text(book,"id")).ToString().ToUpperInvariant()+".pdf");
        }
        var headers=Array.Empty<byte[]>();
        if(version>=12){
            progress?.Report("التحقق من بصمات الملفات…");
            using var schemas=Read(Entry("payroll-headers.json"),48*1024*1024);
            headers=schemas.RootElement.EnumerateArray().Select(v=>v.GetBytesFromBase64()).ToArray();
            using var combined=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach(var e in entries.Values.Where(e=>e.FullName is not ("manifest.json" or "employees.jsonl")).OrderBy(e=>e.FullName,StringComparer.Ordinal)){
                combined.AppendData(Encoding.UTF8.GetBytes(e.FullName));combined.AppendData([0]);
                using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var stream=e.Open();byte[] buf=new byte[262144];int n;
                while((n=stream.Read(buf))>0){token.ThrowIfCancellationRequested();digest.AppendData(buf,0,n);}
                combined.AppendData(digest.GetHashAndReset());
            }
            if(!combined.GetHashAndReset().SequenceEqual(Data(m,"supportingDigest")??[]))throw Invalid();
        }
        progress?.Report("التحقق من الرواتب وفهرسة الاستقطاعات…");
        Store.Exec(db,"PRAGMA journal_mode=DELETE; CREATE TABLE months(name TEXT PRIMARY KEY,key INTEGER,expected INTEGER,actual INTEGER DEFAULT 0); CREATE TABLE people(kind TEXT,person TEXT,month TEXT,info TEXT,search TEXT); CREATE TABLE payidentity(period INTEGER,personkey TEXT,rank TEXT); CREATE TABLE deductions(id INTEGER PRIMARY KEY,month TEXT,period INTEGER,person TEXT,rank TEXT,agency TEXT,reference TEXT,casekey TEXT,amount TEXT,status TEXT,search TEXT); CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT)");
        using var tx=db.BeginTransaction();
        using var profiles=new PersonIndexWriter(db,tx);
        SqliteCommand Command(string sql,params string[] keys){var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;foreach(var k in keys)c.Parameters.Add(new SqliteParameter(k,DBNull.Value));return c;}
        static void Insert(SqliteCommand c,params object?[] values){for(int i=0;i<values.Length;i++)c.Parameters[i].Value=values[i]??DBNull.Value;c.ExecuteNonQuery();}
        using var month=Command("INSERT INTO months(name,key,expected) VALUES($a,$b,$c)","$a","$b","$c");
        var names=new HashSet<string>();
        Lines(Entry("months.jsonl"),r=>{var name=Text(r,"name");names.Add(name);Insert(month,name,Arabic.Period(name),Count(r,"deductionCount"));},token);
        if(!names.SetEquals(m.GetProperty("months").EnumerateArray().Select(v=>v.GetString()!)))throw Invalid();
        using var payHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Int(ulong n){Span<byte> bytes=stackalloc byte[8];BinaryPrimitives.WriteUInt64BigEndian(bytes,n);payHash.AppendData(bytes);}
        void Field(byte[]? b){Int(b==null?ulong.MaxValue:(ulong)b.Length);if(b!=null)payHash.AppendData(b);}
        using var personIndex=Command("INSERT INTO people VALUES($k,$p,$m,$i,$s)","$k","$p","$m","$i","$s");
        void Person(string kind,string person,string month,string info,string extra="")=>Insert(personIndex,kind,person,month,info,Arabic.Key(person+" "+month+" "+info+" "+extra));
        using var identity=Command("INSERT INTO payidentity VALUES($k,$p,$r)","$k","$p","$r");
        var paid=Lines(Entry("employees.jsonl"),r=>{
            if(!names.Contains(Text(r,"month")))throw Invalid();
            var info=Text(r,"rank")+" · "+Text(r,"workplace");
            var fields=Data(r,"details");
            string extra=fields==null?"":string.Join(" ",JsonSerializer.Deserialize<Dictionary<string,string>>(fields)!.Values);
            Person("راتب",Text(r,"name"),Text(r,"month"),info,extra);
            Insert(identity,Arabic.Period(Text(r,"month")),Arabic.Match(Text(r,"name")),Text(r,"rank"));
            profiles.Payroll(r);
            if(version<12)return;
            foreach(var key in new[]{"month","name","rank","workplace"})Field(Encoding.UTF8.GetBytes(Text(r,key)));
            if(r.TryGetProperty("net",out var net)&&net.ValueKind!=JsonValueKind.Null){Int(1);Int(BitConverter.DoubleToUInt64Bits(net.GetDouble()));}else Int(0);
            Field(Data(r,"items"));Field(Data(r,"details"));var raw=Data(r,"rawColumns");
            bool hasIndex=r.TryGetProperty("headerIndex",out var idx)&&idx.ValueKind!=JsonValueKind.Null;
            bool hasOffset=r.TryGetProperty("headerOffset",out var ofs)&&ofs.ValueKind!=JsonValueKind.Null;
            if(hasIndex!=hasOffset)throw Invalid();
            if(hasIndex){
                var i=idx.GetInt32();var offset=ofs.GetInt32();
                if(raw==null||i<0||i>=headers.Length||offset<0||offset>raw.Length)throw Invalid();
                Int((ulong)raw.Length+(ulong)headers[i].Length);payHash.AppendData(raw,0,offset);payHash.AppendData(headers[i]);payHash.AppendData(raw,offset,raw.Length-offset);
            }else Field(raw);
        },token);
        if(paid!=Count(m,"employees")||version>=12&&!payHash.GetHashAndReset().SequenceEqual(Data(m,"payrollDigest")??[]))throw Invalid();
        using var deduct=Command("INSERT INTO deductions(month,period,person,rank,agency,reference,casekey,amount,status,search) VALUES($a,$b,$c,$d,$e,$f,$g,$h,$i,$j)","$a","$b","$c","$d","$e","$f","$g","$h","$i","$j");
        int cuts=Lines(Entry("deductions.jsonl"),r=>{
            var mo=Text(r,"month");if(!names.Contains(mo))throw Invalid();
            var person=Text(r,"name");var rank=Text(r,"rank");var agency=Text(r,"title");var note=Text(r,"note").Trim();
            decimal? amount=r.TryGetProperty("amount",out var a)&&a.ValueKind!=JsonValueKind.Null?a.GetDecimal():null;
            if(amount<0)throw Invalid();
            string status="";const string prefix="⟦حالة الاستقطاع:";int end=note.IndexOf('⟧');
            if(amount==null&&note.StartsWith(prefix)&&end>=prefix.Length){var label=Arabic.Key(note[prefix.Length..end]);if(label is "موقوف" or "هارب" or "محكوم"){status=label;note=note[(end+1)..].Trim();}}
            Insert(deduct,mo,Arabic.Period(mo),person,rank,agency,note,Arabic.CaseKey(note),amount?.ToString(CultureInfo.InvariantCulture),status,Arabic.Key(person+" "+agency+" "+note));
        },token);
        if(cuts!=Count(m,"deductions")||Lines(Entry("staff.jsonl"),r=>{profiles.Staff(r);Person("ملاك",Text(r,"name"),"",Text(r,"rank")+" · "+Text(r,"workplace"),Text(r,"mother")+" "+Text(r,"stat")+" "+Text(r,"unified"));},token)!=Count(m,"staff"))throw Invalid();
        if(version>=2&&Lines(Entry("accounts.jsonl"),_=>{},token)!=Count(m,"accounts"))throw Invalid();
        if(version==14){
            bool draft=false;var ids=new HashSet<string>();
            Lines(Entry("refunds.jsonl"),r=>{var id=Text(r,"id");if(id=="draft"){if(draft)throw Invalid();draft=true;}else if(!ids.Add(id))throw Invalid();
                using var payload=JsonDocument.Parse(r.GetProperty("contents").GetBytesFromBase64());payload.RootElement.GetProperty("batchID");},token);
            if(ids.Count!=m.GetProperty("refundBatchCount").GetInt32()||draft!=m.GetProperty("hasRefundDraft").GetBoolean())throw Invalid();
        }
        using var settings=Read(Entry("settings.json"),64_000_000);
        using var meta=Command("INSERT INTO meta VALUES($key,$value)","$key","$value");
        Insert(meta,"format",version.ToString());Insert(meta,"refunds",Count(m,"refundBatchCount").ToString());
        if(settings.RootElement.TryGetProperty("bankReconciliations",out var wrapped)){
            byte[] bank=PlistData.Unwrap(wrapped.GetBytesFromBase64());
            if(bank.Length>0){using var ledger=JsonDocument.Parse(bank);if(Count(ledger.RootElement,"version")!=1)throw Invalid();Insert(meta,"bank",Encoding.UTF8.GetString(bank));}
        }
        if(settings.RootElement.TryGetProperty("salaryTrustLedgerV1",out var trust)){
            var data=PlistData.Unwrap(trust.GetBytesFromBase64());
            if(data.Length>0){
                var ledger=SalaryDesktop.Core.SalaryTrust.Decode(data);profiles.Trust(ledger);
                foreach(var item in ledger.Entries)
                    Person("أمانات",item.Name,SalaryDesktop.Core.SalaryTrust.MonthLabel(item.Year,item.Month),Arabic.Money(item.Amount)+" · "+item.ImportedStatus+" · "+item.ImportedPaymentDate);
            }
        }
        if(settings.RootElement.TryGetProperty("reportHeader",out var head))Insert(meta,"header",PlistData.String(head.GetBytesFromBase64()));
        using var finish=Command("CREATE INDEX pay_name ON payidentity(personkey,period); CREATE INDEX d_name ON deductions(person_key(person)); CREATE INDEX d_month ON deductions(month); CREATE INDEX d_person ON deductions(person,agency,casekey); CREATE INDEX d_agency ON deductions(agency,period); UPDATE months SET actual=(SELECT count(*) FROM deductions WHERE month=months.name)");
        finish.ExecuteNonQuery();profiles.Finish();token.ThrowIfCancellationRequested();tx.Commit();
    }
}
