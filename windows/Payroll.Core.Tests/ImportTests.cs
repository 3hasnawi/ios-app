using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Payroll.Core;
using ClosedXML.Excel;
using UglyToad.PdfPig;

namespace Payroll.Core.Tests;

public sealed class ImportTests
{
    [Fact]
    public void LatestTwoUnderstandsIraqiMonthsAndRejectsAmbiguousNames()
    {
        var keep = MonthOrder.LatestTwo(["أيلول ٢٠٢٦", "تشرين الاول ٢٠٢٦", "تشرين الثاني ٢٠٢٦"]);
        Assert.Equal(2, keep.Count);
        Assert.Contains("تشرين الاول ٢٠٢٦", keep);
        Assert.Contains("تشرين الثاني ٢٠٢٦", keep);
        Assert.Throws<InvalidDataException>(() => MonthOrder.LatestTwo(["تشرين الاول 2026", "أكتوبر 2026"]));
        Assert.Throws<InvalidDataException>(() => MonthOrder.LatestTwo(["شهر غير معروف"]));
    }

    [Fact]
    public async Task ImportRetainsLastTwoPayrollMonthsButAllDeductionsAndOriginal()
    {
        var dir = Temp();
        try
        {
            var input = Fixture(dir);
            var bytes = await File.ReadAllBytesAsync(input);
            var result = await IosBackupImporter.ImportAsync(input, Path.Combine(dir, "data"));
            Assert.Equal(2, result.Employees);
            Assert.Equal(3, result.Deductions);
            Assert.Equal(1, result.ArchivedPayrollRows);
            var info = PayrollRepository.GetInfo(result.Directory);
            Assert.Equal(3, info.Months.Count);
            Assert.Equal(2, info.Employees);
            var rows = PayrollRepository.GetPage(result.Directory, true, "أيلول 2026", "وهمي", 0);
            Assert.Single(rows.Rows);
            Assert.Equal("1000", rows.Rows[0].Amount);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(input));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(result.Directory, "original.zip")));
            using var db = Open(result.Directory);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM deductions WHERE month='أيلول 2026'";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
            cmd.CommandText = "SELECT COUNT(*) FROM employees WHERE month='أيلول 2026'";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FailureLeavesPriorDataAndOriginalUntouched()
    {
        var dir = Temp();
        try
        {
            var good = Fixture(dir);
            var dataRoot = Path.Combine(dir, "data");
            var result = await IosBackupImporter.ImportAsync(good, dataRoot);
            var marker = Path.Combine(result.Directory, "marker.txt");
            await File.WriteAllTextAsync(marker, "unchanged");
            var bad = Fixture(dir, wrongCount: true, fileName: "bad.zip");
            await Assert.ThrowsAsync<InvalidDataException>(() => IosBackupImporter.ImportAsync(bad, dataRoot));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(marker));
            Assert.Single(Directory.GetDirectories(dataRoot));
            Assert.True(File.Exists(bad));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task RejectsDuplicateAndUnsafeArchivePaths()
    {
        var dir = Temp();
        try
        {
            foreach (var invalidName in new[] { "../escape", "EMPLOYEES.JSONL" })
            {
                var file = Fixture(dir, extra: invalidName, fileName: Guid.NewGuid() + ".zip");
                await Assert.ThrowsAsync<InvalidDataException>(() => IosBackupImporter.ImportAsync(file, Path.Combine(dir, "data")));
            }
            Assert.False(File.Exists(Path.Combine(dir, "escape")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LatestFormatCanBeReadAndCancellationLeavesNoDataset()
    {
        var dir = Temp();
        try
        {
            var source = Fixture(dir, version: 10);
            var root = Path.Combine(dir, "data");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IosBackupImporter.ImportAsync(source, root, cancellation: cancelled.Token));
            Assert.Empty(Directory.GetDirectories(root));
            var result = await IosBackupImporter.ImportAsync(source, root);
            Assert.Equal(10, result.FormatVersion);
            Assert.Equal(2, result.Employees);
            Assert.Equal(3, result.Deductions);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ComparisonUsesDecimalAndMakesMissingAmountsVisible()
    {
        var dir = Temp();
        try
        {
            var result = await IosBackupImporter.ImportAsync(Fixture(dir), Path.Combine(dir, "data"));
            using (var db = Open(result.Directory))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO deductions(month,name,title,amount,note,rank,original) VALUES ('تشرين الاول 2026','اسم','جهة وهمية',NULL,'','',X'00'), ('تشرين الاول 2026','اسم','جهة وهمية','0.1','','',X'00'), ('تشرين الاول 2026','اسم','جهة وهمية',NULL,'⟦حالة الاستقطاع:موقوف⟧','',X'00')";
                cmd.ExecuteNonQuery();
            }
            var rows = DeductionComparison.Compare(result.Directory, "أيلول 2026", "تشرين الاول 2026");
            var row = Assert.Single(rows);
            Assert.Equal(1000m, row.PreviousAmount);
            Assert.Equal(1000.1m, row.CurrentAmount);
            Assert.Equal(0.1m, row.Difference);
            Assert.Equal(1, row.CurrentMissing);
            Assert.Equal(3, row.CurrentCount);
            Assert.Throws<ArgumentException>(() => DeductionComparison.Compare(result.Directory, "أيلول 2026", "أيلول 2026"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ArchivedBookIsListedAndCopiedWithoutChangingBackup()
    {
        var dir = Temp();
        try
        {
            var id = Guid.NewGuid();
            var source = Fixture(dir, bookId: id);
            var original = await File.ReadAllBytesAsync(source);
            var result = await IosBackupImporter.ImportAsync(source, Path.Combine(dir, "data"));
            var book = Assert.Single(ArchivedBooks.List(result.Directory));
            Assert.Equal(id, book.Id);
            Assert.Equal("كتاب وهمي", book.Title);
            Assert.Equal("جهة وهمية", book.Entity);
            using var copy = new MemoryStream();
            await ArchivedBooks.CopyPdfAsync(result.Directory, id, copy);
            Assert.Equal("%PDF-1.7\nfictional", Encoding.UTF8.GetString(copy.ToArray()));
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(result.Directory, "original.zip")));
            await Assert.ThrowsAsync<InvalidDataException>(() => ArchivedBooks.CopyPdfAsync(result.Directory, Guid.NewGuid(), new MemoryStream()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SearchNormalizesArabicWithoutChangingStoredNames()
    {
        var dir = Temp();
        try
        {
            var result = await IosBackupImporter.ImportAsync(Fixture(dir), Path.Combine(dir, "data"));
            using (var db = Open(result.Directory))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "UPDATE deductions SET name='أَحمد فاطمة ١٢' WHERE month='أيلول 2026'";
                cmd.ExecuteNonQuery();
            }
            var rows = PayrollRepository.GetPage(result.Directory, true, "أيلول 2026", "احمد فاطمه 12", 0);
            Assert.Equal("أَحمد فاطمة ١٢", Assert.Single(rows.Rows).Name);
            Assert.Equal("ايمان 12", ArabicSearch.Normalize("إيـمان ١٢"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task EntityStatementOmitsNonAmountsAndExportsExactPortraitWorkbook()
    {
        var dir = Temp();
        try
        {
            var result = await IosBackupImporter.ImportAsync(Fixture(dir), Path.Combine(dir, "data"));
            using (var db = Open(result.Directory))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO deductions(month,name,title,amount,note,rank,original) VALUES ('أيلول 2026','زكريا','جهة وهمية','500','=بيانات','مفوض',X'00'), ('أيلول 2026','أحمد','جهة وهمية','20','إضبارة 123','شرطي',X'00'), ('أيلول 2026','ناقص','جهة وهمية',NULL,'','',X'00'), ('أيلول 2026','صفري','جهة وهمية','0','','',X'00'), ('أيلول 2026','موقوف','جهة وهمية',NULL,'⟦حالة الاستقطاع:موقوف⟧','',X'00'), ('أيلول 2026','جهة أخرى','جهة أخرى','7','','',X'00')";
                cmd.ExecuteNonQuery();
            }
            Assert.Contains("جهة وهمية", DeductionReport.Entities(result.Directory, "أيلول 2026"));
            var report = DeductionReport.Create(result.Directory, "أيلول 2026", "جهة وهمية");
            Assert.Equal(3, report.Rows.Count);
            Assert.Equal("أحمد", report.Rows[0].Name);
            Assert.Equal(1520m, report.Total);
            Assert.Equal(1, report.MissingAmounts);
            Assert.Equal(1, report.ZeroAmounts);
            Assert.Equal(1, report.StatusRecords);
            using var stream = new MemoryStream();
            DeductionExcelWriter.Write(report, "ترويسة وهمية", stream);
            stream.Position = 0;
            using var workbook = new XLWorkbook(stream);
            var sheet = workbook.Worksheet("الكشف");
            Assert.True(sheet.RightToLeft);
            Assert.Equal(XLPageOrientation.Portrait, sheet.PageSetup.PageOrientation);
            Assert.Equal(XLPaperSize.A4Paper, sheet.PageSetup.PaperSize);
            Assert.Equal("أحمد", sheet.Cell("C4").GetString());
            Assert.Equal("=بيانات", sheet.Cell("E5").GetString());
            Assert.False(sheet.Cell("E5").HasFormula);
            Assert.Equal(1520d, sheet.Cell("D7").GetDouble());
            var unsafeAmount = report with { Rows = [new DeductionReportRow("", "اسم", 0.1m, "")], Total = 0.1m };
            Assert.Throws<InvalidOperationException>(() => DeductionExcelWriter.Write(unsafeAmount, "ترويسة", new MemoryStream()));
            Assert.Throws<InvalidDataException>(() => DeductionExcelWriter.Write(report with { Total = 1 }, "ترويسة", new MemoryStream()));
            var tooMany = report with { Rows = Enumerable.Repeat(new DeductionReportRow("", "اسم", 1, ""), 30001).ToArray(), Total = 30001 };
            Assert.Throws<InvalidOperationException>(() => DeductionExcelWriter.Write(tooMany, "ترويسة", new MemoryStream()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EntityPdfHasPortraitPagesAndContainsAmountsWithoutClipping()
    {
        var fontDir = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Fonts) : "/usr/share/fonts/truetype/dejavu";
        var regular = Path.Combine(fontDir, OperatingSystem.IsWindows() ? "arial.ttf" : "DejaVuSans.ttf");
        var bold = Path.Combine(fontDir, OperatingSystem.IsWindows() ? "arialbd.ttf" : "DejaVuSans-Bold.ttf");
        Assert.True(File.Exists(regular) && File.Exists(bold), "Test fonts must be installed.");
        var rows = Enumerable.Range(1, 90).Select(i => new DeductionReportRow("مفوض", $"اسم موظف وهمي {i}", 1000 + i, $"إضبارة {i}/2026")).ToArray();
        var report = new DeductionReportData("أيلول 2026", "جهة وهمية", rows, 0, 0, 0, 0, rows.Sum(x => x.Amount));
        using var stream = new MemoryStream();
        DeductionPdfWriter.Write(report, "قيادة شرطة محافظة الانبار", regular, bold, stream);
        using var pdf = PdfDocument.Open(stream.ToArray());
        Assert.True(pdf.NumberOfPages > 1);
        foreach (var page in pdf.GetPages())
        {
            Assert.InRange(page.Width, 594, 596);
            Assert.InRange(page.Height, 841, 843);
            Assert.NotEmpty(page.Letters);
            foreach (var letter in page.Letters)
            {
                Assert.InRange(letter.GlyphRectangle.Left, 20, 575);
                Assert.InRange(letter.GlyphRectangle.Right, 20, 575);
                Assert.InRange(letter.GlyphRectangle.Bottom, 35, 820);
                Assert.InRange(letter.GlyphRectangle.Top, 35, 820);
            }
        }
        var text = string.Join(' ', pdf.GetPages().Select(p => p.Text));
        foreach (var row in rows)
            Assert.Contains(row.Amount.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture), text);
        Assert.Contains("94,095", text);
        Assert.Throws<InvalidDataException>(() => DeductionPdfWriter.Write(report with { Total = 1 }, "رأس", regular, bold, new MemoryStream()));
        var longRow = report with { Rows = [new DeductionReportRow("", "اسم", 1, new string('س', 5000))], Total = 1 };
        Assert.Throws<InvalidOperationException>(() => DeductionPdfWriter.Write(longRow, "رأس", regular, bold, new MemoryStream()));
        var missingGlyph = report with { Rows = [new DeductionReportRow("", "اسم", 1, "رقم 🧬")], Total = 1 };
        Assert.Throws<InvalidOperationException>(() => DeductionPdfWriter.Write(missingGlyph, "رأس", regular, bold, new MemoryStream()));
        var mixedRows = new[] { new DeductionReportRow("مفوض", "اسم وهمي", 1000, "رقم ١٢٣ / ٢٠٢٦"), new DeductionReportRow("شرطي", "اسم وهمي", 2000, "رقم 456 / 2026") };
        var mixedReport = report with { Month = "شهر", Rows = mixedRows, Total = 3000 };
        using var mixedStream = new MemoryStream();
        DeductionPdfWriter.Write(mixedReport, "رأس", regular, bold, mixedStream);
        using var mixedPdf = PdfDocument.Open(mixedStream.ToArray());
        var words = mixedPdf.GetPage(1).GetWords().ToArray();
        var arabicNumber = Assert.Single(words, w => w.Text == "١٢٣");
        var arabicYear = Assert.Single(words, w => w.Text == "٢٠٢٦");
        Assert.True(arabicNumber.BoundingBox.Left > arabicYear.BoundingBox.Left, "Arabic number must be to the right of the year in the RTL note.");
        var latinNumber = Assert.Single(words, w => w.Text == "456");
        var latinYear = Assert.Single(words, w => w.Text == "2026");
        Assert.True(latinNumber.BoundingBox.Left > latinYear.BoundingBox.Left, "Latin number must be to the right of the year in the RTL note.");
    }

    private static string Temp() { var dir = Path.Combine(Path.GetTempPath(), "payroll-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
    private static SqliteConnection Open(string dir) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "data.sqlite"), Pooling = false }.ToString()); db.Open(); return db; }
    private static string Fixture(string dir, bool wrongCount = false, string? extra = null, string fileName = "source.zip", int version = 5, Guid? bookId = null)
    {
        var months = new[] { "أيلول 2026", "تشرين الاول 2026", "تشرين الثاني 2026" };
        var path = Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string body) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(body); }
        static string Lines<T>(IEnumerable<T> rows) => string.Join('\n', rows.Select(x => JsonSerializer.Serialize(x))) + "\n";
        Add("manifest.json", JsonSerializer.Serialize(new { version, createdAt = 0, months, employees = wrongCount ? 4 : 3, deductions = 3, staff = 0, accounts = 0, books = bookId.HasValue ? 1 : 0 }));
        Add("settings.json", "{}");
        Add("months.jsonl", Lines(months.Select(name => new { name, importedAt = 0, employeeCount = 1, deductionCount = 1 })));
        Add("employees.jsonl", Lines(months.Select(month => new { month, name = "موظف وهمي", rank = "عقد", workplace = "قسم", net = 123456, items = (string?)null, details = (string?)null, rawColumns = (string?)null })));
        Add("deductions.jsonl", Lines(months.Select(month => new { month, name = "موظف وهمي", title = "جهة وهمية", amount = 1000, note = "", rank = "عقد" })));
        Add("staff.jsonl", "");
        Add("accounts.jsonl", "");
        Add("books/index.json", bookId.HasValue ? JsonSerializer.Serialize(new[] { new { id = bookId.Value.ToString().ToUpperInvariant(), title = "كتاب وهمي", month = months[0], entity = "جهة وهمية", pages = 1, createdAt = 0 } }) : "[]");
        if (bookId.HasValue) Add("books/" + bookId.Value.ToString().ToUpperInvariant() + ".pdf", "%PDF-1.7\nfictional");
        if (extra != null) Add(extra, "unsafe");
        return path;
    }
}
