using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace AnbarPayroll;

public sealed partial class Store(string root)
{
    public string Root {get;}=Path.GetFullPath(root);
    private string Pointer=>Path.Combine(Root,"current");
    public string? Active
    {
        get {
            if(!File.Exists(Pointer))return null;
            var id=File.ReadAllText(Pointer);
            if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("فهرس النسخة المحلية غير صالح.");
            return Path.Combine(Root,id);
        }
    }
    public const string BackupName="نسخة الرواتب.zip";
    public string Archive=>Path.Combine(Active??throw new InvalidOperationException("اختر نسخة أولاً."),BackupName);
    public void Import(string source,IProgress<string>? progress=null,CancellationToken token=default) => Install(source,progress,token, false,null);
    private void Install(string source,IProgress<string>? progress,CancellationToken token,bool checkRevision,string? expected)
    {
        Directory.CreateDirectory(Root);
        using var lease=new FileStream(Path.Combine(Root,"import.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(checkRevision && Active!=expected)throw new InvalidOperationException("تغيرت النسخة. أعد فتح البيانات قبل الحفظ.");
        var id=Guid.NewGuid().ToString("N");
        string stage=Path.Combine(Root,id),next=Path.Combine(Root,"current."+id);
        Directory.CreateDirectory(stage);
        bool committed=false;
        try {
            var zip=Path.Combine(stage,BackupName);
            using var fileHash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            progress?.Report("نسخ الأرشيف المحلي…");
            using(var input=File.OpenRead(source))
            using(var output=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                byte[] buffer=new byte[262144];int n;
                while((n=input.Read(buffer))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,n);fileHash.AppendData(buffer,0,n);}
                output.Flush(true);
            }
            File.WriteAllText(Path.Combine(stage,"archive.sha256"),Convert.ToHexString(fileHash.GetHashAndReset()));
            using(var db=Connect(stage,false)) BackupReader.Index(zip,db,progress,token);
            if(!checkRevision && Active is {} before)CheckRestoredTransfers(Path.Combine(before,BackupName),zip);
            token.ThrowIfCancellationRequested();
            File.WriteAllText(next,id);
            File.Move(next,Pointer,true);
            committed=true;
        } finally {
            if(!committed)Directory.Delete(stage,true);
            if(File.Exists(next))File.Delete(next);
        }
        // Only managed snapshots are collected, and only after the new one is usable.
        foreach(var dir in Directory.EnumerateDirectories(Root))
            if(dir!=stage && Guid.TryParseExact(Path.GetFileName(dir),"N",out _))
                try{Directory.Delete(dir,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    internal static SqliteConnection Connect(string dir,bool read=true)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(dir,"lookup.db"),Pooling=false,
            Mode=read?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate}.ToString());
        db.Open();
        db.CreateCollation("ARABIC",(a,b)=>CultureInfo.GetCultureInfo("ar-IQ").CompareInfo.Compare(a,b,CompareOptions.None));
        db.CreateFunction<string,string>("person_key",Arabic.Match,isDeterministic:true);
        db.CreateFunction<string,string>("agency_category",Arabic.Category,isDeterministic:true);
        return db;
    }
    private SqliteConnection Open()=>Connect(Active??throw new InvalidOperationException("اختر نسخة الآيفون أولاً."));
    internal static void Exec(SqliteConnection db,string sql){using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
    public bool HasPayroll(string month){
        using var db=Open();using var query=db.CreateCommand();query.CommandText="SELECT EXISTS(SELECT 1 FROM payidentity WHERE period=$key)";query.Parameters.AddWithValue("$key",Arabic.Period(month));return Convert.ToInt32(query.ExecuteScalar())!=0;
    }
    public List<MonthInfo> Months()
    {
        if(Active==null)return [];
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT name,key,expected,actual FROM months ORDER BY key DESC,name";
        using var r=cmd.ExecuteReader();var result=new List<MonthInfo>();while(r.Read())result.Add(new(r.GetString(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3)));return result;
    }
    public IEnumerable<Deduction> Find(Filters f,int limit=-1,int offset=0)
    {
        if(Active==null)yield break;
        using var db=Open();using var cmd=db.CreateCommand();
        var filter=Predicate(cmd,f);
        cmd.CommandText="SELECT month,period,person,rank,agency,reference,amount,status FROM deductions "+filter+
            " ORDER BY person COLLATE ARABIC,period,agency,reference,id LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit",limit);cmd.Parameters.AddWithValue("$offset",offset);
        using var r=cmd.ExecuteReader();
        while(r.Read())yield return new(r.GetString(0),r.GetInt32(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),
            r.IsDBNull(6)?null:decimal.Parse(r.GetString(6),CultureInfo.InvariantCulture),r.GetString(7));
    }
    public (int Count,decimal Total) Summary(Filters f,CancellationToken token=default)
    {
        if(Active==null)return (0,0);
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT amount FROM deductions "+Predicate(cmd,f);
        using var rows=cmd.ExecuteReader();int count=0;decimal total=0;
        while(rows.Read()){token.ThrowIfCancellationRequested();count++;if(!rows.IsDBNull(0))total+=decimal.Parse(rows.GetString(0),CultureInfo.InvariantCulture);}
        return (count,total);
    }
    private static string Predicate(SqliteCommand cmd,Filters f)
    {
        var clauses=new List<string>();
        void Where(string expression,string name,object value){clauses.Add(expression);cmd.Parameters.AddWithValue(name,value);}
        if(f.Year!=0)Where("period / 100 = $y","$y",f.Year);
        if(f.Month!=0)Where("period % 100 = $m","$m",f.Month);
        if(f.Agency.Length>0)Where("agency=$e","$e",f.Agency);
        if(f.Category.Length>0)Where("agency_category(agency)=$g","$g",f.Category);
        if(f.Person.Length>0)Where("person_key(person)=$p","$p",Arabic.Match(f.Person));
        if(f.Reference.Length>0)Where(f.ExactReference?"casekey=$c":"instr(casekey,$c)>0","$c",Arabic.CaseKey(f.Reference));
        int i=0;
        string column=f.SearchField switch{
            DeductionSearchField.Agency=>"person_key(agency)",
            DeductionSearchField.Employee=>"person_key(person)",
            _=>"search"
        };
        foreach(var word in Arabic.Key(f.Search).Split(' ',StringSplitOptions.RemoveEmptyEntries)){var key="$q"+i++;Where("instr("+column+","+key+")>0",key,word);}
        return clauses.Count==0?"":"WHERE "+string.Join(" AND ",clauses);
    }
    public List<string> Agencies()
    {
        if(Active==null)return [];
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT DISTINCT agency FROM deductions ORDER BY agency COLLATE ARABIC";
        using var r=cmd.ExecuteReader();var a=new List<string>();while(r.Read())a.Add(r.GetString(0));return a;
    }
    public List<Review> Reviews(MonthInfo month)
    {
        var previous=Months().FirstOrDefault(m=>m.Key>0&&m.Key<month.Key);
        if(previous is null||!month.Verified||!previous.Verified)return [];
        using var db=Open();
        Dictionary<string,string> Set(string name)
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT DISTINCT agency FROM deductions WHERE month=$m AND amount IS NOT NULL AND CAST(amount AS REAL)>0 ORDER BY agency COLLATE ARABIC";
            cmd.Parameters.AddWithValue("$m",name);
            using var rows=cmd.ExecuteReader();var result=new Dictionary<string,string>();
            while(rows.Read()){var title=rows.GetString(0);result.TryAdd(Arabic.Key(title),title);}
            return result;
        }
        var curr=Set(month.Name);var prev=Set(previous.Name);
        return curr.Keys.Except(prev.Keys).Select(e=>new Review("جهة ظهرت في الشهر المختار",curr[e],month.Name))
            .Concat(prev.Keys.Except(curr.Keys).Select(e=>new Review("جهة غابت عن الشهر المختار",prev[e],previous.Name))).ToList();
    }
    public Statement PersonStatement(string person,string agency,string reference,int start,int end,string header)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT count(*),max(rank) FROM payidentity WHERE personkey=$p AND period BETWEEN $a AND $b GROUP BY period ORDER BY period DESC";
        cmd.Parameters.AddWithValue("$p",Arabic.Match(person));cmd.Parameters.AddWithValue("$a",start);cmd.Parameters.AddWithValue("$b",end);
        using var rows=cmd.ExecuteReader();string? rank=null;
        while(rows.Read()){
            if(rows.GetInt32(0)>1)throw new InvalidDataException("يوجد أكثر من سجل راتب بالاسم نفسه في شهر واحد. راجع الهوية قبل إصدار الكشف.");
            rank??=rows.GetString(1);
        }
        var statement=Statement.Employee(Find(new(Person:person)),Months(),person,agency,reference,start,end,header);
        return string.IsNullOrEmpty(rank)?statement:statement with {Rank=rank};
    }
    public void UpgradeIndex()
    {
        if(Active==null)return;
        using(var db=Open()){using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";if(Convert.ToInt32(cmd.ExecuteScalar())>=2&&File.Exists(Path.Combine(Active!,"archive.sha256")))return;}
        Import(Archive);
    }
    public record SearchResult(string Kind,string Name,string Month,string Info);
    public List<SearchResult> SearchPeople(string query)
    {
        var words=Arabic.Key(query).Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(words.Length==0||Active==null)return [];
        using var db=Open();using var cmd=db.CreateCommand();
        string where=string.Join(" AND ",words.Select((w,i)=>"instr(search,$w"+i+")>0"));
        for(int i=0;i<words.Length;i++)cmd.Parameters.AddWithValue("$w"+i,words[i]);
        cmd.CommandText="SELECT kind,person,month,info FROM people WHERE "+where+" UNION ALL SELECT 'استقطاع',person,month,agency || ' · ' || reference FROM deductions WHERE "+where+" ORDER BY 2 COLLATE ARABIC LIMIT 301";
        using var rows=cmd.ExecuteReader();var hits=new List<SearchResult>();
        while(rows.Read())hits.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetString(3)));
        return hits;
    }
    public List<ArchivedBook> Books()
    {
        if(Active==null)return [];
        using var zip=ZipFile.OpenRead(Archive);
        var index=zip.GetEntry("books/index.json");
        if(index==null)return [];
        using var stream=index.Open();using var data=JsonDocument.Parse(stream);
        return data.RootElement.EnumerateArray().Select(e=>new ArchivedBook(
            e.GetProperty("id").GetGuid(),e.GetProperty("title").GetString()!,e.GetProperty("month").GetString()!,
            e.TryGetProperty("entity",out var agency)?agency.GetString()??"":"",e.GetProperty("pages").GetInt32()))
            .OrderByDescending(b=>Arabic.Period(b.Month)).ThenBy(b=>b.Title).ToList();
    }
    public void SaveBook(ArchivedBook book,string output)
    {
        using var zip=ZipFile.OpenRead(Archive);
        var entry=zip.GetEntry("books/"+book.Id.ToString().ToUpperInvariant()+".pdf")??throw new InvalidDataException("ملف الكتاب غير موجود.");
        if(entry.Length is <=0 or >60_000_000)throw new InvalidDataException("حجم ملف الكتاب غير صالح.");
        var temp=output+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var file=File.Create(temp)){using var input=entry.Open();input.CopyTo(file);if(file.Length!=entry.Length)throw new InvalidDataException();file.Flush(true);}File.Move(temp,output,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public string Metadata(string key){if(Active==null)return "";using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM meta WHERE key=$k";cmd.Parameters.AddWithValue("$k",key);return cmd.ExecuteScalar() as string??"";}
}
