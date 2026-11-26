using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AnbarPayroll.Desktop;

public partial class MainWindow : Window
{
    private readonly Store store;
    private bool initializing=true,busy;
    private string page="الرئيسية";
    private List<MonthInfo> months=[];
    private List<string> agencies=[];
    private MonthInfo? SelectedMonth=>Period.SelectedItem as MonthInfo;
    private static readonly (string Name,string Icon)[] Sections=[("الرئيسية","\uE80F"),("التحويلات","\uE8AB"),("كشف البنك","\uE825"),("الإعدادات","\uE713")];
    private static readonly (string Name,string Desc,string Icon)[] Cards=[
        ("جهات التنفيذ","القيود والمبالغ حسب الجهة","\uEC06"),("كشف استقطاعات منتسب","الجهة والإضبارة والفترة","\uE77B"),
        ("بحث الاستقطاعات","البحث في جميع الأشهر","\uE721"),("كشف البنك","فترات المطابقة المحفوظة","\uE825"),("الكتب المحفوظة","ملفات PDF الأصلية من الآيفون","\uE8B7"),
        (TransfersName,"كتب المصارف ودفعات الشهر","\uE8AB"),(AuditName,"حالات المراجعة والتغييرات","\uEA8F"),
        (TrustName,"المنتسبون وحالات الصرف","\uE77B"),(FundingName,"تفصيل المبالغ ومطابقتها","\uE825"),
        (CommandName,"استقطاعات القيادة للشهر","\uE8A5"),(HealthName,"المشتركون والمبالغ غير المنتظمة","\uE95E"),(FundName,"الاستقطاعات حسب الفئة","\uE8A5")];
    private List<string> pins=Cards.Take(4).Select(x=>x.Name).ToList();
    private readonly DispatcherTimer debounce=new(){Interval=TimeSpan.FromMilliseconds(220)};
    private int generation,offset;private const int PageSize=150;
    private TextBox? search,reference;private ComboBox? agency,year,monthFilter,category;
    private DataGrid? grid;private TextBlock? footer;private Button? next,previous;
    private string? backPage;
    public MainWindow(Store source)
    {
        InitializeComponent();store=source;
        try{var path=Path.Combine(store.Root,"pins.json");if(File.Exists(path))pins=(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))??pins).Where(n=>Cards.Any(c=>c.Name==n)).Distinct().ToList();}
        catch(IOException){}catch(JsonException){}
        debounce.Tick+=async (_,_)=>{debounce.Stop();await LoadGrid();};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.K&&Keyboard.Modifiers==ModifierKeys.Control&&!busy){ShowPage(SearchName);e.Handled=true;}};
        Loaded+=(_,_)=>Guard(()=>{store.UpgradeIndex();Reload();initializing=false;ShowPage("الرئيسية");});
    }
    void Guard(Action work){try{work();}catch(Exception e){Error(e);}}
    void Error(Exception e){Status.Text="تعذّر إكمال العملية. "+e.Message;MessageBox.Show(this,e.Message,"قسم الحسابات",MessageBoxButton.OK,MessageBoxImage.Warning);}
    void Reload(){initializing=true;months=store.Months();agencies=store.Agencies();Period.ItemsSource=months;Period.SelectedIndex=months.Count>0?0:-1;initializing=false;}
    static Brush BrushOf(string c)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
    static TextBlock Text(string s,double size=15,bool bold=false)=>new(){Text=s,FontSize=size,FontWeight=bold?FontWeights.Bold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    static TextBlock Glyph(string s,string color="#29644E")=>new(){Text=s,FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),FontSize=20,Foreground=BrushOf(color),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,6,0)};
    static Button ButtonFor(string title,Action click,string glyph="",bool primary=false)
    {
        var label=new StackPanel{Orientation=Orientation.Horizontal};if(glyph!="")label.Children.Add(Glyph(glyph,primary?"#FFFFFF":"#38735A"));label.Children.Add(Text(title,14,true));
        var b=new Button{Content=label,Margin=new Thickness(3),HorizontalContentAlignment=HorizontalAlignment.Center};if(primary){b.Background=BrushOf("#1B5945");b.Foreground=Brushes.White;}
        AutomationProperties.SetName(b,title);b.Click+=(_,_)=>click();return b;
    }
    static Border Box(UIElement content,string bg="#FFFFFF")=>new(){Child=content,Background=BrushOf(bg),Padding=new Thickness(17),BorderBrush=BrushOf("#D2DED5"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Margin=new Thickness(4)};
    static WrapPanel Bar(params UIElement[] items){var b=new WrapPanel();foreach(var item in items)b.Children.Add(item);return b;}
    static FrameworkElement Labeled(string title,FrameworkElement input,double width=160){var p=new StackPanel{Width=width,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(4)};p.Children.Add(Text(title,12,true));input.Margin=new Thickness(0,4,0,0);AutomationProperties.SetName(input,title);p.Children.Add(input);return p;}
    static ComboBox Choices(IEnumerable<string> values){var c=new ComboBox{ItemsSource=values.ToList(),SelectedIndex=0};return c;}
    static StackPanel Column(params UIElement[] items){var p=new StackPanel();foreach(var i in items)p.Children.Add(i);return p;}
    static DataGrid GridView(params (string Title,string Field,double Width)[] cols)
    {
        var g=new DataGrid();foreach(var c in cols)g.Columns.Add(new DataGridTextColumn{Header=c.Title,Binding=new Binding(c.Field),Width=new DataGridLength(c.Width,DataGridLengthUnitType.Star),MinWidth=65});return g;
    }
    void PeriodChanged(object s,SelectionChangedEventArgs e){if(!initializing&&!busy)ShowPage(page);}
    void SearchClick(object s,RoutedEventArgs e){if(!busy)ShowPage(SearchName);}
    void ShowPage(string name,Review? review=null)
    {
        if(busy)return;query?.Cancel();generation++;debounce.Stop();page=name;PageTitle.Text=name;Nav.Children.Clear();CanvasArea.Children.Clear();grid=null;
        string active=name is "جهات التنفيذ" or "بحث الاستقطاعات" or "الكتب المحفوظة"?"التحويلات":name;
        foreach(var n in Sections){var btn=ButtonFor(n.Name,()=>{backPage=null;ShowPage(n.Name);},n.Icon);btn.Background=BrushOf(active==n.Name?"#D8EEE0":"#19483D");btn.Foreground=active==n.Name?BrushOf("#163E31"):Brushes.White;
            ((TextBlock)((StackPanel)btn.Content).Children[0]).Foreground=active==n.Name?BrushOf("#215740"):BrushOf("#CADFD2");btn.BorderThickness=new Thickness(0);btn.HorizontalContentAlignment=HorizontalAlignment.Right;btn.Margin=new Thickness(0,4,0,2);btn.Padding=new Thickness(12,11,12,11);Nav.Children.Add(btn);}
        Status.Text=store.Active==null?"اختر نسخة الآيفون من الإعدادات لبدء العمل.":"تُعرض البيانات من النسخة المحفوظة محلياً";
        if(name=="الرئيسية")Home();else if(name=="الإعدادات")Settings();else if(name=="كشف البنك")Bank();else if(WorkPages.Contains(name))WorkPage(name);else if(name=="الكتب المحفوظة")Books();else DeductionGrid(name=="بحث الاستقطاعات",review);
        if(SystemParameters.ClientAreaAnimation&&!SystemParameters.HighContrast){CanvasArea.BeginAnimation(OpacityProperty,new DoubleAnimation(.84,1,TimeSpan.FromMilliseconds(140)){FillBehavior=FillBehavior.Stop,EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});}
    }
    void Activate(string name)
    {
        backPage="الرئيسية";
        if(name=="كشف استقطاعات منتسب"){ShowPage("بحث الاستقطاعات");Status.Text="ابحث عن المنتسب، وحدّد قيده ثم افتح كشف المنتسب.";search?.Focus();}else ShowPage(name);
    }
    async void Home()
    {
        var panel=new StackPanel();
        var selectedPeriod=SelectedMonth;
        var hero=new Grid();hero.ColumnDefinitions.Add(new());hero.ColumnDefinitions.Add(new());
        var intro=Column(Text("الاستقطاعات الشهرية",26,true),Text(selectedPeriod?.Name??"استعادة أول نسخة من الآيفون",16));
        var balance=Text("—",30,true);var totalCaption=Text("إجمالي المبالغ المسجلة · دينار عراقي",13);
        var value=Column(balance,totalCaption);value.HorizontalAlignment=HorizontalAlignment.Left;Grid.SetColumn(value,1);
        foreach(var t in intro.Children.OfType<TextBlock>().Concat(value.Children.OfType<TextBlock>()))t.Foreground=Brushes.White;
        hero.Children.Add(intro);hero.Children.Add(value);var banner=Box(hero,"#1E5646");banner.Padding=new Thickness(25,24,25,24);panel.Children.Add(banner);
        if(store.Active==null)panel.Children.Add(ButtonFor("اختيار نسخة الرواتب.zip",()=>_=Import(),"\uE8E5",true));
        var pair=new Grid();pair.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});pair.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        var reviews=Column(Bar(Glyph("\uEA8F","#8E6126"),Text("حالات المراجعة",19,true)));
        var alerts=SelectedMonth is {} selected?store.Reviews(selected):[];
        if(alerts.Count==0)reviews.Children.Add(Text(SelectedMonth==null?"تظهر حالات المراجعة بعد استعادة البيانات.":!SelectedMonth.Verified||!months.Any(m=>m.Key<SelectedMonth.Key&&m.Verified)?"المقارنة تتطلب شهرين متحققين من الاستقطاعات.":"لا توجد جهات جديدة أو غائبة في المقارنة.",14));
        foreach(var r in alerts){var button=ButtonFor(r.Title+" · "+r.Agency,()=>{backPage="الرئيسية";ShowPage("جهات التنفيذ",r);});
            var hint=Text(r.Title,12);hint.Foreground=BrushOf("#775125");hint.Margin=new Thickness(0,0,0,3);
            button.Content=Bar(Glyph("\uE7BA","#94692D"),Column(hint,Text(r.Agency,15,true)));button.Background=BrushOf("#FCF8EF");
            button.HorizontalContentAlignment=HorizontalAlignment.Right;button.Margin=new Thickness(0,8,0,0);reviews.Children.Add(button);}
        reviews.Children.Add(ButtonFor("مراجعة تغيّرات الرواتب",()=>Activate(AuditName),"\uEA8F"));
        var left=Column(Bar(Text("كشوفي المثبتة",19,true),ButtonFor("تخصيص",EditPins,"\uE713")));
        var tiles=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};foreach(var pin in pins){var c=Cards.First(x=>x.Name==pin);
            var button=ButtonFor(c.Name,()=>Activate(c.Name));button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            button.Padding=new Thickness(13);button.MinHeight=100;button.Margin=new Thickness(4,7,4,0);
            var accent=c.Name switch{"جهات التنفيذ"=>("#E6F1E9","#245C43"),"كشف استقطاعات منتسب"=>("#E7EDF5","#385E82"),"بحث الاستقطاعات"=>("#F2EDE1","#765E31"),_=>("#EEEBF5","#625178")};
            var caption=Text(c.Desc,12);caption.Foreground=BrushOf("#50635B");caption.Margin=new Thickness(0,4,0,0);
            var glyph=Glyph(c.Icon,accent.Item2);glyph.Margin=new Thickness(0);glyph.HorizontalAlignment=HorizontalAlignment.Center;
            var symbol=new Border{Width=32,Height=32,CornerRadius=new CornerRadius(8),Background=BrushOf(accent.Item1),Child=glyph};
            var row=new Grid();row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});symbol.HorizontalAlignment=HorizontalAlignment.Right;row.Children.Add(symbol);
            var arrow=Glyph("\uE76B","#779385");arrow.FontSize=12;Grid.SetColumn(arrow,1);row.Children.Add(arrow);
            var name=Text(c.Name,15,true);name.Margin=new Thickness(0,8,0,0);button.Content=Column(row,name,caption);
            var shift=new TranslateTransform();button.RenderTransform=shift;
            void Lift(double y){
                if(!SystemParameters.ClientAreaAnimation||SystemParameters.HighContrast){shift.BeginAnimation(TranslateTransform.YProperty,null);shift.Y=0;return;}
                shift.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(y,TimeSpan.FromMilliseconds(110)){EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});
            }
            button.MouseEnter+=(_,_)=>Lift(-2);button.MouseLeave+=(_,_)=>Lift(0);
            tiles.Children.Add(button);}
        left.Children.Add(tiles);var leftBox=Box(left);Grid.SetColumn(leftBox,1);pair.Children.Add(Box(reviews));pair.Children.Add(leftBox);panel.Children.Add(pair);
        var library=Bar();foreach(var key in WorkPages.Where(k=>k!=SearchName))library.Children.Add(ButtonFor(key,()=>Activate(key)));
        panel.Children.Add(Box(Column(Text("دليل الكشوف",18,true),library)));
        var recent=Column(Text("الأشهر المحفوظة",18,true));
        foreach(var m in months.Take(4)){var line=Bar(ButtonFor(m.Name,()=>{initializing=true;Period.SelectedItem=m;initializing=false;Activate("جهات التنفيذ");},"\uE787"),
            Text(Arabic.Money(m.Actual)+" قيد استقطاع",14));recent.Children.Add(line);}
        if(months.Count>0)panel.Children.Add(Box(recent));
        CanvasArea.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        if(selectedPeriod is {Key:>0})try{
            var sum=await Task.Run(()=>store.Summary(new(Year:selectedPeriod.Key/100,Month:selectedPeriod.Key%100)));
            balance.Text=Arabic.Money(sum.Total);
        }catch(Exception e){Status.Text=e.Message;}
    }
    void EditPins()
    {
        var pending=pins.ToList();var body=new StackPanel{Margin=new Thickness(15)};
        var dialog=new Window{Owner=this,Title="تخصيص كشوفي المثبتة",Content=body,SizeToContent=SizeToContent.WidthAndHeight,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        void Draw(){body.Children.Clear();foreach(var name in pending.Concat(Cards.Select(c=>c.Name).Except(pending)).ToArray()){
            var check=new CheckBox{Content=name,IsChecked=pending.Contains(name),Width=200,VerticalAlignment=VerticalAlignment.Center};
            check.Click+=(_,_)=>{if(check.IsChecked==true)pending.Add(name);else pending.Remove(name);Draw();};
            var up=ButtonFor("↑",()=>{var i=pending.IndexOf(name);(pending[i-1],pending[i])=(pending[i],pending[i-1]);Draw();});up.IsEnabled=pending.IndexOf(name)>0;
            var down=ButtonFor("↓",()=>{var i=pending.IndexOf(name);(pending[i+1],pending[i])=(pending[i],pending[i+1]);Draw();});down.IsEnabled=pending.Contains(name)&&pending.IndexOf(name)<pending.Count-1;body.Children.Add(Bar(check,up,down));}
            body.Children.Add(ButtonFor("حفظ التخصيص",()=>dialog.DialogResult=true,"\uE74E",true));}
        Draw();if(dialog.ShowDialog()==true)Guard(()=>{Directory.CreateDirectory(store.Root);var path=Path.Combine(store.Root,"pins.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(pending));File.Move(path+".tmp",path,true);pins=pending;ShowPage(page);});
    }
    void Settings()
    {
        var p=Column(Text("البيانات والنسخ الاحتياطي",23,true),Text("تُفتح النسخة من الحاسبة أو من مجلد iCloud Drive بعد تنزيل الملف.",14));
        p.Children.Add(Box(Column(Text("نسخة الآيفون",18,true),Text(store.Active==null?"لم تُفتح نسخة بعد.":"صيغة "+store.Metadata("format")+"　·　"+months.Count+" فترات　·　"+Arabic.Money(new FileInfo(store.Archive).Length)+" بايت"),
            Bar(ButtonFor("فتح نسخة الآيفون",()=>_=Import(),"\uE8E5",true),ButtonFor("حفظ نسخة مطابقة",CopyArchive,"\uE74E")))));
        if(store.Active is {} active){
            long archive=new FileInfo(store.Archive).Length,lookup=new FileInfo(Path.Combine(active,"lookup.db")).Length;
            static string Size(long size)=>size>=1048576?(size/1048576d).ToString("N1")+" م.ب":Math.Ceiling(size/1024d).ToString("N0")+" ك.ب";
            var sizes=new Grid();
            sizes.ColumnDefinitions.Add(new(){Width=new(archive,GridUnitType.Star)});sizes.ColumnDefinitions.Add(new(){Width=new(lookup,GridUnitType.Star)});
            var original=new Border{Height=7,Background=BrushOf("#33785C")};var index=new Border{Height=7,Background=BrushOf("#7494AD")};Grid.SetColumn(index,1);sizes.Children.Add(original);sizes.Children.Add(index);
            var legend=Bar(Glyph("\uE8B7","#33785C"),Text("أرشيف الآيفون　"+Size(archive)),Glyph("\uE721","#486D8A"),Text("فهرس البحث المحلي　"+Size(lookup)));
            var heading=Text("المساحة على الحاسبة",18,true);heading.Margin=new Thickness(0,0,0,12);legend.Margin=new Thickness(0,10,0,4);
            p.Children.Add(Box(Column(heading,sizes,legend,Text("عند الحفظ ينتقل أرشيف الآيفون وحده. يُعاد إنشاء فهرس البحث على الحاسبة عند فتح النسخة.",14))));
        }
        p.Children.Add(ButtonFor("استيراد رواتب أو استقطاعات من Excel",()=>_=ImportExcel(),"\uE8B5"));
        CanvasArea.Children.Add(p);
    }
    void CopyArchive()=>Guard(()=>{if(store.Active==null)return;var d=new SaveFileDialog{FileName=Store.BackupName,Filter="نسخة الآيفون|*.zip"};if(d.ShowDialog(this)!=true)return;var temp=d.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.Copy(store.Archive,temp);File.Move(temp,d.FileName,true);}finally{if(File.Exists(temp))File.Delete(temp);}Status.Text="حُفظ الأرشيف الأصلي كاملاً.";});
    async Task Import()
    {
        if(busy)return;
        var d=new OpenFileDialog{Filter="نسخة الآيفون (*.zip)|*.zip",Title="فتح نسخة الرواتب"};if(d.ShowDialog(this)!=true)return;
        if(store.Active!=null&&MessageBox.Show(this,"عرض بيانات النسخة المختارة مكان النسخة المحلية الحالية؟","فتح نسخة الآيفون",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        busy=true;IsEnabled=false;try{var progress=new Progress<string>(s=>Status.Text=s);await Task.Run(()=>store.Import(d.FileName,progress));Reload();busy=false;ShowPage("الرئيسية");Status.Text="اكتمل التحقق وفتح النسخة.";}catch(Exception e){Error(e);}finally{busy=false;IsEnabled=true;}
    }
    CancellationTokenSource? query;
    Button? export,employee;
    Filters Current()=>new(search?.Text??"",agency?.SelectedIndex>0?agency.SelectedItem.ToString()!:"",
        reference?.Text??"",int.TryParse(year?.SelectedItem?.ToString(),out int y)?y:0,Math.Max(0,monthFilter?.SelectedIndex??0),Category:category?.SelectedIndex>0?(string)category.SelectedItem:"");
    void DeductionGrid(bool history,Review? review)
    {
        offset=0;
        var dock=new DockPanel();
        search=new TextBox();reference=new TextBox();
        agency=Choices(new[]{"كل الجهات"}.Concat(agencies));
        year=Choices(new[]{"كل السنوات"}.Concat(months.Where(m=>m.Key>0).Select(m=>(m.Key/100).ToString()).Distinct()));
        monthFilter=Choices(new[]{"كل الأشهر"}.Concat(Arabic.Months));
        var key=review==null?SelectedMonth?.Key:Arabic.Period(review.Month);
        if(!history&&key is >0){year.SelectedItem=(key.Value/100).ToString();monthFilter.SelectedIndex=key.Value%100;}
        if(review!=null)agency.SelectedItem=review.Agency;
        var title=Bar(Text(history?"البحث في القيود":"قيود جهات التنفيذ",23,true));
        if(!history)title.Children.Add(ButtonFor(TransfersName,()=>Activate(TransfersName),"\uE8AB"));
        if(!history)title.Children.Add(ButtonFor("الكتب المحفوظة",()=>ShowPage("الكتب المحفوظة"),"\uE8B7"));
        if(backPage!=null)title.Children.Add(ButtonFor("العودة للرئيسية",()=>{backPage=null;ShowPage("الرئيسية");},"\uE72A"));
        category=Choices(["كل المجموعات","مديريات التنفيذ","المصارف","الجهات الأخرى"]);
        var tools=Bar(Labeled("مجموعة الجهات",category,150),Labeled("الاسم أو كلمة بحث",search,180),Labeled("جهة التنفيذ",agency,224),Labeled("رقم وتاريخ الإضبارة",reference,160),Labeled("السنة",year,104),Labeled("الشهر",monthFilter,134));
        var top=Column(title,Box(tools));DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var gridView=GridView(("الرتبة","Rank",0.7),("الاسم الكامل","Name",1.4),("المبلغ","Value",0.8),("جهة التنفيذ","Agency",1.6),("رقم وتاريخ الإضبارة","Reference",1.6),("الشهر","Month",1.0));
        grid=gridView;grid.CanUserSortColumns=false;
        var cellText=new Style(typeof(TextBlock));
        cellText.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));
        cellText.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(5,5,5,5)));
        foreach(DataGridTextColumn col in grid.Columns)col.ElementStyle=cellText;
        grid.RowHeight=double.NaN;grid.MinRowHeight=35;
        grid.MouseDoubleClick+=(_,_)=>OpenEmployee();
        grid.SelectionChanged+=(_,_)=>{if(employee!=null)employee.IsEnabled=gridView.SelectedItem is Deduction;};
        export=ButtonFor("معاينة كشف الجهات",()=>_=EntityReport(),"\uE890",true);export.IsEnabled=false;
        employee=ButtonFor("كشف المنتسب",OpenEmployee,"\uE77B");employee.IsEnabled=false;
        previous=ButtonFor("السابق",()=>{offset=Math.Max(0,offset-PageSize);_=LoadGrid();});previous.IsEnabled=false;
        next=ButtonFor("التالي",()=>{offset+=PageSize;_=LoadGrid();});next.IsEnabled=false;
        footer=Text("",14,true);
        var bottom=Box(Bar(footer,previous,next,export,employee));DockPanel.SetDock(bottom,Dock.Bottom);dock.Children.Add(bottom);
        dock.Children.Add(grid);CanvasArea.Children.Add(dock);
        void Changed(){query?.Cancel();generation++;offset=0;export.IsEnabled=false;employee.IsEnabled=false;gridView.ItemsSource=null;debounce.Stop();debounce.Start();}
        search.TextChanged+=(_,_)=>Changed();reference.TextChanged+=(_,_)=>Changed();
        foreach(var c in new[]{agency,year,monthFilter,category})c.SelectionChanged+=(_,_)=>Changed();
        _=LoadGrid();
    }
    async Task LoadGrid()
    {
        if(grid==null)return;
        query?.Cancel();using var current=new CancellationTokenSource();query=current;
        int version=++generation,skip=offset;var f=Current();
        try{
            var result=await Task.Run(()=>{
                var total=store.Summary(f,current.Token);
                current.Token.ThrowIfCancellationRequested();
                return (total.Count,total.Total,Rows:store.Find(f,PageSize,skip).ToList());
            });
            if(version!=generation||current.IsCancellationRequested)return;
            grid.ItemsSource=result.Rows;
            footer!.Text=result.Count==0?"لا توجد قيود ضمن هذا الاختيار":
                $"{skip+1}–{skip+result.Rows.Count} من {Arabic.Money(result.Count)}　·　المجموع {Arabic.Money(result.Total)} د.ع";
            previous!.IsEnabled=skip>0;next!.IsEnabled=skip+PageSize<result.Count;export!.IsEnabled=result.Count>0;
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
    void OpenEmployee()
    {
        if(grid?.SelectedItem is not Deduction row)return;
        var choices=store.Find(new(Person:row.Name)).ToList();
        var periods=months.Where(m=>m.Key>0).OrderBy(m=>m.Key).ToList();
        if(periods.Count==0)return;
        var agenciesBox=Choices(choices.Select(r=>r.Agency).Distinct());agenciesBox.SelectedItem=row.Agency;
        var refs=new ComboBox();void SetRefs(){refs.ItemsSource=new[]{"كل الأضابير"}.Concat(choices.Where(r=>r.Agency==(string?)agenciesBox.SelectedItem&&r.Reference.Length>0).Select(r=>r.Reference).Distinct()).ToList();refs.SelectedIndex=0;}
        SetRefs();if(row.Reference!="")refs.SelectedItem=row.Reference;agenciesBox.SelectionChanged+=(_,_)=>SetRefs();
        var start=new ComboBox{ItemsSource=periods,SelectedIndex=0};var end=new ComboBox{ItemsSource=periods,SelectedIndex=periods.Count-1};
        var dialog=new Window{Owner=this,Title="كشف استقطاعات منتسب",SizeToContent=SizeToContent.WidthAndHeight,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        dialog.Content=Box(Column(Text(row.Name,20,true),Labeled("جهة الاستقطاع",agenciesBox,335),Labeled("رقم وتاريخ الإضبارة",refs,335),
            Bar(Labeled("من شهر",start),Labeled("إلى شهر",end)),ButtonFor("معاينة الكشف",()=>dialog.DialogResult=true,"\uE890",true)));
        if(dialog.ShowDialog()!=true)return;
        Guard(()=>{var report=store.PersonStatement(row.Name,(string)agenciesBox.SelectedItem,refs.SelectedIndex>0?(string)refs.SelectedItem:"",
            ((MonthInfo)start.SelectedItem).Key,((MonthInfo)end.SelectedItem).Key,ReportHeader());new ReportWindow(report){Owner=this}.ShowDialog();});
    }
    void Books()
    {
        var entries=store.Books();var dock=new DockPanel();CanvasArea.Children.Add(dock);
        var picker=Choices(new[]{"كل الأشهر"}.Concat(entries.Select(b=>b.Month).Distinct()));
        if(entries.Any(b=>b.Month==SelectedMonth?.Name))picker.SelectedItem=SelectedMonth!.Name;
        var top=Column(Bar(Text("الكتب المحفوظة",23,true),ButtonFor("العودة",()=>ShowPage(backPage??"التحويلات"),"\uE72A")),Labeled("شهر الكتاب",picker,210));
        DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var list=GridView(("اسم الكتاب","Title",2.3),("الجهة","Agency",1.5),("الشهر","Month",1),("الصفحات","Pages",.6));
        var save=ButtonFor("حفظ PDF الأصلي",()=>Guard(()=>{
            if(list.SelectedItem is not ArchivedBook book)return;
            var name=string.Concat(book.Title.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?' ':c)).Trim();
            var dialog=new SaveFileDialog{Filter="ملف الكتاب|*.pdf",FileName=name};
            if(dialog.ShowDialog(this)==true){store.SaveBook(book,dialog.FileName);Status.Text="تم حفظ ملف الكتاب الأصلي.";}
        }),"\uE74E",true);
        var hint=Text("",14);var bottom=Box(Bar(hint,save));DockPanel.SetDock(bottom,Dock.Bottom);dock.Children.Add(bottom);
        dock.Children.Add(list);save.IsEnabled=false;
        void Draw(){var rows=entries.Where(b=>picker.SelectedIndex==0||b.Month==(string?)picker.SelectedItem).ToList();list.ItemsSource=rows;hint.Text=rows.Count==0?"لا توجد كتب محفوظة ضمن هذه الفترة.":rows.Count+" كتاب محفوظ";}
        list.SelectionChanged+=(_,_)=>save.IsEnabled=list.SelectedItem is ArchivedBook;
        picker.SelectionChanged+=(_,_)=>Draw();Draw();
    }
    static string BankDate(JsonElement e,string key)=>new DateTimeOffset(2001,1,1,0,0,0,TimeSpan.Zero).AddSeconds(e.GetProperty(key).GetDouble()).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy/MM/dd");
    public async Task Capture(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach(var (name,file) in new[]{("الرئيسية","home"),("التحويلات","transfers"),("كشف البنك","bank"),("الإعدادات","settings")}){
            ShowPage(name);if(grid!=null)await LoadGrid();
            await Task.Delay(280);await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
            var target=(FrameworkElement)Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth),(int)Math.Ceiling(target.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(target);
            // RenderTargetBitmap omits the RTL transform normally supplied by the Window.
            var visual=new DrawingVisual();using(var draw=visual.RenderOpen()){
                if(target.FlowDirection==FlowDirection.RightToLeft)draw.PushTransform(new MatrixTransform(-1,0,0,1,bitmap.PixelWidth,0));
                draw.DrawImage(bitmap,new Rect(0,0,bitmap.PixelWidth,bitmap.PixelHeight));
                if(target.FlowDirection==FlowDirection.RightToLeft)draw.Pop();
            }
            var screen=new RenderTargetBitmap(bitmap.PixelWidth,bitmap.PixelHeight,96,96,PixelFormats.Pbgra32);screen.Render(visual);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(screen));using var stream=File.Create(Path.Combine(directory,file+".png"));png.Save(stream);
        }
        var sample=store.Find(new()).First();
        var f=new Filters(Agency:sample.Agency,Year:sample.Period/100,Month:sample.Period%100);
        var agencyStatement=Statement.Entities(store.Find(f),ReportHeader(),true);
        var employeeStatement=store.PersonStatement(sample.Name,sample.Agency,sample.Reference,months[^1].Key,months[0].Key,ReportHeader());
        foreach(var (model,file) in new[]{(agencyStatement,"agency"),(employeeStatement,"employee")}){
            var preview=new ReportWindow(model){Owner=this};preview.Show();
            try{await preview.PrintPdf(Path.Combine(directory,file+".pdf"));model.Excel(Path.Combine(directory,file+".xlsx"));}
            finally{preview.Close();}
        }
    }
}
