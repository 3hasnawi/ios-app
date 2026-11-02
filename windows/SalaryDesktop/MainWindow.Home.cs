using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// الرئيسية «ماذا أفعل الآن» والقائمة الجانبية العائمة.
public partial class MainWindow
{
    private sealed record CategoryTile(string Name, int Count) { public string Text => $"{Name} {Count:N0}"; }
    /// خطوة مقترحة: Warn تعني أنها تحتاج انتباهاً (تُقدَّم على غيرها وتظهر بلون التنبيه).
    private sealed record HomeStep(string Glyph, string Title, string Detail, string Action, bool Warn, System.Action Run);
    private sealed record RecentChip(RecentItem Item)
    {
        public string Glyph => Item.Kind switch { "person" => "", "book" => "", _ => "" };
        public string Label => Item.Label;
        public string Hint => (Item.Kind switch { "person" => "ملف المنتسب", "book" => "كتاب في الأرشيف", _ => "قيود الجهة" }) + " · فُتح " + Ago(Item.At);
    }
    private sealed record ToolTile(string Key, string Glyph, string Title, string Tint, string Hint, bool Pinned)
    {
        public string PinText => Pinned ? "إلغاء التثبيت" : "تثبيت في البداية";
        public Visibility PinVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;
    }

    /// كل أدوات الرئيسية بترتيبها الافتراضي؛ يظهر منها ستّ: المثبتة أولاً ثم الأكثر استخداماً.
    private static readonly (string Key, string Glyph, string Title, string Tint, string Hint)[] ToolCatalog =
    [
        ("groups", "", "جهات التنفيذ", "primary", "مبالغ الاستقطاع لكل جهة وقوائمها المعتمدة"),
        ("employee", "", "كشف استقطاعات موظف", "accent", "استقطاع جهة واحدة لموظف عبر الأشهر"),
        ("items", "", "بنود الرواتب", "accent", "بنود الرواتب للشهر المختار"),
        ("fund", "", "صندوق الشهداء", "warn", "الكشف الخاص بالشكل المعتمد"),
        ("trust", "", "أمانات الرواتب", "primary", "أمانات رواتب الكاش غير المستلمة"),
        ("transfer", "", "ملفات التحويل", "primary", "ملفات المصارف والكتب للشهر المختار"),
        ("health", "", "الرعاية الصحية", "warn", "الكشف الخاص بالشكل المعتمد"),
        ("food", "", "عقد الإطعام", "warn", "الكشف الخاص بالشكل المعتمد"),
        ("audit", "", "تدقيق الرواتب", "warn", "ملاحظات الشهر ومقارنته بالشهر السابق"),
        ("ledger", "", "دفتر المطابقة", "primary", "فترات مطابقة حساب البنك وكشوفها"),
        ("bank", "", "كشف البنك", "primary", "مبالغ الشهر للمراجعة مع المصرف"),
        ("archive", "", "الأرشيف", "accent", "الكتب والإشعارات"),
        ("accounts", "", "حسابات الجهات", "accent", "IBAN والفروع المحفوظة لجهات التنفيذ"),
    ];

    private HomeStep? nextStep;
    private (BackupSnapshot Source, string Month, BankSummary? Summary, string? Error)? homeBank;

    private static string Greeting() => DateTime.Now.Hour < 12 ? "صباح الخير" : "مساء الخير";
    private static string HomeSubtitle(string month)
    {
        var backup = UiPreferences.Current.BackupLast is { } last ? "آخر نسخة احتياطية " + Ago(last) : "لم تُحفظ نسخة احتياطية بعد";
        return month.Length > 0 ? $"{month} · {backup}" : backup;
    }

    // ——— الرئيسية ———

    private async void RenderHome()
    {
        var ticket = ++homeRequest; var source = snapshot; var month = SelectedMonth;
        RenderRecents(); RenderTools();
        if (source is null || month.Length == 0)
        {
            NetLabel.Text = "صافي الرواتب"; NetAmount.Text = "—"; DeltaNet.Text = source is null ? "لا توجد بيانات مفتوحة" : "اختر شهراً";
            PeopleCount.Text = "—"; DeltaPeople.Text = ""; DeductionTotal.Text = "—"; DeductionDetail.Text = "";
            BankHomeTotal.Text = "—"; BankHomeHint.Text = ""; BankHomeIcon.Visibility = Visibility.Collapsed; DataWarning.Visibility = Visibility.Collapsed;
            HomeCategories.ItemsSource = FundReport.Categories.Select(c => new CategoryTile(c.Name, 0)).ToArray();
            RenderNextSteps(); return;
        }
        var people = staffDigest == source.SourceDigest ? staff : null;
        var cached = homeCache is { } hit && ReferenceEquals(hit.Source, source) && hit.Month == month && ReferenceEquals(hit.People, people) ? hit : ((BackupSnapshot, string, IReadOnlyList<StaffMember>?, MonthSummary, HomeOverview)?)null;
        if (cached is null) { BeginLoading("home", "جارٍ احتساب ملخص الشهر…"); RenderNextSteps(); }
        try
        {
            var totals = cached?.Item4 ?? await Task.Run(() => PayrollAnalysis.Summarize(source, month));
            if (ticket != homeRequest || !ReferenceEquals(source, snapshot) || month != SelectedMonth) return;
            NetLabel.Text = totals.MissingNet > 0 ? "صافي الرواتب · بيانات ناقصة" : "صافي الرواتب";
            NetAmount.Text = totals.Employees == 0 || totals.MissingNet == totals.Employees ? "غير متاح" : Amount(totals.NetTotal);
            PeopleCount.Text = totals.Employees == 0 ? "لا رواتب" : totals.Employees.ToString("N0", Numbers);
            DeductionTotal.Text = totals.Deductions == 0 ? "لا قيود" : Amount(totals.DeductionTotal);
            DeductionDetail.Text = totals.Deductions == 0 ? "لم تُستورد استقطاعات لهذا الشهر" : $"{totals.Deductions:N0} قيد · {totals.Entities:N0} جهة" + (totals.MissingAmounts > 0 ? $" · {totals.MissingAmounts:N0} بلا مبلغ" : "");
            DataWarning.Text = totals.MissingNet > 0 || totals.MissingAmounts > 0 ? $"تنبيه: {totals.MissingNet:N0} راتب بلا صافي و{totals.MissingAmounts:N0} قيد بلا مبلغ؛ المجاميع أولية." : "";
            DataWarning.Visibility = DataWarning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var overview = cached?.Item5 ?? await Task.Run(() => HomeOverviews.Build(source, month, people));
            if (ticket != homeRequest || !ReferenceEquals(source, snapshot) || month != SelectedMonth) return;
            homeCache = (source, month, people, totals, overview);
            HomeCategories.ItemsSource = overview.Categories.Select(c => new CategoryTile(c.Name, c.Count)).ToArray();
            if (overview.PreviousMonth is { } previous)
            {
                DeltaNet.Text = overview.NetChange is { } change ? (change == 0 ? $"بلا تغيير عن {previous}" : $"{(change > 0 ? "▲" : "▼")} {Amount(Math.Abs(change))} عن {previous}") : $"لا مقارنة مع {previous} (مبالغ ناقصة)";
                DeltaPeople.Text = overview.PeopleChange == 0 ? "بلا تغيير ·" : $"{(overview.PeopleChange > 0 ? "+" : "−")}{Math.Abs(overview.PeopleChange):N0} ·";
            }
            else { DeltaNet.Text = "لا يوجد شهر سابق للمقارنة"; DeltaPeople.Text = ""; }
            var severeAlerts = overview.Alerts.Where(a => SevereKinds.Contains(a.Kind)).ToArray();
            ShowAuditNotice(month, severeAlerts.Sum(a => a.Count), severeAlerts.Select(a => $"{a.Kind} ({a.Count:N0})"));
            RenderNextSteps();
            // مبلغ المراجعة مع المصرف يُحسب بعد ملخص الشهر حتى لا يؤخر ظهور الأرقام الأخرى.
            if (homeBank is not { } bank || !ReferenceEquals(bank.Source, source) || bank.Month != month)
            {
                BankHomeTotal.Text = "…"; BankHomeHint.Text = "جارٍ الاحتساب"; BankHomeIcon.Visibility = Visibility.Collapsed;
                try { homeBank = (source, month, await Task.Run(() => BankStatementCalculator.Calculate(source, month)), null); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or OverflowException) { homeBank = (source, month, null, ex.Message); }
                if (ticket != homeRequest || !ReferenceEquals(source, snapshot) || month != SelectedMonth) return;
            }
            ShowHomeBank();
            RenderNextSteps();
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or System.Text.Json.JsonException)
        {
            if (ticket == homeRequest) { NetAmount.Text = "غير متاح"; DataWarning.Text = "تعذّر حساب الملخص: " + ex.Message; DataWarning.Visibility = Visibility.Visible; }
        }
        finally { if (ticket == homeRequest) EndLoading("home"); }
    }

    private void ShowHomeBank()
    {
        if (homeBank is not { } bank) return;
        if (bank.Summary is not { } summary) { BankHomeTotal.Text = "غير متاح"; BankHomeHint.Text = bank.Error ?? ""; BankHomeIcon.Visibility = Visibility.Collapsed; return; }
        BankHomeTotal.Text = Amount(summary.BankTotal);
        var matched = summary.ExecutionMatched;
        BankHomeHint.Text = matched ? "التنفيذ متطابق" : summary.ExecutionCount == 0 ? "لا ملف جهات تنفيذ للمطابقة" : $"فرق التنفيذ {Amount(summary.ExecutionDifference)}";
        BankHomeHint.SetResourceReference(TextBlock.ForegroundProperty, matched ? "Primary" : summary.ExecutionCount == 0 ? "Muted" : "Warn");
        BankHomeIcon.Text = matched ? "" : "";
        BankHomeIcon.SetResourceReference(TextBlock.ForegroundProperty, matched ? "Primary" : "Warn");
        BankHomeIcon.Visibility = summary.ExecutionCount == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ——— الخطوة التالية ———

    /// تُختار من حالة الشهر: ملاحظات التدقيق المهمة، فترة مطابقة قيد العمل، تعديلات غير محفوظة، فرق التنفيذ، ثم الخطوة المعتادة.
    private void RenderNextSteps()
    {
        if (!IsInitialized || NextCard is null) return;
        var source = snapshot; var month = SelectedMonth;
        StartFromExcelButton.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        var steps = new List<HomeStep>();
        if (source is null)
        {
            nextStep = null;
            ShowNext("", "ابدأ بملفاتك", "افتح النسخة الاحتياطية من الآيفون، أو ملف رواتب أو استقطاعات Excel، أو اسحبها إلى النافذة. تُحفظ البيانات محلياً دون تعديل الملف الأصلي.", null, false);
            LaterSteps.ItemsSource = null; return;
        }
        var overview = homeCache is { } hit && ReferenceEquals(hit.Source, source) && hit.Month == month ? hit.Overview : null;
        if (overview is not null)
        {
            var severe = overview.Alerts.Where(a => SevereKinds.Contains(a.Kind)).ToArray();
            if (severe.Length > 0)
                steps.Add(new("", $"راجع {severe.Sum(a => a.Count):N0} ملاحظة مهمة في التدقيق", string.Join(" · ", severe.Take(3).Select(a => $"{a.Count:N0} {a.Kind}")) + " · النقر يفتح التدقيق مصفّى عليها", "فتح التدقيق", true, () => OpenSalaryReview(severe[0].Kind)));
            else if (overview.AlertError is { } error)
                steps.Add(new("", "تعذّر تدقيق الشهر", error, "إعادة المحاولة", true, () => OpenSalaryReview((string?)null)));
        }
        try
        {
            var periods = reconciliationStore.Load(source).Periods;
            var open = periods.Where(p => !p.IsConfirmed).ToArray();
            RefreshBankBadge(open.Length);
            if (open.OrderByDescending(p => p.Start).FirstOrDefault() is { } period)
            {
                var card = PeriodCard.From(period);
                steps.Add(new("", $"أكمل مطابقة حساب {period.Month}", card.StatusText + " · بعد التأشير يُعتمد الكشف ويُحفظ", "متابعة المطابقة", true, () => OpenLedger(period.Id, false)));
            }
            else if (periods.Count > 0 && MonthNames.Previous(month) is { } previous && !periods.Any(p => MonthNames.SortKey(p.Month) == previous)
                && source.Months.FirstOrDefault(m => MonthNames.SortKey(m.Name) == previous) is { } previousMonth)
                steps.Add(new("", $"ابدأ مطابقة حساب {previousMonth.Name}", "كل الفترات السابقة معتمدة؛ أضف فترة الشهر السابق في دفتر المطابقة", "فترة جديدة", false, () => OpenLedger(null, true)));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { RefreshBankBadge(0); }
        if (BackupReminder.Visibility == Visibility.Visible)
            steps.Add(new("", BackupReminderTitle.Text, BackupReminderText.Text, "حفظ الآن", true, () => BackupNow(this, new RoutedEventArgs())));
        if (homeBank is { } bank && ReferenceEquals(bank.Source, source) && bank.Month == month && bank.Summary is { ExecutionCount: > 0, ExecutionMatched: false } summary)
            steps.Add(new("", "استقطاعات المصارف لا تطابق ملف جهات التنفيذ", $"الفرق {Amount(summary.ExecutionDifference)} · قيود بلا مبلغ {summary.MissingExecutionAmounts:N0}", "كشف البنك", true, () => NavBank.IsChecked = true));
        if (overview is not null && overview.Alerts.Where(a => !SevereKinds.Contains(a.Kind)).ToArray() is { Length: > 0 } changes)
            steps.Add(new("", $"{changes.Sum(a => a.Count):N0} تغيّر في الشهر للمراجعة", string.Join(" · ", changes.Take(3).Select(a => $"{a.Count:N0} {a.Kind}")), "عرض", false, () => OpenSalaryReview(changes[0].Kind)));
        var routine = new HomeStep("", $"حضّر ملفات التحويل لـ {month}", "ملفات المصارف والكتب للشهر المختار", "ملفات التحويل", false, () => NavTransfer.IsChecked = true);
        steps.Add(routine);
        var ordered = steps.Where(s => s.Warn).Concat(steps.Where(s => !s.Warn)).ToList();
        nextStep = ordered[0];
        if (ReferenceEquals(nextStep, routine))
            ShowNext("", $"لا شيء عاجل في {month}", "لا ملاحظات مهمة، ولا مطابقة معلّقة، ولا تعديلات غير محفوظة. الخطوة المعتادة التالية: ملفات التحويل.", nextStep.Action, false);
        else ShowNext(nextStep.Glyph, nextStep.Title, nextStep.Detail, nextStep.Action, nextStep.Warn);
        LaterSteps.ItemsSource = ordered.Skip(1).Take(3).ToArray();
    }

    private void ShowNext(string glyph, string title, string detail, string? action, bool warn)
    {
        NextIcon.Text = glyph; NextTitle.Text = title; NextDetail.Text = detail;
        NextAction.Text = action ?? ""; NextButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        NextCard.SetResourceReference(Border.BorderBrushProperty, warn ? "WarnLine" : "TbgCFD9DA");
        NextIconBox.SetResourceReference(Border.BackgroundProperty, warn ? "WarnSoft" : "PrimarySoft");
        NextIcon.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Warn" : "Primary");
    }
    private void RunNextStep(object sender, RoutedEventArgs e) => nextStep?.Run();
    private void RunLaterStep(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: HomeStep step }) step.Run(); }

    private void RefreshBankBadge(int open)
    {
        BankBadgeText.Text = open.ToString("N0", Numbers);
        BankBadge.Visibility = open > 0 && !UiPreferences.Current.SidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    // ——— فُتح مؤخراً ———

    private void Remember(string kind, string key, string label)
    {
        if (snapshot is not { } source || string.IsNullOrWhiteSpace(key)) return;
        var list = UiPreferences.Current.Recent!;
        list.RemoveAll(r => r.Digest == source.SourceDigest && r.Kind == kind && r.Key == key);
        list.Insert(0, new RecentItem(source.SourceDigest, kind, key, label.Trim(), DateTime.Now));
        if (list.Count > 40) list.RemoveRange(40, list.Count - 40);
        UiPreferences.Save();
        if (page == "home") RenderRecents();
    }
    private void RenderRecents()
    {
        var digest = snapshot?.SourceDigest;
        var items = digest is null ? Array.Empty<RecentChip>() : UiPreferences.Current.Recent!.Where(r => r.Digest == digest).Take(5).Select(r => new RecentChip(r)).ToArray();
        RecentItems.ItemsSource = items;
        RecentRow.Visibility = items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void OpenRecent(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecentChip { Item: var item } } || snapshot is not { } source) return;
        switch (item.Kind)
        {
            case "person": await OpenPerson(item.Key, null); break;
            case "entity": ShowEntity(item.Key); break;
            case "book":
                if (Guid.TryParse(item.Key, out var id) && source.Books.FirstOrDefault(b => b.Id == id) is { } book)
                {
                    try { var bytes = await Task.Run(() => BookBytes(source, book)); Exports.ShowBytes(this, book.Title, bytes); Remember("book", item.Key, book.Title); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                    { Ui.Message(this, "تعذّر فتح الكتاب.\n" + ex.Message, "الأرشيف", MessageBoxButton.OK, MessageBoxImage.Warning); }
                }
                else { Forget(item); Ui.Message(this, "لم يعد هذا الكتاب في الأرشيف؛ أُزيل من «فُتح مؤخراً».", "الأرشيف"); }
                break;
        }
    }
    private void Forget(RecentItem item) { UiPreferences.Current.Recent!.Remove(item); UiPreferences.Save(); RenderRecents(); }
    private void ForgetRecent(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: RecentChip chip }) Forget(chip.Item); }
    private void ClearRecent(object sender, RoutedEventArgs e)
    {
        if (snapshot is not { } source) return;
        UiPreferences.Current.Recent!.RemoveAll(r => r.Digest == source.SourceDigest); UiPreferences.Save(); RenderRecents();
    }

    // ——— أدواتك ———

    private void RenderTools()
    {
        var saved = UiPreferences.Current;
        var index = ToolCatalog.Select((t, i) => (t.Key, i)).ToDictionary(p => p.Key, p => p.i, StringComparer.Ordinal);
        var pinned = saved.ToolPins!.Where(index.ContainsKey).ToList();
        var rest = ToolCatalog.Where(t => !pinned.Contains(t.Key) && !saved.ToolHidden!.Contains(t.Key))
            .OrderByDescending(t => saved.ToolUse!.GetValueOrDefault(t.Key)).ThenBy(t => index[t.Key]).Select(t => t.Key);
        HomeTools.ItemsSource = pinned.Concat(rest).Take(6).Select(key => Tile(key, pinned.Contains(key))).ToArray();
    }
    private static ToolTile Tile(string key, bool pinned)
    {
        var t = ToolCatalog.First(c => c.Key == key);
        return new ToolTile(t.Key, t.Glyph, t.Title, t.Tint, t.Hint, pinned);
    }
    private async void RunTool(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToolTile tile }) await RunTool(tile.Key);
    }
    private async Task RunTool(string key)
    {
        var use = UiPreferences.Current.ToolUse!;
        use[key] = use.GetValueOrDefault(key) + 1; UiPreferences.Save();
        switch (key)
        {
            case "groups": case "transfer": case "bank": case "archive": case "accounts":
                if (NavItems.FirstOrDefault(item => Equals(item.Tag, key)) is { } radio) radio.IsChecked = true; break;
            case "employee": OpenEmployeeDeductions(this, new RoutedEventArgs()); break;
            case "items": await ShowSalaryItems(null); break;
            case "fund": case "health": case "food": await ShowSalaryItems(key); break;
            case "trust": OpenSalaryTrust(this, new RoutedEventArgs()); break;
            case "audit": OpenSalaryReview((string?)null); break;
            case "ledger": OpenLedger(null, false); break;
        }
    }
    private void PinTool(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ToolTile tile }) return;
        var pins = UiPreferences.Current.ToolPins!;
        if (!pins.Remove(tile.Key)) pins.Add(tile.Key);
        UiPreferences.Current.ToolHidden!.Remove(tile.Key);
        UiPreferences.Save(); RenderTools();
    }
    private void HideTool(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ToolTile tile }) return;
        UiPreferences.Current.ToolPins!.Remove(tile.Key);
        if (!UiPreferences.Current.ToolHidden!.Contains(tile.Key)) UiPreferences.Current.ToolHidden.Add(tile.Key);
        UiPreferences.Save(); RenderTools();
        StatusBar.Text = $"أُخفيت «{tile.Title}» من الرئيسية؛ تجدها في «كل الأدوات».";
    }
    private void ResetTools(object sender, RoutedEventArgs e)
    {
        var saved = UiPreferences.Current;
        saved.ToolPins!.Clear(); saved.ToolHidden!.Clear(); saved.ToolUse!.Clear();
        UiPreferences.Save(); RenderTools();
    }
    /// قائمة بكل الأدوات (المخفية معلَّمة)، والنقر يشغّل الأداة.
    private void ShowAllTools(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AllToolsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, FlowDirection = FlowDirection.RightToLeft };
        var hidden = UiPreferences.Current.ToolHidden!;
        foreach (var tool in ToolCatalog)
        {
            var key = tool.Key;
            var item = new MenuItem { Header = tool.Title + (hidden.Contains(key) ? "  (مخفية)" : ""), Icon = new TextBlock { Text = tool.Glyph, Style = (Style)FindResource("Icon") } };
            item.Click += async (_, _) => await RunTool(key);
            menu.Items.Add(item);
        }
        if (hidden.Count > 0)
        {
            menu.Items.Add(new Separator());
            var show = new MenuItem { Header = "إظهار الأدوات المخفية" };
            show.Click += (_, _) => { UiPreferences.Current.ToolHidden!.Clear(); UiPreferences.Save(); RenderTools(); };
            menu.Items.Add(show);
        }
        menu.IsOpen = true;
    }

    // ——— القائمة الجانبية العائمة ———

    private readonly Dictionary<FrameworkElement, object?> navTips = new();
    private IEnumerable<ContentControl> SidebarItems() =>
        SidebarNav.Children.OfType<ContentControl>().Concat(new ContentControl[] { SidebarToggle, NavSettings });

    private void ToggleSidebar(object sender, RoutedEventArgs e)
    {
        UiPreferences.Current.SidebarCollapsed = !UiPreferences.Current.SidebarCollapsed;
        UiPreferences.Save();
        ApplySidebar(true);
    }

    /// المطويّة: أيقونات في المنتصف، والعناوين خطوط فاصلة، والتلميح يحمل اسم القسم واختصاره.
    private void ApplySidebar(bool animate)
    {
        var collapsed = UiPreferences.Current.SidebarCollapsed;
        var width = collapsed ? 76.0 : 236.0;
        if (animate && !App.Unattended)
            Sidebar.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        else { Sidebar.BeginAnimation(WidthProperty, null); Sidebar.Width = width; }
        SidebarBrand.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarDataBox.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarDataMini.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in SidebarItems())
        {
            if (!navTips.ContainsKey(item)) navTips[item] = item.ToolTip;
            var original = navTips[item];
            if (item.Content is not StackPanel panel || panel.Children.Count < 2) continue;
            var label = (panel.Children[1] as TextBlock)?.Text ?? "";
            for (var i = 1; i < panel.Children.Count; i++)
                if (!ReferenceEquals(panel.Children[i], BankBadge)) panel.Children[i].Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            item.HorizontalContentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            item.ToolTip = !collapsed ? original : ReferenceEquals(item, SidebarToggle) ? "توسيع القائمة (Ctrl+B)" :
                original is string tip && tip.StartsWith("Ctrl", StringComparison.Ordinal) ? $"{label} · {tip}" : label;
        }
        if (collapsed) BankBadge.Visibility = Visibility.Collapsed;
        else if (BankBadgeText.Text is { Length: > 0 } count && count != "0") BankBadge.Visibility = Visibility.Visible;
        foreach (var header in SidebarNav.Children.OfType<TextBlock>())
        {
            header.Tag ??= header.Text;
            if (collapsed)
            {
                header.Text = ""; header.Height = 1; header.Margin = new Thickness(12, 9, 12, 9);
                header.SetResourceReference(TextBlock.BackgroundProperty, "TbgE8EEED");
            }
            else
            {
                header.Text = (string)header.Tag; header.ClearValue(HeightProperty); header.ClearValue(MarginProperty); header.ClearValue(TextBlock.BackgroundProperty);
            }
        }
        SidebarToggleIcon.Text = collapsed ? "" : "";
    }
}
