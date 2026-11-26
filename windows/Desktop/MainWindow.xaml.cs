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
    private (string? Revision,string Month,Task<SalaryDesktop.Core.SalaryReviewResult> Work)? auditCache;
    Task<SalaryDesktop.Core.SalaryReviewResult> Audit(string month)
    {
        var revision=store.Active;
        if(auditCache is {} hit&&hit.Revision==revision&&hit.Month==month&&!hit.Work.IsFaulted)return hit.Work;
        var prev=months.FirstOrDefault(m=>m.Key==SalaryDesktop.Core.MonthNames.Previous(month))?.Name??"";
        var task=Task.Run(()=>SalaryDesktop.Core.SalaryReview.Prepare(store.Snapshot(month,prev),month));
        auditCache=(revision,month,task);return task;
    }
    private bool initializing=true,busy;
    private string page="الرئيسية";
    private List<MonthInfo> months=[];
    private List<string> agencies=[];
    private MonthInfo? SelectedMonth=>Period.SelectedItem as MonthInfo;
    private const string DeductionsName="الاستقطاعات", AgencyPage="جهات التنفيذ", EmployeePage="كشف استقطاعات منتسب", HistoryPage="بحث الاستقطاعات";
    private static readonly (string Name,string Icon)[] Sections=[("الرئيسية","\uE80F"),(DeductionsName,"\uE8FD"),("التحويلات","\uE8AB"),("كشف البنك","\uE825"),("الإعدادات","\uE713")];
    private static readonly (string Name,string Desc,string Icon)[] Cards=[
        ("جهات التنفيذ","القيود والمبالغ حسب الجهة","\uEC06"),("كشف استقطاعات منتسب","الجهة والإضبارة والفترة","\uE77B"),
        ("بحث الاستقطاعات","البحث في جميع الأشهر","\uE721"),("كشف البنك","فترات المطابقة المحفوظة","\uE825"),
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
        store=source;appearance=Appearance.Read(store.Root);appearance.Apply();InitializeComponent();
        SizeChanged+=(_,_)=>AdaptNavigation();
        debounce.Tick+=async (_,_)=>{debounce.Stop();await LoadGrid();};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.K&&Keyboard.Modifiers==ModifierKeys.Control&&!busy){ShowPage(SearchName);e.Handled=true;}};
        Loaded+=(_,_)=>Guard(()=>{store.UpgradeIndex();Reload();initializing=false;ShowPage("الرئيسية");});
    }
    void Guard(Action work){try{work();}catch(Exception e){Error(e);}}
    void Error(Exception e){Status.Text="تعذّر إكمال العملية. "+e.Message;MessageBox.Show(this,e.Message,"قسم الحسابات",MessageBoxButton.OK,MessageBoxImage.Warning);}
    void Reload(){LoadWorkPreferences();initializing=true;months=store.Months();agencies=store.Agencies();Period.ItemsSource=months;Period.SelectedIndex=months.Count>0?0:-1;initializing=false;}
    static Brush BrushOf(string c)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
    static TextBlock Text(string s,double size=15,bool bold=false){
        var text=new DisplayText{Text=s,FontWeight=bold?FontWeights.Bold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
        text.SetResourceReference(TextBlock.FontSizeProperty,Appearance.FontKey(size));return text;
    }
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
    static Border FilterBar(params (string Label,Control Input,double Weight)[] fields)
    {
        var row=new Grid();
        AutomationProperties.SetAutomationId(row,"ReportFilterRow");
        int inputCount=fields.Count(f=>f.Input is not Button);
        foreach(var (label,input,weight) in fields){
            int index=row.ColumnDefinitions.Count;
            var width=weight>0?new GridLength(weight,GridUnitType.Star):GridLength.Auto;
            row.ColumnDefinitions.Add(new ColumnDefinition{Width=width,MaxWidth=inputCount==1?440:double.PositiveInfinity});
            FrameworkElement cell;
            if(input is Button){
                input.VerticalAlignment=VerticalAlignment.Bottom;
                cell=input;
            }else{
                var caption=Text(label,12,true);
                caption.TextWrapping=TextWrapping.NoWrap;
                caption.TextTrimming=TextTrimming.CharacterEllipsis;
                caption.ToolTip=label;
                input.MinHeight=30;input.Padding=new Thickness(5,2,5,2);
                input.Margin=new Thickness(0,2,0,0);
                AutomationProperties.SetName(input,label);
                if(input is ComboBox combo){
                    string path=string.IsNullOrEmpty(combo.DisplayMemberPath)?"SelectedItem":"SelectedItem."+combo.DisplayMemberPath;
                    combo.SetBinding(ToolTipProperty,new Binding(path){Source=combo});
                }
                cell=Column(caption,input);
            }
            cell.Margin=new Thickness(3,0,3,0);
            Grid.SetColumn(cell,index);row.Children.Add(cell);
        }
        var border=Box(row);border.Padding=new Thickness(5);return border;
    }
    static ComboBox Choices(IEnumerable<string> values){var c=new ComboBox{ItemsSource=values.ToList(),ItemTemplate=(DataTemplate)Application.Current.FindResource("DisplayNumberItem"),SelectedIndex=0};return c;}
    static StackPanel Column(params UIElement[] items){var p=new StackPanel();foreach(var i in items)p.Children.Add(i);return p;}
    static DataGrid GridView(params (string Title,string Field,double Width)[] cols)
    {
        var g=new DataGrid();foreach(var c in cols)g.Columns.Add(new DataGridTextColumn{Header=c.Title,Binding=new Binding(c.Field){Mode=BindingMode.OneWay,Converter=(DisplayNumberConverter)Application.Current.FindResource("NumeralConverter")},Width=new DataGridLength(c.Width,DataGridLengthUnitType.Star),MinWidth=65});return g;
    }
    void OpenMonthList(object sender,RoutedEventArgs e){Period.Focus();Period.IsDropDownOpen=!Period.IsDropDownOpen;}
    void PeriodChanged(object s,SelectionChangedEventArgs e){if(!initializing&&!busy&&page!=DirectoryName)ShowPage(page);}
    void SearchClick(object s,RoutedEventArgs e){if(!busy)ShowPage(SearchName);}
    void ShowPage(string name,Review? review=null)
    {
        if(busy)return;
        if(name=="التحويلات")name=TransfersName;
        if(name==DeductionsName)name=AgencyPage;
        query?.Cancel();generation++;debounce.Stop();page=name;
        PageTitle.Text=name switch{AgencyPage or EmployeePage or HistoryPage=>DeductionsName,TransfersName=>"التحويلات",_=>name};Nav.Children.Clear();CanvasArea.Children.Clear();grid=null;
        string active=name switch{AgencyPage or EmployeePage or HistoryPage=>DeductionsName,TransfersName or CurrentTransfers or DeferredTransfers or RefundTransfers or "الكتب المحفوظة"=>"التحويلات",_=>name};
        foreach(var n in Sections){var btn=ButtonFor(n.Name,()=>{backPage=null;ShowPage(n.Name);},n.Icon);btn.Background=BrushOf(active==n.Name?"#D8EEE0":"#19483D");btn.Foreground=active==n.Name?BrushOf("#163E31"):Brushes.White;
            ((TextBlock)((StackPanel)btn.Content).Children[0]).Foreground=active==n.Name?BrushOf("#215740"):BrushOf("#CADFD2");btn.BorderThickness=new Thickness(0);btn.HorizontalContentAlignment=HorizontalAlignment.Right;btn.Margin=new Thickness(0,4,0,2);btn.Padding=new Thickness(12,11,12,11);Nav.Children.Add(btn);}
        AdaptNavigation();
        Status.Text=store.Active==null?"اختر نسخة الآيفون من الإعدادات لبدء العمل.":"تُعرض البيانات من النسخة المحفوظة محلياً";
        if(name=="الرئيسية")homeLoad=Home();else if(name=="الإعدادات")Settings();else if(name==DirectoryName)workPageLoad=ShowAccountDirectory();else if(name=="كشف البنك")Bank();else if(WorkPages.Contains(name)||name is CurrentTransfers or DeferredTransfers or RefundTransfers)workPageLoad=WorkPage(name);else if(name=="الكتب المحفوظة")Books();else if(name is AgencyPage or EmployeePage or HistoryPage)DeductionGrid(review);
        if(SystemParameters.ClientAreaAnimation&&!SystemParameters.HighContrast){CanvasArea.BeginAnimation(OpacityProperty,new DoubleAnimation(.84,1,TimeSpan.FromMilliseconds(140)){FillBehavior=FillBehavior.Stop,EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});}
    }
    void AdaptNavigation()
    {
        bool compact=ActualWidth<1050;
        NavColumn.Width=new GridLength(compact?62:194);
        Brand.Visibility=NavFoot.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
        SidePanel.Padding=compact?new Thickness(3,12,3,8):new Thickness(17,30,17,18);
        BodyViewport.Margin=compact?new Thickness(6):new Thickness(26,22,26,10);
        AccountDirectoryButton.Content=compact?"IBAN":"حسابات IBAN";
        SearchButton.Content=ActualWidth<760?"بحث":"البحث الشامل　 Ctrl+K";
        foreach(var button in Nav.Children.OfType<Button>()){
            var parts=(StackPanel)button.Content;
            parts.Children[1].Visibility=compact?Visibility.Collapsed:Visibility.Visible;
            button.ToolTip=compact?AutomationProperties.GetName(button):null;
            button.HorizontalContentAlignment=compact?HorizontalAlignment.Center:HorizontalAlignment.Right;
            button.Padding=compact?new Thickness(4,9,4,9):new Thickness(12,11,12,11);
        }
    }
    void Activate(string name)
    {
        backPage="الرئيسية";
        ShowPage(name);
    }
    async Task Home()
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
        var reviewHost=new StackPanel();
        var revision=store.Active;
        reviewHost.Children.Add(Text(selectedPeriod is {Key:>0}?"جارٍ تحميل حالات الشهر…":"تظهر حالات المراجعة بعد استيراد البيانات.",15));
        var reviews=Column(Bar(Glyph("\uEA8F","#8E6126"),Text("حالات المراجعة",19,true)),reviewHost);
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
        left.Children.Add(tiles);panel.Children.Add(Box(reviews));panel.Children.Add(Box(left));
        var library=Bar();foreach(var key in WorkPages.Where(k=>k!=SearchName))library.Children.Add(ButtonFor(key,()=>Activate(key)));
        panel.Children.Add(Box(Column(Text("دليل الكشوف",18,true),library)));
        var recent=Column(Text("الأشهر المحفوظة",18,true));
        foreach(var m in months.Take(4)){var line=Bar(ButtonFor(m.Name,()=>{initializing=true;Period.SelectedItem=m;initializing=false;Activate("جهات التنفيذ");},"\uE787"),
            Text(Arabic.Money(m.Actual)+" قيد استقطاع",14));recent.Children.Add(line);}
        if(months.Count>0)panel.Children.Add(Box(recent));
        var viewport=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        CanvasArea.Children.Add(viewport);
        bool Current()=>CanvasArea.Children.Contains(viewport) && SelectedMonth?.Name==selectedPeriod?.Name && store.Active==revision;
        if(selectedPeriod is not {Key:>0})return;
        try{
            var summary=await Task.Run(()=>store.Summary(new(Year:selectedPeriod.Key/100,Month:selectedPeriod.Key%100)));
            if(!Current())return;
            balance.Text=Arabic.Money(summary.Total);
            var prior=months.FirstOrDefault(m=>m.Key>0&&m.Key<selectedPeriod.Key);
            var data=await HomeReviewData(revision,selectedPeriod,prior);
            if(!Current())return;
            SalaryDesktop.Core.SalaryReviewResult? audit=null;
            if(data.Salary){
                try{audit=await Audit(selectedPeriod.Name);}
                catch(Exception ex){if(Current())Status.Text=ex.Message;}
            }
            if(Current())ShowReviewCards(reviewHost,selectedPeriod,data.Alerts,data.Salary,audit,prior,data.Deductions);
        }catch(Exception e){
            if(Current()){reviewHost.Children.Clear();reviewHost.Children.Add(Text("تعذّر تحميل حالات المراجعة.",16));Status.Text=e.Message;}
        }
    }
    void EditPins()
    {
        var pending=pins.ToList();var body=new StackPanel{Margin=new Thickness(15)};
        var dialog=new Window{Owner=this,Title="تخصيص كشوفي المثبتة",Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto},SizeToContent=SizeToContent.WidthAndHeight,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        void Draw(){body.Children.Clear();foreach(var name in pending.Concat(Cards.Select(c=>c.Name).Except(pending)).ToArray()){
            var check=new CheckBox{Content=name,IsChecked=pending.Contains(name),Width=200,VerticalAlignment=VerticalAlignment.Center};
            check.Click+=(_,_)=>{if(check.IsChecked==true)pending.Add(name);else pending.Remove(name);Draw();};
            var up=ButtonFor("↑",()=>{var i=pending.IndexOf(name);(pending[i-1],pending[i])=(pending[i],pending[i-1]);Draw();});up.IsEnabled=pending.IndexOf(name)>0;
            var down=ButtonFor("↓",()=>{var i=pending.IndexOf(name);(pending[i+1],pending[i])=(pending[i],pending[i+1]);Draw();});down.IsEnabled=pending.Contains(name)&&pending.IndexOf(name)<pending.Count-1;body.Children.Add(Bar(check,up,down));}
            body.Children.Add(ButtonFor("حفظ التخصيص",()=>dialog.DialogResult=true,"\uE74E",true));}
        Draw();if(dialog.ShowDialog()==true)Guard(()=>{SavePins(pending);ShowPage(page);});
    }
    void Settings()
    {
        var p=Column(AppearanceSettings(),Text("البيانات والنسخ الاحتياطي",23,true),Text("تُفتح النسخة من الحاسبة أو من مجلد iCloud Drive بعد تنزيل الملف.",14));
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
        p.Children.Add(ButtonFor("استيراد ملف بيانات",()=>_=ImportDataFile(),"\uE8B5"));
        p.Children.Add(Box(Column(Text("تحديث التطبيق",18,true),Text("الإصدار المثبّت "+WindowsRelease.DisplayVersion(InstalledVersion),14),ButtonFor("التحقق من تحديث",()=>_=CheckForUpdate(true),"\uE895"))));
        CanvasArea.Children.Add(new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
    }
    void CopyArchive()=>Guard(()=>{if(store.Active==null)return;var d=new SaveFileDialog{FileName=Store.BackupName,Filter="نسخة الآيفون|*.zip"};if(d.ShowDialog(this)!=true)return;_=Working(()=>store.ExportBackup(d.FileName),"حُفظت النسخة المشتركة كاملة.");});
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
        reference?.Text??"",int.TryParse(year?.SelectedItem?.ToString(),out int y)?y:0,Math.Max(0,monthFilter?.SelectedIndex??0),Category:category?.SelectedIndex>0?(string)category.SelectedItem:"",
        SearchField:page==EmployeePage?DeductionSearchField.Employee:DeductionSearchField.Any);
    void DeductionGrid(Review? review)
    {
        offset=0;
        var dock=new DockPanel();
        search=page==AgencyPage?null:new TextBox();reference=new TextBox();
        agency=Choices(new[]{"كل الجهات"}.Concat(agencies));
        year=Choices(new[]{"كل السنوات"}.Concat(months.Where(m=>m.Key>0).Select(m=>(m.Key/100).ToString()).Distinct()));
        monthFilter=Choices(new[]{"كل الأشهر"}.Concat(Arabic.Months));
        var key=review==null?SelectedMonth?.Key:Arabic.Period(review.Month);
        if(page==AgencyPage&&key is >0){year.SelectedItem=(key.Value/100).ToString();monthFilter.SelectedIndex=key.Value%100;}
        if(review!=null)agency.SelectedItem=review.Agency;
        var title=Bar();
        foreach(var (route,label,icon) in new[]{(AgencyPage,"جهات التنفيذ","\uEC06"),(EmployeePage,"استقطاعات المنتسبين","\uE77B"),(HistoryPage,"بحث في جميع القيود","\uE721")})
            title.Children.Add(ButtonFor(label,()=>{if(page!=route)ShowPage(route);},icon,page==route));
        if(backPage=="الرئيسية")title.Children.Add(ButtonFor("العودة للرئيسية",()=>{backPage=null;ShowPage("الرئيسية");},"\uE72A"));
        category=Choices(["كل المجموعات","مديريات التنفيذ","المصارف","الجهات الأخرى"]);
        var fields=new List<(string,Control,double)>{("مجموعة الجهات",category,0)};
        if(search!=null)fields.Add((page==EmployeePage?"بحث باسم المنتسب":"كلمة البحث",search,1));
        fields.AddRange([("جهة التنفيذ",agency,1.4),("رقم وتاريخ الإضبارة",reference,1),("السنة",year,0),("الشهر",monthFilter,0)]);
        var filterBar=FilterBar(fields.ToArray());
        var top=Column(title,filterBar);DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var gridView=GridView(("الرتبة","Rank",0.7),("الاسم الكامل","Name",1.4),("المبلغ","Value",0.8),("جهة التنفيذ","Agency",1.6),("رقم وتاريخ الإضبارة","Reference",1.6),("الشهر","Month",1.0));
        grid=gridView;grid.CanUserSortColumns=false;
        var cellText=new Style(typeof(TextBlock));
        cellText.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));
        cellText.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(5,5,5,5)));
        foreach(DataGridTextColumn col in grid.Columns)col.ElementStyle=cellText;
        grid.RowHeight=double.NaN;grid.MinRowHeight=35;
        grid.MouseDoubleClick+=(_,_)=>OpenEmployee();
        grid.SelectionChanged+=(_,_)=>{if(employee!=null)employee.IsEnabled=gridView.SelectedItem is Deduction;};
        export=ButtonFor("معاينة كشف جهات التنفيذ",()=>_=EntityReport(),"\uE890",true);export.IsEnabled=false;
        employee=ButtonFor("معاينة كشف المنتسب",OpenEmployee,"\uE77B");employee.IsEnabled=false;
        previous=ButtonFor("السابق",()=>{offset=Math.Max(0,offset-PageSize);_=LoadGrid();});previous.IsEnabled=false;
        next=ButtonFor("التالي",()=>{offset+=PageSize;_=LoadGrid();});next.IsEnabled=false;
        footer=Text("",14,true);
        var reportActions=page==EmployeePage?Bar(footer,previous,next,employee):Bar(footer,previous,next,export,employee);
        if(page==AgencyPage&&agencies.Count>0)reportActions.Children.Add(ButtonFor("توحيد تسمية الجهة",()=>Guard(RenameAgency)));
        var bottom=Box(reportActions);DockPanel.SetDock(bottom,Dock.Bottom);dock.Children.Add(bottom);
        dock.Children.Add(grid);CanvasArea.Children.Add(dock);
        void Changed(){query?.Cancel();generation++;offset=0;export.IsEnabled=false;employee.IsEnabled=false;gridView.ItemsSource=null;debounce.Stop();debounce.Start();}
        if(search!=null)search.TextChanged+=(_,_)=>Changed();
        reference.TextChanged+=(_,_)=>Changed();
        foreach(var c in new[]{agency,year,monthFilter,category})c.SelectionChanged+=(_,_)=>Changed();
        _=LoadGrid();if(page==EmployeePage)search?.Focus();
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
        var f=Current();
        var scope=periods.Where(m=>(f.Year==0||m.Key/100==f.Year)&&(f.Month==0||m.Key%100==f.Month)).ToList();
        var start=new ComboBox{ItemsSource=periods,SelectedItem=scope.FirstOrDefault()??periods[0]};var end=new ComboBox{ItemsSource=periods,SelectedItem=scope.LastOrDefault()??periods[^1]};
        var dialog=new Window{Owner=this,Title="كشف استقطاعات منتسب",Width=Math.Min(800,SystemParameters.WorkArea.Width*.94),SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        dialog.Content=Box(Column(Text(row.Name,20,true),FilterBar(("جهة الاستقطاع",agenciesBox,1.25),("رقم وتاريخ الإضبارة",refs,1.4),("من شهر",start,0),("إلى شهر",end,0)),ButtonFor("معاينة الكشف",()=>dialog.DialogResult=true,"\uE890",true)));
        if(dialog.ShowDialog()!=true)return;
        Guard(()=>{var report=store.PersonStatement(row.Name,(string)agenciesBox.SelectedItem,refs.SelectedIndex>0?(string)refs.SelectedItem:"",
            ((MonthInfo)start.SelectedItem).Key,((MonthInfo)end.SelectedItem).Key,ReportHeader());new ReportWindow(report){Owner=this}.ShowDialog();});
    }
    void Books()
    {
        var entries=store.Books();var dock=new DockPanel();CanvasArea.Children.Add(dock);
        var picker=Choices(new[]{"كل الأشهر"}.Concat(entries.Select(b=>b.Month).Distinct()));
        if(entries.Any(b=>b.Month==SelectedMonth?.Name))picker.SelectedItem=SelectedMonth!.Name;
        var top=Column(Bar(Text("الكتب المحفوظة",23,true),ButtonFor("إضافة كتاب موقّع",()=>Guard(ImportSigned)),ButtonFor("العودة",()=>ShowPage(backPage??TransfersName),"\uE72A")),FilterBar(("شهر الكتاب",picker,0)));
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
        var sample=store.Find(new()).First();
        static IEnumerable<T> Controls<T>(DependencyObject root) where T:DependencyObject{
            if(root is T match)yield return match;
            foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach(var found in Controls<T>(child))yield return found;
        }
        Button FindAction(DependencyObject root,string label)=>Controls<Button>(root).FirstOrDefault(b=>AutomationProperties.GetName(b)==label)
            ??throw new InvalidDataException("زر غير موجود في مسار الاختبار: "+label);
        void Click(DependencyObject root,string label)=>FindAction(root,label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task ChooseMonth(int key){
            Period.ApplyTemplate();
            var opener=(Button)Period.Template.FindName("MonthButton",Period);
            if(Math.Abs(opener.ActualHeight-SearchButton.ActualHeight)>1)throw new InvalidDataException("ارتفاع زر الشهر لا يطابق البحث.");
            opener.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
            if(!Period.IsDropDownOpen)throw new InvalidDataException("تعذر فتح قائمة الأشهر.");
            var month=months.Single(m=>m.Key==key);
            Period.SelectedItem=month;Period.IsDropDownOpen=false;
            if(SelectedMonth!=month)throw new InvalidDataException("تعذر تغيير شهر العمل.");
            await workPageLoad;
        }
        foreach(var (name,file) in new[]{("الرئيسية","home"),(DeductionsName,"deductions"),(EmployeePage,"employee-search"),("التحويلات","transfers"),("كشف البنك","bank"),("الإعدادات","settings"),(CurrentTransfers,"transfer-files"),(FundingName,"funding"),(AuditName,"audit"),(SearchName,"search"),("إعادة المبالغ","refunds"),(TrustName,"trusts"),(DeferredTransfers,"deferred-transfers"),(HealthName,"health"),(FundName,"fund")}){
            if(name==DeductionsName){
                var order=Nav.Children.OfType<Button>().Select(AutomationProperties.GetName).ToList();
                if(order.IndexOf(DeductionsName)!=1)throw new InvalidDataException("ترتيب قسم الاستقطاعات غير صحيح.");
                Click(Nav,DeductionsName);
                agency!.SelectedItem=sample.Agency;debounce.Stop();await LoadGrid();
                if(search!=null||grid!.Items.Count==0||grid.Items.Cast<Deduction>().Any(r=>r.Agency!=sample.Agency))throw new InvalidDataException("تصفية القائمة لا تطابق الجهة المختارة.");
                UpdateLayout();
                var firstRow=grid.Items[0];
                foreach(var column in grid.Columns.Take(3)){
                    grid.ScrollIntoView(firstRow,column);
                    grid.UpdateLayout();
                    if(column.GetCellContent(firstRow) is not TextBlock displayed ||
                       FieldCopy.ValueAt(displayed)!=displayed.Text ||
                       displayed.Parent is DataGridCell cell && FieldCopy.ValueAt(cell)!=displayed.Text)
                        throw new InvalidDataException("تعذّر نسخ قيمة خلية الاستقطاع المعروضة.");
                }
                const string account="IQ00TEST00000000000001";
                if(FieldCopy.ValueAt(new TextBox{Text=account})!=account ||
                   FieldCopy.ValueAt(new DisplayText{Text="٠٠١٢٣"} )!=DisplayNumerals.Format("٠٠١٢٣"))
                    throw new InvalidDataException("لم يحافظ النسخ على نص الحقل وأصفاره.");
                if(page!=AgencyPage||Current().Month!=SelectedMonth!.Key%100)throw new InvalidDataException("تعذّر فتح استقطاعات الشهر.");
            }else if(name==EmployeePage){
                Click(CanvasArea,"استقطاعات المنتسبين");search!.Text=sample.Name;
                debounce.Stop();await LoadGrid();
                if(grid!.Items.Count==0||grid.Items.Cast<Deduction>().Any(r=>r.Name!=sample.Name))throw new InvalidDataException("نتائج بحث المنتسب غير مطابقة.");
                grid.SelectedIndex=0;
                if(!employee!.IsEnabled)throw new InvalidDataException("تعذّر اختيار كشف المنتسب.");
            }else if(name=="التحويلات"){
                Click(Nav,name);
                var names=Controls<Button>(CanvasArea).Select(AutomationProperties.GetName).ToArray();
                if(page!=TransfersName||!names.SequenceEqual(new[]{CurrentTransfers,DeferredTransfers,RefundTransfers}))throw new InvalidDataException("واجهة التحويلات يجب أن تحتوي أقسام العمل الثلاثة فقط.");
            }else if(name is CurrentTransfers or DeferredTransfers){
                ShowPage(TransfersName);Click(CanvasArea,name);await workPageLoad;
                if(name==CurrentTransfers){
                    await ChooseMonth(202610);
                    var group=Controls<ComboBox>(CanvasArea).Single(c=>AutomationProperties.GetName(c)=="المجموعة");
                    group.SelectedIndex=(int)SalaryDesktop.Core.TransferGroup.Anbar;
                    var books=Controls<DataGrid>(CanvasArea).Single(c=>AutomationProperties.GetName(c)=="كتب فرع الأنبار والكتب المنفردة");
                    var letters=books.Items.Cast<TransferRow>().ToArray();
                    if(!BookTitles.Keys.Where(id=>!id.EndsWith("_group")).All(id=>letters.Count(row=>row.Book?.Id==id)==1))
                        throw new InvalidDataException("جدول فرع الأنبار يفتقد كتاباً منفرداً أو يعرضه مرتين.");
                    if(letters.Any(row=>row.Entry!=null && row.Value!=row.Entry.Amount || row.Book?.NeedsLink==true && row.Value!=null))
                        throw new InvalidDataException("مبلغ الجهة في جدول الكتب لا يطابق الربط.");
                    var selectedMonth=SelectedMonth!.Name;
                    var snapshot=store.Snapshot(selectedMonth);
                    var foodCompany=letters.Single(r=>r.Book?.Id=="food_company");
                    var foodTax=letters.Single(r=>r.Book?.Id=="food_tax");
                    if(snapshot.Employees.Any(e=>e.Month==selectedMonth)){
                        var bank=SalaryDesktop.Core.BankStatementCalculator.Calculate(snapshot,selectedMonth);
                        var food=new SalaryDesktop.Core.FoodContractReport.Split(bank.Breakdown["شركة عراق الجوال"]);
                        if(foodCompany.Value!=food.Company || foodTax.Value!=food.Tax)
                            throw new InvalidDataException("مبالغ كتب عقد الإطعام لا تطابق حصص الكشف.");
                    }else if(foodCompany.Value!=null || foodTax.Value!=null)
                        throw new InvalidDataException("ظهر مبلغ لكتاب رواتب في شهر بلا بيانات رواتب.");
                    if(Controls<Button>(CanvasArea).Any(btn=>AutomationProperties.GetName(btn) is "فتح الكتاب" or "خيارات"))
                        throw new InvalidDataException("ظهرت أوامر الكتاب خارج صفّه.");
                    async Task CheckActions(TransferRow item,bool linkable){
                        books.ScrollIntoView(item);books.UpdateLayout();
                        var row=(DataGridRow)books.ItemContainerGenerator.ContainerFromItem(item);
                        var cell=books.Columns[0].GetCellContent(row)??throw new InvalidDataException("تعذّر الوصول إلى خلية الكتاب.");
                        MouseButtonEventArgs Left(RoutedEvent route)=>new(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=route};
                        cell.RaiseEvent(Left(Mouse.PreviewMouseDownEvent));cell.RaiseEvent(Left(Mouse.MouseUpEvent));
                        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                        static ContextMenu? FindMenu(DependencyObject visual){
                            if(visual is ContextMenu menu)return menu;
                            for(int i=0;i<VisualTreeHelper.GetChildrenCount(visual);i++)
                                if(FindMenu(VisualTreeHelper.GetChild(visual,i)) is {} found)return found;
                            return null;
                        }
                        var menu=PresentationSource.CurrentSources.OfType<PresentationSource>().Select(p=>p.RootVisual)
                            .OfType<Visual>().Select(FindMenu).FirstOrDefault(m=>m?.IsOpen==true && m.PlacementTarget==row)
                            ??throw new InvalidDataException("تعذّر فتح أوامر الصف بنقرة يسرى.");
                        try{
                            var commands=menu.Items.OfType<MenuItem>().ToDictionary(i=>i.Header.ToString()!);
                            if(!ReferenceEquals(books.SelectedItem,item) || !commands.ContainsKey("تعديل الكتاب") ||
                                commands["معاينة الكتاب"].IsEnabled==item.Book!.NeedsLink ||
                                commands["تصدير الكتاب"].IsEnabled==item.Book.NeedsLink ||
                                commands.ContainsKey("ربط جهة بالكتاب")!=linkable)
                                throw new InvalidDataException("قائمة الأوامر لا تطابق الكتاب المحدد.");
                        }finally{menu.IsOpen=false;}
                    }
                    await CheckActions(letters.Single(row=>row.Book?.Id=="ramadi"),true);
                    await CheckActions(letters.Single(row=>row.Book?.Id=="cash_salary"),false);
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
                    CaptureWindow((FrameworkElement)Content,"anbar-letters");
                    group.SelectedIndex=(int)SalaryDesktop.Core.TransferGroup.Rafidain;
                    await ChooseMonth(202611);
                }else{
                    FindAction(CanvasArea,"إلغاء التأجيل");
                    await ChooseMonth(202610);
                    FindAction(CanvasArea,"تأجيل استقطاعات العقود");
                    if(Controls<DataGrid>(CanvasArea).Any())throw new InvalidDataException("ظهرت جهات مؤجلة لشهر بلا فصل.");
                    await ChooseMonth(202611);
                }
            }else if(name=="إعادة المبالغ"){
                ShowPage(TransfersName);Click(CanvasArea,RefundTransfers);await workPageLoad;
                Click(CanvasArea,"الدفعات المحفوظة");
                var saved=store.Refunds().First(b=>b.ArchiveId!="draft");
                var savedRow=Controls<Button>(CanvasArea).First(b=>AutomationProperties.GetName(b).StartsWith(saved.Date.ToString("yyyy/MM/dd")));
                savedRow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var banks=saved.Groups();
                if(Controls<Button>(CanvasArea).Count(b=>AutomationProperties.GetName(b)=="تصدير CSV")!=banks.Count ||
                    Controls<Button>(CanvasArea).Count(b=>AutomationProperties.GetName(b).StartsWith("معاينة كتاب إعادة مبالغ"))!=banks.GroupBy(g=>g.Code=="RAFB").Count())
                    throw new InvalidDataException("ملفات الدفعة المحفوظة لا تطابق مصارفها.");

            }else{
                if(name is HealthName or FundName){
                    initializing=true;Period.SelectedItem=months.Single(m=>m.Key==202610);initializing=false;
                }
                ShowPage(name);
            }
            if(name=="الرئيسية"){
                await homeLoad;
                var cards=Controls<Button>(CanvasArea).Where(b=>AutomationProperties.GetAutomationId(b).StartsWith("HomeReview-")).ToList();
                if(cards.Count<2 || cards.Select(AutomationProperties.GetName).Distinct().Count()!=cards.Count)
                    throw new InvalidDataException("لم تظهر حالات الشهر كبطاقات مستقلة.");
            }
            if(name=="الإعدادات"){
                FindAction(CanvasArea,"استيراد ملف بيانات");
                ComboBox Setting(string name)=>Controls<ComboBox>(CanvasArea).Single(c=>AutomationProperties.GetName(c)==name);
                string statementMonth=months.Single(m=>m.Key==202610).Name;
                var statement=WorkReports.Command(store.Snapshot(statementMonth),statementMonth);
                var originalPreview=statement.Html();
                Setting("حجم خط المحتوى").SelectedIndex=4;Setting("شكل أرقام الواجهة").SelectedIndex=1;
                var sampleText=Controls<TextBlock>(CanvasArea).FirstOrDefault(t=>t.Text.Contains("١٢,٥٠٠"));
                if(sampleText==null||Math.Abs(sampleText.FontSize-19.2)>.1||store.ReadWorkPreferences().GetValueOrDefault("windowsContentTextLevel")!="4" || store.ReadWorkPreferences().GetValueOrDefault("appNumerals")!="arabicIndic")
                    throw new InvalidDataException("تعذّر تطبيق وحفظ حجم الخط والأرقام.");
                if(statement.Html()!=originalPreview)throw new InvalidDataException("تغيّرت أرقام كشف القيادة عند تبديل أرقام الواجهة.");
                Setting("حجم خط المحتوى").SelectedIndex=0;Setting("شكل أرقام الواجهة").SelectedIndex=0;
                if(!Controls<TextBlock>(CanvasArea).Any(t=>t.Text.Contains("12,500")))throw new InvalidDataException("تعذر استرجاع الأرقام العربية.");
            }
            if(name=="كشف البنك"){
                await bankLoad;
                if(bankLedgerSelected)throw new InvalidDataException("لم يفتح كشف الشهر أولاً.");
                UpdateLayout();
                CaptureWindow((FrameworkElement)Content,"bank-month");
                // Verify both preview actions through the same controls used by the accountant.
                async Task CheckPreview(){
                    bool opened=false;
                    var finished=new TaskCompletionSource<bool>();
                    var probe=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
                    var deadline=DateTime.UtcNow.AddSeconds(25);
                    probe.Tick+=async (_,_)=>{
                        var preview=OwnedWindows.OfType<LetterPreview>().FirstOrDefault();
                        if(preview==null){if(DateTime.UtcNow>=deadline){probe.Stop();finished.TrySetResult(false);}return;}
                        probe.Stop();
                        try{opened=await preview.Ready.WaitAsync(TimeSpan.FromSeconds(15));}
                        catch(Exception){opened=false;}
                        finally{
                            if(opened){
                                opened=Controls<Button>((FrameworkElement)preview.Content).Any(b=>Equals(b.Content,"تصدير PDF")&&b.IsEnabled);
                                var escape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(preview)!,Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.KeyDownEvent};
                                preview.RaiseEvent(escape);
                                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                                opened=opened && escape.Handled && !preview.IsVisible;
                            }
                            if(preview.IsVisible)preview.Close();
                            finished.TrySetResult(opened);
                        }
                    };
                    probe.Start();
                    try{
                        Click(CanvasArea,"معاينة الكشف");
                        if(!await finished.Task.WaitAsync(TimeSpan.FromSeconds(45)))throw new InvalidDataException("تعذّرت معاينة كشف البنك.");
                    }finally{probe.Stop();}
                }
                await CheckPreview();
                Click(CanvasArea,BankLedger);
                await bankLoad;
                await CheckPreview();
            }
            if(grid!=null&&name!=EmployeePage)await LoadGrid();
            await Task.Delay(280);await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
            if(name is DeductionsName or EmployeePage or CurrentTransfers or DeferredTransfers or "كشف البنك" or AuditName or SearchName or TrustName or "الكتب المحفوظة" or HealthName or FundName){
                static IEnumerable<Grid> FilterRows(DependencyObject root){
                    if(root is Grid row&&AutomationProperties.GetAutomationId(row)=="ReportFilterRow"){yield return row;yield break;}
                    foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                        foreach(var result in FilterRows(child))yield return result;
                }
                var rows=FilterRows(CanvasArea).ToArray();
                if(rows.Length!=1)throw new InvalidDataException("شريط التصفية غير موجود في "+name);
                var original=CanvasArea.MaxWidth;
                try{
                    foreach(double available in new[]{628d,original}){
                        CanvasArea.MaxWidth=available;UpdateLayout();
                        foreach(var row in rows){
                            var inputs=row.Children.OfType<StackPanel>().Select(field=>(Control)field.Children[1]);
                            var bounds=inputs.Select(c=>c.TransformToAncestor(CanvasArea).TransformBounds(new Rect(c.RenderSize))).OrderBy(r=>r.Left).ToArray();
                            if(bounds.Any(r=>Math.Abs(r.Top-bounds[0].Top)>1||r.Left<0||r.Right>CanvasArea.ActualWidth+1||r.Width<42)||
                                bounds.Zip(bounds.Skip(1),(a,b)=>a.Right>b.Left+1).Any(overlap=>overlap))
                                throw new InvalidDataException("عرض حقول التصفية غير مناسب في "+name);
                        }
                        if(grid!=null&&grid.TranslatePoint(new Point(),CanvasArea).Y>120)throw new InvalidDataException("شريط الاستقطاعات مرتفع.");
                    }
                }finally{CanvasArea.MaxWidth=original;UpdateLayout();}
            }
            CaptureWindow((FrameworkElement)Content,file);
        }
        AccountDirectoryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await workPageLoad;
        var accountGrid=Controls<DataGrid>(CanvasArea).Single();
        if(page!=DirectoryName || accountGrid.Items.Count!=store.Snapshot().Accounts.Count)
            throw new InvalidDataException("لم يعرض الدليل جميع الحسابات المحفوظة.");
        FindAction(CanvasArea,"إضافة حساب");
        FindAction(CanvasArea,"تصدير Excel");
        accountGrid.SelectedIndex=accountGrid.Items.Count>0?0:-1;
        if(FindAction(CanvasArea,"تعديل الحساب").IsEnabled!=(accountGrid.SelectedItem!=null))
            throw new InvalidDataException("خيار تعديل الحساب لا يوافق الصف المحدد.");
        UpdateLayout();CaptureWindow((FrameworkElement)Content,"account-directory");
        ShowPage(DeductionsName);
        agency!.SelectedItem=sample.Agency;debounce.Stop();await LoadGrid();
        double savedWidth=Width,savedHeight=Height;
        try{
            Width=820;Height=560;UpdateLayout();
            await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
            if(NavColumn.Width.Value>70||agency.SelectedItem?.ToString()!=sample.Agency||grid!.ActualHeight<80)
                throw new InvalidDataException("تعذّر تصغير نافذة الاستقطاعات مع الاحتفاظ بالجهة والجدول.");
            CaptureWindow((FrameworkElement)Content,"deductions-compact");
            Width=560;Height=390;UpdateLayout();
            if(BodyViewport.ScrollableWidth<=0||BodyViewport.ScrollableHeight<=0)throw new InvalidDataException("تعذّر الوصول إلى كامل المحتوى في النافذة الصغيرة.");
            var inner=new ScrollViewer{Width=150,Height=90,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
                Content=new Border{Width=400,Height=250}};
            CanvasArea.Children.Add(inner);
            try{
                UpdateLayout();
                async Task NextFrame(){
                    var rendered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Frame(object? sender,EventArgs e)=>rendered.TrySetResult(true);
                    CompositionTarget.Rendering+=Frame;
                    try{await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));}
                    finally{CompositionTarget.Rendering-=Frame;}
                    await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
                }
                PrecisionScroll.Move(inner,.25,false,3);PrecisionScroll.Move(inner,.25,false,3);
                await NextFrame();
                if(Math.Abs(inner.VerticalOffset-24)>1)throw new InvalidDataException("لم تُجمع حركات التمرير القصيرة.");
                inner.ScrollToVerticalOffset(inner.ScrollableHeight-4);inner.ScrollToHorizontalOffset(25);
                BodyViewport.ScrollToTop();UpdateLayout();
                PrecisionScroll.Move(inner,.25,false,3);PrecisionScroll.Move(inner,.25,true,3);
                await NextFrame();
                if(Math.Abs(BodyViewport.VerticalOffset-8)>1 || Math.Abs(inner.HorizontalOffset-19)>1)
                    throw new InvalidDataException("اتجاه التمرير الأفقي أو انتقال الحركة عند حافة الجدول غير صحيح.");
            }finally{CanvasArea.Children.Remove(inner);}
            BodyViewport.ScrollToRightEnd();BodyViewport.ScrollToBottom();UpdateLayout();
            if(BodyViewport.VerticalOffset<=0)throw new InvalidDataException("تعذّر تمرير المحتوى.");
        }finally{Width=savedWidth;Height=savedHeight;UpdateLayout();}
        ShowPage(SearchName);
        var hit=store.SearchPeople(sample.Name).First(r=>r.Name==sample.Name);
        await OpenPerson(hit with{Kind="راتب"},target=>CaptureWindow(target,"employee-profile"));
        var editorMonth="تشرين الثاني 2026";
        var editorSource=store.Snapshot(editorMonth);
        var demoTemplate=new SalaryDesktop.Core.LetterTemplate{Title="كتاب تجريبي",Body="يرجى تحويل مبلغ ({{المبلغ}}) إلى الحساب ({{الحساب}}).",FirstSigner="عضو اللجنة"};
        var editor=new LetterWindow(demoTemplate,"custom",editorSource,editorMonth,true,t=>t.Validate(),t=>LayoutSample(t,editorMonth)){Owner=this};
        editor.Show();editor.UpdateLayout();CaptureWindow((FrameworkElement)editor.Content,"letter-editor");
        var letterPreview=new LetterPreview(LayoutSample(demoTemplate,editorMonth)){Owner=editor};letterPreview.Show();
        if(!await letterPreview.Ready.WaitAsync(TimeSpan.FromSeconds(20)))throw new InvalidDataException("تعذّرت معاينة القالب في العارض المحلي.");
        letterPreview.Close();editor.Close();
        void CaptureWindow(FrameworkElement target,string file){
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth),(int)Math.Ceiling(target.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(target);
            // RenderTargetBitmap omits the RTL transform normally supplied by the Window.
            var visual=new DrawingVisual();using(var draw=visual.RenderOpen()){
                if(target.FlowDirection==FlowDirection.RightToLeft)draw.PushTransform(new MatrixTransform(-1,0,0,1,bitmap.PixelWidth,0));
                draw.DrawImage(bitmap,new Rect(0,0,bitmap.PixelWidth,bitmap.PixelHeight));
                if(target.FlowDirection==FlowDirection.RightToLeft)draw.Pop();
            }
            var screen=new RenderTargetBitmap(bitmap.PixelWidth,bitmap.PixelHeight,96,96,PixelFormats.Pbgra32);screen.Render(visual);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(screen));using var stream=File.Create(Path.Combine(directory,file+".png"));png.Save(stream);
        }
        initializing=true;Period.SelectedIndex=0;initializing=false;
        var f=new Filters(Agency:sample.Agency,Year:sample.Period/100,Month:sample.Period%100);
        var agencyStatement=Statement.Entities(store.Find(f),ReportHeader(),true);
        var employeeStatement=store.PersonStatement(sample.Name,sample.Agency,sample.Reference,months[^1].Key,months[0].Key,ReportHeader());
        var longRows=Enumerable.Range(1,180).Select(i=>sample with{Name="منتسب تجريبي "+i,Reference="إضبارة رقم "+i+" / ٢٠٢٦ بتاريخ ١٢ / ٣ / ٢٠٢٦ ضمن كتاب التنفيذ المؤرخ في ٢٥ / ١١ / ٢٠٢٦",Amount=i}).ToList();
        var longSheet=Statement.Entities(longRows,ReportHeader(),false);
        string[] refNames=["أحمد سامر محمود","باسم علي حسن","حسن فاضل ناصر","خالد سعد كريم","رائد عماد علي","سامي كاظم حسن","عادل حامد سالم","علي حسن ناصر","محمد ناصر علي","مصطفى سعد سالم","نزار حامد فاضل","يوسف سامر كريم"];
        var reference=Statement.Entities(refNames.Select((name,i)=>sample with{Month="تشرين الثاني 2026",Period=202611,Agency="جهة تجريبية",Name=name,Rank=i%2==0?"مفوض":"شرطي",Reference=(101+i)+" / ٢٠٢٦",Amount=125000+i*2500}),"قيادة شرطة محافظة الانبار",false);

        var annualRows=Enumerable.Range(0,60).Select(i=>sample with{Period=(2022+i/12)*100+i%12+1,Month=Arabic.Months[i%12]+" "+(2022+i/12),Amount=1000+i}).ToArray();
        var annual=Statement.Employee(annualRows,annualRows.Select(r=>new MonthInfo(r.Month,r.Period,1,1)).ToList(),sample.Name,sample.Agency,sample.Reference,202201,202612,ReportHeader());
        var command=WorkReports.Command(store.Snapshot(SelectedMonth!.Name),SelectedMonth.Name);
        foreach(var (model,file) in new[]{(agencyStatement,"agency"),(employeeStatement,"employee"),(longSheet,"agency-long"),(command,"command"),(reference,"entity-reference"),(annual,"employee-years")}){
            var preview=new ReportWindow(model){Owner=this};preview.Show();
            try{await preview.PrintPdf(Path.Combine(directory,file+".pdf"));model.Excel(Path.Combine(directory,file+".xlsx"));}
            finally{preview.Close();}
        }
    }
}
