using System.IO;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    void TrustDocument(A.BackupSnapshot source,A.SalaryTrustEntry[] entries){
        if(entries.Length==0)throw new InvalidDataException("اختر قيود الأمانات التي تريد ربطها.");
        var ids=entries.Select(e=>e.Id).ToHashSet();
        var books=source.Books.OrderByDescending(b=>b.CreatedAt).ToArray();
        var choice=Choices(new[]{"مسح كتاب جديد بالسكنر"}.Concat(books.Select(b=>b.Title+" · "+b.Month)));
        choice.SelectedIndex=books.Length>0?1:0;
        if(!Form("ربط كتاب بالأمانات",Text("عدد القيود المحددة　"+ids.Count),Labeled("الكتاب",choice,390)))return;
        if(choice.SelectedIndex>0){
            var book=books[choice.SelectedIndex-1];
            _=Working(()=>store.UpdateTrustBook(source,ids,book.Id),"رُبط الكتاب بالقيود المحددة.",()=>ShowPage(TrustName));
            return;
        }
        var title=new TextBox{Text="كتاب أمانات الرواتب"};
        var periods=entries.Select(e=>A.SalaryTrust.MonthLabel(e.Year,e.Month)).Concat(source.Months.Select(m=>m.Name)).Distinct();
        var month=Choices(periods);var agency=new TextBox();
        if(!Form("أرشفة كتاب الأمانات",Labeled("عنوان الكتاب",title,360),Labeled("الشهر",month,360),Labeled("الجهة (اختياري)",agency,360)))return;
        var name=title.Text.Trim();if(name.Length==0)throw new InvalidDataException("أدخل عنوان الكتاب.");
        var period=(string)month.SelectedItem;var entity=agency.Text.Trim();
        if(ArchiveScanner.Scan(this) is not {} pdf)return;
        _=Working(()=>store.ArchivePdf(source,name,period,entity.Length==0?null:entity,pdf,ids),"حُفظ الكتاب ورُبط بالقيود المحددة.",()=>ShowPage(TrustName));
    }

    sealed record TrustMonthRow(A.SalaryTrustEntry Entry,string Month,string Amount,string Status,string FileStatus,string Date,string Note);
    void TrustDetails(A.BackupSnapshot source,string name,A.SalaryTrustEntry[] entries,A.ReportSheet sheet){
        CanvasArea.Children.Clear();PageTitle.Text=name;SetSubpageBack(()=>ShowPage(TrustName));
        var grid=GridView(("الشهر","Month",1),("المبلغ","Amount",.85),("حالة الصرف","Status",1),("الحالة بالملف","FileStatus",1),("تاريخ الصرف","Date",1),("الملاحظات","Note",1.5));
        grid.SelectionMode=DataGridSelectionMode.Extended;grid.SelectionUnit=DataGridSelectionUnit.FullRow;
        grid.ItemsSource=entries.Select(e=>new TrustMonthRow(e,A.SalaryTrust.MonthLabel(e.Year,e.Month),Arabic.Money(e.Amount),A.SalaryTrust.PaymentStatus(e),
            e.ImportedStatus,e.ImportedPaymentDate??string.Join("، ",e.Payments.Select(x=>x.BookDate)),
            string.Join(" · ",new[]{e.CaseStatus,e.Note}.Where(x=>x.Length>0).Distinct()))).ToList();
        A.SalaryTrustEntry[] Selected()=>grid.SelectedItems.Cast<TrustMonthRow>().Select(r=>r.Entry).ToArray();
        var link=ButtonFor("ربط الأشهر المحددة بكتاب",()=>Guard(()=>TrustDocument(source,Selected())),"\uE71B");
        var linked=new ComboBox{DisplayMemberPath="Title",MinWidth=160};
        var preview=PreviewButton("الكتاب المرتبط",()=>{
            var b=(A.ArchiveBook)linked.SelectedItem;
            return Task.Run(()=>store.ReadBook(new(b.Id,b.Title,b.Month,b.Entity??"",b.Pages)));
        },printAndSave:true);
        var unlink=ButtonFor("إزالة الارتباط",()=>Guard(()=>{
            var b=(A.ArchiveBook)linked.SelectedItem;var ids=Selected().Select(e=>e.Id).ToHashSet();
            if(Form("إزالة ارتباط الكتاب بالأشهر المحددة",Text(b.Title)))
                _=Working(()=>store.UpdateTrustBook(source,ids,b.Id,unlink:true),"أُزيل ارتباط الكتاب بالقيود المحددة.",()=>ShowPage(TrustName));
        }));
        void Draw(){
            var selected=Selected();link.IsEnabled=selected.Length>0;
            var ids=selected.SelectMany(e=>e.BookIds??[]).Select(Guid.Parse).ToHashSet();
            linked.ItemsSource=source.Books.Where(b=>ids.Contains(b.Id)).ToArray();linked.SelectedIndex=0;
            linked.IsEnabled=preview.IsEnabled=unlink.IsEnabled=linked.SelectedItem!=null;
        }
        grid.SelectionChanged+=(_,_)=>Draw();grid.SelectedIndex=0;Draw();
        var actions=Bar(Text("المجموع　"+Arabic.Money(entries.Sum(e=>e.Amount)),16,true),
            PreviewButton(TrustName+" - "+name,()=>Task.Run(()=>A.SalaryTrustReports.Pdf(sheet,Fonts(),DateTime.Now)),printAndSave:true),link);
        var top=Column(actions,Bar(Text("الكتب المرتبطة"),linked,preview,unlink));var dock=new DockPanel();
        DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);dock.Children.Add(grid);CanvasArea.Children.Add(dock);
    }
}
