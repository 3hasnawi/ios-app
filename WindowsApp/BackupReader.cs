using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SalaryWindows;

internal sealed record Manifest(int Version, string[] Months, int Employees, int Deductions, int Staff, int? Accounts, int? Books, double CreatedAt = 0);
internal sealed record Employee(string Month, string Name, string Rank, string Workplace, double? Net, string? Items, string? Details);
internal sealed record Deduction(string Month, string Name, string Title, double? Amount, string? Note, string? Rank);
internal sealed record Staff(string Name, string Mother, string Stat, string Unified, string Rank, string Status, string Workplace, string? Details);
internal sealed record Account(string Entity, string Iban, string Branch, string Swift);
internal sealed record Book(Guid Id, string Title, string Month, string? Entity, int Pages);
internal sealed record BackupData(Manifest Manifest, List<Employee> Employees, List<Deduction> Deductions, List<Staff> Staff, List<Account> Accounts, List<Book> Books, Dictionary<string, string> Settings, string ArchivePath);

internal static class BackupReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private const long MaxEntry = 1024L * 1024 * 1024;

    public static BackupData Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.GroupBy(x => x.FullName, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new InvalidDataException("تحتوي النسخة ملفات مكررة.");
        using var manifestStream = Entry(zip, "manifest.json", 1_000_000).Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestStream, Options) ?? throw new InvalidDataException("بيانات النسخة غير صالحة.");
        if (manifest.Version < 1 || manifest.Version > 4 || manifest.Months == null) throw new InvalidDataException("إصدار النسخة غير مدعوم.");
        using var settingsStream = Entry(zip, "settings.json", 20_000_000).Open();
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(settingsStream, Options) ?? throw new InvalidDataException("إعدادات النسخة غير صالحة.");
        var months = Lines<JsonElement>(zip, "months.jsonl");
        var employees = Lines<Employee>(zip, "employees.jsonl");
        var deductions = Lines<Deduction>(zip, "deductions.jsonl");
        var staff = Lines<Staff>(zip, "staff.jsonl");
        var accounts = manifest.Version >= 2 ? Lines<Account>(zip, "accounts.jsonl") : [];
        var books = new List<Book>();
        if (manifest.Version >= 3)
        {
            using var stream = Entry(zip, "books/index.json", 20_000_000).Open();
            books = JsonSerializer.Deserialize<List<Book>>(stream, Options) ?? throw new InvalidDataException("فهرس الكتب غير صالح.");
        }
        if (employees.Count != manifest.Employees || deductions.Count != manifest.Deductions || staff.Count != manifest.Staff ||
            (manifest.Version >= 2 && accounts.Count != manifest.Accounts) || (manifest.Version >= 3 && books.Count != manifest.Books) ||
            months.Count != manifest.Months.Length)
            throw new InvalidDataException("أعداد البيانات لا تطابق فهرس النسخة؛ لم تُفتح.");
        if (employees.Count + deductions.Count + staff.Count + accounts.Count + books.Count == 0 && months.Count == 0)
            throw new InvalidDataException("النسخة لا تحتوي بيانات.");
        if (employees.Any(x => x.Name == null || x.Month == null) || deductions.Any(x => x.Name == null || x.Month == null || x.Title == null) ||
            staff.Any(x => x.Name == null) || books.Any(x => x.Id == Guid.Empty || x.Title == null || x.Month == null))
            throw new InvalidDataException("تحتوي النسخة سجلات غير مكتملة.");
        foreach (var book in books) _ = Entry(zip, $"books/{book.Id.ToString().ToUpperInvariant()}.pdf");
        return new(manifest, employees, deductions, staff, accounts, books, settings, path);
    }
    private static ZipArchiveEntry Entry(ZipArchive zip, string name, long max = MaxEntry)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"النسخة لا تحتوي {name}.");
        if (entry.Length > max) throw new InvalidDataException("حجم أحد ملفات النسخة أكبر من الحد المدعوم.");
        return entry;
    }
    private static List<T> Lines<T>(ZipArchive zip, string name)
    {
        var list = new List<T>();
        using var reader = new StreamReader(Entry(zip, name).Open(), Encoding.UTF8, true);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonSerializer.Deserialize<T>(line, Options);
            if (item is null) throw new InvalidDataException($"تعذرت قراءة {name}.");
            list.Add(item);
        }
        return list;
    }
    public static async Task<string> Download(Uri baseUri, string token)
    {
        if (baseUri.Scheme != "http" || !IPAddress.TryParse(baseUri.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !IsPrivate(ip) || !string.IsNullOrEmpty(baseUri.UserInfo) || baseUri.AbsolutePath != "/" || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new InvalidOperationException("أدخل عنوان HTTP محلياً، مثل http://192.168.1.10:8765");
        if (token.Length < 32 || token.Any(c => c > 127 || char.IsWhiteSpace(c) || char.IsControl(c))) throw new InvalidOperationException("رمز الوصول غير صالح.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/v1/backups/latest"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_147_483_648) throw new InvalidDataException("حجم النسخة كبير جداً.");
        var temp = Path.Combine(Path.GetTempPath(), "salary-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await using var source = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[81920]; long total = 0; int n;
            while ((n = await source.ReadAsync(buffer)) > 0) { total += n; if (total > 2_147_483_648) throw new InvalidDataException("حجم النسخة كبير جداً."); await target.WriteAsync(buffer.AsMemory(0, n)); }
            return temp;
        }
        catch { try { File.Delete(temp); } catch { } throw; }
    }
    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
