using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// حفظ الكشوف وطباعتها من أي نافذة: يُنشأ الملف في الخلفية ويُحفظ ذرياً، ثم يُعرض فتحه.
internal static class Exports
{
    public static string WatermarkPath => Path.Combine(AppContext.BaseDirectory, "Assets", "WatermarkLogo.png");
    private static ReportAssets Assets() => ReportAssets.Load(WatermarkPath);

    public static Task SaveExcel(Window owner, string name, Func<byte[]> make) => Save(owner, name, "Excel", ".xlsx", "ملف Excel (*.xlsx)|*.xlsx", make);
    public static Task SavePdf(Window owner, string name, Func<ReportAssets, byte[]> make) => Save(owner, name, "PDF", ".pdf", "ملف PDF (*.pdf)|*.pdf", () => make(Assets()));

    private static async Task Save(Window owner, string name, string kind, string extension, string filter, Func<byte[]> make)
    {
        var dialog = new SaveFileDialog { Title = $"حفظ الكشف ({kind})", Filter = filter, FileName = ReportSheet.FileName(name) + extension, AddExtension = true, DefaultExt = extension, OverwritePrompt = true };
        if (dialog.ShowDialog(owner) != true) return;
        var destination = dialog.FileName;
        var cursor = owner.Cursor; owner.Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await Task.Run(() => ReportFiles.Save(make(), destination, extension));
            owner.Cursor = cursor;
            if (Ui.Message(owner, $"حُفظ الكشف:\n{destination}\n\nهل تريد فتحه الآن؟", "تصدير", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                // PDF يُعرض داخل البرنامج، وExcel يُفتح في برنامجه.
                if (extension == ".pdf") ShowBytes(owner, Path.GetFileNameWithoutExtension(destination), await File.ReadAllBytesAsync(destination));
                else Open(owner, destination, false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException or DllNotFoundException or TypeInitializationException)
        { owner.Cursor = cursor; Ui.Message(owner, "تعذّر إنشاء الكشف.\n" + ex.Message, "تصدير", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { owner.Cursor = cursor; }
    }

    /// المعاينة داخل البرنامج: يُنشأ الكشف في الخلفية ويُعرض في نافذة فيها الحفظ والطباعة.
    public static Task Preview(Window owner, string name, Func<ReportAssets, byte[]> make) => Show(owner, name, () => make(Assets()), false);
    /// الطباعة: نفس نافذة المعاينة ثم نافذة الطباعة مباشرة، دون عارض PDF خارجي.
    public static Task Print(Window owner, string name, Func<ReportAssets, byte[]> make) => Show(owner, name, () => make(Assets()), true);
    /// معاينة ملف PDF جاهز (مسودات الكتب والقوائم والأرشيف).
    public static Task PreviewPdf(Window owner, string name, Func<byte[]> make) => Show(owner, name, make, false);

    private static async Task Show(Window owner, string name, Func<byte[]> make, bool print)
    {
        var cursor = owner.Cursor; owner.Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var bytes = await Task.Run(make);
            owner.Cursor = cursor;
            ShowBytes(owner, name, bytes, print);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException or DllNotFoundException or TypeInitializationException)
        { owner.Cursor = cursor; Ui.Message(owner, "تعذّر تجهيز المعاينة.\n" + ex.Message, "معاينة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { owner.Cursor = cursor; }
    }
    public static void ShowBytes(Window owner, string name, byte[] bytes, bool print = false) =>
        new PdfPreviewWindow(name, bytes, name, print) { Owner = owner }.Show();

    private static void Open(Window owner, string path, bool print)
    {
        if (print)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" }); return; }
            catch (Win32Exception) { }
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            if (print) Ui.Message(owner, "فُتح الكشف في عارض PDF؛ اطبعه من هناك (Ctrl+P).", "طباعة", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { Ui.Message(owner, "حُفظ الملف لكن تعذّر فتحه؛ لا يوجد برنامج لعرض الملف.\n" + path, "تصدير", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
