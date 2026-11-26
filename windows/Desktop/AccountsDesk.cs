using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    const string DirectoryName="دليل الحسابات المصرفية";
    void OpenAccountDirectory(object sender,RoutedEventArgs e)=>ShowPage(DirectoryName);

    async Task ShowAccountDirectory(string query="",A.ExecutionAccount? selected=null)
    {
        var host=new DockPanel();
        CanvasArea.Children.Clear();
        CanvasArea.Children.Add(host);
        var loading=Text("جارٍ تحميل الحسابات…");
        host.Children.Add(loading);
        var revision=store.Active;
        try{
            var snapshot=revision==null?null:await Task.Run(()=>store.Snapshot());
            if(!CanvasArea.Children.Contains(host)||store.Active!=revision)return;
            host.Children.Clear();
            Status.Text="دليل حسابات الجهات المستفيدة لكل الأشهر";
            var accounts=(snapshot?.Accounts??[]).OrderBy(a=>a.Entity,StringComparer.Create(CultureInfo.GetCultureInfo("ar-IQ"),true)).ToArray();
            var table=GridView(("الجهة المستفيدة","Entity",1.5),("المصرف والفرع","Branch",1.5),("IBAN","Iban",1.55),
                ("SWIFT","Swift",.95),("الحساب المحلي","LocalAccount",1.1));
            foreach(var (index,width) in new[]{(2,225d),(3,115d),(4,155d)})table.Columns[index].MinWidth=width;
            var searchBox=new TextBox{Text=query};
            var count=Text("",13);
            A.ExecutionAccount[] visible=[];
            void Edit(A.ExecutionAccount? original){
                if(EditDirectoryAccount(snapshot,original) is {} saved)
                    workPageLoad=ShowAccountDirectory(searchBox.Text,saved);
            }
            var add=ButtonFor("إضافة حساب",()=>Edit(null),"\uE710",true);
            var edit=ButtonFor("تعديل الحساب",()=>{if(table.SelectedItem is A.ExecutionAccount row)Edit(row);},"\uE70F");
            var exportButton=ButtonFor("تصدير Excel",()=>{
                var exportRows=visible;
                string header=snapshot==null?A.ReportSheet.DefaultHeader:A.ReportSheet.HeaderFor(snapshot);
                _=SaveFile(DirectoryName,"xlsx",()=>AccountDirectory.Excel(exportRows,header));
            },"\uE74E");
            void Filter(){
                var term=Arabic.Match(searchBox.Text);
                var identifier=A.TransferPreparation.NormalizeAccount(searchBox.Text);
                visible=accounts.Where(a=>term.Length==0 || Arabic.Match(a.Entity).Contains(term) || Arabic.Match(a.Branch).Contains(term) ||
                    identifier.Length>0 && new[]{a.Iban,a.Swift,a.LocalAccount??""}.Any(v=>A.TransferPreparation.NormalizeAccount(v).Contains(identifier))).ToArray();
                table.ItemsSource=visible;
                count.Text=visible.Length==accounts.Length?$"{accounts.Length} حساب":$"{visible.Length} من {accounts.Length} حساب";
                exportButton.IsEnabled=visible.Length>0;
                edit.IsEnabled=table.SelectedItem!=null;
            }
            searchBox.TextChanged+=(_,_)=>Filter();
            table.SelectionChanged+=(_,_)=>edit.IsEnabled=table.SelectedItem is A.ExecutionAccount;
            table.MouseDoubleClick+=(_,args)=>{
                for(var hit=args.OriginalSource as DependencyObject;hit!=null&&hit!=table;hit=ElementTree.Parent(hit))
                    if(hit is DataGridRow {Item:A.ExecutionAccount row}){Edit(row);break;}
            };
            var toolbar=FilterBar(("ابحث عن جهة أو مصرف أو رقم حساب",searchBox,1),("",add,0),("",edit,0),("",exportButton,0));
            var head=Column(toolbar,count);DockPanel.SetDock(head,Dock.Top);host.Children.Add(head);host.Children.Add(table);
            Filter();
            if(selected!=null&&visible.Contains(selected)){table.SelectedItem=selected;table.ScrollIntoView(selected);}
        }catch(Exception ex){if(CanvasArea.Children.Contains(host)){host.Children.Clear();host.Children.Add(Text(ex.Message));}}
    }

    A.ExecutionAccount? EditDirectoryAccount(A.BackupSnapshot? snapshot,A.ExecutionAccount? original)
    {
        var dialog=new Window{Title=original==null?"إضافة حساب":"تعديل الحساب",Owner=this,SizeToContent=SizeToContent.WidthAndHeight,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,FlowDirection=FlowDirection.RightToLeft};
        var entity=new TextBox{Text=original?.Entity??""};
        var branch=new TextBox{Text=original?.Branch??""};
        TextBox Number(string? value)=>new(){Text=value??"",FlowDirection=FlowDirection.LeftToRight};
        var iban=Number(original?.Iban);var swift=Number(original?.Swift);var local=Number(original?.LocalAccount);
        var feedback=Text("");
        bool saving=false;
        A.ExecutionAccount? result=null;
        var save=ButtonFor("حفظ الحساب",async ()=>{
            try{
                var account=AccountDirectory.Account(entity.Text,branch.Text,iban.Text,swift.Text,local.Text);
                saving=true;dialog.IsEnabled=false;
                await Task.Run(()=>store.SaveDirectoryAccount(snapshot,original,account));
                result=account;saving=false;dialog.DialogResult=true;
            }catch(Exception ex){feedback.Text=ex.Message;}
            finally{saving=false;if(dialog.IsVisible)dialog.IsEnabled=true;}
        },"\uE74E",true);
        dialog.Closing+=(_,args)=>{if(saving)args.Cancel=true;};
        dialog.Content=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=Box(Column(
            Labeled("الجهة المستفيدة",entity,320),Labeled("المصرف والفرع",branch,320),Labeled("IBAN",iban,320),
            Labeled("SWIFT",swift,320),Labeled("الحساب المحلي",local,320),feedback,Bar(save,ButtonFor("إلغاء",dialog.Close))))};
        dialog.ShowDialog();
        return result;
    }
}
