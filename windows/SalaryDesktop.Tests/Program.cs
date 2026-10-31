using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SalaryDesktop.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Entry(ZipArchive zip, string name, string text)
{
    var entry = zip.CreateEntry(name);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write(text);
}
static string Lines<T>(IEnumerable<T> values) => string.Concat(values.Select(v => JsonSerializer.Serialize(v) + "\n"));
static void MakeBackup(string path, bool wrongCount = false, bool duplicate = false, bool omitPdf = false)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    var id = Guid.Parse("a0a0a0a0-b0b0-c0c0-d0d0-e0e0e0e0e0e0");
    Entry(zip, "manifest.json", JsonSerializer.Serialize(new { version = 5, months = new[] { "تشرين الاول 2026", "أيلول 2026" }, employees = wrongCount ? 3 : 2, deductions = 3, staff = 0, accounts = 1, books = 1 }));
    Entry(zip, "months.jsonl", Lines(new[] { new { name = "أيلول 2026", employeeCount = 2, deductionCount = 3 }, new { name = "تشرين الاول 2026", employeeCount = 0, deductionCount = 0 } }));
    Entry(zip, "employees.jsonl", Lines(new[] {
        new { month = "أيلول 2026", name = "أحمد وهمي", rank = "رتبة وهمية", workplace = "قسم وهمي", net = (decimal?)1250000m,
              items = (byte[]?)JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["بند وهمي"] = 250m }),
              details = (byte[]?)JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["الدرجة"] = "تجريبية" }) },
        new { month = "أيلول 2026", name = "علي وهمي", rank = "رتبة وهمية", workplace = "قسم وهمي", net = (decimal?)null, items = (byte[]?)null, details = (byte[]?)null }
    }));
    Entry(zip, "deductions.jsonl", Lines(new[] {
        new { month = "أيلول 2026", name = "أحمد وهمي", title = "جهة وهمية", amount = (decimal?)2500m, note = "", rank = "" },
        new { month = "أيلول 2026", name = "احمد وهمي", title = "جهة وهمية", amount = (decimal?)null, note = "", rank = "" },
        new { month = "أيلول 2026", name = "علي وهمي", title = "جهة وهمية", amount = (decimal?)null, note = "⟦حالة الاستقطاع:موقوف⟧", rank = "" }
    }));
    Entry(zip, "staff.jsonl", "");
    Entry(zip, "accounts.jsonl", Lines(new[] { new { entity = "جهة وهمية", iban = "IQ00TEST000000000000000", branch = "فرع تجريبي", swift = "TESTIQBAAAA", localAccount = (string?)null } }));
    Entry(zip, "settings.json", "{}");
    Entry(zip, "books/index.json", JsonSerializer.Serialize(new[] { new { id, title = "كتاب وهمي", month = "أيلول 2026", pages = 1, entity = "جهة وهمية" } }));
    if (!omitPdf) Entry(zip, $"books/{id.ToString().ToUpperInvariant()}.pdf", "%PDF-1.4\n%%EOF");
    if (duplicate) Entry(zip, "manifest.json", "{}");
}
static void Reject(string path, string reason)
{
    try { _ = BackupReader.Read(path); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException(reason);
}
var root = Path.Combine(Path.GetTempPath(), "salary-desktop-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    string valid = Path.Combine(root, "valid.zip"), wrong = Path.Combine(root, "wrong.zip"), duplicate = Path.Combine(root, "duplicate.zip"), missing = Path.Combine(root, "missing.zip");
    MakeBackup(valid); MakeBackup(wrong, wrongCount: true); MakeBackup(duplicate, duplicate: true); MakeBackup(missing, omitPdf: true);
    var before = SHA256.HashData(File.ReadAllBytes(valid));
    var snapshot = BackupReader.Read(valid);
    Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(valid))), "backup was changed");
    Check(snapshot.Version == 5 && snapshot.Months[0].Name == "تشرين الاول 2026" && snapshot.Employees.Count == 2 && snapshot.Accounts.Count == 1 && snapshot.Books.Count == 1, "valid backup contents");
    Check(JsonSerializer.Deserialize<Dictionary<string, decimal>>(snapshot.Employees[0].Items!)!["بند وهمي"] == 250m &&
          JsonSerializer.Deserialize<Dictionary<string, string>>(snapshot.Employees[0].Details!)!["الدرجة"] == "تجريبية", "salary detail data preserved");
    var preview = Path.Combine(root, "preview.pdf");
    BackupReader.CopyBook(snapshot, snapshot.Books[0], preview);
    Check(File.ReadAllText(preview).StartsWith("%PDF-", StringComparison.Ordinal), "archive PDF copy");
    var existing = Path.Combine(root, "existing.pdf"); File.WriteAllText(existing, "existing");
    try { BackupReader.CopyBook(snapshot, snapshot.Books[0], existing); throw new InvalidOperationException("existing file overwritten"); }
    catch (IOException) { }
    Check(File.ReadAllText(existing) == "existing", "existing file preserved");
    var month = PayrollAnalysis.Summarize(snapshot, "أيلول 2026");
    Check(month.Employees == 2 && month.NetTotal == 1250000m && month.MissingNet == 1 && month.Deductions == 2 && month.MissingAmounts == 1, "monthly totals and missing values");
    var groups = PayrollAnalysis.Groups(snapshot, "أيلول 2026");
    Check(groups.Count == 1 && groups[0].People == 1 && groups[0].Total == 2500m && groups[0].MissingAmounts == 1, "deduction grouping and inactive status");
    Check(PayrollAnalysis.Normalize("أحمد كَريم") == PayrollAnalysis.Normalize("احمد كريم"), "Arabic search normalization");
    using (var editedFile = new FileStream(valid, FileMode.Append, FileAccess.Write)) editedFile.WriteByte(0);
    try { BackupReader.CopyBook(snapshot, snapshot.Books[0], Path.Combine(root, "changed.pdf")); throw new InvalidOperationException("changed backup accepted"); }
    catch (InvalidDataException) { }
    Reject(wrong, "count mismatch accepted"); Reject(duplicate, "duplicate entry accepted"); Reject(missing, "missing archive document accepted");
    Console.WriteLine("Windows core: 13 checks passed with fictional data.");
}
finally { Directory.Delete(root, true); }
