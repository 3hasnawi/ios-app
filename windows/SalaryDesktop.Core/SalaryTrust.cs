using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

// صيغة JSON نفسها في الآيفون داخل قيمة بيانات plist في settings.json.
public sealed record SalaryTrustPayment(string Id, long Amount, string BookNumber, string BookDate, string Note);
public sealed record SalaryTrustEntry(string Id, string Name, string Rank, string Workplace, int Year, int Month,
    long Amount, string CaseStatus, string Note, string ImportedStatus, bool LegacyPaid, bool ReviewPending, IReadOnlyList<SalaryTrustPayment> Payments);
public sealed record SalaryTrustLedger(int Version, IReadOnlyList<SalaryTrustEntry> Entries, bool OpeningImported);

public static class SalaryTrust
{
    public const string SettingsKey = "salaryTrustLedgerV1";
    public const long Maximum = 9_007_199_254_740_991;
    public const int OpeningCutoff = 202609;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static SalaryTrustLedger Empty => new(1, [], false);

    public static void Validate(SalaryTrustLedger ledger)
    {
        if (ledger.Version != 1 || ledger.Entries is null || ledger.Entries.Count > 100_000) throw new InvalidDataException("دفتر الأمانات غير صالح أو من إصدار أحدث.");
        var ids = new HashSet<Guid>(); var payments = new HashSet<Guid>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var e in ledger.Entries)
        {
            if (!Guid.TryParse(e.Id, out var id) || !ids.Add(id) || string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 300 ||
                e.Rank is null || e.Rank.Length > 150 || e.Workplace is null || e.Workplace.Length > 300 ||
                e.CaseStatus is null || e.CaseStatus.Length > 2000 || e.Note is null || e.Note.Length > 2000 || e.ImportedStatus is null || e.ImportedStatus.Length > 300 ||
                e.Year is < 1900 or > 2200 || e.Month is < 1 or > 12 || e.Amount is <= 0 or > Maximum || e.Payments is null ||
                !keys.Add($"{e.Year}-{e.Month}|{PersonProfiles.Key(e.Name)}") || (e.LegacyPaid && e.Payments.Count != 0) || (e.ReviewPending && (e.LegacyPaid || e.Payments.Count != 0)))
                throw new InvalidDataException("سجل أمانة غير صالح أو مكرر؛ لم تُحفظ التغييرات.");
            long paid = 0;
            foreach (var p in e.Payments)
            {
                if (!Guid.TryParse(p.Id, out var paymentId) || !payments.Add(paymentId) || p.Amount <= 0 || p.Amount > e.Amount ||
                    string.IsNullOrWhiteSpace(p.BookNumber) || p.BookNumber.Length > 150 || p.Note is null || p.Note.Length > 2000 ||
                    !DateOnly.TryParseExact(p.BookDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || paid > e.Amount - p.Amount)
                    throw new InvalidDataException("دفعة الصرف غير صالحة أو تتجاوز الرصيد.");
                paid += p.Amount;
            }
            if (total > Maximum - e.Amount) throw new InvalidDataException("مجموع الأمانات يتجاوز الحد المدعوم.");
            total += e.Amount;
        }
    }
    public static long Paid(SalaryTrustEntry e) => e.LegacyPaid ? e.Amount : e.Payments.Sum(p => p.Amount);
    public static long Remaining(SalaryTrustEntry e) => e.Amount - Paid(e);
    public static byte[] Encode(SalaryTrustLedger ledger) { Validate(ledger); return JsonSerializer.SerializeToUtf8Bytes(ledger, Json); }
    public static SalaryTrustLedger Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return Empty;
        try { var ledger = JsonSerializer.Deserialize<SalaryTrustLedger>(bytes, Json) ?? throw new JsonException(); Validate(ledger); return ledger; }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة دفتر الأمانات؛ لم تتغير البيانات.", ex); }
    }
    public static SalaryTrustLedger ReadFromBackup(BackupSnapshot snapshot)
    {
        if (!snapshot.SettingsPlists.TryGetValue(SettingsKey, out var plist)) return Empty;
        try { return PropertyListParser.Parse(plist) is NSData data ? Decode(data.Bytes) : throw new InvalidDataException("صيغة دفتر الأمانات غير صالحة."); }
        catch (Exception ex) when (ex is not InvalidDataException) { throw new InvalidDataException("تعذّر قراءة دفتر الأمانات.", ex); }
    }
    public static byte[] Plist(SalaryTrustLedger ledger)
    {
        using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(Encode(ledger)), stream); return stream.ToArray();
    }
}
