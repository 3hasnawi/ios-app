using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

// صيغة JSON نفسها في الآيفون داخل قيمة بيانات plist في settings.json.
public sealed record SalaryTrustPayment(string Id, long Amount, string BookNumber, string BookDate, string Note);
public sealed record SalaryTrustEntry(string Id, string Name, string Rank, string Workplace, int Year, int Month,
    long Amount, string CaseStatus, string Note, string ImportedStatus, bool LegacyPaid, bool ReviewPending, IReadOnlyList<SalaryTrustPayment> Payments, IReadOnlyList<string>? BookIds = null);
public sealed record SalaryTrustLedger(int Version, IReadOnlyList<SalaryTrustEntry> Entries, bool OpeningImported);
public enum SalaryTrustFilter { All, Unfinished, Unpaid, Partial, Paid, Review }

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
            var books = new HashSet<Guid>();
            if (e.BookIds is { Count: > 100 } || (e.BookIds is not null && e.BookIds.Any(x => !Guid.TryParse(x, out var bookId) || !books.Add(bookId))))
                throw new InvalidDataException("روابط كتب الأمانة غير صالحة أو مكررة.");
            long paid = 0;
            foreach (var p in e.Payments)
            {
                // رقم الكتاب وتاريخه اختياريان ومستقلان (مثل الآيفون): السلسلة الفارغة تعني أن المعلومة غير متوفرة.
                if (!Guid.TryParse(p.Id, out var paymentId) || !payments.Add(paymentId) || p.Amount <= 0 || p.Amount > e.Amount ||
                    p.BookNumber is null || p.BookNumber.Length > 150 || p.Note is null || p.Note.Length > 2000 ||
                    p.BookDate is null || !(p.BookDate.Length == 0 || ValidDate(p.BookDate)) || paid > e.Amount - p.Amount)
                    throw new InvalidDataException("دفعة الصرف غير صالحة أو تتجاوز الرصيد.");
                paid += p.Amount;
            }
            if (total > Maximum - e.Amount) throw new InvalidDataException("مجموع الأمانات يتجاوز الحد المدعوم.");
            total += e.Amount;
        }
    }
    /// تاريخ ميلادي مدني yyyy-MM-dd بين 1900 و2200 (مطابق لـ validDate في الآيفون).
    public static bool ValidDate(string text) =>
        text.Length == 10 && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year is >= 1900 and <= 2200;

    /// مرشح حالة الصرف (مطابق لـ PaymentFilter في الآيفون).
    public static readonly (SalaryTrustFilter Filter, string Title)[] Filters =
        [(SalaryTrustFilter.All, "كل الحالات"), (SalaryTrustFilter.Unfinished, "غير مكتمل"), (SalaryTrustFilter.Unpaid, "غير مصروف"),
         (SalaryTrustFilter.Partial, "مصروف جزئياً"), (SalaryTrustFilter.Paid, "مصروف بالكامل"), (SalaryTrustFilter.Review, "للمراجعة")];
    public static string FilterTitle(SalaryTrustFilter filter) => Filters.First(f => f.Filter == filter).Title;
    public static bool Includes(SalaryTrustFilter filter, SalaryTrustEntry e)
    {
        if (filter == SalaryTrustFilter.All) return true;
        if (e.ReviewPending) return filter == SalaryTrustFilter.Review;
        return filter switch
        {
            SalaryTrustFilter.Unfinished => Remaining(e) > 0,
            SalaryTrustFilter.Unpaid => Paid(e) == 0,
            SalaryTrustFilter.Partial => Paid(e) > 0 && Remaining(e) > 0,
            SalaryTrustFilter.Paid => Remaining(e) == 0,
            _ => false,
        };
    }
    public static string PaymentStatus(SalaryTrustEntry e) =>
        e.ReviewPending ? "للمراجعة" : Remaining(e) == 0 ? "مصروف بالكامل" : Paid(e) > 0 ? "مصروف جزئياً" : "غير مصروف";
    /// المتبقي المؤكد: يستبعد قيود المراجعة.
    public static long Outstanding(IEnumerable<SalaryTrustEntry> entries) => entries.Where(e => !e.ReviewPending).Sum(Remaining);
    public static string MonthLabel(int year, int month) => month is >= 1 and <= 12 ? $"{EmployeeDeductions.MonthTitles[month - 1]} {year.ToString(CultureInfo.InvariantCulture)}" : "—";

    /// ربط مجموعة بكتاب واحد لا يسجل صرفاً؛ يُرفض الطلب كله عند غياب أي قيد.
    public static SalaryTrustLedger Link(SalaryTrustLedger ledger, Guid bookId, IReadOnlySet<string> ids)
    {
        Validate(ledger);
        if (bookId == Guid.Empty || ids.Count == 0 || !ids.IsSubsetOf(ledger.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal)))
            throw new InvalidDataException("تغيّرت القيود؛ أعد اختيارها.");
        var result = ledger with { Entries = ledger.Entries.Select(entry => {
            if (!ids.Contains(entry.Id)) return entry;
            var links = (entry.BookIds ?? []).ToList();
            if (!links.Any(id => Guid.TryParse(id, out var parsed) && parsed == bookId)) links.Add(bookId.ToString());
            return entry with { BookIds = links };
        }).ToArray() };
        Validate(result); return result;
    }
    public static void ValidateBooks(SalaryTrustLedger ledger, IReadOnlyCollection<ArchiveBook> available)
    {
        var ids = available.Select(x => x.Id).ToHashSet();
        if (ledger.Entries.SelectMany(e => e.BookIds ?? []).Any(id => !Guid.TryParse(id, out var bookId) || !ids.Contains(bookId)))
            throw new InvalidDataException("كتاب مرتبط بأحد قيود الأمانات غير موجود في الأرشيف؛ لم تُحفظ النسخة.");
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
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data) throw new InvalidDataException("صيغة دفتر الأمانات غير صالحة.");
            var ledger = Decode(data.Bytes); ValidateBooks(ledger, snapshot.Books); return ledger;
        }
        catch (Exception ex) when (ex is not InvalidDataException) { throw new InvalidDataException("تعذّر قراءة دفتر الأمانات.", ex); }
    }
    public static byte[] Plist(SalaryTrustLedger ledger)
    {
        using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(Encode(ledger)), stream); return stream.ToArray();
    }
}
