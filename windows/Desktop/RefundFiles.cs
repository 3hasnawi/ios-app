using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    async Task OpenRefundEditor(A.BackupSnapshot source,string month,RefundBatch? editing,bool latestDraft=false,bool hasHistory=true)
    {
        A.BackupSnapshot? payroll=null;
        await Working(()=>{
            var revision=store.Active;
            if(latestDraft)editing=store.Refunds().SingleOrDefault(b=>b.ArchiveId=="draft");
            else if(revision!=Path.GetDirectoryName(source.SourcePath))throw new IOException("تغيّرت النسخة. أعد فتح الدفعة.");
            if(editing?.ArchiveId=="draft" && editing.People.FirstOrDefault() is {} first)month=first.Month;
            payroll=store.Snapshot(month);
            if(Path.GetDirectoryName(payroll.SourcePath)!=revision)throw new IOException("تغيّرت النسخة. أعد فتح الدفعة.");
        },"",()=>RefundDesk(payroll!,month,editing,hasHistory));
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

    void RefundFiles(A.BackupSnapshot source,string month,RefundBatch batch,IReadOnlyList<RefundBatch>? saved=null)
    {
        CanvasArea.Children.Clear();PageTitle.Text=RefundTransfers;
        subpageBack=null;UpdateBackButton();
        saved??=store.Refunds().Where(b=>b.ArchiveId!="draft").ToArray();
        var picker=Choices(saved.Select(b=>b.Date.ToString("yyyy/MM/dd")+"　·　"+b.Source+"　·　"+Arabic.Money(b.People.Sum(p=>(decimal)p.Amount))));
        picker.SelectedIndex=Array.FindIndex(saved.ToArray(),b=>b.Id==batch.Id);
        picker.SelectionChanged+=(_,_)=>{if(picker.SelectedIndex>=0)RefundFiles(source,month,saved[picker.SelectedIndex],saved);};
        var start=ButtonFor("دفعة جديدة",()=>_=OpenRefundEditor(source,month,null,latestDraft:true),"\uE710");
        var actions=Bar(Labeled("الدفعات المحفوظة",picker,270),start,ButtonFor("تعديل الدفعة",()=>_=OpenRefundEditor(source,month,batch),"\uE70F"),
            ButtonFor("حذف الدفعة",()=>{if(Form("حذف الدفعة المحفوظة",Text(batch.Source+"　·　"+batch.Date)))
                _=Working(()=>store.SaveRefund(batch,Path.GetDirectoryName(source.SourcePath),true),"حُذفت الدفعة.",()=>ShowPage(RefundTransfers),"delete");},"\uE74D"));
        CanvasArea.Children.Add(RefundDocumentPanel(batch,raf=>RefundStyle(batch,raf,source),actions,this));
    }

    Grid RefundDocumentPanel(RefundBatch batch,Func<bool,A.GroupLetterSettings> style,Panel actions,Window owner)
    {
        var layout=new Grid();
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        layout.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});

        var files=new Dictionary<string,Func<byte[]>>();
        var exportAll=ButtonFor("تصدير جميع الملفات",()=>_=SaveFile("إعادة مبالغ "+batch.Date.ToString("yyyy-MM-dd"),"zip",
            ()=>Package(files.ToDictionary(x=>x.Key,x=>x.Value())),owner:owner),"\uE74E");
        actions.Children.Add(exportAll);
        var summary=TransferSummary(("مبلغ الدفعة",Arabic.Money(batch.People.Sum(p=>(decimal)p.Amount))+" د.ع"),
            ("عدد المنتسبين",Arabic.Money(batch.People.Count)),
            ("المصارف",Arabic.Money(batch.People.Select(p=>A.TransferPreparation.NormalizeAccount(p.Iban)).Where(A.TransferPreparation.ValidIban).Select(iban=>iban.Substring(4,4)).Distinct().Count())));
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
            return layout;
        }
        table.ItemsSource=groups.SelectMany(g=>g.People.Select(p=>new {p.Name,Bank=g.Name,p.Amount,Value=Arabic.Money(p.Amount)})).ToList();
        foreach(var set in groups.GroupBy(g=>g.Code=="RAFB").OrderByDescending(g=>g.Key)){
            var recipients=set.ToList();bool raf=set.Key;
            var fileName="كتاب إعادة مبالغ - "+(raf?"الرافدين":"الرشيد والمصارف الأخرى");
            var title=raf?"كتاب الرافدين":recipients.Count==1?"كتاب "+recipients[0].Name:"كتاب الرشيد والمصارف الأخرى";
            AddRow(letters,title,null,Preview(fileName,()=>batch.Letter(recipients,style(raf),DocumentFonts())));
        }
        foreach(var bank in groups){
            var fileName="إعادة مبالغ - "+bank.Code;
            byte[] Create()=>batch.Csv(bank);
            files.Add(fileName+".csv",Create);
            var export=ButtonFor("تصدير CSV",()=>_=SaveFile(fileName,"csv",Create,owner:owner));
            AddRow(banks,bank.Name,Arabic.Money(bank.Total)+" د.ع　·　"+Arabic.Money(bank.People.Count)+" منتسب",
                Icon(export,"\uE74E","تصدير CSV · "+bank.Name+" ("+bank.Code+")"));
        }
        const string rosterName="قائمة منتسبي إعادة المبالغ";
        var sheet=batch.ListSheet(batch.Payer);
        rosterActions.Children.Add(Preview(rosterName,()=>A.ReportPdf.Generate([sheet],Fonts(),DateTime.Now)));
        byte[] Excel()=>A.XlsxReport.Generate(sheet);
        files.Add(rosterName+".xlsx",Excel);
        rosterActions.Children.Add(Icon(ButtonFor("تصدير Excel",()=>_=SaveFile(rosterName,"xlsx",Excel,owner:owner)),"\uE8A5","تصدير قائمة المنتسبين إلى Excel"));
        return layout;
    }
}
