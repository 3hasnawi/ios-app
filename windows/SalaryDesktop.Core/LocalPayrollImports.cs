using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

public sealed record MonthImport(string Month, List<Employee>? Employees, List<Deduction>? Deductions);
public sealed record PayrollImportState(BackupSnapshot Snapshot, IReadOnlyList<MonthImport> Imports, string? Revision);
/// الإصدارات القديمة تبقى على الجهاز حتى بعد استبدال بيانات شهر بالمراجعة.
public sealed class LocalPayrollImports(string directory)
{
    private sealed record Saved(int Version, string SourceDigest, List<MonthImport> Months);
    private sealed record Pointer(int Version, string Revision);
    private readonly string root = Path.GetFullPath(directory);
    private static string Digest(string digest) => digest.Length == 64 && digest.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') ? digest : throw new InvalidDataException("بصمة بيانات الرواتب غير صالحة.");
    private string Folder(BackupSnapshot source) => Path.Combine(root, Digest(source.SourceDigest));
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    private static Pointer ReadPointer(string path)
    {
        using var input = File.OpenRead(path);
        var pointer = JsonSerializer.Deserialize<Pointer>(input) ?? throw new JsonException();
        if (pointer.Version != 1) throw new InvalidDataException("إصدار بيانات الرواتب المحلية غير مدعوم.");
        _ = Digest(pointer.Revision);
        return pointer;
    }

    // لا نجمع ملف الأشهر في مصفوفة واحدة: يُكتب كل سجل على حدة إلى الملف وبصمته بالتتابع.
    private static string WriteImportFile(string path, string sourceDigest, IReadOnlyList<MonthImport> imports)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        using var hash = SHA256.Create();
        using (var hashed = new CryptoStream(output, hash, CryptoStreamMode.Write, leaveOpen: true))
        {
            using (var writer = new Utf8JsonWriter(hashed))
            {
                writer.WriteStartObject(); writer.WriteNumber("Version", 1); writer.WriteString("SourceDigest", sourceDigest);
                writer.WriteStartArray("Months");
                foreach (var month in imports)
                {
                    writer.WriteStartObject(); writer.WriteString("Month", month.Month);
                    void WriteItems<T>(string name, List<T>? items)
                    {
                        if (items is null) { writer.WriteNull(name); return; }
                        writer.WriteStartArray(name);
                        var count = 0;
                        foreach (var item in items) { JsonSerializer.Serialize(writer, item); if (++count % 256 == 0) writer.Flush(); }
                        writer.WriteEndArray();
                    }
                    WriteItems("Employees", month.Employees); WriteItems("Deductions", month.Deductions);
                    writer.WriteEndObject(); writer.Flush();
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
            }
            hashed.FlushFinalBlock();
        }
        output.Flush(true);
        return Convert.ToHexString(hash.Hash ?? throw new InvalidDataException("تعذّر حساب بصمة بيانات الشهر."));
    }
    private static BackupSnapshot Apply(BackupSnapshot source, IReadOnlyList<MonthImport> imports)
    {
        if (imports.Count > 5000 || imports.Select(i => i.Month).Distinct(StringComparer.Ordinal).Count() != imports.Count) throw new InvalidDataException("أشهر الاستيراد مكررة.");
        var months = source.Months.ToList(); var employees = source.Employees.ToList(); var deductions = source.Deductions.ToList();
        foreach (var item in imports)
        {
            if (item is null || MonthNames.SortKey(item.Month) == 0 || item.Employees is null && item.Deductions is null ||
                item.Employees is { Count: > 200_000 } || item.Deductions is { Count: > 500_000 } ||
                item.Employees?.Any(e => e is null || e.Month != item.Month || string.IsNullOrWhiteSpace(e.Name) || e.Net is null or < 0 or > 9_007_199_254_740_991m) == true ||
                item.Deductions?.Any(d => d is null || d.Month != item.Month || string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Title) || d.Amount is < 0 or > 9_007_199_254_740_991m || d.Amount is null && !PayrollAnalysis.IsInactive(d)) == true)
                throw new InvalidDataException("بيانات الشهر المستورد غير صالحة؛ لم تُستبدل البيانات.");
            var key = MonthNames.SortKey(item.Month);
            if (months.Any(m => MonthNames.SortKey(m.Name) == key && m.Name != item.Month)) throw new InvalidDataException("اسم الشهر يطابق شهراً محفوظاً باسم آخر؛ اختر الشهر المحفوظ نفسه.");
            if (item.Employees is not null) { employees.RemoveAll(e => e.Month == item.Month); employees.AddRange(item.Employees); }
            if (item.Deductions is not null) { deductions.RemoveAll(d => d.Month == item.Month); deductions.AddRange(item.Deductions); }
            int index = months.FindIndex(m => m.Name == item.Month);
            var counts = new PayrollMonth(item.Month, employees.Count(e => e.Month == item.Month), deductions.Count(d => d.Month == item.Month));
            if (index < 0) months.Add(counts); else months[index] = counts;
        }
        return source with { Months = months.OrderByDescending(m => MonthNames.SortKey(m.Name)).ToArray(), Employees = employees.ToArray(), Deductions = deductions.ToArray() };
    }
    public PayrollImportState Load(BackupSnapshot source)
    {
        var folder = Folder(source); var pointerPath = Path.Combine(folder, "current.json");
        if (!File.Exists(pointerPath)) return new(source, [], null);
        try
        {
            var pointer = ReadPointer(pointerPath);
            using var input = new FileStream(Path.Combine(folder, pointer.Revision + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (Hash(input) != pointer.Revision) throw new InvalidDataException("تغيّر ملف بيانات الرواتب المحلية.");
            input.Position = 0;
            var data = JsonSerializer.DeserializeAsync<Saved>(input).AsTask().GetAwaiter().GetResult() ?? throw new JsonException();
            if (data.Version != 1 || data.SourceDigest != source.SourceDigest || data.Months is null) throw new InvalidDataException("بيانات الرواتب المحلية غير صالحة.");
            return new(Apply(source, data.Months), data.Months, pointer.Revision);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new InvalidDataException("تعذّر فتح بيانات الرواتب المحلية؛ لم تُستبدل البيانات.", ex); }
    }
    public PayrollImportState Import(BackupSnapshot source, PayrollImportState expected, PayrollImportPreview preview, bool replaceExisting) => ImportBatch(source, expected, [preview], replaceExisting);
    public PayrollImportState ImportBatch(BackupSnapshot source, PayrollImportState expected, IReadOnlyList<PayrollImportPreview> previews, bool replaceExisting)
    {
        if (previews.Count is 0 or > 12 || previews.Any(p => p.Problems.Count > 0 || p.Employees.Count + p.Deductions.Count == 0 || MonthNames.SortKey(p.Month) == 0) || previews.Select(p => MonthNames.SortKey(p.Month)).Distinct().Count() != previews.Count)
            throw new InvalidDataException("راجع أخطاء الملف والأشهر قبل الحفظ.");
        var folder = Folder(source); Directory.CreateDirectory(folder);
        using var guard = new FileStream(Path.Combine(folder, ".payroll.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pointerPath = Path.Combine(folder, "current.json");
        var revision = File.Exists(pointerPath) ? ReadPointer(pointerPath).Revision : null;
        if (revision != expected.Revision || expected.Snapshot.SourceDigest != source.SourceDigest) throw new InvalidDataException("تغيّرت بيانات الرواتب في نافذة أخرى؛ أعد فتح الملف.");
        if (revision is not null)
        {
            using var existing = File.OpenRead(Path.Combine(folder, revision + ".json"));
            if (Hash(existing) != revision) throw new InvalidDataException("تغيّر ملف بيانات الرواتب المحلية.");
        }
        var current = expected;
        if (!replaceExisting && previews.Any(preview => preview.Employees.Count > 0 && current.Snapshot.Employees.Any(e => e.Month == preview.Month) || preview.Deductions.Count > 0 && current.Snapshot.Deductions.Any(d => d.Month == preview.Month)))
            throw new InvalidDataException("توجد بيانات محفوظة لأحد الأشهر؛ يجب مراجعة الاستبدال أولاً.");
        var imports = current.Imports.ToList();
        foreach (var preview in previews)
        {
            var existing = imports.FirstOrDefault(m => m.Month == preview.Month);
            var change = new MonthImport(preview.Month, preview.Employees.Count > 0 ? preview.Employees.ToList() : existing?.Employees,
                preview.Deductions.Count > 0 ? preview.Deductions.ToList() : existing?.Deductions);
            imports.RemoveAll(m => m.Month == preview.Month); imports.Add(change);
        }
        var updated = Apply(source, imports); var temp = Path.Combine(folder, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
        var pointerTemp = Path.Combine(folder, ".pointer-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var digest = WriteImportFile(temp, source.SourceDigest, imports);
            using (var input = File.OpenRead(temp)) if (Hash(input) != digest) throw new InvalidDataException("تعذّر التحقق من بيانات الشهر.");
            var versionPath = Path.Combine(folder, digest + ".json");
            if (!File.Exists(versionPath))
            {
                File.Move(temp, versionPath, false);
            }
            else { using var existingFile = File.OpenRead(versionPath); if (Hash(existingFile) != digest) throw new InvalidDataException("ملف بيانات الشهر المحفوظ تالف."); }
            using (var output = new FileStream(pointerTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { JsonSerializer.Serialize(output, new Pointer(1, digest)); output.Flush(true); }
            File.Move(pointerTemp, Path.Combine(folder, "current.json"), true);
            return new(updated, imports, digest);
        }
        finally { try { File.Delete(temp); File.Delete(pointerTemp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
