using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Claunia.PropertyList;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace SalaryDesktop.Core;

public enum ReportColumnKind { Serial, Text, Amount }
public sealed record ReportColumn(string Title, double Width, ReportColumnKind Kind);

/// كشف بالشكل المعتمد في الآيفون: رأس (اسم الجهة) ← شريط أزرق بعنوان الكشف ← رؤوس الأعمدة ← الصفوف ← سطر المجموع.
/// قيم الصفوف نصوص، وعمود المبلغ رقم في Amounts.
public sealed record ReportSheet(string Header, string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<string[]> Rows, IReadOnlyList<decimal> Amounts, bool ShowTotal = true)
{
    public const string DefaultHeader = "قيادة شرطة محافظة الانبار";
    public decimal Total => Amounts.Sum();
    public int AmountColumn => Columns.ToList().FindIndex(c => c.Kind == ReportColumnKind.Amount);
    public bool HasTotal => ShowTotal && AmountColumn > 0;
    /// A4 بالعرض إذا كان مجموع عرض الأعمدة أكبر مما تتسعه الصفحة بالطول.
    public bool Landscape => Columns.Sum(c => c.Width) > 96;

    public static string Eastern(string text) => string.Concat(text.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    public static string Money(decimal value) => value.ToString("#,0.##", CultureInfo.GetCultureInfo("en-US"));

    /// اسم الجهة من إعدادات نسخة الآيفون (نفس مفتاح reportHeader)، وإلا الافتراضي.
    public static string HeaderFor(BackupSnapshot snapshot)
    {
        if (!snapshot.SettingsPlists.TryGetValue("reportHeader", out var data)) return DefaultHeader;
        try { return PropertyListParser.Parse(data) is NSString text && !string.IsNullOrWhiteSpace(text.Content) ? text.Content.Trim() : DefaultHeader; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return DefaultHeader; }
    }

    public void Validate()
    {
        if (Columns.Count == 0 || Columns.Count > 40 || Rows.Count > 200_000) throw new InvalidDataException("الكشف فارغ أو أكبر من الحد المدعوم.");
        if (Rows.Any(r => r.Length != Columns.Count)) throw new InvalidDataException("صفوف الكشف لا تطابق أعمدته.");
        if (AmountColumn >= 0 ? Amounts.Count != Rows.Count : Amounts.Count != 0) throw new InvalidDataException("مبالغ الكشف لا تطابق صفوفه.");
        if (Columns.Count(c => c.Kind == ReportColumnKind.Amount) > 1) throw new InvalidDataException("الكشف يدعم عمود مبلغ واحداً.");
    }

    /// عرض كل عمود بحسب أطول قيمة فيه، مع حد أدنى وأعلى (مثل الآيفون).
    public ReportSheet Fitted()
    {
        var maxAmount = Money(Math.Max(Total, Amounts.Count == 0 ? 0 : Amounts.Max())).Length;
        var columns = Columns.Select((column, c) =>
        {
            var longest = column.Kind switch
            {
                ReportColumnKind.Serial => Rows.Count.ToString(CultureInfo.InvariantCulture).Length,
                ReportColumnKind.Amount => maxAmount,
                _ => Rows.Count == 0 ? 0 : Rows.Max(r => r[c].Length)
            };
            var factor = column.Kind == ReportColumnKind.Text ? 1.08 : 1.3;
            var width = Math.Min(48, Math.Max(Math.Max(column.Title.Length + 2, longest * factor + 3), column.Kind == ReportColumnKind.Serial ? 5 : 8));
            return column with { Width = Math.Round(width) };
        }).ToArray();
        return this with { Columns = columns };
    }

    /// اسم ملف آمن من العنوان.
    public static string FileName(string title) => string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or ':' ? '-' : c)).Trim();
}

/// حفظ ذري: ملف مؤقت في المجلد نفسه ثم استبدال، فلا يبقى ملف ناقص عند الفشل.
public static class ReportFiles
{
    public static void Save(byte[] bytes, string destination, string extension)
    {
        var target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(target), extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"اختر ملفاً بالامتداد {extension}.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد الحفظ غير صالح.");
        var temporary = Path.Combine(directory, ".report-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            using (var file = File.OpenRead(temporary)) if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من الملف المحفوظ.");
            File.Move(temporary, target, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

/// ملفات Excel بالتنسيق المعتمد (اتجاه من اليمين، ألوان، حدود، دمج، معادلة المجموع).
public static class XlsxReport
{
    internal static string Letter(int index)
    {
        var n = index + 1; var s = "";
        while (n > 0) { var r = (n - 1) % 26; s = (char)('A' + r) + s; n = (n - 1) / 26; }
        return s;
    }
    internal static string Esc(string text) => new string(text.Where(c => c is '\n' or '\t' || c >= ' ').ToArray())
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    internal static string Text(string reference, string text, int style) =>
        $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>";
    internal static string Number(decimal value) => value.ToString(value == decimal.Truncate(value) ? "0" : "0.############", CultureInfo.InvariantCulture);

    public static byte[] Generate(ReportSheet sheet) => Package([("الكشف", Worksheet(sheet))], Styles, "$1:$3");
    public static byte[] Generate(IReadOnlyList<(string Name, ReportSheet Sheet)> sheets) => Package(sheets.Select(s => (s.Name, Worksheet(s.Sheet))).ToArray(), Styles, "$1:$3");

    internal static string Worksheet(ReportSheet sheet)
    {
        sheet.Validate();
        var last = Letter(sheet.Columns.Count - 1);
        var rows = new StringBuilder();
        rows.Append("<row r=\"1\" ht=\"24\" customHeight=\"1\">").Append(Text("A1", sheet.Header, 1)).Append("</row>");
        rows.Append("<row r=\"2\" ht=\"30\" customHeight=\"1\">").Append(Text("A2", sheet.Title, 2));
        for (int c = 1; c < sheet.Columns.Count; c++) rows.Append($"<c r=\"{Letter(c)}2\" s=\"2\"/>");
        rows.Append("</row><row r=\"3\" ht=\"22\" customHeight=\"1\">");
        for (int c = 0; c < sheet.Columns.Count; c++) rows.Append(Text($"{Letter(c)}3", sheet.Columns[c].Title, 3));
        rows.Append("</row>");
        for (int i = 0; i < sheet.Rows.Count; i++)
        {
            var r = i + 4; rows.Append($"<row r=\"{r}\" ht=\"20\" customHeight=\"1\">");
            for (int c = 0; c < sheet.Columns.Count; c++)
            {
                var reference = $"{Letter(c)}{r}";
                rows.Append(sheet.Columns[c].Kind switch
                {
                    ReportColumnKind.Serial => $"<c r=\"{reference}\" s=\"5\"><v>{i + 1}</v></c>",
                    ReportColumnKind.Amount => $"<c r=\"{reference}\" s=\"6\"><v>{Number(sheet.Amounts[i])}</v></c>",
                    _ => Text(reference, sheet.Rows[i][c], 4)
                });
            }
            rows.Append("</row>");
        }
        var totalRow = sheet.Rows.Count + 4; var amount = sheet.AmountColumn;
        var merges = new List<string> { $"A1:{last}1", $"A2:{last}2" };
        if (sheet.HasTotal)
        {
            rows.Append($"<row r=\"{totalRow}\" ht=\"22\" customHeight=\"1\">");
            for (int c = 0; c < sheet.Columns.Count; c++)
            {
                var reference = $"{Letter(c)}{totalRow}";
                if (c == 0) rows.Append(Text(reference, "المجموع", 7));
                else if (c == amount)
                {
                    var col = Letter(amount); var formula = sheet.Rows.Count == 0 ? "0" : $"SUM({col}4:{col}{totalRow - 1})";
                    rows.Append($"<c r=\"{reference}\" s=\"8\"><f>{formula}</f><v>{Number(sheet.Total)}</v></c>");
                }
                else rows.Append($"<c r=\"{reference}\" s=\"{(c < amount ? 7 : 9)}\"/>");
            }
            rows.Append("</row>");
            if (amount > 1) merges.Add($"A{totalRow}:{Letter(amount - 1)}{totalRow}");
        }
        var cols = string.Concat(sheet.Columns.Select((c, i) => $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{c.Width.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>" +
            "<sheetViews><sheetView rightToLeft=\"true\" workbookViewId=\"0\"><pane ySplit=\"3\" topLeftCell=\"A4\" activePane=\"bottomLeft\" state=\"frozen\"/><selection pane=\"bottomLeft\" activeCell=\"A4\" sqref=\"A4\"/></sheetView></sheetViews>" +
            "<sheetFormatPr defaultRowHeight=\"20\"/>" + $"<cols>{cols}</cols><sheetData>{rows}</sheetData>" +
            $"<mergeCells count=\"{merges.Count}\">{string.Concat(merges.Select(m => $"<mergeCell ref=\"{m}\"/>"))}</mergeCells>" +
            "<printOptions horizontalCentered=\"1\"/><pageMargins left=\"0.4\" right=\"0.4\" top=\"0.5\" bottom=\"0.5\" header=\"0.3\" footer=\"0.3\"/>" +
            $"<pageSetup paperSize=\"9\" orientation=\"{(sheet.Landscape ? "landscape" : "portrait")}\" fitToWidth=\"1\" fitToHeight=\"0\"/></worksheet>";
    }

    internal static byte[] Package(IReadOnlyList<(string Name, string Xml)> sheets, string styles, string? printTitles)
    {
        if (sheets.Count == 0) throw new InvalidDataException("لا توجد أوراق للتصدير.");
        var names = sheets.Select(s => SheetName(s.Name)).ToArray();
        for (int i = 0; i < names.Length; i++) for (int j = 0; j < i; j++) if (string.Equals(names[i], names[j], StringComparison.OrdinalIgnoreCase)) names[i] = SheetName(names[i][..Math.Min(names[i].Length, 27)] + " " + (i + 1));
        var overrides = string.Concat(sheets.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
        var types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" + overrides +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>";
        var defined = printTitles is null ? "" : "<definedNames>" + string.Concat(names.Select((n, i) => $"<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"{i}\">'{Esc(n.Replace("'", "''"))}'!{printTitles}</definedName>")) + "</definedNames>";
        var book = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><bookViews><workbookView/></bookViews><sheets>" +
            string.Concat(names.Select((n, i) => $"<sheet name=\"{Esc(n)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets>" + defined + "</workbook>";
        var rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
            $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
        const string rootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string path, string text) { var entry = zip.CreateEntry(path, CompressionLevel.Optimal); using var stream = entry.Open(); stream.Write(new UTF8Encoding(false).GetBytes(text)); }
            Add("[Content_Types].xml", types); Add("_rels/.rels", rootRels); Add("xl/workbook.xml", book); Add("xl/_rels/workbook.xml.rels", rels); Add("xl/styles.xml", styles);
            for (int i = 0; i < sheets.Count; i++) Add($"xl/worksheets/sheet{i + 1}.xml", sheets[i].Xml);
        }
        return output.ToArray();
    }

    /// اسم الورقة: حتى 31 حرفاً بلا الرموز الممنوعة في Excel.
    internal static string SheetName(string name)
    {
        var clean = new string(name.Where(c => c is not ('/' or '\\' or '?' or '*' or '[' or ']' or ':')).ToArray()).Trim().Trim('\'');
        if (clean.Length == 0) clean = "الكشف";
        return clean.Length > 31 ? clean[..31] : clean;
    }

    internal const string Styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"#,##0\"/></numFmts>" +
        "<fonts count=\"5\"><font><sz val=\"11\"/><name val=\"Arial\"/></font><font><b/><sz val=\"14\"/><name val=\"Arial\"/></font><font><b/><sz val=\"15\"/><color rgb=\"FFFFFFFF\"/><name val=\"Arial\"/></font><font><b/><sz val=\"11\"/><name val=\"Arial\"/></font><font><b/><sz val=\"12\"/><name val=\"Arial\"/></font></fonts>" +
        "<fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E79\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style=\"thin\"><color rgb=\"FF9BB7D4\"/></left><right style=\"thin\"><color rgb=\"FF9BB7D4\"/></right><top style=\"thin\"><color rgb=\"FF9BB7D4\"/></top><bottom style=\"thin\"><color rgb=\"FF9BB7D4\"/></bottom><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"10\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"4\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"4\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFill=\"1\" applyBorder=\"1\"/>" +
        "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
}

/// خطوط الكشوف وشعار العلامة المائية (من ويندوز ومجلد البرنامج).
public sealed record ReportAssets(byte[] Regular, byte[] Bold, byte[]? Watermark)
{
    public static ReportAssets Load(string? watermarkPath)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var regular = Path.Combine(fonts, "arial.ttf"); var bold = Path.Combine(fonts, "arialbd.ttf");
        if (!File.Exists(regular)) throw new InvalidDataException("خط Arial غير موجود في ويندوز؛ لا يمكن إنشاء الكشف بالخط المعتمد.");
        var regularBytes = File.ReadAllBytes(regular);
        return new(regularBytes, File.Exists(bold) ? File.ReadAllBytes(bold) : regularBytes,
            watermarkPath is not null && File.Exists(watermarkPath) ? File.ReadAllBytes(watermarkPath) : null);
    }
}

/// رسم الكشوف PDF بخط Arial، مع شعار الوزارة علامة مائية وتذييل في كل صفحة.
public sealed class ReportCanvas : IDisposable
{
    public const string Footer = "( قيادة شرطة محافظة الانبار - قسم الحسابات )";
    private readonly SKData regularData, boldData; private readonly SKTypeface regular, bold; private readonly SKShaper regularShaper, boldShaper;
    private readonly SKBitmap? watermark;
    private readonly MemoryStream output = new(); private readonly SKDocument document;
    private readonly DateTime stamp;
    public SKCanvas Canvas { get; private set; } = null!;
    public float PageWidth { get; private set; }
    public float PageHeight { get; private set; }

    public ReportCanvas(ReportAssets assets, DateTime stamp)
    {
        regularData = SKData.CreateCopy(assets.Regular); boldData = SKData.CreateCopy(assets.Bold);
        regular = SKTypeface.FromData(regularData) ?? throw new InvalidDataException("تعذّر فتح خط Arial.");
        bold = SKTypeface.FromData(boldData) ?? regular;
        regularShaper = new SKShaper(regular); boldShaper = new SKShaper(bold);
        if (assets.Watermark is { } logo) watermark = SKBitmap.Decode(logo);
        document = SKDocument.CreatePdf(output) ?? throw new InvalidDataException("تعذّر إنشاء ملف PDF.");
        this.stamp = stamp;
    }

    public SKPaint Paint(float size, bool isBold, SKColor? color = null) =>
        new() { Typeface = isBold ? bold : regular, TextSize = size, IsAntialias = true, Color = color ?? SKColors.Black };
    private SKShaper Shaper(SKPaint paint) => paint.Typeface == bold ? boldShaper : regularShaper;

    public void BeginPage(bool landscape)
    {
        PageWidth = landscape ? 842 : 595; PageHeight = landscape ? 595 : 842;
        Canvas = document.BeginPage(PageWidth, PageHeight); Canvas.Clear(SKColors.White);
        if (watermark is not null)
        {
            var side = Math.Min(PageWidth, PageHeight) * 0.62f; var aspect = (float)watermark.Height / Math.Max(1, watermark.Width);
            var rect = SKRect.Create(PageWidth / 2 - side / 2, PageHeight / 2 - side * aspect / 2, side, side * aspect);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha(26), FilterQuality = SKFilterQuality.High, IsAntialias = true };
            Canvas.DrawBitmap(watermark, rect, paint);
        }
    }

    /// التذييل: يميناً اسم القسم، وفي الوسط رقم الصفحة، ويساراً تاريخ ووقت التصدير (مثل كشوف الآيفون).
    public void EndPage(int page, int total)
    {
        float margin = 28, lineY = PageHeight - 26, baseline = PageHeight - 13;
        using var line = new SKPaint { Color = new SKColor(158, 158, 158), StrokeWidth = .5f, IsAntialias = true };
        Canvas.DrawLine(margin, lineY, PageWidth - margin, lineY, line);
        using var footerPaint = Paint(8.5f, true, new SKColor(64, 64, 64)); using var small = Paint(8.5f, false, new SKColor(64, 64, 64));
        var third = (PageWidth - margin * 2) / 3;
        Text(footerPaint, Footer, PageWidth - margin - third * 1.4f, PageWidth - margin, baseline, Align.Right);
        Text(small, $"صفحة {ReportSheet.Eastern(page.ToString(CultureInfo.InvariantCulture))} من {ReportSheet.Eastern(total.ToString(CultureInfo.InvariantCulture))}", PageWidth / 2 - third / 2, PageWidth / 2 + third / 2, baseline, Align.Center);
        var time = ReportSheet.Eastern(stamp.ToString("yyyy/MM/dd   h:mm", CultureInfo.InvariantCulture)) + (stamp.Hour < 12 ? " ص" : " م");
        Text(small, time, margin, margin + third, baseline, Align.Left);
        document.EndPage();
    }

    public enum Align { Right, Center, Left }

    public float Width(SKPaint paint, string text) => ReconciliationPdf.Width(Clean(text), Shaper(paint), paint);
    public List<string> Lines(SKPaint paint, string text, float width) => ReconciliationPdf.Wrap(Clean(text), Shaper(paint), paint, width);
    private static string Clean(string text) => new string(text.Where(c => c >= ' ' && c != '‏' && c != '‎').ToArray()).Trim();

    /// نص داخل مساحة؛ يصغر الخط قليلاً ثم يُختصر بـ "…" بدل أن يتجاوز الخلية (الأرقام لا تُختصر).
    public void Text(SKPaint paint, string text, float left, float right, float baseline, Align align, bool shrinkOnly = false)
    {
        text = Clean(text); if (text.Length == 0) return;
        var shaper = Shaper(paint); var size = paint.TextSize; var room = right - left - 8;
        while (ReconciliationPdf.Width(text, shaper, paint) > room && paint.TextSize > Math.Max(6.5f, size - 2.5f)) paint.TextSize -= .5f;
        if (!shrinkOnly && ReconciliationPdf.Width(text, shaper, paint) > room)
        {
            var runes = text.EnumerateRunes().ToList();
            while (runes.Count > 1 && ReconciliationPdf.Width(string.Concat(runes) + "…", shaper, paint) > room) runes.RemoveAt(runes.Count - 1);
            text = string.Concat(runes).TrimEnd() + "…";
        }
        var width = ReconciliationPdf.Width(text, shaper, paint);
        var edge = align switch { Align.Right => right - 4, Align.Left => left + 4 + width, _ => left + (right - left + width) / 2 };
        ReconciliationPdf.DrawRight(Canvas, shaper, paint, text, edge, baseline);
        paint.TextSize = size;
    }

    public void Fill(SKRect rect, SKColor color) { using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill }; Canvas.DrawRect(rect, paint); }
    public void Stroke(SKRect rect, SKColor color, float width) { using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true }; Canvas.DrawRect(rect, paint); }

    public byte[] Finish()
    {
        document.Close();
        var bytes = output.ToArray();
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new InvalidDataException("تعذّر إنشاء ملف PDF.");
        return bytes;
    }

    public void Dispose()
    {
        document.Dispose(); output.Dispose(); regularShaper.Dispose(); boldShaper.Dispose();
        if (!ReferenceEquals(bold, regular)) bold.Dispose(); regular.Dispose(); regularData.Dispose(); boldData.Dispose(); watermark?.Dispose();
    }
}

/// الكشف المعتمد PDF: A4 بالطول أو بالعرض بحسب عرض الأعمدة، يتكرر العنوان ورؤوس الأعمدة في كل صفحة، والمجموع في آخر صفحة.
public static class ReportPdf
{
    private static readonly SKColor Dark = new(31, 79, 120), Light = new(222, 235, 247), Line = new(155, 184, 212);

    public static byte[] Generate(IReadOnlyList<ReportSheet> sheets, ReportAssets assets, DateTime stamp)
    {
        if (sheets.Count == 0) throw new InvalidDataException("لا يوجد كشف للتصدير.");
        foreach (var sheet in sheets) sheet.Validate();
        using var canvas = new ReportCanvas(assets, stamp);
        var layouts = sheets.Select(s => (Sheet: s, PerPage: PerPage(s), Pages: 0)).ToArray();
        for (int i = 0; i < layouts.Length; i++) layouts[i].Pages = Math.Max(1, (int)Math.Ceiling((layouts[i].Sheet.Rows.Count + (layouts[i].Sheet.HasTotal ? 1 : 0)) / (double)layouts[i].PerPage));
        var total = layouts.Sum(l => l.Pages); var page = 0;
        foreach (var (sheet, perPage, pageCount) in layouts)
            for (int p = 0; p < pageCount; p++) { canvas.BeginPage(sheet.Landscape); Render(canvas, sheet, p, perPage, pageCount); canvas.EndPage(++page, total); }
        return canvas.Finish();
    }

    private const float Margin = 28, RowHeight = 20, TitleHeight = 30, ColumnsHeight = 22;
    private static int PerPage(ReportSheet sheet)
    {
        var height = sheet.Landscape ? 595f : 842f;
        var tableTop = Margin + 24 + TitleHeight + ColumnsHeight; var bottom = height - Margin - 18;
        return Math.Max(1, (int)((bottom - tableTop) / RowHeight));
    }

    /// التسلسل والمبالغ (ومنها المجموع) تظهر كاملة؛ والباقي يوزّع على أعمدة النص بنسبتها.
    private static float[] Widths(ReportCanvas canvas, ReportSheet sheet, float tableWidth)
    {
        var sum = sheet.Columns.Sum(c => c.Width);
        var widths = sheet.Columns.Select(c => (float)(c.Width / sum) * tableWidth).ToArray();
        var fixedColumns = new HashSet<int>();
        using var bold10 = canvas.Paint(10, true); using var bold11 = canvas.Paint(11, true); using var head = canvas.Paint(10.5f, true);
        for (int c = 0; c < sheet.Columns.Count; c++)
        {
            float need = sheet.Columns[c].Kind switch
            {
                ReportColumnKind.Serial => Math.Max(canvas.Width(bold10, Math.Max(sheet.Rows.Count, 1).ToString(CultureInfo.InvariantCulture)), canvas.Width(head, sheet.Columns[c].Title)) + 14,
                ReportColumnKind.Amount => Math.Max(Math.Max(canvas.Width(bold11, ReportSheet.Money(Math.Max(Math.Abs(sheet.Total), sheet.Amounts.Count == 0 ? 0 : sheet.Amounts.Max(a => Math.Abs(a))))), canvas.Width(bold11, ReportSheet.Money(sheet.Total))), canvas.Width(head, sheet.Columns[c].Title)) + 16,
                _ => 0
            };
            if (need > widths[c]) { widths[c] = need; fixedColumns.Add(c); }
        }
        var excess = widths.Sum() - tableWidth;
        var flexible = Enumerable.Range(0, widths.Length).Where(i => !fixedColumns.Contains(i) && sheet.Columns[i].Kind == ReportColumnKind.Text).ToArray();
        var flexibleTotal = flexible.Sum(i => widths[i]);
        if (excess > 0 && flexibleTotal > excess) foreach (var i in flexible) widths[i] -= excess * widths[i] / flexibleTotal;
        return widths;
    }

    private static void Render(ReportCanvas canvas, ReportSheet sheet, int page, int perPage, int pages)
    {
        var width = canvas.PageWidth; var tableWidth = width - Margin * 2; var headerTop = Margin;
        var widths = Widths(canvas, sheet, tableWidth);
        var xs = new float[widths.Length]; var x = width - Margin;
        for (int i = 0; i < widths.Length; i++) { x -= widths[i]; xs[i] = x; }
        SKRect Cell(int c, float y, float h) => SKRect.Create(xs[c], y, widths[c], h);

        using var header = canvas.Paint(14, true); using var titlePaint = canvas.Paint(14, true, SKColors.White);
        using var head = canvas.Paint(10.5f, true); using var text = canvas.Paint(10, false); using var serial = canvas.Paint(10, true);
        using var amount = canvas.Paint(10.5f, true); using var totalPaint = canvas.Paint(11, true);
        canvas.Text(header, sheet.Header, Margin, width - Margin, headerTop + 16, ReportCanvas.Align.Center);
        var titleRect = SKRect.Create(Margin, headerTop + 24, tableWidth, TitleHeight);
        canvas.Fill(titleRect, Dark);
        canvas.Text(titlePaint, sheet.Title, titleRect.Left, titleRect.Right, titleRect.Top + 20, ReportCanvas.Align.Center);
        var columnsY = titleRect.Bottom;
        for (int c = 0; c < sheet.Columns.Count; c++)
        {
            var r = Cell(c, columnsY, ColumnsHeight); canvas.Fill(r, Light); canvas.Stroke(r, Line, .5f);
            canvas.Text(head, sheet.Columns[c].Title, r.Left, r.Right, r.Top + 15, ReportCanvas.Align.Center);
        }
        var y = columnsY + ColumnsHeight; var start = page * perPage; var end = Math.Min(sheet.Rows.Count, start + perPage);
        for (int i = start; i < end; i++)
        {
            for (int c = 0; c < sheet.Columns.Count; c++)
            {
                var r = Cell(c, y, RowHeight); canvas.Stroke(r, Line, .5f);
                switch (sheet.Columns[c].Kind)
                {
                    case ReportColumnKind.Serial: canvas.Text(serial, (i + 1).ToString(CultureInfo.InvariantCulture), r.Left, r.Right, r.Top + 14, ReportCanvas.Align.Center, true); break;
                    case ReportColumnKind.Amount: canvas.Text(amount, ReportSheet.Money(sheet.Amounts[i]), r.Left, r.Right, r.Top + 14, ReportCanvas.Align.Center, true); break;
                    default: canvas.Text(text, sheet.Rows[i][c], r.Left, r.Right, r.Top + 14, ReportCanvas.Align.Right); break;
                }
            }
            y += RowHeight;
        }
        if (page == pages - 1 && sheet.HasTotal)
        {
            var amountColumn = sheet.AmountColumn;
            for (int c = 0; c < sheet.Columns.Count; c++) canvas.Fill(Cell(c, y, ColumnsHeight), Light);
            var label = Cell(0, y, ColumnsHeight); for (int c = 1; c < amountColumn; c++) label = SKRect.Union(label, Cell(c, y, ColumnsHeight));
            canvas.Stroke(label, Line, .5f);
            for (int c = amountColumn; c < sheet.Columns.Count; c++) canvas.Stroke(Cell(c, y, ColumnsHeight), Line, .5f);
            canvas.Text(totalPaint, "المجموع", label.Left, label.Right, y + 15, ReportCanvas.Align.Center);
            var cell = Cell(amountColumn, y, ColumnsHeight);
            canvas.Text(totalPaint, ReportSheet.Money(sheet.Total), cell.Left, cell.Right, y + 15, ReportCanvas.Align.Center, true);
        }
    }
}
