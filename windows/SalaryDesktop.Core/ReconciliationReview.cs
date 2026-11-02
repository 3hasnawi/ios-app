namespace SalaryDesktop.Core;

/// تنبيه في مراجعة الفترة؛ Blocking يمنع اعتبار المطابقة مكتملة (مثل isComplete في BankReconciliationView.swift).
public sealed record ReconciliationAlert(string Text, bool Blocking = true);

/// نتيجة فحص مصادر الشهر للفترة (تُحسب في الخلفية): المصادر المتغيرة، المبالغ المقترحة غير المدرجة، وتنبيهات بيانات الشهر.
public sealed record ReconciliationSources(IReadOnlyList<ReconciliationAlert> Alerts, IReadOnlyList<BankMovement> Stale, int MissingSuggestions)
{
    public static readonly ReconciliationSources None = new([], [], 0);
}

/// مراجعة فترة المطابقة: الحركات غير المؤشرة، الرصيد الختامي، التمويل، المبالغ المسترجعة، ثم تنبيهات مصادر الشهر.
/// المطابقة «مكتملة» إذا لم يبقَ تنبيه مانع؛ والاعتماد ممكن يدوياً مع التنبيهات بعد تأشير كل الحركات.
public sealed record ReconciliationReview(ReconciliationTotals Totals, int Unmatched, ReconciliationSources Sources, IReadOnlyList<ReconciliationAlert> Alerts)
{
    public bool Complete => !Alerts.Any(a => a.Blocking);
    public IReadOnlyList<BankMovement> Stale => Sources.Stale;

    public static ReconciliationSources CheckSources(BankPeriod period, ReconciliationSuggestionResult? sources, BankSummary? summary, string? sourceError = null)
    {
        var alerts = new List<ReconciliationAlert>();
        IReadOnlyList<BankMovement> stale = [];
        var missing = 0;
        if (sourceError is not null) alerts.Add(new("تعذّر فحص مصادر الشهر: " + sourceError));
        else if (sources is not null)
        {
            stale = ReconciliationSuggestions.Changed(period, sources);
            if (stale.Count > 0) alerts.Add(new($"تغيّر أو اختفى مصدر {stale.Count:N0} حركة: " + string.Join("، ", stale.Take(3).Select(m => m.Description)) + (stale.Count > 3 ? "…" : "") + "؛ احذفها وأعد إضافتها."));
            var used = period.Movements.Where(m => m.SourceId is not null).Select(m => m.SourceId!).ToHashSet(StringComparer.Ordinal);
            missing = sources.Items.Count(item => !used.Contains(item.Id));
            if (missing > 0) alerts.Add(new($"{missing:N0} مبالغ مقترحة لم تُدرج بعد."));
            alerts.AddRange(sources.Warnings.Select(w => new ReconciliationAlert(w)));
        }
        if (summary is not null)
        {
            if (summary.MissingNet > 0) alerts.Add(new($"توجد {summary.MissingNet:N0} رواتب بلا صافي في {summary.Month}."));
            if (summary.MissingContractNominal > 0 && period.AcceptedContractMissing != summary.MissingContractNominal)
                alerts.Add(new($"توجد {summary.MissingContractNominal:N0} عقود بلا راتب اسمي في {summary.Month}."));
            if (!summary.ExecutionMatched && (summary.ExecutionCount > 0 || summary.BankDeductions != 0))
                alerts.Add(new("استقطاعات المصارف لا تطابق ملف جهات التنفيذ أو بيانات المطابقة ناقصة."));
        }
        return new(alerts, stale, missing);
    }

    public static ReconciliationReview Prepare(BankPeriod period, ReconciliationSuggestionResult? sources, BankSummary? summary, BankAccountDetails? account, string? sourceError = null) =>
        Combine(period, CheckSources(period, sources, summary, sourceError), account);

    /// تنبيهات الفترة نفسها تُحسب من جديد في كل مرة، وتُضاف إليها نتيجة آخر فحص لمصادر الشهر.
    public static ReconciliationReview Combine(BankPeriod period, ReconciliationSources sources, BankAccountDetails? account)
    {
        var totals = BankReconciliation.Calculate(period);
        var alerts = new List<ReconciliationAlert>();
        var unmatched = period.Movements.Count(m => !m.Matched);
        if (period.Movements.Count == 0) alerts.Add(new("لا توجد حركات في الفترة بعد."));
        if (unmatched > 0) alerts.Add(new($"{unmatched:N0} حركة غير مؤشرة؛ أشّرها بعد مطابقتها مع كشف المصرف الورقي."));
        if (period.Movements.Count > 0 && totals.Balance != 0) alerts.Add(new($"الرصيد الختامي ليس صفراً ({totals.Balance:N0})؛ راجع الحركات أو الرصيد الافتتاحي."));
        var funding = period.Movements.Where(m => m.IsFunding).ToArray();
        if (funding.Length == 0) alerts.Add(new("لا توجد حركة تمويل؛ أدخل تمويل الشهر كحركة دائنة مؤشرة «تمويل»."));
        else if (funding.Any(m => m.Date != period.Start)) alerts.Add(new("تاريخ التمويل لا يطابق بداية الفترة."));
        if (totals.OutstandingRefunds.Count > 0) alerts.Add(new($"{totals.OutstandingRefunds.Count:N0} مبلغ مسترجع لم يُعد بالكامل."));
        alerts.AddRange(sources.Alerts);
        if (account is { IsIncomplete: true }) alerts.Add(new("بيانات الحساب في رأس الكشف ناقصة (الاسم أو الفرع أو الحساب المحلي أو IBAN).", false));
        return new(totals, unmatched, sources, alerts);
    }
}
