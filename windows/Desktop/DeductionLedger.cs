using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    CancellationTokenSource? query;
    Button? export;

    Filters Current()=>new(Search:search?.Text??"",Agency:agency?.SelectedIndex>0?(string)agency.SelectedItem:"",
        Year:int.TryParse(year?.SelectedItem?.ToString(),out int y)?y:0,Month:Math.Max(0,monthFilter?.SelectedIndex??0));

    void DeductionGrid(Review? review)
    {
        offset=0;
        var today=DateTime.Today;
        // رابط إشعار الجهة يفتح قيد الإشعار نفسه، والدخول العادي يبدأ دائماً بتاريخ الحاسبة.
        var key=review==null?today.Year*100+today.Month:Arabic.Period(review.Month);
        var years=months.Where(m=>m.Key>0).Select(m=>m.Key/100).Append(today.Year).Distinct().OrderDescending();
        var dock=new DockPanel();
        search=new TextBox{ToolTip="ابحث بالاسم أو الرتبة أو الجهة أو الإضبارة أو الشهر أو المبلغ أو الحالة"};
        agency=Choices(new[]{"كل الجهات"}.Concat(agencies));
        year=Choices(new[]{"كل السنوات"}.Concat(years.Select(y=>y.ToString())));
        monthFilter=Choices(new[]{"كل الأشهر"}.Concat(Arabic.Months));
        if(key>0){year.SelectedItem=(key/100).ToString();monthFilter.SelectedIndex=key%100;}
        if(review!=null)agency.SelectedItem=review.Agency;

        export=ButtonFor("معاينة كشف الجهات",()=>_=EntityReport(),"\uE890",true);
        export.IsEnabled=false;
        var top=FilterBar(("بحث في جميع الحقول",search,1.25),("جهة التنفيذ",agency,1),("السنة",year,0),("الشهر",monthFilter,0),("",export,0));
        DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var gridView=GridView(("اسم المنتسب","Name",1.5),("الرتبة","Rank",.65),("جهة التنفيذ","Agency",1.3),
            ("رقم وتاريخ الإضبارة","Reference",1.5),("الشهر","Month",1),("المبلغ · د.ع","Value",.9));
        grid=gridView;grid.CanUserSortColumns=false;
        var cellText=new Style(typeof(TextBlock));
        cellText.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));
        cellText.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(5,4,5,4)));
        foreach(DataGridTextColumn col in grid.Columns)col.ElementStyle=cellText;
        grid.RowHeight=double.NaN;grid.MinRowHeight=32;
        BindLedgerActions(gridView);
        var rename=new MenuItem{Header="توحيد تسمية جهة التنفيذ"};
        rename.Click+=(_,_)=>Guard(()=>RenameAgency());
        agency.ContextMenu=new ContextMenu{Items={rename}};
        previous=HomeAction("السابق",()=>{offset=Math.Max(0,offset-PageSize);_=LoadGrid();});
        next=HomeAction("التالي",()=>{offset+=PageSize;_=LoadGrid();});
        previous.IsEnabled=next.IsEnabled=false;
        footer=Text("",12);
        var pager=new StackPanel{Orientation=Orientation.Horizontal};
        pager.Children.Add(previous);pager.Children.Add(next);
        var band=new DockPanel();DockPanel.SetDock(pager,Dock.Right);band.Children.Add(pager);band.Children.Add(footer);
        var bottom=Box(band,"SolidBackgroundFillColorTertiaryBrush");bottom.Padding=new Thickness(8,2,8,2);
        DockPanel.SetDock(bottom,Dock.Bottom);dock.Children.Add(bottom);
        dock.Children.Add(gridView);CanvasArea.Children.Add(dock);
        void Changed(){
            query?.Cancel();generation++;offset=0;
            export.IsEnabled=previous.IsEnabled=next.IsEnabled=false;
            gridView.ItemsSource=null;footer.Text="جارٍ تحميل القيود…";
            debounce.Stop();debounce.Start();
        }
        search.TextChanged+=(_,_)=>Changed();
        foreach(var c in new[]{agency,year,monthFilter})c.SelectionChanged+=(_,_)=>Changed();
        _=LoadGrid();
    }

    [DllImport("user32.dll")]
    static extern uint GetDoubleClickTime();

    void BindLedgerActions(DataGrid ledger)
    {
        DataGridCell? Cell(object? source){
            for(var node=source as DependencyObject;node!=null && node!=ledger;node=ElementTree.Parent(node))
                if(node is DataGridCell cell && cell.DataContext is Deduction)return cell;
            return null;
        }
        void Menu(DataGridCell cell){
            if(cell.DataContext is not Deduction row)return;
            ledger.SelectedItem=row;
            var menu=new ContextMenu{PlacementTarget=cell,Placement=PlacementMode.Bottom,FlowDirection=FlowDirection.RightToLeft};
            void Add(string title,Action action){
                var item=new MenuItem{Header=title};item.Click+=(_,_)=>Guard(action);menu.Items.Add(item);
            }
            if(cell.Column==ledger.Columns[0])Add("كشف استقطاع منتسب",()=>OpenEmployee(row));
            if(cell.Column==ledger.Columns[2])Add("توحيد تسمية جهة التنفيذ",()=>RenameAgency(row.Agency));
            if(cell.Content is TextBlock text)Add("نسخ قيمة الحقل",()=>Clipboard.SetText(text.Text));
            menu.IsOpen=true;
        }
        // ننتظر مهلة النقر المزدوج الخاصة بويندوز حتى لا تحجب القائمة النقرة الثانية.
        var singleClick=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(GetDoubleClickTime())};
        DataGridCell? pendingCell=null;
        Deduction? pendingRow=null;
        singleClick.Tick+=(_,_)=>{
            singleClick.Stop();
            if(pendingCell is {IsLoaded:true,IsMouseOver:true} cell && ReferenceEquals(cell.DataContext,pendingRow) && ledger.IsKeyboardFocusWithin)
                Menu(cell);
        };
        ledger.Unloaded+=(_,_)=>singleClick.Stop();
        ledger.ContextMenuOpening+=(_,e)=>{
            singleClick.Stop();
            var cell=Cell(e.OriginalSource);
            if(cell!=null && (cell.Column==ledger.Columns[0]||cell.Column==ledger.Columns[2])){e.Handled=true;Menu(cell);}
        };
        ledger.PreviewMouseLeftButtonDown+=(_,e)=>{
            singleClick.Stop();
            var cell=Cell(e.OriginalSource);
            if(cell?.Column!=ledger.Columns[0] || cell.DataContext is not Deduction row)return;
            if(e.ClickCount==2){e.Handled=true;Guard(()=>OpenEmployee(row));}
            else if(e.ClickCount==1){pendingCell=cell;pendingRow=row;singleClick.Start();}
        };
        ledger.PreviewKeyDown+=(_,e)=>{
            if(e.Key==Key.Enter && ledger.CurrentColumn==ledger.Columns[0] && ledger.CurrentItem is Deduction row){
                e.Handled=true;Guard(()=>OpenEmployee(row));
            }
        };
    }

    async Task LoadGrid()
    {
        if(grid==null)return;
        query?.Cancel();using var current=new CancellationTokenSource();query=current;
        int version=++generation,skip=offset;var f=Current();
        try{
            previous!.IsEnabled=next!.IsEnabled=false;
            var result=await Task.Run(()=>{
                var total=store.DeductionTotals(f,token:current.Token);
                current.Token.ThrowIfCancellationRequested();
                return (total.Count,total.Total,total.People,Rows:store.Find(f,PageSize,skip).ToList());
            });
            if(version!=generation||current.IsCancellationRequested)return;
            grid.ItemsSource=result.Rows;
            footer!.Text=$"مجموع المبالغ  {Arabic.Money(result.Total)} د.ع　·　عدد المنتسبين  {Arabic.Money(result.People)}　·　عدد القيود  {Arabic.Money(result.Count)}";
            if(result.Count>PageSize)footer.Text+=$"　·　{skip+1}–{skip+result.Rows.Count}";
            if(result.Count==0)footer.Text="لا توجد قيود　·　"+footer.Text;
            previous.Visibility=next.Visibility=result.Count>PageSize?Visibility.Visible:Visibility.Collapsed;
            previous.IsEnabled=skip>0;next.IsEnabled=skip+PageSize<result.Count;export!.IsEnabled=result.Count>0;
        }catch(OperationCanceledException){}catch(Exception ex){if(version==generation)Error(ex);}
        finally{if(query==current)query=null;}
    }

    string ReportHeader()=>store.Metadata("header") is {Length:>0} h?h:"قيادة شرطة محافظة الانبار";
    async Task EntityReport()
    {
        var f=Current();
        try{var report=await Task.Run(()=>Statement.Entities(store.Find(f),ReportHeader(),true));new ReportWindow(report){Owner=this}.ShowDialog();}
        catch(Exception ex){Error(ex);}
    }

    void OpenEmployee(Deduction row,Window? owner=null,IReadOnlyList<Deduction>? loaded=null)
    {
        var records=loaded??store.Find(new(Person:row.Name)).ToList();
        var entities=Choices(records.Select(r=>r.Agency).Distinct());
        entities.SelectedItem=row.Agency;
        var refs=Choices([]);var start=Choices([]);var end=Choices([]);
        var dialog=new Window{Owner=owner??this,Title="كشف استقطاع منتسب",FlowDirection=FlowDirection.RightToLeft,Width=Math.Min(800,SystemParameters.WorkArea.Width*.94),
            SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var preview=ButtonFor("معاينة الكشف",()=>dialog.DialogResult=true,"\uE890",true);
        string Reference()=>refs.SelectedIndex>0?(string)refs.SelectedItem:"";
        void SetPeriods(){
            var keys=Statement.EmployeeRows(records,row.Name,entities.SelectedItem as string??"",Reference())
                .Select(r=>r.Period).Where(k=>k>0).ToHashSet();
            var periods=months.Where(m=>keys.Contains(m.Key)).DistinctBy(m=>m.Key).OrderBy(m=>m.Key).ToList();
            start.ItemsSource=end.ItemsSource=periods;
            start.SelectedIndex=periods.Count>0?0:-1;end.SelectedIndex=periods.Count-1;
            preview.IsEnabled=periods.Count>0;
        }
        void SetRefs(string initial){
            var options=records.Where(r=>r.Agency==(string?)entities.SelectedItem && r.Reference.Length>0)
                .Select(r=>r.Reference).DistinctBy(Arabic.CaseKey).Order().ToList();
            refs.ItemsSource=new[]{"كل الإضبارات"}.Concat(options).ToList();
            refs.SelectedIndex=options.FindIndex(r=>Arabic.CaseKey(r)==Arabic.CaseKey(initial))+1;
            refs.IsEnabled=options.Count>0;SetPeriods();
        }
        SetRefs(row.Reference);
        entities.SelectionChanged+=(_,_)=>SetRefs("");
        refs.SelectionChanged+=(_,_)=>SetPeriods();
        start.SelectionChanged+=(_,_)=>{if(start.SelectedItem is MonthInfo a && end.SelectedItem is MonthInfo b && a.Key>b.Key)end.SelectedItem=a;};
        end.SelectionChanged+=(_,_)=>{if(start.SelectedItem is MonthInfo a && end.SelectedItem is MonthInfo b && b.Key<a.Key)start.SelectedItem=b;};
        dialog.Content=Box(Column(Text(row.Name,20,true),FilterBar(("جهة الاستقطاع",entities,1.25),("رقم وتاريخ الإضبارة",refs,1.4),
            ("من شهر",start,1),("إلى شهر",end,1)),preview));
        if(dialog.ShowDialog()!=true || start.SelectedItem is not MonthInfo first || end.SelectedItem is not MonthInfo last)return;
        var report=store.PersonStatement(row.Name,(string)entities.SelectedItem,Reference(),first.Key,last.Key,ReportHeader());
        new ReportWindow(report){Owner=owner??this}.ShowDialog();
    }
}
