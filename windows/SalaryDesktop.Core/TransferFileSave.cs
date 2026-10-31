using System.Security.Cryptography;

namespace SalaryDesktop.Core;

/// يحضّر الملف ويفحصه بجانب الوجهة قبل الاستبدال؛ لا يغيّر ملف النسخة المصدر.
public static class TransferFileSave
{
    public static void Write(BackupSnapshot source, TransferSnapshot transfer, TransferGroup group, DateOnly date, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(target), ".csv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, source.SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("وجهة ملف التحويل غير صالحة.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد التصدير غير صالح.");
        using var original = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(original)), source.SourceDigest, StringComparison.Ordinal))
            throw new InvalidDataException("تغيّرت النسخة المصدر؛ افتحها مجدداً قبل التصدير.");
        var bytes = TransferPreparation.MakeCsv(transfer, group, date);
        var temporary = Path.Combine(directory, ".salary-transfer-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                created = true; output.Write(bytes); output.Flush(true);
            }
            var written = File.ReadAllBytes(temporary);
            if (!written.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("تعذّر التحقق من الملف المكتوب.");
            TransferPreparation.VerifyCsv(written, transfer, group, date);
            original.Position = 0;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(original)), source.SourceDigest, StringComparison.Ordinal))
                throw new InvalidDataException("تغيّرت النسخة المصدر أثناء تجهيز الملف؛ لم يُستبدل الملف السابق.");
            File.Move(temporary, target, true);
            created = false;
        }
        finally { if (created) { try { File.Delete(temporary); } catch (IOException) { } } }
    }
}
