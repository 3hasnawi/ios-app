using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    partial void AddInteropActions(StackPanel target)
    {
        target.Children.Add(Ui.Card(new StackPanel { Children = {
            Ui.Text("تبادل نسخة الآيفون", 21),
            Ui.Text("استيراد ZIP إلى مساحة مستقلة، أو تصدير الحالة الحالية للآيفون. يدعم صيغ التطبيق 1–6 و8–10 بعد فحصها. لا اتصال تلقائياً ولا دمج بين نسختين."),
            Ui.Text("ملف الآيفون لا ينقل تاريخ إصدارات الحاسبة. استخدم .salarybackup للنقل بين الحاسبتين ولحفظ التاريخ. الاسترجاع على الآيفون يستبدل بياناته، لذلك احفظ نسخته الشاملة أولاً وراجع الاختلافات."),
            Ui.Text("إعدادات القوالب والتمويل غير المعروضة هنا تُحفظ دون تنفيذها أو تعديلها. دعم التبادل اختُبر ببيانات وهمية، ولم يُختبر على جهاز آيفون فعلي."),
            Ui.Button("فحص واستيراد ZIP الآيفون", ImportIphoneClick), Ui.Button("تصدير ZIP جديد للآيفون", ExportIphoneClick)
        } }));
    }
    private async void ImportIphoneClick()
    {
        if (busy) return;
        var picker = new OpenFileDialog { Filter = "نسخة تطبيق الرواتب (*.zip)|*.zip", CheckFileExists = true };
        if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        await Run(async () => {
            var review = await Task.Run(() => IphoneInteropService.Inspect(picker.FileName, cancellation!.Token));
            var message = $"صيغة {review.Version} • {review.Months:N0} أشهر\n{review.Employees:N0} رواتب • {review.Deductions:N0} استقطاعات\n{review.Staff:N0} ملاك • {review.Accounts:N0} حسابات • {review.Books:N0} كتب\n\n" + string.Join("\n", review.Warnings) + "\n\nاستيراد وفتح مساحة جديدة؟ تبقى المساحة الحالية محفوظة دون استبدال.";
            if (Ui.Message(this, message, true) != MessageBoxResult.Yes) return;
            var path = await Task.Run(() => IphoneInteropService.ImportNewWorkspace(picker.FileName, workspace.Workspaces, review.Digest, cancellation!.Token));
            cancellation!.Token.ThrowIfCancellationRequested(); CancelOperation.Visibility = Visibility.Collapsed;
            await Task.Run(() => workspace.Switch(path)); ready = true; await Reload();
            Ui.Message(this, "حُفظت نسخة الآيفون في مساحة مستقلة. لم يتغير ZIP الأصلي ولا مساحة العمل السابقة. أنشئ نسخة .salarybackup لحفظ بيانات الحاسبة وتاريخها.");
        }, true);
    }
    private async void ExportIphoneClick()
    {
        if (busy || !ready) return;
        if (Ui.Message(this, "سيُحفظ ملف جديد للحالة الحالية، وليس تاريخ إصدارات الحاسبة.\nالاسترجاع على الآيفون يستبدل بياناته ولا يدمجها؛ احفظ نسخة شاملة منه أولاً، خصوصاً دفتر الأمانات وتعديلات العقود.\nالتبادل غير مشفر، وقد تمنع الآيفون حمايات فقد البيانات من استرجاع نسخة ناقصة. هل تريد إنشاء الملف؟", true) != MessageBoxResult.Yes) return;
        var picker = new SaveFileDialog { Filter = "نسخة الآيفون (*.zip)|*.zip", FileName = $"رواتب-للآيفون-{DateTime.Now:yyyy-MM-dd-HHmmss}.zip", OverwritePrompt = true };
        if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        if (File.Exists(picker.FileName)) { Ui.Message(this, "اختر اسماً جديداً؛ لن تُستبدل نسخة موجودة."); return; }
        await Run(async () => { await Task.Run(() => IphoneInteropService.Export(workspace.Store, picker.FileName, cancellation!.Token)); Ui.Message(this, "حُفظ الملف وفُحص تطابق البيانات. احتفظ أيضاً بنسخة .salarybackup للتاريخ المحلي.\n" + picker.FileName); }, true);
    }
}
