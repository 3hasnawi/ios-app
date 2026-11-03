using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SalaryDesktop.Core;

/// أداة ترحيل تجريبية للاختبار فقط. لا تُستخدم في واجهة التطبيق ولا تُرفع إلى السيرفر.
/// تحفظ كل بايت من السجلات والملفات الأصلية؛ النسخة الأصلية لا تُستبدل.
public static class SharedPayrollCandidate
{
    private const string SchemasName = "v7/payroll-schemas.jsonl";
    private const string OriginalManifestName = "v7/original-manifest.json";
    private const int MaxLineBytes = 16 * 1024 * 1024;
    private const long MaxSchemaBytes = 128L * 1024 * 1024;
    private const int MaxSchemaCount = 100_000;
    private sealed record SchemaLine(string Id, byte[] Data);
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));

    internal static IEnumerable<byte[]> Lines(Stream input)
    {
        var buffer = new byte[65_536];
        using var pending = new MemoryStream();
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != 10) continue;
                pending.Write(buffer, start, i + 1 - start); start = i + 1;
                if (pending.Length > MaxLineBytes) throw new InvalidDataException("سجل رواتب يتجاوز الحد المدعوم للترحيل التجريبي.");
                yield return pending.ToArray(); pending.SetLength(0);
            }
            pending.Write(buffer, start, read - start);
            if (pending.Length > MaxLineBytes) throw new InvalidDataException("سجل رواتب يتجاوز الحد المدعوم للترحيل التجريبي.");
        }
        if (pending.Length > 0) yield return pending.ToArray();
    }
    private static Dictionary<string, ZipArchiveEntry> Unique(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries) if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("توجد ملفات مكررة في النسخة الأصلية.");
        return entries;
    }
    private static void Copy(ZipArchiveEntry source, ZipArchive destination)
    {
        var entry = destination.CreateEntry(source.FullName, CompressionLevel.Optimal);
        entry.LastWriteTime = source.LastWriteTime; entry.ExternalAttributes = source.ExternalAttributes;
        using var input = source.Open(); using var output = entry.Open(); input.CopyTo(output);
    }

    /// يختبر إدخال ملفات محدثة إلى نسخة مؤقتة قبل الترحيل. المنتج للمحتوى مسؤول عن تجهيز جميع
    /// السجلات والفهارس والإعدادات بعد دمج التعديلات؛ هذه الدالة وحدها لا تقرأ الاستيرادات المحلية.
    public static void CreateMerged(string source, string destination, IReadOnlyDictionary<string, Action<Stream>> replacements, string? expectedSourceDigest = null)
    {
        var original = Path.GetFullPath(source); var target = Path.GetFullPath(destination);
        if (string.Equals(original, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target)) throw new InvalidDataException("اختر ملفاً جديداً لحماية النسخة الأصلية.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد غير صالح.");
        var merged = Path.Combine(directory, ".salary-merge-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var originalFile = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sourceHash = Hash(originalFile); originalFile.Position = 0;
            if (expectedSourceDigest is not null && !string.Equals(sourceHash, expectedSourceDigest, StringComparison.Ordinal))
                throw new InvalidDataException("تغيّرت النسخة الأصلية؛ أعد فتحها قبل الدمج.");
            using var originalZip = new ZipArchive(originalFile, ZipArchiveMode.Read, true);
            var old = Unique(originalZip);
            if (replacements.Keys.Any(name => !old.ContainsKey(name) || name is SchemasName or OriginalManifestName))
                throw new InvalidDataException("أحد ملفات التعديلات غير موجود في النسخة الأصلية.");
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var output = new FileStream(merged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65_536, FileOptions.WriteThrough))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var entry in originalZip.Entries)
                    {
                        if (!replacements.TryGetValue(entry.FullName, out var write)) { Copy(entry, zip); continue; }
                        var replacement = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                        replacement.LastWriteTime = entry.LastWriteTime; replacement.ExternalAttributes = entry.ExternalAttributes;
                        using var stream = replacement.Open(); using var hash = SHA256.Create();
                        using (var hashed = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true))
                        { write(hashed); hashed.FlushFinalBlock(); }
                        expected.Add(entry.FullName, Convert.ToHexString(hash.Hash ?? throw new InvalidDataException("تعذّر حساب بصمة التعديلات.")));
                    }
                }
                output.Flush(true);
            }
            using (var mergedZip = ZipFile.OpenRead(merged))
            {
                var result = Unique(mergedZip);
                if (result.Count != old.Count) throw new InvalidDataException("ملفات النسخة المدمجة غير مكتملة.");
                foreach (var (name, oldEntry) in old)
                {
                    if (!result.TryGetValue(name, out var entry)) throw new InvalidDataException("ملف مفقود في النسخة المدمجة.");
                    using var written = entry.Open(); var actual = Hash(written);
                    if (expected.TryGetValue(name, out var changed))
                    {
                        if (actual != changed) throw new InvalidDataException("تعذّر التحقق من محتوى التعديلات.");
                    }
                    else { using var previous = oldEntry.Open(); if (actual != Hash(previous)) throw new InvalidDataException("تغيّر ملف خارج التعديلات."); }
                }
            }
            originalFile.Position = 0;
            if (Hash(originalFile) != sourceHash) throw new InvalidDataException("تغيّرت النسخة الأصلية أثناء الدمج.");
            Create(merged, target);
        }
        finally { try { File.Delete(merged); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static Dictionary<string, byte[]> ReadSchemas(ZipArchiveEntry entry)
    {
        var schemas = new Dictionary<string, byte[]>(StringComparer.Ordinal); long size = 0;
        using var input = entry.Open();
        foreach (var line in Lines(input))
        {
            var schema = JsonSerializer.Deserialize<SchemaLine>(line) ?? throw new InvalidDataException("مخطط أعمدة فارغ.");
            if (schema.Id is null || schema.Data is null) throw new InvalidDataException("مخطط أعمدة ناقص.");
            size = checked(size + schema.Data.Length);
            if (size > MaxSchemaBytes || schemas.Count >= MaxSchemaCount || !schemas.TryAdd(schema.Id, schema.Data) ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(schema.Data)), schema.Id, StringComparison.Ordinal))
                throw new InvalidDataException("مخطط أعمدة مكرر أو تالف أو كبير جداً.");
        }
        return schemas;
    }

    /// تنشئ ملفاً جديداً فقط، ثم تعيد فتحه وتفحص كل ملف وتعيد تركيب كل سجل قبل تسليمه.
    public static void Create(string source, string destination)
    {
        var original = Path.GetFullPath(source); var target = Path.GetFullPath(destination);
        if (string.Equals(original, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target)) throw new InvalidDataException("اختر ملفاً جديداً لحماية النسخة الأصلية.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد غير صالح.");
        var temp = Path.Combine(directory, ".salary-compact-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var originalFile = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
            var originalDigest = Hash(originalFile); originalFile.Position = 0;
            using var oldZip = new ZipArchive(originalFile, ZipArchiveMode.Read, true);
            var old = Unique(oldZip);
            if (!old.TryGetValue("manifest.json", out var manifestEntry) || !old.TryGetValue("employees.jsonl", out var employees) || old.ContainsKey(SchemasName) || old.ContainsKey(OriginalManifestName))
                throw new InvalidDataException("النسخة الأصلية أو ملفات الترحيل غير صالحة.");
            if (manifestEntry.Length > 1_000_000) throw new InvalidDataException("فهرس النسخة كبير جداً.");
            byte[] manifestBytes;
            using (var input = manifestEntry.Open()) using (var memory = new MemoryStream()) { input.CopyTo(memory); manifestBytes = memory.ToArray(); }
            var manifest = JsonNode.Parse(manifestBytes)?.AsObject() ?? throw new InvalidDataException("فهرس النسخة غير صالح.");
            if (manifest["version"]?.GetValue<int>() is not (>= 1 and <= 6)) throw new InvalidDataException("إصدار النسخة الأصلية غير مدعوم.");
            manifest["version"] = 7;
            var schemas = new Dictionary<string, byte[]>(StringComparer.Ordinal); long schemaBytes = 0;
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65_536, FileOptions.WriteThrough))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var entry in oldZip.Entries)
                    {
                        if (entry.FullName == "manifest.json")
                        {
                            var saved = zip.CreateEntry(OriginalManifestName, CompressionLevel.Optimal);
                            saved.LastWriteTime = entry.LastWriteTime; saved.ExternalAttributes = entry.ExternalAttributes;
                            using (var savedStream = saved.Open()) savedStream.Write(manifestBytes);
                            var updated = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                            updated.LastWriteTime = entry.LastWriteTime; updated.ExternalAttributes = entry.ExternalAttributes;
                            using var manifestStream = updated.Open(); JsonSerializer.Serialize(manifestStream, manifest);
                        }
                        else if (entry.FullName == "employees.jsonl")
                        {
                            var packed = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                            packed.LastWriteTime = entry.LastWriteTime; packed.ExternalAttributes = entry.ExternalAttributes;
                            using var outputRows = packed.Open(); using var inputRows = entry.Open();
                            foreach (var line in Lines(inputRows))
                            {
                                var previousCount = schemas.Count;
                                var row = SharedPayrollBackupRow.Pack(line, schemas);
                                if (schemas.Count != previousCount && row.SchemaId is { } id) schemaBytes = checked(schemaBytes + schemas[id].Length);
                                if (schemas.Count > MaxSchemaCount || schemaBytes > MaxSchemaBytes) throw new InvalidDataException("عدد أو حجم مخططات الأعمدة أكبر من الحد التجريبي.");
                                JsonSerializer.Serialize(outputRows, row); outputRows.WriteByte(10);
                            }
                        }
                        else Copy(entry, zip);
                    }
                    var schemaFile = zip.CreateEntry(SchemasName, CompressionLevel.Optimal);
                    using var schemaStream = schemaFile.Open();
                    foreach (var (id, data) in schemas.OrderBy(p => p.Key, StringComparer.Ordinal)) { JsonSerializer.Serialize(schemaStream, new SchemaLine(id, data)); schemaStream.WriteByte(10); }
                }
                output.Flush(true);
            }
            using (var candidate = ZipFile.OpenRead(temp))
            {
                var current = Unique(candidate);
                if (current.Count != old.Count + 2 || !current.TryGetValue(SchemasName, out var schemaFile) || !current.TryGetValue(OriginalManifestName, out var savedManifest)) throw new InvalidDataException("ملفات النسخة التجريبية غير مكتملة.");
                using (var saved = savedManifest.Open()) if (!SHA256.HashData(saved).AsSpan().SequenceEqual(SHA256.HashData(manifestBytes))) throw new InvalidDataException("فهرس النسخة الأصلية تغير.");
                var checkSchemas = ReadSchemas(schemaFile);
                using var outRows = current["employees.jsonl"].Open();
                using var running = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var line in Lines(outRows))
                {
                    var row = JsonSerializer.Deserialize<SharedPayrollBackupRow.Packed>(line) ?? throw new InvalidDataException("سجل رواتب تجريبي غير صالح.");
                    running.AppendData(SharedPayrollBackupRow.Unpack(row, checkSchemas));
                }
                using (var originalRows = employees.Open()) if (!running.GetHashAndReset().AsSpan().SequenceEqual(SHA256.HashData(originalRows))) throw new InvalidDataException("سجلات الرواتب لا تطابق النسخة الأصلية.");
                foreach (var (name, originalEntry) in old.Where(p => p.Key is not ("manifest.json" or "employees.jsonl")))
                {
                    if (!current.TryGetValue(name, out var copied)) throw new InvalidDataException("ملف مفقود في النسخة التجريبية.");
                    using var a = originalEntry.Open(); using var b = copied.Open();
                    if (!SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b))) throw new InvalidDataException("أحد ملفات النسخة التجريبية تغير.");
                }
                using var candidateManifest = current["manifest.json"].Open();
                var checkManifest = JsonNode.Parse(candidateManifest)?.AsObject() ?? throw new InvalidDataException("فهرس النسخة التجريبية غير صالح.");
                if (checkManifest["version"]?.GetValue<int>() != 7 || !JsonNode.DeepEquals(checkManifest, manifest)) throw new InvalidDataException("فهرس النسخة التجريبية لا يطابق المصدر.");
            }
            originalFile.Position = 0;
            if (Hash(originalFile) != originalDigest) throw new InvalidDataException("تغيّرت النسخة الأصلية أثناء الترحيل.");
            File.Move(temp, target, false);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// استعادة تجريبية إلى ملف جديد؛ تُفحص بصمات المخططات والصفوف وكل الملفات قبل تسليمه.
    public static void RestoreOriginal(string candidatePath, string destination)
    {
        var candidate = Path.GetFullPath(candidatePath); var target = Path.GetFullPath(destination);
        if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target)) throw new InvalidDataException("اختر ملفاً جديداً لحماية النسخة التجريبية.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد غير صالح.");
        var temp = Path.Combine(directory, ".salary-unpack-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var inputFile = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
            var originalDigest = Hash(inputFile); inputFile.Position = 0;
            using var zip = new ZipArchive(inputFile, ZipArchiveMode.Read, true);
            var entries = Unique(zip);
            if (!entries.TryGetValue("manifest.json", out var manifest) || !entries.TryGetValue(OriginalManifestName, out var oldManifest) ||
                !entries.TryGetValue(SchemasName, out var schemaFile) || !entries.TryGetValue("employees.jsonl", out var employees))
                throw new InvalidDataException("ملفات النسخة التجريبية غير مكتملة.");
            if (manifest.Length > 1_000_000 || oldManifest.Length > 1_000_000) throw new InvalidDataException("فهرس النسخة كبير جداً.");
            using (var current = manifest.Open()) if (JsonNode.Parse(current)?["version"]?.GetValue<int>() != 7) throw new InvalidDataException("إصدار النسخة التجريبية غير صالح.");
            using (var previous = oldManifest.Open()) if (JsonNode.Parse(previous)?["version"]?.GetValue<int>() is not (>= 1 and <= 6)) throw new InvalidDataException("فهرس النسخة الأصلية غير صالح.");
            var schemas = ReadSchemas(schemaFile);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65_536, FileOptions.WriteThrough))
            {
                using (var result = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName is SchemasName or OriginalManifestName) continue;
                        if (entry.FullName == "manifest.json")
                        {
                            var restored = result.CreateEntry("manifest.json", CompressionLevel.Optimal);
                            restored.LastWriteTime = oldManifest.LastWriteTime; restored.ExternalAttributes = oldManifest.ExternalAttributes;
                            using var source = oldManifest.Open(); using var destinationStream = restored.Open(); source.CopyTo(destinationStream);
                        }
                        else if (entry.FullName == "employees.jsonl")
                        {
                            var restored = result.CreateEntry("employees.jsonl", CompressionLevel.Optimal);
                            restored.LastWriteTime = entry.LastWriteTime; restored.ExternalAttributes = entry.ExternalAttributes;
                            using var rows = entry.Open(); using var destinationStream = restored.Open();
                            foreach (var line in Lines(rows))
                            {
                                var row = JsonSerializer.Deserialize<SharedPayrollBackupRow.Packed>(line) ?? throw new InvalidDataException("سجل رواتب تجريبي غير صالح.");
                                destinationStream.Write(SharedPayrollBackupRow.Unpack(row, schemas));
                            }
                        }
                        else Copy(entry, result);
                    }
                }
                output.Flush(true);
            }
            using (var restored = ZipFile.OpenRead(temp))
            {
                var outputEntries = Unique(restored);
                if (outputEntries.Count != entries.Count - 2) throw new InvalidDataException("ملفات الاستعادة غير مكتملة.");
                foreach (var (name, entry) in entries.Where(p => p.Key is not (SchemasName or OriginalManifestName or "employees.jsonl" or "manifest.json")))
                {
                    if (!outputEntries.TryGetValue(name, out var copied)) throw new InvalidDataException("ملف مفقود بعد الاستعادة.");
                    using var a = entry.Open(); using var b = copied.Open();
                    if (!SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b))) throw new InvalidDataException("أحد الملفات تغير أثناء الاستعادة.");
                }
                using (var a = oldManifest.Open()) using (var b = outputEntries["manifest.json"].Open())
                    if (!SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b))) throw new InvalidDataException("فهرس النسخة المستعادة تغير.");
                using var rows = employees.Open(); using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var line in Lines(rows))
                {
                    var row = JsonSerializer.Deserialize<SharedPayrollBackupRow.Packed>(line) ?? throw new InvalidDataException("سجل رواتب تجريبي غير صالح.");
                    expected.AppendData(SharedPayrollBackupRow.Unpack(row, schemas));
                }
                using var actual = outputEntries["employees.jsonl"].Open();
                if (!expected.GetHashAndReset().AsSpan().SequenceEqual(SHA256.HashData(actual))) throw new InvalidDataException("سجلات الرواتب المستعادة تغيرت.");
            }
            inputFile.Position = 0;
            if (Hash(inputFile) != originalDigest) throw new InvalidDataException("تغيّرت النسخة التجريبية أثناء الاستعادة.");
            File.Move(temp, target, false);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
