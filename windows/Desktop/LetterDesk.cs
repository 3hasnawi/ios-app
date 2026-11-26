using System.IO;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;
namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    static readonly Dictionary<string,string> BookTitles=new(){
        ["rafidain_group"]="مجموعة الرافدين",["other_banks_group"]="مجموعة الرشيد والمصارف الأخرى",["food_company"]="شركة عراق الجوال",
        ["food_tax"]="ضريبة عقد الإطعام",["tax_social"]="الضريبة والحماية الاجتماعية",["cash_salary"]="رواتب الكاش",["contracts"]="الضمان الاجتماعي للعقود",
        ["ramadi"]="تنفيذ الرمادي",["housing"]="الإسكان",["water"]="الموارد المائية",["rafidain_anbar"]="الرافدين / فرع الأنبار",
        ["treasury_guarantee"]="خزينة الأنبار · التضمين",["treasury_schools"]="خزينة الأنبار · رسم الطابع وبناء المدارس"};
    static A.LetterFonts DocumentFonts()=>A.LetterFonts.Resolve(LetterFont(),Fonts().Bold,Logo());
    static string DefaultBody(string id)=>id.EndsWith("_group")?A.GroupLetterPdf.DefaultBodyTemplate:A.IndividualLetterBody.Default(id);
    static A.LetterTemplate Builtin(A.BackupSnapshot s,string id){
        var saved=A.GroupLetterSettingsReader.Read(s,id);
        if(id.StartsWith("treasury_")&&saved.Template==null){
            saved=A.GroupLetterSettingsReader.Read(s,"food_tax") with{Template=null,BodyTemplate=null,BodyMonth=null,OutgoingMonth=null,SignatureOffset=0,Footer="نسخة منه إلى:\nمديرية خزينة الأنبار للاطلاع وإعلامنا الاستلام ... مع التقدير.\nشعبة المصروفات / شعبة الموازنة / شعبة الرواتب / إجراء اللازم والمتابعة.\nالأرشيف إجراء اللازم."};
        }
        return A.LetterTemplate.From(saved,BookTitles[id],DefaultBody(id));
    }
    static byte[] LayoutSample(A.LetterTemplate t,string month){
        t.Validate();int period=A.MonthNames.SortKey(month);
        var vals=new Dictionary<string,string>{{"الشهر",A.AdaptiveLetterPdf.MonthName(period%100)},{"السنة",A.AdaptiveLetterPdf.Eastern((period/100).ToString())},
            {"المبلغ","١٠٠,٠٠٠"},{"المبلغ كتابة","مائة ألف دينار"},{"حسابنا","حساب الجهة الدافعة"},{"حساب المستفيد","حساب المستفيد"},{"الحساب",string.IsNullOrWhiteSpace(t.Account)?"الحساب المرتبط":t.Account},
            {"المستفيد","اسم المستفيد"},{"المصرف","اسم المصرف"},{"عقد الإطعام",t.FoodContractReference??"رقم العقد"},{"الرتبة","الرتبة"},{"الاسم","اسم المنتسب"},{"سبب التضمين","سبب التضمين"}};
        return A.TemplateLetterPdf.Document(t,month,A.LetterTemplate.Fill(A.LetterTemplate.FormatNumbers(t.Body),vals),DocumentFonts(),null,[]);
    }
    A.LetterTemplate? EditLetter(A.LetterTemplate t,string kind,A.BackupSnapshot s,string month,bool account=false,Func<A.LetterTemplate,byte[]>? render=null,Action<A.LetterTemplate>? extra=null){
        void Validate(A.LetterTemplate result){
            result.Validate(kind!="custom");
            if(kind.EndsWith("_group"))A.GroupLetterPdf.ValidateBodyTemplate(result.Body);
            else if(BookTitles.ContainsKey(kind))A.IndividualLetterBody.Validate(kind,result.Body);
            extra?.Invoke(result);
        }
        var dialog=new LetterWindow(t,kind,s,month,account,Validate,render??(draft=>LayoutSample(draft,month))){Owner=this};
        if(render==null)dialog.PreviewCaption="معاينة التنسيق ببيانات توضيحية";
        return dialog.ShowDialog()==true?dialog.Result:null;
    }
    void TemplatesDesk(A.BackupSnapshot s,string month){
        var custom=Store.LetterLibrary(s);
        var books=BookTitles.Select(b=>(b.Key,b.Value,(A.LetterTemplate?)null)).Concat(custom.Select(t=>("custom",t.Title,(A.LetterTemplate?)t))).ToList();
        var picker=Choices(books.Select(b=>b.Item2));
        var command=Choices(["تعديل القالب","إنشاء كتاب مخصص","نسخ قالب مخصص","حذف قالب مخصص","تصدير كتاب مخصص"]);
        if(!Form("قوالب الكتب",FilterBar(("القالب",picker,1),("الإجراء",command,0))))return;
        var (kind,title,template)=books[picker.SelectedIndex];
        bool fresh=command.SelectedIndex==1;
        if(fresh){kind="custom";template=new A.LetterTemplate{Title="كتاب مخصص"};}
        if(command.SelectedIndex>=2&&template==null)throw new InvalidDataException("اختر أحد كتبك المخصصة.");
        if(command.SelectedIndex==3){
            if(Form("حذف القالب",Text(title)))_=Working(()=>store.SaveLetterTemplate(s,null,template!,true),"حُذف القالب.",()=>ShowPage(TransfersName));return;
        }
        if(command.SelectedIndex==4){
            decimal? amount=null;
            if(template!.Source=="manual"){
                var field=new TextBox();if(!Form("مبلغ الكتاب",Labeled("المبلغ بالدينار",field)))return;
                if(field.Text.Any(c=>!char.IsDigit(c)&&c!=','&&c!='٬')||!decimal.TryParse(A.TransferPreparation.NormalizeAccount(field.Text.Replace(",","").Replace("٬","")),out var n))throw new InvalidDataException("أدخل مبلغاً صحيحاً.");amount=n;
            }
            var bytes=A.TemplateLetterPdf.Custom(template,s,month,amount,DocumentFonts());new LetterPreview(bytes){Owner=this}.ShowDialog();_=SaveFile(title,"pdf",()=>bytes);return;
        }
        if(command.SelectedIndex==2)template=template! with{Id=Guid.NewGuid(),Title=title+" (نسخة)"};
        var t=template??Builtin(s,kind);
        var binding=BookAccount(s,kind,month);
        if(binding!=null)t=t with{Account=A.IndividualAccountCustomization.Read(s,kind)?.Value??binding.Original};
        bool treasury=kind.StartsWith("treasury_");
        if(treasury&&string.IsNullOrEmpty(t.Account)&&s.SettingsPlists.ContainsKey("transferReferenceArchiveData")&&A.TransferPreparation.ValidIban(A.TransferPreparation.Payer(s).Iban)){
            var refs=A.LetterReferencePack.Read(s);
            t=t with{Account=A.LetterReferencePack.IndividualAccount(refs,"tax_social",A.TransferPreparation.Payer(s).Iban)??""};
        }
        A.IndividualAccountOverride? update=null;
        var edited=EditLetter(t,kind,s,month,kind=="custom"||binding!=null||treasury,extra:draft=>{
            if(treasury&&draft.Account.Length>0&&(!A.TransferPreparation.ValidIban(draft.Account)||draft.Account==A.TransferPreparation.Payer(s).Iban))throw new InvalidDataException("راجع آيبان حساب الخزينة.");
            if(binding!=null){
                string value=A.TransferPreparation.NormalizeAccount(draft.Account);
                if(value.Length==0||value==binding.Original){update=new("","","","");return;}
                bool local=kind is "housing" or "water";
                if(local?value.Length!=15||!value.All(char.IsAsciiDigit):!A.TransferPreparation.ValidIban(value)||value.Substring(4,4)!=binding.Original.Substring(4,4)||value==A.TransferPreparation.Payer(s).Iban)throw new InvalidDataException("راجع حساب المستفيد ومصرفه.");
                
                update=binding with{Value=value};
            }
        });
        if(edited!=null)_=Working(()=>store.SaveLetterTemplate(s,kind=="custom"?null:kind,edited,account:update),"حُفظ القالب ضمن النسخة الاحتياطية.",()=>ShowPage(TransfersName));
    }
    static A.IndividualAccountOverride? BookAccount(A.BackupSnapshot s,string id,string month){
        bool entity=id is "ramadi" or "water" or "housing";
        if(!entity&&id is not ("food_company" or "food_tax" or "tax_social" or "contracts" or "cash_salary"))return null;
        var payer=A.TransferPreparation.Payer(s).Iban;if(!A.TransferPreparation.ValidIban(payer)||!s.SettingsPlists.ContainsKey("transferReferenceArchiveData"))return null;
        var refs=A.LetterReferencePack.Read(s);if(refs.Count==0)return null;
        var reference=A.LetterReferencePack.IndividualAccount(refs,id,payer);if(reference==null)return null;
        if(!entity)return new(reference,id,reference,reference);
        var entry=A.IndividualLetterLinks.Resolve(s,id,A.TransferPreparation.Prepare(s,month).Entries.Where(e=>e.Group==A.TransferGroup.Anbar).ToArray());
        if(entry?.Account==null)return null;
        var original=id=="ramadi"?entry.Account.Iban:A.TransferPreparation.LocalAccount(entry.Account);
        return string.IsNullOrEmpty(original)?null:new(original,entry.Title,original,reference);
    }
}
