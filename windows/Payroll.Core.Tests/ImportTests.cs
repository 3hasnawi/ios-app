using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Payroll.Core;

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
