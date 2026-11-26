using System.Text.Json;
using Claunia.PropertyList;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;

public static class AccountDirectory
{
    public static A.ExecutionAccount Account(string entity,string branch,string iban,string swift,string local)
    {
        string key=A.TransferPreparation.NormalizeAccount(iban);
        string code=A.TransferPreparation.NormalizeAccount(swift);
        string number=A.TransferPreparation.NormalizeAccount(local);
        var account=new A.ExecutionAccount(entity.Trim(),key,branch.Trim(),code,number.Length==0?null:number);
        if(account.Entity.Length==0 || account.Entity.IndexOfAny(['|','"','\r','\n'])>=0)
            throw new InvalidDataException("أدخل اسم الجهة المستفيدة.");
        if(key.Length==0 && number.Length==0)throw new InvalidDataException("أدخل الآيبان أو رقم الحساب المحلي.");
        if(key.Length>0 && !A.TransferPreparation.ValidIban(key))throw new InvalidDataException("راجع رقم IBAN العراقي وأرقام التحقق.");
        if(number.Length>0 && A.TransferPreparation.LocalAccount(account)==null)throw new InvalidDataException("راجع الحساب المحلي ذي ١٥ رقماً ومطابقته للآيبان.");
        if(code.Length>0 && !A.TransferPreparation.ValidSwift(code))throw new InvalidDataException("راجع رمز SWIFT.");
        return account;
    }

    public static byte[] Excel(IReadOnlyList<A.ExecutionAccount> accounts,string header)
    {
        var sheet=new A.ReportSheet(header,"دليل الحسابات المصرفية",
            [new("ت",6,A.ReportColumnKind.Serial),new("الجهة المستفيدة",34,A.ReportColumnKind.Text),
             new("المصرف والفرع",34,A.ReportColumnKind.Text),new("IBAN",32,A.ReportColumnKind.Text),
             new("SWIFT",18,A.ReportColumnKind.Text),new("الحساب المحلي",22,A.ReportColumnKind.Text)],
            accounts.Select(a=>new[]{"",a.Entity,a.Branch,a.Iban,a.Swift,a.LocalAccount??""}).ToList(),[],ShowTotal:false);
        return A.XlsxReport.Generate(sheet);
    }
}

public sealed partial class Store
{
    public void SaveDirectoryAccount(A.BackupSnapshot? snapshot,A.ExecutionAccount? original,A.ExecutionAccount account)
    {
        account=AccountDirectory.Account(account.Entity,account.Branch,account.Iban,account.Swift,account.LocalAccount??"");
        var accounts=snapshot?.Accounts.ToList()??[];
        int index=original==null?-1:accounts.IndexOf(original);
        if(original!=null && index<0)throw new InvalidDataException("تغيّر الحساب بعد فتحه. أعد فتح الدليل.");
        string key=A.PayrollAnalysis.Normalize(account.Entity);
        if(accounts.Where((a,i)=>i!=index).Any(a=>A.PayrollAnalysis.Normalize(a.Entity)==key))
            throw new InvalidDataException("يوجد حساب محفوظ باسم هذه الجهة.");
        var settings=new Dictionary<string,byte[]>();
        if(original==null)accounts.Add(account);
        else{
            accounts[index]=account;
            var oldKey=A.PayrollAnalysis.Normalize(original.Entity);
            if(oldKey!=key){
                var links=A.TransferPreparation.SettingDictionary(snapshot!,"transferPayrollAccountLinks");
                foreach(var pair in links.Where(p=>p.Value==oldKey).ToArray())links[pair.Key]=key;
                // تطابق الاسم يعمل كرابط ضمني قبل إعادة التسمية.
                links.TryAdd("deduction|"+oldKey,key);
                settings["transferPayrollAccountLinks"]=Plist(new NSData(JsonSerializer.SerializeToUtf8Bytes(links)));
            }
            var letters=A.IndividualLetterLinks.Read(snapshot!);
            bool changed=false;
            foreach(var (id,link) in letters.ToArray()){
                if(link.AccountKey!=oldKey || link.Iban!=A.TransferPreparation.NormalizeAccount(original.Iban) ||
                   link.LocalAccount!=(original.LocalAccount==null?null:A.TransferPreparation.NormalizeAccount(original.LocalAccount)))continue;
                var updated=link with{AccountKey=key,Iban=account.Iban,LocalAccount=account.LocalAccount};
                if(updated!=link){letters[id]=updated;changed=true;}
            }
            if(changed)settings["transferLetterEntityLinks"]=A.IndividualLetterLinks.Encode(letters);
        }
        Rewrite(snapshot==null?null:Path.GetDirectoryName(snapshot.SourcePath),null,settings,accounts:accounts);
    }
}
