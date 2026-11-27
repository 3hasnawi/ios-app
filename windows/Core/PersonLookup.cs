using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;

// فهرس محلي لحقول الملف الشخصي فقط. لا تُنسخ أعمدة Excel الخام إلى الفهرس.
internal sealed class PersonIndexWriter:IDisposable
{
    readonly SqliteCommand insert;
    internal PersonIndexWriter(SqliteConnection db,SqliteTransaction transaction){
        using var schema=db.CreateCommand();schema.Transaction=transaction;
        schema.CommandText="CREATE TABLE profile_rows (person TEXT NOT NULL,kind TEXT NOT NULL,payload BLOB NOT NULL)";
        schema.ExecuteNonQuery();
        insert=db.CreateCommand();insert.Transaction=transaction;
        insert.CommandText="INSERT INTO profile_rows VALUES($person,$kind,$payload)";
        foreach(string key in new[]{"$person","$kind","$payload"})insert.Parameters.Add(new SqliteParameter(key,DBNull.Value));
    }
    public void Payroll(JsonElement row){
        string Text(string k)=>row.GetProperty(k).GetString()!;
        byte[]? Bytes(string k)=>row.TryGetProperty(k,out var v)&&v.ValueKind!=JsonValueKind.Null?v.GetBytesFromBase64():null;
        var pay=new A.Employee(Text("month"),Text("name"),Text("rank"),Text("workplace"),
            row.TryGetProperty("net",out var net)&&net.ValueKind!=JsonValueKind.Null?net.GetDecimal():null,Bytes("items"),Bytes("details"),null);
        Add(pay.Name,"pay",pay);
    }
    public void Staff(JsonElement row)=>Add(row.GetProperty("name").GetString()!,"staff",row);
    public void Trust(A.SalaryTrustLedger ledger){
        using var meta=insert.Connection!.CreateCommand();meta.Transaction=insert.Transaction;
        meta.CommandText="INSERT INTO meta VALUES('profileTrustVersion',$version),('profileTrustOpening',$opening)";
        meta.Parameters.AddWithValue("$version",ledger.Version.ToString());meta.Parameters.AddWithValue("$opening",ledger.OpeningImported?"1":"0");meta.ExecuteNonQuery();
        foreach(var entry in ledger.Entries)Add(entry.Name,"trust",entry);
    }
    void Add<T>(string name,string kind,T value){
        using var bytes=new MemoryStream();
        using(var compressed=new BrotliStream(bytes,CompressionLevel.Fastest,true))JsonSerializer.Serialize(compressed,value);
        insert.Parameters[0].Value=A.PersonProfiles.Key(name);insert.Parameters[1].Value=kind;insert.Parameters[2].Value=bytes.ToArray();insert.ExecuteNonQuery();
    }
    public void Finish(){
        using var cmd=insert.Connection!.CreateCommand();cmd.Transaction=insert.Transaction;
        cmd.CommandText="CREATE INDEX profile_name ON profile_rows(person); PRAGMA user_version=3";cmd.ExecuteNonQuery();
    }
    public void Dispose()=>insert.Dispose();
}

public sealed record EmployeeFile(string Name,A.ProfileMonth[] Months,List<Deduction> Deductions,List<A.StaffMember> Staff,A.SalaryTrustLedger Trust,string Header);

public sealed partial class Store
{
    readonly object profileIndexLock=new();
    public void PreparePersonIndex(string dir){
        lock(profileIndexLock)EnsurePersonIndex(dir);
    }
    static void EnsurePersonIndex(string dir){
        static bool Ready(SqliteConnection db,SqliteTransaction? tx=null){
            using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="PRAGMA user_version";
            return Convert.ToInt32(cmd.ExecuteScalar())>=3;
        }
        using(var read=Connect(dir)){if(Ready(read))return;}
        using var db=Connect(dir,false);using var tx=db.BeginTransaction();
        if(Ready(db,tx))return;
        // ترقية الفهرس السابق في معاملة واحدة وبقاء النسخة وتخصيصاتها في موضعها.
        using var zip=ZipFile.OpenRead(Path.Combine(dir,BackupName));using var writer=new PersonIndexWriter(db,tx);
        Records(zip,"employees.jsonl",writer.Payroll);Records(zip,"staff.jsonl",writer.Staff);
        var settings=ReadJson<Dictionary<string,byte[]>>(zip,"settings.json");
        if(settings?.TryGetValue(A.SalaryTrust.SettingsKey,out var raw)==true && PlistData.Unwrap(raw) is {Length:>0} data)
            writer.Trust(A.SalaryTrust.Decode(data));
        writer.Finish();tx.Commit();
    }

    public EmployeeFile EmployeeDetails(string name){
        string dir=Active??throw new InvalidOperationException("اختر نسخة أولاً.");
        PreparePersonIndex(dir);
        using var db=Connect(dir);using var query=db.CreateCommand();
        query.CommandText="SELECT kind,payload FROM profile_rows WHERE person=$name";
        string key=A.PersonProfiles.Key(name);query.Parameters.AddWithValue("$name",key);
        var payroll=new List<A.Employee>();var staff=new List<A.StaffMember>();var trusts=new List<A.SalaryTrustEntry>();
        using(var rows=query.ExecuteReader())while(rows.Read()){
            using var buffer=rows.GetStream(1);using var payload=new BrotliStream(buffer,CompressionMode.Decompress);
            T Read<T>()=>JsonSerializer.Deserialize<T>(payload,IosJson)??throw new InvalidDataException("تعذّر فتح بيانات المنتسب.");
            switch(rows.GetString(0)){
                case "pay":payroll.Add(Read<A.Employee>());break;
                case "staff":staff.Add(Read<A.StaffMember>());break;
                case "trust":trusts.Add(Read<A.SalaryTrustEntry>());break;
            }
        }
        var records=Find(db,new(Person:name)).Where(d=>A.PersonProfiles.Key(d.Name)==key).OrderByDescending(d=>d.Period).ToList();
        var byMonth=records.ToLookup(d=>d.Month,d=>new A.Deduction(d.Month,d.Name,d.Agency,d.Amount,d.Status.Length==0?d.Reference:"⟦حالة الاستقطاع:"+d.Status+"⟧"+d.Reference,d.Rank));
        var months=payroll.GroupBy(e=>e.Month).OrderByDescending(g=>Arabic.Period(g.Key))
            .Select(g=>new A.ProfileMonth(g.Key,g.ToArray(),byMonth[g.Key].ToArray())).ToArray();
        string Meta(string field){using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM meta WHERE key=$key";
            cmd.Parameters.AddWithValue("$key",field);return cmd.ExecuteScalar() as string??"";}
        string header=Meta("header").Trim();
        var trust=new A.SalaryTrustLedger(Meta("profileTrustVersion")=="2"?2:1,trusts.OrderByDescending(t=>t.Year*100+t.Month).ToArray(),Meta("profileTrustOpening")=="1");
        return new(months.FirstOrDefault()?.Employees.First().Name??staff.FirstOrDefault()?.Name??name,months,records,staff,trust,
            header.Length>0?header:A.ReportSheet.DefaultHeader);
    }
}
