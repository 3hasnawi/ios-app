using System.Globalization;
using System.Text.Json;
using SkiaSharp;

namespace SalaryDesktop.Core;

/// سطر في جدول المخصصات أو الاستقطاعات؛ العدد (مثل عدد العلاوات) يُكتب بلا فواصل.
public sealed record SalaryReportLine(string Label, decimal Value, bool IsCount = false);
public sealed record SalaryReportBank(string Bank, string Branch, decimal Amount);

/// تقرير راتب موظف لشهر واحد بشكل تقرير «برنامج الرواتب الالكتروني الموحد» (نقل من SalaryReport.swift في الآيفون).
public sealed record SalaryReportData(string Month, string Name, string Rank, string Agency, string Directorate, string Section,
    IReadOnlyList<SalaryReportLine> Entitlements, decimal? EntitlementTotal, IReadOnlyList<SalaryReportLine> Deductions, decimal? DeductionTotal,
    decimal? Net, IReadOnlyList<(string Label, string Value)> Info, IReadOnlyList<SalaryReportBank> Banks, string Notes, bool MissingDetails)
{
    /// مجموع المخصصات لا يساوي «الاستحقاق» في الملف، أو مجموع الاستقطاعات لا يساوي «مجموع الاستقطاعات».
    public IReadOnlyList<string> Mismatches
    {
        get
        {
            var result = new List<string>();
            if (EntitlementTotal is { } total)
            {
                var sum = Entitlements.Where(l => !l.IsCount).Sum(l => l.Value);
                if (Math.Abs(sum - total) > 0.5m) result.Add($"مجموع المخصصات ({ReportSheet.Money(sum)}) لا يساوي الاستحقاق الكلي ({ReportSheet.Money(total)})");
            }
            if (DeductionTotal is { } deductions)
            {
                var sum = Deductions.Sum(l => l.Value);
                if (Math.Abs(sum - deductions) > 0.5m) result.Add($"مجموع الاستقطاعات المفصّلة ({ReportSheet.Money(sum)}) لا يساوي مجموع الاستقطاعات ({ReportSheet.Money(deductions)})");
            }
            return result;
        }
    }
}

public static class SalaryReports
{
    public static string FileName(string name, string month) => ReportSheet.FileName($"تقرير راتب - {name} - {month}");

    /// يبني التقرير من راتب الشهر في ملف المنتسب واستقطاعات ملف التنفيذ له (المحتسبة فقط).
    public static SalaryReportData Make(ProfileMonth month, bool hideZeros, bool hidePersonal)
    {
        var employee = month.Employee ?? throw new InvalidDataException(month.Ambiguous ? "الاسم مكرر في رواتب هذا الشهر؛ لا يمكن إعداد تقرير الراتب." : "لا يوجد راتب لهذا الاسم في الشهر المختار.");
        var details = PersonProfiles.Details(employee);
        Dictionary<string, decimal> items;
        try { items = employee.Items is { Length: > 0 } raw ? JsonSerializer.Deserialize<Dictionary<string, decimal>>(raw) ?? new() : new(); }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة بنود راتب المنتسب.", ex); }
        var normalized = details.GroupBy(p => PayrollAnalysis.Normalize(p.Key)).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        string Text(string key) => (details.TryGetValue(key, out var exact) ? exact : normalized.GetValueOrDefault(PayrollAnalysis.Normalize(key)) ?? "").Trim();
        decimal Number(string key) => PersonProfiles.Amount(Text(key)) ?? (items.TryGetValue(key, out var value) ? value : 0);
        string Titled(string label, string nameKey) { var extra = Text(nameKey); return extra.Length == 0 ? label : $"{label} {extra}"; }

        // المخصصات بترتيب التقرير الرسمي.
        var temporaryName = Number("استقطاع مؤقت") == 0 ? Text("اسم استقطاع المؤقته") : "";
        var entitlements = new List<SalaryReportLine>
        {
            new("الراتب الاسمي", Number("الاسمي")),
            new("عدد العلاوات", Number("عدد العلاوات"), true),
            new("الخطورة", Number("الخطورة")),
            new("الارزاق", Number("الارزاق")),
            new("الاستثنائية", Number("الاستثنائية")),
            new(temporaryName.Length == 0 ? "مخصصات مؤقتة" : temporaryName, Number("مؤقتة")),
            new(Titled("الشهادة", Text("اسم الشهدة").Length == 0 ? "اسم الشهادة" : "اسم الشهدة"), Number("الشهادة")),
            new("الاطفال", Number("الاطفال")),
            new("الزوجية", Number("الزوجية")),
            new(Titled("المنصب", "اسم المنصب"), Number("منصب")),
            new(Titled("مخصصات الصنف", "اسم الصنف"), Number("مخصصات صنف")),
            new("الجامعية", Number("جامعية")),
            new("مخصصات النقل", Number("مخصصات نقل")),
            new(Titled("اللقب العلمي", "اسم اللقب العلمي"), Number("لقب علمي")),
            new(Text("اسم المخصصات الاخرى") is { Length: > 0 } other ? other : "مخصصات اخرى", Number("مخصصات اخرى")),
            new("اضافية", Number("اضافية")),
        };
        var deductions = new List<SalaryReportLine>
        {
            new("التوقيفات التقاعدية", Number("توقيفات تقاعدية")),
            new("الضريبة", Number("الضريبة")),
            new("الحماية الاجتماعية", Number("حماية الاجتماعية")),
            new("الضمان الصحي", Number("الرعاية الصحية")),
            new("النشاط الصحي", Number("نشاط صحي")),
            new("صندوق الشهداء", Number("صندوق الشهداء")),
            new("النشاط الرياضي", Number("نشاط رياضي")),
            new("التامين الصحي", Number("تامين صحي")),
            new("التضمين", Number("تضمين")),
            new("الايراد النهائي", Number("ايراد نهائي")),
            new("رسم الطابع", Number("رسم طابع")),
            new(Text("اسم الاستقطاع"), Number("مبلغ الاستقطاع")),
            new(Text("2اسم الاستقطاع"), Number("2مبلغ الاستقطاع")),
            new(Number("استقطاع مؤقت") == 0 ? "" : Text("اسم استقطاع المؤقته"), Number("استقطاع مؤقت")),
            new("استقطاع نسبة", Number("% استقطاع 3")),
            new("استقطاعات المصارف", Number("مجموع استقطاعات المصارف")),
        };
        var missing = details.Count == 0;
        if (missing)
        {
            // شهر مستورد قبل حفظ تفاصيل الراتب: الاستقطاعات المحفوظة فقط.
            entitlements.Clear();
            deductions = items.Where(p => !p.Key.Contains("مجموع", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new SalaryReportLine(p.Key, p.Value)).ToList();
        }
        if (hideZeros) { entitlements.RemoveAll(l => l.Value == 0); deductions.RemoveAll(l => l.Value == 0); }

        string Personal(string value) => hidePersonal && value.Length > 4 ? "•••• " + value[^4..] : value;
        var info = new List<(string, string)>
        {
            ("التحصيل الدراسي", Text("اسم الشهدة").Length == 0 ? Text("اسم الشهادة") : Text("اسم الشهدة")),
            ("اسم المنصب", Text("اسم المنصب")),
            ("اسم الصنف", Text("اسم الصنف")),
            ("اسم مصرف التوطين", Text("توطين المصرف")),
            ("IBAN", Personal(Text("IBAN"))),
            ("الرقم الموحد", Personal(Text("الرقم الموحد"))),
            ("الرقم الوظيفي", Personal(Text("الرقم الوظيفي"))),
            ("رقم الضمان الصحي", Personal(Text("رقم الضمان الصحي"))),
        };
        // استقطاعات المصارف من ملف الاستقطاعات: «المصرف / الفرع»، والحالات غير المحتسبة لا تدخل.
        var banks = month.Deductions.Where(d => !PayrollAnalysis.IsInactive(d)).Select(d =>
        {
            var parts = d.Title.Split('/', 2).Select(p => p.Trim()).ToArray();
            return new SalaryReportBank(parts[0], parts.Length > 1 ? parts[1] : "", d.Amount ?? 0);
        }).ToArray();
        var section = Text("NO_markaz");
        var deductionTotal = PersonProfiles.Amount(Text("مجموع الاستقطاعات")) ?? (items.TryGetValue("مجموع الاستقطاعات", out var stored) ? (decimal?)stored : null);
        return new SalaryReportData(month.Month, employee.Name, employee.Rank, Text("اسم الوكالة"), Text("اسم المديرية"), section.Length == 0 ? employee.Workplace : section,
            entitlements, PersonProfiles.Amount(Text("الاستحقاق")), deductions, deductionTotal, employee.Net, info, banks,
            string.Join(" — ", new[] { Text("ملاحظات عامه"), Text("ملاحظات") }.Where(n => n.Length > 0)), missing);
    }
}

/// صفحة A4 لكل شهر بنفس رسم تقرير الآيفون، مع العلامة المائية والتذييل المعتمدين.
public static class SalaryReportPdf
{
    private static readonly SKColor LineColor = new(140, 153, 173), Head = new(219, 230, 245), Soft = new(237, 242, 250);
    private const float PageW = 595, Margin = 30;

    private static string Digits(string text) => ReportSheet.Eastern(text);
    /// المبالغ بأرقام هندية وفاصل آلاف عربي حتى منزلتين عشريتين كما في الآيفون.
    public static string Money(decimal value) => Digits(value.ToString("#,0.##", CultureInfo.InvariantCulture)).Replace(",", "٬", StringComparison.Ordinal);

    public static byte[] Generate(IReadOnlyList<SalaryReportData> reports, ReportAssets assets, byte[]? logo, DateTime stamp)
    {
        if (reports.Count == 0) throw new InvalidDataException("لا يوجد تقرير راتب للتصدير.");
        using var canvas = new ReportCanvas(assets, stamp);
        using var image = logo is { Length: > 0 } ? SKBitmap.Decode(logo) : null;
        for (int i = 0; i < reports.Count; i++)
        {
            canvas.BeginPage(false);
            Draw(canvas, reports[i], image);
            canvas.EndPage(i + 1, reports.Count);
        }
        return canvas.Finish();
    }

    private static void Draw(ReportCanvas canvas, SalaryReportData r, SKBitmap? logo)
    {
        float width = PageW - Margin * 2, right = PageW - Margin, mid = PageW / 2;
        // نص داخل مستطيل وفي وسطه عمودياً، بلا هامش داخلي (مثل PDFText في الآيفون).
        void Text(string value, SKRect rect, float size, bool bold = false, ReportCanvas.Align align = ReportCanvas.Align.Right, SKColor? color = null, bool number = false)
        {
            if (string.IsNullOrEmpty(value)) return;
            using var paint = canvas.Paint(size, bold, color);
            canvas.Text(paint, value, rect.Left - 4, rect.Right + 4, rect.MidY + size * 0.36f, align, number);
        }
        void Box(SKRect rect, SKColor? fill = null, float stroke = 0.5f, SKColor? color = null)
        {
            if (fill is { } f) canvas.Fill(rect, f);
            canvas.Stroke(rect, color ?? LineColor, stroke);
        }
        const ReportCanvas.Align C = ReportCanvas.Align.Center;

        // الرأس: الجهة يميناً، الشعار في الوسط، الشهر والسنة يساراً.
        Text("جمهورية العراق", SKRect.Create(right - 170, 26, 170, 18), 12, align: C);
        Text("وزارة الداخلية", SKRect.Create(right - 170, 46, 170, 18), 12, align: C);
        Text(ReportSheet.DefaultHeader, SKRect.Create(right - 170, 66, 170, 18), 10.5f, true, C);
        if (logo is not null)
        {
            const float side = 86;
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.Canvas.DrawBitmap(logo, SKRect.Create(mid - side / 2, 18, side, side * logo.Height / Math.Max(1f, logo.Width)), paint);
        }
        var parts = r.Month.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var monthName = string.Join(' ', parts.SkipLast(1)); var year = parts.LastOrDefault() ?? "";
        Text($"الشهر : {(monthName.Length == 0 ? r.Month : monthName)}", SKRect.Create(Margin, 70, 150, 16), 10);
        Text($"السنة : {Digits(year)}", SKRect.Create(Margin, 88, 150, 16), 10);
        Text("برنامج الرواتب الالكتروني الموحد", SKRect.Create(Margin, 110, width, 18), 12.5f, align: C);
        Text("تقرير راتب", SKRect.Create(Margin, 128, width, 20), 14, true, C);

        // أسطر البيانات بخط منقّط.
        float y = 156;
        void DataLine(params (string Label, string Value)[] pairs)
        {
            var x = right;
            foreach (var (label, value) in pairs)
            {
                var labelWidth = label.Length * 6.2f + 8;
                Text(label, SKRect.Create(x - labelWidth, y, labelWidth, 18), 11);
                x -= labelWidth + 4;
                var valueWidth = Math.Min(Math.Max(value.Length * 6.6f + 12, 90), x - Margin);
                Text(value, SKRect.Create(x - valueWidth, y, valueWidth, 18), 11, true);
                x -= valueWidth + 14;
            }
            using var dash = new SKPaint { Color = new SKColor(140, 140, 140), StrokeWidth = 0.6f, IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash([2, 2], 0) };
            canvas.Canvas.DrawLine(Margin, y + 20, right, y + 20, dash);
            y += 26;
        }
        static string OrDash(string value) => value.Length == 0 ? "—" : value;
        DataLine(("ندرج ادناه تفاصيل راتب", OrDash(r.Rank)), ("/", r.Name));
        DataLine(("منسوب", OrDash(r.Agency)), ("مديرية", OrDash(r.Directorate)));
        DataLine(("قسم", OrDash(r.Section)));

        // جدولا المخصصات والاستقطاعات.
        y += 4;
        const float gap = 16, rowH = 15.5f;
        var half = (width - gap) / 2;
        var rows = Math.Max(Math.Max(r.Entitlements.Count, r.Deductions.Count), r.MissingDetails ? 4 : 1);
        void Table(float x, string title, string header, IReadOnlyList<SalaryReportLine> lines, string totalLabel, decimal? total, string? note)
        {
            Text(title, SKRect.Create(x, y, half, 16), 11.5f, true, C);
            var ty = y + 18; var labelW = half * 0.6f;
            void Cells(string label, string value, SKColor? fill, bool bold)
            {
                var lr = SKRect.Create(x + half - labelW, ty, labelW, rowH); var vr = SKRect.Create(x, ty, half - labelW, rowH);
                Box(lr, fill); Box(vr, fill);
                Text(label, SKRect.Create(lr.Left + 4, ty, lr.Width - 8, rowH), 9.5f, true);
                Text(value, SKRect.Create(vr.Left + 3, ty, vr.Width - 6, rowH), 9.5f, bold, C, number: true);
                ty += rowH;
            }
            Cells(header, "المبلغ", Head, true);
            for (int i = 0; i < rows; i++)
            {
                if (i < lines.Count) Cells(lines[i].Label, lines[i].IsCount ? Digits(((long)lines[i].Value).ToString(CultureInfo.InvariantCulture)) : Money(lines[i].Value), null, false);
                else Cells("", "", null, false);
            }
            Cells(totalLabel, total is { } t ? Money(t) : "—", Soft, true);
            if (note is not null) Text(note, SKRect.Create(x, y + 18 + rowH + 4, half, rowH * 3), 9, align: C, color: new SKColor(85, 85, 85));
        }
        Table(right - half, "الراتب المستحق", "اسم المخصصات", r.Entitlements, "الاستحقاق الكلي", r.EntitlementTotal,
            r.MissingDetails ? "أعد استيراد ملف رواتب هذا الشهر لإظهار المخصصات" : null);
        Table(Margin, "الاستقطاعات", "اسم الاستقطاع", r.Deductions, "مجموع الاستقطاعات", r.DeductionTotal, null);
        y += 18 + rowH * (rows + 2) + 10;

        // صافي الراتب.
        Box(SKRect.Create(Margin, y, width, 26), Soft, 1.2f, new SKColor(51, 51, 51));
        Text("صافي الراتب", SKRect.Create(mid, y, width / 2 - 20, 26), 13, true, C);
        Text(r.Net is { } net ? Money(net) : "—", SKRect.Create(Margin + 20, y, width / 2 - 20, 26), 13, true, C, number: true);
        y += 38;

        // بيانات الموظف (يميناً) واستقطاعات المصارف (يساراً).
        var infoX = right - half;
        Text("بيانات الموظف", SKRect.Create(infoX, y, half, 16), 11.5f, true, C);
        Text("استقطاعات المصارف", SKRect.Create(Margin, y, half, 16), 11.5f, true, C);
        var iy = y + 18;
        foreach (var (label, value) in r.Info)
        {
            var lr = SKRect.Create(infoX + half * 0.55f, iy, half * 0.45f, rowH + 1); var vr = SKRect.Create(infoX, iy, half * 0.55f, rowH + 1);
            Box(lr); Box(vr);
            Text(label, SKRect.Create(lr.Left + 4, iy, lr.Width - 8, rowH + 1), 9.5f, true);
            Text(label == "IBAN" ? value : Digits(value), SKRect.Create(vr.Left + 3, iy, vr.Width - 6, rowH + 1), 9, align: C, number: true);
            iy += rowH + 1;
        }
        var by = y + 18;
        float[] bw = [half * 0.42f, half * 0.33f, half * 0.25f];   // المصرف، الفرع، المبلغ (من اليمين)
        void BankRow(string[] values, SKColor? fill, bool bold)
        {
            var x = Margin + half;
            for (int i = 0; i < values.Length; i++)
            {
                x -= bw[i];
                var rect = SKRect.Create(x, by, bw[i], rowH + 1);
                Box(rect, fill);
                Text(values[i], SKRect.Create(x + 3, by, bw[i] - 6, rowH + 1), 9, bold || i == 0, i == 0 ? ReportCanvas.Align.Right : C, number: i == 2);
            }
            by += rowH + 1;
        }
        BankRow(["المصرف", "الفرع", "المبلغ"], Head, true);
        if (r.Banks.Count == 0) BankRow(["لا توجد", "", ""], null, false);
        else
        {
            foreach (var b in r.Banks) BankRow([b.Bank, b.Branch, Money(b.Amount)], null, false);
            if (r.Banks.Count > 1) BankRow(["المجموع", "", Money(r.Banks.Sum(b => b.Amount))], Soft, true);
        }

        // الملاحظات.
        var ny = Math.Max(iy, by) + 10;
        Box(SKRect.Create(Margin, ny, width, 34));
        Text("الملاحظات", SKRect.Create(right - 70, ny, 64, 34), 10, true);
        if (r.Notes.Length > 0) Text(r.Notes, SKRect.Create(Margin + 6, ny, width - 84, 34), 9.5f);
    }
}
