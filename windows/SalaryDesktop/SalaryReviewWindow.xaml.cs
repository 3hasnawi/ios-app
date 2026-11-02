using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// صفحة التدقيق: أنواع الملاحظات بعددها وشدتها، حالات النوع المختار، وتفاصيل الحالة («مكمن الخلل»، جدول المقارنة، الخطوة المقترحة).
public partial class SalaryReviewWindow : Window
{
    private sealed record Category(string? Kind, string Label, int Count, string Tone, bool IsHeader) { public string CountText => Count.ToString("N0"); }
    private sealed record CaseView(ReviewIssue Issue)
    {
        public string Title => Issue.Title;
        public string Subtitle => Issue.Subtitle;
        public IReadOnlyList<ReviewTag> Tags => Issue.Tags;
        public bool Mono => Issue.MonoTitle;
        public Visibility GlyphVisibility => SalaryReviewDiagnosis.Severe.Contains(Issue.Kind) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly string[] Groups = ["مشكلات مهمة", "تحتاج مراجعة", "تغيّرات الشهر", "الملاك"];
    private readonly SalaryReviewResult result;
    private readonly Func<string, Task>? openPerson;
    private readonly string[] search;
    private readonly DispatcherTimer delay = new() { Interval = TimeSpan.FromMilliseconds(220) };

    public SalaryReviewWindow(SalaryReviewResult result, string? kind = null, Func<string, Task>? openPerson = null)
    {
        InitializeComponent();
        this.result = result; this.openPerson = openPerson;
        search = result.Issues.Select(i => i.Search).ToArray();
        Heading.Text = "تدقيق رواتب " + result.Month;
        Subheading.Text = (result.PreviousMonth is { } previous ? $"مقارنة مع {previous}" : "بلا مقارنة بشهر سابق") + $" · {result.EmployeeCount:N0} منتسب";
        Notes.Text = "ملاحظات الفحص: " + string.Join(" · ", result.Notes);
        int Count(string group) => result.Issues.Count(i => SalaryReviewDiagnosis.GroupOf(i.Kind) == group);
        SevereCount.Text = Count("مشكلات مهمة").ToString("N0"); ReviewCount.Text = Count("تحتاج مراجعة").ToString("N0");
        ChangeCount.Text = Count("تغيّرات الشهر").ToString("N0"); StaffCount.Text = Count("الملاك").ToString("N0");
        if (result.PreviousMonth is { } p) ChangeLabel.Text = "تغيّرات عن " + p;

        var categories = new List<Category>();
        foreach (var group in Groups)
        {
            var kinds = result.Issues.Where(i => SalaryReviewDiagnosis.GroupOf(i.Kind) == group).GroupBy(i => i.Kind, StringComparer.Ordinal)
                .OrderBy(g => Order(g.Key)).ToArray();
            if (kinds.Length == 0) continue;
            categories.Add(new(null, group, 0, "", true));
            var tone = group switch { "مشكلات مهمة" => "bad", "تحتاج مراجعة" => "warn", "تغيّرات الشهر" => "info", _ => "muted" };
            categories.AddRange(kinds.Select(g => new Category(g.Key, SalaryReviewDiagnosis.Label(g.Key), g.Count(), tone, false)));
        }
        CategoryList.ItemsSource = categories;
        var start = categories.FirstOrDefault(c => !c.IsHeader && c.Kind == kind) ?? categories.FirstOrDefault(c => !c.IsHeader);
        if (start is not null) CategoryList.SelectedItem = start;
        else { CasesTitle.Text = "لا توجد ملاحظات"; CasesHint.Text = "لم تظهر أي حالة في فحوص هذا الشهر."; CasesEmpty.Visibility = Visibility.Visible; ShowDetail(null); }
        delay.Tick += (_, _) => { delay.Stop(); ShowCases(); };
        Closed += (_, _) => delay.Stop();
    }

    private static int Order(string kind)
    {
        var all = SalaryReviewDiagnosis.Severe.Concat(SalaryReviewDiagnosis.Review).Concat(SalaryReviewDiagnosis.Changes).Concat(SalaryReviewDiagnosis.Staff).ToList();
        var index = all.IndexOf(kind);
        return index < 0 ? all.Count : index;
    }

    private static string Hint(string kind) => kind switch
    {
        "IBAN مكرر" => "رقم حساب واحد مسجّل لأكثر من اسم",
        "رقم مكرر" => "رقم موحد أو وظيفي مسجّل لأكثر من اسم",
        "IBAN يحتاج مراجعة" => "رقم الحساب لا يجتاز فحص IBAN العراقي",
        "IBAN فارغ" => "رقم الحساب غير موجود في الراتب",
        "حساب الصافي" => "الاستحقاق − مجموع الاستقطاعات ≠ الصافي في الملف",
        "صافي ناقص" => "خانة الصافي فارغة",
        "أسماء مكررة" => "الاسم نفسه في أكثر من صف",
        "مجموع الاستقطاعات" => "مجموع الجهات لا يطابق استقطاعات المصارف",
        "الرعاية الصحية" => "المبلغ لا يقبل القسمة على 3,500",
        "استقطاع بلا راتب" => "قيد استقطاع لاسم لا يوجد في رواتب الشهر",
        _ => "",
    };

    private void CategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is Category { IsHeader: true }) { CategoryList.SelectedItem = e.RemovedItems.OfType<Category>().FirstOrDefault(); return; }
        if (IsLoaded || CategoryList.SelectedItem is not null) ShowCases();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { delay.Stop(); delay.Start(); }

    /// بحث فارغ: حالات النوع المختار؛ وإلا: كل الحالات المطابقة من كل الأنواع.
    private void ShowCases()
    {
        var query = PayrollAnalysis.Normalize(Search.Text);
        IReadOnlyList<ReviewIssue> issues;
        if (query.Length > 0)
        {
            issues = result.Issues.Where((_, i) => search[i].Contains(query, StringComparison.Ordinal)).ToArray();
            CasesTitle.Text = $"نتائج البحث · {issues.Count:N0}"; CasesHint.Text = "من كل أنواع الملاحظات";
        }
        else if (CategoryList.SelectedItem is Category { Kind: { } kind } category)
        {
            issues = result.Issues.Where(i => i.Kind == kind).ToArray();
            CasesTitle.Text = $"{category.Label} · {issues.Count:N0}"; CasesHint.Text = Hint(kind);
        }
        else issues = [];
        CaseList.ItemsSource = issues.Select(i => new CaseView(i)).ToArray();
        CasesEmpty.Visibility = issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (issues.Count > 0) CaseList.SelectedIndex = 0; else ShowDetail(null);
    }

    private void CaseChanged(object sender, SelectionChangedEventArgs e) => ShowDetail((CaseList.SelectedItem as CaseView)?.Issue);

    private TextBlock Text(string text, double size = 13.5, bool bold = false, string? brush = null, bool wrap = true)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (brush is not null) block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    private void ShowDetail(ReviewIssue? issue)
    {
        Detail.Children.Clear();
        DetailEmpty.Visibility = issue is null ? Visibility.Visible : Visibility.Collapsed;
        DetailScroll.Visibility = issue is null ? Visibility.Collapsed : Visibility.Visible;
        if (issue is null) return;
        DetailScroll.ScrollToTop();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (SalaryReviewDiagnosis.Severe.Contains(issue.Kind))
        {
            var glyph = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 16, Margin = new Thickness(0, 0, 8, 0) };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); titleRow.Children.Add(glyph);
        }
        var title = Text(issue.Title, 18, true, wrap: false);
        if (issue.MonoTitle) { title.FontFamily = new FontFamily("Consolas"); title.FlowDirection = FlowDirection.LeftToRight; }
        titleRow.Children.Add(title);
        Detail.Children.Add(titleRow);
        var sub = Text(SalaryReviewDiagnosis.Label(issue.Kind) + (issue.Subtitle.Length > 0 ? " · " + issue.Subtitle : ""), 12.5, brush: "Muted"); sub.Margin = new Thickness(0, 3, 0, 0);
        Detail.Children.Add(sub);

        // مكمن الخلل.
        var why = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 22 };
        var label = new Run("مكمن الخلل: ") { FontWeight = FontWeights.SemiBold }; label.SetResourceReference(TextElement.ForegroundProperty, "Warn");
        why.Inlines.Add(label); why.Inlines.Add(new Run(issue.Diagnosis));
        var whyBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 12, 0, 0), Child = why };
        whyBox.SetResourceReference(Border.BackgroundProperty, "WarnSoft"); whyBox.SetResourceReference(Border.BorderBrushProperty, "WarnLine");
        Detail.Children.Add(whyBox);

        // جدول المقارنة: عمود التسمية ثم عمود لكل شخص أو شهر؛ الخانة المتعارضة باللون الأحمر والمرجعية بالأخضر.
        if (issue.Table.Count > 0)
        {
            var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            foreach (var _ in issue.Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Cell(UIElement content, int row, int column, string? background = null, bool line = true)
            {
                var border = new Border { Padding = new Thickness(10, 7, 10, 7), BorderThickness = new Thickness(0, 0, 0, line ? 1 : 0), Child = content };
                border.SetResourceReference(Border.BorderBrushProperty, "TbgE8EEED");
                if (background is not null) border.SetResourceReference(Border.BackgroundProperty, background);
                Grid.SetRow(border, row); Grid.SetColumn(border, column); grid.Children.Add(border);
            }
            var showHeader = issue.Columns.Count > 1 || issue.Columns[0] != "القيمة";
            var r = 0;
            if (showHeader)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Cell(Text("", 12.5), 0, 0, "TbgF4F7F7");
                for (int c = 0; c < issue.Columns.Count; c++) Cell(Text(issue.Columns[c], 12.5, true, "Tfg5D716E"), 0, c + 1, "TbgF4F7F7");
                r = 1;
            }
            foreach (var row in issue.Table)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Cell(Text(row.Label, 13, brush: "Muted"), r, 0);
                for (int c = 0; c < row.Cells.Count && c < issue.Columns.Count; c++)
                {
                    var cell = row.Cells[c];
                    var block = Text(cell.Text, 13, cell.Bad, cell.Bad ? "Danger" : cell.Good ? "Primary" : null);
                    if (cell.Text.StartsWith("IQ", StringComparison.Ordinal) || cell.Text.StartsWith("…", StringComparison.Ordinal)) { block.FontFamily = new FontFamily("Consolas"); block.FlowDirection = FlowDirection.LeftToRight; block.TextAlignment = TextAlignment.Right; }
                    Cell(block, r, c + 1, cell.Bad ? "DangerSoft" : cell.Good ? "PrimarySoft" : null);
                }
                r++;
            }
            Detail.Children.Add(grid);
        }

        if (issue.Action.Length > 0)
        {
            var action = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 22, Margin = new Thickness(0, 12, 0, 0) };
            action.Inlines.Add(new Run("ماذا تفعل: ") { FontWeight = FontWeights.SemiBold }); action.Inlines.Add(new Run(issue.Action));
            Detail.Children.Add(action);
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (openPerson is not null)
            foreach (var (name, index) in issue.People.Distinct(StringComparer.Ordinal).Take(4).Select((n, i) => (n, i)))
            {
                var button = new Button { Margin = new Thickness(0, 0, 8, 8), ToolTip = "فتح ملف المنتسب (القسيمة والأشهر والاستقطاعات)" };
                if (index == 0) button.Style = (Style)FindResource("PrimaryAction");
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 13, Margin = new Thickness(0, 0, 7, 0) };
                if (index == 0) icon.Foreground = Brushes.White;
                content.Children.Add(icon); content.Children.Add(new TextBlock { Text = "ملف " + name, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
                button.Content = content;
                var person = name;
                button.Click += async (_, _) => await openPerson(person);
                buttons.Children.Add(button);
            }
        var copy = new Button { Content = "نسخ التفاصيل", Margin = new Thickness(0, 0, 8, 8) };
        copy.Click += (_, _) => CopyIssue(issue);
        buttons.Children.Add(copy);
        Detail.Children.Add(buttons);
    }

    private static string Describe(ReviewIssue issue)
    {
        var lines = new List<string> { SalaryReviewDiagnosis.Label(issue.Kind) + ": " + issue.Title };
        if (issue.Subtitle.Length > 0) lines.Add(issue.Subtitle);
        lines.Add("مكمن الخلل: " + issue.Diagnosis);
        if (issue.Columns.Count > 1) lines.Add("\t" + string.Join("\t", issue.Columns));
        lines.AddRange(issue.Table.Select(r => r.Label + "\t" + string.Join("\t", r.Cells.Select(c => c.Text))));
        if (issue.Action.Length > 0) lines.Add("ماذا تفعل: " + issue.Action);
        return string.Join(Environment.NewLine, lines);
    }
    private void CopyIssue(ReviewIssue issue)
    {
        try { Clipboard.SetText(Describe(issue)); }
        catch (System.Runtime.InteropServices.ExternalException) { Ui.Message(this, "تعذّر النسخ؛ الحافظة مشغولة ببرنامج آخر.", "التدقيق"); }
    }

    /// الكشف الشامل: كل الحالات بنوعها وأسمائها وتشخيصها والخطوة المقترحة، بالشكل المعتمد للكشوف.
    private ReportSheet Sheet()
    {
        var issues = result.Issues.OrderBy(i => Array.IndexOf(Groups, SalaryReviewDiagnosis.GroupOf(i.Kind))).ThenBy(i => Order(i.Kind)).ToArray();
        var rows = issues.Select((i, n) => new[] { (n + 1).ToString("N0"), SalaryReviewDiagnosis.Label(i.Kind), i.Title, string.Join("، ", i.People), i.Diagnosis, i.Action }).ToArray();
        return new ReportSheet(ReportSheet.DefaultHeader, "تدقيق رواتب " + result.Month,
            [new("ت", 5, ReportColumnKind.Serial), new("النوع", 16, ReportColumnKind.Text), new("الحالة", 22, ReportColumnKind.Text), new("الأسماء", 26, ReportColumnKind.Text),
             new("مكمن الخلل", 48, ReportColumnKind.Text), new("ماذا تفعل", 30, ReportColumnKind.Text)],
            rows, rows.Select(_ => 0m).ToArray(), false).Fitted();
    }
    private async void ExportExcel(object sender, RoutedEventArgs e)
    {
        if (result.Issues.Count == 0) { Ui.Message(this, "لا توجد حالات لتصديرها.", "التدقيق"); return; }
        var sheet = Sheet();
        await Exports.SaveExcel(this, "تدقيق رواتب " + result.Month, () => XlsxReport.Generate(sheet));
    }
    private async void ExportPdf(object sender, RoutedEventArgs e)
    {
        if (result.Issues.Count == 0) { Ui.Message(this, "لا توجد حالات لتصديرها.", "التدقيق"); return; }
        var sheet = Sheet();
        await Exports.SavePdf(this, "تدقيق رواتب " + result.Month, assets => ReportPdf.Generate([sheet], assets, DateTime.Now));
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
