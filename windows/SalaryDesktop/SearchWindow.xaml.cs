using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public enum SearchKind { Person, Entity, Page }

/// نتيجة في البحث الشامل. Target: اسم المنتسب أو الجهة أو وسم القسم.
public sealed record SearchItem(SearchKind Kind, string Title, string Subtitle, string Target, string Key, string TitleKey, string Icon = "")
{
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    public string Glyph => Icon.Length > 0 ? Icon : Kind switch { SearchKind.Person => "", SearchKind.Entity => "", _ => "" };
    public Brush Tint => Resource(Kind switch { SearchKind.Person => "PrimarySoft", SearchKind.Entity => "AccentSoft", _ => "TbgF1F5F4" });
    public Brush Ink => Resource(Kind switch { SearchKind.Person => "Primary", SearchKind.Entity => "Accent", _ => "Muted" });
    public string KindText => Kind switch { SearchKind.Person => "منتسب", SearchKind.Entity => "جهة تنفيذ", _ => "قسم" };
}

/// البحث الشامل (Ctrl+K): المنتسب بالاسم أو اسم الأم أو الرقم الموحد/الإحصائي، وجهات التنفيذ، والأقسام.
public partial class SearchWindow : Window
{
    private static readonly (string Title, string Tag, string Icon, string Words)[] Sections =
    [
        ("الرئيسية", "home", "", "ملخص"), ("الرواتب", "payroll", "", "رواتب صافي"), ("الاستقطاعات", "deductions", "", "قيود"), ("الملاك", "staff", "", "ملاك"),
        ("الأرشيف", "archive", "", "كتب"), ("جهات التنفيذ", "groups", "", "جهات"), ("كشف حساب البنك", "bank", "", "بنك مصرف"), ("ملفات التحويل", "transfer", "", "تحويل"),
        ("حسابات جهات التنفيذ", "accounts", "", "iban حسابات"), ("الإعدادات", "settings", "", "نسخ احتياطي"),
    ];
    private static IReadOnlyList<SearchItem> Pages => Sections.Select(s => new SearchItem(SearchKind.Page, s.Title, "الانتقال إلى القسم", s.Tag, PayrollAnalysis.Normalize(s.Title + " " + s.Words), PayrollAnalysis.Normalize(s.Title), s.Icon)).ToArray();
    private const int Limit = 150;
    private readonly BackupSnapshot? source;
    private IReadOnlyList<SearchItem> index = Pages;
    private int people, entities;
    public SearchItem? Result { get; private set; }

    public SearchWindow(BackupSnapshot? source, Task<IReadOnlyList<StaffMember>?>? staff)
    {
        InitializeComponent();
        this.source = source;
        Render();
        Loaded += async (_, _) => { Query.Focus(); await Build(staff); };
    }

    private async Task Build(Task<IReadOnlyList<StaffMember>?>? staffTask)
    {
        if (source is null) { Status.Text = "افتح نسخة الآيفون للبحث في المنتسبين والجهات؛ يمكن الآن الانتقال بين الأقسام."; return; }
        try
        {
            var staff = staffTask is null ? null : await staffTask;
            var active = source;
            var built = await Task.Run(() => Items(active, staff));
            index = built; people = built.Count(i => i.Kind == SearchKind.Person); entities = built.Count(i => i.Kind == SearchKind.Entity);
            Render();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException) { Status.Text = "تعذّر تجهيز فهرس البحث: " + ex.Message; }
    }

    internal static IReadOnlyList<SearchItem> Items(BackupSnapshot source, IReadOnlyList<StaffMember>? staff)
    {
        var items = new List<SearchItem>(Pages);
        foreach (var person in PersonProfiles.SearchIndex(source, staff))
        {
            var parts = new List<string>();
            if (person.Rank.Length > 0) parts.Add(person.Rank);
            if (person.Workplace.Length > 0) parts.Add(person.Workplace);
            if (person.Unified.Length > 0) parts.Add("الموحد " + person.Unified);
            if (person.Mother.Length > 0) parts.Add("الأم " + person.Mother);
            parts.Add(person.LatestMonth.Length > 0 ? "آخر راتب " + person.LatestMonth : "في الملاك فقط");
            items.Add(new SearchItem(SearchKind.Person, person.Name, string.Join(" · ", parts), person.Name,
                PayrollAnalysis.Normalize(string.Join(' ', person.Name, person.Mother, person.Unified, person.Stat, person.Rank, person.Workplace)), PayrollAnalysis.Normalize(person.Name)));
        }
        var latest = source.Months.Select(m => m.Name).OrderByDescending(MonthNames.SortKey).ToArray();
        var titles = source.Deductions.GroupBy(d => d.Title, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var account in source.Accounts) titles.TryAdd(account.Entity, []);
        foreach (var (title, rows) in titles)
        {
            if (string.IsNullOrWhiteSpace(title)) continue;
            var month = latest.FirstOrDefault(m => rows.Any(r => r.Month == m));
            var subtitle = month is null ? "حساب محفوظ بلا قيود" : $"{rows.Count(r => r.Month == month):N0} قيد في {month} · عرض القيود مصفّاة على الجهة";
            items.Add(new SearchItem(SearchKind.Entity, title, subtitle, title, PayrollAnalysis.Normalize(title), PayrollAnalysis.Normalize(title)));
        }
        return items;
    }

    private void QueryChanged(object sender, TextChangedEventArgs e) => Render();

    private void Render()
    {
        if (Results is null) return;
        var tokens = PayrollAnalysis.Normalize(Query.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            Results.ItemsSource = index.Where(i => i.Kind == SearchKind.Page).ToArray();
            if (source is not null && people + entities > 0) Status.Text = $"اكتب للبحث في {people:N0} منتسب و{entities:N0} جهة، أو اختر قسماً.";
        }
        else
        {
            var matches = index.Where(i => tokens.All(t => i.Key.Contains(t, StringComparison.Ordinal))).ToArray();
            Results.ItemsSource = matches
                .OrderBy(i => i.TitleKey.StartsWith(tokens[0], StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(i => i.Kind switch { SearchKind.Person => 0, SearchKind.Entity => 1, _ => 2 })
                .ThenBy(i => i.Title.Length).Take(Limit).ToArray();
            Status.Text = matches.Length == 0 ? "لا توجد نتائج؛ جرّب جزءاً من الاسم أو الرقم الموحد." :
                matches.Length > Limit ? $"{matches.Length:N0} نتيجة · تُعرض أول {Limit}؛ أضف كلمة أخرى لتضييق البحث." : $"{matches.Length:N0} نتيجة";
        }
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
    }

    private void Choose()
    {
        if (Results.SelectedItem is not SearchItem item) return;
        Result = item; DialogResult = true;
    }
    private void ResultsDoubleClick(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is DependencyObject node && ItemsControl.ContainerFromElement(Results, node) is ListBoxItem) Choose(); }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Close(); break;
            case Key.Enter: e.Handled = true; Choose(); break;
            case Key.Down or Key.Up when Results.Items.Count > 0:
                e.Handled = true;
                Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.Items.Count - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                break;
        }
    }
}
