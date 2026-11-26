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
    record IndividualBook(string Title,string Id,A.TransferEntry? Entry=null)
    {
        public bool NeedsLink => Entry==null && LinkableBooks.Contains(Id);
    }
    static readonly string[] LinkableBooks=["ramadi","housing","water","rafidain_anbar"];

    static List<IndividualBook> BranchBooks(A.BackupSnapshot source,A.TransferSnapshot transfer,ContractBatch? split,bool held)
    {
        var books=new List<IndividualBook>();
        var entities=transfer.Entries.Where(e=>e.Group==A.TransferGroup.Anbar).ToArray();
        if(split!=null)books.AddRange(entities.Select(e=>new IndividualBook(e.Title,e.Id.StartsWith("deduction|")?"split":"entry_only",e)));
        else foreach(var id in LinkableBooks){
            var entry=A.IndividualLetterLinks.Resolve(source,id,entities);
            if(id=="rafidain_anbar"&&entry==null){
                var matches=entities.Where(e=>A.TransferPreparation.IsDirectAnbar(e.Title)).Take(2).ToArray();
                if(matches.Length==1)entry=matches[0];
            }
            books.Add(new(BookTitles[id],id,entry));
        }
        if(split==null){
            var linked=books.Select(b=>b.Entry?.Id).ToHashSet();
            books.AddRange(entities.Where(e=>!linked.Contains(e.Id)).Select(e=>new IndividualBook(e.Title,"entry_only",e)));
        }
        if(!held)foreach(var id in new[]{"food_company","food_tax","tax_social","cash_salary","treasury_guarantee","treasury_schools"})
            books.Add(new(BookTitles[id],id));
        if(split==null||held&&split.Guarantee>0)books.Add(new(BookTitles["contracts"],"contracts"));
        return books;
    }

    static TransferRow BookRow(IndividualBook book,A.BackupSnapshot source,string month,ContractBatch? split,
        IReadOnlyDictionary<string,decimal> originals,bool hasPayroll,Lazy<A.BankSummary> bank,Lazy<Dictionary<string,A.TemplateLetterPdf.ItemSummary>> items)
    {
        decimal? amount=null;
        string sourceName="",issue="";
        var entry=book.Entry;
        try{
            if(entry!=null){
                amount=entry.Amount;
                sourceName=entry.Title;
                var notes=A.TransferPreparation.Issues(entry).ToList();
                if(book.Id=="entry_only")notes.Add("قيد تحويل بلا كتاب منفرد مرتبط");
                issue=string.Join("، ",notes);
            }else if(book.NeedsLink)issue="اختر جهة استقطاع الشهر من خيارات الربط";
            else if(book.Id.StartsWith("treasury_")){
                var summaries=items.Value;
                var names=TreasurySources(Builtin(source,book.Id),book.Id,summaries.Where(kv=>kv.Value.Total>0).Select(kv=>kv.Key));
                sourceName=string.Join(" + ",names.OfType<string>());
                if(names.All(n=>n!=null) && names.Distinct().Count()==names.Length)
                    amount=names.Sum(n=>summaries[n!].Total);
                else issue="اختر بنود الرواتب داخل الكتاب";
            }else if(book.Id=="contracts" && split!=null)amount=split.Guarantee;
            else if(hasPayroll){
                amount=A.IndividualLetterPreparation.PayrollFigures(book.Id,bank.Value).Total;
                sourceName="رواتب "+month;
            }else issue="لا توجد رواتب لهذا الشهر";
        }catch(System.IO.InvalidDataException error){issue=error.Message;}
        return new(entry,book,book.Title,sourceName,entry==null||!originals.TryGetValue(entry.Id,out var original)?amount:original,amount,issue);
    }

    enum BookCommand { Preview, Export, EditForExport }

    async Task PreviewTransferLetter(string title,Func<byte[]> generate,Action export)
    {
        if(busy)return;
        byte[] pdf;
        busy=true;IsEnabled=false;
        try{pdf=await Task.Run(generate);}
        catch(Exception ex){Error(ex);return;}
        finally{busy=false;IsEnabled=true;}
        var preview=new LetterPreview(pdf,title,exportLetter:true){Owner=this};
        if(preview.ShowDialog()==true)export();
    }

    void OpenIndividual(A.BackupSnapshot s,A.TransferSnapshot transfer,ContractBatch? split,bool held,IndividualBook selected,BookCommand command){
        var (title,id,entity)=selected;
        if(selected.NeedsLink)throw new System.IO.InvalidDataException("اربط جهة التنفيذ بهذا الكتاب أولاً.");
        if(id.StartsWith("treasury_")){TreasuryBook(s,transfer.Month,id,command);return;}
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
        if(command==BookCommand.EditForExport){
            var draft=EditLetter(initial,book,s,transfer.Month,render:t=>Generate(t.Style())["الكتاب.pdf"]);
            if(draft==null)return;
            initial=draft;
        }
        initial.Validate();
        A.IndividualLetterBody.Validate(book,initial.Body);
        var style=initial.Style();
        var files=new Lazy<Dictionary<string,byte[]>>(()=>Generate(style));
        void Export()=>_=SaveFile(title,"zip",()=>Package(files.Value),
            mark?()=>store.UpdateBatch(s,transfer.Month,prepared:marker,date:DateTime.Today):null,
            mark?()=>ShowPage(held?DeferredTransfers:CurrentTransfers):null);
        if(command==BookCommand.Preview)_=PreviewTransferLetter(title,()=>files.Value["الكتاب.pdf"],Export);
        else Export();
    }
}
