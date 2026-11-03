using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// الجداول: أعمدة كل قسم (مع إظهار الإضافية وإخفائها)، التصفية السريعة، صف المجاميع، النسخ والتصدير بالشكل المعتمد،
/// والبحث الشامل وفتح ملف المنتسب.
public partial class MainWindow
{
    private enum FilterKind { Text, Rank, Month }
    private sealed record TableColumn(string Key, string Title, double Weight, bool Number, bool Shown, Func<object, object?> Value);
    private sealed record TableFilter(string Title, Func<object, string> Value, FilterKind Kind);
    private sealed record TableSpec(TableColumn[] Columns, string? Sum, TableFilter[] Filters);
    private sealed record TableResult(System.Collections.IList Rows, string Status, string[][] Options, decimal? Sum, int Missing);
    private sealed record PayrollCache(BackupSnapshot Source, string Month, PayrollRow[] Rows);

    private static TableColumn Text<T>(string key, string title, double weight, Func<T, object?> value, bool shown = true) => new(key, title, weight, false, shown, row => row is T item ? value(item) : null);
    private static TableColumn Number<T>(string key, string title, double weight, Func<T, object?> value, bool shown = true) => new(key, title, weight, true, shown, row => row is T item ? value(item) : null);
    private static TableFilter Filter<T>(string title, Func<T, string?> value, FilterKind kind = FilterKind.Text) => new(title, row => row is T item ? (value(item) ?? "").Trim() : "", kind);
    private static string DeductionState(Deduction row) => PayrollAnalysis.IsInactive(row) ? "غير محتسب" : "محتسب";

    private static readonly Dictionary<string, TableSpec> Tables = new(StringComparer.Ordinal)
    {
        ["payroll"] = new([
            Text<PayrollRow>("Name", "الاسم", 2, r => r.Name), Text<PayrollRow>("Rank", "الرتبة", 1, r => r.Rank), Text<PayrollRow>("Workplace", "محل العمل", 2, r => r.Workplace),
            Text<PayrollRow>("Payment", "طريقة الدفع", 1, r => r.Payment), Text<PayrollRow>("Unified", "الرقم الموحد", 1, r => r.Unified, false),
            Text<PayrollRow>("Grade", "الدرجة الوظيفية", 1, r => r.Grade, false), Text<PayrollRow>("Bank", "توطين المصرف", 1.5, r => r.Bank, false),
            Text<PayrollRow>("Iban", "IBAN", 2, r => r.Iban, false), Number<PayrollRow>("Gross", "الاستحقاق", 1, r => r.Gross, false),
            Number<PayrollRow>("TotalDeductions", "مجموع الاستقطاعات", 1.2, r => r.TotalDeductions, false), Number<PayrollRow>("Net", "الصافي", 1, r => r.Net)],
            "Net", [Filter<PayrollRow>("الرتبة", r => r.Rank, FilterKind.Rank), Filter<PayrollRow>("محل العمل", r => r.Workplace), Filter<PayrollRow>("طريقة الدفع", r => r.Payment)]),
        ["deductions"] = new([
            Text<Deduction>("Name", "الاسم", 2, d => d.Name), Text<Deduction>("Rank", "الرتبة", 1, d => d.Rank), Text<Deduction>("Title", "جهة التنفيذ", 2, d => d.Title),
            Number<Deduction>("Amount", "المبلغ", 1, d => d.Amount), Text<Deduction>("Note", "الملاحظة", 2, d => d.Note)],
            "Amount", [Filter<Deduction>("جهة التنفيذ", d => d.Title), Filter<Deduction>("الرتبة", d => d.Rank, FilterKind.Rank), Filter<Deduction>("الحالة", DeductionState)]),
        ["groups"] = new([
            Text<DeductionGroup>("Title", "جهة التنفيذ", 3, g => g.Title), Number<DeductionGroup>("People", "عدد الأشخاص", 1, g => g.People),
            Number<DeductionGroup>("Total", "المبلغ", 1, g => g.Total), Number<DeductionGroup>("MissingAmounts", "مبالغ ناقصة", 1, g => g.MissingAmounts)],
            "Total", []),
        ["accounts"] = new([
            Text<ExecutionAccount>("Entity", "الجهة", 2, a => a.Entity), Text<ExecutionAccount>("Branch", "المصرف والفرع", 2, a => a.Branch), Text<ExecutionAccount>("Iban", "IBAN", 2, a => a.Iban),
            Text<ExecutionAccount>("Swift", "SWIFT", 1, a => a.Swift), Text<ExecutionAccount>("LocalAccount", "الحساب المحلي", 1, a => a.LocalAccount)],
            null, [Filter<ExecutionAccount>("المصرف والفرع", a => a.Branch)]),
        ["staff"] = new([
            Text<StaffMember>("Name", "الاسم", 2, s => s.Name), Text<StaffMember>("Rank", "الرتبة", 1, s => s.Rank), Text<StaffMember>("Unified", "الرقم الموحد", 1, s => s.Unified),
            Text<StaffMember>("Stat", "الرقم الإحصائي", 1, s => s.Stat, false), Text<StaffMember>("Mother", "اسم الأم", 1.5, s => s.Mother, false),
            Text<StaffMember>("Status", "الحالة", 1, s => s.Status), Text<StaffMember>("Workplace", "محل العمل", 2, s => s.Workplace)],
            null, [Filter<StaffMember>("الحالة", s => s.Status), Filter<StaffMember>("الرتبة", s => s.Rank, FilterKind.Rank), Filter<StaffMember>("محل العمل", s => s.Workplace)]),
        ["archive"] = new([
            Text<ArchiveBook>("Title", "عنوان الكتاب", 3, b => b.Title), Text<ArchiveBook>("Month", "الشهر", 1, b => b.Month), Text<ArchiveBook>("Entity", "الجهة", 2, b => b.Entity),
            Number<ArchiveBook>("Pages", "الصفحات", 1, b => b.Pages)],
            null, [Filter<ArchiveBook>("الشهر", b => b.Month, FilterKind.Month), Filter<ArchiveBook>("الجهة", b => b.Entity)]),
    };

    private static readonly StringComparer Arabic = StringComparer.Create(CultureInfo.GetCultureInfo("ar"), false);
    private const string AllValues = "الكل";
    private volatile PayrollCache? payrollCache;
    private string?[] filterValues = [];
    private readonly List<ComboBox> filterBoxes = new();
    private bool fillingFilters;
    private string? pendingEntity;
    private decimal? shownSum;
    private int shownMissing;

    // ——— الأعمدة ———

    /// أعمدة الجدول تُبنى عند تغيّر القسم فقط، فيبقى عرض الأعمدة وترتيبها أثناء البحث وتغيير الشهر.
    private void EnsureColumns(string current)
    {
        if (columnsPage == current) return;
        columnsPage = current;
        DataTable.ItemsSource = null;
        DataTable.Columns.Clear();
        var spec = Tables.GetValueOrDefault(current);
        if (spec is not null)
        {
            var shown = ShownColumns.GetValueOrDefault(current);
            foreach (var column in spec.Columns)
            {
                var binding = new Binding(column.Key) { TargetNullValue = "—", StringFormat = column.Number ? "{0:#,0.##}" : null, ConverterCulture = Numbers };
                var col = new DataGridTextColumn
                {
                    Header = column.Title, Binding = binding, SortMemberPath = column.Key, Width = new DataGridLength(column.Weight, DataGridLengthUnitType.Star),
                    Visibility = column == spec.Columns[0] || (shown is null ? column.Shown : shown.Contains(column.Key)) ? Visibility.Visible : Visibility.Collapsed
                };
                ApplyAccountingColumnStyle(col, col.Header?.ToString() ?? "");
                DataTable.Columns.Add(col);
            }
        }
        BuildFilters(spec);
        if (pendingEntity is { } entity && current == "deductions" && filterValues.Length > 0) { filterValues[0] = entity; pendingEntity = null; }
        ColumnsButton.IsEnabled = spec is not null;
        OpenRowMenu.Header = current switch
        {
            "payroll" or "deductions" or "staff" => "ملف المنتسب", "groups" => "عرض قيود الجهة", "accounts" => "تعديل الحساب", "archive" => "فتح الكتاب", _ => "لا يوجد إجراء لهذا الجدول"
        };
        OpenRowMenu.IsEnabled = current is "payroll" or "deductions" or "staff" or "groups" or "accounts" or "archive";
        SecondRowMenu.Header = current == "payroll" ? "تفاصيل الراتب بكل الأعمدة" : "سجل الملاك الكامل";
        SecondRowMenu.Visibility = current is "payroll" or "staff" ? Visibility.Visible : Visibility.Collapsed;
        RowEntityMenu.Visibility = current is "groups" or "deductions" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyAccountingColumnStyle(DataGridTextColumn column, string headerOrKey)
    {
        var current = columnsPage ?? "";
        var numeric = Tables.GetValueOrDefault(current)?.Columns.FirstOrDefault(c => c.Key == column.SortMemberPath)?.Number ?? false;
        headerOrKey = headerOrKey + " " + column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(headerOrKey)) return;
        var net = headerOrKey.Contains("صافي", StringComparison.OrdinalIgnoreCase) || headerOrKey.Contains("الصافي", StringComparison.OrdinalIgnoreCase) || headerOrKey.Contains("Net", StringComparison.OrdinalIgnoreCase);
        var deduction = headerOrKey.Contains("استقطاع", StringComparison.OrdinalIgnoreCase) || headerOrKey.Contains("سلفة", StringComparison.OrdinalIgnoreCase) || headerOrKey.Contains("Deduction", StringComparison.OrdinalIgnoreCase) || (current == "deductions" && numeric && column.SortMemberPath == "Amount");
        var amount = numeric || headerOrKey.Contains("الاسمي", StringComparison.OrdinalIgnoreCase) || headerOrKey.Contains("مخصصات", StringComparison.OrdinalIgnoreCase) || headerOrKey.Contains("المبلغ", StringComparison.OrdinalIgnoreCase) || headerOrKey.Contains("Amount", StringComparison.OrdinalIgnoreCase) || headerOrKey.Contains("Salary", StringComparison.OrdinalIgnoreCase);
        var key = net ? "AccountingNetText" : deduction ? "AccountingDeductionText" : amount ? "AccountingNumberText" : null;
        if (key is not null && TryFindResource(key) is Style style) column.ElementStyle = style;
    }

    private static Dictionary<string, string[]> ShownColumns => UiPreferences.Current.Columns!;
    private void SaveShownColumns() { if (UiPreferences.Save() is { } error) StatusBar.Text = "تعذّر حفظ اختيار الأعمدة: " + error; }

    private void ShowColumns(object sender, RoutedEventArgs e)
    {
        if (Tables.GetValueOrDefault(page) is not { } spec) return;
        ColumnsMenu.Items.Clear();
        foreach (var column in spec.Columns.Skip(1))
        {
            var grid = DataTable.Columns.FirstOrDefault(c => c.SortMemberPath == column.Key);
            var item = new MenuItem { Header = column.Title, IsCheckable = true, StaysOpenOnClick = true, IsChecked = grid?.Visibility == Visibility.Visible, Tag = column.Key };
            item.Click += (_, _) => { if (grid is not null) { grid.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed; RememberColumns(); } };
            ColumnsMenu.Items.Add(item);
        }
        ColumnsMenu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "استعادة الأعمدة الافتراضية" };
        reset.Click += (_, _) =>
        {
            foreach (var column in spec.Columns) if (DataTable.Columns.FirstOrDefault(c => c.SortMemberPath == column.Key) is { } grid) grid.Visibility = column.Shown || column == spec.Columns[0] ? Visibility.Visible : Visibility.Collapsed;
            ShownColumns.Remove(page); SaveShownColumns();
        };
        ColumnsMenu.Items.Add(reset);
        ColumnsMenu.PlacementTarget = ColumnsButton; ColumnsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; ColumnsMenu.IsOpen = true;
    }
    private void RememberColumns()
    {
        ShownColumns[page] = DataTable.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.SortMemberPath).ToArray();
        SaveShownColumns();
    }

    // ——— التصفية السريعة ———

    private void BuildFilters(TableSpec? spec)
    {
        FilterHost.Children.Clear(); filterBoxes.Clear();
        var filters = spec?.Filters ?? [];
        filterValues = new string?[filters.Length];
        foreach (var filter in filters)
        {
            var box = new ComboBox { Width = 180, Height = 34, ToolTip = "تصفية حسب " + filter.Title, ItemsSource = new List<string> { AllValues }, SelectedIndex = 0 };
            var index = filterBoxes.Count;
            box.SelectionChanged += (_, _) =>
            {
                if (fillingFilters || index >= filterValues.Length) return;
                filterValues[index] = box.SelectedIndex <= 0 ? null : box.SelectedItem as string;
                ClearFiltersButton.Visibility = filterValues.Any(v => v is not null) ? Visibility.Visible : Visibility.Collapsed;
                RenderTable();
            };
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            cell.Children.Add(new TextBlock { Text = filter.Title, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            cell.Children.Add(box);
            FilterHost.Children.Add(cell); filterBoxes.Add(box);
        }
        FilterBar.Visibility = filters.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFiltersButton.Visibility = Visibility.Collapsed;
    }

    private void FillFilters(string[][] options)
    {
        fillingFilters = true;
        try
        {
            for (int f = 0; f < filterBoxes.Count && f < options.Length; f++)
            {
                var items = new List<string>(options[f].Length + 1) { AllValues }; items.AddRange(options[f]);
                var box = filterBoxes[f];
                if (box.ItemsSource is not List<string> old || !old.SequenceEqual(items, StringComparer.Ordinal)) box.ItemsSource = items;
                box.SelectedIndex = filterValues[f] is { } chosen ? Math.Max(0, items.IndexOf(chosen, 1)) : 0;
            }
        }
        finally { fillingFilters = false; }
        ClearFiltersButton.Visibility = filterValues.Any(v => v is not null) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearFilters(object sender, RoutedEventArgs e)
    {
        for (int f = 0; f < filterValues.Length; f++) filterValues[f] = null;
        fillingFilters = true;
        try { foreach (var box in filterBoxes) box.SelectedIndex = 0; }
        finally { fillingFilters = false; }
        ClearFiltersButton.Visibility = Visibility.Collapsed;
        RenderTable();
    }

    /// يعرض قيود جهة في صفحة الاستقطاعات مصفّاة عليها.
    private void ShowEntity(string entity)
    {
        Remember("entity", entity, entity);
        if (page == "deductions" && filterValues.Length > 0) { filterValues[0] = entity; SearchBox.Text = ""; RenderTable(); return; }
        pendingEntity = entity; NavDeductions.IsChecked = true;
    }

    // ——— الاستعلام ———

    private TableResult Query(string current, BackupSnapshot source, IReadOnlyList<StaffMember>? staffRows, string month, string[] tokens, string?[] filters)
    {
        if (Tables.GetValueOrDefault(current) is not { } spec) return new TableResult(Array.Empty<object>(), "", [], null, 0);
        if (filters.Length != spec.Filters.Length) filters = new string?[spec.Filters.Length];
        switch (current)
        {
            case "payroll":
                var cache = payrollCache; PayrollRow[] monthRows;
                if (cache is not null && ReferenceEquals(cache.Source, source) && cache.Month == month) monthRows = cache.Rows;
                else { monthRows = PayrollRows.Build(source.Employees.Where(e => e.Month == month)); payrollCache = new PayrollCache(source, month, monthRows); }
                return Result(Index("payroll", monthRows, null, r => [r.Name, r.Rank, r.Workplace, r.Payment, r.Unified, r.Iban]), null, tokens, spec, filters,
                    n => $"{n:N0} سجل · انقر نقراً مزدوجاً أو اضغط Enter لفتح ملف المنتسب؛ القيم الناقصة تظهر «—».");
            case "deductions":
                return Result(Index("deductions", source.Deductions, d => d.Month, d => [d.Name, d.Title, d.Rank, d.Note]), month, tokens, spec, filters,
                    n => $"{n:N0} قيد · تظهر القيود كما سُجلت؛ غير المحتسبة لا تدخل المجموع. النقر المزدوج يفتح ملف المنتسب.");
            case "groups":
                var groupsCache = groupCache;
                IReadOnlyList<DeductionGroup> all;
                if (groupsCache is not null && ReferenceEquals(groupsCache.Source, source) && groupsCache.Month == month) all = groupsCache.Groups;
                else { all = PayrollAnalysis.Groups(source, month); groupCache = new GroupCache(source, month, all); }
                return Result(Index("groups", all, null, g => [g.Title]), null, tokens, spec, filters,
                    n => $"{n:N0} جهة · المبالغ الناقصة لم تُحتسب. النقر المزدوج يعرض قيود الجهة، والقائمة المعتمدة من «تصدير وطباعة» أو القائمة اليمنى.");
            case "accounts":
                return Result(Index("accounts", source.Accounts, null, a => [a.Entity, a.Branch, a.Iban]), null, tokens, spec, filters,
                    n => $"{n:N0} حساب · انقر نقراً مزدوجاً لتعديل بيانات الحساب المحفوظ.");
            case "staff":
                return Result(Index("staff", staffRows ?? Array.Empty<StaffMember>(), null, v => [v.Name, v.Mother, v.Unified, v.Stat, v.Rank, v.Workplace, v.Status]), null, tokens, spec, filters,
                    n => $"{n:N0} سجل · انقر نقراً مزدوجاً لفتح ملف المنتسب.");
            case "archive":
                return Result(Index("archive", source.Books, null, b => [b.Title, b.Month, b.Entity]), null, tokens, spec, filters,
                    n => $"{n:N0} كتاب · انقر نقراً مزدوجاً على الكتاب لفتحه محلياً في عارض PDF.");
            default:
                return new TableResult(Array.Empty<object>(), "", [], null, 0);
        }
    }

    private static TableResult Result<T>(RowIndex<T> index, string? month, string[] tokens, TableSpec spec, string?[] filters, Func<int, string> status) where T : class
    {
        var options = new string[spec.Filters.Length][];
        if (spec.Filters.Length > 0)
        {
            var all = index.Query(month, []);
            for (int f = 0; f < spec.Filters.Length; f++)
            {
                var filter = spec.Filters[f];
                var values = all.Select(r => filter.Value(r)).Where(v => v.Length > 0);
                if (filters[f] is { } chosen) values = values.Append(chosen);
                var distinct = values.Distinct(StringComparer.Ordinal);
                options[f] = (filter.Kind switch
                {
                    FilterKind.Rank => distinct.OrderBy(RankOrder.Rank).ThenBy(v => v, Arabic),
                    FilterKind.Month => distinct.OrderByDescending(MonthNames.SortKey),
                    _ => distinct.OrderBy(v => v, Arabic)
                }).ToArray();
            }
        }
        var rows = index.Query(month, tokens);
        if (filters.Any(v => v is not null))
            rows = rows.Where(r =>
            {
                for (int f = 0; f < filters.Length; f++) if (filters[f] is { } wanted && spec.Filters[f].Value(r) != wanted) return false;
                return true;
            }).ToArray();
        decimal? sum = null; var missing = 0;
        if (spec.Sum is { } key && spec.Columns.FirstOrDefault(c => c.Key == key) is { } column)
        {
            sum = 0;
            foreach (var row in rows)
                switch (column.Value(row)) { case decimal amount: sum += amount; break; case int count: sum += count; break; default: missing++; break; }
        }
        return new TableResult(rows, status(rows.Length), options, sum, missing);
    }

    // ——— المجاميع والتحديد ———

    private TableColumn? SumColumn => Tables.GetValueOrDefault(page) is { Sum: { } key } spec ? spec.Columns.FirstOrDefault(c => c.Key == key) : null;

    private void UpdateTotals()
    {
        if (snapshot is null || DataTable.ItemsSource is null) { TotalsShown.Text = "—"; TotalsSelected.Text = ""; return; }
        var column = SumColumn; var count = DataTable.Items.Count;
        TotalsShown.Text = $"المعروض: {count:N0}" + (column is null || shownSum is not { } sum ? "" : $"   ·   مجموع {column.Title}: {Amount(sum)}" + (shownMissing > 0 ? $" (بلا مبلغ: {shownMissing:N0})" : ""));
        UpdateSelectionTotal();
    }
    private void UpdateSelectionTotal()
    {
        var selected = DataTable.SelectedItems; var column = SumColumn;
        if (selected.Count < 2) { TotalsSelected.Text = selected.Count == 1 ? "صف واحد محدد · حدّد أكثر من صف بـ Shift أو Ctrl لرؤية مجموعها" : ""; return; }
        decimal sum = 0;
        if (column is not null) foreach (var item in selected) switch (column.Value(item)) { case decimal amount: sum += amount; break; case int value: sum += value; break; }
        TotalsSelected.Text = $"المحدد: {selected.Count:N0}" + (column is null ? "" : $"   ·   مجموعه {Amount(sum)}");
    }
    private void TableSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionTotal();

    // ——— النسخ والتصدير ———

    private static string Cell(object? value) => value switch
    {
        null => "", decimal amount => Amount(amount), int count => count.ToString("#,0", Numbers), string text => text, _ => Convert.ToString(value, Numbers) ?? ""
    };

    private (TableSpec Spec, TableColumn[] Columns, object[] Items)? Displayed(bool selectedOnly)
    {
        if (Tables.GetValueOrDefault(page) is not { } spec || DataTable.ItemsSource is null) return null;
        var columns = DataTable.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex)
            .Select(c => spec.Columns.FirstOrDefault(s => s.Key == c.SortMemberPath)).OfType<TableColumn>().ToArray();
        var items = DataTable.Items.Cast<object>();
        if (selectedOnly && DataTable.SelectedItems.Count > 0)
        {
            var chosen = new HashSet<object>(DataTable.SelectedItems.Cast<object>(), ReferenceEqualityComparer.Instance);
            items = items.Where(chosen.Contains);
        }
        return (spec, columns, items.ToArray());
    }

    /// نسخ بصيغة يلصقها Excel مباشرة: العناوين ثم الصفوف، والمبالغ أرقام بلا فواصل.
    private void CopyRows(object sender, RoutedEventArgs e)
    {
        if (Displayed(true) is not { } shown || shown.Items.Length == 0) return;
        var text = new StringBuilder();
        text.AppendJoin('\t', shown.Columns.Select(c => c.Title)).Append("\r\n");
        foreach (var item in shown.Items)
            text.AppendJoin('\t', shown.Columns.Select(c => c.Value(item) switch
            {
                decimal amount => amount.ToString("0.##", CultureInfo.InvariantCulture), int count => count.ToString(CultureInfo.InvariantCulture),
                var value => Cell(value).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')
            })).Append("\r\n");
        try
        {
            Clipboard.SetText(text.ToString());
            StatusBar.Text = DataTable.SelectedItems.Count > 0 ? $"نُسخ {shown.Items.Length:N0} صف مع العناوين؛ الصقه في Excel." : $"لم تُحدد صفوف، فنُسخ المعروض كاملاً ({shown.Items.Length:N0} صف).";
        }
        catch (System.Runtime.InteropServices.ExternalException ex) { StatusBar.Text = "تعذّر النسخ: " + ex.Message; }
    }

    private string TableTitle()
    {
        var month = ReportSheet.Eastern(SelectedMonth);
        var title = page switch
        {
            "payroll" => $"رواتب شهر {month}", "deductions" => $"قيود الاستقطاع لـ شهر {month}", "groups" => $"مبالغ الاستقطاع لكل جهة لـ شهر {month}",
            "accounts" => "حسابات جهات التنفيذ", "staff" => "سجلات الملاك", "archive" => "الكتب المؤرشفة", _ => PageTitle.Text
        };
        var filters = filterValues.OfType<string>().ToArray();
        if (filters.Length > 0) title += " - " + string.Join(" - ", filters);
        if (SearchBox.Text.Trim() is { Length: > 0 } search) title += $" (بحث: {search})";
        return title;
    }

    private (ReportSheet Sheet, string Name)? TableSheet()
    {
        var source = snapshot;
        if (source is null || Displayed(false) is not { } shown) return null;
        try
        {
            var amountIndex = shown.Spec.Sum is { } key ? Array.FindIndex(shown.Columns, c => c.Key == key) : -1;
            var rows = shown.Items.Select(item => shown.Columns.Select(c => Cell(c.Value(item))).ToArray()).ToArray();
            var amounts = amountIndex >= 0 ? shown.Items.Select(item => shown.Columns[amountIndex].Value(item) switch { decimal amount => amount, int count => count, _ => (decimal?)null }).ToArray() : [];
            var title = TableTitle();
            return (TableReport.Build(ReportSheet.HeaderFor(source), title, shown.Columns.Select(c => c.Title).ToArray(), amountIndex, rows, amounts), title);
        }
        catch (InvalidDataException ex) { Ui.Message(this, ex.Message, "تصدير الجدول", MessageBoxButton.OK, MessageBoxImage.Information); return null; }
    }
    private async void PreviewTable(object sender, RoutedEventArgs e) { if (TableSheet() is { } report) await Exports.Preview(this, report.Name, assets => ReportPdf.Generate([report.Sheet], assets, DateTime.Now)); }
    private async void ExportTableExcel(object sender, RoutedEventArgs e) { if (TableSheet() is { } report) await Exports.SaveExcel(this, report.Name, () => XlsxReport.Generate(report.Sheet)); }
    private async void ExportTablePdf(object sender, RoutedEventArgs e) { if (TableSheet() is { } report) await Exports.SavePdf(this, report.Name, assets => ReportPdf.Generate([report.Sheet], assets, DateTime.Now)); }
    private async void PrintTable(object sender, RoutedEventArgs e) { if (TableSheet() is { } report) await Exports.Print(this, report.Name, assets => ReportPdf.Generate([report.Sheet], assets, DateTime.Now)); }

    private string? CurrentEntity() => page switch
    {
        "groups" => (DataTable.SelectedItem as DeductionGroup)?.Title,
        "deductions" => filterValues.ElementAtOrDefault(0) ?? (DataTable.SelectedItems.Cast<object>().OfType<Deduction>().Select(d => d.Title).Distinct().ToArray() is [var only] ? only : null),
        _ => null
    };
    private void RefreshEntityMenus()
    {
        var entity = CurrentEntity(); var applies = page is "groups" or "deductions";
        var header = entity is null ? (page == "groups" ? "قائمة استقطاعات الجهة المعتمدة (اختر جهة)" : "قائمة استقطاعات الجهة المعتمدة (صفِّ على جهة)") : $"قائمة استقطاعات «{entity}» المعتمدة";
        foreach (var menu in new[] { EntityExportMenu, RowEntityMenu }) { menu.Header = header; menu.IsEnabled = entity is not null; menu.Visibility = applies ? Visibility.Visible : Visibility.Collapsed; }
        EntityExportSeparator.Visibility = applies ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowExportMenu(object sender, RoutedEventArgs e)
    {
        RefreshEntityMenus();
        var hasRows = DataTable.Items.Count > 0 && snapshot is not null;
        foreach (var item in ExportMenu.Items.OfType<MenuItem>().Where(m => m != EntityExportMenu)) item.IsEnabled = hasRows;
        ExportCopyMenu.Header = DataTable.SelectedItems.Count > 0 ? $"نسخ الصفوف المحددة ({DataTable.SelectedItems.Count:N0}) للصقها في Excel" : "نسخ المعروض للصقه في Excel";
        ExportMenu.PlacementTarget = ExportButton; ExportMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; ExportMenu.IsOpen = true;
    }
    private void RowMenuOpened(object sender, RoutedEventArgs e)
    {
        RefreshEntityMenus();
        CopyRowsMenu.Header = DataTable.SelectedItems.Count > 1 ? $"نسخ الصفوف المحددة ({DataTable.SelectedItems.Count:N0})" : "نسخ الصف";
        CopyRowsMenu.IsEnabled = DataTable.SelectedItems.Count > 0;
    }
    private (ReportSheet Sheet, string Name)? EntitySheet()
    {
        var source = snapshot; var month = SelectedMonth;
        if (source is null || month.Length == 0 || CurrentEntity() is not { } entity) return null;
        try { var sheet = StandardReports.EntityDeductions(source, month, entity); return (sheet, sheet.Title); }
        catch (InvalidDataException ex) { Ui.Message(this, ex.Message, "قائمة الجهة", MessageBoxButton.OK, MessageBoxImage.Information); return null; }
    }
    private async void PreviewEntity(object sender, RoutedEventArgs e) { if (EntitySheet() is { } report) await Exports.Preview(this, report.Name, assets => ReportPdf.Generate([report.Sheet], assets, DateTime.Now)); }
    private async void ExportEntityExcel(object sender, RoutedEventArgs e) { if (EntitySheet() is { } report) await Exports.SaveExcel(this, report.Name, () => XlsxReport.Generate(report.Sheet)); }
    private async void ExportEntityPdf(object sender, RoutedEventArgs e) { if (EntitySheet() is { } report) await Exports.SavePdf(this, report.Name, assets => ReportPdf.Generate([report.Sheet], assets, DateTime.Now)); }
    private async void PrintEntity(object sender, RoutedEventArgs e) { if (EntitySheet() is { } report) await Exports.Print(this, report.Name, assets => ReportPdf.Generate([report.Sheet], assets, DateTime.Now)); }

    // ——— البحث الشامل وملف المنتسب ———

    /// الملاك من الذاكرة إن قُرئت لهذه النسخة، وإلا تُقرأ في الخلفية وتُحفظ للصفحات الأخرى.
    private Task<IReadOnlyList<StaffMember>?> StaffFor(BackupSnapshot source)
    {
        if (staffDigest == source.SourceDigest && staff is not null) return Task.FromResult<IReadOnlyList<StaffMember>?>(staff);
        return source.StaffCount == 0 ? Task.FromResult<IReadOnlyList<StaffMember>?>(null) : ReadStaff(source);
    }
    private async Task<IReadOnlyList<StaffMember>?> ReadStaff(BackupSnapshot source)
    {
        try
        {
            var loaded = await Task.Run(() => StaffRecords.Read(source));
            if (snapshot?.SourceDigest == source.SourceDigest && staffLoadingDigest is null) { staff = loaded; staffDigest = source.SourceDigest; staffError = null; }
            return loaded;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or JsonException) { return null; }
    }

    private async Task OpenPerson(string name, string? month)
    {
        var source = snapshot; if (source is null) return;
        var cursor = Cursor; Cursor = Cursors.Wait;
        try
        {
            var people = await StaffFor(source);
            var profile = await Task.Run(() => PersonProfiles.Build(source, name, people));
            Cursor = cursor;
            new PersonWindow(source, profile, month) { Owner = this }.Show();
            Remember("person", name, name);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
        { Cursor = cursor; Ui.Message(this, "تعذّر فتح ملف المنتسب.\n" + ex.Message, "ملف المنتسب", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Cursor = cursor; }
    }

    private async void OpenGlobalSearch(object sender, RoutedEventArgs e)
    {
        var source = snapshot;
        var search = new SearchWindow(source, source is null ? null : StaffFor(source)) { Owner = this };
        if (search.ShowDialog() != true || search.Result is not { } result) return;
        switch (result.Kind)
        {
            case SearchKind.Person when ReferenceEquals(source, snapshot): await OpenPerson(result.Target, null); break;
            case SearchKind.Entity when ReferenceEquals(source, snapshot): ShowEntity(result.Target); break;
            case SearchKind.Page: if (NavItems.FirstOrDefault(item => Equals(item.Tag, result.Target)) is { } radio) radio.IsChecked = true; break;
        }
    }

    private void OpenSecondRow(object sender, RoutedEventArgs e)
    {
        if (page == "payroll" && DataTable.SelectedItem is PayrollRow row) new EmployeeDetailsWindow(row.Employee, snapshot) { Owner = this }.ShowDialog();
        else if (page == "staff" && DataTable.SelectedItem is StaffMember member) new StaffDetailsWindow(member) { Owner = this }.ShowDialog();
    }
}
