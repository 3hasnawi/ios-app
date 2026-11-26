using System.IO;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    void RefundDesk(A.BackupSnapshot source,string month,RefundBatch? editing=null)
    {
        CanvasArea.Children.Clear();PageTitle.Text="إعادة المبالغ";var revision=Path.GetDirectoryName(source.SourcePath);
        var pay=A.TransferPreparation.Payer(source);var rows=editing?.People.ToList()??[];
        var history=store.Refunds();var options=Choices(history.Select(b=>(b.ArchiveId=="draft"?"مسودة　":"")+b.Date.ToString("yyyy/MM/dd")+"　·　"+b.Source+"　·　"+b.People.Count+" منتسب"));options.SelectedIndex=-1;
        var payer=new TextBox{Text=editing?.Payer??pay.Name};var account=new TextBox{Text=editing?.Account??pay.Iban};var agency=new TextBox{Text=editing?.Source??""};
        var date=new DatePicker{SelectedDate=editing?.Date.ToDateTime(TimeOnly.MinValue)??DateTime.Today};
        var searchBox=new TextBox();var amounts=new TextBox();var results=new ComboBox{DisplayMemberPath="Name"};
        var dock=new DockPanel();var note=Text("",16,true);
        var selected=GridView(("المنتسب","Name",1.8),("المصرف","Bank",1.3),("آيبان المستفيد","Iban",1.7),("سويفت الفرع","Bic",1),("مبلغ الإعادة","Amount",1));
        void Redraw(){selected.ItemsSource=rows.ToArray();note.Text=rows.Count+" منتسب　·　المجموع　"+Arabic.Money(rows.Sum(p=>(decimal)p.Amount));}
        var available=source.Employees.Where(e=>e.Month==month).Select(e=>(Key:Arabic.Match(e.Name),Person:e)).ToArray();
        searchBox.TextChanged+=(_,_)=>{string q=Arabic.Match(searchBox.Text);results.ItemsSource=q.Length<2?[]:available.Where(x=>x.Key.Contains(q)).Take(60).Select(x=>RefundBatch.Employee(x.Person)).ToArray();results.SelectedIndex=results.Items.Count==1?0:-1;};
        long Value(){var parsed=A.PayrollSpreadsheet.ParseMoney(amounts.Text);if(parsed is null or <=0 or >999_999_999_999_999m||parsed!=decimal.Truncate(parsed.Value))throw new InvalidDataException("أدخل مبلغاً صحيحاً بالدينار.");return (long)parsed.Value;}
        selected.SelectionChanged+=(_,_)=>{if(selected.SelectedItem is RefundPayee row)amounts.Text=row.Amount.ToString();};
        RefundBatch Model()=>new(editing?.Id??Guid.NewGuid().ToString().ToUpperInvariant(),agency.Text.Trim(),DateOnly.FromDateTime(date.SelectedDate??throw new InvalidDataException("اختر التاريخ.")),payer.Text.Trim(),A.TransferPreparation.NormalizeAccount(account.Text),rows.ToList(),editing?.Original,editing?.ArchiveId);
        void Return()=>ShowPage(TransfersName);
        var add=ButtonFor("إضافة",()=>Guard(()=>{
                if(results.SelectedItem is not RefundPayee p)throw new InvalidDataException("اختر المنتسب من النتائج.");
                if(rows.Any(x=>x.Iban==p.Iban))throw new InvalidDataException("الحساب موجود في الدفعة.");
                if(!A.TransferPreparation.ValidIban(p.Iban)||!A.TransferPreparation.ValidSwift(p.Bic)||!p.Bic.StartsWith(p.Iban.Substring(4,4)+"IQ"))throw new InvalidDataException("راجع آيبان المنتسب وعمود الرفرنس في الرواتب.");
                rows.Add(p with{Amount=Value()});Redraw();searchBox.Clear();amounts.Clear();searchBox.Focus();
            }),"\uE710");
        var open=ButtonFor("فتح الدفعة",()=>Guard(()=>{if(options.SelectedIndex>=0)RefundDesk(source,month,history[options.SelectedIndex]);}));
        var head=Column(Bar(ButtonFor("ملفات التحويل",Return,"\uE72A"),ButtonFor("دفعة جديدة",()=>RefundDesk(source,month))),
            FilterBar(("الدفعات المحفوظة",options,1.45),("",open,0),("ابحث في رواتب "+month,searchBox,1),("المنتسب",results,1.2),("المبلغ",amounts,.75),("",add,0)),
            FilterBar(("جهة الإعادة",agency,1),("تاريخ الدفعة",date,0),("الجهة الدافعة",payer,1),("آيبان الجهة الدافعة",account,1.5)),note);
        var actions=Bar(ButtonFor("تعديل مبلغ المنتسب",()=>Guard(()=>{if(selected.SelectedItem is RefundPayee p){rows[rows.IndexOf(p)]=p with{Amount=Value()};Redraw();}})),ButtonFor("إزالة المنتسب",()=>{if(selected.SelectedItem is RefundPayee p){rows.Remove(p);Redraw();}}),
            ButtonFor("حفظ الدفعة",()=>Guard(()=>{var model=Model();_=Working(()=>store.SaveRefund(model,revision),"حُفظت الدفعة بتاريخها ضمن النسخة.",Return);}),"\uE74E",true),
            ButtonFor("تصدير الكتب والملفات",()=>Guard(()=>{
                var b=Model();var groups=b.Groups();var sets=new List<(List<RefundBank> Banks,A.GroupLetterSettings Style)>();
                foreach(var grouping in groups.GroupBy(g=>g.Code=="RAFB")){
                    var saved=b.LetterStyle(grouping.Key,A.GroupLetterSettingsReader.Read(source,grouping.Key?A.TransferGroup.Rafidain:A.TransferGroup.OtherBanks));
                    var t=A.LetterTemplate.From(saved,"إعادة المبالغ","يُحتسب متن إعادة المبالغ تلقائياً من بيانات الدفعة.") with{Number="١‏/٢٢/ح/",Subject="تحويل مبلغ",Attachments="١. قرص CD"};
                    var draft=EditLetter(t,"refund",source,month,render:x=>b.Letter(grouping.ToList(),x.Style(),DocumentFonts()));
                    var style=draft?.Style();
                    if(style==null)return;b=b.WithLetter(grouping.Key,style);sets.Add((grouping.ToList(),style));
                }
                _=SaveFile("إعادة المبالغ "+b.Date.ToString("yyyy-MM-dd"),"zip",()=>{
                    var files=groups.ToDictionary(g=>g.Code+".csv",b.Csv);var sheet=b.ListSheet(A.ReportSheet.HeaderFor(source));files["قائمة المستفيدين.xlsx"]=A.XlsxReport.Generate(sheet);
                    files["قائمة المستفيدين.pdf"]=A.ReportPdf.Generate([sheet],Fonts(),DateTime.Now);
                    var fonts=A.LetterFonts.Resolve(LetterFont(),Fonts().Bold,Logo());foreach(var (banks,style) in sets)files["كتاب "+(banks[0].Code=="RAFB"?"الرافدين":"المصارف الأخرى")+".pdf"]=b.Letter(banks,style,fonts);
                    return Package(files);
                },()=>store.SaveRefund(b,revision),Return);
            }),"\uE8B7"));
        if(editing!=null)actions.Children.Add(ButtonFor("حذف الدفعة",()=>{if(Form("حذف الدفعة المحفوظة",Text(editing.Date.ToString("yyyy/MM/dd")+"　·　"+editing.Source)))_=Working(()=>store.SaveRefund(editing,revision,true),"حُذفت الدفعة.",Return);}));
        head.Children.Add(actions);DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);dock.Children.Add(selected);CanvasArea.Children.Add(dock);Redraw();
    }
}
