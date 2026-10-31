using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace SalaryDesktop.Core;

// Snapshot is read only. The original backup is never extracted or overwritten.
public sealed record PayrollMonth(string Name, int EmployeeCount, int DeductionCount);
public sealed record Employee(string Month, string Name, string Rank, string Workplace, decimal? Net, byte[]? Items, byte[]? Details, byte[]? RawColumns);
public sealed record Deduction(string Month, string Name, string Title, decimal? Amount, string Note, string Rank);
public sealed record ExecutionAccount(string Entity, string Iban, string Branch, string Swift, string? LocalAccount);
public sealed record ArchiveBook(Guid Id, string Title, string Month, int Pages, string? Entity);
public sealed record BackupSnapshot(string SourcePath, string SourceDigest, int Version, IReadOnlyList<PayrollMonth> Months,
    IReadOnlyList<Employee> Employees, IReadOnlyList<Deduction> Deductions, int StaffCount,
    IReadOnlyList<ExecutionAccount> Accounts, IReadOnlyList<ArchiveBook> Books, IReadOnlyDictionary<string, byte[]> SettingsPlists);

public static class BackupReader
{
    private const long MaximumEntryBytes = 1_500_000_000;
    private const int MaximumLineCharacters = 2_000_000;
    private const decimal MaximumAmount = 9_007_199_254_740_991m;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, MaxDepth = 64 };
    private sealed record Manifest(int Version, string[] Months, int Employees, int Deductions, int Staff, int? Accounts, int? Books);
    private sealed record MonthData(string Name, int EmployeeCount, int DeductionCount);
    private sealed record EmployeeData(string Month, string Name, string Rank, string Workplace, decimal? Net, byte[]? Items, byte[]? Details, byte[]? RawColumns);
    private sealed record DeductionData(string Month, string Name, string Title, decimal? Amount, string Note, string Rank);
    private sealed record AccountData(string Entity, string Iban, string Branch, string Swift, string? LocalAccount);
    private sealed record BookData(Guid Id, string Title, string Month, int Pages, string? Entity);

    // The caller chooses a new private temporary file; the UUID is resolved only inside the original backup.
    public static void CopyBook(BackupSnapshot snapshot, ArchiveBook book, string destination)
    {
        if (!snapshot.Books.Any(item => item.Id == book.Id)) throw new InvalidDataException("الكتاب غير موجود في فهرس النسخة المفتوحة.");
        using var input = new FileStream(snapshot.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), snapshot.SourceDigest, StringComparison.Ordinal))
            throw new InvalidDataException("تغيّر ملف النسخة منذ فتحه؛ أعد فتح النسخة قبل عرض الكتاب.");
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var name = $"books/{book.Id.ToString().ToUpperInvariant()}.pdf";
        var matches = archive.Entries.Where(entry => entry.FullName == name).ToArray();
        if (matches.Length != 1 || matches[0].Length is < 5 or > 100_000_000) throw new InvalidDataException("ملف الكتاب غير صالح أو كبير جداً.");
        using var source = matches[0].Open();
        var bytes = new byte[5]; source.ReadExactly(bytes);
        if (!bytes.AsSpan().SequenceEqual("%PDF-"u8)) throw new InvalidDataException("ملف الكتاب ليس PDF صالحاً.");
        var created = false;
        try
        {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            created = true;
            output.Write(bytes); source.CopyTo(output);
        }
        catch { if (created) { try { File.Delete(destination); } catch (IOException) { } } throw; }
    }

    public static BackupSnapshot Read(string file, CancellationToken cancellation = default)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!entries.TryAdd(entry.FullName, entry) || entry.Length > MaximumEntryBytes)
                throw new InvalidDataException("النسخة تحتوي ملفاً مكرراً أو يتجاوز الحجم المدعوم.");
        }
        ZipArchiveEntry Required(string name) => entries.TryGetValue(name, out var entry) ? entry
            : throw new InvalidDataException($"الملف المطلوب غير موجود في النسخة: {name}");
        T ReadJson<T>(string name)
        {
            using var input = Required(name).Open();
            return JsonSerializer.Deserialize<T>(input, Options) ?? throw new InvalidDataException($"الملف غير صالح: {name}");
        }
        List<T> ReadLines<T>(string name)
        {
            using var input = Required(name).Open();
            using var reader = new StreamReader(input, new UTF8Encoding(false, true), false, 65536);
            var result = new List<T>();
            while (reader.ReadLine() is { } line)
            {
                if ((result.Count & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                if (line.Length == 0 || line.Length > MaximumLineCharacters) throw new InvalidDataException($"سطر غير صالح في {name}");
                result.Add(JsonSerializer.Deserialize<T>(line, Options) ?? throw new InvalidDataException($"بيانات غير صالحة في {name}"));
            }
            return result;
        }
        var manifest = ReadJson<Manifest>("manifest.json");
        if (manifest.Version is < 1 or > 5 || manifest.Months is null || manifest.Employees < 0 || manifest.Deductions < 0 || manifest.Staff < 0)
            throw new InvalidDataException("إصدار النسخة أو فهرسها غير مدعوم.");
        if (manifest.Months.Length == 0 && manifest.Employees == 0 && manifest.Deductions == 0 && manifest.Staff == 0 && (manifest.Accounts ?? 0) == 0 && (manifest.Books ?? 0) == 0)
            throw new InvalidDataException("النسخة لا تحتوي بيانات محفوظة.");
        var months = ReadLines<MonthData>("months.jsonl");
        var employees = ReadLines<EmployeeData>("employees.jsonl");
        var deductions = ReadLines<DeductionData>("deductions.jsonl");
        int CountValidatedLines(string name)
        {
            using var input = Required(name).Open();
            using var reader = new StreamReader(input, new UTF8Encoding(false, true), false, 65536);
            var count = 0;
            while (reader.ReadLine() is { } line)
            {
                if ((count & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                if (line.Length == 0 || line.Length > MaximumLineCharacters) throw new InvalidDataException($"سطر غير صالح في {name}");
                using var value = JsonDocument.Parse(line);
                if (value.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"سجل غير صالح في {name}");
                checked { count++; }
            }
            return count;
        }
        var staffCount = CountValidatedLines("staff.jsonl");
        var accounts = manifest.Version >= 2 ? ReadLines<AccountData>("accounts.jsonl") : new List<AccountData>();
        if (months.Count != manifest.Months.Length || !months.Select(m => m.Name).Order(StringComparer.Ordinal).SequenceEqual(manifest.Months.Order(StringComparer.Ordinal)) ||
            employees.Count != manifest.Employees || deductions.Count != manifest.Deductions || staffCount != manifest.Staff ||
            (manifest.Version >= 2 && accounts.Count != manifest.Accounts))
            throw new InvalidDataException("أعداد السجلات لا تطابق فهرس النسخة؛ لم تُفتح البيانات.");
        if (months.Any(m => string.IsNullOrWhiteSpace(m.Name) || m.EmployeeCount < 0 || m.DeductionCount < 0) ||
            employees.Any(e => string.IsNullOrWhiteSpace(e.Month) || string.IsNullOrWhiteSpace(e.Name) || e.Net is > MaximumAmount or < -MaximumAmount) ||
            deductions.Any(d => string.IsNullOrWhiteSpace(d.Month) || string.IsNullOrWhiteSpace(d.Title) || string.IsNullOrWhiteSpace(d.Name) || d.Amount is > MaximumAmount or < -MaximumAmount) ||
            accounts.Any(a => string.IsNullOrWhiteSpace(a.Entity)))
            throw new InvalidDataException("تحتوي النسخة على سجل ناقص لا يمكن عرضه بأمان.");
        var monthNames = months.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        if (monthNames.Count != months.Count || employees.Any(e => !monthNames.Contains(e.Month)) || deductions.Any(d => !monthNames.Contains(d.Month)))
            throw new InvalidDataException("أسماء الأشهر في السجلات غير متطابقة مع فهرس النسخة.");
        // Read settings as a JSON object without attempting to reinterpret or overwrite iOS property-list values.
        var settings = ReadJson<JsonElement>("settings.json");
        if (settings.ValueKind != JsonValueKind.Object) throw new InvalidDataException("إعدادات النسخة غير صالحة.");
        var settingsPlists = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var setting in settings.EnumerateObject())
            {
                if (setting.Value.ValueKind != JsonValueKind.String || setting.Value.GetString() is not { } encoded ||
                    !settingsPlists.TryAdd(setting.Name, Convert.FromBase64String(encoded)))
                    throw new InvalidDataException("إعدادات النسخة غير صالحة أو مكررة.");
            }
        }
        catch (FormatException ex) { throw new InvalidDataException("تعذّر قراءة إعدادات النسخة.", ex); }
        var books = manifest.Version >= 3 ? ReadJson<List<BookData>>("books/index.json") : new List<BookData>();
        if (manifest.Version >= 3 && (manifest.Books is null || books.Count != manifest.Books || books.Select(b => b.Id).Distinct().Count() != books.Count))
            throw new InvalidDataException("فهرس الأرشيف لا يطابق النسخة.");
        var signature = new byte[5];
        foreach (var book in books)
        {
            if (book.Id == Guid.Empty || string.IsNullOrWhiteSpace(book.Title) || string.IsNullOrWhiteSpace(book.Month) || book.Pages is < 1 or > 3)
                throw new InvalidDataException("بيانات أحد الكتب المؤرشفة غير صالحة.");
            var entry = Required($"books/{book.Id.ToString().ToUpperInvariant()}.pdf");
            using var input = entry.Open();
            try { input.ReadExactly(signature); }
            catch (EndOfStreamException) { throw new InvalidDataException("أحد ملفات الأرشيف غير مكتمل."); }
            if (!signature.AsSpan().SequenceEqual("%PDF-"u8))
                throw new InvalidDataException("أحد ملفات الأرشيف ليس ملف PDF صالحاً.");
        }
        var monthIndex = months.ToDictionary(m => m.Name, StringComparer.Ordinal);
        return new BackupSnapshot(Path.GetFullPath(file), digest, manifest.Version,
            manifest.Months.Select(name => monthIndex[name]).Select(m => new PayrollMonth(m.Name, m.EmployeeCount, m.DeductionCount)).ToArray(),
            employees.Select(e => new Employee(e.Month, e.Name, e.Rank ?? "", e.Workplace ?? "", e.Net, e.Items, e.Details, e.RawColumns)).ToArray(),
            deductions.Select(d => new Deduction(d.Month, d.Name, d.Title, d.Amount, d.Note ?? "", d.Rank ?? "")).ToArray(),
            staffCount,
            accounts.Select(a => new ExecutionAccount(a.Entity, a.Iban ?? "", a.Branch ?? "", a.Swift ?? "", a.LocalAccount)).ToArray(),
            books.Select(b => new ArchiveBook(b.Id, b.Title, b.Month, b.Pages, b.Entity)).ToArray(), settingsPlists);
    }
}
