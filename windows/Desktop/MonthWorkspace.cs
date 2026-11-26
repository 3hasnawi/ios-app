using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    async Task ImportLocal(string kind)
    {
        var revision=store.Active;
        var dialog=new OpenFileDialog{Filter=kind switch{"الأمانات"=>"ملف الأمانات|*.xlsx","الملاك"=>"قاعدة الملاك|*.accdb;*.mdb",_=>"مراجع كتب التحويل|*.zip"}};
        if(dialog.ShowDialog(this)!=true)return;
        string file=dialog.FileName;
        try{
            Action save;string review;
            if(kind=="الأمانات"){
                var ledger=await Task.Run(()=>A.SalaryTrustSpreadsheet.Read(file));
                review=$"{ledger.Entries.Count:N0} قيد　·　المجموع {Arabic.Money(ledger.Entries.Sum(e=>(decimal)e.Amount))} دينار";
                save=()=>store.ImportTrust(ledger,revision);
            }else if(kind=="الملاك"){
                var staff=await Task.Run(()=>StaffAccess.Read(file));
                review=$"{staff.Count:N0} منتسب";save=()=>store.ImportStaff(staff,file,revision);
            }else{
                var (bytes,count)=await Task.Run(()=>{if(new FileInfo(file).Length>10_000_000)throw new InvalidDataException("حزمة المراجع كبيرة جداً.");var raw=File.ReadAllBytes(file);return (raw,A.LetterReferencePack.ReadArchive(raw).Count);});
                review=count+" نموذج كتاب";save=()=>store.ImportReferences(bytes,revision);
            }
            if(!Form("استبدال "+kind,Text(Path.GetFileName(file)),Text(review,17,true),Text("سيحل محتوى هذا الملف محل "+kind+" المحفوظة.")))return;
            await Working(save,"حُفظت "+kind+" في النسخة المحلية.",()=>{Reload();ShowPage("الإعدادات");});
        }catch(Exception e){Error(e);}
    }
    void ImportSigned()
    {
        var file=new OpenFileDialog{Filter="كتاب موقّع|*.pdf"};if(file.ShowDialog(this)!=true)return;
        var snapshot=store.Snapshot();var period=Choices(months.Select(m=>m.Name));period.SelectedItem=SelectedMonth?.Name;
        var title=new TextBox{Text=Path.GetFileNameWithoutExtension(file.FileName)};var entity=Choices(agencies);entity.IsEditable=true;entity.SelectedIndex=-1;
        if(!Form("أرشفة كتاب موقّع",Labeled("العنوان",title,370),Labeled("شهر الكتاب",period,370),Labeled("الجهة (اختياري)",entity,370)))return;
        string name=title.Text,agencyName=entity.Text,month=period.Text,path=file.FileName;
        _=Working(()=>{if(new FileInfo(path).Length>60000000)throw new InvalidDataException("حجم الكتاب كبير جداً.");store.ArchivePdf(snapshot,name,month,agencyName.Length==0?null:agencyName,File.ReadAllBytes(path));},"أُضيف الكتاب إلى الأرشيف.",()=>ShowPage("الكتب المحفوظة"));
    }
    void RenameAgency()
    {
        var snapshot=store.Snapshot();var source=Choices(agencies);if(agency?.SelectedIndex>0)source.SelectedItem=agency.SelectedItem;
        var target=Choices(agencies);target.IsEditable=true;target.SelectedIndex=-1;
        if(!Form("توحيد تسمية جهة التنفيذ",Labeled("التسمية الحالية",source,390),Labeled("التسمية المعتمدة",target,390),Text("يشمل التغيير قيود الجهة في جميع الأشهر المحفوظة.")))return;
        string old=source.Text,name=target.Text;
        if(!Form("تأكيد توحيد الجهة",Text(old+"　←　"+name)))return;
        _=Working(()=>store.RenameAgency(snapshot,old,name),"حُدّثت تسمية الجهة.",()=>{Reload();ShowPage(AgencyPage);});
    }
    void FundingActions(A.BackupSnapshot s,string month,Panel bar,FundingSnapshot? current)
    {
        var records=Store.FundingHistory(s,null);
        if(current!=null&&!Store.IsFundingApproved(s,current))bar.Children.Add(ButtonFor("اعتماد الكشف",()=>Guard(()=>{
            decimal difference=current.BankTotal-current.Rows.Sum(r=>r.Amount);
            bool incomplete=!current.HasExecutionRows||current.MissingNet+current.MissingContractNominal+current.MissingExecutionAmount+current.UnclassifiedCount>0||current.ContractOverrideStale;
            if(!Form("تأكيد صحة كشف المصروفات واعتماده",Text(month+"　·　فرق المطابقة "+Arabic.Money(difference),16,true),
                Text(incomplete||difference!=0?"تنبيه: توجد بيانات تحتاج مراجعة أو فرق في المبالغ. المتابعة تعتمد الكشف يدوياً بالقيم المعروضة.":"راجعت تفاصيل الكشف ومجموع التمويل.")))return;
            _=Working(()=>store.ChangeFunding(s,month,false),"حُفظت نسخة الكشف المعتمدة.",()=>ShowPage(FundingName));
        })));
        if(records.Count==0)return;
        var ordered=records.AsEnumerable().Reverse().ToList();
        bar.Children.Add(ButtonFor("الاعتمادات المحفوظة",()=>{
            var picker=Choices(ordered.Select(r=>r.Snapshot.Month+"　·　"+StoreDate(r.ConfirmedAt)+"　·　"+Arabic.Money(r.Snapshot.BankTotal)+(r.RevokedAt!=null?"　· ملغى":"")));
            if(Form("اختيار نسخة محفوظة للتصدير",Labeled("تاريخ الاعتماد والمبلغ",picker,420))&&picker.SelectedIndex>=0){
                var record=ordered[picker.SelectedIndex];new ReportWindow(record.Snapshot.Report(record.Header,record.RevokedAt==null)){Owner=this}.ShowDialog();
            }
        }));
        var latest=ordered.FirstOrDefault(r=>r.Snapshot.Month==month);
        if(latest!=null&&latest.RevokedAt==null)bar.Children.Add(ButtonFor("إلغاء الاعتماد الأخير",()=>{if(Form("إلغاء الاعتماد",Text(month+"　·　"+StoreDate(latest.ConfirmedAt))))_=Working(()=>store.ChangeFunding(s,month,true),"أُلغي الاعتماد مع حفظ تاريخه.",()=>ShowPage(FundingName));}));
    }
    static string StoreDate(double value)=>new DateTimeOffset(2001,1,1,0,0,0,TimeSpan.Zero).AddSeconds(value).ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    async Task RecoverRefund(A.BackupSnapshot source,string month,bool hasRows)
    {
        var dialog=new OpenFileDialog{Multiselect=true,Filter="ملفات إعادة المبالغ|*.csv"};
        if(dialog.ShowDialog(this)!=true)return;
        try{
            var files=dialog.FileNames;var batch=await Task.Run(()=>RefundRecovery.Read(files,month,source.Employees));
            if(store.Active!=Path.GetDirectoryName(source.SourcePath)||page!=RefundTransfers||month!=SelectedMonth?.Name)return;
            if(!Form("استرجاع دفعة للمراجعة",Text(batch.Date.ToString("yyyy/MM/dd")+"　·　"+batch.People.Count+" منتسب　·　"+Arabic.Money(batch.People.Sum(p=>(decimal)p.Amount))),
                Text(hasRows?"سيُستبدل محتوى المحرر الحالي. تُحفظ الدفعة المسترجعة عند اختيار حفظ الدفعة.":"راجع الجهة والمستفيدين ثم اختر حفظ الدفعة.")))return;
            foreach(var code in batch.People.Where(p=>p.Bank==p.Iban.Substring(4,4)).Select(p=>p.Bank).Distinct().ToArray()){
                var name=new TextBox();if(!Form("اسم المصرف للكتاب",Labeled(code,name,340))||string.IsNullOrWhiteSpace(name.Text))return;
                batch=batch with{People=batch.People.Select(p=>p.Bank==code?p with{Bank=name.Text.Trim()}:p).ToList()};
            }
            RefundDesk(source,month,batch);
        }catch(Exception e){Error(e);}
    }
}
