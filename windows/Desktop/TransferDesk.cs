using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    readonly Dictionary<string,DateTime> transferDates=new();
    A.TransferGroup currentGroup=A.TransferGroup.Rafidain,deferredGroup=A.TransferGroup.Rafidain;

    sealed record TransferMonth(A.BackupSnapshot Source,ContractBatch? Batch,A.TransferSnapshot Plan,Lazy<Task<ContractBatch>> Proposal);
    Task<TransferMonth>? transferMonth;
    (string Revision,string Month) transferMonthKey;

    Task<TransferMonth> LoadTransferMonth(string revision,string month){
        if(transferMonthKey!=(revision,month)||transferMonth==null||transferMonth.IsFaulted){
            transferMonthKey=(revision,month);
            transferMonth=Task.Run(()=>{
                var source=store.Snapshot(month);
                if(Path.GetDirectoryName(source.SourcePath)!=revision)throw new IOException("تغيّرت نسخة الشهر أثناء الفتح.");
                var frozen=Store.ActiveBatch(source,month);
                return new TransferMonth(source,frozen,frozen?.Plan(false)??A.TransferPreparation.Prepare(source,month),
                    new Lazy<Task<ContractBatch>>(()=>Task.Run(()=>Store.PrepareBatch(source,month))));
            });
        }
        return transferMonth;
    }

    void UpdateTransferTabs(string route)
    {
        TransferTabs.Children.Clear();
        bool visible=route is CurrentTransfers or DeferredTransfers or RefundTransfers;
        TransferTabs.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        if(!visible)return;
        foreach(var (name,icon) in new[]{(CurrentTransfers,"\uE8A5"),(DeferredTransfers,"\uE823"),(RefundTransfers,"\uE8AB")}){
            var tab=ButtonFor(name,()=>{if(page!=name || subpageBack!=null)ShowPage(name);},icon,route==name);
            TransferTabs.Children.Add(tab);
        }
    }

    static Border TransferSummary(params (string Label,string Value)[] values){
        var metrics=new UniformGrid{Columns=values.Length};
        foreach(var (label,value) in values)metrics.Children.Add(Column(Text(label,13),Text(value,18,true)));
        var card=Box(metrics);card.Padding=new Thickness(12,6,12,6);return card;
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
    void MenuAction(ContextMenu menu,string title,Action action,bool enabled=true)
    {
        var item=new MenuItem{Header=title,IsEnabled=enabled};item.Click+=(_,_)=>Guard(action);menu.Items.Add(item);
    }

    void TransferDesk(A.BackupSnapshot s,string month,bool held,ContractBatch? batch,A.TransferSnapshot plan,ContractBatch? proposal,string? issue)
    {
        PageTitle.Text="التحويلات";
        var route=held?DeferredTransfers:CurrentTransfers;
        var head=Column();
        var dock=new DockPanel();
        DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);CanvasArea.Children.Add(dock);
        var split=batch??proposal;
        if(split!=null){
            var before=split.Parts.Sum(p=>p.Entity.Group.Total)+split.Guarantee;
            head.Children.Add(TransferSummary(("قيود التحويل وضمان العقود قبل الفصل",Arabic.Money(before)),
                (batch==null?"الحالية بعد الاعتماد":"الحصة الحالية",Arabic.Money(before-split.Held)),
                (batch==null?"المقترح تأجيله":"حصة العقود المؤجلة",Arabic.Money(split.Held))));
        }else head.Children.Add(TransferSummary(("مجموع قيود التحويل",Arabic.Money(plan.Entries.Sum(e=>e.Amount))),
            ("المجموعات ذات المبالغ",Arabic.Money(plan.Entries.Where(e=>e.Amount>0).Select(e=>e.Group).Distinct().Count())),
            ("الجهات والبنود ذات المبالغ",Arabic.Money(plan.Entries.Count(e=>e.Amount>0)))));
        if(held){
            void ChangeDeferral(){
                if(split==null)return;
                if(!Form(batch!=null?"إلغاء التأجيل والعودة إلى التحويل الشامل":"اعتماد فصل استقطاعات العقود",Text(month),
                    Text("استقطاعات الجهات والصندوق　"+Arabic.Money(split.Held-split.Guarantee)),Text("ضمان العقود　"+Arabic.Money(split.Guarantee))))return;
                _=Working(()=>store.UpdateBatch(s,month,proposal),"حُفظت حالة الشهر.",()=>ShowPage(batch==null?DeferredTransfers:CurrentTransfers));
            }
            var change=ButtonFor(batch==null?"تأجيل استقطاعات العقود":"إلغاء التأجيل",()=>Guard(ChangeDeferral));
            change.IsEnabled=split!=null;
            head.Children.Add(Bar(Text(batch==null?"الفصل غير معتمد":"الفصل معتمد",14,true),change));
            if(batch==null){
                if(proposal!=null)ShowSplitReview(dock,head,proposal);
                else head.Children.Add(Text(issue??"لا توجد حصة عقود قابلة للفصل."));
                return;
            }
        }
        void CheckSource(){
            if(batch!=null&&s.Months.Single(m=>m.Name==month).ImportedAt!=batch.ImportedAt)
                throw new InvalidDataException("تغير استيراد الشهر بعد اعتماد الفصل. راجع المبالغ في الدفعات المؤجلة.");
        }
        var group=Choices(["الرافدين","الرشيد والمصارف الأخرى","فرع الأنبار والكتب المنفردة","جهات تحتاج مراجعة"]);
        group.SelectedIndex=(int)(held?deferredGroup:currentGroup);
        var date=new DatePicker{SelectedDate=transferDates.GetValueOrDefault(month,DateTime.Today)};
        bool restoringDate=false;
        date.SelectedDateChanged+=(_,_)=>{if(!restoringDate && date.IsEnabled && date.SelectedDate is {} selected)transferDates[month]=selected;};
        group.MinWidth=210*(1+appearance.Level*.05);
        date.MinWidth=132*(1+appearance.Level*.05);
        var filter=FilterBar(("المجموعة",group,0),("تاريخ التحويل",date,0));
        group.Height=date.Height=36;
        var row=(Grid)filter.Child;
        var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Bottom};
        void AddCommand(Button button){
            button.Height=group.Height;button.Margin=new Thickness(3,0,3,0);button.Padding=new Thickness(8,2,8,2);
            actions.Children.Add(button);
        }
        Grid.SetColumn(actions,row.ColumnDefinitions.Count);
        row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        row.Children.Add(actions);
        head.Children.Add(new ScrollViewer{Content=filter,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        var info=Text("",13,true);var footer=Box(info);footer.Padding=new Thickness(10,5,10,5);
        DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);
        var body=new Grid();dock.Children.Add(body);
        var mainColumn=new ColumnDefinition{Width=new GridLength(2,GridUnitType.Star)};
        var sideColumn=new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)};
        body.ColumnDefinitions.Add(mainColumn);body.ColumnDefinitions.Add(sideColumn);
        body.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        body.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var detail=new StackPanel();
        var side=Box(new ScrollViewer{Content=detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        side.Padding=new Thickness(10);body.Children.Add(side);
        void Fit(){
            bool compact=body.ActualWidth<790;
            sideColumn.Width=compact?new GridLength(0):new GridLength(1,GridUnitType.Star);
            Grid.SetColumn(side,compact?0:1);Grid.SetRow(side,compact?1:0);
            side.MaxHeight=compact?Math.Min(145,Math.Max(80,body.ActualHeight*.34)):double.PositiveInfinity;
        }
        body.SizeChanged+=(_,_)=>Fit();Fit();
        if(plan.SourceWarnings.Count>0)
            head.Children.Add(new Expander{Header="ملاحظات المراجعة ("+plan.SourceWarnings.Count+")",Content=new ScrollViewer{MaxHeight=95,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=Text(string.Join("\n",plan.SourceWarnings),13)}});
        DataGrid? previousTable=null;
        var categoryBefore=batch?.Parts.GroupBy(p=>p.Entity.Category).ToDictionary(g=>g.Key,g=>g.Sum(p=>p.Entity.Group.Total));
        var originals=batch?.Parts.Select(p=>(Entry:p.Portion(held),p.Entity.Group.Total)).Where(p=>p.Entry!=null).ToDictionary(p=>p.Entry!.Id,p=>p.Total)
            ??new Dictionary<string,decimal>();
        var bank=new Lazy<A.BankSummary>(()=>A.BankStatementCalculator.Calculate(s,month));
        var items=new Lazy<Dictionary<string,A.TemplateLetterPdf.ItemSummary>>(()=>A.TemplateLetterPdf.Summary(s,month));
        var hasPayroll=s.Employees.Any(e=>e.Month==month);
        void Draw(){
            var g=(A.TransferGroup)group.SelectedIndex;
            if(held)deferredGroup=g;else currentGroup=g;
            var bankGroup=g is A.TransferGroup.Rafidain or A.TransferGroup.OtherBanks;
            var marker=(held?"deferred":"current")+"|group|"+System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(g.ToString());
            var prepared=batch!=null&&bankGroup?Store.PreparedDate(s,month,marker):null;
            restoringDate=true;
            date.SelectedDate=prepared??transferDates.GetValueOrDefault(month,DateTime.Today);
            restoringDate=false;
            date.IsEnabled=bankGroup&&prepared==null;
            var rows=plan.Entries.Where(e=>e.Group==g).ToArray();
            bool branch=g==A.TransferGroup.Anbar;
            if(previousTable!=null)body.Children.Remove(previousTable);
            var table=GridView((branch?"الكتاب / الجهة":"الجهة","Title",1.8),("مصدر المبلغ","Source",1.6),("المصرف","Entry.Account.Branch",1.35),
                ("قبل الفصل","Original",.9),("مبلغ الدفعة","Amount",.9),("المراجعة","Issues",1.45));
            if(batch==null)table.Columns.RemoveAt(3);
            if(!branch)table.Columns.RemoveAt(1);
            previousTable=table;body.Children.Add(table);
            actions.Children.Clear();

            Action<DataGridRow,PlacementMode>? openBookActions=null;
            if(branch){
                var list=BranchBooks(s,plan,batch,held).Select(book=>BookRow(book,s,month,batch,originals,hasPayroll,bank,items)).ToArray();
                table.ItemsSource=list;
                System.Windows.Automation.AutomationProperties.SetName(table,"كتب فرع الأنبار والكتب المنفردة");
                info.Text="عدد الجهات والكتب　"+list.Length+"　·　مجموع قيود التحويل　"+Arabic.Money(rows.Sum(e=>e.Amount));
                void OpenActions(DataGridRow row,PlacementMode placement){
                    if(row.Item is not TransferRow {Book:{} book} || ItemsControl.ItemsControlFromItemContainer(row)!=table)return;
                    table.SelectedItem=row.Item;
                    var menu=new ContextMenu{FlowDirection=FlowDirection.RightToLeft,PlacementTarget=row,Placement=placement};
                    if(book.Id!="entry_only"){
                        MenuAction(menu,"معاينة الكتاب",()=>{CheckSource();OpenIndividual(s,plan,batch,held,book,BookCommand.Preview);},!book.NeedsLink);
                        MenuAction(menu,"تصدير الكتاب",()=>{CheckSource();OpenIndividual(s,plan,batch,held,book,BookCommand.Export);},!book.NeedsLink);
                        if(book.Id=="split")
                            MenuAction(menu,"تعديل وتصدير كتاب هذه الدفعة",()=>{CheckSource();OpenIndividual(s,plan,batch,held,book,BookCommand.EditForExport);});
                        else MenuAction(menu,"تعديل الكتاب",()=>EditTransferTemplate(s,month,book.Id));
                    }
                    if(batch==null && LinkableBooks.Contains(book.Id))
                        MenuAction(menu,"ربط جهة بالكتاب",()=>LinkBook(s,book,rows,book.Entry));
                    if(batch==null && book.Id=="entry_only" && book.Entry is {} entry && entry.Id.StartsWith("deduction|"))
                        MenuAction(menu,"ربط الجهة بكتاب",()=>{
                            var ids=LinkableBooks.Where(id=>id!="rafidain_anbar"||A.TransferPreparation.IsDirectAnbar(entry.Title)).ToArray();
                            var choices=Choices(ids.Select(id=>BookTitles[id]));choices.SelectedIndex=-1;
                            if(Form("ربط الجهة بكتاب",Text(book.Title),Labeled("الكتاب",choices,300)) && choices.SelectedIndex>=0){
                                var id=ids[choices.SelectedIndex];
                                _=Working(()=>store.LinkIndividual(s,id,entry),"حُفظ ربط الكتاب.",()=>ShowPage(CurrentTransfers));
                            }
                        });
                    if(!held && book.Entry is {} accountEntry)
                        MenuAction(menu,batch==null?"تعديل الحساب وربط الجهة":"حساب الجهة للتحويلات اللاحقة",()=>{
                            var live=batch==null?plan:A.TransferPreparation.Prepare(s,month);
                            TransferAccounts(s,live,live.Entries.SingleOrDefault(e=>e.Id==accountEntry.Id),batch!=null);
                        });
                    if(menu.Items.Count==0)return;
                    row.ContextMenu=menu;
                    menu.Closed+=(_,_)=>{if(ReferenceEquals(row.ContextMenu,menu))row.ClearValue(ContextMenuProperty);};
                    menu.IsOpen=true;
                }
                openBookActions=OpenActions;
                TransferRow? pressed=null;
                Point pressPoint=default;
                DataGridRow? HitRow(DependencyObject? hit){
                    for(var element=hit;element!=null && element!=table;element=ElementTree.Parent(element))
                        if(element is DataGridRow row && ItemsControl.ItemsControlFromItemContainer(row)==table)return row;
                    return null;
                }
                table.AddHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler((_,e)=>{
                    if(e.ChangedButton!=MouseButton.Left)return;
                    pressPoint=e.GetPosition(table);
                    pressed=HitRow(e.OriginalSource as DependencyObject)?.Item as TransferRow;
                }),true);
                // يعالج الجدول رفع الزر أولاً كي ينهي تحديد الخلية ويفك التقاط الفأرة.
                table.AddHandler(Mouse.MouseUpEvent,new MouseButtonEventHandler((_,e)=>{
                    if(e.ChangedButton!=MouseButton.Left)return;
                    var clicked=pressed;pressed=null;
                    var point=e.GetPosition(table);
                    if(clicked==null || Math.Abs(point.X-pressPoint.X)>SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(point.Y-pressPoint.Y)>SystemParameters.MinimumVerticalDragDistance)return;
                    var hit=e.OriginalSource as DependencyObject;
                    if(hit==table)hit=table.InputHitTest(point) as DependencyObject;
                    if(HitRow(hit) is DataGridRow row && ReferenceEquals(clicked,row.Item)){
                        Dispatcher.InvokeAsync(()=>{
                            if(row.IsLoaded && ReferenceEquals(row.Item,clicked))OpenActions(row,PlacementMode.MousePoint);
                        },System.Windows.Threading.DispatcherPriority.Input);
                    }
                }),true);
                table.PreviewKeyDown+=(_,e)=>{
                    if(e.Key==Key.Enter && table.SelectedItem!=null && table.ItemContainerGenerator.ContainerFromItem(table.SelectedItem) is DataGridRow row){
                        e.Handled=true;OpenActions(row,PlacementMode.Bottom);
                    }
                };
            }else{
                table.ItemsSource=rows.Select(e=>new TransferRow(e,null,e.Title,"",originals.GetValueOrDefault(e.Id,e.Amount),e.Amount,string.Join("، ",A.TransferPreparation.Issues(e)))).ToArray();
                info.Text="عدد الجهات والبنود　"+rows.Length+"　·　مجموع المجموعة　"+Arabic.Money(rows.Sum(e=>e.Amount));
            }
            if(batch!=null){
                var category=System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(g.ToString());
                var before=(categoryBefore?.GetValueOrDefault(category)??0)+(branch?batch.Guarantee:0);
                var portion=rows.Sum(e=>e.Amount)+(branch&&held?batch.Guarantee:0);
                info.Text="قبل الفصل　"+Arabic.Money(before)+"　·　"+(held?"المؤجل":"الحالي")+"　"+Arabic.Money(portion)+
                    (branch?"　·　قيود التحويل وضمان العقود":"")+"　د.ع";
            }
            void ShowDetail(){
                detail.Children.Clear();
                if(table.SelectedItem is not TransferRow item){
                    detail.Children.Add(Text(g==A.TransferGroup.Unresolved?"لا توجد جهات تحتاج مراجعة.":"لا توجد قيود لهذه المجموعة."));return;
                }
                detail.Children.Add(Text(branch?"تفاصيل الكتاب":"تفاصيل الجهة",14));
                detail.Children.Add(Text(item.Title,17,true));
                if(openBookActions!=null){
                    var commands=ButtonFor("خيارات القيد",()=>{
                        table.ScrollIntoView(item);table.UpdateLayout();
                        if(table.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow view)openBookActions(view,PlacementMode.Bottom);
                    },"\uE712");
                    detail.Children.Add(commands);
                }
                if(item.Entry?.Account?.Branch is {Length:>0} location)detail.Children.Add(Text(location));
                if(item.Source.Length>0 && item.Source!=item.Title)detail.Children.Add(Text(item.Source));
                detail.Children.Add(Text("مبلغ الدفعة　"+item.Amount,15,true));
                if(batch!=null)detail.Children.Add(Text("قبل الفصل　"+item.Original));
                if(item.Issues.Length>0)detail.Children.Add(Text(item.Issues,13));
                if(item.Book is {} book && book.Id!="entry_only"){
                    var preview=ButtonFor("معاينة الكتاب المحدد",()=>Guard(()=>{CheckSource();OpenIndividual(s,plan,batch,held,book,BookCommand.Preview);}),"\uE890");
                    preview.IsEnabled=!book.NeedsLink;detail.Children.Add(preview);
                }else if(g is A.TransferGroup.Rafidain or A.TransferGroup.OtherBanks){
                    var id=g==A.TransferGroup.Rafidain?"rafidain_group":"other_banks_group";
                    detail.Children.Add(Text(BookTitles[id]));
                    detail.Children.Add(PreviewButton(BookTitles[id],()=>{
                        CheckSource();var day=date.SelectedDate??throw new InvalidDataException("حدد تاريخ التحويل.");
                        day=prepared??day;
                        var style=Builtin(s,id).Style();return Task.Run(()=>A.GroupLetterPdf.Generate(
                            A.GroupLetterPreparation.Prepare(plan,g,DateOnly.FromDateTime(day)),style,LetterFont(),Fonts().Bold,Logo()));
                    },caption:"معاينة كتاب المجموعة",printAndSave:batch==null));
                }
                if(!branch&&!held && item.Entry is {} selected)
                    detail.Children.Add(ButtonFor(batch==null?"تعديل الحساب وربط الجهة":"حساب الجهة للتحويلات اللاحقة",()=>Guard(()=>{
                        var live=batch==null?plan:A.TransferPreparation.Prepare(s,month);
                        TransferAccounts(s,live,live.Entries.SingleOrDefault(e=>e.Id==selected.Id),batch!=null);
                    }),"\uE71B"));
            }
            table.SelectionChanged+=(_,_)=>ShowDetail();table.SelectedIndex=0;ShowDetail();
            if(!branch && g!=A.TransferGroup.Unresolved){
                var export=ButtonFor("الكتاب وملف CSV",()=>Guard(()=>{
                    CheckSource();ExportGroup(s,month,plan,g,batch,held,date.SelectedDate??throw new InvalidDataException("حدد تاريخ التحويل."));
                }),"\uE74E",true);
                export.IsEnabled=rows.Length>0;AddCommand(export);
            }
            if(batch!=null && g!=A.TransferGroup.Unresolved){
                var parts=batch.Parts.Where(p=>p.Portion(held)?.Group==g).ToList();
                var entities=parts.Where(p=>p.Entity.PayrollRecipient==null).ToList();
                if(entities.Count>0){
                    AddCommand(PreviewButton("كشوف جهات التنفيذ - "+month,()=>{
                        CheckSource();
                        return Task.Run(()=>ExecutionSheetsPdf(s,entities,held));
                    },caption:"معاينة كشوف جهات التنفيذ"));
                }
                if(parts.FirstOrDefault(p=>p.Entity.PayrollRecipient=="fundDonations") is {} fund)
                    AddCommand(PreviewButton("كشف صندوق الشهداء - "+month,()=>{
                        CheckSource();
                        return Task.Run(()=>SplitFundPdf(s,month,fund,held));
                    },caption:"معاينة كشف الصندوق"));
            }
            if(branch)return;
            var options=OptionsButton(menu=>{
                if(g is A.TransferGroup.Rafidain or A.TransferGroup.OtherBanks)
                    MenuAction(menu,"تعديل كتاب المجموعة",()=>EditTransferTemplate(s,month,g==A.TransferGroup.Rafidain?"rafidain_group":"other_banks_group"));
                if(!held)MenuAction(menu,batch==null?"تعديل الحساب وربط الجهة":"حسابات وربط التحويلات اللاحقة",()=>{
                    var live=batch==null?plan:A.TransferPreparation.Prepare(s,month);
                    var selected=(table.SelectedItem as TransferRow)?.Entry;
                    TransferAccounts(s,live,live.Entries.FirstOrDefault(e=>e.Id==selected?.Id),batch!=null);
                });
                if(!held)MenuAction(menu,"الكتب المخصصة",()=>TemplatesDesk(s,month));
            });
            if(!held||g is A.TransferGroup.Rafidain or A.TransferGroup.OtherBanks)AddCommand(options);
        }
        group.SelectionChanged+=(_,_)=>Guard(Draw);
        Draw();
    }

    void ShowSplitReview(DockPanel dock,StackPanel head,ContractBatch proposal){
        head.Children.Add(Text("الكتب المتأثرة بفصل العقود",16,true));
        var impacts=ContractLetterImpact.For(proposal);
        var letters=GridView(("الكتاب","Title",1.7),("قبل الفصل","Original",1),("المؤجل","Held",1),("بعد الفصل","Current",1));
        var impactRows=impacts.Select(i=>new{Impact=i,i.Title,Original=Arabic.Money(i.Before),Held=Arabic.Money(i.Deferred),Current=Arabic.Money(i.Current)}).ToArray();
        letters.ItemsSource=impactRows;
        System.Windows.Automation.AutomationProperties.SetName(letters,"الكتب المتأثرة بفصل العقود");
        var recipients=GridView(("الجهة أو البند","Title",2),("حصتها المؤجلة","Value",1));
        var detail=new Expander{Header="جهات الكتاب المحدد",Content=recipients};
        recipients.MaxHeight=140;
        letters.SelectionChanged+=(_,_)=>{
            if(letters.SelectedItem==null)return;
            var selected=impactRows.First(r=>ReferenceEquals(r,letters.SelectedItem)).Impact;
            recipients.ItemsSource=selected.Recipients.Select(e=>new{e.Title,Value=Arabic.Money(e.Amount)}).ToList();
            detail.Visibility=selected.Recipients.Count==0?Visibility.Collapsed:Visibility.Visible;
        };
        letters.SelectedIndex=0;DockPanel.SetDock(detail,Dock.Bottom);dock.Children.Add(detail);dock.Children.Add(letters);
    }

    sealed record TransferRow(A.TransferEntry? Entry,IndividualBook? Book,string Title,string Source,decimal? Before,decimal? Value,string Issues)
    {
        public string Original=>Before.HasValue?Arabic.Money(Before.Value):"—";
        public string Amount=>Value.HasValue?Arabic.Money(Value.Value):"—";
    }

    void LinkBook(A.BackupSnapshot s,IndividualBook book,A.TransferEntry[] rows,A.TransferEntry? selected){
        var candidates=rows.Where(e=>e.Id.StartsWith("deduction|") && (book.Id!="rafidain_anbar" || A.TransferPreparation.IsDirectAnbar(e.Title))).ToArray();
        if(candidates.Length==0){Status.Text="لا توجد جهة استقطاع مناسبة لهذا الكتاب في الشهر المختار.";return;}
        var list=new ComboBox{ItemsSource=candidates,DisplayMemberPath="Title",SelectedItem=selected??book.Entry};
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
            var a=AccountDirectory.Account(entity.Text,branch.Text,beneficiary.Text,bic.Text,local.Text);
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
