using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SalaryDesktop.Core;

/// تجريبي وغير موصول بالتطبيق: يضيف استيرادات الرواتب والاستقطاعات إلى مرشح منفصل.
/// لا يجمع أو يقفل المخازن المحلية تلقائياً؛ يدمج فقط اللقطة والتعديلات التي يمررها المستدعي.
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
    public static void Create(BackupSnapshot source, PayrollImportState state, string destination) => CreateCore(source, state, state.Snapshot, null, null, destination);

    /// يضيف الحسابات والإعدادات ودفتر المطابقة والأمانات والأرشيف من اللقطة الممررة صراحةً.
    public static void CreateIncludingSettings(BackupSnapshot source, PayrollImportState state, BackupSnapshot active,
        IReadOnlyList<BankPeriod> periods, SalaryTrustLedger? trust, string destination) => CreateCore(source, state, active, periods, trust, destination);

    private static void CreateCore(BackupSnapshot source, PayrollImportState state, BackupSnapshot active,
        IReadOnlyList<BankPeriod>? periods, SalaryTrustLedger? trust, string destination)
    {
        if (source.SourceDigest != state.Snapshot.SourceDigest || source.SourceDigest != active.SourceDigest || state.Imports.Any(i => i is null || MonthNames.SortKey(i.Month) == 0) ||
            state.Imports.Select(i => i.Month).Distinct(StringComparer.Ordinal).Count() != state.Imports.Count ||
            source.Months.Any(m => !state.Snapshot.Months.Any(n => n.Name == m.Name)))
            throw new InvalidDataException("بيانات الأشهر المحلية لا تطابق النسخة الأصلية.");
        if (!active.Months.SequenceEqual(state.Snapshot.Months) || !ReconciliationBackupExport.SameEmployees(active.Employees, state.Snapshot.Employees) ||
            !active.Deductions.SequenceEqual(state.Snapshot.Deductions)) throw new InvalidDataException("بيانات الرواتب المعروضة لا تطابق حالة الاستيراد.");
        var localBooks = active.LocalBookFiles ?? new Dictionary<Guid, string>();
        var archiveChanged = localBooks.Count > 0 || !active.Books.SequenceEqual(source.Books);
        if (archiveChanged && (periods is null || source.Version < 3 || active.Books.Select(b => b.Id).Distinct().Count() != active.Books.Count ||
            localBooks.Keys.Any(id => !active.Books.Any(b => b.Id == id)) ||
            active.Books.Any(b => !localBooks.ContainsKey(b.Id) && !source.Books.Any(o => o.Id == b.Id && o.Pages == b.Pages))))
            throw new InvalidDataException("تعديلات الأرشيف غير صالحة أو غير مدعومة في المرشح التجريبي.");
        if (periods is not null && (source.Version < 2 || active.Accounts.Count < source.Accounts.Count ||
            !source.Accounts.Select(a => a.Entity).SequenceEqual(active.Accounts.Take(source.Accounts.Count).Select(a => a.Entity))))
            throw new InvalidDataException("الحسابات لا تطابق صيغة النسخة الأصلية.");
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
        if (periods is not null)
        {
            manifest["accounts"] = active.Accounts.Count;
            if (archiveChanged) manifest["books"] = active.Books.Count;
            if (active.SettingsPlists.ContainsKey(ContractOverrides.SettingsKey)) manifest["version"] = 6;
        }
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
        var additions = new Dictionary<string, Action<Stream>>(StringComparer.Ordinal);
        var removals = new HashSet<string>(StringComparer.Ordinal);
        if (archiveChanged)
        {
            static string BookName(Guid id) => "books/" + id.ToString().ToUpperInvariant() + ".pdf";
            replacements["books/index.json"] = output => WithEntry("books/index.json", input => output.Write(ReconciliationBackupExport.BookIndex(input, active.Books)));
            foreach (var old in source.Books.Where(b => localBooks.ContainsKey(b.Id) || !active.Books.Any(a => a.Id == b.Id))) removals.Add(BookName(old.Id));
            foreach (var (id, path) in localBooks)
            {
                additions.Add(BookName(id), output =>
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length is < 5 or >= 60_000_000) throw new InvalidDataException("ملف كتاب الأرشيف غير صالح أو أكبر من الحد المدعوم.");
                    var before = SHA256.HashData(file); file.Position = 0;
                    Span<byte> header = stackalloc byte[5]; file.ReadExactly(header);
                    if (!header.SequenceEqual("%PDF-"u8)) throw new InvalidDataException("ملف كتاب الأرشيف ليس PDF صالحاً.");
                    output.Write(header); file.CopyTo(output); file.Position = 0;
                    if (!SHA256.HashData(file).AsSpan().SequenceEqual(before)) throw new InvalidDataException("تغيّر ملف الأرشيف أثناء إعداد النسخة.");
                });
            }
        }
        if (periods is not null)
        {
            var settings = active.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            settings["bankReconciliations"] = BankReconciliation.LedgerPlist(periods);
            if (trust is not null) { SalaryTrust.ValidateBooks(trust, active.Books); settings[SalaryTrust.SettingsKey] = SalaryTrust.Plist(trust); }
            replacements["settings.json"] = output => JsonSerializer.Serialize(output, settings);
            replacements["accounts.jsonl"] = output =>
            {
                using var writer = new StreamWriter(output, Utf8, 65_536, true);
                foreach (var a in active.Accounts) writer.WriteLine(JsonSerializer.Serialize(new { entity = a.Entity, iban = a.Iban, branch = a.Branch, swift = a.Swift, localAccount = a.LocalAccount }));
            };
        }
        SharedPayrollCandidate.CreateMerged(source.SourcePath, destination, replacements, source.SourceDigest, additions, removals);
    }
}
