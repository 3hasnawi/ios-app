using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

internal sealed class LetterWindow:Window
{
    readonly A.LetterTemplate initial;
    readonly Dictionary<string,TextBox> fields=new();
    readonly Dictionary<string,ComboBox> options=new();
    readonly Dictionary<string,int> sizes;
    bool kashidaChanged;
    readonly CheckBox bold=new(){Content="متن عريض"};
    readonly TextBlock error=new(){Foreground=Brushes.Firebrick,TextWrapping=TextWrapping.Wrap};
    StackPanel page=null!;
    readonly TabControl tabs=new(){Margin=new Thickness(12)};
    void Tab(string title){page=new StackPanel{Margin=new Thickness(18,4,18,16)};tabs.Items.Add(new TabItem{Header=title,Content=new ScrollViewer{Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});}
    public string PreviewCaption {get;set;}="معاينة الكتاب";
    public A.LetterTemplate? Result {get;private set;}

    public LetterWindow(A.LetterTemplate template,string kind,A.BackupSnapshot snapshot,string month,bool accountEnabled,Action<A.LetterTemplate> validate,Func<A.LetterTemplate,byte[]> preview){
        initial=template;sizes=new(template.FontSizes??[]);Title="قالب الكتاب";Width=810;Height=740;MinHeight=460;MinWidth=560;WindowStartupLocation=WindowStartupLocation.CenterOwner;FlowDirection=FlowDirection.RightToLeft;
        var root=new DockPanel();Content=root;
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(12,6,12,6)};
        DockPanel.SetDock(actions,Dock.Top);root.Children.Add(actions);
        void Action(string name,Action run){var b=new Button{Content=name,Margin=new Thickness(4),Padding=new Thickness(14,5,14,5)};b.Click+=(_,_)=>{try{error.Text="";run();}catch(Exception e){error.Text=e.Message;}};actions.Children.Add(b);}
        Action("حفظ",()=>{var t=Read();validate(t);Result=t;DialogResult=true;});
        var showPreview=new Button{Content="معاينة الكتاب",Margin=new Thickness(4),Padding=new Thickness(14,5,14,5)};
        actions.Children.Add(showPreview);
        showPreview.Click+=async (_,_)=>{
            try{
                var t=Read();IsEnabled=false;error.Text="جارٍ إعداد المعاينة…";
                var pdf=await Task.Run(()=>preview(t));
                if(IsLoaded){error.Text="";IsEnabled=true;new LetterPreview(pdf){Owner=this,Title=PreviewCaption}.ShowDialog();}
            }catch(Exception e){error.Text=e.Message;}finally{IsEnabled=true;}
        };
        Action("إلغاء",()=>DialogResult=false);
        error.Margin=new Thickness(16,4,16,6);DockPanel.SetDock(error,Dock.Bottom);root.Children.Add(error);
        root.Children.Add(tabs);Tab("بيانات الكتاب");
        bool custom=kind=="custom",refund=kind=="refund";
        Field("title","اسم القالب",template.Title,locked:refund);
        Field("header","الترويسة",template.Header,true);Field("number","العدد",template.Number,locked:refund);
        Field("recipient","إلى",template.Recipient);Field("subject","الموضوع",template.Subject,locked:refund);
        Tab("المتن والتنسيق");var body=Field("body","نص الكتاب",template.Body,true,refund);
        if(!refund){
            string[] tokens=custom?["المبلغ","المبلغ كتابة","الحساب","الشهر","السنة"]:System.Text.RegularExpressions.Regex.Matches(template.Body,@"\{\{([^{}]+)\}\}").Select(m=>m.Groups[1].Value).Distinct().ToArray();
            var items=Pick("token","إضافة حقل إلى المتن",["اختر الحقل",..tokens],"اختر الحقل");
            items.SelectionChanged+=(_,_)=>{if(items.SelectedIndex<=0)return;string value="{{"+items.SelectedItem+"}}";int at=body.SelectionStart;body.SelectedText=value;body.Select(at+value.Length,0);items.SelectedIndex=0;body.Focus();};
        }
        var k=Pick("kashida","الكشيدة",["ضبط","صغيرة","متوسطة","كبيرة"],template.BodyKashida switch{"low"=>"صغيرة","medium"=>"متوسطة","high"=>"كبيرة",_=>"ضبط"});
        k.SelectionChanged+=(_,_)=>kashidaChanged=true;
        Pick("spacing","تباعد أسطر المتن",["صغير","متوسط","كبير"],template.BodySpacing switch{"compact"=>"صغير","wide"=>"كبير",_=>"متوسط"});
        bold.IsChecked=template.BodyBold??false;page.Children.Add(bold);
        var parts=Pick("part","حجم الخط",["الترويسة","العدد والتاريخ","الجهة","الموضوع","التحية","المتن","عبارة الختام","المرفقات","التواقيع","نسخة منه إلى","جدول الكتاب"],"المتن");
        var pts=Pick("size","الحجم بالنقاط",["حسب النموذج",..Enumerable.Range(8,17).Select(n=>n.ToString())],"حسب النموذج");
        bool updating=false;
        void LoadSize(){updating=true;pts.SelectedIndex=sizes.TryGetValue(A.LetterTemplate.Parts[parts.SelectedIndex],out var size)?size-7:0;updating=false;}
        LoadSize();parts.SelectionChanged+=(_,_)=>LoadSize();pts.SelectionChanged+=(_,_)=>{if(updating)return;string part=A.LetterTemplate.Parts[parts.SelectedIndex];if(pts.SelectedIndex==0)sizes.Remove(part);else sizes[part]=pts.SelectedIndex+7;};
        Field("attachments","المرفقات",template.Attachments??"",true,refund);
        if(custom||accountEnabled||kind is "food_tax" or "food_company"||!refund&&!kind.StartsWith("treasury_"))Tab("الربط والتواريخ");
        if(custom){
            string[] sources=["بدون ربط","مبلغ يدوي","بند رواتب","جهة تنفيذ"];
            var source=Pick("source","مصدر المبلغ",sources,template.Source switch{"manual"=>sources[1],"payroll"=>sources[2],"execution"=>sources[3],_=>sources[0]});
            var item=Pick("sourceName","البند أو الجهة",[],null);
            string[]? payroll=null,entities=null;
            void Bind(string? selected){var names=source.SelectedIndex switch{2=>payroll??=A.TemplateLetterPdf.Items(snapshot,month),3=>entities??=A.PayrollAnalysis.Groups(snapshot,month).Select(g=>g.Title).Order().ToArray(),_=>Array.Empty<string>()};item.ItemsSource=names.Concat(string.IsNullOrEmpty(selected)?[]:[selected]).Distinct().ToArray();item.SelectedItem=selected;item.IsEnabled=source.SelectedIndex>=2;}
            Bind(template.SourceName);source.SelectionChanged+=(_,_)=>Bind(null);
        }
        if(accountEnabled){
            var account=Field("account","حساب المستفيد",template.Account);
            var search=Field("find","بحث في الحسابات المحفوظة","");
            var picker=Pick("accounts","اختيار حساب محفوظ",[],null);
            var accounts=snapshot.Accounts.SelectMany(a=>new[]{a.Iban,a.LocalAccount}.Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct().Select(n=>new KeyValuePair<string,string>(a.Entity+" · "+a.Branch+" · "+n,n!))).ToArray();
            picker.DisplayMemberPath="Key";picker.ItemsSource=accounts;
            search.TextChanged+=(_,_)=>picker.ItemsSource=accounts.Where(a=>A.PayrollAnalysis.Normalize(a.Key).Contains(A.PayrollAnalysis.Normalize(search.Text))).ToArray();
            picker.SelectionChanged+=(_,_)=>{if(picker.SelectedItem is KeyValuePair<string,string> entry)account.Text=entry.Value;};
        }
        if(kind is "food_company" or "food_tax")Field("food","رقم وتاريخ عقد الإطعام",template.FoodContractReference??"");
        if(!custom&&!refund&&!kind.StartsWith("treasury_")){
            Field("bodyMonth","شهر المتن (اختياري)",template.BodyMonth??"");Field("outgoing","شهر الصادر (١ إلى ١٢)",template.OutgoingMonth??"");
        }
        Tab("اللجنة والتبليغ");Field("first","التوقيع الأول",template.FirstSigner,true);Field("second","التوقيع الثاني",template.SecondSigner,true);Field("third","التوقيع الثالث",template.ThirdSigner,true);
        page.Children.Add(new TextBlock{Text="لكل عضو اكتب العنوان الوظيفي والاسم والمنصب في ثلاثة أسطر. يُدرج التاريخ تلقائياً.",TextWrapping=TextWrapping.Wrap});
        Position("vertical","موضع التواقيع العمودي (+ للأسفل)",60,template.SignatureVerticalOffset??0);
        Position("horizontal","موضع التواقيع الأفقي (+ لليمين)",30,template.SignatureHorizontalOffset??0);
        var reset=new Button{Content="إعادة موضع التواقيع للأصل",Margin=new Thickness(0,6,0,0)};
        reset.Click+=(_,_)=>{options["horizontal"].SelectedItem="0";options["vertical"].SelectedItem="0";};page.Children.Add(reset);
        Field("footer","نسخة منه إلى",template.Footer,true,refund);
    }
    void Caption(string name)=>page.Children.Add(new TextBlock{Text=name,FontWeight=FontWeights.Bold,FontSize=19,Margin=new Thickness(0,12,0,4)});
    void Label(string name)=>page.Children.Add(new TextBlock{Text=name,Margin=new Thickness(0,7,0,2)});
    TextBox Field(string key,string name,string value,bool multi=false,bool locked=false){Label(name);var box=new TextBox{Text=value,IsReadOnly=locked,AcceptsReturn=multi,TextWrapping=TextWrapping.Wrap,MinLines=multi?2:1,Padding=new Thickness(5)};page.Children.Add(box);fields.Add(key,box);return box;}
    ComboBox Pick(string key,string label,string[] choices,string? selected){Label(label);var picker=new ComboBox{ItemsSource=choices,SelectedItem=selected};page.Children.Add(picker);options.Add(key,picker);return picker;}
    void Position(string key,string label,int max,int current){var values=Enumerable.Range(0,max*2/5+1).Select(n=>n*5-max).Append(current).Distinct().Order().Select(n=>n.ToString()).ToArray();Pick(key,label,values,current.ToString());}
    A.LetterTemplate Read(){
        string V(string k,string? fallback="")=>fields.TryGetValue(k,out var b)?b.Text.ReplaceLineEndings("\n").Trim():fallback??"";
        int Index(string k)=>options[k].SelectedIndex;
        string Choice(string k)=>options[k].SelectedItem?.ToString()??"";
        string kashida=new[]{"none","low","medium","high"}[Index("kashida")];
        return initial with{Title=V("title"),Header=V("header"),Number=V("number"),Recipient=V("recipient"),Subject=V("subject"),Body=V("body"),Footer=V("footer"),
            FirstSigner=V("first"),SecondSigner=V("second"),ThirdSigner=V("third"),Attachments=V("attachments"),Account=V("account",initial.Account),
            BodyKashida=kashidaChanged?kashida:initial.BodyKashida,BodyAlignment=kashidaChanged?"justified":initial.BodyAlignment,BodySpacing=new[]{"compact","medium","wide"}[Index("spacing")],BodyBold=bold.IsChecked,
            FontSizes=new(sizes),SignatureVerticalOffset=int.Parse(Choice("vertical")),SignatureHorizontalOffset=int.Parse(Choice("horizontal")),
            Source=options.ContainsKey("source")?new[]{"none","manual","payroll","execution"}[Index("source")]:initial.Source,SourceName=options.ContainsKey("sourceName")?Choice("sourceName"):initial.SourceName,
            FoodContractReference=V("food",initial.FoodContractReference),BodyMonth=V("bodyMonth",initial.BodyMonth),OutgoingMonth=V("outgoing",initial.OutgoingMonth)};
    }
}

internal sealed class LetterPreview:Window
{
    readonly TaskCompletionSource<bool> loaded=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<bool> Ready=>loaded.Task;
    public LetterPreview(byte[] pdf,string? statementTitle=null,bool exportLetter=false){
        Title=statementTitle==null?"معاينة الكتاب":"معاينة "+statementTitle;Width=850;Height=740;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new DockPanel();Content=panel;
        var edit=new Button{Content=statementTitle==null?"✎  العودة إلى التحرير":"إغلاق المعاينة",HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(4)};
        edit.Click+=(_,_)=>Close();
        var toolbar=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,FlowDirection=FlowDirection.RightToLeft};
        toolbar.Children.Add(edit);DockPanel.SetDock(toolbar,Dock.Top);panel.Children.Add(toolbar);
        Button? save=null;
        if(statementTitle!=null){
            save=new Button{Content=exportLetter?"تصدير الكتاب":"تصدير PDF",IsEnabled=false,Margin=new Thickness(4)};
            toolbar.Children.Add(save);
            save.Click+=async (_,_)=>{
                if(exportLetter){DialogResult=true;return;}
                var dialog=new Microsoft.Win32.SaveFileDialog{FileName=A.ReportSheet.FileName(statementTitle),Filter="PDF|*.pdf",DefaultExt=".pdf"};
                if(dialog.ShowDialog(this)!=true)return;
                IsEnabled=false;
                try{await Task.Run(()=>A.ReportFiles.Save(pdf,dialog.FileName,".pdf"));}
                catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}
                finally{IsEnabled=true;}
            };
        }
        var viewer=new WebView2();panel.Children.Add(viewer);bool closed=false;
        Closed+=(_,_)=>{closed=true;loaded.TrySetResult(false);viewer.Dispose();};
        Loaded+=async (_,_)=>{
            try{
                var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AnbarPayroll","WebView");
                var env=await CoreWebView2Environment.CreateAsync(userDataFolder:profile);if(closed)return;
                await viewer.EnsureCoreWebView2Async(env);if(closed)return;
                const string url="https://preview.invalid/letter.pdf";
                var web=viewer.CoreWebView2;web.Settings.AreDevToolsEnabled=false;
                web.AddWebResourceRequestedFilter(url,CoreWebView2WebResourceContext.All);
                web.WebResourceRequested+=(_,e)=>e.Response=env.CreateWebResourceResponse(new MemoryStream(pdf,false),200,"OK","Content-Type: application/pdf\r\nCache-Control: no-store");
                web.NavigationStarting+=(_,e)=>e.Cancel=e.Uri!=url;
                web.NewWindowRequested+=(_,e)=>e.Handled=true;
                web.NavigationCompleted+=(_,e)=>{
                    if(save!=null)save.IsEnabled=e.IsSuccess;
                    if(e.IsSuccess)loaded.TrySetResult(true);
                    else loaded.TrySetException(new IOException("تعذّرت معاينة المستند: "+e.WebErrorStatus));
                };
                web.Navigate(url);
            }catch(Exception e){loaded.TrySetException(e);if(!closed){panel.Children.Remove(viewer);panel.Children.Add(new TextBlock{Text=e.Message,TextWrapping=TextWrapping.Wrap});}}
        };
    }
}
