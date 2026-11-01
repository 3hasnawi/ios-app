using System.Globalization;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace SalaryDesktop.Core;

/// الكشوف المعتمدة المنقولة من الآيفون بنفس الأعمدة والعناوين والألوان.
public static class StandardReports
{
    internal static string Key(string name) => PayrollAnalysis.Normalize(name).Replace(" ", "", StringComparison.Ordinal);

    internal static Dictionary<string, decimal> Items(Employee employee)
    {
        if (employee.Items is null || employee.Items.Length == 0) return new(StringComparer.Ordinal);
        try { return JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.Items) ?? new(StringComparer.Ordinal); }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة بنود أحد الرواتب؛ لم يُنشأ الكشف.", ex); }
    }

    /// قائمة الاستقطاعات لجهة: ت، الرتبة، الاسم الكامل، المبلغ، جهة التنفيذ، رقم وتاريخ الاضبارة، ثم المجموع.
    /// الحالات غير المحتسبة (موقوف/هارب/محكوم) لا تدخل، والرتبة من القيد أو من راتب الشهر.
    public static ReportSheet EntityDeductions(BackupSnapshot source, string month, string entity)
    {
        var rows = source.Deductions.Where(d => d.Month == month && d.Title == entity && !PayrollAnalysis.IsInactive(d)).ToArray();
        if (rows.Length == 0) throw new InvalidDataException("لا توجد قيود محتسبة لهذه الجهة في الشهر المختار.");
        var ranks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var employee in source.Employees.Where(e => e.Month == month)) ranks.TryAdd(Key(employee.Name), employee.Rank);
        return new ReportSheet(ReportSheet.HeaderFor(source), $"قائمة الاستقطاعات لـ شهر {ReportSheet.Eastern(month)} - {entity}",
            [new("ت", 5, ReportColumnKind.Serial), new("الرتبة", 11, ReportColumnKind.Text), new("الاسم الكامل", 34, ReportColumnKind.Text),
             new("المبلغ", 14, ReportColumnKind.Amount), new("جهة التنفيذ", 28, ReportColumnKind.Text), new("رقم وتاريخ الاضبارة", 32, ReportColumnKind.Text)],
            rows.Select((d, i) => new[] { (i + 1).ToString(CultureInfo.InvariantCulture), d.Rank.Length > 0 ? d.Rank : ranks.GetValueOrDefault(Key(d.Name), ""), d.Name, "", d.Title, d.Note }).ToArray(),
            rows.Select(d => d.Amount ?? 0m).ToArray()).Fitted();
    }

    /// قائمة بند من بنود الرواتب: ت، الرتبة، الاسم الكامل، المبلغ، محل العمل (بترتيب ملف الرواتب).
    public static ReportSheet ItemList(BackupSnapshot source, string month, string item)
    {
        var people = ItemPeople(source, month, item);
        if (people.Count == 0) throw new InvalidDataException("لا توجد مبالغ لهذا البند في الشهر المختار.");
        return new ReportSheet(ReportSheet.HeaderFor(source), $"قائمة {item} لـ شهر {ReportSheet.Eastern(month)}",
            [new("ت", 5, ReportColumnKind.Serial), new("الرتبة", 11, ReportColumnKind.Text), new("الاسم الكامل", 34, ReportColumnKind.Text),
             new("المبلغ", 14, ReportColumnKind.Amount), new("محل العمل", 36, ReportColumnKind.Text)],
            people.Select((p, i) => new[] { (i + 1).ToString(CultureInfo.InvariantCulture), p.Rank, p.Name, "", p.Workplace }).ToArray(),
            people.Select(p => p.Amount).ToArray()).Fitted();
    }

    internal sealed record Person(int Index, string Name, string Rank, string Workplace, decimal Amount, decimal? Net);
    internal static List<Person> ItemPeople(BackupSnapshot source, string month, string item)
    {
        var people = new List<Person>(); var index = 0;
        foreach (var employee in source.Employees.Where(e => e.Month == month))
        {
            if (Items(employee).TryGetValue(item, out var amount) && amount != 0) people.Add(new(index, employee.Name, employee.Rank, employee.Workplace, amount, employee.Net));
            index++;
        }
        return people;
    }
}

/// كشف صندوق الشهداء حسب الفئة (ضباط 5000، موظفين 4000، مراتب 4000، عقود 2000) بشكله المعتمد.
public static class FundReport
{
    public sealed record Category(string Name, decimal Rate);
    public sealed record Row(Category Category, int Count, decimal Total);
    public static readonly Category[] Categories = [new("ضباط", 5000), new("موظفين", 4000), new("مراتب", 4000), new("عقود", 2000)];
    public static bool Applies(string title) => PayrollAnalysis.Normalize(title).Contains(PayrollAnalysis.Normalize("شهداء"), StringComparison.Ordinal);
    private static string RankKey(string rank) => string.Join(' ', PayrollAnalysis.Normalize(rank).Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static readonly HashSet<string> Officers = new[] { "لواء", "فريق", "فريق اول", "عميد", "عقيد", "مقدم", "رائد", "نقيب", "ملازم اول", "م اول", "م. اول", "م . اول", "ملازم" }.Select(RankKey).ToHashSet(StringComparer.Ordinal);
    /// العقود، ثم الموظفون (موظف د…)، ثم الضباط، وكل ما عداها مراتب.
    public static int CategoryOf(string rank)
    {
        var key = RankKey(rank);
        if (key.Split(' ').Any(w => w is "عقد" or "عقود")) return 3;
        if (key.StartsWith(RankKey("موظف"), StringComparison.Ordinal)) return 1;
        return Officers.Contains(key) ? 0 : 2;
    }
    public static IReadOnlyList<Row> Summary(BackupSnapshot source, string month, string item)
    {
        var counts = new int[4]; var totals = new decimal[4];
        foreach (var person in StandardReports.ItemPeople(source, month, item)) { var i = CategoryOf(person.Rank); counts[i]++; totals[i] += person.Amount; }
        return Categories.Select((c, i) => new Row(c, counts[i], totals[i])).ToArray();
    }
    public static string Title(BackupSnapshot source, string month)
    {
        var words = month.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var year = words.Length > 1 && words[^1].All(char.IsDigit) ? words[^1] : null;
        var name = year is null ? month : string.Join(' ', words[..^1]);
        return $"{ReportSheet.HeaderFor(source)} / تبرعات صندوق شهداء الشرطة لشهر( {name} )" + (year is null ? "" : $" لسنة ({ReportSheet.Eastern(year)})");
    }
    public static string FileName(string month) => ReportSheet.FileName($"كشف صندوق الشهداء حسب الفئة - {month}");

    public static byte[] Excel(IReadOnlyList<Row> rows, string title)
    {
        string[] headers = ["ت", "الرتبة", "العدد", "دعم النشاط الاجتماعي", "المجموع", "الملاحظات"]; double[] widths = [5, 16, 14, 24, 22, 24];
        var xml = new StringBuilder($"<row r=\"1\" ht=\"28\" customHeight=\"1\">{XlsxReport.Text("A1", title, 1)}</row><row r=\"2\" ht=\"26\" customHeight=\"1\">");
        for (int c = 0; c < headers.Length; c++) xml.Append(XlsxReport.Text($"{XlsxReport.Letter(c)}2", headers[c], 2));
        xml.Append("</row>");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = i + 3;
            xml.Append($"<row r=\"{r}\" ht=\"26\" customHeight=\"1\"><c r=\"A{r}\" s=\"3\"><v>{i + 1}</v></c>{XlsxReport.Text($"B{r}", rows[i].Category.Name, 4)}" +
                $"<c r=\"C{r}\" s=\"5\"><v>{rows[i].Count}</v></c><c r=\"D{r}\" s=\"5\"><v>{XlsxReport.Number(rows[i].Category.Rate)}</v></c><c r=\"E{r}\" s=\"5\"><v>{XlsxReport.Number(rows[i].Total)}</v></c><c r=\"F{r}\" s=\"6\"/></row>");
        }
        var last = rows.Count + 2; var tr = last + 1;
        xml.Append($"<row r=\"{tr}\" ht=\"28\" customHeight=\"1\">{XlsxReport.Text($"A{tr}", "المجموع", 7)}<c r=\"B{tr}\" s=\"7\"/><c r=\"C{tr}\" s=\"8\"><f>SUM(C3:C{last})</f><v>{rows.Sum(r => r.Count)}</v></c>" +
            $"<c r=\"D{tr}\" s=\"8\"/><c r=\"E{tr}\" s=\"9\"><f>SUM(E3:E{last})</f><v>{XlsxReport.Number(rows.Sum(r => r.Total))}</v></c><c r=\"F{tr}\" s=\"6\"/></row>");
        var sheet = SpecialXlsx.Sheet(xml.ToString(), widths, [$"A1:F1", $"A{tr}:B{tr}"], frozen: null, tab: null, autoFilter: null);
        return XlsxReport.Package([("الكشف", sheet)], Styles, null);
    }
    private const string Styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"#,##0\"/></numFmts>" +
        "<fonts count=\"3\"><font><sz val=\"12\"/><name val=\"Arial\"/></font><font><b/><sz val=\"15\"/><name val=\"Arial\"/></font><font><b/><sz val=\"14\"/><name val=\"Arial\"/></font></fonts>" +
        "<fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFBFBFBF\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF92D050\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        SpecialXlsx.BlackBorders + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"10\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

    /// A4 بالطول، أرقام هندية كما في النموذج.
    public static byte[] Pdf(IReadOnlyList<Row> rows, string title, ReportAssets assets, DateTime stamp)
    {
        using var canvas = new ReportCanvas(assets, stamp); canvas.BeginPage(false);
        const float margin = 36, h = 30; float width = 595 - margin * 2;
        float[] ratios = [5, 16, 14, 24, 22, 24]; var widths = ratios.Select(r => r / ratios.Sum() * width).ToArray();
        var xs = new float[6]; var x = 595 - margin; for (int i = 0; i < 6; i++) { x -= widths[i]; xs[i] = x; }
        SKColor gray = new(191, 191, 191), green = new(146, 209, 80);
        using var titlePaint = canvas.Paint(15, true); using var bold = canvas.Paint(13, true); using var head = canvas.Paint(12.5f, true); using var big = canvas.Paint(14, true);
        var lines = canvas.Lines(titlePaint, title, width - 8);
        for (int i = 0; i < Math.Min(2, lines.Count); i++) canvas.Text(titlePaint, lines[i], margin, 595 - margin, margin + 18 + i * 20, ReportCanvas.Align.Right);
        var y = margin + 58f;
        SKRect Cell(int c, SKColor? fill = null) { var r = SKRect.Create(xs[c], y, widths[c], h); if (fill is { } f) canvas.Fill(r, f); canvas.Stroke(r, SKColors.Black, .8f); return r; }
        void Draw(SKPaint paint, string text, SKRect r) => canvas.Text(paint, text, r.Left, r.Right, r.Top + h / 2 + paint.TextSize / 2.8f, ReportCanvas.Align.Center, true);
        string[] headers = ["ت", "الرتبة", "العدد", "دعم النشاط الاجتماعي", "المجموع", "الملاحظات"];
        for (int c = 0; c < 6; c++) Draw(head, headers[c], Cell(c, gray));
        for (int i = 0; i < rows.Count; i++)
        {
            y += h;
            Draw(bold, SpecialXlsx.EasternNumber(i + 1), Cell(0)); Draw(bold, rows[i].Category.Name, Cell(1)); Draw(bold, SpecialXlsx.EasternNumber(rows[i].Count), Cell(2));
            Draw(bold, SpecialXlsx.EasternNumber(rows[i].Category.Rate), Cell(3)); Draw(bold, SpecialXlsx.EasternNumber(rows[i].Total), Cell(4)); Cell(5);
        }
        y += h;
        var label = SKRect.Union(Cell(0, gray), Cell(1, gray)); canvas.Fill(label, gray); canvas.Stroke(label, SKColors.Black, .8f); Draw(bold, "المجموع", label);
        Draw(bold, SpecialXlsx.EasternNumber(rows.Sum(r => r.Count)), Cell(2, gray)); Cell(3, gray); Draw(big, SpecialXlsx.EasternNumber(rows.Sum(r => r.Total)), Cell(4, green)); Cell(5);
        canvas.EndPage(1, 1);
        return canvas.Finish();
    }
}

/// كشف عقد الإطعام من بند شركة عراق الجوال: المشتركون حسب مبلغ الاستقطاع، ثم صندوق الشهداء ٣٪ وضريبة ١٫٨٪ من الباقي وحصة الشركة.
public static class FoodContractReport
{
    public const decimal FundRate = 0.03m, TaxRate = 0.018m;
    public sealed record Row(decimal Amount, int Count) { public decimal Total => Amount * Count; }
    public sealed record Split(decimal Total)
    {
        public decimal Fund => Math.Round(Total * FundRate, MidpointRounding.AwayFromZero);
        public decimal AfterFund => Total - Fund;
        public decimal Tax => Math.Round(AfterFund * TaxRate, MidpointRounding.AwayFromZero);
        public decimal Company => AfterFund - Tax;
    }
    private static readonly string[] SplitLabels = ["مجموع الاستقطاع", "حصة صندوق شهداء ٣٪", "المبلغ بعد حسم حصة الصندوق", "ضريبة ١٫٨٪", "شركة عراق الجوال"];
    public static bool Applies(string title) => PayrollAnalysis.Normalize(title).Contains(PayrollAnalysis.Normalize("الجوال"), StringComparison.Ordinal);
    public static IReadOnlyList<Row> Rows(BackupSnapshot source, string month, string item) =>
        StandardReports.ItemPeople(source, month, item).GroupBy(p => p.Amount).Select(g => new Row(g.Key, g.Count())).OrderBy(r => r.Amount).ToArray();
    public static string Title(BackupSnapshot source, string month) => $"تفاصيل نسبة عدد المشتركين في عقد الاطعام ( {ReportSheet.HeaderFor(source)} ) لشهر {ReportSheet.Eastern(month)}";
    public static string FileName(string month) => ReportSheet.FileName($"كشف عقد الاطعام - {month}");

    public static byte[] Excel(IReadOnlyList<Row> rows, string title)
    {
        var count = rows.Sum(r => r.Count); var total = rows.Sum(r => r.Total); var split = new Split(total);
        var xml = new StringBuilder($"<row r=\"1\" ht=\"26\" customHeight=\"1\">{XlsxReport.Text("A1", title, 1)}</row><row r=\"2\" ht=\"24\" customHeight=\"1\">");
        string[] headers = ["ت", "عدد المشتركين", "مبلغ الاستقطاع", "المجموع"];
        for (int c = 0; c < 4; c++) xml.Append(XlsxReport.Text($"{XlsxReport.Letter(c)}2", headers[c], 2));
        xml.Append("</row>");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = i + 3;
            xml.Append($"<row r=\"{r}\" ht=\"22\" customHeight=\"1\"><c r=\"A{r}\" s=\"3\"><v>{i + 1}</v></c><c r=\"B{r}\" s=\"4\"><v>{rows[i].Count}</v></c><c r=\"C{r}\" s=\"4\"><v>{XlsxReport.Number(rows[i].Amount)}</v></c><c r=\"D{r}\" s=\"4\"><f>B{r}*C{r}</f><v>{XlsxReport.Number(rows[i].Total)}</v></c></row>");
        }
        var last = rows.Count + 2; var tr = last + 1;
        xml.Append($"<row r=\"{tr}\" ht=\"26\" customHeight=\"1\">{XlsxReport.Text($"A{tr}", "الإجمالي", 5)}<c r=\"B{tr}\" s=\"6\"><f>SUM(B3:B{last})</f><v>{count}</v></c><c r=\"C{tr}\" s=\"3\"/><c r=\"D{tr}\" s=\"6\"><f>SUM(D3:D{last})</f><v>{XlsxReport.Number(total)}</v></c></row>");
        var h = tr + 2;
        xml.Append($"<row r=\"{h}\" ht=\"24\" customHeight=\"1\">{XlsxReport.Text($"B{h}", "تفاصيل النسبة", 2)}<c r=\"C{h}\" s=\"2\"/></row>");
        xml.Append($"<row r=\"{h + 1}\" ht=\"22\" customHeight=\"1\">{XlsxReport.Text($"B{h + 1}", "البيان", 2)}{XlsxReport.Text($"C{h + 1}", "المبلغ", 2)}</row>");
        var first = h + 2;
        var formulas = new (string Formula, decimal Value)[] { ($"D{tr}", split.Total), ($"ROUND(C{first}*{FundRate.ToString(CultureInfo.InvariantCulture)},0)", split.Fund), ($"C{first}-C{first + 1}", split.AfterFund),
            ($"ROUND(C{first + 2}*{TaxRate.ToString(CultureInfo.InvariantCulture)},0)", split.Tax), ($"C{first + 2}-C{first + 3}", split.Company) };
        for (int i = 0; i < formulas.Length; i++)
        {
            var r = first + i;
            xml.Append($"<row r=\"{r}\" ht=\"22\" customHeight=\"1\">{XlsxReport.Text($"B{r}", SplitLabels[i], 7)}<c r=\"C{r}\" s=\"{(i == 4 ? 8 : 4)}\"><f>{formulas[i].Formula}</f><v>{XlsxReport.Number(formulas[i].Value)}</v></c></row>");
        }
        var sheet = SpecialXlsx.Sheet(xml.ToString(), [7, 28, 22, 22], ["A1:D1", $"B{h}:C{h}"], frozen: null, tab: null, autoFilter: null);
        return XlsxReport.Package([("الكشف", sheet)], Styles, null);
    }
    private const string Styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"#,##0\"/></numFmts>" +
        "<fonts count=\"3\"><font><sz val=\"12\"/><name val=\"Arial\"/></font><font><b/><sz val=\"13\"/><name val=\"Arial\"/></font><font><b/><sz val=\"12\"/><name val=\"Arial\"/></font></fonts>" +
        "<fills count=\"5\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9D9D9\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFF00\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        SpecialXlsx.BlackBorders + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"9\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

    public static byte[] Pdf(IReadOnlyList<Row> rows, string title, ReportAssets assets, DateTime stamp)
    {
        using var canvas = new ReportCanvas(assets, stamp);
        const float margin = 40, rowH = 24, pageH = 842; float width = 595 - margin * 2;
        float[] ratios = [13, 28, 26, 29]; var widths = ratios.Select(r => r / ratios.Sum() * width).ToArray();
        var xs = new float[4]; var x = 595 - margin; for (int i = 0; i < 4; i++) { x -= widths[i]; xs[i] = x; }
        SKColor gray = new(217, 217, 217), yellow = new(255, 255, 0), pale = new(242, 242, 242);
        using var bold = canvas.Paint(12, true); using var titlePaint = canvas.Paint(13, true); using var label = canvas.Paint(11.5f, true);
        var count = rows.Sum(r => r.Count); var total = rows.Sum(r => r.Total); var split = new Split(total);
        const float limit = pageH - margin, top = margin + 50 + rowH;
        // عدد الصفحات قبل الرسم بنفس خطوات التوزيع، حتى يكتب التذييل "صفحة س من ص" صحيحاً.
        int CountPages()
        {
            int pages = 1; float yy = top;
            for (int i = 0; i < rows.Count; i++) { if (yy + rowH > limit) { pages++; yy = top; } yy += rowH; }
            if (yy + rowH > limit) { pages++; yy = top; }
            yy += rowH * 2;
            return yy + rowH * 7 > limit ? pages + 1 : pages;
        }
        var totalPages = CountPages();
        var y = margin; var pageNumber = 0;
        void Draw(SKPaint paint, string text, SKRect r, ReportCanvas.Align align = ReportCanvas.Align.Center) => canvas.Text(paint, text, r.Left, r.Right, r.Top + rowH / 2 + paint.TextSize / 2.8f, align, align == ReportCanvas.Align.Center);
        SKRect Cell(int c, SKColor? fill = null) { var r = SKRect.Create(xs[c], y, widths[c], rowH); if (fill is { } f) canvas.Fill(r, f); canvas.Stroke(r, SKColors.Black, .7f); return r; }
        void NewPage()
        {
            if (pageNumber > 0) canvas.EndPage(pageNumber, totalPages);
            canvas.BeginPage(false); pageNumber++;
            var lines = canvas.Lines(titlePaint, title, width - 8);
            for (int i = 0; i < Math.Min(2, lines.Count); i++) canvas.Text(titlePaint, lines[i], margin, 595 - margin, margin + 16 + i * 18, ReportCanvas.Align.Center);
            y = margin + 50;
            string[] headers = ["ت", "عدد المشتركين", "مبلغ الاستقطاع", "المجموع"];
            for (int c = 0; c < 4; c++) Draw(bold, headers[c], Cell(c, gray));
            y += rowH;
        }
        NewPage();
        for (int i = 0; i < rows.Count; i++)
        {
            if (y + rowH > limit) NewPage();
            Draw(bold, SpecialXlsx.EasternNumber(i + 1), Cell(0)); Draw(bold, SpecialXlsx.EasternNumber(rows[i].Count), Cell(1));
            Draw(bold, SpecialXlsx.EasternNumber(rows[i].Amount), Cell(2)); Draw(bold, SpecialXlsx.EasternNumber(rows[i].Total), Cell(3)); y += rowH;
        }
        if (y + rowH > limit) NewPage();
        Draw(bold, "الإجمالي", Cell(0)); Draw(bold, SpecialXlsx.EasternNumber(count), Cell(1, yellow)); Cell(2); Draw(titlePaint, SpecialXlsx.EasternNumber(total), Cell(3, yellow));
        y += rowH * 2;
        if (y + rowH * 7 > limit) { canvas.EndPage(pageNumber, totalPages); canvas.BeginPage(false); pageNumber++; y = margin; }
        var labelWidth = widths[1] + widths[2];
        var headerRect = SKRect.Create(xs[2], y, labelWidth, rowH); canvas.Fill(headerRect, gray); canvas.Stroke(headerRect, SKColors.Black, .7f); Draw(bold, "تفاصيل النسبة", headerRect);
        y += rowH;
        SKRect LabelCell(SKColor fill) { var r = SKRect.Create(xs[1], y, widths[1], rowH); canvas.Fill(r, fill); canvas.Stroke(r, SKColors.Black, .7f); return r; }
        SKRect AmountCell(SKColor? fill) { var r = SKRect.Create(xs[2], y, widths[2], rowH); if (fill is { } f) canvas.Fill(r, f); canvas.Stroke(r, SKColors.Black, .7f); return r; }
        Draw(bold, "البيان", LabelCell(gray)); Draw(bold, "المبلغ", AmountCell(gray));
        decimal[] values = [split.Total, split.Fund, split.AfterFund, split.Tax, split.Company];
        for (int i = 0; i < values.Length; i++)
        {
            y += rowH;
            Draw(label, SplitLabels[i], LabelCell(pale), ReportCanvas.Align.Right); Draw(bold, SpecialXlsx.EasternNumber(values[i]), AmountCell(i == 4 ? yellow : null));
        }
        canvas.EndPage(pageNumber, totalPages);
        return canvas.Finish();
    }
}

/// اشتراكات الرعاية الصحية: المشتركون مع عدد أفراد الأسرة (المبلغ ÷ ٣٥٠٠)، وغير المشتركين ممن لهم راتب.
/// المبلغ الذي لا يقبل القسمة يُنبَّه عليه ولا يُقرّب؛ العقود ورجال الأمن لا يدخلون قائمة غير المشتركين.
public static class HealthCareReport
{
    public const decimal PerMember = 3500;
    public sealed record Row(int Index, string Name, string Rank, decimal Amount);
    public sealed record Lists(IReadOnlyList<Row> Subscribers, IReadOnlyList<Row> Others) { public IReadOnlyList<Row> Irregular => Subscribers.Where(r => !IsExact(r.Amount)).ToArray(); }
    public static bool Applies(string title) => PayrollAnalysis.Normalize(title).Contains(PayrollAnalysis.Normalize("الرعاية"), StringComparison.Ordinal);
    public static bool NotCovered(string rank) { var key = PayrollAnalysis.Normalize(rank); return new[] { "عقد", "عقود", "رجل امن", "رجل الامن", "رجال الامن" }.Any(w => key.Contains(PayrollAnalysis.Normalize(w), StringComparison.Ordinal)); }
    public static bool IsExact(decimal amount) => amount % PerMember == 0;
    public static int Family(decimal amount) => Math.Max(1, (int)Math.Round(amount / PerMember, MidpointRounding.AwayFromZero));
    public static string FamilyText(decimal amount) => IsExact(amount) ? Family(amount).ToString(CultureInfo.InvariantCulture) : (amount / PerMember).ToString("0.00", CultureInfo.InvariantCulture);

    public static Lists Build(BackupSnapshot source, string month, string item)
    {
        var subscribers = new List<Row>(); var others = new List<Row>(); var index = 0;
        foreach (var employee in source.Employees.Where(e => e.Month == month))
        {
            if (StandardReports.Items(employee).TryGetValue(item, out var amount) && amount != 0) subscribers.Add(new(index, employee.Name, employee.Rank, amount));
            else if ((employee.Net ?? 0) > 0 && !NotCovered(employee.Rank)) others.Add(new(index, employee.Name, employee.Rank, 0));
            index++;
        }
        IReadOnlyList<Row> Sort(List<Row> rows) => rows.OrderBy(r => RankOrder.Rank(r.Rank)).ThenBy(r => r.Index).ToArray();
        return new Lists(Sort(subscribers), Sort(others));
    }
    public static string Title(BackupSnapshot source, string month, bool others) =>
        others ? $"غير المشتركين في الرعاية الصحية ل{ReportSheet.HeaderFor(source)} لشهر {ReportSheet.Eastern(month)}" : $"اشتراكات الرعاية الصحية ل{ReportSheet.HeaderFor(source)} لشهر {ReportSheet.Eastern(month)}";
    public static string FileName(string month) => ReportSheet.FileName($"اشتراكات الرعاية الصحية - {month}");

    public static byte[] Excel(Lists lists, string subscribersTitle, string othersTitle) =>
        XlsxReport.Package([("المشتركين", Sheet(lists.Subscribers, subscribersTitle, false)), ("الغير مشتركين", Sheet(lists.Others, othersTitle, true))], Styles, null);
    private static string Sheet(IReadOnlyList<Row> rows, string title, bool others)
    {
        string[] headers = others ? ["ت", "الرتبة", "الاسم", "المبلغ"] : ["ت", "الرتبة", "الاسم", "المبلغ", "عدد افراد الاسرة"];
        var last = XlsxReport.Letter(headers.Length - 1);
        var xml = new StringBuilder($"<row r=\"1\" ht=\"34\" customHeight=\"1\">{XlsxReport.Text("A1", title, 1)}");
        for (int c = 1; c < headers.Length; c++) xml.Append($"<c r=\"{XlsxReport.Letter(c)}1\" s=\"1\"/>");
        xml.Append("</row><row r=\"2\" ht=\"22\" customHeight=\"1\">");
        for (int c = 0; c < headers.Length; c++) xml.Append(XlsxReport.Text($"{XlsxReport.Letter(c)}2", headers[c], 2));
        xml.Append("</row>");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = i + 3; var row = rows[i];
            xml.Append($"<row r=\"{r}\" ht=\"20\" customHeight=\"1\"><c r=\"A{r}\" s=\"3\"><v>{i + 1}</v></c>{XlsxReport.Text($"B{r}", row.Rank, 4)}{XlsxReport.Text($"C{r}", row.Name, 4)}<c r=\"D{r}\" s=\"3\"><v>{XlsxReport.Number(row.Amount)}</v></c>");
            if (!others) xml.Append(IsExact(row.Amount) ? $"<c r=\"E{r}\" s=\"3\"><v>{Family(row.Amount)}</v></c>" : $"<c r=\"E{r}\" s=\"5\"><v>{FamilyText(row.Amount)}</v></c>");
            xml.Append("</row>");
        }
        var end = Math.Max(2, rows.Count + 2);
        double[] widths = others ? [7, 13, 42, 14] : [7, 13, 42, 14, 17];
        return SpecialXlsx.Sheet(xml.ToString(), widths, [$"A1:{last}1"], frozen: 2, tab: others ? "FFFF0000" : "FF70AD47", autoFilter: $"A2:{last}{end}");
    }
    private const string Styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"3\"><font><sz val=\"12\"/><name val=\"Arial\"/></font><font><b/><sz val=\"16\"/><name val=\"Arial\"/></font><font><b/><sz val=\"12\"/><name val=\"Arial\"/></font></fonts>" +
        "<fills count=\"5\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFF00\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9D9D9\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFF7C80\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        SpecialXlsx.BlackBorders + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"6\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

    /// A4 بالطول: المشتركون ثم غير المشتركين من صفحة جديدة.
    public static byte[] Pdf(Lists lists, string subscribersTitle, string othersTitle, ReportAssets assets, DateTime stamp)
    {
        using var canvas = new ReportCanvas(assets, stamp);
        const float margin = 32, rowH = 20, pageH = 842; float width = 595 - margin * 2;
        SKColor yellow = new(255, 255, 0), gray = new(217, 217, 217), red = new(255, 125, 128);
        using var bold = canvas.Paint(10.5f, true); using var titlePaint = canvas.Paint(13, true);
        var perPage = (int)((pageH - margin * 2 - 34 - rowH - 16) / rowH);
        int Pages(int count) => Math.Max(1, (int)Math.Ceiling(count / (double)perPage));
        var total = Pages(lists.Subscribers.Count) + Pages(lists.Others.Count); var page = 0;
        foreach (var others in new[] { false, true })
        {
            var rows = others ? lists.Others : lists.Subscribers;
            string[] headers = others ? ["ت", "الرتبة", "الاسم", "المبلغ"] : ["ت", "الرتبة", "الاسم", "المبلغ", "عدد افراد الاسرة"];
            float[] ratios = others ? [7, 13, 42, 14] : [7, 13, 42, 14, 17];
            var widths = ratios.Select(r => r / ratios.Sum() * width).ToArray();
            var xs = new float[widths.Length]; var x = 595 - margin; for (int i = 0; i < widths.Length; i++) { x -= widths[i]; xs[i] = x; }
            for (int p = 0; p < Pages(rows.Count); p++)
            {
                canvas.BeginPage(false);
                var titleRect = SKRect.Create(margin, margin, width, 30); canvas.Fill(titleRect, yellow); canvas.Stroke(titleRect, SKColors.Black, .6f);
                canvas.Text(titlePaint, others ? othersTitle : subscribersTitle, titleRect.Left, titleRect.Right, titleRect.Top + 20, ReportCanvas.Align.Center);
                var y = titleRect.Bottom + 4;
                for (int c = 0; c < headers.Length; c++)
                {
                    var r = SKRect.Create(xs[c], y, widths[c], rowH); canvas.Fill(r, gray); canvas.Stroke(r, SKColors.Black, .6f);
                    canvas.Text(bold, headers[c], r.Left, r.Right, r.Top + 14, ReportCanvas.Align.Center);
                }
                y += rowH;
                for (int i = p * perPage; i < Math.Min(rows.Count, (p + 1) * perPage); i++)
                {
                    var row = rows[i];
                    var values = new List<string> { (i + 1).ToString(CultureInfo.InvariantCulture), row.Rank, row.Name, XlsxReport.Number(row.Amount) };
                    if (!others) values.Add(FamilyText(row.Amount));
                    for (int c = 0; c < values.Count; c++)
                    {
                        var r = SKRect.Create(xs[c], y, widths[c], rowH);
                        if (c == 4 && !IsExact(row.Amount)) canvas.Fill(r, red);
                        canvas.Stroke(r, SKColors.Black, .6f);
                        canvas.Text(bold, values[c], r.Left, r.Right, r.Top + 14, ReportCanvas.Align.Center, c is 0 or 3 or 4);
                    }
                    y += rowH;
                }
                canvas.EndPage(++page, total);
            }
        }
        return canvas.Finish();
    }
}

/// ترتيب الرتب من الأعلى إلى الأدنى، لترتيب الكشوف كما في النماذج.
public static class RankOrder
{
    private static string Key(string rank)
    {
        var latin = string.Concat(PayrollAnalysis.Normalize(rank).Select(c => c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c is >= '۰' and <= '۹' ? (char)('0' + c - '۰') : c));
        return string.Join(' ', latin.Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
    private static readonly string[] Order = new[] {
        "فريق اول", "فريق", "لواء", "عميد", "عقيد", "مقدم", "رائد", "نقيب", "ملازم اول", "م اول", "ملازم",
        "موظف د1", "موظف د2", "موظف د3", "موظف د4", "موظف د5", "موظف د6", "موظف د7", "موظف د8", "موظف د9", "موظف د10",
        "مفوض1", "مفوض2", "مفوض3", "مفوض4", "مفوض5", "مفوض6", "مفوض7", "مفوض8",
        "ر ع", "ع م", "ن ع", "ش1 م", "ش م", "شرطي", "عقود", "عقد" }.Select(Key).ToArray();
    private static readonly Dictionary<string, int> Index = Order.Select((k, i) => (k, i)).GroupBy(p => p.k).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
    public static int Rank(string rank) => Index.TryGetValue(Key(rank), out var i) ? i : Order.Length;
}

internal static class SpecialXlsx
{
    internal const string BlackBorders = "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style=\"thin\"><color rgb=\"FF000000\"/></left><right style=\"thin\"><color rgb=\"FF000000\"/></right><top style=\"thin\"><color rgb=\"FF000000\"/></top><bottom style=\"thin\"><color rgb=\"FF000000\"/></bottom><diagonal/></border></borders>";
    /// أرقام هندية بفاصل الآلاف العربي، كما في الكشوف الخاصة.
    internal static string EasternNumber(decimal value) => ReportSheet.Eastern(value.ToString("#,0.##", CultureInfo.GetCultureInfo("en-US"))).Replace(',', '٬').Replace('.', '٫');
    internal static string EasternNumber(int value) => EasternNumber((decimal)value);
    internal static string Sheet(string rows, double[] widths, string[] merges, int? frozen, string? tab, string? autoFilter)
    {
        var cols = string.Concat(widths.Select((w, i) => $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{w.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>"));
        var view = frozen is { } f ? $"<sheetView rightToLeft=\"true\" workbookViewId=\"0\"><pane ySplit=\"{f}\" topLeftCell=\"A{f + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/><selection pane=\"bottomLeft\" activeCell=\"A{f + 1}\" sqref=\"A{f + 1}\"/></sheetView>" : "<sheetView rightToLeft=\"true\" workbookViewId=\"0\"/>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            $"<sheetPr>{(tab is null ? "" : $"<tabColor rgb=\"{tab}\"/>")}<pageSetUpPr fitToPage=\"1\"/></sheetPr><sheetViews>{view}</sheetViews><sheetFormatPr defaultRowHeight=\"20\"/><cols>{cols}</cols><sheetData>{rows}</sheetData>" +
            (autoFilter is null ? "" : $"<autoFilter ref=\"{autoFilter}\"/>") +
            $"<mergeCells count=\"{merges.Length}\">{string.Concat(merges.Select(m => $"<mergeCell ref=\"{m}\"/>"))}</mergeCells>" +
            "<printOptions horizontalCentered=\"1\"/><pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/><pageSetup paperSize=\"9\" orientation=\"portrait\" fitToWidth=\"1\" fitToHeight=\"0\"/></worksheet>";
    }
}
