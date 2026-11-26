using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    static byte[] Package(Dictionary<string,byte[]> files){
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))foreach(var (name,bytes) in files){
            using var output=zip.CreateEntry(name,CompressionLevel.Optimal).Open();output.Write(bytes);
        }
        return memory.ToArray();
    }
    static byte[] Logo()=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"IraqEmblem.png"));
    static byte[] LetterFont()=>File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"timesbd.ttf"));
    void TransferDesk(A.BackupSnapshot s,string month)
    {
        var dock=new DockPanel();var actions=Bar();var note=Text("",14);
        var top=Column(Text("تحويل استقطاعات "+month,21,true),actions,note);DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var grid=GridView(("الجهة","Title",1.7),("المصرف","Bank",1.3),("قبل الفصل","Original",.8),("مبلغ الدفعة","Amount",.8),("المراجعة","Issues",1.4));dock.Children.Add(grid);CanvasArea.Children.Add(dock);
        actions.Children.Add(ButtonFor("الكتب المحفوظة",()=>{backPage=TransfersName;ShowPage("الكتب المحفوظة");},"\uE8B7"));
        actions.Children.Add(ButtonFor("إعادة مبالغ المنتسبين",()=>Guard(()=>RefundDesk(s,month)),"\uE8AB"));
        actions.Children.Add(ButtonFor("قوالب الكتب",()=>Guard(()=>TemplatesDesk(s,month)),"\uE70F"));
        var batch=Store.ActiveBatch(s,month);
        var all=batch==null?A.TransferPreparation.Prepare(s,month):null;
        actions.Children.Add(ButtonFor("الحسابات والربط",()=>Guard(()=>{
            var payer=A.TransferPreparation.Payer(s);var name=new TextBox{Text=payer.Name};var iban=new TextBox{Text=payer.Iban};
            var saved=new ComboBox{ItemsSource=s.Accounts,DisplayMemberPath="Entity"};var entity=new TextBox();var branch=new TextBox();var bic=new TextBox();var beneficiary=new TextBox();var local=new TextBox();
            saved.SelectionChanged+=(_,_)=>{if(saved.SelectedItem is A.ExecutionAccount a){entity.Text=a.Entity;branch.Text=a.Branch;bic.Text=a.Swift;beneficiary.Text=a.Iban;local.Text=a.LocalAccount??"";}};
            var entries=all?.Entries??[];var link=new ComboBox{ItemsSource=entries,DisplayMemberPath="Title"};
            if(!Form("الحسابات",Bar(Labeled("الجهة الدافعة",name,250),Labeled("آيبان الجهة الدافعة",iban,250)),
                FilterBar(("اختيار حساب محفوظ",saved,1),("ربط ببند أو جهة",link,1)),Bar(Labeled("اسم حساب المستفيد",entity,250),Labeled("المصرف والفرع",branch,250)),Bar(Labeled("آيبان المستفيد",beneficiary,240),Labeled("SWIFT",bic,120),Labeled("الحساب المحلي",local,145))))return;
            var accounts=s.Accounts.ToList();Dictionary<string,string>? links=null;
            if(entity.Text.Trim().Length>0){
                var a=new A.ExecutionAccount(entity.Text.Trim(),A.TransferPreparation.NormalizeAccount(beneficiary.Text),branch.Text.Trim(),bic.Text.Trim().ToUpperInvariant(),string.IsNullOrWhiteSpace(local.Text)?null:A.TransferPreparation.NormalizeAccount(local.Text));
                if(saved.SelectedItem is A.ExecutionAccount old&&old.Entity!=a.Entity)throw new InvalidDataException("احتفظ باسم الحساب لتعديل بياناته.");
                var group=A.TransferPreparation.Classify(a);if(group==A.TransferGroup.Unresolved||(group==A.TransferGroup.Anbar?A.TransferPreparation.LocalAccount(a)==null:!A.TransferPreparation.ValidIban(a.Iban)||!A.TransferPreparation.ValidSwift(a.Swift)))throw new InvalidDataException("راجع الفرع ورموز الحساب.");
                accounts.RemoveAll(x=>A.PayrollAnalysis.Normalize(x.Entity)==A.PayrollAnalysis.Normalize(a.Entity));accounts.Add(a);
                if(link.SelectedItem is A.TransferEntry item)links=new(){{item.Id.StartsWith("payroll|")?item.Id[8..]:"deduction|"+A.PayrollAnalysis.Normalize(item.Title),A.PayrollAnalysis.Normalize(a.Entity)}};
            }
            string who=name.Text,value=iban.Text;_=Working(()=>store.SaveAccounts(s,who,value,accounts,links),"حُفظت الحسابات.",()=>ShowPage(TransfersName));
        })));
        var phase=Choices(batch==null?["التحويل الشامل"]:["الدفعة الحالية","دفعة العقود المؤجلة"]);
        var group=Choices(["الرافدين","الرشيد والمصارف الأخرى","فرع الأنبار","جهات تحتاج مراجعة"]);var date=new DatePicker{SelectedDate=DateTime.Today};
        top.Children.Insert(top.Children.IndexOf(note),FilterBar(("الدفعة",phase,0),("مجموعة التحويل",group,0),("تاريخ التحويل",date,0)));
        A.TransferSnapshot Plan()=>batch?.Plan(phase.SelectedIndex==1)??all!;
        A.TransferGroup Group()=>(A.TransferGroup)group.SelectedIndex;
        void Draw(){
            var plan=Plan();var rows=plan.Entries.Where(e=>e.Group==Group()).ToArray();
            grid.ItemsSource=rows.Select(e=>new{e.Title,Bank=e.Account?.Branch,Original=Arabic.Money(batch?.Parts.First(p=>p.Entity.Group.Title==e.Title).Entity.Group.Total??e.Amount),Amount=Arabic.Money(e.Amount),Issues=string.Join("، ",A.TransferPreparation.Issues(e))}).ToArray();
            note.Text="مجموع المجموعة　"+Arabic.Money(rows.Sum(e=>e.Amount))+(batch!=null?"　·　إجمالي المؤجل مع الضمان　"+Arabic.Money(batch.Held):"")+(plan.SourceWarnings.Count>0?"\n"+string.Join("　·　",plan.SourceWarnings):"");
        }
        actions.Children.Add(ButtonFor(batch==null?"تأجيل استقطاعات العقود":"إلغاء التأجيل",()=>Guard(()=>{
            var proposed=batch==null?Store.PrepareBatch(s,month):null;
            if(!Form(proposed==null?"العودة إلى التحويل الشامل":"اعتماد الفصل",Text(month),Text("استقطاعات الجهات والصندوق　"+Arabic.Money((proposed??batch!).Held-(proposed??batch!).Guarantee)),Text("ضمان العقود　"+Arabic.Money((proposed??batch!).Guarantee))))return;
            _=Working(()=>store.UpdateBatch(s,month,proposed),"حُفظت حالة الشهر.",()=>ShowPage(TransfersName));}),"\uE8B5"));
        bool Changed()=>batch!=null&&s.Months.Single(m=>m.Name==month).ImportedAt!=batch.ImportedAt;
        void CheckSource(){if(Changed())throw new InvalidDataException("تغير استيراد الشهر بعد اعتماده. راجع التغييرات وألغ الفصل ثم اعتمده بالمبالغ المحدثة.");}
        if(batch==null)actions.Children.Add(ButtonFor("ربط جهة بكتاب",()=>Guard(()=>{
            CheckSource();var entities=Plan().Entries.Where(e=>e.Group==A.TransferGroup.Anbar).ToArray();
            if(entities.Length==0){Status.Text="لا توجد جهات مصنفة ضمن فرع الأنبار لهذا الشهر.";return;}
            string[] keys=["ramadi","housing","water","rafidain_anbar"];var names=Choices(keys.Select(k=>BookTitles[k]));var list=Choices(entities.Select(e=>e.Title));
            if(!Form("ربط كتاب منفرد",Labeled("الكتاب",names,350),Labeled("جهة التنفيذ",list,350)))return;
            var id=keys[names.SelectedIndex];var entity=entities[list.SelectedIndex];
            _=Working(()=>store.LinkIndividual(s,id,entity),"حُفظ ربط الكتاب.",()=>ShowPage(TransfersName));
        })));
        actions.Children.Add(ButtonFor("الكتب المنفردة",()=>Guard(()=>{CheckSource();IndividualBooks(s,Plan(),batch,phase.SelectedIndex==1);}),"\uE8A5"));
        actions.Children.Add(ButtonFor("تصدير الكتاب وملف CSV",()=>Guard(()=>{
            CheckSource();var g=Group();if(g is not (A.TransferGroup.Rafidain or A.TransferGroup.OtherBanks))throw new InvalidDataException("اختر مجموعة الرافدين أو الرشيد والمصارف الأخرى.");
            var plan=Plan();var day=date.SelectedDate??throw new InvalidDataException("حدد تاريخ التحويل.");
            string marker=(phase.SelectedIndex==1?"deferred":"current")+"|group|"+System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(g.ToString());
            if(batch!=null)day=Store.PreparedDate(s,month,marker)??day;
            bool held=phase.SelectedIndex==1;var letter=A.GroupLetterPreparation.Prepare(plan,g,DateOnly.FromDateTime(day));
            var bookId=g==A.TransferGroup.Rafidain?"rafidain_group":"other_banks_group";
            var edited=EditLetter(Builtin(s,bookId),bookId,s,month,render:t=>A.GroupLetterPdf.Generate(letter,t.Style(),LetterFont(),Fonts().Bold,Logo()));
            if(edited==null)return;
            store.SaveLetterTemplate(s,bookId,edited);s=store.Snapshot(month);var style=edited.Style();
            var filename=(string)group.SelectedItem+" - "+(string)phase.SelectedItem;
            _=SaveFile(filename,"zip",()=>{
                var pdf=A.GroupLetterPdf.Generate(letter,style,LetterFont(),Fonts().Bold,Logo());
                var files=new Dictionary<string,byte[]>{{"كتاب التحويل.pdf",pdf},{"التحويل.csv",letter.Csv}};
                if(batch!=null){
                    var parts=batch.Parts.Where(p=>p.Portion(held)?.Group==g).ToList();
                    var sheets=parts.Where(p=>p.Entity.PayrollRecipient==null).Select(p=>SplitSheet(s,p,held)).ToList();
                    if(sheets.Count>0)files["كشوف جهات التنفيذ.pdf"]=A.ReportPdf.Generate(sheets,Fonts(),DateTime.Now);
                    var fund=parts.FirstOrDefault(p=>p.Entity.PayrollRecipient=="fundDonations");
                    if(fund!=null){
                        var amounts=(held?fund.Deferred:fund.Current).ToLookup(r=>A.FundReport.CategoryOf(r.Rank));
                        var data=A.FundReport.Categories.Select((cat,i)=>new A.FundReport.Row(cat,amounts[i].Count(),amounts[i].Sum(r=>r.Amount))).ToList();
                        files["كشف صندوق الشهداء.pdf"]=A.FundReport.Pdf(data,A.FundReport.Title(s,month),Fonts(),DateTime.Now);
                    }
                }
                var bytes=Package(files);
                return bytes;
            },batch==null?null:()=>store.UpdateBatch(s,month,prepared:marker,date:day),batch==null?null:()=>ShowPage(TransfersName));
        }),"\uE74E",true));
        phase.SelectionChanged+=(_,_)=>Guard(Draw);group.SelectionChanged+=(_,_)=>Guard(Draw);Draw();
    }
}
