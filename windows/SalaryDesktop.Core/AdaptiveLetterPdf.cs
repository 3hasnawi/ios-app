using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using UglyToad.PdfPig;

namespace SalaryDesktop.Core;

/// خطوط الكتب كما في الآيفون: Times New Roman للترويسة والمتن والتواقيع والهامش، Arial للعنوان والجداول، وساكال العريض للعدد والتاريخ.
public sealed record LetterFonts(byte[] TimesRegular, byte[] TimesBold, byte[] ArialRegular, byte[] ArialBold, byte[] SakkalBold, byte[] Logo)
{
    /// خطوط ويندوز المثبتة (Times New Roman وArial بوزنيهما) مع خط ساكال المعتمد في النسخة. عند غياب أحدها يُستعمل الخط المعطى بديلاً
    /// حتى لا يتعذر إعداد الكتاب، لكن المطابقة البصرية للآيفون تتطلب خطوط ويندوز القياسية.
    public static LetterFonts Resolve(byte[] sakkalBold, byte[] tableFont, byte[] logo)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        byte[] Read(string file, byte[] fallback)
        {
            try { var path = Path.Combine(folder, file); return folder.Length > 0 && File.Exists(path) ? File.ReadAllBytes(path) : fallback; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return fallback; }
        }
        return new LetterFonts(Read("times.ttf", sakkalBold), Read("timesbd.ttf", sakkalBold), Read("arial.ttf", tableFont), Read("arialbd.ttf", tableFont), sakkalBold, logo);
    }
}

public sealed record LetterParagraph(string Text, string Font = "Times New Roman", int HalfPoints = 28, bool Bold = false, bool Centered = false);
public sealed record LetterTable(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows, string Font = "Arial", int HalfPoints = 22,
    int[]? ColumnWeights = null, bool ShowBorders = true, bool BoldLastRow = false, double WidthFraction = 1, bool AlignRight = false);
public sealed record LetterBlock(LetterParagraph? Paragraph = null, LetterTable? Table = null, bool PageBreak = false)
{
    public static LetterBlock Of(LetterParagraph paragraph) => new(paragraph);
    public static LetterBlock Of(LetterTable table) => new(null, table);
    public static LetterBlock Break { get; } = new(null, null, true);
}

/// نقل مباشر لمنشئ «مسودة PDF» في الآيفون (TransferAdaptivePDF): كتاب A4 تُقاس سطوره وخلاياه قبل الرسم، بنفس المواضع والخطوط والأحجام،
/// ويُرفض أي قص للمحتوى. الإحداثيات من أعلى الصفحة بالنقاط.
public static class AdaptiveLetterPdf
{
    private const float PageWidth = 595.3f, PageHeight = 841.9f, Margin = 38, Bottom = 803, ContentWidth = 519.3f;
    private const string Closing = "للتفضل بالاطلاع وإجراء اللازم وإعلامنا... مع التقدير.";

    private sealed class Face(byte[] bytes) : IDisposable
    {
        private readonly byte[] source = bytes;
        private readonly SKData data = SKData.CreateCopy(bytes);
        public SKTypeface Typeface { get; private set; } = null!;
        public SKShaper Shaper { get; private set; } = null!;
        /// مقاييس السطر من جدول hhea كما يقرؤها Core Text في الآيفون (ويندوز قد يعتمد مقاييس OS/2 الأكبر فتختلف المسافات).
        public (float Ascent, float Descent, float Gap)? Metrics { get; private set; }
        public Face Load()
        {
            Typeface = SKTypeface.FromData(data) ?? throw new InvalidDataException("تعذّر فتح خط الكتاب."); Shaper = new SKShaper(Typeface);
            Metrics = Hhea(source);
            return this;
        }
        private static (float, float, float)? Hhea(byte[] font)
        {
            int U16(int at) => at + 2 <= font.Length ? (font[at] << 8) | font[at + 1] : throw new IndexOutOfRangeException();
            int U32(int at) => (U16(at) << 16) | U16(at + 2);
            try
            {
                int head = -1, hhea = -1, tables = U16(4);
                for (int i = 0; i < tables && i < 512; i++)
                {
                    var record = 12 + 16 * i;
                    var tag = System.Text.Encoding.ASCII.GetString(font, record, 4);
                    if (tag == "head") head = U32(record + 8); else if (tag == "hhea") hhea = U32(record + 8);
                }
                if (head < 0 || hhea < 0) return null;
                var units = U16(head + 18);
                if (units is < 16 or > 16384) return null;
                float ascent = (short)U16(hhea + 4), descent = -(short)U16(hhea + 6), gap = (short)U16(hhea + 8);
                return ascent > 0 && descent >= 0 ? (ascent / units, descent / units, Math.Max(0, gap) / units) : null;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { return null; }
        }
        public void Dispose() { Shaper?.Dispose(); Typeface?.Dispose(); data.Dispose(); }
    }
    private sealed record TextLine(string Text, Face Face, float Size, bool Underline, bool Justify, float Height, float Ascent);
    private abstract record Draw;
    private sealed record LineDraw(TextLine Line, SKRect Rect, float Flush) : Draw;
    private sealed record BoxDraw(SKRect Rect, bool Shaded) : Draw;
    private sealed record LogoDraw(SKRect Rect) : Draw;

    private sealed class Fonts(LetterFonts source) : IDisposable
    {
        private readonly Dictionary<string, Face> faces = new(StringComparer.Ordinal);
        public Face For(string family, bool bold)
        {
            var key = family == "Sakkal Majalla" ? "sakkal" : family == "Arial" ? (bold ? "arial-bold" : "arial") : (bold ? "times-bold" : "times");
            if (faces.TryGetValue(key, out var face)) return face;
            var bytes = key switch { "sakkal" => source.SakkalBold, "arial-bold" => source.ArialBold, "arial" => source.ArialRegular, "times-bold" => source.TimesBold, _ => source.TimesRegular };
            if (bytes.Length is 0 or > 30_000_000) throw new InvalidDataException("خط الكتاب غير صالح.");
            face = new Face(bytes).Load(); faces[key] = face;
            return face;
        }
        public void Dispose() { foreach (var face in faces.Values) face.Dispose(); }
    }

    private static SKPaint Paint(Face face, float size) => new() { Typeface = face.Typeface, TextSize = size, IsAntialias = true, SubpixelText = true, Color = SKColors.Black };

    // ——— ترتيب السطر من اليمين لليسار (اتجاه الفقرة في الآيفون RTL دائماً) ———
    // الكلمات تُرتب من اليمين، والأرقام والكلمات اللاتينية تبقى من اليسار لليمين داخل الكلمة، والأقواس المحايدة تُعكس كما في نظام الاتجاه الثنائي.
    private static readonly Regex Strong = new(@"[A-Za-z0-9٠-٩۰-۹](?:[A-Za-z0-9٠-٩۰-۹.,/:٬٫-]*[A-Za-z0-9٠-٩۰-۹])?", RegexOptions.CultureInvariant);
    private static bool IsArabicLetter(char c) => c >= '\u0600' && char.IsLetter(c);
    private static string Reverse(string value) => string.Concat(value.EnumerateRunes().Reverse());
    private static string Mirror(string value) => string.Concat(value.Select(c => c switch { '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{', '<' => '>', '>' => '<', '«' => '»', '»' => '«', _ => c }));
    /// الأرقام الهندية من خط عربي يرسمها HarfBuzz من اليمين، فتُعكس قبل التشكيل لتظهر بترتيبها الصحيح.
    private static string LeftToRight(string run) => run.Any(c => c is >= '٠' and <= '٩' or >= '۰' and <= '۹') && !run.Any(char.IsAsciiLetter) ? Reverse(run) : run;
    private sealed record Piece(string Text, bool SpaceBefore);
    private static List<Piece> Pieces(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        static bool Latin(string token) { var m = Strong.Match(token); return m.Success && m.Length == token.Length && token.Any(char.IsAsciiLetter); }
        var result = new List<Piece>();
        for (int i = 0; i < tokens.Length;)
        {
            if (Latin(tokens[i]))
            {
                // كلمات لاتينية متتالية تبقى بترتيبها من اليسار.
                var j = i; while (j < tokens.Length && Latin(tokens[j])) j++;
                for (int k = j - 1; k >= i; k--) result.Add(new Piece(tokens[k], result.Count > 0));
                i = j; continue;
            }
            var first = true;
            var token = tokens[i]; var at = 0;
            void Part(string text, bool strong)
            {
                if (text.Length == 0) return;
                var shaped = strong ? LeftToRight(text) : text.Any(IsArabicLetter) ? text : Reverse(Mirror(text));
                result.Add(new Piece(shaped, first && result.Count > 0)); first = false;
            }
            foreach (Match match in Strong.Matches(token))
            {
                Part(token[at..match.Index], false);
                Part(match.Value, true);
                at = match.Index + match.Length;
            }
            Part(token[at..], false);
            i++;
        }
        return result;
    }
    private static float Space(Face face, SKPaint paint) => face.Shaper.Shape(" ", paint).Width;
    private static float Width(string line, Face face, SKPaint paint)
    {
        var space = Space(face, paint); float width = 0;
        foreach (var piece in Pieces(line)) width += face.Shaper.Shape(piece.Text, paint).Width + (piece.SpaceBefore ? space : 0);
        return width;
    }
    private static void DrawLine(SKCanvas canvas, Face face, SKPaint paint, string line, float right, float baseline, float extraGap = 0)
    {
        var space = Space(face, paint) + extraGap;
        foreach (var piece in Pieces(line))
        {
            if (piece.SpaceBefore) right -= space;
            var width = face.Shaper.Shape(piece.Text, paint).Width;
            canvas.DrawShapedText(face.Shaper, piece.Text, right - width, baseline, paint);
            right -= width;
        }
    }

    /// تقسيم السطور مثل CTTypesetter: عند المسافات، والكلمة الأطول من السطر تُقسم على الحروف، وفاصل السطر يُنهي السطر.
    private static List<TextLine> Lines(string text, Face face, float size, float width, bool underline = false, bool justify = false, float spacing = 3)
    {
        if (text.Length > 100_000 || width <= 8) throw new InvalidDataException("بيانات الكتاب غير مكتملة.");
        var result = new List<TextLine>();
        if (text.Length == 0) return result;
        using var paint = Paint(face, size);
        if (!paint.ContainsGlyphs(text.Replace("\n", "", StringComparison.Ordinal))) throw new InvalidDataException("خط الكتاب لا يدعم بعض الحروف؛ راجع النص.");
        var metrics = paint.FontMetrics;
        float ascent = -metrics.Ascent, descent = metrics.Descent, leading = metrics.Leading;
        if (face.Metrics is { } m) { ascent = m.Ascent * size; descent = m.Descent * size; leading = m.Gap * size; }
        var height = MathF.Ceiling(ascent + descent + leading) + spacing;
        var segments = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var count = text.EndsWith('\n') ? segments.Length - 1 : segments.Length;
        for (int s = 0; s < count; s++)
        {
            var segment = segments[s];
            var lines = new List<string>();
            var line = "";
            foreach (var word in segment.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var proposed = line.Length == 0 ? word : line + " " + word;
                if (Width(proposed, face, paint) <= width) { line = proposed; continue; }
                if (line.Length > 0) { lines.Add(line); line = ""; }
                var rest = word;
                while (Width(rest, face, paint) > width)
                {
                    var fit = 0;
                    for (int n = 1; n <= rest.Length && Width(rest[..n], face, paint) <= width; n++) fit = n;
                    if (fit == 0) throw new InvalidDataException("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات.");
                    lines.Add(rest[..fit]); rest = rest[fit..];
                }
                line = rest;
            }
            lines.Add(line);
            for (int i = 0; i < lines.Count; i++)
            {
                if (Width(lines[i], face, paint) > width + 0.5f) throw new InvalidDataException("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات.");
                var isLast = i == lines.Count - 1;
                result.Add(new TextLine(lines[i], face, size, underline, justify && !isLast, height, ascent));
                if (result.Count > 20_000) throw new InvalidDataException("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات.");
            }
        }
        return result;
    }

    public static byte[] Write(IReadOnlyList<LetterBlock> blocks, LetterFonts source, bool group, int signatureOffset, IReadOnlyList<string> required)
    {
        if (signatureOffset is < -60 or > 60 || blocks.Count < 7 || blocks[0].Paragraph is not { } header || blocks[1].Paragraph is not { } reference)
            throw new InvalidDataException("بيانات الكتاب غير مكتملة.");
        using var fonts = new Fonts(source);
        Face FaceOf(LetterParagraph p) => fonts.For(p.Font, p.Bold || p.Font == "Sakkal Majalla");
        float SizeOf(int halfPoints) => halfPoints is >= 12 and <= 96 ? halfPoints / 2f : throw new InvalidDataException("بيانات الكتاب غير مكتملة.");
        const float headingWidth = 190;
        var headerLines = Lines(header.Text, FaceOf(header), SizeOf(header.HalfPoints), headingWidth);
        // بخط ساكال يتسع العدد والتاريخ في سطرين كما في الآيفون؛ إن كان الخط البديل أعرض يُصغَّر قليلاً بدل رفض الكتاب.
        var refLines = Lines(reference.Text, FaceOf(reference), SizeOf(reference.HalfPoints), headingWidth);
        for (int size = reference.HalfPoints - 1; size >= 24 && refLines.Sum(l => l.Height) > 58; size--)
            refLines = Lines(reference.Text, FaceOf(reference), SizeOf(size), headingWidth);
        if (headerLines.Count == 0 || refLines.Count == 0 || headerLines.Sum(l => l.Height) > 123 || refLines.Sum(l => l.Height) > 58)
            throw new InvalidDataException("الترويسة أو العدد والتاريخ لا تتسع في موضعها؛ اختصر النص.");

        var pages = new List<List<Draw>> { new() };
        float y = 23, limit = 590;
        var expectingFooter = false; float? signatureEnd = null;
        void Add(Draw draw) => pages[^1].Add(draw);
        void NextPage() { if (pages.Count >= 4000) throw new InvalidDataException("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات."); pages.Add(new()); y = Margin; }
        void AddLines(IEnumerable<TextLine> items, float x, float width, float flush)
        {
            foreach (var line in items)
            {
                if (y + line.Height > limit) NextPage();
                Add(new LineDraw(line, SKRect.Create(x, y, width, line.Height), flush));
                y += line.Height;
            }
        }

        // مواضع ثابتة من هيئة النماذج: الترويسة يساراً وشعار الجمهورية وسط الصفحة.
        Add(new LogoDraw(SKRect.Create(271.2f, 36.1f, 86.4f, 107.9f)));
        AddLines(headerLines, 28, headingWidth, 0.5f);
        y = MathF.Max(150, y + 8);
        AddLines(refLines, 28, headingWidth, 1);
        y = MathF.Max(220, y + 12);
        var afterBreak = false; var needsClosing = false;
        void AddClosing()
        {
            var closing = new LetterParagraph(Closing, "Times New Roman", 32, true, true);
            y += 15;
            AddLines(Lines(closing.Text, FaceOf(closing), SizeOf(closing.HalfPoints), ContentWidth), Margin, ContentWidth, 0.5f);
            needsClosing = false;
        }

        for (int blockIndex = 0; blockIndex < blocks.Count - 2; blockIndex++)
        {
            var block = blocks[blockIndex + 2];
            if (block.PageBreak)
            {
                if (expectingFooter) throw new InvalidDataException("بيانات الكتاب غير مكتملة.");
                NextPage(); limit = Bottom; afterBreak = true;
                continue;
            }
            if (block.Paragraph is { } p)
            {
                if (needsClosing && p.Text.StartsWith("المرفقات:", StringComparison.Ordinal)) AddClosing();
                if (blockIndex == 2)
                {
                    y += 18;
                    var salutation = new LetterParagraph("تحية طيبة ..", "Times New Roman", 28, true);
                    AddLines(Lines(salutation.Text, FaceOf(salutation), SizeOf(salutation.HalfPoints), ContentWidth), Margin, ContentWidth, 1);
                    y += 7; needsClosing = true;
                }
                var ls = Lines(p.Text, FaceOf(p), SizeOf(p.HalfPoints), ContentWidth, p.Centered && p.Text.StartsWith("م /", StringComparison.Ordinal), blockIndex == 2, expectingFooter ? 0 : 3);
                if (expectingFooter)
                {
                    var footerHeight = ls.Sum(l => l.Height); const float footerBottom = 832;
                    if (signatureEnd is not { } end || footerHeight > footerBottom - Margin) throw new InvalidDataException("الهامش السفلي لا يتسع؛ اختصر النص.");
                    y = footerBottom - footerHeight; limit = footerBottom;
                    if (y < end + 8) throw new InvalidDataException("موضع اللجنة يتداخل مع المتن أو الهامش السفلي؛ عدّل موضع اللجنة أو اختصر النص.");
                    expectingFooter = false;
                }
                AddLines(ls, Margin, ContentWidth, p.Centered ? 0.5f : 1);
                if (ls.Count > 0) y += 6;
                continue;
            }
            if (block.Table is not { } table) throw new InvalidDataException("بيانات الكتاب غير مكتملة.");
            if (needsClosing && table.ShowBorders && !afterBreak) { AddClosing(); y += 18; }
            var count = table.Headers.Count == 0 ? (table.Rows.FirstOrDefault()?.Length ?? 0) : table.Headers.Count;
            var weights = table.ColumnWeights ?? Enumerable.Repeat(1, count).ToArray();
            if (count is <= 0 or > 12 || weights.Length != count || weights.Any(w => w <= 0) || table.Rows.Any(r => r.Length != count) || table.Rows.Count >= 50_000 || table.WidthFraction is <= 0 or > 1)
                throw new InvalidDataException("بيانات الكتاب غير مكتملة.");
            var sum = weights.Sum(); var tableWidth = ContentWidth * (float)table.WidthFraction;
            var widths = weights.Select(w => tableWidth * w / sum).ToArray();
            if (widths.Any(w => w <= 10)) throw new InvalidDataException("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات.");
            var tableRight = table.AlignRight ? PageWidth - Margin : Margin + (ContentWidth + tableWidth) / 2;
            var groupTable = group && afterBreak; var signature = !table.ShowBorders;
            var chosen = table.HalfPoints;

            (List<TextLine>[] Cells, float Height) Layout(IReadOnlyList<string> values, bool bold, int halfPoints)
            {
                if (values.Count != count) throw new InvalidDataException("بيانات الكتاب غير مكتملة.");
                var face = fonts.For(table.Font, bold); var size = SizeOf(halfPoints);
                float spacing = groupTable ? 0.5f : (signature ? 1 : 3), padding = groupTable ? 2 : 6, tallest = 0;
                var cells = new List<TextLine>[count];
                for (int i = 0; i < count; i++)
                {
                    if (signature)
                    {
                        var parts = values[i].Split('\n');
                        if (parts.Length != 4) throw new SignatureFit();
                        var single = new List<TextLine>();
                        foreach (var part in parts)
                        {
                            List<TextLine> measured;
                            try { measured = Lines(part, face, size, widths[i] - 8, spacing: spacing); }
                            catch (InvalidDataException) { throw new SignatureFit(); }
                            if (measured.Count != 1) throw new SignatureFit();
                            single.AddRange(measured);
                        }
                        cells[i] = single;
                    }
                    else cells[i] = Lines(values[i], face, size, widths[i] - 8, spacing: spacing);
                    tallest = MathF.Max(tallest, cells[i].Sum(l => l.Height));
                }
                var height = MathF.Max(MathF.Ceiling(halfPoints / 2f * 1.3f) + padding, tallest + padding);
                if (height > Bottom - Margin) throw new InvalidDataException("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات.");
                return (cells, height);
            }
            void DrawRow(IReadOnlyList<string> values, bool bold)
            {
                var (cells, height) = Layout(values, bold, chosen);
                if (y + height > limit) throw new InvalidDataException("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات.");
                var right = tableRight;
                for (int i = 0; i < count; i++)
                {
                    right -= widths[i];
                    var rect = SKRect.Create(right, y, widths[i], height);
                    if (table.ShowBorders) Add(new BoxDraw(rect, bold));
                    var top = y + (height - cells[i].Sum(l => l.Height)) / 2;
                    foreach (var line in cells[i]) { Add(new LineDraw(line, SKRect.Create(right + 4, top, widths[i] - 8, line.Height), 0.5f)); top += line.Height; }
                }
                y += height;
            }

            if (signature)
            {
                if (table.Rows.Count != 1) throw new InvalidDataException("بيانات الكتاب غير مكتملة.");
                if (needsClosing) AddClosing();
                var found = false;
                for (int size = table.HalfPoints; size >= 24; size--)
                {
                    try { if (Layout(table.Rows[0], true, size).Height <= 125) { chosen = size; found = true; break; } }
                    catch (SignatureFit) { }
                }
                if (!found) throw new InvalidDataException("أحد أسماء اللجنة أو صفاتها لا يتسع في سطر التوقيع؛ راجع أسماء اللجنة.");
                var signatureStart = 610f + signatureOffset;
                var signatureHeight = Layout(table.Rows[0], true, chosen).Height;
                if (y + 8 > signatureStart || signatureStart + signatureHeight > Bottom) throw new InvalidDataException("موضع اللجنة يتداخل مع المتن أو الهامش السفلي؛ عدّل موضع اللجنة أو اختصر النص.");
                y = signatureStart; limit = Bottom;
                DrawRow(table.Rows[0], true);
                signatureEnd = y; expectingFooter = true;
                continue;
            }
            if (groupTable)
            {
                var found = false;
                for (int size = Math.Min(table.HalfPoints, 22); size >= 14; size--)
                {
                    float needed = 0;
                    if (table.Headers.Count > 0) needed += Layout(table.Headers, true, size).Height;
                    for (int i = 0; i < table.Rows.Count; i++) needed += Layout(table.Rows[i], table.BoldLastRow && i == table.Rows.Count - 1, size).Height;
                    if (y + needed <= limit) { chosen = size; found = true; break; }
                }
                if (!found) throw new InvalidDataException("جدول المجموعة لا يتسع في صفحة واحدة بوضوح؛ راجع أطوال الأسماء وعدد الجهات.");
            }
            if (table.Headers.Count > 0)
            {
                if (y + Layout(table.Headers, true, chosen).Height > limit) NextPage();
                DrawRow(table.Headers, true);
            }
            for (int i = 0; i < table.Rows.Count; i++)
            {
                var bold = table.BoldLastRow && i == table.Rows.Count - 1;
                if (y + Layout(table.Rows[i], bold, chosen).Height > limit)
                {
                    if (groupTable) throw new InvalidDataException("جدول المجموعة لا يتسع في صفحة واحدة بوضوح؛ راجع أطوال الأسماء وعدد الجهات.");
                    NextPage();
                    if (table.Headers.Count > 0) DrawRow(table.Headers, true);
                }
                DrawRow(table.Rows[i], bold);
            }
            y += 8;
        }
        if (expectingFooter) throw new InvalidDataException("بيانات الكتاب غير مكتملة.");

        using var logo = SKBitmap.Decode(source.Logo) ?? throw new InvalidDataException("تعذّر فتح شعار الكتاب.");
        using var memory = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(memory) ?? throw new InvalidDataException("تعذّر إنشاء الكتاب."))
        {
            using var stroke = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, IsAntialias = true };
            using var shade = new SKPaint { Color = new SKColor(240, 240, 240), Style = SKPaintStyle.Fill };
            using var underline = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 0.7f, IsAntialias = true };
            using var image = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            foreach (var operations in pages)
            {
                var canvas = pdf.BeginPage(PageWidth, PageHeight);
                canvas.Clear(SKColors.White);
                foreach (var operation in operations)
                {
                    switch (operation)
                    {
                        case LogoDraw logoDraw: canvas.DrawBitmap(logo, logoDraw.Rect, image); break;
                        case BoxDraw box: if (box.Shaded) canvas.DrawRect(box.Rect, shade); canvas.DrawRect(box.Rect, stroke); break;
                        case LineDraw draw:
                        {
                            var line = draw.Line; if (line.Text.Length == 0) break;
                            using var paint = Paint(line.Face, line.Size);
                            var baseline = draw.Rect.Top + line.Ascent;
                            var width = Width(line.Text, line.Face, paint);
                            var gaps = line.Justify ? Pieces(line.Text).Count(p => p.SpaceBefore) : 0;
                            if (gaps > 0)
                            {
                                // ضبط الأسطر كما في الآيفون: المسافة الزائدة توزع بين الكلمات.
                                DrawLine(canvas, line.Face, paint, line.Text, draw.Rect.Right, baseline, MathF.Max(0, draw.Rect.Width - width) / gaps);
                                break;
                            }
                            var x = draw.Rect.Left + (draw.Rect.Width - width) * draw.Flush;
                            DrawLine(canvas, line.Face, paint, line.Text, x + width, baseline);
                            if (line.Underline) canvas.DrawLine(x, baseline + 2, x + width, baseline + 2, underline);
                            break;
                        }
                    }
                }
                pdf.EndPage();
            }
            pdf.Close();
        }
        var bytes = memory.ToArray();
        Verify(bytes, pages.Count, required);
        return bytes;
    }

    private sealed class SignatureFit : Exception { }

    /// يقرأ نص الملف الناتج ويتأكد من وجود الحسابات والمبالغ المطلوبة قبل تسليم الكتاب (مثل الآيفون).
    private static string Key(string value) => new string(value.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c is >= '۰' and <= '۹' ? (char)('0' + c - '۰') : char.ToUpperInvariant(c))
        .Where(c => char.IsAsciiLetterOrDigit(c)).ToArray());
    private static void Verify(byte[] bytes, int pages, IReadOnlyList<string> required)
    {
        using var document = PdfDocument.Open(bytes);
        if (document.NumberOfPages != pages) throw new InvalidDataException("تعذّر التحقق من صفحات مسودة الكتاب.");
        var text = new System.Text.StringBuilder();
        for (int i = 1; i <= document.NumberOfPages; i++)
        {
            var page = document.GetPage(i);
            if (Math.Abs(page.Width - PageWidth) > 2 || Math.Abs(page.Height - PageHeight) > 2 || page.Letters.Count == 0) throw new InvalidDataException("تعذّر التحقق من صفحات مسودة الكتاب.");
            text.Append(Key(string.Concat(page.Letters.Select(l => l.Value))));
        }
        var all = text.ToString();
        foreach (var value in required)
        {
            var expected = Key(value);
            if (expected.Length == 0 || !all.Contains(expected, StringComparison.Ordinal)) throw new InvalidDataException("تعذّر التحقق من الحسابات أو المبالغ في الكتاب الناتج.");
        }
    }

    // ——— قيم مشتركة بين الكتب (TransferWordGroupDraft في الآيفون) ———

    private static readonly string[] MonthNamesList = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"];
    public static string Eastern(string value) => string.Concat(value.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    public static string Grouped(long amount) => amount.ToString("#,0", CultureInfo.InvariantCulture);
    public static string MonthName(int month) => MonthNamesList[month - 1];
    /// تاريخ الصادر: الشهر من التخصيص أو شهر البيانات، والسنة من شهر البيانات، واليوم فارغ للكتابة باليد.
    public static string OutgoingDate(string sourceMonth, int? outgoingMonth)
    {
        var key = MonthNames.SortKey(sourceMonth);
        if (key == 0 || outgoingMonth is < 1 or > 12) throw new InvalidDataException("شهر الكتاب غير صالح.");
        return Eastern($" / {outgoingMonth ?? key % 100} / {key / 100}");
    }
    public static string ReferenceText(string number, string date) => $"العدد: {Eastern(number)}\nالتاريخ:{new string('\u00A0', 14)}{date}";
    public static string[] SignatureCells(GroupLetterSettings settings, string date)
    {
        string Name(string value) => string.Join(' ', value.Replace("ـ", "", StringComparison.Ordinal).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return [$"معاون مدير تدقيق\n{Name(settings.First)}\nالمصروفات\n{date}", $"العميد\n{Name(settings.Second)}\nمدير الحسابات\n{date}", $"لواء الشرطة الحقوقي\n{Name(settings.Third)}\nقائد شرطة محافظة الأنبار\n{date}"];
    }
    /// الكتل الأولى في كل الكتب: الترويسة، العدد والتاريخ، الجهة، الموضوع، المتن.
    public static List<LetterBlock> Opening(GroupLetterSettings settings, string date, string body) =>
    [
        LetterBlock.Of(new LetterParagraph(settings.Header, "Times New Roman", 32, true)),
        LetterBlock.Of(new LetterParagraph(ReferenceText(settings.Number, date), "Sakkal Majalla", 36, true)),
        LetterBlock.Of(new LetterParagraph("إلى / مصرف الرافدين / فرع الأنبار", "Arial", 32, true, true)),
        LetterBlock.Of(new LetterParagraph("م / تحويل مبلغ", "Arial", 32, true, true)),
        LetterBlock.Of(new LetterParagraph(body, "Times New Roman", 28)),
    ];
    public static LetterBlock Signatures(GroupLetterSettings settings, string date) =>
        LetterBlock.Of(new LetterTable([], [SignatureCells(settings, date)], "Times New Roman", 32, [1, 1, 1], ShowBorders: false));
    public static LetterBlock Footer(GroupLetterSettings settings) => LetterBlock.Of(new LetterParagraph(settings.Footer, "Times New Roman", 24, true));
    public static LetterBlock Attachment(int count) => LetterBlock.Of(new LetterParagraph($"المرفقات:\nقائمة أسماء عدد ({Eastern(count.ToString(CultureInfo.InvariantCulture))})", "Times New Roman", 24, true));
}
