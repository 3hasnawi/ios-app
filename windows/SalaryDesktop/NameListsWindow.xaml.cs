using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class NameListsWindow : Window
{
    private readonly Func<bool> dataCurrent;
    private bool working;
    private sealed record Row(int Number, string Name, string Rank, string Workplace, string Amount);
    public NameListsWindow(string month, IReadOnlyList<NameList> lists, Func<bool> dataCurrent)
    {
        InitializeComponent(); this.dataCurrent = dataCurrent; Closing += (_, e) => { if (working) e.Cancel = true; }; Heading.Text = "قوائم الأسماء · " + month;
        ListPicker.ItemsSource = lists; ListPicker.SelectedIndex = lists.Count > 0 ? 0 : -1;
    }
    private void ListChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Rows is null || Notice is null || Summary is null) return;
        if (ListPicker.SelectedItem is not NameList list) { Rows.ItemsSource = null; Summary.Text = ""; Notice.Text = "لا توجد قائمة متاحة."; return; }
        Rows.ItemsSource = list.Rows.Select(row => new Row(row.Number, row.Name, row.Rank, row.Workplace, row.Amount.ToString("N0", CultureInfo.GetCultureInfo("en-US")))).ToArray();
        Notice.Text = list.Problem ?? "قائمة للمراجعة؛ طابق الأسماء والمبالغ قبل اعتمادها.";
        ExportButton.IsEnabled = list.Problem is null && list.Rows.Count > 0;
        Summary.Text = list.Problem is null ? $"عدد القيود: {list.Rows.Count:N0}   ·   المجموع: {list.Total:N0}" : "تعذّر إعداد القائمة.";
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (working || ListPicker.SelectedItem is not NameList list || list.Problem is not null) return;
        var dialog = new SaveFileDialog { Title = "حفظ قائمة الأسماء", Filter = "ملف PDF (*.pdf)|*.pdf", FileName = "قائمة أسماء.pdf", AddExtension = true, DefaultExt = ".pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        working = true; ExportButton.IsEnabled = false; ListPicker.IsEnabled = false;
        try
        {
            var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
            var changed = await Task.Run(() => { if (!dataCurrent()) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد فتح القائمة."); NameListPdf.Save(list, File.ReadAllBytes(fontPath), dialog.FileName); return !dataCurrent(); });
            Ui.Message(this, changed ? "حُفظت قائمة سابقة وتغيّرت البيانات أثناء الحفظ؛ أعد تصديرها." : "حُفظت القائمة بعد فحص صفوفها ومجموعها. راجعها قبل الاعتماد.", "قائمة الأسماء", MessageBoxButton.OK, changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or DllNotFoundException or TypeInitializationException)
        { Ui.Message(this, "تعذّر تصدير القائمة.\n" + ex.Message, "قائمة الأسماء", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { working = false; ListPicker.IsEnabled = true; ExportButton.IsEnabled = list.Problem is null && list.Rows.Count > 0; }
    }
}
