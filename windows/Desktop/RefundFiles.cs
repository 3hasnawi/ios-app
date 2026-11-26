using System.IO;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    void RefundHome(A.BackupSnapshot source,string month)
    {
        var history=store.Refunds();
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
        Section("دفعة جديدة",draft==null?"اختيار المنتسبين وإدخال المبالغ":"متابعة المسودة الحالية","\uE710",()=>RefundDesk(source,month,draft));
        Section("الدفعات المحفوظة",saved.Count+" دفعة　·　الكتب وملفات المصارف","\uE8B7",()=>RefundHistory(source,month,saved));
        CanvasArea.Children.Add(Column(Bar(ButtonFor("التحويلات",()=>ShowPage(TransfersName),"\uE72A")),choices));
    }

    void RefundHistory(A.BackupSnapshot source,string month,IReadOnlyList<RefundBatch> batches)
    {
        CanvasArea.Children.Clear();PageTitle.Text="الدفعات المحفوظة";
        var panel=Column(Bar(ButtonFor("إعادة المبالغ",()=>ShowPage(RefundTransfers),"\uE72A")));
        if(batches.Count==0)panel.Children.Add(Text("لا توجد دفعات محفوظة."));
        foreach(var batch in batches){
            string label=batch.Date.ToString("yyyy/MM/dd")+"　·　"+batch.Source+"　·　"+batch.People.Count+" منتسب　·　"+Arabic.Money(batch.People.Sum(p=>(decimal)p.Amount));
            panel.Children.Add(ButtonFor(label,()=>Guard(()=>RefundFiles(source,month,batch)),"\uE8B7"));
        }
        CanvasArea.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
    }

    static A.GroupLetterSettings RefundStyle(RefundBatch batch,bool raf,A.BackupSnapshot source)
    {
        var fallback=A.GroupLetterSettingsReader.Read(source,raf?A.TransferGroup.Rafidain:A.TransferGroup.OtherBanks);
        if(string.IsNullOrWhiteSpace(fallback.Header))fallback=fallback with{Header="جمهورية العراق\nوزارة الداخلية\nقيادة شرطة محافظة الأنبار\nقسم الحسابات"};
        return batch.LetterStyle(raf,fallback);
    }

    void RefundFiles(A.BackupSnapshot source,string month,RefundBatch batch)
    {
        CanvasArea.Children.Clear();PageTitle.Text="ملفات الدفعة";
        void Back()=>RefundHistory(source,month,store.Refunds().Where(b=>b.ArchiveId!="draft").ToList());
        var panel=Column(Bar(ButtonFor("الدفعات المحفوظة",Back,"\uE72A"),ButtonFor("تعديل الدفعة",()=>RefundDesk(source,month,batch),"\uE70F"),
            ButtonFor("حذف الدفعة",()=>{if(Form("حذف الدفعة المحفوظة",Text(batch.Source+"　·　"+batch.Date)))
                _=Working(()=>store.SaveRefund(batch,Path.GetDirectoryName(source.SourcePath),true),"حُذفت الدفعة.",()=>ShowPage(RefundTransfers));},"\uE74D")),
            Text(batch.Source+"　·　"+batch.Date.ToString("yyyy/MM/dd")+"　·　"+batch.People.Count+" منتسب　·　"+Arabic.Money(batch.People.Sum(p=>(decimal)p.Amount)),18,true));
        CanvasArea.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var groups=batch.Groups();
        var files=new Dictionary<string,Func<byte[]>>();
        void FileRow(string name,string extension,Func<byte[]> create,string? detail=null){
            files.Add(name+"."+extension,create);
            var row=Bar(Text(name,16,true));
            if(detail!=null)row.Children.Add(Text(detail,14));
            if(extension=="pdf")row.Children.Add(PreviewButton(name,()=>Task.Run(create),caption:"معاينة "+name));
            row.Children.Add(ButtonFor("تصدير "+extension.ToUpperInvariant(),()=>_=SaveFile(name,extension,create),"\uE74E"));
            panel.Children.Add(Box(row));
        }
        panel.Children.Add(Text("كتب الدفعة",18,true));
        foreach(var set in groups.GroupBy(g=>g.Code=="RAFB").OrderByDescending(g=>g.Key)){
            var banks=set.ToList();bool raf=set.Key;
            FileRow("كتاب إعادة مبالغ - "+(raf?"الرافدين":"الرشيد والمصارف الأخرى"),"pdf",
                ()=>batch.Letter(banks,RefundStyle(batch,raf,source),DocumentFonts()));
        }
        panel.Children.Add(Text("ملفات المصارف",18,true));
        foreach(var bank in groups)FileRow("إعادة مبالغ - "+bank.Code,"csv",()=>batch.Csv(bank),bank.Name+"　·　"+bank.People.Count+" منتسب　·　"+Arabic.Money(bank.Total));
        var sheet=batch.ListSheet(batch.Payer);
        FileRow("قائمة منتسبي إعادة المبالغ","pdf",()=>A.ReportPdf.Generate([sheet],Fonts(),DateTime.Now));
        FileRow("قائمة منتسبي إعادة المبالغ","xlsx",()=>A.XlsxReport.Generate(sheet));
        panel.Children.Add(ButtonFor("تصدير جميع الملفات",()=>_=SaveFile("إعادة مبالغ "+batch.Date.ToString("yyyy-MM-dd"),"zip",()=>Package(files.ToDictionary(x=>x.Key,x=>x.Value()))),"\uE74E"));
    }
}
