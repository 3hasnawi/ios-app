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
    const string ArchiveName="الأرشيف";
    private static readonly (string Name,string Icon)[] Sections=[("الرئيسية","\uE80F"),(DeductionsName,"\uE8FD"),("التحويلات","\uE8AB"),("كشف البنك","\uE825"),(ArchiveName,"\uE8B7"),("الإعدادات","\uE713")];
    private static readonly (string Name,string Desc,string Icon)[] Cards=[
        ("جهات التنفيذ","القيود والمبالغ حسب الجهة","\uEC06"),("كشف استقطاعات منتسب","الجهة والإضبارة والفترة","\uE77B"),
        ("بحث الاستقطاعات","البحث والتصفية في سجل القيود","\uE721"),("كشف البنك","فترات المطابقة المحفوظة","\uE825"),
        (TransfersName,"كتب المصارف ودفعات الشهر","\uE8AB"),(AuditName,"حالات المراجعة والتغييرات","\uEA8F"),
        (TrustName,"المنتسبون وحالات الصرف","\uE77B"),(FundingName,"تفصيل المبالغ ومطابقتها","\uE825"),
        (CommandName,"استقطاعات القيادة للشهر","\uE8A5"),(HealthName,"المشتركون والمبالغ غير المنتظمة","\uE95E"),(FundName,"الاستقطاعات حسب الفئة","\uE8A5")];
    private List<string> pins=Cards.Take(4).Select(x=>x.Name).ToList();
    private readonly DispatcherTimer debounce=new(){Interval=TimeSpan.FromMilliseconds(220)};
    private int generation,offset;private const int PageSize=150;
    private TextBox? search;private ComboBox? agency,year,monthFilter;
    private DataGrid? grid;private TextBlock? footer;private Button? next,previous;
    readonly Stack<string> pageHistory=new();
    Action? subpageBack;
    IInputElement? focusBeforeSearch;
    bool SearchIsOpen=>SearchLayer.Visibility==Visibility.Visible;

    void UpdateBackButton()=>BackButton.IsEnabled=SearchIsOpen || subpageBack!=null || pageHistory.Count>0;

    void SetSubpageBack(Action back){subpageBack=back;UpdateBackButton();}

    void BackClick(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        if(SearchIsOpen){CloseKeyboardSearch();return;}
        if(subpageBack!=null){Guard(subpageBack);return;}
        if(pageHistory.TryPop(out var previous))ShowPage(previous,goingBack:true);
    }

    void OpenKeyboardSearch()
    {
        if(SearchIsOpen || busy)return;
        focusBeforeSearch=Keyboard.FocusedElement;
        BodyViewport.Visibility=PageTitle.Visibility=Visibility.Hidden;TransferTabs.Visibility=Visibility.Collapsed;
        SearchLayer.Visibility=Visibility.Visible;
        UpdateBackButton();
        SearchAll(SearchLayer);
    }

    void CloseKeyboardSearch()
    {
        if(!SearchIsOpen)return;
        SearchLayer.Children.Clear();SearchLayer.Visibility=Visibility.Collapsed;
        BodyViewport.Visibility=PageTitle.Visibility=Visibility.Visible;UpdateTransferTabs(page);
        UpdateBackButton();
        if(focusBeforeSearch is UIElement {IsVisible:true,IsEnabled:true} target)target.Focus();
        focusBeforeSearch=null;
    }
    public MainWindow(Store source)
    {
        store=source;appearance=Appearance.Read(store.Root);appearance.Apply();InitializeComponent();
        SizeChanged+=(_,_)=>AdaptNavigation();
        debounce.Tick+=async (_,_)=>{debounce.Stop();await LoadGrid();};
        PreviewKeyDown+=(_,e)=>{
            if(e.Key==Key.K && Keyboard.Modifiers==ModifierKeys.Control && !busy){OpenKeyboardSearch();e.Handled=true;}
            else if(e.Key==Key.Escape && SearchIsOpen){CloseKeyboardSearch();e.Handled=true;}
        };
        Loaded+=async (_,_)=>{
            Guard(()=>{store.UpgradeIndex();Reload();initializing=false;ShowPage("الرئيسية");});
            if(store.Active is {} revision){
                try{await Task.Run(()=>store.PreparePersonIndex(revision));}
                catch(Exception ex){if(store.Active==revision)Status.Text="تعذّر تجهيز فهرس المنتسبين. "+ex.Message;}
            }
        };
    }
    void Guard(Action work){try{work();}catch(Exception e){Error(e);}}
    void Error(Exception e){Status.Text="تعذّر إكمال العملية. "+e.Message;MessageBox.Show(this,e.Message,"قسم الحسابات",MessageBoxButton.OK,MessageBoxImage.Warning);}
    void Reload(){LoadWorkPreferences();initializing=true;months=store.Months();agencies=store.Agencies();Period.ItemsSource=months;Period.SelectedIndex=months.Count>0?0:-1;initializing=false;}
    static Brush BrushOf(string c)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
    static TextBlock Text(string s,double size=15,bool bold=false){
        var text=new DisplayText{Text=s,FontWeight=bold?FontWeights.Bold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
        text.SetResourceReference(TextBlock.FontSizeProperty,Appearance.FontKey(size));return text;
    }
    static TextBlock Glyph(string s,string? color=null){
        var glyph=new TextBlock{Text=s,FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),FontSize=20,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,6,0)};
        if(color!=null)glyph.Foreground=BrushOf(color);else glyph.SetResourceReference(TextBlock.ForegroundProperty,"TextFillColorSecondaryBrush");
        return glyph;
    }
    static Button ButtonFor(string title,Action click,string glyph="",bool primary=false)
    {
        var label=new StackPanel{Orientation=Orientation.Horizontal};if(glyph!="")label.Children.Add(Glyph(glyph,primary?"#FFFFFF":null));label.Children.Add(Text(title,14,true));
        var b=new Button{Content=label,Margin=new Thickness(3),HorizontalContentAlignment=HorizontalAlignment.Center};if(primary){b.Background=BrushOf("#1B5945");b.Foreground=Brushes.White;}
        AutomationProperties.SetName(b,title);b.Click+=(_,_)=>click();return b;
    }
    static Border Box(UIElement content,string background="ControlSolidFillColorDefaultBrush"){
        var card=new Border{Child=content,Padding=new Thickness(17),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Margin=new Thickness(4)};
        card.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"TextFillColorPrimaryBrush");
        card.SetResourceReference(Border.BackgroundProperty,background);
        card.SetResourceReference(Border.BorderBrushProperty,"CardStrokeColorDefaultBrush");
        return card;
    }
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
    void PeriodChanged(object s,SelectionChangedEventArgs e){if(!initializing&&!busy&&page!=DirectoryName)ShowPage(page);}
    void ShowPage(string name,Review? review=null,bool goingBack=false)
    {
        if(busy)return;
        if(name is "التحويلات" or TransfersName)name=CurrentTransfers;
        if(name is DeductionsName or EmployeePage or HistoryPage)name=AgencyPage;
        Period.Visibility=name==AgencyPage?Visibility.Hidden:Visibility.Visible;
        CloseKeyboardSearch();
        if(name=="الرئيسية")pageHistory.Clear();
        else if(page!=name && !goingBack)pageHistory.Push(page);
        subpageBack=null;UpdateBackButton();
        query?.Cancel();generation++;debounce.Stop();page=name;
        PageTitle.Text=name switch{AgencyPage=>DeductionsName,TransfersName=>"التحويلات",_=>name};Nav.Children.Clear();CanvasArea.Children.Clear();grid=null;
        bool fitViewport=name is "الرئيسية" or "الإعدادات";
        CanvasArea.MinWidth=fitViewport?0:628;
        CanvasArea.MinHeight=fitViewport?0:400;
        UpdateTransferTabs(name);
        string active=name switch{AgencyPage=>DeductionsName,TransfersName or CurrentTransfers or DeferredTransfers or RefundTransfers=>"التحويلات",_=>name};
        foreach(var n in Sections){var btn=ButtonFor(n.Name,()=>ShowPage(n.Name),n.Icon);btn.Background=BrushOf(active==n.Name?"#D8EEE0":"#19483D");btn.Foreground=active==n.Name?BrushOf("#163E31"):Brushes.White;
            ((TextBlock)((StackPanel)btn.Content).Children[0]).Foreground=active==n.Name?BrushOf("#215740"):BrushOf("#CADFD2");btn.BorderThickness=new Thickness(0);btn.HorizontalContentAlignment=HorizontalAlignment.Right;btn.Margin=new Thickness(0,4,0,2);btn.Padding=new Thickness(12,11,12,11);Nav.Children.Add(btn);}
        AdaptNavigation();
        Status.Text=store.Active==null?"استورد نسخة احتياطية أو ملف بيانات من الإعدادات لبدء العمل.":"تُعرض البيانات من النسخة المحفوظة محلياً";
        if(name=="الرئيسية")homeLoad=Home();else if(name=="الإعدادات")Settings();else if(name==DirectoryName)workPageLoad=ShowAccountDirectory();else if(name=="كشف البنك")Bank();else if(WorkPages.Contains(name)||name is CurrentTransfers or DeferredTransfers or RefundTransfers)workPageLoad=WorkPage(name);else if(name==ArchiveName)Books();else if(name==AgencyPage)DeductionGrid(review);
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
        ShowPage(name);
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
        static string Size(long bytes)
        {
            double value=bytes/1048576d;
            return bytes>0 && value<0.01 ? "أقل من 0.01 ميغابايت" : value.ToString("N2")+" ميغابايت";
        }
        var active=store.Active;
        long archive=active==null?0:new FileInfo(store.Archive).Length;
        var save=ButtonFor("تصدير نسخة",ExportBackup,"\uE74E");
        save.IsEnabled=active!=null;
        var backup=Box(Column(Text("النسخ الاحتياطي",18,true),
            Text(active==null?"لم تُفتح نسخة بعد.":"حجم النسخة "+Size(archive)+"　·　عدد الفترات "+months.Count+"　·　صيغة "+store.Metadata("format"),14),
            Text("صدّر آخر عملك بما فيه الدفعات المحفوظة، ثم استورد الملف على الجهاز الآخر.",14),
            Bar(ButtonFor("استيراد نسخة",()=>_=ImportBackup(),"\uE8E5",true),save)));
        var storage=Column(Text("البيانات على الحاسبة",18,true));
        if(active!=null){
            long lookup=new FileInfo(Path.Combine(active,"lookup.db")).Length;
            var sizes=new Grid();
            sizes.ColumnDefinitions.Add(new(){Width=new(archive,GridUnitType.Star)});sizes.ColumnDefinitions.Add(new(){Width=new(lookup,GridUnitType.Star)});
            var original=new Border{Height=7,Background=BrushOf("#33785C")};var index=new Border{Height=7,Background=BrushOf("#7494AD")};Grid.SetColumn(index,1);sizes.Children.Add(original);sizes.Children.Add(index);
            sizes.Margin=new Thickness(0,10,0,8);
            storage.Children.Add(sizes);
            storage.Children.Add(Bar(Glyph("\uE8B7"),Text("ملف النسخة الاحتياطية　"+Size(archive),14)));
            storage.Children.Add(Bar(Glyph("\uE721"),Text("فهرس البحث المحلي　"+Size(lookup),14)));
            storage.Children.Add(Text("يُنقل الأرشيف عند الحفظ. يُنشأ فهرس البحث محلياً عند فتح النسخة.",14));
        }else{
            storage.Children.Add(Text("تظهر مساحة الأرشيف وفهرس البحث بعد فتح نسخة.",14));
        }
        storage.Children.Add(Bar(ButtonFor("استيراد ملف بيانات",()=>_=ImportDataFile(),"\uE8B5")));
        var deviceLock=((App)Application.Current).DeviceLock;
        var lockTime=Choices(["متوقف","فوراً","بعد دقيقة","بعد 5 دقائق","بعد 15 دقيقة"]);
        AutomationProperties.SetName(lockTime,"قفل التطبيق");
        lockTime.SelectedIndex=Array.IndexOf(DeviceLock.Delays,deviceLock.Delay);
        var lockStatus=Text("فتح برمز الجهاز PIN أو البصمة عبر Windows Hello.",14);
        lockTime.SelectionChanged+=async (_,_)=>{
            if(lockTime.SelectedIndex<0)return;
            int seconds=DeviceLock.Delays[lockTime.SelectedIndex];
            if(seconds==deviceLock.Delay || !lockTime.IsEnabled)return;
            lockTime.IsEnabled=false;
            try{
                string? error=await deviceLock.Change(this,seconds);
                lockStatus.Text=error??(seconds<0?"قفل التطبيق متوقف.":"القفل مفعّل عند الفتح وعند العودة بعد المدة المختارة.");
            }catch(Exception){lockStatus.Text="تعذّر حفظ إعداد القفل على هذا الجهاز.";}
            finally{lockTime.SelectedIndex=Array.IndexOf(DeviceLock.Delays,deviceLock.Delay);lockTime.IsEnabled=true;}
        };
        var updateTitle=Text("تحديث التطبيق",18,true);
        updateTitle.Margin=new Thickness(0,12,0,0);
        var updates=Box(Column(Text("قفل التطبيق",18,true),lockStatus,lockTime,updateTitle,
            Text("الإصدار المثبّت "+WindowsRelease.DisplayVersion(InstalledVersion),14),
            Bar(ButtonFor("التحقق من تحديث",()=>_=CheckForUpdate(true),"\uE895"))));
        var cards=new[]{backup,AppearanceSettings(),Box(storage),updates};
        var layout=new Grid{VerticalAlignment=VerticalAlignment.Top};
        foreach(var card in cards)layout.Children.Add(card);
        void ArrangeCards(double width)
        {
            int columns=width>=650*(1+appearance.Level*.05)?2:1;
            if(layout.ColumnDefinitions.Count==columns)return;
            layout.ColumnDefinitions.Clear();
            layout.RowDefinitions.Clear();
            for(int col=0;col<columns;col++)layout.ColumnDefinitions.Add(new ColumnDefinition());
            for(int i=0;i<cards.Length;i++){
                if(i%columns==0)layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                Grid.SetRow(cards[i],i/columns);
                Grid.SetColumn(cards[i],i%columns);
            }
        }
        ArrangeCards(CanvasArea.ActualWidth);
        layout.SizeChanged+=(_,args)=>ArrangeCards(args.NewSize.Width);
        CanvasArea.Children.Add(new ScrollViewer{Content=layout,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalContentAlignment=HorizontalAlignment.Stretch});
    }
    void ExportBackup()
    {
        if(busy || store.Active==null)return;
        var dialog=new SaveFileDialog{Title="تصدير نسخة",FileName=Store.BackupName,
            Filter="نسخة احتياطية مشتركة (*.zip)|*.zip",DefaultExt=".zip"};
        if(dialog.ShowDialog(this)!=true)return;
        _=Working(()=>store.ExportBackup(dialog.FileName),"صُدّرت النسخة. يمكنك استرجاع هذا الملف على الآيفون أو استيراده على حاسبة أخرى.",()=>ShowPage("الإعدادات"));
    }
    async Task ImportBackup()
    {
        if(busy)return;
        var d=new OpenFileDialog{Filter="نسخة احتياطية مشتركة (*.zip)|*.zip",Title="استيراد نسخة"};
        if(d.ShowDialog(this)!=true)return;
        if(store.Active!=null && MessageBox.Show(this,
            "سيستبدل استيراد «"+Path.GetFileName(d.FileName)+"» بيانات هذه الحاسبة، بما فيها الدفعات المحفوظة. هل تريد المتابعة؟",
            "استيراد نسخة",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        busy=true;IsEnabled=false;try{var progress=new Progress<string>(s=>Status.Text=s);await Task.Run(()=>store.Import(d.FileName,progress));Reload();busy=false;ShowPage("الرئيسية");Status.Text="تم التحقق من النسخة واستيراد بياناتها.";}catch(Exception e){Error(e);}finally{busy=false;IsEnabled=true;}
    }
    void Books()
    {
        var entries=store.Books();var dock=new DockPanel();CanvasArea.Children.Add(dock);
        var picker=Choices(new[]{"كل الأشهر"}.Concat(entries.Select(b=>b.Month).Distinct()));
        var searchBook=new TextBox();
        var add=ButtonFor("إضافة كتاب موقّع",()=>Guard(ImportSigned),"\uE710");
        add.IsEnabled=store.Active!=null && months.Count>0;
        var scan=ButtonFor("مسح كتاب بالسكنر",()=>Guard(()=>{
            if(ArchiveScanner.Scan(this) is {} pdf)ArchiveSigned("كتاب موقّع",()=>pdf);
        }),"\uE8FE");scan.IsEnabled=add.IsEnabled;
        var top=FilterBar(("ابحث عن كتاب أو جهة أو شهر",searchBook,1),("شهر الكتاب",picker,0),("",add,0),("",scan,0));
        DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var list=GridView(("اسم الكتاب","Title",2.3),("الجهة","Agency",1.5),("الشهر","Month",1),("الصفحات","Pages",.6));
        var save=ButtonFor("حفظ PDF الأصلي",()=>Guard(()=>{
            if(list.SelectedItem is not ArchivedBook book)return;
            var name=string.Concat(book.Title.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?' ':c)).Trim();
            var dialog=new SaveFileDialog{Filter="ملف الكتاب|*.pdf",FileName=name};
            if(dialog.ShowDialog(this)==true){store.SaveBook(book,dialog.FileName);Status.Text="تم حفظ ملف الكتاب الأصلي.";}
        }),"\uE74E",true);
        bool opening=false;
        var preview=ButtonFor("معاينة الكتاب",()=>{},"\uE890");
        preview.Click+=async (_,_)=>{
            if(opening || list.SelectedItem is not ArchivedBook book)return;
            opening=true;preview.IsEnabled=false;
            var revision=store.Active;
            try{
                var bytes=await Task.Run(()=>store.ReadBook(book));
                if(preview.IsLoaded && store.Active==revision && Equals(list.SelectedItem,book))
                    new LetterPreview(bytes,book.Title,printAndSave:true){Owner=this}.ShowDialog();
            }catch(Exception ex){if(preview.IsLoaded)Error(ex);}
            finally{opening=false;preview.IsEnabled=list.SelectedItem is ArchivedBook;}
        };
        list.MouseDoubleClick+=(_,e)=>{
            if(e.OriginalSource is DependencyObject hit && ItemsControl.ContainerFromElement(list,hit) is DataGridRow)
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        var hint=Text("",14);var bottom=Box(Bar(hint,preview,save));DockPanel.SetDock(bottom,Dock.Bottom);dock.Children.Add(bottom);
        dock.Children.Add(list);save.IsEnabled=preview.IsEnabled=false;
        void Draw(){
            string term=Arabic.Match(searchBook.Text);
            var rows=entries.Where(b=>(picker.SelectedIndex==0||b.Month==(string?)picker.SelectedItem) &&
                (term.Length==0||Arabic.Match(b.Title+" "+b.Month+" "+b.Agency).Contains(term))).ToList();
            list.ItemsSource=rows;hint.Text=rows.Count==0?"لا توجد كتب ضمن هذا الاختيار.":rows.Count+" كتاب محفوظ";
        }
        searchBook.TextChanged+=(_,_)=>Draw();
        list.SelectionChanged+=(_,_)=>{save.IsEnabled=list.SelectedItem is ArchivedBook;preview.IsEnabled=save.IsEnabled && !opening;};
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
        async Task CheckPreview(string caption="معاينة الكشف",bool refund=false){
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
                        var enabled=Controls<Button>((FrameworkElement)preview.Content).Where(b=>b.IsEnabled).Select(b=>b.Content).ToArray();
                        opened=enabled.Contains(refund?"حفظ PDF":"تصدير PDF") && (!refund || enabled.Contains("طباعة"));
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
                Click(CanvasArea,caption);
                if(!await finished.Task.WaitAsync(TimeSpan.FromSeconds(45)))throw new InvalidDataException("تعذّرت "+caption);
            }finally{probe.Stop();}
        }
        void CheckBackPosition(bool enabled){
            UpdateLayout();
            var backLeft=BackButton.PointToScreen(new Point());
            var backRight=BackButton.PointToScreen(new Point(BackButton.ActualWidth,0));
            var sideLeft=SidePanel.PointToScreen(new Point());
            var sideRight=SidePanel.PointToScreen(new Point(SidePanel.ActualWidth,0));
            double gap=(Math.Min(sideLeft.X,sideRight.X)-Math.Max(backLeft.X,backRight.X))/VisualTreeHelper.GetDpi(this).DpiScaleX;
            if(!BackButton.IsVisible || BackButton.IsEnabled!=enabled || Math.Abs(BackButton.ActualWidth-40)>1 || Math.Abs(gap-8)>1)
                throw new InvalidDataException("تغيّر موضع زر الرجوع أو حالته في شريط العنوان.");
        }
        async Task ChooseMonth(int key){
            Period.ApplyTemplate();
            UpdateLayout();
            var opener=(System.Windows.Controls.Primitives.ToggleButton)Period.Template.FindName("FieldToggle",Period);
            if(Math.Abs(opener.ActualHeight-BackButton.ActualHeight)>1)throw new InvalidDataException($"ارتفاع زر الشهر ({opener.ActualHeight}) لا يطابق زر الرجوع ({BackButton.ActualHeight}).");
            opener.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
            if(!Period.IsDropDownOpen)throw new InvalidDataException("تعذر فتح قائمة الأشهر.");
            var month=months.Single(m=>m.Key==key);
            Period.SelectedItem=month;Period.IsDropDownOpen=false;
            if(SelectedMonth!=month)throw new InvalidDataException("تعذر تغيير شهر العمل.");
            await workPageLoad;
        }
        foreach(var (name,file) in new[]{("الرئيسية","home"),(DeductionsName,"deductions"),(EmployeePage,"employee-search"),("التحويلات","transfers"),("كشف البنك","bank"),("الإعدادات","settings"),(ArchiveName,"archive"),(CurrentTransfers,"transfer-files"),(FundingName,"funding"),(AuditName,"audit"),(SearchName,"search"),("إعادة المبالغ","refunds"),(TrustName,"trusts"),(DeferredTransfers,"deferred-transfers"),(HealthName,"health"),(FundName,"fund")}){
            if(name==DeductionsName){
                var order=Nav.Children.OfType<Button>().Select(AutomationProperties.GetName).ToList();
                if(order.IndexOf(ArchiveName)!=order.IndexOf("الإعدادات")-1 || order.IndexOf(DeductionsName)!=1)throw new InvalidDataException("ترتيب قسم الاستقطاعات غير صحيح.");
                Click(Nav,DeductionsName);
                CheckBackPosition(true);
                var today=DateTime.Today;
                if(Current().Year!=today.Year || Current().Month!=today.Month || Current().Agency!="")
                    throw new InvalidDataException("لم يفتح السجل على شهر الحاسبة وكل الجهات.");
                year!.SelectedItem=(sample.Period/100).ToString();monthFilter!.SelectedIndex=sample.Period%100;
                agency!.SelectedItem=sample.Agency;debounce.Stop();await LoadGrid();
                if(search==null||grid!.Items.Count==0||grid.Items.Cast<Deduction>().Any(r=>r.Agency!=sample.Agency))throw new InvalidDataException("تصفية القائمة لا تطابق الجهة المختارة.");
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
                if(page!=AgencyPage||Current().Month!=sample.Period%100)throw new InvalidDataException("تعذّر فتح استقطاعات الشهر.");
            }else if(name==EmployeePage){
                search!.Text=sample.Name;
                debounce.Stop();await LoadGrid();
                if(grid!.Items.Count==0||grid.Items.Cast<Deduction>().Any(r=>r.Name!=sample.Name))throw new InvalidDataException("نتائج بحث المنتسب غير مطابقة.");
                grid.SelectedIndex=0;
                if(grid.SelectedItem is not Deduction picked)throw new InvalidDataException("تعذّر اختيار قيد المنتسب.");
                Exception? dialogError=null;
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
                    var dialog=OwnedWindows.OfType<Window>().Single(w=>w.Title=="كشف استقطاع منتسب");
                    try{
                        ComboBox Picker(string label)=>Controls<ComboBox>(dialog).Single(c=>AutomationProperties.GetName(c)==label);
                        var from=Picker("من شهر");var to=Picker("إلى شهر");
                        var expected=store.Find(new(Person:picked.Name,Agency:picked.Agency,Reference:picked.Reference,ExactReference:true))
                            .Select(r=>r.Period).Where(k=>k>0).Distinct().Order().ToArray();
                        if(!from.Items.Cast<MonthInfo>().Select(m=>m.Key).SequenceEqual(expected) ||
                            !to.Items.Cast<MonthInfo>().Select(m=>m.Key).SequenceEqual(expected) ||
                            (from.SelectedItem as MonthInfo)?.Key!=expected.First() || (to.SelectedItem as MonthInfo)?.Key!=expected.Last())
                            throw new InvalidDataException("أشهر كشف المنتسب لا تطابق قيود الجهة والإضبارة.");
                    }catch(Exception ex){dialogError=ex;}
                    finally{dialog.Close();}
                }));
                OpenEmployee(picked);
                if(dialogError!=null)throw dialogError;
            }else if(name=="التحويلات"){
                Click(Nav,name);await workPageLoad;
                var names=Controls<Button>(TransferTabs).Select(AutomationProperties.GetName).ToArray();
                if(page!=CurrentTransfers||!names.SequenceEqual(new[]{CurrentTransfers,DeferredTransfers,RefundTransfers})||!Controls<DataGrid>(CanvasArea).Any())throw new InvalidDataException("لم تفتح الدفعة الشاملة مع جدول الشهر وتبويبات التحويلات.");
            }else if(name is CurrentTransfers or DeferredTransfers){
                ShowPage(TransfersName);Click(TransferTabs,name);await workPageLoad;
                if(name==CurrentTransfers){
                    await ChooseMonth(202610);
                    var group=Controls<ComboBox>(CanvasArea).Single(c=>AutomationProperties.GetName(c)=="المجموعة");
                    group.SelectedIndex=(int)SalaryDesktop.Core.TransferGroup.Rafidain;
                    UpdateLayout();
                    var date=Controls<DatePicker>(CanvasArea).Single();
                    var exportButton=FindAction(CanvasArea,"الكتاب وملف CSV");
                    var options=FindAction(CanvasArea,"خيارات");
                    var y=group.TranslatePoint(new Point(),CanvasArea).Y;
                    if(new Control[]{date,exportButton,options}.Any(c=>Math.Abs(c.TranslatePoint(new Point(),CanvasArea).Y-y)>1 || Math.Abs(c.ActualHeight-group.ActualHeight)>1))
                        throw new InvalidDataException("حقول التحويل وأوامره ليست في صف واحد.");
                    date.IsDropDownOpen=true;UpdateLayout();
                    if(date.Template.FindName("PART_Popup",date) is not System.Windows.Controls.Primitives.Popup {IsOpen:true})
                        throw new InvalidDataException("تعذّر فتح تقويم التحويل.");
                    date.IsDropDownOpen=false;
                    await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    OpenKeyboardSearch();
                    CheckBackPosition(true);
                    if(!SearchIsOpen)throw new InvalidDataException("تعذّر فتح البحث من الدفعة.");
                    BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if(SearchIsOpen || !group.IsVisible || page!=CurrentTransfers)throw new InvalidDataException("تعذّر الرجوع إلى الدفعة من البحث.");
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
                        var menu=row.ContextMenu;
                        if(menu?.IsOpen!=true || menu.PlacementTarget!=row)
                            throw new InvalidDataException("تعذّر فتح أوامر كتاب "+item.Book!.Id+" من صفه بنقرة يسرى.");
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
                    if(FindAction(CanvasArea,"تأجيل استقطاعات العقود").IsEnabled && !Controls<DataGrid>(CanvasArea).Any(c=>AutomationProperties.GetName(c)=="الكتب المتأثرة بفصل العقود"))throw new InvalidDataException("تعذرت مراجعة الكتب قبل اعتماد الفصل.");
                    await ChooseMonth(202611);
                }
            }else if(name=="إعادة المبالغ"){
                ShowPage(TransfersName);Click(TransferTabs,RefundTransfers);await workPageLoad;
                var saved=store.Refunds().First(b=>b.ArchiveId!="draft");
                var picker=Controls<ComboBox>(CanvasArea).Single(c=>AutomationProperties.GetName(c)=="الدفعات المحفوظة");
                if(picker.SelectedIndex!=0)throw new InvalidDataException("لم تفتح أحدث دفعة محفوظة.");
                var banks=saved.Groups();
                var commands=Controls<Button>(CanvasArea).Select(AutomationProperties.GetName).ToArray();
                var letters=commands.Where(n=>n.StartsWith("معاينة كتاب إعادة مبالغ")).ToArray();
                var roster=Controls<DataGrid>(CanvasArea).Single(t=>AutomationProperties.GetName(t)=="منتسبو الدفعة");
                if(banks.Any(bank=>!commands.Contains("تصدير CSV · "+bank.Name+" ("+bank.Code+")")) ||
                    letters.Length!=banks.GroupBy(g=>g.Code=="RAFB").Count() || roster.Items.Count!=saved.People.Count ||
                    commands.Count(n=>n.StartsWith("تصدير CSV"))!=banks.Count)
                    throw new InvalidDataException("ملفات الدفعة أو جدول المنتسبين لا يطابقان الدفعة المحفوظة.");
                foreach(var letter in letters)await CheckPreview(letter,refund:true);

            }else{
                if(name is HealthName or FundName){
                    initializing=true;Period.SelectedItem=months.Single(m=>m.Key==202610);initializing=false;
                }
                ShowPage(name);
            }
            if(name=="الإعدادات"){
                UpdateLayout();
                var settingsView=CanvasArea.Children.OfType<ScrollViewer>().Single();
                var cards=(Grid)settingsView.Content;
                if(cards.Children.Count!=4 || (CanvasArea.ActualWidth>=820 && CanvasArea.ActualHeight>=500 &&
                    (cards.ColumnDefinitions.Count!=2 || settingsView.ScrollableHeight>1 || settingsView.ScrollableWidth>1)))
                    throw new InvalidDataException("لم تظهر أقسام الإعدادات معاً ضمن مساحة الشاشة الكبيرة.");
                foreach(var action in new[]{"استيراد نسخة","تصدير نسخة","استيراد ملف بيانات","التحقق من تحديث"})
                    FindAction(cards,action);
            }
            if(name=="الرئيسية"){
                await homeLoad;
                await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
                List<Button> HomeButtons(string prefix)=>Controls<Button>(CanvasArea)
                    .Where(button=>AutomationProperties.GetAutomationId(button).StartsWith(prefix,StringComparison.Ordinal)).ToList();
                void CheckHomeFit(){
                    CheckBackPosition(false);
                    var visiblePins=HomeButtons("HomePin-");
                    if(BodyViewport.ScrollableHeight>1 || BodyViewport.ScrollableWidth>1 ||
                        visiblePins.Count>8 || (pins.Count>0 && visiblePins.Count==0))
                        throw new InvalidDataException("لم تتكيف الرئيسية مع مساحة النافذة.");
                    foreach(var control in visiblePins.Concat(HomeButtons("HomeReview-"))){
                        var bounds=control.TransformToAncestor(CanvasArea).TransformBounds(new Rect(control.RenderSize));
                        if(bounds.Top< -1 || bounds.Bottom>CanvasArea.ActualHeight+1 || bounds.Height<16 ||
                           bounds.Left< -1 || bounds.Right>CanvasArea.ActualWidth+1)
                            throw new InvalidDataException("خرجت إحدى بطاقات الرئيسية عن المساحة المتاحة.");
                    }
                }
                CheckHomeFit();
                void CheckReviewCards(){
                    foreach(var section in new[]{"الاستقطاعات","الرواتب"}){
                        var next=FindAction(CanvasArea,"التالي · "+section);
                        var previous=FindAction(CanvasArea,"السابق · "+section);
                        if(next.Visibility!=previous.Visibility)throw new InvalidDataException("سهم مفقود في بطاقة "+section);
                        if(next.Visibility!=Visibility.Visible)continue;
                        var card=(Grid)next.Parent;
                        var subject=Controls<Button>(card).Single(b=>AutomationProperties.GetAutomationId(b).StartsWith("HomeReview-"));
                        string Current()=>AutomationProperties.GetAutomationId(subject);
                        string first=Current();
                        var seen=new HashSet<string>{first};
                        do{
                            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            if(Current()==first)break;
                            if(!seen.Add(Current()))throw new InvalidDataException("تكررت حالة قبل إكمال دورة المراجعة.");
                        }while(true);
                        if(seen.Count<2)throw new InvalidDataException("لم يبدّل سهم المراجعة الحالة.");
                        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if(Current()==first)throw new InvalidDataException("تعذر الرجوع إلى الحالة السابقة.");
                        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if(Current()!=first)throw new InvalidDataException("تعذر الرجوع إلى بداية الدورة.");
                    }
                }
                CheckReviewCards();
                var originalWidth=Width;var originalHeight=Height;
                var originalAppearance=appearance;
                try{
                    appearance=appearance with {Level=4};appearance.Apply();
                    Width=560;Height=390;ShowPage("الرئيسية");await homeLoad;
                    await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
                    CheckHomeFit();
                    CheckReviewCards();
                }finally{
                    appearance=originalAppearance;appearance.Apply();
                    Width=originalWidth;Height=originalHeight;ShowPage("الرئيسية");await homeLoad;
                    await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
                    CheckReviewCards();
                }
            }
            if(name=="الإعدادات"){
                FindAction(CanvasArea,"استيراد ملف بيانات");
                ComboBox Setting(string name)=>Controls<ComboBox>(CanvasArea).Single(c=>AutomationProperties.GetName(c)==name);
                string statementMonth=months.Single(m=>m.Key==202610).Name;
                var statement=WorkReports.Command(store.Snapshot(statementMonth),statementMonth);
                var originalPreview=statement.Html();
                var themeAtStart=Setting("ثيم الواجهة").SelectedIndex;
                Setting("ثيم الواجهة").SelectedIndex=2;
                if(store.ReadWorkPreferences().GetValueOrDefault("appAppearance")!="dark")
                    throw new InvalidDataException("تعذّر حفظ الثيم في النسخة.");
                async Task CheckFieldTheme(){
                    var picker=Setting("ثيم الواجهة");
                    picker.ApplyTemplate();Period.ApplyTemplate();
                    if(!ReferenceEquals(picker.Template,Period.Template))
                        throw new InvalidDataException("قائمة الإعدادات لا تستخدم قالب حقل الشهر.");
                    static double Light(Brush brush){
                        var c=((SolidColorBrush)brush).Color;
                        static double Linear(byte b){double s=b/255d;return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);}
                        return .2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);
                    }
                    static void Readable(Brush text,Brush surface){
                        var a=Light(text);var b=Light(surface);
                        if((Math.Max(a,b)+.05)/(Math.Min(a,b)+.05)<4.5)
                            throw new InvalidDataException("تباين النص والخلفية في حقل الاختيار ضعيف.");
                    }
                    var field=(Border)picker.Template.FindName("Surface",picker);
                    Readable(picker.Foreground,field.Background);
                    picker.IsDropDownOpen=true;
                    try{
                        await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
                        var selected=(ComboBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(picker.SelectedIndex);
                        selected.ApplyTemplate();
                        var surface=(Border)selected.Template.FindName("ItemSurface",selected);
                        Readable(selected.Foreground,surface.Background);
                    }finally{picker.IsDropDownOpen=false;}
                }
                await CheckFieldTheme();
                Setting("حجم خط المحتوى").SelectedIndex=4;Setting("شكل أرقام الواجهة").SelectedIndex=1;
                var sampleText=Controls<TextBlock>(CanvasArea).FirstOrDefault(t=>t.Text.Contains("١٢,٥٠٠"));
                if(sampleText==null||Math.Abs(sampleText.FontSize-19.2)>.1||store.ReadWorkPreferences().GetValueOrDefault("windowsContentTextLevel")!="4" || store.ReadWorkPreferences().GetValueOrDefault("appNumerals")!="arabicIndic")
                    throw new InvalidDataException("تعذّر تطبيق وحفظ حجم الخط والأرقام.");
                var options=Setting("شكل أرقام الواجهة");
                if(!options.Items.Cast<string>().SequenceEqual(new[]{"إنجليزية (123)","هندية (١٢٣)"}))
                    throw new InvalidDataException("تطابق مثالا الأرقام في القائمة.");
                if(statement.Html()!=originalPreview)throw new InvalidDataException("تغيّرت أرقام كشف القيادة عند تبديل أرقام الواجهة.");
                Setting("ثيم الواجهة").SelectedIndex=1;
                await CheckFieldTheme();
                Setting("ثيم الواجهة").SelectedIndex=themeAtStart;Setting("حجم خط المحتوى").SelectedIndex=0;Setting("شكل أرقام الواجهة").SelectedIndex=0;
                if(!Controls<TextBlock>(CanvasArea).Any(t=>t.Text.Contains("12,500")))throw new InvalidDataException("تعذر استرجاع الأرقام العربية.");
            }
            if(name=="كشف البنك"){
                await bankLoad;
                if(bankLedgerSelected)throw new InvalidDataException("لم يفتح كشف الشهر أولاً.");
                UpdateLayout();
                CaptureWindow((FrameworkElement)Content,"bank-month");
                await CheckPreview();
                Click(CanvasArea,BankLedger);
                await bankLoad;
                await CheckPreview();
            }
            if(grid!=null&&name!=EmployeePage)await LoadGrid();
            await Task.Delay(280);await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.ApplicationIdle);
            if(name is DeductionsName or EmployeePage or CurrentTransfers or DeferredTransfers or "كشف البنك" or AuditName or SearchName or TrustName or ArchiveName or HealthName or FundName){
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
                            var inputs=row.Children.OfType<StackPanel>().Where(field=>field.Children.Count==2 && field.Children[0] is TextBlock).Select(field=>(Control)field.Children[1]);
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
        await OpenPerson(hit,target=>CaptureWindow(target,"employee-profile"));
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
