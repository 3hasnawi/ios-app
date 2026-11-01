using System.IO;
using System.Windows;
using Microsoft.Win32;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public partial class ReferenceImportWindow : Window
{
    private byte[]? archive; private bool working;
    public byte[]? Result { get; private set; }
    public ReferenceImportWindow() { InitializeComponent(); Closing += (_, e) => { if (working) e.Cancel = true; }; }
    private async void ChooseFolder(object sender, RoutedEventArgs e)
    {
        if (working) return;
        var dialog = new OpenFolderDialog { Title = "اختيار مجلد مراجع الكتب", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        working = true; Choose.IsEnabled = false; SaveButton.IsEnabled = false; archive = null; Notice.Text = "جارٍ فحص المراجع محلياً…";
        try
        {
            var result = await Task.Run(() => { var bytes = ReferenceArchiveBuilder.Create(dialog.FolderName); return (bytes, count: LetterReferencePack.ReadArchive(bytes).Count); });
            var count = result.count; archive = result.bytes; SaveButton.IsEnabled = true;
            Notice.Text = count == 12 ? "اكتمل فحص المراجع الاثني عشر؛ راجع ثم احفظها." : "اكتمل فحص المراجع الإلزامية الأحد عشر. مرجع تنسيق الأرقام غير موجود، وقد يلزم للآيفون.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Notice.Text = "تعذّر فحص المراجع: " + ex.Message; }
        finally { working = false; Choose.IsEnabled = true; }
    }
    private void Save(object sender, RoutedEventArgs e) { if (archive is null) return; Result = archive; DialogResult = true; }
}
