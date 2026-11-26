using System.IO;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;
namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    bool bankLedgerSelected;
    Guid? bankPeriodId;
    Task bankLoad = Task.CompletedTask;
    const string MonthlyBank = "كشف حساب البنك", BankLedger = "مطابقة كشف المصرف";

    void Bank()
    {
        var dock = new DockPanel();
        var content = new Grid();
        CanvasArea.Children.Add(dock);
        void Select(bool ledger)
        {
            bankLedgerSelected = ledger;
            BankPage();
        }
        void BankPage()
        {
            dock.Children.Clear();
            content.Children.Clear();
            var menu = Bar(ButtonFor(MonthlyBank, () => Select(false), "\uE825", !bankLedgerSelected),
                ButtonFor(BankLedger, () => Select(true), "\uE73E", bankLedgerSelected));
            DockPanel.SetDock(menu,Dock.Top);
            dock.Children.Add(menu);
            dock.Children.Add(content);
            // Each request owns a separate host: even a quick round trip cannot display stale results.
            var host = new Grid();
            content.Children.Add(host);
            var revision = store.Active;
            var month = SelectedMonth?.Name ?? "";
            bool Current() => CanvasArea.Children.Contains(dock) && content.Children.Contains(host) && store.Active == revision;
            if (revision == null) { host.Children.Add(Text("استورد نسخة احتياطية أو البيانات أولاً.")); return; }
            bankLoad = Load();
            async Task Load()
            {
                host.Children.Add(Text("جارٍ تحميل الكشف…"));
                try
                {
                    if (bankLedgerSelected)
                    {
                        var snapshot = await Task.Run(() => store.Snapshot());
                        if (!Current()) return;
                        host.Children.Clear();
                        Reconciliation(host,snapshot,month,Current);
                    }
                    else
                    {
                        var data = await MonthFigures(revision,month);
                        if (!Current()) return;
                        host.Children.Clear();
                        if (data.summary == null) host.Children.Add(Text("لا توجد رواتب محفوظة للشهر المختار. " + month));
                        else MonthlyStatement(host,data.snapshot,data.summary,Current);
                    }
                }
                catch (Exception ex) { if (Current()) { host.Children.Clear(); host.Children.Add(Text(ex.Message)); } }
            }
        }
        BankPage();
    }

    (string Revision,string Month,Task<(A.BackupSnapshot snapshot,A.BankSummary? summary)> Work)? bankMonthCache;

    Task<(A.BackupSnapshot snapshot,A.BankSummary? summary)> MonthFigures(string revision,string month)
    {
        if(bankMonthCache is {} hit && hit.Revision==revision && hit.Month==month && !hit.Work.IsFaulted)
            return hit.Work;
        var work=Task.Run(()=>{
            var source=string.IsNullOrEmpty(month)?store.Snapshot():store.Snapshot(month);
            var summary=source.Employees.Any(e=>e.Month==month)?A.BankStatementCalculator.Calculate(source,month):null;
            return (source,summary);
        });
        bankMonthCache=(revision,month,work);
        return work;
    }

    void MonthlyStatement(Grid host,A.BackupSnapshot source,A.BankSummary summary,Func<bool> current)
    {
        var sheet = A.MonthlyBankReport.Sheet(source,summary);
        var account = A.TransferPreparation.Account(source);
        var dock = new DockPanel();
        host.Children.Add(dock);
        var figures = new System.Windows.Controls.Primitives.UniformGrid{Columns=3};
        foreach(var (label,value) in new[]{("مجموع كشف البنك",summary.BankTotal),("صافي الرواتب",summary.Net),("استقطاعات المصارف",summary.BankDeductions)})
            figures.Children.Add(Box(Column(Text(label,13),Text(Arabic.Money(value),20,true))));
        var actions = Bar();
        actions.Children.Add(PreviewButton(MonthlyBank+" - "+summary.Month,()=>Task.Run(()=>A.ReportPdf.Generate([sheet],Fonts(),DateTime.Now)),current));
        var head = Column(Text(string.Join("　·　",new[]{summary.Month,account.Name,account.Iban}.Where(t=>!string.IsNullOrWhiteSpace(t))),16,true),figures,actions);
        if(!summary.ExecutionMatched || summary.ContractOverrideStale || summary.MissingNet>0)
            head.Children.Add(Text(string.Join("　·　", new[]{
                !summary.ExecutionMatched?"مطابقة استقطاعات المصارف مع ملف التنفيذ غير مكتملة":null,
                summary.ContractOverrideStale?"تغيّر أساس ضمان العقود؛ راجع الاعتماد":null,
                summary.MissingNet>0?$"صافي الراتب مفقود في {summary.MissingNet} قيد":null
            }.Where(x=>x!=null)),13));
        if(summary.ContractCount>0) actions.Children.Add(ButtonFor("اعتماد ضمان العقود", async ()=>{
            try{
                var amount=new TextBox{Text=summary.ContractContribution.ToString("0")};
                if(!Form("اعتماد الضمان الاجتماعي للعقود",Text(summary.Month+"　·　المحتسب "+Arabic.Money(summary.CalculatedContractContribution)),
                    Text("عدد العقود "+summary.ContractCount+"　·　بلا راتب اسمي "+summary.MissingContractNominal),Labeled("المبلغ النهائي بعد المراجعة",amount,310)))return;
                var total=A.PayrollSpreadsheet.ParseMoney(amount.Text)??throw new InvalidDataException("راجع المبلغ النهائي.");
                await Working(()=>store.ApproveContract(source,summary.Month,total),"اعتُمد ضمان العقود لهذا الشهر.",()=>ShowPage("كشف البنك"));
            }catch(Exception ex){Error(ex);}
        }));
        DockPanel.SetDock(head,Dock.Top);
        dock.Children.Add(head);
        var table = GridView(("القسم","Section",1.25),("البيان","Label",1.8),("المبلغ (دينار)","Amount",1));
        table.ItemsSource = sheet.Rows.Select((r,i)=>new { Section=r[0],Label=r[1],Amount=Arabic.Money(sheet.Amounts[i]) }).ToArray();
        dock.Children.Add(table);
    }

    void Reconciliation(Grid host,A.BackupSnapshot source,string month,Func<bool> current)
    {
        var periods=A.BankReconciliation.ReadFromBackup(source).OrderByDescending(p=>p.Start).ToList();
        var dock=new DockPanel();host.Children.Add(dock);
        var picker=Choices(periods.Select(p=>$"{p.Month}　·　{p.Start:yyyy/MM/dd} – {p.End:yyyy/MM/dd}　·　{(p.IsConfirmed?"معتمدة":"قيد المطابقة")}"));
        var match=periods.FindIndex(p=>p.Id==bankPeriodId);if(match<0)match=periods.FindIndex(p=>p.Month==month);picker.SelectedIndex=match<0?(periods.Count>0?0:-1):match;
        A.BankPeriod? Period()=>picker.SelectedIndex>=0?periods[picker.SelectedIndex]:null;
        var detail=Text("",13);
        var head=Column(FilterBar(("الفترة",picker,1)));var ops=Bar();var metrics=new System.Windows.Controls.Primitives.UniformGrid{Columns=4};head.Children.Add(ops);head.Children.Add(metrics);head.Children.Add(detail);
        DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);var table=GridView(("تاريخ المصرف","Date",1),("البيان","Description",2),("الحركة","Direction",.85),("المبلغ","Value",1),("الرصيد","Balance",1));dock.Children.Add(table);
        async Task Save(A.BankPeriod updated){
            var after=periods.Where(p=>p.Id!=updated.Id).Append(updated).ToList();
            await Working(()=>store.SaveBank(source,after),"حُفظت المطابقة ضمن النسخة.",()=>{bankPeriodId=updated.Id;ShowPage("كشف البنك");});
        }
        void Draw(){
            var p=Period();ops.Children.Clear();metrics.Children.Clear();
            ops.Children.Add(ButtonFor("فترة جديدة",()=>{var e=new ReconciliationEditor(null,month,p){Owner=this};if(e.ShowDialog()==true)_=Save(e.ResultPeriod!);},"\uE710"));
            if(p==null){metrics.Children.Add(Text("لا توجد فترات مطابقة محفوظة."));return;}
            bankPeriodId=p.Id;
            var totals=A.BankReconciliation.Calculate(p);
            detail.Text="تمويل الفترة　"+Arabic.Money(totals.Funding)+"　·　عدد الحركات　"+p.Movements.Count;
            if(totals.Balance!=0 && totals.OutstandingRefunds.Count>0)
                detail.Text+="　·　المتبقي من المبالغ المسترجعة　"+Arabic.Money(totals.OutstandingRefunds.Sum(r=>r.Remaining));
            if(p.Movements.Count==0)detail.Text+="　·　أضف حركة لمعاينة الكشف وتصديره.";
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
                        if(!current()||Period()?.Id!=p.Id)return;
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
            if(p.Movements.Count>0)
                ops.Children.Add(PreviewButton($"{BankLedger} - {p.Month} - {p.Start:yyyy-MM-dd} - {p.End:yyyy-MM-dd}",
                    ()=>Task.Run(()=>A.ReconciliationPdf.Statement(p,A.TransferPreparation.Account(source),p.IsConfirmed||totals.Funding>0&&totals.Balance==0,Fonts())),
                    ()=>current()&&Period()?.Id==p.Id));

        }
        picker.SelectionChanged+=(_,_)=>Guard(Draw);Draw();

    }
    sealed record MovementView(A.BankMovement Item,string Date,string Description,string Direction,string Value,string Balance);
}
