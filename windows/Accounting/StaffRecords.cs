using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SalaryDesktop.Core;

public sealed record StaffMember(string Name, string Mother, string Stat, string Unified, string Rank, string Status, string Workplace, byte[]? Details);
public sealed record StaffField(string Label, string Value);
public sealed record StaffSection(string Title, IReadOnlyList<StaffField> Fields);

public static class StaffRecords
{
    private sealed record RawStaff(string? Name, string? Mother, string? Stat, string? Unified, string? Rank, string? Status, string? Workplace, byte[]? Details);
    private sealed record RawField(string? Label, string? Value);
    private sealed record RawSection(string? Title, RawField[]? Fields);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static IReadOnlyList<StaffMember> Read(BackupSnapshot source)
    {
        if (source.StaffCount is < 0 or > 500_000) throw new InvalidDataException("عدد سجلات الملاك غير مدعوم.");
        using var file = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(file)) != source.SourceDigest) throw new InvalidDataException("تغيّرت نسخة المصدر؛ أعد فتحها قبل عرض الملاك.");
        file.Position = 0; using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var found = zip.Entries.Where(e => e.FullName == "staff.jsonl").ToArray();
        if (found.Length != 1 || found[0].Length > 1_500_000_000) throw new InvalidDataException("ملف الملاك غير صالح.");
        using var stream = found[0].Open(); using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 65536); var result = new List<StaffMember>(source.StaffCount);
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Length is 0 or > 2_000_000 || result.Count >= source.StaffCount) throw new InvalidDataException("سجل الملاك غير صالح أو العدد غير مطابق.");
                var row = JsonSerializer.Deserialize<RawStaff>(line, Options) ?? throw new JsonException();
                if (string.IsNullOrWhiteSpace(row.Name)) throw new InvalidDataException("يوجد سجل ملاك بلا اسم.");
                result.Add(new(row.Name, row.Mother ?? "", row.Stat ?? "", row.Unified ?? "", row.Rank ?? "", row.Status ?? "", row.Workplace ?? "", row.Details));
            }
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or FormatException) { throw new InvalidDataException("تعذّر قراءة سجلات الملاك.", ex); }
        if (result.Count != source.StaffCount) throw new InvalidDataException("عدد سجلات الملاك لا يطابق النسخة.");
        reader.Dispose(); stream.Dispose(); file.Position = 0;
        if (Convert.ToHexString(SHA256.HashData(file)) != source.SourceDigest) throw new InvalidDataException("تغيّرت نسخة المصدر أثناء قراءة الملاك.");
        return result;
    }
    public static IReadOnlyList<StaffSection> Sections(StaffMember member)
    {
        if (member.Details is null || member.Details.Length == 0) return [];
        try
        {
            var sections = JsonSerializer.Deserialize<RawSection[]>(member.Details, Options) ?? throw new JsonException();
            if (sections.Length > 1000 || sections.Any(s => s is null || s.Fields is null || s.Fields.Length > 10_000 || s.Fields.Any(f => f is null))) throw new InvalidDataException("تفاصيل الملاك غير صالحة.");
            return sections.Select(s => new StaffSection(s.Title ?? "", s.Fields!.Select(f => new StaffField(f.Label ?? "", f.Value ?? "")).ToArray())).ToArray();
        }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة تفاصيل سجل الملاك.", ex); }
    }
}
