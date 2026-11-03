using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SalaryDesktop.Core;

/// تجريبي وغير موصول بالتطبيق: يضيف استيرادات الرواتب والاستقطاعات إلى مرشح منفصل.
/// لا يدمج روابط الحسابات أو الأرشيف أو المطابقة أو الأمانات أو إعدادات ويندوز المحلية.
public static class SharedPayrollOverlay
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string Month(byte[] line)
    {
        using var doc = JsonDocument.Parse(line);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("سجل شهر غير صالح.");
        var matches = doc.RootElement.EnumerateObject().Where(p => string.Equals(p.Name, "month", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(matches[0].Value.GetString()))
            throw new InvalidDataException("شهر أحد السجلات غير صالح أو مكرر.");
        return matches[0].Value.GetString()!;
    }
    /// لا يعتمد الإصدار المحلي أو يحميه من التعديل المتزامن؛ يجب على المستدعي التحقق من مراجع كل المخازن.
    public static void Create(BackupSnapshot source, PayrollImportState state, string destination)
    {
        if (source.SourceDigest != state.Snapshot.SourceDigest || state.Imports.Any(i => i is null || MonthNames.SortKey(i.Month) == 0) ||
            state.Imports.Select(i => i.Month).Distinct(StringComparer.Ordinal).Count() != state.Imports.Count ||
            source.Months.Any(m => !state.Snapshot.Months.Any(n => n.Name == m.Name)))
            throw new InvalidDataException("بيانات الأشهر المحلية لا تطابق النسخة الأصلية.");
        var salaryMonths = state.Imports.Where(i => i.Employees is not null).ToDictionary(i => i.Month, i => i.Employees!, StringComparer.Ordinal);
        var deductionMonths = state.Imports.Where(i => i.Deductions is not null).ToDictionary(i => i.Month, i => i.Deductions!, StringComparer.Ordinal);
        if (salaryMonths.Any(p => p.Value.Any(e => e.Month != p.Key)) || deductionMonths.Any(p => p.Value.Any(d => d.Month != p.Key)))
            throw new InvalidDataException("سجلات الاستيراد لا تطابق الشهر.");
        using var sourceFile = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(sourceFile)) != source.SourceDigest) throw new InvalidDataException("تغيّرت النسخة الأصلية؛ أعد فتحها.");
        sourceFile.Position = 0;
        using var sourceZip = new ZipArchive(sourceFile, ZipArchiveMode.Read, true);
        void WithEntry(string name, Action<Stream> action)
        {
            var entries = sourceZip.Entries.Where(e => e.FullName == name).ToArray();
            if (entries.Length != 1) throw new InvalidDataException("أحد ملفات النسخة مفقود أو مكرر.");
            using var input = entries[0].Open(); action(input);
        }
        var monthsByName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        WithEntry("months.jsonl", input =>
        {
            foreach (var line in SharedPayrollCandidate.Lines(input))
            {
                var node = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("بيانات الأشهر غير صالحة.");
                var name = node["name"]?.GetValue<string>() ?? throw new InvalidDataException("اسم شهر غير صالح.");
                if (!monthsByName.TryAdd(name, node)) throw new InvalidDataException("تكرر شهر في النسخة الأصلية.");
            }
        });
        JsonObject manifest = null!;
        WithEntry("manifest.json", input => manifest = JsonNode.Parse(input)?.AsObject() ?? throw new InvalidDataException("فهرس النسخة غير صالح."));
        manifest["createdAt"] = ReconciliationBackupExport.IosSeconds(DateTimeOffset.UtcNow);
        manifest["months"] = new JsonArray(state.Snapshot.Months.Select(m => (JsonNode?)JsonValue.Create(m.Name)).ToArray());
        manifest["employees"] = state.Snapshot.Employees.Count; manifest["deductions"] = state.Snapshot.Deductions.Count;
        var replacements = new Dictionary<string, Action<Stream>>(StringComparer.Ordinal)
        {
            ["manifest.json"] = output => JsonSerializer.Serialize(output, manifest),
            ["months.jsonl"] = output =>
            {
                using var writer = new StreamWriter(output, Utf8, 65_536, true);
                foreach (var month in state.Snapshot.Months)
                {
                    var node = monthsByName.TryGetValue(month.Name, out var prior) ? (JsonObject)prior.DeepClone() :
                        new JsonObject { ["name"] = month.Name, ["importedAt"] = ReconciliationBackupExport.IosSeconds(DateTimeOffset.UtcNow) };
                    node["employeeCount"] = month.EmployeeCount; node["deductionCount"] = month.DeductionCount;
                    if (salaryMonths.ContainsKey(month.Name)) node["summary"] = Convert.ToBase64String(ReconciliationBackupExport.Summary(state.Snapshot.Employees.Where(e => e.Month == month.Name)));
                    writer.WriteLine(node.ToJsonString());
                }
            },
            ["employees.jsonl"] = output =>
            {
                var lastByte = -1;
                WithEntry("employees.jsonl", input =>
                {
                    foreach (var line in SharedPayrollCandidate.Lines(input))
                    {
                        if (salaryMonths.ContainsKey(Month(line))) continue;
                        output.Write(line); lastByte = line[^1];
                    }
                });
                if (salaryMonths.Count > 0 && lastByte >= 0 && lastByte != 10) output.WriteByte(10);
                using var writer = new StreamWriter(output, Utf8, 65_536, true);
                foreach (var item in state.Imports.Where(i => i.Employees is not null))
                    foreach (var e in item.Employees!) writer.WriteLine(JsonSerializer.Serialize(new { month = e.Month, name = e.Name, rank = e.Rank, workplace = e.Workplace, net = e.Net, items = e.Items, details = e.Details, rawColumns = e.RawColumns }));
            },
            ["deductions.jsonl"] = output =>
            {
                var lastByte = -1;
                WithEntry("deductions.jsonl", input =>
                {
                    foreach (var line in SharedPayrollCandidate.Lines(input))
                    {
                        if (deductionMonths.ContainsKey(Month(line))) continue;
                        output.Write(line); lastByte = line[^1];
                    }
                });
                if (deductionMonths.Count > 0 && lastByte >= 0 && lastByte != 10) output.WriteByte(10);
                using var writer = new StreamWriter(output, Utf8, 65_536, true);
                foreach (var item in state.Imports.Where(i => i.Deductions is not null))
                    foreach (var d in item.Deductions!) writer.WriteLine(JsonSerializer.Serialize(new { month = d.Month, name = d.Name, title = d.Title, amount = d.Amount, note = d.Note, rank = d.Rank }));
            }
        };
        SharedPayrollCandidate.CreateMerged(source.SourcePath, destination, replacements, source.SourceDigest);
    }
}
