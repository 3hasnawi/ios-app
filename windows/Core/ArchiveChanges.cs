using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;

public sealed partial class Store
{
    static readonly JsonSerializerOptions IosJson=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true,
        DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull};
    static double NowIos=>(DateTime.UtcNow-new DateTime(2001,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
    static T? ReadJson<T>(ZipArchive? archive,string name)
    {
        var entry=archive?.GetEntry(name);if(entry==null)return default;
        using var stream=entry.Open();return JsonSerializer.Deserialize<T>(stream,IosJson);
    }
    static void Records(ZipArchive? archive,string name,Action<JsonElement> handle)
    {
        if(archive?.GetEntry(name) is {} entry)BackupReader.Lines(entry,handle,default);
    }
    static A.Employee Expanded(JsonElement row,IReadOnlyList<byte[]> headers)
    {
        var e=row.Deserialize<A.Employee>(IosJson)??throw new InvalidDataException();
        if(!row.TryGetProperty("headerIndex",out var i)||i.ValueKind==JsonValueKind.Null)return e;
        var raw=e.RawColumns!;var offset=row.GetProperty("headerOffset").GetInt32();var schema=headers[i.GetInt32()];
        var restored=new byte[checked(raw.Length+schema.Length)];raw.AsSpan(0,offset).CopyTo(restored);
        schema.CopyTo(restored,offset);raw.AsSpan(offset).CopyTo(restored.AsSpan(offset+schema.Length));return e with{RawColumns=restored};
    }
    static void Fingerprint(IncrementalHash hash,A.Employee row)
    {
        void Integer(ulong n){Span<byte> bytes=stackalloc byte[8];BinaryPrimitives.WriteUInt64BigEndian(bytes,n);hash.AppendData(bytes);}
        void Bytes(byte[]? bytes){Integer(bytes==null?ulong.MaxValue:(ulong)bytes.Length);if(bytes!=null)hash.AppendData(bytes);}
        foreach(var s in new[]{row.Month,row.Name,row.Rank,row.Workplace})Bytes(Encoding.UTF8.GetBytes(s));
        Integer(row.Net.HasValue?1UL:0UL);if(row.Net.HasValue)Integer(BitConverter.DoubleToUInt64Bits((double)row.Net.Value));
        Bytes(row.Items);Bytes(row.Details);Bytes(row.RawColumns);
    }
    public A.BackupSnapshot Snapshot(params string[] periods)
    {
        var file=Archive;using var zip=ZipFile.OpenRead(file);
        var manifest=ReadJson<JsonObject>(zip,"manifest.json")!;
        var schemas=ReadJson<byte[][]>(zip,"payroll-headers.json")??[];
        var emps=new List<A.Employee>();var deductions=new List<A.Deduction>();var accounts=new List<A.ExecutionAccount>();var months=new List<A.PayrollMonth>();
        bool Selected(JsonElement row)=>periods.Contains(row.GetProperty("month").GetString(),StringComparer.Ordinal);
        if(periods.Length>0)Records(zip,"employees.jsonl",r=>{if(Selected(r))emps.Add(Expanded(r,schemas));});
        if(periods.Length>0)Records(zip,"deductions.jsonl",r=>{if(Selected(r))deductions.Add(r.Deserialize<A.Deduction>(IosJson)!);});
        Records(zip,"accounts.jsonl",r=>accounts.Add(r.Deserialize<A.ExecutionAccount>(IosJson)!));
        Records(zip,"months.jsonl",r=>months.Add(r.Deserialize<A.PayrollMonth>(IosJson)!));
        return new(file,File.ReadAllText(Path.Combine(Path.GetDirectoryName(file)!,"archive.sha256")),manifest["version"]!.GetValue<int>(),months,emps,deductions,
            manifest["staff"]!.GetValue<int>(),accounts,ReadJson<List<A.ArchiveBook>>(zip,"books/index.json")??[],ReadJson<Dictionary<string,byte[]>>(zip,"settings.json")??[]);
    }
    public A.PayrollImportPreview PreviewExcel(string file,string month)
    {
        var p=A.PayrollSpreadsheet.Read(file,month);
        if(p.Problems.Count!=0)throw new InvalidDataException(string.Join("\n",p.Problems.Take(10).Select(x=>$"{x.Sheet} · صف {x.Row} · {x.Message}")));
        return p;
    }
    public void ApplyExcel(A.PayrollImportPreview preview,string? expected)
    {
        if(preview.Problems.Count>0||preview.Employees.Count+preview.Deductions.Count==0)throw new InvalidDataException("راجع ملف Excel.");
        Rewrite(expected,preview,null);
    }
    public void ApproveContract(A.BackupSnapshot snapshot,string month,decimal total)
    {
        var summary=A.BankStatementCalculator.Calculate(snapshot,month);
        var value=new A.ContractOverride(total,summary.ContractNominal,summary.ContractRetirement,summary.ContractCount,summary.MissingContractNominal);
        var key=A.ContractOverrides.SettingsKey;
        var data=A.ContractOverrides.Set(snapshot.SettingsPlists.GetValueOrDefault(key),month,value);
        Rewrite(Path.GetDirectoryName(snapshot.SourcePath),null,new Dictionary<string,byte[]>{{key,data}},
            manifest=>{if((int?)manifest["contentVersion"]==5)manifest["contentVersion"]=6;});
    }
    public void SaveBank(A.BackupSnapshot snapshot,IReadOnlyList<A.BankPeriod> periods)
    {
        var before=A.BankReconciliation.ReadFromBackup(snapshot);
        A.BankReconciliation.CheckLocked(before,periods);
        foreach(var p in periods)A.BankReconciliation.Calculate(p);
        Rewrite(Path.GetDirectoryName(snapshot.SourcePath),null,new Dictionary<string,byte[]>{{A.BankReconciliation.SettingsKey,A.BankReconciliation.LedgerPlist(periods)}});
    }
    internal void Rewrite(string? expected,A.PayrollImportPreview? payroll,IReadOnlyDictionary<string,byte[]>? changes, Action<JsonObject>? changeManifest=null, IReadOnlyDictionary<string,byte[]?>? refunds=null,IReadOnlyCollection<A.ExecutionAccount>? accounts=null)
    {
        if(expected!=Active)throw new IOException("تغيّرت النسخة بعد فتح الشاشة.");
        Directory.CreateDirectory(Root);
        string output=Path.Combine(Root,"edit-"+Guid.NewGuid().ToString("N")+".zip");
        try {
            using(var source=expected==null?null:ZipFile.OpenRead(Path.Combine(expected,BackupName)))
            using(var target=ZipFile.Open(output,ZipArchiveMode.Create)){
                var manifest=ReadJson<JsonObject>(source,"manifest.json")??new JsonObject{["version"]=12,["contentVersion"]=5,["staff"]=0,["accounts"]=0,["books"]=0};
                int version=(int)manifest["version"]!;
                var settings=ReadJson<Dictionary<string,byte[]>>(source,"settings.json")??[];
                if(changes!=null)foreach(var (key,bytes) in changes){if(bytes.Length==0)settings.Remove(key);else settings[key]=bytes;}
                var content=(int?)manifest["contentVersion"]??Math.Max(5,version);
                if(version<12){manifest["contentVersion"]=content;manifest["version"]=12;}
                var schemas=ReadJson<List<byte[]>>(source,"payroll-headers.json")??[];
                var outputSchemas=new List<byte[]>();var schemaIndex=new Dictionary<string,int>();
                var months=new Dictionary<string,JsonObject>();
                Records(source,"months.jsonl",r=>months.Add(r.GetProperty("name").GetString()!,r.Deserialize<JsonObject>()!));
                string? replacing=payroll?.Month;
                if(payroll!=null){
                    var aliases=months.Keys.Where(m=>Arabic.Period(m)==Arabic.Period(replacing!)).ToArray();
                    if(aliases.Length>1)throw new InvalidDataException("يوجد أكثر من تسمية للشهر نفسه.");
                    replacing=aliases.FirstOrDefault()??payroll.Month;
                    if(!months.ContainsKey(replacing))months[replacing]=new JsonObject{["name"]=replacing,["employeeCount"]=0,["deductionCount"]=0};
                    months[replacing]["importedAt"]=NowIos;
                }
                var payCounts=new Dictionary<string,int>();var cutCounts=new Dictionary<string,int>();
                var retained=months.Where(p=>(int)p.Value["employeeCount"]!>0||p.Key==replacing&&payroll?.Employees.Count>0)
                    .OrderByDescending(p=>Arabic.Period(p.Key)).Take(2).Select(p=>p.Key).ToHashSet();
                if(payroll?.Employees.Count>0&&!retained.Contains(replacing!))throw new InvalidDataException("شهر الرواتب أقدم من شهري الرواتب المحتفظ بهما.");
                var digests=new SortedDictionary<string,byte[]>(StringComparer.Ordinal);
                void Entry(string name,Action<Stream> write,bool supporting=true){
                    using var dest=target.CreateEntry(name,CompressionLevel.Optimal).Open();
                    if(!supporting){write(dest);return;}
                    using var hash=SHA256.Create();using var stream=new CryptoStream(dest,hash,CryptoStreamMode.Write,true);
                    write(stream);stream.FlushFinalBlock();digests.Add(name,hash.Hash!);
                }
                void Json(string name,object value,bool supporting=true)=>Entry(name,s=>JsonSerializer.Serialize(s,value,IosJson),supporting);
                void Line(Stream stream,JsonElement row){using(var writer=new Utf8JsonWriter(stream))row.WriteTo(writer);stream.WriteByte(10);}
                var replaced=new HashSet<string>{"settings.json","employees.jsonl","payroll-headers.json","months.jsonl","deductions.jsonl","manifest.json"};
                foreach(var entry in source?.Entries.AsEnumerable()??Enumerable.Empty<ZipArchiveEntry>()){
                    if(replaced.Contains(entry.FullName)||refunds!=null&&entry.FullName=="refunds.jsonl"||accounts!=null&&entry.FullName=="accounts.jsonl")continue;
                    Entry(entry.FullName,d=>{using var s=entry.Open();s.CopyTo(d);});
                }
                if(accounts!=null){Entry("accounts.jsonl",s=>{foreach(var account in accounts)Line(s,JsonSerializer.SerializeToElement(account,IosJson));});manifest["accounts"]=accounts.Count;}
                foreach(var name in new[]{"accounts.jsonl","staff.jsonl","books/index.json"})if(!digests.ContainsKey(name))Entry(name,s=>{if(name.EndsWith(".json"))s.Write("[]"u8);});
                using var payHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                bool copyPayroll=source!=null&&version>=12&&!(payroll?.Employees.Count>0);
                if(copyPayroll){
                    Entry("employees.jsonl",s=>{using var original=source!.GetEntry("employees.jsonl")!.Open();original.CopyTo(s);},false);
                    Entry("payroll-headers.json",s=>{using var original=source!.GetEntry("payroll-headers.json")!.Open();original.CopyTo(s);});
                }else{
                Entry("employees.jsonl",s=>{
                    void Emit(JsonElement row,bool existing){
                        var month=row.GetProperty("month").GetString()!;
                        if(existing&&payroll?.Employees.Count>0&&(!retained.Contains(month)||month==replacing))return;
                        var person=existing?Expanded(row,schemas):row.Deserialize<A.Employee>(IosJson)!;
                        Fingerprint(payHash,person);payCounts[month]=payCounts.GetValueOrDefault(month)+1;
                        var node=row.Deserialize<JsonObject>()!;node.Remove("headerIndex");node.Remove("headerOffset");
                        if(person.RawColumns!=null){
                            var parts=A.SharedPayrollColumns.Split(person.RawColumns);
                            if(parts!=null&&parts.Prefix.Length>=32){
                                if(!schemaIndex.TryGetValue(parts.SchemaId,out int index)){index=outputSchemas.Count;outputSchemas.Add(parts.Prefix);schemaIndex.Add(parts.SchemaId,index);}
                                node["headerIndex"]=index;node["headerOffset"]=parts.Before.Length;node["rawColumns"]=Convert.ToBase64String([..parts.Before,..parts.Tail]);
                            }else node["rawColumns"]=Convert.ToBase64String(person.RawColumns);
                        }
                        Line(s,JsonSerializer.SerializeToElement(node));
                    }
                    Records(source,"employees.jsonl",r=>Emit(r,true));
                    if(payroll!=null)foreach(var p in payroll.Employees)Emit(JsonSerializer.SerializeToElement(p with{Month=replacing!},IosJson),false);
                },false);
                Json("payroll-headers.json",outputSchemas);
                manifest["employees"]=payCounts.Values.Sum();manifest["payrollDigest"]=Convert.ToBase64String(payHash.GetHashAndReset());
                }
                Entry("deductions.jsonl",s=>{
                    void Emit(JsonElement row){string m=row.GetProperty("month").GetString()!;cutCounts[m]=cutCounts.GetValueOrDefault(m)+1;Line(s,row);}
                    Records(source,"deductions.jsonl",r=>{if(payroll?.Deductions.Count>0&&r.GetProperty("month").GetString()==replacing)return;Emit(r);});
                    if(payroll!=null)foreach(var d in payroll.Deductions)Emit(JsonSerializer.SerializeToElement(d with{Month=replacing!},IosJson));
                });
                Entry("months.jsonl",s=>{foreach(var (name,record) in months){
                    int count=payCounts.GetValueOrDefault(name);
                    if(payroll?.Employees.Count>0 && (name==replacing||!retained.Contains(name))){record["employeeCount"]=count;record.Remove("summary");}
                    if(name==replacing&&payroll?.Deductions.Count>0)record["deductionCount"]=cutCounts.GetValueOrDefault(name);
                    Line(s,JsonSerializer.SerializeToElement(record));}});
                if(refunds!=null){
                    int history=0;bool draft=false;
                    Entry("refunds.jsonl",s=>{
                        void WriteRefund(JsonElement row){
                            if(row.GetProperty("id").GetString()=="draft")draft=true;else history++;
                            Line(s,row);
                        }
                        Records(source,"refunds.jsonl",row=>{if(!refunds.ContainsKey(row.GetProperty("id").GetString()!))WriteRefund(row);});
                        foreach(var (id,bytes) in refunds)if(bytes!=null)WriteRefund(JsonSerializer.SerializeToElement(new{id,contents=bytes}));
                    });
                    manifest["version"]=14;manifest["refundBatchCount"]=history;manifest["hasRefundDraft"]=draft;
                }
                changeManifest?.Invoke(manifest);
                Json("settings.json",settings);
                using var combined=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach(var (name,digest) in digests){combined.AppendData(Encoding.UTF8.GetBytes(name));combined.AppendData([0]);combined.AppendData(digest);}
                manifest["createdAt"]=NowIos;manifest["months"]=JsonSerializer.SerializeToNode(months.Keys.OrderByDescending(Arabic.Period));
                manifest["deductions"]=cutCounts.Values.Sum();
                manifest["accounts"]??=0;manifest["books"]??=0;
                manifest["supportingDigest"]=Convert.ToBase64String(combined.GetHashAndReset());
                Json("manifest.json",manifest,false);
            }
            Install(output,null,default,true,expected);
        }finally{if(File.Exists(output))File.Delete(output);}
    }
}
