using System.Windows.Controls;
using A=SalaryDesktop.Core;
namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    static A.ReportSheet SplitSheet(A.BackupSnapshot s,SplitPart part,bool held){
        var rows=held?part.Deferred:part.Current;
        return new(A.ReportSheet.HeaderFor(s),part.Entity.Group.Title+" · "+part.Entity.Group.Month,
            [new("ت",5,A.ReportColumnKind.Serial),new("الرتبة",15,A.ReportColumnKind.Text),new("الاسم الكامل",35,A.ReportColumnKind.Text),new("المبلغ",15,A.ReportColumnKind.Amount),new("رقم وتاريخ الاضبارة",28,A.ReportColumnKind.Text)],
            rows.Select((r,i)=>new[]{(i+1).ToString(),r.Rank,r.Name,Arabic.Money(r.Amount),r.Note}).ToArray(),rows.Select(r=>r.Amount).ToArray());
    }
    void IndividualBooks(A.BackupSnapshot s,A.TransferSnapshot transfer,ContractBatch? split,bool held){
        var choices=new List<(string Title,string Id,A.TransferEntry? Entry)>();
        if(split==null||!held)choices.AddRange(new[]{("شركة عراق الجوال","food_company"),("ضريبة عقد الإطعام","food_tax"),("الضريبة والحماية الاجتماعية","tax_social"),("رواتب الكاش","cash_salary")}.Select(x=>(x.Item1,x.Item2,(A.TransferEntry?)null)));
        if(split==null||held&&split.Guarantee>0)choices.Add(("الضمان الاجتماعي للعقود","contracts",null));
        if(split==null||!held)foreach(var treasury in new[]{"treasury_guarantee","treasury_schools"})choices.Add((BookTitles[treasury],treasury,null));
        var entities=transfer.Entries.Where(e=>e.Group==A.TransferGroup.Anbar).ToArray();
        if(split!=null)choices.AddRange(entities.Select(e=>(e.Title,"split",(A.TransferEntry?)e)));
        else foreach(var bookId in new[]{"ramadi","housing","water","rafidain_anbar"}){
            var entry=A.IndividualLetterLinks.Resolve(s,bookId,entities);
            if(bookId=="rafidain_anbar"&&entry==null)entry=entities.SingleOrDefault(e=>A.PayrollAnalysis.Normalize(e.Title).Replace("/","").Split(' ',StringSplitOptions.RemoveEmptyEntries).SequenceEqual(new[]{"مصرف","الرافدين","فرع","الانبار"}));
            if(entry!=null)choices.Add((entry.Title,bookId,entry));
        }
        if(choices.Count==0)throw new System.IO.InvalidDataException("لا توجد كتب منفردة لهذه الدفعة.");
        var picker=Choices(choices.Select(x=>x.Title));
        if(!Form("الكتب المنفردة",FilterBar(("الكتاب",picker,0))))return;
        var (title,id,entity)=choices[picker.SelectedIndex];

        if(id.StartsWith("treasury_")){TreasuryBook(s,transfer.Month,id);return;}
        string marker=(held?"deferred":"current")+"|"+(entity==null?"guarantee":("entity|deductions|"+transfer.Month+"|false|"+entity.Title+"|execution"));
        // Payroll letters unrelated to the suspended amounts are issued from the current month.
        bool mark=split!=null&&(id=="contracts"||entity!=null);
        Dictionary<string,byte[]> Generate(A.GroupLetterSettings style){
            var files=new Dictionary<string,byte[]>();var refs=id=="split"?new Dictionary<string,byte[]>():A.LetterReferencePack.Read(s);
            if(entity!=null){
                A.IndividualEntityPlan plan;
                if(split==null)plan=A.IndividualEntityPreparation.Prepare(s,transfer,id,entity.Id,refs);
                else{
                    var part=split.Parts.Single(p=>p.Entity.Group.Title==entity.Title);var rows=held?part.Deferred:part.Current;
                    var persons=rows.Select(r=>new A.IndividualEntityPerson(r.Name,r.Rank,checked((long)r.Amount))).ToArray();
                    var local=entity.Account==null?null:A.TransferPreparation.LocalAccount(entity.Account)??throw new System.IO.InvalidDataException("راجع الحساب المحلي للجهة.");
                    var amount=checked((long)entity.Amount);
                    plan=new(local==null?"rafidain_anbar":"ramadi",transfer.Month,entity.Title,transfer.Payer,transfer.PayerAccount,local,entity.Account?.Entity??entity.Title,entity.Account?.Branch,amount,A.ArabicAmountWords.Dinars(amount),persons,entity.People);
                    files["قائمة الجهة.pdf"]=A.ReportPdf.Generate([SplitSheet(s,part,held)],Fonts(),DateTime.Now);
                }
                files["الكتاب.pdf"]=A.IndividualEntityPdf.Generate(plan,style,LetterFont(),Logo(),id=="split");
            }else{
                A.IndividualLetterPlan plan;
                if(split!=null&&id=="contracts"){
                    var reference=A.LetterReferencePack.IndividualAccount(refs,id,split.Iban)??throw new System.IO.InvalidDataException("حساب الضمان غير موجود في المرجع.");
                    var acc=A.IndividualAccountCustomization.Resolve(s,id,id,reference,reference)??reference;
                    var share=(long)((decimal?)split.Figures["contractNominalShare"]??(decimal?)split.Figures["availableContractNominalShare"]??0);var retirement=(long)(decimal)split.Figures["contractRetirement"]!;
                    long total=checked((long)split.Guarantee);
                    var lines=new List<A.IndividualLetterLine>{new("حصة التقاعد من الراتب الاسمي",share),new("التوقيفات التقاعدية",retirement)};
                    if(total!=share+retirement)lines.Add(new("التعديل اليدوي",total-share-retirement));
                    plan=new(id,transfer.Month,split.Payer,split.Iban,acc,total,A.ArabicAmountWords.Dinars(total),lines,null,null,[]);
                }else plan=A.IndividualLetterPreparation.Payroll(id,A.BankStatementCalculator.Calculate(s,transfer.Month),transfer.Payer,transfer.PayerAccount,refs,customization:s);
                var cash=id=="cash_salary"?A.CashSalaryListPreparation.Prepare(s,transfer.Month):null;
                files["الكتاب.pdf"]=A.IndividualPayrollPdf.Generate(plan,style,LetterFont(),Fonts().Bold,Logo(),style.FoodContractReference,cash);
            }
            return files;
        }
        var book=id=="split"?(entity?.Account==null?"rafidain_anbar":"ramadi"):id;
        var initial=Builtin(s,book);
        if(id=="split")initial=initial with{Body=DefaultBody(book)};
        var draft=EditLetter(initial,book,s,transfer.Month,render:t=>Generate(t.Style())["الكتاب.pdf"]);
        if(draft==null)return;
        if(id!="split"){store.SaveLetterTemplate(s,id,draft);s=store.Snapshot(transfer.Month);ShowPage(TransfersName);}
        _=SaveFile(title,"zip",()=>Package(Generate(draft.Style())),mark?()=>store.UpdateBatch(s,transfer.Month,prepared:marker,date:DateTime.Today):null,mark?()=>ShowPage(TransfersName):null);
    }
}
