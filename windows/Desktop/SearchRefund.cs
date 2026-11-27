using System.IO;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    Func<RefundBatch>? readOpenDraft;
    Action<RefundBatch,string>? applySearchDraft;
    string? openDraftMonth;
    async Task SearchRefund(string name,string? selectedMonth,Window owner){
        var revision=store.Active;
        try{
            var open=readOpenDraft?.Invoke();
            string? editorMonth=openDraftMonth;
            var (batch,candidate)=await Task.Run(()=>{
                var source=store.PersonSnapshot(name);
                var payer=A.TransferPreparation.Payer(source);
                var draft=open??store.Refunds().SingleOrDefault(b=>b.ArchiveId=="draft")??
                    new RefundBatch(Guid.NewGuid().ToString().ToUpperInvariant(),"",DateOnly.FromDateTime(DateTime.Today),payer.Name,payer.Iban,[]);
                var latest=source.Employees.OrderByDescending(e=>Arabic.Period(e.Month)).FirstOrDefault()?.Month;
                var month=draft.People.FirstOrDefault()?.Month??editorMonth??selectedMonth??latest;
                var matches=source.Employees.Where(e=>e.Month==month).ToArray();
                if(matches.Length!=1)throw new InvalidDataException("تعذّر تحديد سجل راتب واحد لهذا المنتسب في شهر الدفعة.");
                var person=RefundBatch.Employee(matches[0]);
                if(!A.TransferPreparation.ValidIban(person.Iban))throw new InvalidDataException("راجع آيبان المنتسب في رواتب شهر "+month);
                return (draft,person);
            });
            if(!owner.IsVisible || store.Active!=revision)return;
            var previous=batch.People.FirstOrDefault(p=>p.Iban==candidate.Iban);
            var value=new TextBox{Text=previous?.Amount.ToString()??""};
            var dialog=new Window{Owner=owner,Title="مبلغ الإعادة",Width=440,SizeToContent=SizeToContent.Height,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,FlowDirection=FlowDirection.RightToLeft};
            bool saving=false;
            var note=Text(A.TransferPreparation.ValidSwift(candidate.Bic)&&candidate.Bic.StartsWith(candidate.Iban.Substring(4,4)+"IQ") ? "" : "راجع عمود الرفرنس في الرواتب قبل إصدار ملفات المصرف.",14);
            var save=ButtonFor("حفظ في المسودة",()=>{},"\uE74E",true);
            save.Click+=async (_,_)=>{
                if(saving)return;
                try{
                    var updated=batch.SetSearchAmount(candidate,RefundBatch.ParseAmount(value.Text));
                    saving=true;dialog.IsEnabled=false;
                    var nextRevision=await Task.Run(()=>store.SaveRefund(updated,revision,asDraft:true));
                    applySearchDraft?.Invoke(updated,nextRevision);
                    Status.Text="حُفظ مبلغ "+candidate.Name+" في مسودة الإعادة.";
                    saving=false;dialog.DialogResult=true;
                }catch(Exception ex){note.Text=ex.Message;}
                finally{saving=false;dialog.IsEnabled=true;}
            };
            dialog.Closing+=(_,e)=>{if(saving)e.Cancel=true;};
            dialog.Content=Box(Column(Text(candidate.Name,19,true),Text(candidate.Month+"　·　"+candidate.Bank+"　·　…"+candidate.Iban[^4..]),
                Labeled("مبلغ الإعادة بالدينار",value,335),note,Bar(save,ButtonFor("إلغاء",()=>dialog.DialogResult=false))));
            dialog.Loaded+=(_,_)=>{value.Focus();value.SelectAll();};
            if(dialog.ShowDialog()==true && owner!=this)owner.Close();
        }catch(Exception ex){if(owner.IsVisible)MessageBox.Show(owner,ex.Message,"إعادة المبالغ",MessageBoxButton.OK,MessageBoxImage.Information);}
    }
}
