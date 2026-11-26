using System.IO;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;
namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    async void Bank()
    {
        if(store.Active==null){CanvasArea.Children.Add(Text("افتح نسخة الآيفون أو استورد رواتب أولاً."));return;}
        var stamp=store.Active;var month=SelectedMonth?.Name??"";
        try{
            var source=await Task.Run(()=>store.Snapshot());if(page!="كشف البنك"||stamp!=store.Active||month!=(SelectedMonth?.Name??""))return;
            var periods=A.BankReconciliation.ReadFromBackup(source).OrderByDescending(p=>p.Start).ToList();
            var dock=new DockPanel();CanvasArea.Children.Clear();CanvasArea.Children.Add(dock);
            var picker=Choices(periods.Select(p=>$"{p.Month}　·　{p.Start:yyyy/MM/dd} – {p.End:yyyy/MM/dd}"));
            var match=periods.FindIndex(p=>p.Month==month);picker.SelectedIndex=match<0?(periods.Count>0?0:-1):match;
            A.BankPeriod? Period()=>picker.SelectedIndex>=0?periods[picker.SelectedIndex]:null;
            var head=Column(Text("مطابقة كشف البنك",22,true),FilterBar(("الفترة",picker,1)));var ops=Bar();var metrics=new System.Windows.Controls.Primitives.UniformGrid{Columns=4};head.Children.Add(ops);head.Children.Add(metrics);
            DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);var table=GridView(("تاريخ المصرف","Date",1),("البيان","Description",2),("الحركة","Direction",.85),("المبلغ","Value",1),("الرصيد","Balance",1));dock.Children.Add(table);
            async Task Save(A.BankPeriod updated){
                var after=periods.Where(p=>p.Id!=updated.Id).Append(updated).ToList();
                await Working(()=>store.SaveBank(source,after),"حُفظت المطابقة ضمن النسخة.",()=>ShowPage("كشف البنك"));
            }
            void Draw(){
                var p=Period();ops.Children.Clear();metrics.Children.Clear();
                ops.Children.Add(ButtonFor("فترة جديدة",()=>{var e=new ReconciliationEditor(null,month,p){Owner=this};if(e.ShowDialog()==true)_=Save(e.ResultPeriod!);},"\uE710"));
                if(month.Length>0)ops.Children.Add(ButtonFor("اعتماد ضمان العقود",async ()=>{
                    try{
                        var snap=await Task.Run(()=>store.Snapshot(month));
                        if(page!="كشف البنك"||stamp!=store.Active||month!=(SelectedMonth?.Name??""))return;
                        var summary=A.BankStatementCalculator.Calculate(snap,month);
                        if(summary.ContractCount==0){Status.Text="لا توجد رواتب عقود لهذا الشهر.";return;}
                        var amount=new TextBox{Text=summary.ContractContribution.ToString("0")};
                        if(!Form("اعتماد الضمان الاجتماعي للعقود",Text(month+"　·　المحتسب "+Arabic.Money(summary.CalculatedContractContribution)),
                            Text("عدد العقود "+summary.ContractCount+"　·　بلا راتب اسمي "+summary.MissingContractNominal),Labeled("المبلغ النهائي بعد المراجعة",amount,310)))return;
                        var total=A.PayrollSpreadsheet.ParseMoney(amount.Text)??throw new InvalidDataException("راجع المبلغ النهائي.");
                        await Working(()=>store.ApproveContract(snap,month,total),"اعتُمد ضمان العقود لهذا الشهر.",()=>ShowPage("كشف البنك"));
                    }catch(Exception ex){Error(ex);}
                }));
                if(p==null)return;
                var totals=A.BankReconciliation.Calculate(p);
                foreach(var (label,value) in new[]{("الافتتاحي",p.OpeningBalance),("الدائن",totals.Credits),("المدين",totals.Debits),("الختامي",totals.Balance)})
                    metrics.Children.Add(Box(Column(Text(label,13),Text(Arabic.Money(value),20,true))));
                table.ItemsSource=totals.Lines.Select(l=>new MovementView(l.Movement,l.Movement.Date.ToString("yyyy/MM/dd"),l.Movement.Description,l.Movement.Direction==A.MovementDirection.Credit?"دائن":"مدين",Arabic.Money(l.Movement.Amount),Arabic.Money(l.Balance))).ToArray();
                void Edit(A.BankMovement? old){
                    var e=new ReconciliationEditor(p,old){Owner=this};if(e.ShowDialog()!=true)return;
                    var updated=e.ResultMovement!;var rows=old==null?p.Movements.Append(updated):p.Movements.Select(m=>m.Id==old.Id?updated:m);_=Save(p with{Movements=rows.ToArray()});
                }
                if(!p.IsConfirmed){
                    ops.Children.Add(ButtonFor("تعديل الفترة",()=>{var e=new ReconciliationEditor(p,month){Owner=this};if(e.ShowDialog()==true)_=Save(e.ResultPeriod!);}));
                    ops.Children.Add(ButtonFor("المبالغ المقترحة",async ()=>{
                        try{
                            var suggestions=await Task.Run(()=>BankSuggestions.For(store.Snapshot(p.Month),p));
                            if(page!="كشف البنك"||stamp!=store.Active||Period()?.Id!=p.Id)return;
                            if(suggestions.Count==0){Status.Text="لا توجد مبالغ متاحة للاقتراح لهذه الفترة.";return;}
                            var choice=Choices(suggestions.Select(x=>x.Title+"　·　"+Arabic.Money(x.Amount)));
                            var date=new DatePicker{SelectedDate=p.Start.ToDateTime(TimeOnly.MinValue),DisplayDateStart=p.Start.ToDateTime(TimeOnly.MinValue),DisplayDateEnd=p.End.ToDateTime(TimeOnly.MinValue)};
                            if(!Form("تسجيل حركة من كشف المصرف",Labeled("المبلغ المقترح",choice,440),Labeled("تاريخ تنفيذ الحركة في المصرف",date,270)))return;
                            var item=suggestions[choice.SelectedIndex];
                            var movement=new A.BankMovement(Guid.NewGuid(),DateOnly.FromDateTime(date.SelectedDate??throw new InvalidDataException("اختر تاريخ التنفيذ.")),item.Title,item.Amount,A.MovementDirection.Debit,item.Id,item.Lines);
                            await Save(p with{Movements=p.Movements.Append(movement).ToArray()});
                        }catch(Exception e){Error(e);}
                    }));
                    ops.Children.Add(ButtonFor("إضافة حركة",()=>Edit(null),"\uE710"));
                    ops.Children.Add(ButtonFor("تعديل الحركة",()=>{if(table.SelectedItem is MovementView row)Edit(row.Item);else Status.Text="حدّد حركة من الجدول."; }));
                }
                ops.Children.Add(ButtonFor(p.IsConfirmed?"إلغاء الاعتماد":"اعتماد الفترة",()=>{
                    bool balanced=totals.Funding>0&&totals.Balance==0;
                    if(Form(p.IsConfirmed?"إلغاء اعتماد الفترة":balanced?"اعتماد الفترة":"اعتماد يدوي رغم التنبيهات",Text(p.Month+"　·　الرصيد الختامي "+Arabic.Money(totals.Balance)),
                        Text(!balanced&&!p.IsConfirmed?"التمويل أو الرصيد يحتاج مراجعة. تأكد من كشف المصرف الورقي قبل المتابعة.":"راجعت حركات الفترة ورصيدها.")))
                        _=Save(p.IsConfirmed?A.BankReconciliation.Unconfirm(p):A.BankReconciliation.Confirm(p,DateTime.UtcNow));}));
                ops.Children.Add(ButtonFor("تصدير كشف PDF",()=>_=SaveFile("مطابقة كشف البنك","pdf",()=>A.ReconciliationPdf.Statement(p,A.TransferPreparation.Account(source),p.IsConfirmed||totals.Funding>0&&totals.Balance==0,Fonts())),"\uE74E"));
            }
            picker.SelectionChanged+=(_,_)=>Guard(Draw);Draw();
        }catch(Exception e){if(page=="كشف البنك"){CanvasArea.Children.Clear();CanvasArea.Children.Add(Text(e.Message));}}
    }
    sealed record MovementView(A.BankMovement Item,string Date,string Description,string Direction,string Value,string Balance);
}
