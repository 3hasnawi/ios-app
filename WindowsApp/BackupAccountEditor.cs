using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SalaryWindows;

// يحدّث حساب جهة في نسخة مستقلة مع إبقاء كل الأقسام والحقول غير المعروفة.
internal static class BackupAccountEditor
{
    public static string NormalizeIban(string input)
    {
        var text = new StringBuilder();
        foreach (var raw in input.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(raw) || raw == '-') continue;
            if (raw is >= '٠' and <= '٩') text.Append((char)('0' + raw - '٠'));
            else if (raw is >= '۰' and <= '۹') text.Append((char)('0' + raw - '۰'));
            else text.Append(raw);
        }
        var result = text.ToString();
        if (!result.StartsWith("IQ", StringComparison.Ordinal) || result.Length != 23 || result.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9')))
            throw new InvalidDataException("رقم IBAN العراقي يجب أن يبدأ بـ IQ ويتكون من 23 حرفاً ورقماً.");
        int remainder = 0;
        foreach (var c in result[4..] + result[..4])
        {
            var digits = c is >= 'A' and <= 'Z' ? (c - 'A' + 10).ToString() : c.ToString();
            foreach (var digit in digits) remainder = (remainder * 10 + digit - '0') % 97;
        }
        if (remainder != 1) throw new InvalidDataException("رقم IBAN لا يجتاز فحص التدقيق. راجع الرقم مع المصرف.");
        return result;
    }

    public static void SaveCopy(string source, string destination, Account replacement, string? originalEntity)
    {
        if (string.IsNullOrWhiteSpace(replacement.Entity)) throw new InvalidDataException("أدخل اسم جهة التنفيذ.");
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("اختر اسماً مختلفاً للنسخة المعدلة؛ لا يُستبدل الملف المصدر.");
        var iban = NormalizeIban(replacement.Iban);
        var normalizedNew = PayrollMath.Normalize(replacement.Entity.Trim());
        var normalizedOld = originalEntity == null ? null : PayrollMath.Normalize(originalEntity);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var originalHash = Hash(source);
            using (var input = ZipFile.OpenRead(source))
            using (var output = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var manifestEntry = input.GetEntry("manifest.json") ?? throw new InvalidDataException("فهرس النسخة غير موجود.");
                var accountsEntry = input.GetEntry("accounts.jsonl") ?? throw new InvalidDataException("صيغة النسخة لا تدعم تعديل الحسابات.");
                JsonObject manifest;
                using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
                    manifest = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new InvalidDataException("فهرس النسخة غير صالح.");
                if (manifest["version"]?.GetValue<int>() < 2) throw new InvalidDataException("صيغة النسخة قديمة ولا تدعم تعديل الحسابات.");
                var rows = new List<JsonObject>();
                using (var reader = new StreamReader(accountsEntry.Open(), Encoding.UTF8))
                    while (reader.ReadLine() is { } line)
                        if (!string.IsNullOrWhiteSpace(line)) rows.Add(JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("بيانات الحسابات غير صالحة."));
                if (rows.Count != manifest["accounts"]?.GetValue<int>()) throw new InvalidDataException("عدد الحسابات لا يطابق فهرس النسخة.");
                string Key(JsonObject row) => PayrollMath.Normalize(row["entity"]?.GetValue<string>() ?? "");
                var matches = normalizedOld == null ? [] : rows.Where(row => Key(row) == normalizedOld).ToList();
                if (normalizedOld != null && matches.Count != 1) throw new InvalidDataException("تعذّر تحديد حساب الجهة بدقة؛ لم تُغيّر النسخة.");
                if (rows.Any(row => Key(row) == normalizedNew && (matches.Count == 0 || !ReferenceEquals(row, matches[0]))))
                    throw new InvalidDataException("يوجد حساب آخر باسم الجهة نفسه.");
                var account = matches.Count == 1 ? matches[0] : new JsonObject();
                account["entity"] = replacement.Entity.Trim(); account["iban"] = iban;
                account["branch"] = replacement.Branch.Trim(); account["swift"] = replacement.Swift.Trim().ToUpperInvariant();
                if (matches.Count == 0) rows.Add(account);
                manifest["accounts"] = rows.Count;
                manifest["createdAt"] = (DateTimeOffset.UtcNow - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
                foreach (var entry in input.Entries)
                {
                    var outEntry = output.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                    using var outStream = outEntry.Open();
                    if (entry.FullName == "manifest.json") { using var writer = new StreamWriter(outStream, new UTF8Encoding(false)); writer.Write(manifest.ToJsonString()); }
                    else if (entry.FullName == "accounts.jsonl") { using var writer = new StreamWriter(outStream, new UTF8Encoding(false)); foreach (var row in rows) writer.WriteLine(row.ToJsonString()); }
                    else { using var inStream = entry.Open(); inStream.CopyTo(outStream); }
                }
            }
            if (!CryptographicOperations.FixedTimeEquals(originalHash, Hash(source))) throw new IOException("تغيّرت النسخة المصدر أثناء التحرير؛ لم تُحفظ النتيجة.");
            _ = BackupReader.Read(temp); // فحص متكامل قبل وضع الملف في مكانه
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static byte[] Hash(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
}
