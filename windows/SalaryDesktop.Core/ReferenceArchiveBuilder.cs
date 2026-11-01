using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

/// يجمع ملفات المراجع المختارة محلياً ويفحصها قبل تخزينها في النسخة.
public static class ReferenceArchiveBuilder
{
    private sealed record Spec(string Id, string Name, int Pages);
    private static readonly Spec[] Required = [
        new("rafidain_group", "مجموعة مصارف الرافدين", 2), new("other_banks_group", "مجموعة مصارف الرشيد", 2),
        new("food_company", "شركة عراق الجوال", 1), new("food_tax", "امانات ضريبة", 1),
        new("tax_social", "خزينة الانبار حماية اجتماعية وضريبة", 1), new("contracts", "الضمان الاجتماعي", 1),
        new("cash_salary", "امانات رواتب", 1), new("ramadi", "الرمادي", 1), new("housing", "صندوق الاسكان فرع الانبار", 1),
        new("water", "الموارد المائية", 1), new("rafidain_anbar", "مصرف الرافدين فرع الانبار", 1)
    ];
    private static readonly Spec Digits = new("month_digits", "تنسيق ارقام عربية", 1);
    public static byte[] Create(string directory)
    {
        if (!Directory.Exists(directory)) throw new InvalidDataException("مجلد مراجع الكتب غير موجود.");
        var files = Directory.EnumerateFiles(directory).Take(5001).ToArray(); if (files.Length > 5000) throw new InvalidDataException("عدد ملفات المجلد كبير جداً.");
        string? Find(Spec spec, bool required)
        {
            var names = new[] { PayrollAnalysis.Normalize(spec.Name), PayrollAnalysis.Normalize(spec.Id) };
            var matches = files.Where(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase) && names.Contains(PayrollAnalysis.Normalize(Path.GetFileNameWithoutExtension(f)), StringComparer.Ordinal)).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("توجد نسخ متعددة للمرجع: " + spec.Name);
            if (matches.Length == 0 && required) throw new InvalidDataException("مرجع PDF مفقود: " + spec.Name);
            return matches.SingleOrDefault();
        }
        var entries = new List<(Spec Spec, byte[] Data)>(); long total = 0;
        void Add(Spec spec, bool required)
        {
            var path = Find(spec, required); if (path is null) return;
            var info = new FileInfo(path); if (info.Length is <= 0 or > 10_000_000 || (total += info.Length) > 40_000_000) throw new InvalidDataException("حجم أحد المراجع أو مجموعها غير مدعوم.");
            var data = File.ReadAllBytes(path); if (data.LongLength != info.Length) throw new InvalidDataException("تغيّر أحد المراجع أثناء قراءته."); entries.Add((spec, data));
        }
        foreach (var spec in Required) Add(spec, true); Add(Digits, false);
        var manifest = new { version = entries.Count == Required.Length + 1 ? 2 : 1, templates = entries.Select(e => new { id = e.Spec.Id, path = "templates/" + e.Spec.Id + ".pdf", sha256 = Convert.ToHexString(SHA256.HashData(e.Data)).ToLowerInvariant(), pages = e.Spec.Pages }).ToArray() };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var index = zip.CreateEntry("manifest.json", CompressionLevel.Optimal); using (var stream = index.Open()) JsonSerializer.Serialize(stream, manifest);
            foreach (var entry in entries) { var file = zip.CreateEntry("templates/" + entry.Spec.Id + ".pdf", CompressionLevel.Optimal); using var stream = file.Open(); stream.Write(entry.Data); }
        }
        var bytes = output.ToArray(); _ = LetterReferencePack.ReadArchive(bytes); return bytes;
    }
}
