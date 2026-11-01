using System.Windows;
using System.Windows.Threading;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// دائرة التحميل في وسط الصفحة: تظهر إذا تجاوز الاحتساب ربع ثانية (حتى لا تومض مع البحث السريع)، وتخص الصفحة المعروضة فقط؛
/// المفتاح «*» لفتح النسخ والاستيراد ويظهر في كل الصفحات.
public partial class MainWindow
{
    private readonly Dictionary<string, string> busy = new(StringComparer.Ordinal);
    private DispatcherTimer? loadingDelay;
    // نتائج الرئيسية محفوظة لنفس البيانات والشهر، فالرجوع إليها لا يعيد التدقيق.
    private (BackupSnapshot Source, string Month, IReadOnlyList<StaffMember>? People, MonthSummary Totals, HomeOverview Overview)? homeCache;

    private void BeginLoading(string key, string text)
    {
        busy[key] = text;
        if (loadingDelay is null)
        {
            loadingDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            loadingDelay.Tick += (_, _) => { loadingDelay!.Stop(); UpdateLoading(true); };
        }
        if (LoadingOverlay.Visibility == Visibility.Visible) UpdateLoading(false);
        else { loadingDelay.Stop(); loadingDelay.Start(); }
    }
    private void EndLoading(string key) { busy.Remove(key); UpdateLoading(false); }

    private void UpdateLoading(bool show)
    {
        if (LoadingOverlay is null) return;
        var text = busy.TryGetValue("*", out var global) ? global : busy.TryGetValue(page, out var current) ? current : null;
        if (text is null) { LoadingOverlay.Visibility = Visibility.Collapsed; loadingDelay?.Stop(); return; }
        if (show || LoadingOverlay.Visibility == Visibility.Visible) { LoadingView.Text = text; LoadingOverlay.Visibility = Visibility.Visible; }
        else { loadingDelay?.Stop(); loadingDelay?.Start(); }
    }
}
