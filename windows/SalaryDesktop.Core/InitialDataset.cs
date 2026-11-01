using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SalaryDesktop.Core;

/// ينشئ مصدراً محلياً من بيانات راجعها المستخدم؛ لا ينشئ نسخة فارغة ولا يستبدل ملفاً.
public static class InitialDataset
{
    public static BackupSnapshot Create(PayrollImportPreview preview, string destination)
    {
        if (preview.Problems.Count > 0 || MonthNames.SortKey(preview.Month) == 0 || preview.Employees.Count + preview.Deductions.Count == 0 || preview.Employees.Count > 200_000 || preview.Deductions.Count > 500_000 ||
            preview.Employees.Any(e => e.Month != preview.Month || string.IsNullOrWhiteSpace(e.Name) || e.Net is null or < 0 or > 9_007_199_254_740_991m) ||
            preview.Deductions.Any(d => d.Month != preview.Month || string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Title) || d.Amount is < 0 or > 9_007_199_254_740_991m || d.Amount is null && !PayrollAnalysis.IsInactive(d)))
            throw new InvalidDataException("راجع سجلات الشهر والأخطاء قبل إنشاء البيانات.");
        var target = Path.GetFullPath(destination); if (!string.Equals(Path.GetExtension(target), ".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("وجهة البيانات غير صالحة.");
        var summary = new Dictionary<string, (int Count, decimal Total)>(StringComparer.Ordinal);
        try
        {
            foreach (var employee in preview.Employees)
            {
                if (employee.Items is null) continue;
                var items = JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.Items) ?? throw new JsonException();
                foreach (var (title, amount) in items)
                {
                    if (string.IsNullOrWhiteSpace(title) || amount is < -9_007_199_254_740_991m or > 9_007_199_254_740_991m) throw new InvalidDataException("يوجد بند راتب غير صالح.");
                    var old = summary.GetValueOrDefault(title); summary[title] = (checked(old.Count + 1), checked(old.Total + amount));
                }
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة بنود الرواتب.", ex); }
        var summaryData = JsonSerializer.SerializeToUtf8Bytes(summary.ToDictionary(p => p.Key, p => new { count = p.Value.Count, total = p.Value.Total }, StringComparer.Ordinal));
        var timestamp = (DateTime.UtcNow - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        var created = false;
        try
        {
            using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                created = true;
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    void Text(string name, string text) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal); using var stream = entry.Open(); using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(text); }
                    void Lines<T>(string name, IEnumerable<T> rows) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal); using var stream = entry.Open(); using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536); foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row)); }
                    Text("manifest.json", JsonSerializer.Serialize(new { version = 5, createdAt = timestamp, months = new[] { preview.Month }, employees = preview.Employees.Count, deductions = preview.Deductions.Count, staff = 0, accounts = 0, books = 0 }));
                    Lines("months.jsonl", new[] { new { name = preview.Month, importedAt = timestamp, employeeCount = preview.Employees.Count, deductionCount = preview.Deductions.Count, summary = summaryData } });
                    Lines("employees.jsonl", preview.Employees.Select(e => new { month = e.Month, name = e.Name, rank = e.Rank, workplace = e.Workplace, net = e.Net, items = e.Items, details = e.Details, rawColumns = e.RawColumns }));
                    Lines("deductions.jsonl", preview.Deductions.Select(d => new { month = d.Month, name = d.Name, title = d.Title, amount = d.Amount, note = d.Note, rank = d.Rank }));
                    Text("staff.jsonl", ""); Text("accounts.jsonl", ""); Text("books/index.json", "[]"); Text("settings.json", "{}");
                }
                file.Flush(true);
            }
            var snapshot = BackupReader.Read(target);
            if (snapshot.Months.Count != 1 || snapshot.Employees.Count != preview.Employees.Count || snapshot.Deductions.Count != preview.Deductions.Count) throw new InvalidDataException("تعذّر التحقق من البيانات الجديدة.");
            return snapshot;
        }
        catch { if (created) { try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { } } throw; }
    }
}
