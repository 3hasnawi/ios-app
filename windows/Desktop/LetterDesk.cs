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
        string Or(string? s,string hint)=>string.IsNullOrWhiteSpace(s)?hint:A.LetterTemplate.FormatNumbers(s);
        string Signer(string s){
            var parts=s.Trim().Split('\n');
            if(parts.Length==1)return s;
            if(parts.Length>3)return "توقيع غير مكتمل";
            return string.Join('\n',Enumerable.Range(0,3).Select(i=>i<parts.Length&&!string.IsNullOrWhiteSpace(parts[i])?parts[i]:"…"));
        }
        bool noSigners=new[]{t.FirstSigner,t.SecondSigner,t.ThirdSigner}.All(string.IsNullOrWhiteSpace);
        var preview=t with{Header=Or(t.Header,"الترويسة"),Number=Or(t.Number,"العدد"),Recipient=Or(t.Recipient,"الجهة المرسل إليها"),Subject=Or(t.Subject,"الموضوع"),
            FirstSigner=noSigners?"التوقيع":Signer(t.FirstSigner),SecondSigner=Signer(t.SecondSigner),ThirdSigner=Signer(t.ThirdSigner),Footer=Or(t.Footer,"نسخة منه إلى")};
        string body=System.Text.RegularExpressions.Regex.Replace(Or(t.Body,"متن الكتاب"),@"\{\{([^{}]+)\}\}",m=>"«"+m.Groups[1].Value+"»");
        return A.TemplateLetterPdf.Document(preview,month,body,DocumentFonts(),null,[],fitFooterWritingSpace:true);
    }
    A.LetterTemplate? EditLetter(A.LetterTemplate t,string kind,A.BackupSnapshot s,string month,bool account=false,Func<A.LetterTemplate,byte[]>? render=null,Action<A.LetterTemplate>? extra=null){
        void Validate(A.LetterTemplate result){
            result.Validate(kind!="custom");
            if(kind.EndsWith("_group"))A.GroupLetterPdf.ValidateBodyTemplate(result.Body);
            else if(BookTitles.ContainsKey(kind))A.IndividualLetterBody.Validate(kind,result.Body);
            extra?.Invoke(result);
        }
        var dialog=new LetterWindow(t,kind,s,month,account,Validate,render??(draft=>LayoutSample(draft,month))){Owner=this};
        if(render==null)dialog.PreviewCaption="معاينة تنسيق القالب";
        return dialog.ShowDialog()==true?dialog.Result:null;
    }
    void TemplatesDesk(A.BackupSnapshot s,string month){
        var custom=Store.LetterLibrary(s);
        var picker=Choices(custom.Select(t=>t.Title));
        var command=Choices(custom.Count==0?["إنشاء كتاب مخصص"]:["تعديل الكتاب","إنشاء كتاب مخصص","نسخ الكتاب","حذف الكتاب","تصدير الكتاب"]);
        if(!Form("الكتب المخصصة",FilterBar(("الكتاب",picker,1),("الإجراء",command,0))))return;
        bool fresh=custom.Count==0||command.SelectedIndex==1;
        var template=fresh?new A.LetterTemplate{Title="كتاب مخصص"}:custom[picker.SelectedIndex];
        var title=template.Title;
        if(command.SelectedIndex==3){
            if(Form("حذف القالب",Text(title)))_=Working(()=>store.SaveLetterTemplate(s,null,template!,true),"حُذف القالب.",()=>ShowPage(page));return;
        }
        if(command.SelectedIndex==4){
            decimal? amount=null;
            if(template!.Source=="manual"){
                var field=new TextBox();if(!Form("مبلغ الكتاب",Labeled("المبلغ بالدينار",field)))return;
                if(field.Text.Any(c=>!char.IsDigit(c)&&c!=','&&c!='٬')||!decimal.TryParse(A.TransferPreparation.NormalizeAccount(field.Text.Replace(",","").Replace("٬","")),out var n))throw new InvalidDataException("أدخل مبلغاً صحيحاً.");amount=n;
            }
            var bytes=A.TemplateLetterPdf.Custom(template,s,month,amount,DocumentFonts());new LetterPreview(bytes,title){Owner=this}.ShowDialog();return;
        }
        if(command.SelectedIndex==2)template=template! with{Id=Guid.NewGuid(),Title=title+" (نسخة)"};
        EditTransferTemplate(s,month,"custom",template);
    }
    void EditTransferTemplate(A.BackupSnapshot s,string month,string kind,A.LetterTemplate? initial=null){
        var t=initial??Builtin(s,kind);
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
        if(edited!=null)_=Working(()=>store.SaveLetterTemplate(s,kind=="custom"?null:kind,edited,account:update),"حُفظ القالب ضمن النسخة الاحتياطية.",()=>ShowPage(page));
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
