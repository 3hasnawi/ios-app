using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

/// حالة الأرشيف الفعلية للنسخة المفتوحة: الكتب بعد تعديلات ويندوز، وملفات PDF المحلية (المعرّف ← المسار).
public sealed record ArchiveState(IReadOnlyList<ArchiveBook> Books, IReadOnlyDictionary<Guid, string> Files, string? Revision);

/// تعديلات الأرشيف في ويندوز (إضافة كتاب من PDF أو صور أو ماسح، تعديل بياناته، استبدال ملفه، حذفه)
/// مرتبطة ببصمة النسخة مثل دفتر الأمانات؛ ملف النسخة الأصلي لا يتغير، وتدخل التعديلات في النسخة الاحتياطية التالية.
public sealed class LocalArchiveStore(string directory)
{
    private sealed record Saved(int Version, string SourceDigest, List<ArchiveBook> Books, Dictionary<Guid, string> Files);
    private readonly string root = Path.GetFullPath(directory);
    private static string Digest(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') ? value : throw new InvalidDataException("بصمة النسخة غير صالحة.");
    private string FileFor(string digest) => Path.Combine(root, Digest(digest) + ".json");
    private string FolderFor(string digest) => Path.Combine(root, Digest(digest));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool SafeName(string name) => name.Length == 69 && name.EndsWith(".pdf", StringComparison.Ordinal) && name[..65].All(c => char.IsAsciiHexDigitLower(c) || c == '-');
    public static double IosNow() => (DateTime.UtcNow - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    public static string CleanTitle(string? title)
    {
        var value = (title ?? "").Trim();
        if (value.Length == 0) throw new InvalidDataException("اكتب اسماً للكتاب.");
        if (value.Length > 300 || value.Any(char.IsControl)) throw new InvalidDataException("اسم الكتاب طويل جداً أو فيه رموز غير صالحة.");
        return value;
    }
    public static string? CleanEntity(string? entity)
    {
        var value = (entity ?? "").Trim();
        if (value.Length > 300 || value.Any(char.IsControl)) throw new InvalidDataException("اسم الجهة طويل جداً أو فيه رموز غير صالحة.");
        return value.Length == 0 ? null : value;
    }
    private static string CleanMonth(string? month)
    {
        var value = (month ?? "").Trim();
        if (value.Length == 0 || value.Length > 100 || value.Any(char.IsControl)) throw new InvalidDataException("اختر شهر الكتاب من الأشهر المحفوظة.");
        return value;
    }

    private ArchiveState Read(BackupSnapshot source, string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var saved = JsonSerializer.Deserialize<Saved>(bytes) ?? throw new JsonException();
            if (saved.Version != 1 || saved.SourceDigest != source.SourceDigest || saved.Books is null || saved.Files is null) throw new InvalidDataException("أرشيف الكتب المحلي غير صالح.");
            if (saved.Books.Count > 10_000 || saved.Books.Select(b => b.Id).Distinct().Count() != saved.Books.Count) throw new InvalidDataException("أرشيف الكتب المحلي فيه كتب مكررة.");
            var original = source.Books.ToDictionary(b => b.Id);
            var files = new Dictionary<Guid, string>();
            foreach (var book in saved.Books)
            {
                if (book.Id == Guid.Empty || book.Pages is < 1 or > ArchivePdf.MaxPages) throw new InvalidDataException("بيانات أحد الكتب المحلية غير صالحة.");
                _ = CleanTitle(book.Title); _ = CleanEntity(book.Entity); _ = CleanMonth(book.Month);
                if (saved.Files.TryGetValue(book.Id, out var name))
                {
                    if (!SafeName(name)) throw new InvalidDataException("اسم ملف كتاب محلي غير صالح.");
                    var file = Path.Combine(FolderFor(source.SourceDigest), name);
                    if (!File.Exists(file)) throw new InvalidDataException($"ملف الكتاب «{book.Title}» مفقود من مجلد الأرشيف المحلي.");
                    files[book.Id] = file;
                }
                else if (!original.TryGetValue(book.Id, out var old) || old.Pages != book.Pages) throw new InvalidDataException("أرشيف الكتب المحلي لا يطابق النسخة المفتوحة.");
            }
            if (saved.Files.Keys.Any(id => !saved.Books.Any(b => b.Id == id))) throw new InvalidDataException("أرشيف الكتب المحلي فيه ملفات غير مرتبطة.");
            return new(saved.Books, files, Hash(bytes));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة أرشيف الكتب المحلي؛ لم تتغير البيانات.", ex); }
    }

    public ArchiveState Load(BackupSnapshot source)
    {
        var path = FileFor(source.SourceDigest);
        return File.Exists(path) ? Read(source, path) : new(source.Books, new Dictionary<Guid, string>(), null);
    }

    /// النسخة بعد تطبيق تعديلات الأرشيف؛ تُستدعى على النسخة المفتوحة قبل طبقات الأشهر والروابط.
    public BackupSnapshot Apply(BackupSnapshot source)
    {
        var state = Load(source);
        return state.Revision is null ? source : source with { Books = state.Books, LocalBookFiles = state.Files };
    }

    public ArchiveState Add(BackupSnapshot source, ArchiveState expected, string title, string month, string? entity, byte[] pdf)
    {
        var pages = ArchivePdf.Pages(pdf);
        var book = new ArchiveBook(Guid.NewGuid(), CleanTitle(title), CleanMonth(month), pages, CleanEntity(entity), IosNow());
        return Commit(source, expected, books => { books.Insert(0, book); return book.Id; }, pdf);
    }
    public ArchiveState Edit(BackupSnapshot source, ArchiveState expected, Guid id, string title, string month, string? entity)
    {
        string cleanTitle = CleanTitle(title), cleanMonth = CleanMonth(month); var cleanEntity = CleanEntity(entity);
        return Commit(source, expected, books => { var i = Index(books, id); books[i] = books[i] with { Title = cleanTitle, Month = cleanMonth, Entity = cleanEntity }; return null; }, null);
    }
    public ArchiveState Replace(BackupSnapshot source, ArchiveState expected, Guid id, byte[] pdf)
    {
        var pages = ArchivePdf.Pages(pdf);
        return Commit(source, expected, books => { var i = Index(books, id); books[i] = books[i] with { Pages = pages }; return id; }, pdf);
    }
    /// الحذف لا يُسمح إن كان الكتاب مرتبطاً بقيود أمانات (مثل الآيفون)؛ يتحقق المستدعي من ذلك قبل الحذف.
    public ArchiveState Delete(BackupSnapshot source, ArchiveState expected, Guid id) =>
        Commit(source, expected, books => { books.RemoveAt(Index(books, id)); return null; }, null);

    private static int Index(List<ArchiveBook> books, Guid id)
    {
        var index = books.FindIndex(b => b.Id == id);
        return index >= 0 ? index : throw new InvalidDataException("الكتاب غير موجود؛ أعد فتح الأرشيف.");
    }

    private ArchiveState Commit(BackupSnapshot source, ArchiveState expected, Func<List<ArchiveBook>, Guid?> change, byte[]? pdf)
    {
        if (source.Version < 3) throw new InvalidDataException("إصدار النسخة لا يدعم الأرشيف؛ افتح نسخة أحدث من الآيفون.");
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".archive.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّر الأرشيف في نافذة أخرى؛ أعد فتحه.");
        var books = current.Books.ToList();
        var names = current.Files.ToDictionary(p => p.Key, p => Path.GetFileName(p.Value));
        var target = change(books);
        var folder = FolderFor(source.SourceDigest);
        string? created = null;
        try
        {
            if (pdf is not null && target is { } id)
            {
                Directory.CreateDirectory(folder);
                var name = $"{id:N}-{Guid.NewGuid():N}.pdf";
                created = Path.Combine(folder, name);
                using (var output = new FileStream(created, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough)) { output.Write(pdf); output.Flush(true); }
                if (!Hash(File.ReadAllBytes(created)).Equals(Hash(pdf), StringComparison.Ordinal)) throw new InvalidDataException("تعذّر التحقق من ملف الكتاب بعد حفظه.");
                names[id] = name;
            }
            foreach (var removed in names.Keys.Where(k => !books.Any(b => b.Id == k)).ToArray()) names.Remove(removed);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(1, source.SourceDigest, books, names));
            var path = FileFor(source.SourceDigest);
            var temporary = Path.Combine(root, ".archive-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes); output.Flush(true); }
                var verified = Read(source, temporary);
                if (verified.Revision != Hash(bytes)) throw new InvalidDataException("تعذّر التحقق من أرشيف الكتب.");
                File.Move(temporary, path, true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            created = null;
        }
        finally { if (created is not null) { try { File.Delete(created); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        // ملفات الكتب المستبدلة أو المحذوفة لم تعد مرتبطة؛ تُنظف دون أن يمنع فشل التنظيف الحفظ.
        try
        {
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, "*.pdf").Where(f => !names.Values.Contains(Path.GetFileName(f), StringComparer.Ordinal)))
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Load(source);
    }
}
