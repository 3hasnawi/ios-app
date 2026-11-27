using System.IO;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    void RefundDesk(A.BackupSnapshot source,string month,RefundBatch? editing=null,bool hasHistory=true)
    {
        CanvasArea.Children.Clear();PageTitle.Text="إعادة المبالغ";var revision=Path.GetDirectoryName(source.SourcePath);
        var pay=A.TransferPreparation.Payer(source);var rows=editing?.People.ToList()??[];
        var id=editing?.Id??Guid.NewGuid().ToString().ToUpperInvariant();
        var styles=new Dictionary<bool,A.GroupLetterSettings>();
        var payer=new TextBox{Text=editing?.Payer??pay.Name};var account=new TextBox{Text=editing?.Account??pay.Iban};var agency=new TextBox{Text=editing?.Source??""};
        var date=new DatePicker{SelectedDate=editing?.Date.ToDateTime(TimeOnly.MinValue)??DateTime.Today};
        var searchBox=new TextBox();var amounts=new TextBox();var results=new ComboBox{DisplayMemberPath="Name"};
        var dock=new DockPanel();var note=Text("",16,true);
        var selected=GridView(("المنتسب","Name",1.8),("المصرف","Bank",1.3),("آيبان المستفيد","Iban",1.7),("سويفت الفرع","Bic",1),("مبلغ الإعادة","Amount",1));
        void Redraw(){selected.ItemsSource=rows.ToArray();note.Text=rows.Count+" منتسب　·　المجموع　"+Arabic.Money(rows.Sum(p=>(decimal)p.Amount));}
        var available=source.Employees.Where(e=>e.Month==month).Select(e=>(Key:Arabic.Match(e.Name),Person:e)).ToArray();
        searchBox.TextChanged+=(_,_)=>{string q=Arabic.Match(searchBox.Text);results.ItemsSource=q.Length<2?[]:available.Where(x=>x.Key.Contains(q)).Take(60).Select(x=>RefundBatch.Employee(x.Person)).ToArray();results.SelectedIndex=results.Items.Count==1?0:-1;};
        long Value()=>RefundBatch.ParseAmount(amounts.Text);
        selected.SelectionChanged+=(_,_)=>{if(selected.SelectedItem is RefundPayee row)amounts.Text=row.Amount.ToString();};
        RefundBatch Model()=>new(id,agency.Text.Trim(),DateOnly.FromDateTime(date.SelectedDate??throw new InvalidDataException("اختر التاريخ.")),payer.Text.Trim(),A.TransferPreparation.NormalizeAccount(account.Text),rows.ToList(),editing?.Original,editing?.ArchiveId);
        Func<RefundBatch>? reader=null;
        if(editing?.ArchiveId is null or "draft"){
            reader=()=>{
                if(revision!=store.Active)throw new IOException("تغيّرت النسخة. أعد فتح الدفعة.");
                var draft=Model();
                foreach(var (raf,style) in styles)draft=draft.WithLetter(raf,style);
                return draft;
            };
            readOpenDraft=reader;openDraftMonth=month;
        }
        Action<RefundBatch,string> apply=(draft,nextRevision)=>{
            if(reader!=null){
                revision=nextRevision;
                editing=draft;
                rows.Clear();rows.AddRange(draft.People);Redraw();
            }
        };
        applySearchDraft=apply;
        dock.Unloaded+=(_,_)=>{
            if(applySearchDraft!=apply)return;
            readOpenDraft=null;openDraftMonth=null;applySearchDraft=null;
        };
        void Save(){
            var original=Model();
            var batch=original;
            foreach(var raf in original.Groups().Select(g=>g.Code=="RAFB").Distinct())
                batch=batch.WithLetter(raf,styles.GetValueOrDefault(raf)??RefundStyle(original,raf,source));
            _=Working(()=>store.SaveRefund(batch,revision),"حُفظت الدفعة بتاريخها.",()=>{
                var latest=store.Snapshot();
                RefundFiles(latest,month,store.Refunds().Single(b=>b.ArchiveId!="draft" && b.Id==id));
            });
        }
        void EditTemplate(bool raf){
            var batch=Model();
            var banks=batch.Groups().Where(g=>(g.Code=="RAFB")==raf).ToList();
            if(banks.Count==0)throw new InvalidDataException("لا يوجد منتسب لهذا الكتاب في الدفعة.");
            var style=styles.GetValueOrDefault(raf)??RefundStyle(batch,raf,source);
            var t=A.LetterTemplate.From(style,"إعادة المبالغ","يُحتسب متن إعادة المبالغ من بيانات الدفعة.") with{Number="١‏/٢٢/ح/",Subject="تحويل مبلغ",Attachments="١. قرص CD"};
            var changed=EditLetter(t,"refund",source,month,render:x=>batch.Letter(banks,x.Style(),DocumentFonts()));
            if(changed!=null)styles[raf]=changed.Style();
        }
        var add=ButtonFor("إضافة",()=>Guard(()=>{
                if(results.SelectedItem is not RefundPayee p)throw new InvalidDataException("اختر المنتسب من النتائج.");
                if(rows.Any(x=>x.Iban==p.Iban))throw new InvalidDataException("الحساب موجود في الدفعة.");
                if(!A.TransferPreparation.ValidIban(p.Iban))throw new InvalidDataException("راجع آيبان المنتسب في الرواتب.");
                if(editing?.ArchiveId is null or "draft" && rows.Any(row=>row.Month!=p.Month))throw new InvalidDataException("اختر منتسباً من شهر المسودة.");
                rows.Add(p with{Amount=Value()});Redraw();searchBox.Clear();amounts.Clear();searchBox.Focus();
            }),"\uE710");
        PageTitle.Text=editing?.ArchiveId is null or "draft"?"دفعة جديدة":"تعديل الدفعة";
        if(hasHistory)SetSubpageBack(()=>{if(editing?.ArchiveId is null or "draft")ShowPage(RefundTransfers);else RefundFiles(source,month,editing);});
        var savedButton=ButtonFor("الدفعات المحفوظة",()=>ShowPage(RefundTransfers),"\uE8B7");
        savedButton.IsEnabled=hasHistory;
        var head=Column(Bar(savedButton,ButtonFor("استرجاع دفعة من CSV",()=>_=RecoverRefund(source,month,rows.Count>0))),
            FilterBar(("ابحث في رواتب "+month,searchBox,1),("المنتسب",results,1.2),("المبلغ",amounts,.75),("",add,0)),
            FilterBar(("جهة الإعادة",agency,1),("تاريخ الدفعة",date,0),("الجهة الدافعة",payer,1),("آيبان الجهة الدافعة",account,1.5)),note);
        var actions=Bar(ButtonFor("تعديل مبلغ المنتسب",()=>Guard(()=>{if(selected.SelectedItem is RefundPayee p){rows[rows.IndexOf(p)]=p with{Amount=Value()};Redraw();}})),ButtonFor("إزالة المنتسب",()=>{if(selected.SelectedItem is RefundPayee p){rows.Remove(p);Redraw();}}),
            ButtonFor("قالب الرافدين",()=>Guard(()=>EditTemplate(true))),
            ButtonFor("قالب الرشيد والمصارف الأخرى",()=>Guard(()=>EditTemplate(false))),
            ButtonFor("حفظ وفتح ملفات الدفعة",()=>Guard(Save),"\uE74E",true));
        if(reader!=null)actions.Children.Insert(0,ButtonFor("حفظ المسودة",()=>Guard(()=>{
            var draft=reader() with{ArchiveId="draft"};
            var expected=revision;string? updated=null;
            _=Working(()=>{updated=store.SaveRefund(draft,expected,asDraft:true);},"حُفظت مسودة الإعادة.",()=>apply(draft,updated!));
        }),"\uE74E"));
        head.Children.Add(actions);DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);dock.Children.Add(selected);CanvasArea.Children.Add(dock);Redraw();
    }
}
