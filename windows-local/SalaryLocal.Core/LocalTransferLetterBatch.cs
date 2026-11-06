using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

public sealed record LocalLetterRequest(string Id, string? EntryId = null);
public sealed class LocalLetterBatchPreview
{
    internal LocalLetterBatchPreview(IReadOnlyList<LocalLetterPreview> letters) => Letters = Array.AsReadOnly(letters.ToArray());
    public IReadOnlyList<LocalLetterPreview> Letters { get; }
    public string SourceRevision => Letters[0].SourceRevision;
    public string Month => Letters[0].Month;
    public DateOnly Date => Letters[0].Date;
}
public sealed class LocalLetterBatchPackage
{
    internal LocalLetterBatchPackage(LocalLetterBatchPreview preview, IReadOnlyList<LocalLetterPackage> packages)
    {
        if (packages.Count != preview.Letters.Count || packages.Where((p, i) => !ReferenceEquals(p.Preview, preview.Letters[i])).Any())
            throw new InvalidDataException("ملفات الدفعة لا تطابق مراجعتها.");
        Preview = preview; Packages = Array.AsReadOnly(packages.ToArray());
    }
    public LocalLetterBatchPreview Preview { get; }
    public IReadOnlyList<LocalLetterPackage> Packages { get; }
    internal byte[] Zip()
    {
        const long limit = 240L * 1024 * 1024;
        long total = 0; var files = new List<(string Name, byte[] Bytes)>();
        for (int i = 0; i < Packages.Count; i++)
        {
            foreach (var name in Packages[i].FileNames)
            {
                var bytes = Packages[i].ReadFile(name); total += bytes.Length;
                if (total > limit) throw new InvalidDataException("حجم الدفعة يتجاوز الحد الآمن؛ قسّم الاختيار إلى دفعات أصغر.");
                files.Add(($"{i + 1:00}-{Packages[i].Preview.Id}/{name}", bytes));
            }
        }
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in files) { using var stream = zip.CreateEntry(file.Name, CompressionLevel.Optimal).Open(); stream.Write(file.Bytes); }
            using var index = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(index, new { version = 1, kind = "local-reviewed-draft-batch", sourceRevision = Preview.SourceRevision,
                month = Preview.Month, csvDate = Preview.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                books = Preview.Letters.Select(p => new { id = p.Id, title = p.Title, total = p.Total, names = p.Names?.Rows.Count ?? 0 }).ToArray(),
                files = files.Select(f => new { name = f.Name, bytes = f.Bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(f.Bytes)) }).ToArray() });
        }
        var result = output.ToArray();
        if (result.LongLength > limit) throw new InvalidDataException("حجم الحزمة المضغوطة يتجاوز الحد الآمن؛ لم تُحفظ.");
        return result;
    }
}
public static partial class LocalTransferLetters
{
    public static LocalLetterBatchPreview PrepareBatch(BackupSnapshot source, string month, IReadOnlyList<LocalLetterRequest> requests, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var chosen = requests.ToArray();
        if (chosen.Length is < 1 or > 11 || chosen.Any(r => r is null) || chosen.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != chosen.Length)
            throw new InvalidDataException("اختر من كتاب واحد إلى أحد عشر كتاباً دون تكرار القالب.");
        if (chosen.Any(r => !LocalTransferLetterSettings.IsEntity(r.Id) && r.EntryId is not null))
            throw new InvalidDataException("لا يقبل كتاب المجموعة أو الرواتب جهة استقطاع منفردة.");
        var entityIds = chosen.Where(r => LocalTransferLetterSettings.IsEntity(r.Id)).Select(r => r.EntryId).ToArray();
        if (entityIds.Any(string.IsNullOrWhiteSpace) || entityIds.Distinct(StringComparer.Ordinal).Count() != entityIds.Length)
            throw new InvalidDataException("اختر جهة مستقلة لكل كتاب منفرد؛ لا تستخدم الاستقطاع نفسه في كتابين داخل الدفعة.");
        source = Freeze(source);
        var plans = new List<LocalLetterPreview>(); var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in chosen)
        {
            LocalLetterPreview plan;
            try { plan = Prepare(source, month, request.Id, request.EntryId, date); }
            catch (InvalidDataException ex) { throw new InvalidDataException(LocalTransferLetterSettings.Title(request.Id) + ": " + ex.Message, ex); }
            var identities = plan.Issuance is { } group ? group.Lines.Select(l => l.SourceIdentity)
                : plan.Entity is { } entity ? new[] { "deduction|" + entity.Entity } : new[] { "payroll|" + plan.Id };
            foreach (var identity in identities) if (!covered.Add(identity)) throw new InvalidDataException("مصدر مالي واحد ظهر في أكثر من كتاب؛ راجع اختيار الدفعة.");
            plans.Add(plan);
        }
        return new(plans);
    }
    public static LocalLetterBatchPackage GenerateBatch(LocalLetterBatchPreview preview, byte[] sakkalBold, byte[] arial, byte[] arialBold, byte[] logo, CancellationToken cancellation = default)
    {
        var packages = new List<LocalLetterPackage>();
        foreach (var letter in preview.Letters)
        {
            cancellation.ThrowIfCancellationRequested();
            packages.Add(Generate(letter, sakkalBold, arial, arialBold, logo));
        }
        cancellation.ThrowIfCancellationRequested();
        return new(preview, packages);
    }
}
