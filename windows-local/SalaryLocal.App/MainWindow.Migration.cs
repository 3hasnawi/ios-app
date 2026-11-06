using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryLocal.Core;

namespace SalaryLocal.App;
public partial class MainWindow
{
    private void AddMigrationActions(StackPanel target)
    {
        target.Children.Add(Ui.Card(new StackPanel { Children = {
            Ui.Text("الانتقال من برنامج ويندوز السابق", 21), Ui.Text(LocalLegacyMigration.ScopeWarning),
            Ui.Button("اختيار ZIP مُصدَّر للمراجعة", () => ChooseLegacySource(false)),
            Ui.Button("جرد مجلد نسخة / تصدير تختاره", () => ChooseLegacySource(true))
        } }));
    }
    private async void ChooseLegacySource(bool folder)
    {
        if (busy) return;
        string source;
        if (folder) { var pick = new OpenFolderDialog { Title = "اختر مجلد نسخة أو تصدير للقراءة فقط" }; if (Ui.ShowNativeDialog(() => pick.ShowDialog(this)) != true) return; source = pick.FolderName; }
        else { var pick = new OpenFileDialog { Filter = "ZIP (*.zip)|*.zip", CheckFileExists = true }; if (Ui.ShowNativeDialog(() => pick.ShowDialog(this)) != true) return; source = pick.FileName; }
        await Run(async () => {
            var review = await Task.Run(() => LocalLegacyMigration.InspectSource(source, cancellation!.Token));
            var panel = new DockPanel { Margin = new Thickness(20) }; var top = new StackPanel();
            top.Children.Add(Ui.Text("جميع الملفات المكتشفة • اختر ZIP واحداً", 22)); top.Children.Add(Ui.Text(LocalLegacyMigration.ScopeWarning));
            DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
            var grid = GridFor(("المسار", "RelativePath"), ("بايت", "Bytes"), ("النطاق", "Kind")); grid.ItemsSource = review.Files;
            var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom); panel.Children.Add(grid);
            string? selected = null; var dialog = Ui.Dialog(this, "جرد المصدر دون تعديل", panel, 1050, 720);
            bottom.Children.Add(Ui.Button("فحص ZIP المحدد", () => { if (grid.SelectedItem is not LegacySourceFile { IsZip: true } file) { Ui.Message(dialog, "اختر ZIP مصدراً من البرنامج القديم. الملفات الأخرى لا تُدمج تلقائياً."); return; } selected = file.FullPath; dialog.Close(); }));
            bottom.Children.Add(Ui.Button("إلغاء دون تغيير", dialog.Close)); dialog.ShowDialog(); if (selected is null) return;
            var zip = await Task.Run(() => LocalLegacyMigration.InspectZip(selected, cancellation!.Token));
            if (Ui.Message(this, $"{zip.Months} أشهر • {zip.Employees} رواتب • {zip.Deductions} استقطاعات\n{zip.Staff} ملاك • {zip.Accounts} حسابات • {zip.Books} كتب\n\n" + string.Join("\n", zip.Warnings) + "\n\n" + LocalLegacyMigration.ScopeWarning + "\n\nهل تؤكد استيراد ZIP المختار فقط إلى مساحة جديدة؟ تبقى المساحة الحالية والمصدر دون استبدال.", true) != MessageBoxResult.Yes) return;
            var result = await Task.Run(() => LocalLegacyMigration.ImportNewWorkspace(selected, workspace.Workspaces, zip.Digest, cancellation!.Token));
            cancellation!.Token.ThrowIfCancellationRequested(); CancelOperation.Visibility = Visibility.Collapsed;
            await Task.Run(() => workspace.Switch(result)); ready = true; await Reload();
            Ui.Message(this, "استُورد ZIP إلى مساحة جديدة. راجع الأشهر والمجاميع والأمانات والمطابقة والكتب قبل الاعتماد. لم تُنقل التعديلات المنفصلة خارج ZIP. أنشئ نسخة .salarybackup بعد المراجعة.");
        }, true);
    }
}
