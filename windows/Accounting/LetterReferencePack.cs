using System.IO.Compression;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Claunia.PropertyList;
using UglyToad.PdfPig;

namespace SalaryDesktop.Core;

/// ملفات مراجع المالك تبقى داخل نسخته؛ لا تُخمّن الحسابات عند تعذّر قراءتها.
public static class LetterReferencePack
{
    private static readonly IReadOnlyDictionary<string, int> Expected = new Dictionary<string, int>(StringComparer.Ordinal) {
        ["rafidain_group"] = 2, ["other_banks_group"] = 2, ["cash_salary"] = 1, ["food_company"] = 1, ["food_tax"] = 1,
        ["tax_social"] = 1, ["contracts"] = 1, ["water"] = 1, ["housing"] = 1, ["rafidain_anbar"] = 1, ["ramadi"] = 1
    };
    private sealed record Template(string Id, string Path, string Sha256, int Pages);
    private sealed record Manifest(int Version, Template[] Templates);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static IReadOnlyDictionary<string, byte[]> Read(BackupSnapshot snapshot)
    {
        if (!snapshot.SettingsPlists.TryGetValue("transferReferenceArchiveData", out var plist)) throw new InvalidDataException("مراجع الكتب غير موجودة في النسخة؛ استورد مراجع الكتب في الآيفون ثم احفظ نسخة جديدة.");
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data || data.Bytes.Length is 0 or > 10_000_000) throw new InvalidDataException("حزمة مراجع الكتب غير صالحة.");
            return ReadArchive(data.Bytes);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة مراجع الكتب؛ لم تُغيّر البيانات.", ex); }
    }
    public static IReadOnlyDictionary<string, byte[]> ReadArchive(byte[] bytes)
    {
        if (bytes.Length is 0 or > 10_000_000) throw new InvalidDataException("حجم مراجع الكتب غير صالح.");
        using var memory = new MemoryStream(bytes, false); using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal); long size = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == "templates/" && entry.Length == 0) continue;
            if (!entries.TryAdd(entry.FullName, entry) || entry.Length > 10_000_000 || (size += entry.Length) > 40_000_000) throw new InvalidDataException("حزمة مراجع الكتب مكررة أو كبيرة جداً.");
        }
        if (!entries.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.Length > 100_000) throw new InvalidDataException("فهرس مراجع الكتب غير صالح.");
        Manifest manifest; using (var stream = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<Manifest>(stream, JsonOptions) ?? throw new InvalidDataException("فهرس المراجع غير صالح.");
        if (manifest.Version is not (1 or 2) || manifest.Templates is null || manifest.Templates.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Path))) throw new InvalidDataException("إصدار مراجع الكتب أو فهرسها غير صالح.");
        var expected = Expected.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (manifest.Version == 2) expected["month_digits"] = 1;
        if (manifest.Templates.Length != expected.Count || manifest.Templates.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != expected.Count || entries.Count != expected.Count + 1)
            throw new InvalidDataException("حزمة مراجع الكتب ناقصة أو مكررة.");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var template in manifest.Templates)
        {
            if (!expected.TryGetValue(template.Id, out var pages) || template.Pages != pages || template.Path != "templates/" + template.Id + ".pdf" ||
                !entries.TryGetValue(template.Path, out var entry) || template.Sha256 is null || template.Sha256.Length != 64)
                throw new InvalidDataException("فهرس أحد مراجع الكتب غير صالح.");
            using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); var content = output.ToArray();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), template.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("بصمة أحد مراجع الكتب غير مطابقة.");
            try
            {
                using var pdf = PdfDocument.Open(content);
                if (pdf.NumberOfPages != pages) throw new InvalidDataException("عدد صفحات أحد مراجع الكتب غير مطابق.");
                for (int i = 1; i <= pages; i++) { var page = pdf.GetPage(i); var w = template.Id == "month_digits" ? 612 : 595.3; var h = template.Id == "month_digits" ? 792 : 841.9; if (Math.Abs(page.Width - w) >= 2 || Math.Abs(page.Height - h) >= 2) throw new InvalidDataException("حجم أحد مراجع الكتب غير مطابق."); }
                if (template.Id == "month_digits" && !pdf.GetPage(1).Text.Contains("0123456789", StringComparison.Ordinal)) throw new InvalidDataException("مرجع أرقام الأشهر غير صالح.");
            }
            catch (Exception ex) when (ex is not InvalidDataException) { throw new InvalidDataException("تعذّر قراءة أحد مراجع PDF.", ex); }
            result.Add(template.Id, content);
        }
        return result;
    }
    private static string Select(UglyToad.PdfPig.Content.Page page, double x, double top, double width, double height)
    {
        var value = string.Concat(page.Letters.Where(l => { var r = l.GlyphRectangle; var cx = (r.Left + r.Right) / 2; var cy = (r.Bottom + r.Top) / 2;
            return cx >= x - .5 && cx <= x + width + .5 && cy >= page.Height - top - height - .5 && cy <= page.Height - top + .5; })
            .OrderBy(l => l.GlyphRectangle.Left).Select(l => l.Value));
        return new string(TransferPreparation.NormalizeAccount(value).Where(char.IsAsciiLetterOrDigit).ToArray());
    }
    public static string? IndividualAccount(IReadOnlyDictionary<string, byte[]> references, string id, string payerIban)
    {
        var payer = TransferPreparation.NormalizeAccount(payerIban);
        if (!TransferPreparation.ValidIban(payer) || !references.TryGetValue(id, out var data)) throw new InvalidDataException("حساب الدفع أو مرجع الكتاب غير صالح.");
        (double X, double Y, double W, double H) payerRect;
        (double X, double Y, double W, double H)? beneficiaryRect;
        switch (id)
        {
            case "housing": payerRect = (211.37, 299.06, 170.586, 15.543); beneficiaryRect = (140.42, 315.14, 105.20, 15.54); break;
            case "water": payerRect = (385.99, 333.62, 170.586, 15.543); beneficiaryRect = (40.20, 349.70, 104.91, 15.54); break;
            case "ramadi": payerRect = (385.99, 349.70, 170.586, 15.543); beneficiaryRect = (334.39, 365.78, 170.66, 15.54); break;
            case "rafidain_anbar": payerRect = (161.06, 299.06, 170.6, 15.543); beneficiaryRect = null; break;
            case "rafidain_group": case "other_banks_group": payerRect = (385.99, 337.58, 170.685, 15.543); beneficiaryRect = null; break;
            case "food_company": payerRect = (384, 350, 175, 19); beneficiaryRect = null; break;
            case "food_tax": case "cash_salary": payerRect = (385.99, 349.7, 170.586, 15.543); beneficiaryRect = null; break;
            case "contracts": payerRect = (126.26, 333.62, 170.586, 15.543); beneficiaryRect = null; break;
            case "tax_social": payerRect = (97.584, 333.62, 170.6, 15.543); beneficiaryRect = null; break;
            default: throw new InvalidDataException("نوع مرجع الكتاب غير معروف.");
        }
        using var pdf = PdfDocument.Open(data); var page = pdf.GetPage(1);
        if (Select(page, payerRect.X, payerRect.Y, payerRect.W, payerRect.H) != payer) throw new InvalidDataException("حساب الدفع لا يطابق مرجع الكتاب.");
        if (id is "food_company" or "food_tax" or "cash_salary" or "tax_social" or "contracts")
        {
            var text = string.Concat(page.Text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).Select(c => c switch { >= '٠' and <= '٩' => (char)('0' + c - '٠'), >= '۰' and <= '۹' => (char)('0' + c - '۰'), _ => c }));
            if (text.Length > 2_000_000) throw new InvalidDataException("نص مرجع الكتاب كبير جداً.");
            var values = Regex.Matches(text, @"(?i)(?<![A-Z0-9])IQ(?:[\s-]*[A-Z0-9]){21}(?![A-Z0-9])").Select(m => TransferPreparation.NormalizeAccount(m.Value)).ToArray();
            if (values.Length != 2 || values.Distinct(StringComparer.Ordinal).Count() != 2 || values.Any(v => !TransferPreparation.ValidIban(v)) || !values.Contains(payer)) throw new InvalidDataException("تعذّر تحديد حساب المستفيد في مرجع الكتاب بثقة.");
            return values.Single(v => v != payer);
        }
        if (beneficiaryRect is not { } r) return null;
        var beneficiary = Select(page, r.X, r.Y, r.W, r.H);
        if (beneficiary.Length == 0) throw new InvalidDataException("تعذّر قراءة حساب المستفيد من مرجع الكتاب.");
        if (id is "housing" or "water" && (beneficiary.Length != 15 || beneficiary.Any(c => !char.IsAsciiDigit(c)))) throw new InvalidDataException("الحساب المحلي في مرجع الكتاب غير صالح.");
        if (id == "ramadi" && !TransferPreparation.ValidIban(beneficiary)) throw new InvalidDataException("IBAN في مرجع الكتاب غير صالح.");
        return beneficiary;
    }
}
