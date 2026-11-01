using System.Globalization;
using SkiaSharp;

namespace SalaryDesktop.Core;

/// كشف استقطاعات موظف لجهة واحدة خلال فترة (مثل EmployeeDeductions.swift في الآيفون).
/// الربط بالاسم الموحّد بلا مسافات؛ لا يغيّر البيانات المحفوظة.
public sealed record DeductionPerson(string Key, string Name, string Detail);

public enum DeductionPeriodKind { Amount, Status, None, Unavailable }

public sealed record DeductionPeriodRow(int Year, int Month, DeductionPeriodKind Kind, decimal? Amount = null, IReadOnlyList<string>? Statuses = null)
{
    public int SortKey => Year * 100 + Month;
    public string Label => EmployeeDeductions.Label(Year, Month);
    public decimal Counted => Kind is DeductionPeriodKind.Amount or DeductionPeriodKind.Status ? Amount ?? 0 : 0;
    public bool IsUnavailable => Kind == DeductionPeriodKind.Unavailable;
    /// النص المعروض للمبلغ أو الحالة (كما في الآيفون).
    public string ValueText => Kind switch
    {
        DeductionPeriodKind.Amount => SalaryReportPdf.Money(Amount ?? 0),
        DeductionPeriodKind.Status => string.Join(" · ", new[] { Amount is { } a ? SalaryReportPdf.Money(a) : null }.OfType<string>().Concat(Statuses ?? [])),
        DeductionPeriodKind.None => "لا يوجد",
        _ => "غير متحقق",
    };
}

public sealed record EmployeeDeductionStatement(DeductionPerson Person, string Rank, string Workplace, string Entity, IReadOnlyList<DeductionPeriodRow> Rows, bool HasUnrecognizedMonths)
{
    public decimal Total => Rows.Sum(r => r.Counted);
    public bool Incomplete => HasUnrecognizedMonths || Rows.Any(r => r.IsUnavailable);
    public string FileName => ReportSheet.FileName($"كشف استقطاعات - {Person.Name} - {Entity}");
}

public sealed record DeductionAvailableMonths(IReadOnlyList<int> Keys, int UnrecognizedCount);

public static class EmployeeDeductions
{
    public static readonly string[] MonthTitles = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"];
    public static string Label(int year, int month) => $"{MonthTitles[month - 1]} {ReportSheet.Eastern(year.ToString(CultureInfo.InvariantCulture))}";
    public static string Label(int key) => Label(key / 100, key % 100);
    private static string Key(string name) => PersonProfiles.Key(name);

    /// كل من له راتب أو قيد استقطاع (آخر رتبة ومحل عمل من الرواتب).
    public static IReadOnlyList<DeductionPerson> People(BackupSnapshot source)
    {
        var found = new Dictionary<string, DeductionPerson>(StringComparer.Ordinal);
        foreach (var e in source.Employees.OrderByDescending(e => MonthNames.SortKey(e.Month)))
        {
            var key = Key(e.Name);
            if (key.Length > 0 && !found.ContainsKey(key))
                found[key] = new DeductionPerson(key, e.Name, string.Join(" · ", new[] { e.Rank, e.Workplace }.Where(v => !string.IsNullOrWhiteSpace(v))));
        }
        foreach (var d in source.Deductions)
        {
            var key = Key(d.Name);
            if (key.Length > 0 && !found.ContainsKey(key)) found[key] = new DeductionPerson(key, d.Name, d.Rank);
        }
        return found.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    }

    /// بحث بكل كلمات الاسم (حرفان على الأقل)، حتى ١٠٠ نتيجة.
    public static IReadOnlyList<DeductionPerson> Search(IReadOnlyList<DeductionPerson> people, string query)
    {
        var tokens = PayrollAnalysis.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (string.Concat(tokens).Length < 2) return [];
        return people.Where(p => tokens.All(t => p.Key.Contains(t, StringComparison.Ordinal))).Take(100).ToArray();
    }

    public static IReadOnlyList<string> Entities(BackupSnapshot source, string key) =>
        source.Deductions.Where(d => Key(d.Name) == key && !string.IsNullOrWhiteSpace(d.Title))
            .Select(d => d.Title).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// أشهر قيود الشخص والجهة فقط، بما فيها الحالات النصية.
    public static DeductionAvailableMonths AvailableMonths(BackupSnapshot source, string key, string entity)
    {
        var keys = new SortedSet<int>(); var unrecognized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in source.Deductions.Where(d => d.Title == entity && Key(d.Name) == key))
        {
            var month = MonthNames.SortKey(d.Month);
            if (month is >= 190001 and <= 220012 && month % 100 is >= 1 and <= 12) keys.Add(month); else unrecognized.Add(d.Month);
        }
        return new DeductionAvailableMonths(keys.ToArray(), unrecognized.Count);
    }

    private static string? Status(Deduction d)
    {
        const string prefix = "⟦حالة الاستقطاع:";
        if (!PayrollAnalysis.IsInactive(d)) return null;
        var end = d.Note.IndexOf('⟧', prefix.Length);
        var value = PayrollAnalysis.Normalize(d.Note[prefix.Length..end]);
        return value switch { "موقوف" => "موقوف", "هارب" => "هارب", "محكوم" => "محكوم", _ => value };
    }

    public static EmployeeDeductionStatement Statement(BackupSnapshot source, DeductionPerson person, string entity, int from, int to)
    {
        if (from % 100 is < 1 or > 12 || to % 100 is < 1 or > 12 || from > to) throw new InvalidDataException("يجب أن يكون شهر البداية قبل شهر النهاية أو مساوياً له.");
        int start = from / 100 * 12 + from % 100 - 1, end = to / 100 * 12 + to % 100 - 1;
        if (end - start >= 1200) throw new InvalidDataException("الفترة طويلة جداً؛ اختر مدة لا تتجاوز ١٠٠ سنة.");
        bool InRange(int k) => k >= from && k <= to;

        var employees = source.Employees.Where(e => Key(e.Name) == person.Key).ToArray();
        if (employees.GroupBy(e => e.Month).Any(g => g.Count() > 1 && InRange(MonthNames.SortKey(g.Key))))
            throw new InvalidDataException("يوجد أكثر من سجل راتب بالاسم نفسه في شهر واحد. لا يمكن التأكد من تطابق الاستقطاعات؛ راجع الأسماء قبل إصدار الكشف.");
        var latest = employees.Where(e => InRange(MonthNames.SortKey(e.Month))).OrderByDescending(e => MonthNames.SortKey(e.Month)).FirstOrDefault();

        var months = new Dictionary<int, PayrollMonth>();
        foreach (var m in source.Months)
        {
            var k = MonthNames.SortKey(m.Name);
            if (!InRange(k)) continue;
            if (!months.TryAdd(k, m)) throw new InvalidDataException("يوجد أكثر من ملف محفوظ للشهر نفسه بأسماء مختلفة. راجع الأشهر المستوردة قبل إصدار الكشف.");
        }
        var counts = source.Deductions.GroupBy(d => d.Month, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var statuses = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var unrecognized = false;
        foreach (var d in source.Deductions.Where(d => d.Title == entity && Key(d.Name) == person.Key))
        {
            var date = MonthNames.SortKey(d.Month);
            if (date == 0) { unrecognized = true; continue; }
            if (!InRange(date)) continue;
            if (Status(d) is { } status)
            {
                if (!statuses.TryGetValue(d.Month, out var set)) statuses[d.Month] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(status); continue;
            }
            if (d.Amount is not { } amount || amount < 0) throw new InvalidDataException("يوجد مبلغ استقطاع فارغ أو سالب أو غير صالح لهذه الجهة. راجع ملف الاستقطاعات قبل إصدار الكشف.");
            totals[d.Month] = totals.GetValueOrDefault(d.Month) + amount;
        }

        var rows = new List<DeductionPeriodRow>();
        for (int i = start; i <= end; i++)
        {
            int year = i / 12, month = i % 12 + 1;
            if (!months.TryGetValue(year * 100 + month, out var m) || m.DeductionCount <= 0 || counts.GetValueOrDefault(m.Name) != m.DeductionCount)
                rows.Add(new(year, month, DeductionPeriodKind.Unavailable));
            else if (statuses.TryGetValue(m.Name, out var names) && names.Count > 0)
                rows.Add(new(year, month, DeductionPeriodKind.Status, totals.TryGetValue(m.Name, out var t) ? t : null, names.ToArray()));
            else if (totals.TryGetValue(m.Name, out var total)) rows.Add(new(year, month, DeductionPeriodKind.Amount, total));
            else rows.Add(new(year, month, DeductionPeriodKind.None));
        }
        return new EmployeeDeductionStatement(person, latest?.Rank ?? "", latest?.Workplace ?? "", entity, rows, unrecognized);
    }
}

/// تنسيق الكشف المعتمد في الآيفون: حتى ثلاث سنوات في الصفحة، بحدود لكل خلية ومجموع لكل سنة ومجموع كلي.
public static class EmployeeDeductionPdf
{
    private static readonly SKColor Green = new(20, 66, 51), Light = new(230, 240, 232), Border = new(92, 117, 99), Gray = new(85, 85, 85), Orange = new(230, 126, 34);
    private const float PageW = 595, Margin = 30, Gap = 6;

    public static byte[] Generate(EmployeeDeductionStatement s, ReportAssets assets, byte[]? logo, DateTime stamp)
    {
        if (s.Rows.Count == 0) throw new InvalidDataException("لا توجد أشهر في الكشف.");
        var groups = s.Rows.GroupBy(r => r.Year).ToDictionary(g => g.Key, g => g.ToArray());
        var years = groups.Keys.Order().ToArray();
        var pages = years.Chunk(3).ToArray();
        using var canvas = new ReportCanvas(assets, stamp);
        using var image = logo is { Length: > 0 } ? SKBitmap.Decode(logo) : null;
        float width = PageW - Margin * 2, half = width / 2, colWidth = (width - 2 * Gap) / 3, valueWidth = colWidth * 0.47f;
        string Money(decimal v) => SalaryReportPdf.Money(v);
        // نفس فحص الآيفون: لا يُقتطع نص في الكشف؛ إن لم يتسع حتى أصغر خط يُرفض الإصدار.
        float Fit(string text, float room, params float[] sizes)
        {
            foreach (var size in sizes) { using var p = canvas.Paint(size, true); if (canvas.Width(p, text) <= room - 8) return size; }
            throw new InvalidDataException("بعض البيانات أطول من مساحة الكشف؛ تعذّر إنشاء PDF دون اقتطاعها.");
        }
        var first = s.Rows[0]; var last = s.Rows[^1];
        string[] details = [s.Person.Name, s.Rank.Length == 0 ? "—" : s.Rank, s.Entity, s.Workplace.Length == 0 ? "—" : s.Workplace, first.Label, last.Label];
        var detailSizes = details.Select(d => Fit(d, half * 0.68f, 10, 9, 8, 7)).ToArray();
        foreach (var r in s.Rows)
        {
            if (r.Kind == DeductionPeriodKind.Amount) Fit(Money(r.Amount ?? 0), valueWidth, 9, 8, 7);
            if (r.Kind == DeductionPeriodKind.Status)
            {
                if (r.Amount is { } a) Fit(Money(a), valueWidth, 8, 7, 6);
                Fit(string.Join("، ", r.Statuses ?? []), valueWidth, 7, 6, 5);
            }
        }
        foreach (var y in years) Fit(Money(groups[y].Sum(r => r.Counted)), valueWidth, 9, 8, 7);
        var totalSize = Fit(Money(s.Total), width * 0.45f, 13, 11, 9, 7);

        for (int p = 0; p < pages.Length; p++)
        {
            canvas.BeginPage(false);
            void Text(string value, SKRect rect, float size = 9, bool bold = false, SKColor? color = null, ReportCanvas.Align align = ReportCanvas.Align.Right, bool number = false)
            {
                if (string.IsNullOrEmpty(value)) return;
                using var paint = canvas.Paint(size, bold, color);
                canvas.Text(paint, value, rect.Left, rect.Right, rect.MidY + size * 0.36f, align, number);
            }
            void Cell(SKRect rect, SKColor? fill = null) { if (fill is { } f) canvas.Fill(rect, f); canvas.Stroke(rect, Border, 0.6f); }
            const ReportCanvas.Align C = ReportCanvas.Align.Center, L = ReportCanvas.Align.Left;

            if (image is not null)
            {
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                canvas.Canvas.DrawBitmap(image, SKRect.Create(Margin + 5, 26, 52, 52 * image.Height / Math.Max(1f, image.Width)), paint);
            }
            string[] header = ["جمهورية العراق", "وزارة الداخلية", "قيادة شرطة محافظة الأنبار", "قسم الحسابات"];
            for (int i = 0; i < header.Length; i++) Text(header[i], SKRect.Create(275, 25 + i * 13, width - 245, 13), 10, true);
            using (var line = new SKPaint { Color = Green, StrokeWidth = 1.5f, IsAntialias = true }) canvas.Canvas.DrawLine(Margin, 83, PageW - Margin, 83, line);
            Text("كشف استقطاعات موظف", SKRect.Create(Margin, 92, width, 25), 15, true, Green, C);
            Text("تفاصيل الاستقطاع لصالح الجهة خلال الفترة المختارة", SKRect.Create(Margin, 118, width, 16), 9, false, Gray, C);
            string[] labels = ["اسم الموظف", "الرتبة", "جهة الاستقطاع", "محل العمل", "من", "إلى"];
            for (int i = 0; i < 6; i++)
            {
                var x = i % 2 == 0 ? Margin + half : Margin;
                var rect = SKRect.Create(x, 142 + i / 2 * 25, half, 25);
                Cell(rect);
                Text(labels[i], SKRect.Create(rect.Right - half * 0.30f, rect.Top, half * 0.30f, rect.Height), 8, false, Gray);
                Text(details[i], SKRect.Create(rect.Left, rect.Top, half * 0.68f, rect.Height), detailSizes[i], true, null, L);
            }
            Text("الاستقطاعات الشهرية", SKRect.Create(Margin, 226, width, 22), 11, true, Green);
            const float top = 252, yearH = 26, headH = 23, rowH = 23, sumH = 27;
            for (int column = 0; column < pages[p].Length; column++)
            {
                var year = pages[p][column];
                var x = PageW - Margin - colWidth - column * (colWidth + Gap);
                var headRect = SKRect.Create(x, top, colWidth, yearH);
                Cell(headRect, Green); Text(ReportSheet.Eastern(year.ToString(CultureInfo.InvariantCulture)), headRect, 11, true, SKColors.White, C);
                var titleY = top + yearH; var monthWidth = colWidth - valueWidth;
                var vh = SKRect.Create(x, titleY, valueWidth, headH); var mh = SKRect.Create(x + valueWidth, titleY, monthWidth, headH);
                Cell(vh, Light); Cell(mh, Light);
                Text("المبلغ", vh, 8.5f, true, null, C); Text("الشهر", mh, 8.5f, true, null, C);
                var rows = groups[year];
                for (int month = 1; month <= 12; month++)
                {
                    var y = titleY + headH + (month - 1) * rowH;
                    var vr = SKRect.Create(x, y, valueWidth, rowH); var mr = SKRect.Create(x + valueWidth, y, monthWidth, rowH);
                    Cell(vr); Cell(mr);
                    if (rows.FirstOrDefault(r => r.Month == month) is not { } row) continue;
                    Text(EmployeeDeductions.MonthTitles[month - 1], mr, 8.5f);
                    switch (row.Kind)
                    {
                        case DeductionPeriodKind.Amount:
                            Text(Money(row.Amount ?? 0), vr, Fit(Money(row.Amount ?? 0), valueWidth, 9, 8, 7), align: C, number: true); break;
                        case DeductionPeriodKind.Status:
                            var status = string.Join("، ", row.Statuses ?? []); var size = Fit(status, valueWidth, 7, 6, 5);
                            if (row.Amount is { } a)
                            {
                                Text(Money(a), SKRect.Create(vr.Left, vr.Top, vr.Width, vr.Height / 2), Fit(Money(a), valueWidth, 8, 7, 6), align: C, number: true);
                                Text(status, SKRect.Create(vr.Left, vr.MidY, vr.Width, vr.Height / 2), size, align: C);
                            }
                            else Text(status, vr, size, align: C);
                            break;
                        case DeductionPeriodKind.None: Text("لا يوجد", vr, 8, align: C); break;
                        default: Text("غير متحقق", vr, 7.5f, false, Orange, C); break;
                    }
                }
                var sumY = titleY + headH + 12 * rowH;
                var left = SKRect.Create(x, sumY, valueWidth, sumH); var right = SKRect.Create(x + valueWidth, sumY, monthWidth, sumH);
                Cell(left, Light); Cell(right, Light);
                var incomplete = rows.Any(r => r.IsUnavailable);
                Text(incomplete ? "مجموع المتوفر" : rows.Length == 12 ? "مجموع السنة" : "مجموع الفترة", right, 8, true, null, C);
                var subtotal = rows.Sum(r => r.Counted);
                Text(Money(subtotal), left, Fit(Money(subtotal), valueWidth, 9, 8, 7), true, null, C, true);
            }
            if (p == pages.Length - 1)
            {
                var endY = top + yearH + headH + 12 * rowH + sumH;
                var totalRect = SKRect.Create(Margin, endY + 14, width, 30);
                canvas.Fill(totalRect, Green);
                Text(s.Incomplete ? "مجموع القيود المتوفرة" : "المجموع الكلي للفترة", SKRect.Create(totalRect.MidX, totalRect.Top, width / 2, 30), 10, true, SKColors.White);
                Text(Money(s.Total), SKRect.Create(Margin, totalRect.Top, width * 0.45f, 30), totalSize, true, SKColors.White, L, true);
                if (s.Incomplete) Text("توجد أشهر غير متحققة أو بأسماء غير مفهومة؛ المجموع للقيود المتوفرة فقط.", SKRect.Create(Margin, totalRect.Bottom + 4, width, 24), 8, false, Gray);
                Text("التدقيق: ................................", SKRect.Create(PageW - Margin - 200, totalRect.Bottom + 34, 200, 20), 9);
            }
            canvas.EndPage(p + 1, pages.Length);
        }
        return canvas.Finish();
    }
}
