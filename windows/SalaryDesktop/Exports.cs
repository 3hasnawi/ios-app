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
            if (Ui.Message(owner, $"حُفظ الكشف:\n{destination}\n\nهل تريد فتحه الآن؟", "تصدير", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) Open(owner, destination, false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException or DllNotFoundException or TypeInitializationException)
        { owner.Cursor = cursor; Ui.Message(owner, "تعذّر إنشاء الكشف.\n" + ex.Message, "تصدير", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { owner.Cursor = cursor; }
    }

    /// الطباعة: ملف PDF مؤقت يُرسل إلى الطابعة الافتراضية عبر عارض PDF، أو يُفتح للطباعة منه إن لم يدعم العارض الطباعة المباشرة.
    public static async Task Print(Window owner, string name, Func<ReportAssets, byte[]> make)
    {
        var cursor = owner.Cursor; owner.Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "salary-print"); Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "*.pdf").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1))) { try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            var path = Path.Combine(folder, ReportSheet.FileName(name) + " " + DateTime.Now.ToString("HHmmss") + ".pdf");
            await Task.Run(() => ReportFiles.Save(make(Assets()), path, ".pdf"));
            owner.Cursor = cursor;
            Open(owner, path, true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException or DllNotFoundException or TypeInitializationException)
        { owner.Cursor = cursor; Ui.Message(owner, "تعذّر تجهيز الكشف للطباعة.\n" + ex.Message, "طباعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { owner.Cursor = cursor; }
    }

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
