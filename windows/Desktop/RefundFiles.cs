using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    async Task OpenRefundEditor(A.BackupSnapshot source,string month,RefundBatch? editing)
    {
        A.BackupSnapshot? payroll=null;
        await Working(()=>{
            if(store.Active!=Path.GetDirectoryName(source.SourcePath))throw new IOException("تغيّرت النسخة. أعد فتح الدفعة.");
            payroll=store.Snapshot(month);
        },"",()=>RefundDesk(payroll!,month,editing));
    }

    void RefundHome(A.BackupSnapshot source,string month,List<RefundBatch> history)
    {
        var draft=history.FirstOrDefault(b=>b.ArchiveId=="draft");
        var saved=history.Where(b=>b.ArchiveId!="draft").ToList();
        CanvasArea.Children.Clear();
        PageTitle.Text=RefundTransfers;
        var choices=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};
        void Section(string name,string detail,string icon,Action open){
            var button=ButtonFor(name,open);
            button.Content=Column(Bar(Glyph(icon),Text(name,19,true)),Text(detail,14));
            button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            button.Padding=new Thickness(16);button.Margin=new Thickness(4);
            choices.Children.Add(button);
        }
        Section("دفعة جديدة",draft==null?"اختيار المنتسبين وإدخال المبالغ":"متابعة المسودة الحالية","\uE710",()=>_=OpenRefundEditor(source,month,draft));
        Section("الدفعات المحفوظة",saved.Count+" دفعة　·　الكتب وملفات المصارف","\uE8B7",()=>RefundHistory(source,month,saved));
        CanvasArea.Children.Add(Column(choices));
    }

    void RefundHistory(A.BackupSnapshot source,string month,IReadOnlyList<RefundBatch> batches)
    {
        CanvasArea.Children.Clear();PageTitle.Text="الدفعات المحفوظة";
        SetSubpageBack(()=>ShowPage(RefundTransfers));
        var panel=Column();
        if(batches.Count==0)panel.Children.Add(Text("لا توجد دفعات محفوظة."));
        foreach(var batch in batches){
            string label=batch.Date.ToString("yyyy/MM/dd")+"　·　"+batch.Source+"　·　"+batch.People.Count+" منتسب　·　"+Arabic.Money(batch.People.Sum(p=>(decimal)p.Amount));
            panel.Children.Add(ButtonFor(label,()=>Guard(()=>RefundFiles(source,month,batch)),"\uE8B7"));
        }
        CanvasArea.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
    }

    static A.GroupLetterSettings RefundStyle(RefundBatch batch,bool raf,A.BackupSnapshot source)
    {
        const string header="جمهورية العراق\nوزارة الداخلية\nقيادة شرطة محافظة الأنبار\nقسم الحسابات";
        var baseline=new A.GroupLetterSettings(header,"١‏/٢٢/ح/","","","",RefundBatch.Copies,null,null,0,null);
        if(batch.Original?["letters"]?[raf?"rafidain":"otherBanks"]!=null)return batch.LetterStyle(raf,baseline);
        var fallback=A.GroupLetterSettingsReader.Read(source,raf?A.TransferGroup.Rafidain:A.TransferGroup.OtherBanks);
        return fallback with{Header=string.IsNullOrWhiteSpace(fallback.Header)?header:fallback.Header,Footer=RefundBatch.Copies,
            Template=fallback.Template is {} t?t with{Recipient="مصرف الرافدين / فرع الأنبار"}:null,BodyTemplate=null};
    }

    void RefundFiles(A.BackupSnapshot source,string month,RefundBatch batch)
    {
        CanvasArea.Children.Clear();PageTitle.Text="ملفات الدفعة";
        void Back()=>RefundHistory(source,month,store.Refunds().Where(b=>b.ArchiveId!="draft").ToList());
        SetSubpageBack(Back);
        var layout=new Grid();
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        layout.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        CanvasArea.Children.Add(layout);

        var files=new Dictionary<string,Func<byte[]>>();
        var exportAll=ButtonFor("تصدير جميع الملفات",()=>_=SaveFile("إعادة مبالغ "+batch.Date.ToString("yyyy-MM-dd"),"zip",
            ()=>Package(files.ToDictionary(x=>x.Key,x=>x.Value()))),"\uE74E");
        var actions=Bar(ButtonFor("تعديل الدفعة",()=>_=OpenRefundEditor(source,month,batch),"\uE70F"),
            ButtonFor("حذف الدفعة",()=>{if(Form("حذف الدفعة المحفوظة",Text(batch.Source+"　·　"+batch.Date)))
                _=Working(()=>store.SaveRefund(batch,Path.GetDirectoryName(source.SourcePath),true),"حُذفت الدفعة.",()=>ShowPage(RefundTransfers));},"\uE74D"),exportAll);
        var summary=Text(string.Join("　·　",new[]{batch.Source,batch.Date.ToString("yyyy/MM/dd"),
            Arabic.Money(batch.People.Count)+" منتسب",Arabic.Money(batch.People.Sum(p=>(decimal)p.Amount))+" د.ع"}.Where(x=>x.Length>0)),16,true);
        layout.Children.Add(Column(summary,actions));

        var sections=new Grid();
        sections.ColumnDefinitions.Add(new(){Width=new GridLength(2,GridUnitType.Star)});
        sections.ColumnDefinitions.Add(new(){Width=new GridLength(3,GridUnitType.Star)});
        Grid.SetRow(sections,1);layout.Children.Add(sections);
        sections.MaxHeight=180;
        layout.SizeChanged+=(_,_)=>sections.MaxHeight=Math.Max(100,layout.ActualHeight*.44);

        StackPanel Section(int column,string title){
            var body=new StackPanel();
            var content=new DockPanel();
            var heading=Text(title,16,true);DockPanel.SetDock(heading,Dock.Top);
            content.Children.Add(heading);
            content.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
            var card=Box(content);card.Padding=new Thickness(10,6,10,6);
            Grid.SetColumn(card,column);sections.Children.Add(card);
            return body;
        }
        var letters=Section(0,"كتب الدفعة");
        var banks=Section(1,"ملفات المصارف · CSV");
        Button Icon(Button button,string glyph,string label){
            var symbol=Glyph(glyph);symbol.Margin=new Thickness(0);
            button.Content=symbol;button.Padding=new Thickness(4);button.Width=button.Height=34;
            button.ToolTip=label;AutomationProperties.SetName(button,label);
            return button;
        }
        void AddRow(StackPanel list,string title,string? detail,Button button){
            var line=new DockPanel();
            DockPanel.SetDock(button,Dock.Right);line.Children.Add(button);
            var labels=Column(Text(title,15,true));labels.VerticalAlignment=VerticalAlignment.Center;
            if(detail!=null)labels.Children.Add(Text(detail,13));
            line.Children.Add(labels);
            var row=new Border{Child=line,BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(1,3,1,3)};
            row.SetResourceReference(Border.BorderBrushProperty,"CardStrokeColorDefaultBrush");list.Children.Add(row);
        }
        Button Preview(string name,Func<byte[]> create){
            files.Add(name+".pdf",create);
            return Icon(PreviewButton(name,()=>Task.Run(create),caption:"معاينة "+name,printAndSave:true),"\uE890","معاينة "+name);
        }

        var rosterActions=Bar(Text("منتسبو الدفعة",16,true));
        Grid.SetRow(rosterActions,2);layout.Children.Add(rosterActions);
        var table=GridView(("اسم المنتسب","Name",1.4),("المبلغ (د.ع)","Value",.85),("المصرف","Bank",1.55));
        AutomationProperties.SetName(table,"منتسبو الدفعة");
        table.Columns[1].SortMemberPath="Amount";
        Grid.SetRow(table,3);layout.Children.Add(table);

        List<RefundBank> groups;
        try{groups=batch.Groups();}
        catch(InvalidDataException ex){
            exportAll.IsEnabled=false;
            letters.Children.Add(Text(ex.Message,14));
            banks.Children.Add(Text("راجع بيانات الدفعة من خيار التعديل.",14));
            table.ItemsSource=batch.People.Select(p=>new {p.Name,p.Bank,p.Amount,Value=Arabic.Money(p.Amount)}).ToList();
            return;
        }
        table.ItemsSource=groups.SelectMany(g=>g.People.Select(p=>new {p.Name,Bank=g.Name,p.Amount,Value=Arabic.Money(p.Amount)})).ToList();
        foreach(var set in groups.GroupBy(g=>g.Code=="RAFB").OrderByDescending(g=>g.Key)){
            var recipients=set.ToList();bool raf=set.Key;
            var fileName="كتاب إعادة مبالغ - "+(raf?"الرافدين":"الرشيد والمصارف الأخرى");
            var title=raf?"كتاب الرافدين":recipients.Count==1?"كتاب "+recipients[0].Name:"كتاب الرشيد والمصارف الأخرى";
            AddRow(letters,title,null,Preview(fileName,()=>batch.Letter(recipients,RefundStyle(batch,raf,source),DocumentFonts())));
        }
        foreach(var bank in groups){
            var fileName="إعادة مبالغ - "+bank.Code;
            byte[] Create()=>batch.Csv(bank);
            files.Add(fileName+".csv",Create);
            var export=ButtonFor("تصدير CSV",()=>_=SaveFile(fileName,"csv",Create));
            AddRow(banks,bank.Name,Arabic.Money(bank.Total)+" د.ع　·　"+Arabic.Money(bank.People.Count)+" منتسب",
                Icon(export,"\uE74E","تصدير CSV · "+bank.Name+" ("+bank.Code+")"));
        }
        const string rosterName="قائمة منتسبي إعادة المبالغ";
        var sheet=batch.ListSheet(batch.Payer);
        rosterActions.Children.Add(Preview(rosterName,()=>A.ReportPdf.Generate([sheet],Fonts(),DateTime.Now)));
        byte[] Excel()=>A.XlsxReport.Generate(sheet);
        files.Add(rosterName+".xlsx",Excel);
        rosterActions.Children.Add(Icon(ButtonFor("تصدير Excel",()=>_=SaveFile(rosterName,"xlsx",Excel)),"\uE8A5","تصدير قائمة المنتسبين إلى Excel"));
    }
}
