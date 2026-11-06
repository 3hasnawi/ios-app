using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

public sealed partial class LocalPayrollStore
{
    private static readonly JsonSerializerOptions ArchiveInteropJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 64 };
    public static string ArchiveAttachmentKey(Guid id) => $"books/{id.ToString().ToUpperInvariant()}.pdf";
    private static string ArchiveText(string? value, string field, bool required)
    {
        var text = value?.Trim() ?? "";
        if (required && text.Length == 0 || text.Length > 200 || text.Any(char.IsControl)) throw new InvalidDataException($"{field} فارغ أو طويل أو يتضمن محارف غير صالحة.");
        return text;
    }
    private static void CheckArchiveTitle(string title)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(title, @"^(?:scan|img|image|document)[ _-]*\d*(?:\.pdf)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("اكتب اسماً واضحاً للكتاب بدلاً من اسم الماسح أو الكاميرا.");
    }
    public MutationResult AddArchiveBook(string title, string month, string? entity, byte[] pdf, string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var owned = pdf.ToArray(); int pages = ArchivePdf.Pages(owned);
        var book = new ArchiveBook(Guid.NewGuid(), ArchiveText(title, "اسم الكتاب", true), ArchiveText(month, "الشهر", true), pages, ArchiveText(entity, "الجهة", false), (DateTimeOffset.UtcNow - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds);
        CheckArchiveTitle(book.Title);
        return MutateArchive(null, book, owned, expectedRevision);
    }
    public MutationResult EditArchiveBook(Guid id, string title, string month, string? entity, string expectedRevision) =>
        MutateArchive(id, new(id, ArchiveText(title, "اسم الكتاب", true), ArchiveText(month, "الشهر", true), 1, ArchiveText(entity, "الجهة", false)), null, expectedRevision);
    public MutationResult ReplaceArchivePdf(Guid id, byte[] pdf, string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(pdf); var owned = pdf.ToArray(); _ = ArchivePdf.Pages(owned);
        return MutateArchive(id, null, owned, expectedRevision);
    }
    public MutationResult DeleteArchiveBook(Guid id, string expectedRevision) => MutateArchive(id, null, null, expectedRevision);
    private MutationResult MutateArchive(Guid? id, ArchiveBook? requested, byte[]? pdf, string expected)
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); CheckCurrent(c, t, expected);
        var state = CurrentAuxiliaryState(c, t); var books = ReadList<ArchiveBook>(c, t, state, "books").ToList();
        var attachments = ReadSet(c, t, state.GetValueOrDefault("attachments"));
        ArchiveBook? old = null; ArchiveBook? next = requested;
        bool deleting = id is not null && requested is null && pdf is null;
        if (id is { } target)
        {
            old = books.SingleOrDefault(b => b.Id == target) ?? throw new InvalidDataException("الكتاب غير موجود؛ أعد فتح الأرشيف.");
            if (deleting)
            {
                var aux = ReadAuxiliarySnapshot(c, t);
                var source = new BackupSnapshot("", expected, aux.Version, [], [], [], aux.StaffCount, aux.Accounts, aux.Books, aux.Settings);
                if (SalaryTrust.ReadFromBackup(source).Entries.Any(e => (e.BookIds ?? []).Any(link => Guid.TryParse(link, out var parsed) && parsed == target)))
                    throw new InvalidDataException("لا يمكن حذف كتاب مرتبط بقيد أمانات. أزل الربط صراحةً أولاً.");
                books.Remove(old); next = null;
            }
            else
            {
                next = pdf is not null ? old with { Pages = ArchivePdf.Pages(pdf) } : old with { Title = requested!.Title, Month = requested.Month, Entity = requested.Entity };
                if (next.Title != old.Title) CheckArchiveTitle(next.Title);
                books[books.IndexOf(old)] = next;
            }
        }
        else { if (next is null || pdf is null || books.Count >= 50_000) throw Invalid(); books.Add(next); }
        var key = ArchiveAttachmentKey((next ?? old)!.Id);
        var priorKey = attachments.Keys.SingleOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (deleting) { if (priorKey is not null) attachments.Remove(priorKey); }
        else if (pdf is not null) { if (priorKey is not null) attachments.Remove(priorKey); attachments[key] = SaveValue(c, t, pdf); }
        // إعادة ربط الحقول الإضافية بالهوية نفسها؛ لا تُسقط معلومات نسخة الآيفون عند تعديل العنوان.
        const string metadataKey = "interop/metadata.json";
        if (old is not null && old != next && attachments.TryGetValue(metadataKey, out var metadataId))
        {
            try
            {
                var metadata = JsonNode.Parse(ReadValue(c, t, metadataId))?.AsObject() ?? throw Invalid();
                if (metadata["extraFields"]?["books/index.json"] is JsonObject extras)
                {
                    string oldHash = Hash(JsonSerializer.SerializeToUtf8Bytes(old, ArchiveInteropJson));
                    if (extras.Remove(oldHash, out var fields))
                    {
                        if (next is not null) extras.Add(Hash(JsonSerializer.SerializeToUtf8Bytes(next, ArchiveInteropJson)), fields);
                        attachments[metadataKey] = SaveValue(c, t, JsonSerializer.SerializeToUtf8Bytes(metadata, ArchiveInteropJson));
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            { throw new InvalidDataException("تعذّر حفظ الحقول الإضافية للكتاب؛ لم تُغيّر البيانات.", ex); }
        }
        SaveList(c, t, state, "books", books); SetArea(c, t, state, "attachments", attachments);
        var result = SaveAuxiliaryRevision(c, t, state, deleting ? "حذف كتاب من الأرشيف الحالي" : "حفظ كتاب في الأرشيف", Hash(Encode(state)), expected);
        if (result.Changed) t.Commit(); return result;
    }
}
