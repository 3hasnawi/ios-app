using System.IO.Compression;
using System.Text.Json;

namespace Payroll.Core;

public sealed record ArchivedBook(Guid Id, string Title, string Month, string Entity, int Pages);

/// <summary>Reads archived books directly from the preserved iPhone backup, without modifying it.</summary>
public static class ArchivedBooks
{
    public static IReadOnlyList<ArchivedBook> List(string datasetDirectory)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(datasetDirectory, "original.zip"));
        var manifestEntry = Required(zip, "manifest.json");
        if (manifestEntry.Length > 1_000_000) throw new InvalidDataException("فهرس النسخة أكبر من الحد المدعوم.");
        using var manifestStream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var version = manifest.RootElement.GetProperty("version").GetInt32();
        if (version < 3) return [];
        var entry = Required(zip, "books/index.json");
        if (entry.Length > 10_000_000) throw new InvalidDataException("فهرس الكتب أكبر من الحد المدعوم.");
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("فهرس الكتب غير صالح.");
        if (document.RootElement.GetArrayLength() > 10_000) throw new InvalidDataException("عدد الكتب يتجاوز الحد المدعوم.");
        var books = new List<ArchivedBook>();
        var ids = new HashSet<Guid>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!Guid.TryParse(item.GetProperty("id").GetString(), out var id) || !ids.Add(id)) throw new InvalidDataException("معرّف الكتاب غير صالح أو مكرر.");
            var title = item.GetProperty("title").GetString();
            var month = item.GetProperty("month").GetString();
            var pages = item.GetProperty("pages").GetInt32();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(month) || pages is < 1 or > 3) throw new InvalidDataException("بيانات أحد الكتب غير صالحة.");
            var entity = item.TryGetProperty("entity", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            books.Add(new ArchivedBook(id, title, month, entity, pages));
        }
        var expected = manifest.RootElement.GetProperty("books").GetInt32();
        if (books.Count != expected) throw new InvalidDataException("عدد الكتب لا يطابق فهرس النسخة.");
        return books;
    }

    /// Writes only the requested PDF to the supplied stream, never to a path from the archive.
    public static async Task CopyPdfAsync(string datasetDirectory, Guid bookId, Stream destination, CancellationToken cancellation = default)
    {
        if (!List(datasetDirectory).Any(book => book.Id == bookId)) throw new InvalidDataException("الكتاب غير موجود في الفهرس.");
        using var zip = ZipFile.OpenRead(Path.Combine(datasetDirectory, "original.zip"));
        var entry = Required(zip, "books/" + bookId.ToString().ToUpperInvariant() + ".pdf");
        if (entry.Length is < 6 or >= 60_000_000) throw new InvalidDataException("حجم الكتاب غير صالح.");
        await using var input = entry.Open();
        byte[] header = new byte[5];
        await input.ReadExactlyAsync(header, cancellation);
        if (!header.AsSpan().SequenceEqual("%PDF-"u8)) throw new InvalidDataException("ملف الكتاب ليس PDF صالحاً.");
        await destination.WriteAsync(header, cancellation);
        await input.CopyToAsync(destination, cancellation);
    }

    private static ZipArchiveEntry Required(ZipArchive zip, string name)
    {
        var matches = zip.Entries.Where(x => string.Equals(x.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].FullName != name) throw new InvalidDataException("ملف مفقود أو مكرر في نسخة الآيفون: " + name);
        return matches[0];
    }
}
