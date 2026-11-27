using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;
public sealed partial class Store
{
    public void ImportTrust(A.SalaryTrustLedger incoming,string? revision){
        A.SalaryTrust.Validate(incoming);
        if(incoming.Version!=2||incoming.Entries.Count==0)throw new InvalidDataException("لا توجد أمانات للاستبدال.");
        // Preserve document links for the same employee and month when replacing Excel input.
        var snapshot=revision==null?null:Snapshot();
        var old=snapshot==null?A.SalaryTrust.Empty:A.SalaryTrust.ReadFromBackup(snapshot);
        string Key(A.SalaryTrustEntry e)=>e.Year+"|"+e.Month+"|"+A.PersonProfiles.Key(e.Name);
        var previous=old.Entries.ToDictionary(Key);
        var ledger=incoming with{Entries=incoming.Entries.Select(e=>previous.TryGetValue(Key(e),out var prior)?e with{Id=prior.Id,BookIds=prior.BookIds}:e).ToArray()};
        A.SalaryTrust.ValidateBooks(ledger,snapshot?.Books??[]);
        Rewrite(revision,null,new Dictionary<string,byte[]>{{A.SalaryTrust.SettingsKey,A.SalaryTrust.Plist(ledger)}},m=>{m["contentVersion"]=11;m["salaryTrustEntries"]=ledger.Entries.Count;});
    }
    public void ImportStaff(IReadOnlyList<A.StaffMember> members,string file,string? revision){
        if(members.Count is 0 or >500000||members.Any(r=>string.IsNullOrWhiteSpace(r.Name)||r.Details==null))throw new InvalidDataException("راجع أسماء الملاك وتفاصيلهم.");
        var statuses=members.Where(m=>m.Status.Length>0).GroupBy(m=>m.Status).ToDictionary(g=>g.Key,g=>g.Count());
        var settings=new Dictionary<string,byte[]>{{"staffCount",Plist(new NSNumber(members.Count))},{"staffFileName",Plist(new NSString(Path.GetFileNameWithoutExtension(file)))},
            {"staffImportedAt",Plist(new NSDate(DateTime.UtcNow))},{"staffStatuses",Plist(new NSArray(statuses.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key).Select(p=>(NSObject)new NSString(p.Key)).ToArray()))}};
        Rewrite(revision,null,settings,staff:members);
    }
    public void ImportReferences(byte[] bytes,string? revision){
        if(revision==null)throw new InvalidDataException("استورد الرواتب أو الملاك أو الأمانات أولاً لحفظ مراجع الكتب ضمن نسختها.");
        _=A.LetterReferencePack.ReadArchive(bytes);
        Rewrite(revision,null,new Dictionary<string,byte[]>{{"transferReferenceArchiveData",Plist(new NSData(bytes))}});
    }
    public void LinkIndividual(A.BackupSnapshot s,string id,A.TransferEntry entry){
        if(id is not ("ramadi" or "housing" or "water" or "rafidain_anbar")||entry.Group!=A.TransferGroup.Anbar)throw new InvalidDataException("اختر جهة من الكتب المنفردة.");
        var links=A.IndividualLetterLinks.Read(s);links[id]=A.IndividualLetterLinks.FromEntry(entry);
        Rewrite(Path.GetDirectoryName(s.SourcePath),null,new Dictionary<string,byte[]>{{"transferLetterEntityLinks",A.IndividualLetterLinks.Encode(links)}});
    }
    public void UpdateTrustBook(A.BackupSnapshot s,IReadOnlySet<string> rows,Guid bookId,bool unlink=false){
        if(s.Books.All(b=>b.Id!=bookId))throw new InvalidDataException("الكتاب غير موجود في الأرشيف.");
        var ledger=A.SalaryTrust.ReadFromBackup(s);
        if(unlink){
            if(rows.Count==0 || !rows.IsSubsetOf(ledger.Entries.Select(e=>e.Id).ToHashSet()))throw new InvalidDataException("أعد اختيار قيود الأمانات.");
            ledger=ledger with{Entries=ledger.Entries.Select(e=>rows.Contains(e.Id)?e with{
                BookIds=e.BookIds?.Where(raw=>Guid.Parse(raw)!=bookId).ToArray()}:e).ToArray()};
        }else ledger=A.SalaryTrust.Link(ledger,bookId,rows);
        Rewrite(Path.GetDirectoryName(s.SourcePath),null,new Dictionary<string,byte[]>{{A.SalaryTrust.SettingsKey,A.SalaryTrust.Plist(ledger)}});
    }
    public void ArchivePdf(A.BackupSnapshot s,string title,string month,string? entity,byte[] pdf,IReadOnlySet<string>? trustRows=null){
        var ledger=trustRows==null?null:A.SalaryTrust.ReadFromBackup(s);
        bool knownMonth=s.Months.Any(m=>m.Name==month)||ledger?.Entries.Any(e=>trustRows!.Contains(e.Id)&&A.SalaryTrust.MonthLabel(e.Year,e.Month)==month)==true;
        title=title.Trim();if(title.Length is 0 or >300||title.Any(char.IsControl)||!knownMonth||pdf.Length is <6 or >60000000||!pdf.AsSpan(0,5).SequenceEqual("%PDF-"u8))throw new InvalidDataException("راجع عنوان الكتاب والشهر وملف PDF.");
        using var document=UglyToad.PdfPig.PdfDocument.Open(pdf);
        if(document.NumberOfPages is <1 or >3)throw new InvalidDataException("يقبل أرشيف الكتب من صفحة إلى ثلاث صفحات.");
        foreach(var page in document.GetPages())if(page.Width<=0||page.Height<=0)throw new InvalidDataException("صفحة غير صالحة.");
        var book=new A.ArchiveBook(Guid.NewGuid(),title,month,document.NumberOfPages,entity,NowIos);
        var changes=ledger==null?null:new Dictionary<string,byte[]>{{A.SalaryTrust.SettingsKey,A.SalaryTrust.Plist(A.SalaryTrust.Link(ledger,book.Id,trustRows!))}};
        Rewrite(Path.GetDirectoryName(s.SourcePath),null,changes,book:(book,pdf));
    }
    public void RenameAgency(A.BackupSnapshot s,string from,string input){
        var names=Agencies();string target=input.Trim();
        if(!names.Contains(from)||target.Length is 0 or >300||target.Any(char.IsControl))throw new InvalidDataException("راجع تسمية الجهة.");
        string old=A.PayrollAnalysis.Normalize(from),next=A.PayrollAnalysis.Normalize(target);
        if(next.Length==0)throw new InvalidDataException("تسمية الجهة فارغة.");
        var matches=names.Where(n=>n!=from&&A.PayrollAnalysis.Normalize(n)==next).ToArray();
        if(matches.Length>1&&!matches.Contains(target))throw new InvalidDataException("اختر التسمية المطابقة من القائمة.");
        if(matches.Length==1)target=matches[0];
        if(from==target)throw new InvalidDataException("التسمية مطابقة للاسم الحالي.");
        var changes=new Dictionary<string,byte[]>();
        JsonNode? Get(string key)=>s.SettingsPlists.TryGetValue(key,out var plist)&&PlistData.Unwrap(plist) is {Length:>0} raw?JsonNode.Parse(raw):null;
        void Put(string key,JsonNode value)=>changes[key]=Plist(new NSData(JsonSerializer.SerializeToUtf8Bytes(value)));
        var links=Get("transferPayrollAccountLinks")?.AsObject()??new();
        A.ExecutionAccount? Find(string n)=>s.Accounts.SingleOrDefault(a=>A.PayrollAnalysis.Normalize(a.Entity)==((string?)links["deduction|"+n]??n));
        var a=Find(old);var b=Find(next);
        if(a!=null&&b!=null&&(a.Iban!=b.Iban||a.Swift!=b.Swift||a.Branch!=b.Branch||a.LocalAccount!=b.LocalAccount))throw new InvalidDataException("للجهتين حسابان مختلفان. راجع الربط قبل توحيدهما.");
        if(old!=next&&a!=null&&b==null){links["deduction|"+next]=A.PayrollAnalysis.Normalize(a.Entity);Put("transferPayrollAccountLinks",links);}
        var categories=Get("transferEntityCategories")?.AsObject();
        string Category(string n,A.ExecutionAccount? ac)=>n+"|"+ac?.Iban+"|"+A.PayrollAnalysis.Normalize(ac?.Branch??"");
        if(categories?[Category(old,a)] is {} category){
            var key=Category(next,b??a);if(categories[key]!=null&&!JsonNode.DeepEquals(categories[key],category))throw new InvalidDataException("تصنيف التحويل مختلف للجهتين.");
            categories[key]=category.DeepClone();Put("transferEntityCategories",categories);
        }
        if(Get("homePinnedEntityNames") is JsonArray pins)Put("homePinnedEntityNames",JsonSerializer.SerializeToNode(pins.Select(x=>(string?)x==from?target:(string?)x).Distinct())!);
        void Template(JsonNode? t){if((string?)t?["source"]=="execution"&&(string?)t["sourceName"]==from)t["sourceName"]=target;}
        void Customize(JsonNode? c){if((string?)c?["beneficiarySource"]==from)c!["beneficiarySource"]=target;Template(c?["letterTemplate"]);}
        if(Get(BuiltinKey) is JsonObject customs){Customize(customs["common"]);if(customs["individual"] is JsonObject all)foreach(var item in all)Customize(item.Value);Put(BuiltinKey,customs);}
        if(Get(LibraryKey) is JsonObject library){if((int?)library["version"]!=1)throw new InvalidDataException("إصدار غير معروف للقوالب.");foreach(var t in library["templates"]!.AsArray())Template(t);Put(LibraryKey,library);}
        if(Get("transferLetterEntityLinks") is JsonObject letters){foreach(var pair in letters)if((string?)pair.Value?["title"]==old)pair.Value!["title"]=next;Put("transferLetterEntityLinks",letters);}
        Rewrite(Path.GetDirectoryName(s.SourcePath),null,changes,rename:(from,target));
    }
}
