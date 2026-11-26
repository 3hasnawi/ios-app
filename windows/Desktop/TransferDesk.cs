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
    const string CurrentTransfers="الدفعة الشاملة",DeferredTransfers="الدفعات المؤجلة",RefundTransfers="إعادة المبالغ";
    A.TransferGroup currentGroup=A.TransferGroup.Rafidain,deferredGroup=A.TransferGroup.Rafidain;

    void TransferHome()
    {
        var cards=new System.Windows.Controls.Primitives.UniformGrid{Columns=3,VerticalAlignment=VerticalAlignment.Top};
        foreach(var (name,description,icon) in new[]{
            (CurrentTransfers,"كتب المصارف وفرع الأنبار والكتب المنفردة","\uE8A5"),
            (DeferredTransfers,"فصل حصة العقود وتحويلها لاحقاً","\uE823"),
            (RefundTransfers,"دفعات المنتسبين وملفات المصارف","\uE8AB")
        }){
            var card=ButtonFor(name,()=>ShowPage(name));
            card.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            card.Content=Column(Glyph(icon),Text(name,18,true),Text(description,14));
            cards.Children.Add(card);
        }
        CanvasArea.Children.Add(cards);
    }

    static Button OptionsButton(Action<ContextMenu> populate)
    {
        var menu=new ContextMenu{FlowDirection=FlowDirection.RightToLeft};
        var button=ButtonFor("خيارات",()=>{
            menu.Items.Clear();populate(menu);
            menu.IsOpen=true;
        },"\uE70D");
        menu.PlacementTarget=button;
        menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu=menu;
        button.ContextMenuOpening+=(_,e)=>{e.Handled=true;button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
        return button;
    }
    void MenuAction(ContextMenu menu,string title,Action action)
    {
        var item=new MenuItem{Header=title};item.Click+=(_,_)=>Guard(action);menu.Items.Add(item);
    }

    void TransferDesk(A.BackupSnapshot s,string month,bool held)
    {
        var batch=Store.ActiveBatch(s,month);
        var route=held?DeferredTransfers:CurrentTransfers;
        var head=Column(Bar(ButtonFor("التحويلات",()=>ShowPage(TransfersName),"\uE72A"),Text(month,16,true)));
        var dock=new DockPanel();
        DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);CanvasArea.Children.Add(dock);
        if(held){
            void ChangeDeferral(){
                var proposed=batch==null?Store.PrepareBatch(s,month):null;
                var reviewed=proposed??batch!;
                if(!Form(proposed==null?"إلغاء التأجيل والعودة إلى التحويل الشامل":"اعتماد فصل استقطاعات العقود",Text(month),
                    Text("استقطاعات الجهات والصندوق　"+Arabic.Money(reviewed.Held-reviewed.Guarantee)),Text("ضمان العقود　"+Arabic.Money(reviewed.Guarantee))))return;
                _=Working(()=>store.UpdateBatch(s,month,proposed),"حُفظت حالة الشهر.",()=>ShowPage(route));
            }
            head.Children.Add(Bar(Text(batch==null?"لا توجد دفعة مؤجلة لهذا الشهر.":"مجموع الدفعة المؤجلة　"+Arabic.Money(batch.Held),16,true),
                ButtonFor(batch==null?"تأجيل استقطاعات العقود":"إلغاء التأجيل",()=>Guard(ChangeDeferral))));
            if(batch==null)return;
        }else if(batch!=null)head.Children.Add(Text("الدفعة الحالية بعد فصل حصة العقود",16,true));

        var plan=batch?.Plan(held)??A.TransferPreparation.Prepare(s,month);
        void CheckSource(){
            if(batch!=null&&s.Months.Single(m=>m.Name==month).ImportedAt!=batch.ImportedAt)
                throw new InvalidDataException("تغير استيراد الشهر بعد اعتماد الفصل. راجع المبالغ في الدفعات المؤجلة.");
        }
        var group=Choices(["الرافدين","الرشيد والمصارف الأخرى","فرع الأنبار والكتب المنفردة","جهات تحتاج مراجعة"]);
        group.SelectedIndex=(int)(held?deferredGroup:currentGroup);
        var date=new DatePicker{SelectedDate=DateTime.Today};
        var filter=FilterBar(("المجموعة",group,1),("تاريخ التحويل",date,0));
        head.Children.Add(filter);
        var actions=Bar();head.Children.Add(actions);
        var info=Text("",14);head.Children.Add(info);
        if(plan.SourceWarnings.Count>0)
            head.Children.Add(new Expander{Header="ملاحظات المراجعة ("+plan.SourceWarnings.Count+")",Content=new ScrollViewer{MaxHeight=95,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=Text(string.Join("\n",plan.SourceWarnings),13)}});
        var table=GridView(("الجهة","Entry.Title",1.8),("المصرف","Entry.Account.Branch",1.4),("قبل الفصل","Original",.9),("مبلغ الدفعة","Amount",.9),("المراجعة","Issues",1.4));
        if(batch==null)table.Columns.RemoveAt(2);
        dock.Children.Add(table);
        void Draw(){
            var g=(A.TransferGroup)group.SelectedIndex;
            if(held)deferredGroup=g;else currentGroup=g;
            date.IsEnabled=g is A.TransferGroup.Rafidain or A.TransferGroup.OtherBanks;
            var rows=plan.Entries.Where(e=>e.Group==g).ToArray();
            table.ItemsSource=rows.Select(e=>new TransferRow(e,Arabic.Money(batch?.Parts.First(p=>p.Entity.Group.Title==e.Title).Entity.Group.Total??e.Amount),
                Arabic.Money(e.Amount),string.Join("، ",A.TransferPreparation.Issues(e)))).ToArray();
            info.Text="عدد الجهات والبنود　"+rows.Length+"　·　مجموع المجموعة　"+Arabic.Money(rows.Sum(e=>e.Amount));
            actions.Children.Clear();
            ComboBox? books=null;
            IndividualBook? SelectedBook()=>books?.SelectedItem as IndividualBook;
            if(g==A.TransferGroup.Anbar){
                var list=BranchBooks(s,plan,batch,held);
                books=new ComboBox{ItemsSource=list,DisplayMemberPath="Caption",SelectedIndex=list.Count>0?0:-1,MinWidth=195,MaxWidth=285,Margin=new Thickness(3)};
                System.Windows.Automation.AutomationProperties.SetName(books,"الكتب المنفردة");
                actions.Children.Add(Text("الكتاب"));actions.Children.Add(books);
                var open=ButtonFor("معاينة وتصدير الكتاب",()=>Guard(()=>{
                    CheckSource();if(SelectedBook() is {} book)OpenIndividual(s,plan,batch,held,book);
                }),"\uE8A5",true);
                void Availability(){
                    open.IsEnabled=SelectedBook() is {NeedsLink:false};
                    open.ToolTip=SelectedBook()?.NeedsLink==true?"من خيارات اختر ربط جهة بالكتاب.":null;
                }
                books.SelectionChanged+=(_,_)=>Availability();Availability();actions.Children.Add(open);
            }else if(g!=A.TransferGroup.Unresolved){
                var export=ButtonFor("الكتاب وملف CSV",()=>Guard(()=>{
                    CheckSource();ExportGroup(s,month,plan,g,batch,held,date.SelectedDate??throw new InvalidDataException("حدد تاريخ التحويل."));
                }),"\uE74E",true);
                export.IsEnabled=rows.Length>0;actions.Children.Add(export);
            }
            if(batch!=null && g!=A.TransferGroup.Unresolved){
                var parts=batch.Parts.Where(p=>p.Portion(held)?.Group==g).ToList();
                var entities=parts.Where(p=>p.Entity.PayrollRecipient==null).ToList();
                if(entities.Count>0){
                    actions.Children.Add(PreviewButton("كشوف جهات التنفيذ - "+month,()=>{
                        CheckSource();
                        return Task.Run(()=>ExecutionSheetsPdf(s,entities,held));
                    },caption:"معاينة كشوف جهات التنفيذ"));
                }
                if(parts.FirstOrDefault(p=>p.Entity.PayrollRecipient=="fundDonations") is {} fund)
                    actions.Children.Add(PreviewButton("كشف صندوق الشهداء - "+month,()=>{
                        CheckSource();
                        return Task.Run(()=>SplitFundPdf(s,month,fund,held));
                    },caption:"معاينة كشف الصندوق"));
            }
            var options=OptionsButton(menu=>{
                if(SelectedBook() is {} book && book.Id!="split")
                    MenuAction(menu,"تعديل الكتاب",()=>EditTransferTemplate(s,month,book.Id));
                if(g is A.TransferGroup.Rafidain or A.TransferGroup.OtherBanks)
                    MenuAction(menu,"تعديل كتاب المجموعة",()=>EditTransferTemplate(s,month,g==A.TransferGroup.Rafidain?"rafidain_group":"other_banks_group"));
                if(batch==null){
                    if(SelectedBook() is {} individual && LinkableBooks.Contains(individual.Id))
                        MenuAction(menu,"ربط جهة بالكتاب",()=>LinkBook(s,individual,rows,(table.SelectedItem as TransferRow)?.Entry));

                }
                if(!held)MenuAction(menu,batch==null?"تعديل الحساب وربط الجهة":"حسابات وربط التحويلات اللاحقة",()=>{
                    var live=batch==null?plan:A.TransferPreparation.Prepare(s,month);
                    var selected=(table.SelectedItem as TransferRow)?.Entry;
                    TransferAccounts(s,live,live.Entries.FirstOrDefault(e=>e.Id==selected?.Id),batch!=null);
                });
                if(!held)MenuAction(menu,"الكتب المخصصة",()=>TemplatesDesk(s,month));
                if(SelectedBook() is {Id:"split"} splitBook)
                    MenuAction(menu,"تعديل كتاب الجهة لهذه الدفعة",()=>{CheckSource();OpenIndividual(s,plan,batch,held,splitBook);});
            });
            if(!held||g is A.TransferGroup.Rafidain or A.TransferGroup.OtherBanks||SelectedBook()!=null)actions.Children.Add(options);
        }
        group.SelectionChanged+=(_,_)=>Guard(Draw);
        Draw();
    }

    sealed record TransferRow(A.TransferEntry Entry,string Original,string Amount,string Issues);

    void LinkBook(A.BackupSnapshot s,IndividualBook book,A.TransferEntry[] rows,A.TransferEntry? selected){
        if(rows.Length==0){Status.Text="لا توجد جهات في فرع الأنبار لهذا الشهر.";return;}
        var list=new ComboBox{ItemsSource=rows,DisplayMemberPath="Title",SelectedItem=selected??book.Entry};
        if(!Form("ربط جهة بالكتاب",Text(book.Title,16,true),Labeled("جهة التنفيذ",list,350)))return;
        if(list.SelectedItem is not A.TransferEntry entity)throw new InvalidDataException("اختر جهة التنفيذ.");
        _=Working(()=>store.LinkIndividual(s,book.Id,entity),"حُفظ ربط الكتاب.",()=>ShowPage(CurrentTransfers));
    }

    void TransferAccounts(A.BackupSnapshot s,A.TransferSnapshot plan,A.TransferEntry? selected,bool frozen)
    {
        var payer=A.TransferPreparation.Payer(s);var name=new TextBox{Text=payer.Name};var iban=new TextBox{Text=payer.Iban};
        var saved=new ComboBox{ItemsSource=s.Accounts,DisplayMemberPath="Entity"};var entity=new TextBox();var branch=new TextBox();var bic=new TextBox();var beneficiary=new TextBox();var local=new TextBox();
        saved.SelectionChanged+=(_,_)=>{if(saved.SelectedItem is A.ExecutionAccount a){entity.Text=a.Entity;branch.Text=a.Branch;bic.Text=a.Swift;beneficiary.Text=a.Iban;local.Text=a.LocalAccount??"";}};
        var entries=plan.Entries;var link=new ComboBox{ItemsSource=entries,DisplayMemberPath="Title"};
        link.SelectedItem=selected;
        if(selected?.Account!=null)saved.SelectedItem=s.Accounts.FirstOrDefault(a=>a.Entity==selected.Account.Entity);
        if(!Form(frozen?"تعديل الحساب للدفعات اللاحقة · الدفعة المفصولة محفوظة":"تعديل الحساب وربط الجهة",Bar(Labeled("الجهة الدافعة",name,250),Labeled("آيبان الجهة الدافعة",iban,250)),
            FilterBar(("اختيار حساب محفوظ",saved,1),("ربط ببند أو جهة",link,1)),Bar(Labeled("اسم حساب المستفيد",entity,250),Labeled("المصرف والفرع",branch,250)),Bar(Labeled("آيبان المستفيد",beneficiary,240),Labeled("SWIFT",bic,120),Labeled("الحساب المحلي",local,145))))return;
        if(link.SelectedItem!=null&&string.IsNullOrWhiteSpace(entity.Text))throw new InvalidDataException("اختر حساباً محفوظاً أو أدخل بيانات حساب المستفيد للربط.");
        var accounts=s.Accounts.ToList();Dictionary<string,string>? links=null;
        if(entity.Text.Trim().Length>0){
            var a=new A.ExecutionAccount(entity.Text.Trim(),A.TransferPreparation.NormalizeAccount(beneficiary.Text),branch.Text.Trim(),bic.Text.Trim().ToUpperInvariant(),string.IsNullOrWhiteSpace(local.Text)?null:A.TransferPreparation.NormalizeAccount(local.Text));
            if(saved.SelectedItem is A.ExecutionAccount old&&old.Entity!=a.Entity)throw new InvalidDataException("احتفظ باسم الحساب لتعديل بياناته.");
            var group=A.TransferPreparation.Classify(a);if(group==A.TransferGroup.Unresolved||(group==A.TransferGroup.Anbar?A.TransferPreparation.LocalAccount(a)==null:!A.TransferPreparation.ValidIban(a.Iban)||!A.TransferPreparation.ValidSwift(a.Swift)))throw new InvalidDataException("راجع الفرع ورموز الحساب.");
            accounts.RemoveAll(x=>A.PayrollAnalysis.Normalize(x.Entity)==A.PayrollAnalysis.Normalize(a.Entity));accounts.Add(a);
            if(link.SelectedItem is A.TransferEntry item)links=new(){{item.Id.StartsWith("payroll|")?item.Id[8..]:"deduction|"+A.PayrollAnalysis.Normalize(item.Title),A.PayrollAnalysis.Normalize(a.Entity)}};
        }
        string who=name.Text,value=iban.Text;_=Working(()=>store.SaveAccounts(s,who,value,accounts,links),"حُفظت الحسابات.",()=>ShowPage(CurrentTransfers));
    }

    static byte[] ExecutionSheetsPdf(A.BackupSnapshot source,List<SplitPart> parts,bool held) =>
        A.ReportPdf.Generate(parts.Select(p=>SplitSheet(source,p,held)).ToList(),Fonts(),DateTime.Now);

    static byte[] SplitFundPdf(A.BackupSnapshot source,string month,SplitPart fund,bool held)
    {
        var amounts=(held?fund.Deferred:fund.Current).ToLookup(r=>A.FundReport.CategoryOf(r.Rank));
        var data=A.FundReport.Categories.Select((cat,i)=>new A.FundReport.Row(cat,amounts[i].Count(),amounts[i].Sum(r=>r.Amount))).ToList();
        return A.FundReport.Pdf(data,A.FundReport.Title(source,month),Fonts(),DateTime.Now);
    }

    void ExportGroup(A.BackupSnapshot s,string month,A.TransferSnapshot plan,A.TransferGroup g,ContractBatch? batch,bool held,DateTime day)
    {
        string marker=(held?"deferred":"current")+"|group|"+System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(g.ToString());
        if(batch!=null)day=Store.PreparedDate(s,month,marker)??day;
        var letter=A.GroupLetterPreparation.Prepare(plan,g,DateOnly.FromDateTime(day));
        var bookId=g==A.TransferGroup.Rafidain?"rafidain_group":"other_banks_group";
        var edited=EditLetter(Builtin(s,bookId),bookId,s,month,render:t=>A.GroupLetterPdf.Generate(letter,t.Style(),LetterFont(),Fonts().Bold,Logo()));
        if(edited==null)return;
        store.SaveLetterTemplate(s,bookId,edited);s=store.Snapshot(month);var style=edited.Style();
        ShowPage(held?DeferredTransfers:CurrentTransfers);
        var filename=BookTitles[bookId]+" - "+(held?DeferredTransfers:batch==null?CurrentTransfers:"الدفعة الحالية")+" - "+month;
        _=SaveFile(filename,"zip",()=>{
            var pdf=A.GroupLetterPdf.Generate(letter,style,LetterFont(),Fonts().Bold,Logo());
            var files=new Dictionary<string,byte[]>{{"كتاب التحويل.pdf",pdf},{"التحويل.csv",letter.Csv}};
            if(batch!=null){
                var parts=batch.Parts.Where(p=>p.Portion(held)?.Group==g).ToList();
                var entities=parts.Where(p=>p.Entity.PayrollRecipient==null).ToList();
                if(entities.Count>0)files["كشوف جهات التنفيذ.pdf"]=ExecutionSheetsPdf(s,entities,held);
                var fund=parts.FirstOrDefault(p=>p.Entity.PayrollRecipient=="fundDonations");
                if(fund!=null)files["كشف صندوق الشهداء.pdf"]=SplitFundPdf(s,month,fund,held);
            }
            var bytes=Package(files);
            return bytes;
        },batch==null?null:()=>store.UpdateBatch(s,month,prepared:marker,date:day),batch==null?null:()=>ShowPage(held?DeferredTransfers:CurrentTransfers));
    }
}
