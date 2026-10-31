using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public static class ReconciliationBackupExport
{
    private static double IosDate(DateOnly date)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Baghdad", out var iraq) ? iraq :
            TimeZoneInfo.TryFindSystemTimeZoneById("Arab Standard Time", out var windows) ? windows : TimeZoneInfo.CreateCustomTimeZone("Iraq", TimeSpan.FromHours(3), "Iraq", "Iraq");
        var local = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return (utc - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }
    private static byte[] LedgerPlist(IReadOnlyList<BankPeriod> periods)
    {
        foreach (var period in periods) _ = BankReconciliation.Calculate(period);
        if (periods.Count > 5000 || periods.Select(p => p.Id).Distinct().Count() != periods.Count) throw new InvalidDataException("فترات المطابقة غير صالحة.");
        var body = new { version = 1, periods = periods.Select(p => new { id = p.Id, month = p.Month, start = IosDate(p.Start), end = IosDate(p.End), openingBalance = p.OpeningBalance,
            memberFirst = p.FirstMember, memberSecond = p.SecondMember, acceptedContractMissing = p.AcceptedContractMissing, acceptedContractAmount = p.AcceptedContractAmount,
            movements = p.Movements.Select(m => new { id = m.Id, date = IosDate(m.Date), description = m.Description, amount = m.Amount,
                direction = m.Direction == MovementDirection.Credit ? "credit" : "debit", sourceID = m.SourceId, sourceLineIDs = m.SourceLineIds,
                refundOf = m.RefundOf, isRefund = m.IsRefund, isFunding = m.IsFunding, matched = m.Matched }).ToArray() }).ToArray() };
        using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(body)), plist); return plist.ToArray();
    }
    public static void Write(BackupSnapshot source, IReadOnlyList<BankPeriod> periods, string destination)
    {
        var target = Path.GetFullPath(destination); var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("المجلد غير صالح.");
        var name = Path.GetFileNameWithoutExtension(target);
        if (!string.Equals(Path.GetExtension(target), ".zip", StringComparison.OrdinalIgnoreCase) || string.Equals(target, source.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(directory, Path.GetDirectoryName(source.SourcePath), StringComparison.OrdinalIgnoreCase) && name.Length == 64 && name.All(c => char.IsAsciiHexDigit(c))))
            throw new InvalidDataException("وجهة النسخة غير صالحة.");
        var settings = source.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        settings["bankReconciliations"] = LedgerPlist(periods);
        var settingsJson = JsonSerializer.SerializeToUtf8Bytes(settings);
        var temp = Path.Combine(directory, ".salary-ledger-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(input)) != source.SourceDigest) throw new InvalidDataException("تغيّرت نسخة المصدر؛ لم يُحفظ الملف.");
            input.Position = 0;
            using (var oldZip = new ZipArchive(input, ZipArchiveMode.Read, true))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var newZip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var oldEntry in oldZip.Entries)
                {
                    var newEntry = newZip.CreateEntry(oldEntry.FullName, CompressionLevel.Optimal);
                    newEntry.LastWriteTime = oldEntry.LastWriteTime; newEntry.ExternalAttributes = oldEntry.ExternalAttributes;
                    using var outStream = newEntry.Open();
                    if (oldEntry.FullName == "settings.json") outStream.Write(settingsJson);
                    else { using var inStream = oldEntry.Open(); inStream.CopyTo(outStream); }
                }
            }
            var restored = BackupReader.Read(temp);
            var readPeriods = BankReconciliation.ReadFromBackup(restored);
            if (!LedgerPlist(readPeriods).AsSpan().SequenceEqual(LedgerPlist(periods))) throw new InvalidDataException("تعذّر التحقق من دفتر المطابقة في النسخة الجديدة.");
            using (var oldZip = new ZipArchive(input, ZipArchiveMode.Read, true))
            using (var newZip = ZipFile.OpenRead(temp))
            {
                foreach (var oldEntry in oldZip.Entries.Where(e => e.FullName != "settings.json"))
                {
                    var copy = newZip.GetEntry(oldEntry.FullName) ?? throw new InvalidDataException("ملف مفقود في النسخة الجديدة.");
                    using var a = oldEntry.Open(); using var b = copy.Open();
                    if (oldEntry.Length != copy.Length || !SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b))) throw new InvalidDataException("تعذّر التحقق من محتوى النسخة الجديدة.");
                }
            }
            input.Position = 0;
            if (Convert.ToHexString(SHA256.HashData(input)) != source.SourceDigest) throw new InvalidDataException("تغيّرت نسخة المصدر أثناء التصدير.");
            File.Move(temp, target, true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
