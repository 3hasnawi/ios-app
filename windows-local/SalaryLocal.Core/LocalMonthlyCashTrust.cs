using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

/// مراجعة ثابتة للإضافات فقط؛ لا تتضمن أمراً بصرف الأموال أو ربط الكتب.
public sealed class MonthlyCashTrustPreview
{
    internal MonthlyCashTrustPreview(string month, string sourceRevision, string sourceFingerprint,
        IReadOnlyList<SalaryTrustEntry> additions, int existingCount, int cashCount, long cashTotal)
    {
        Month = month; SourceRevision = sourceRevision; SourceFingerprint = sourceFingerprint;
        Additions = Array.AsReadOnly(additions.ToArray()); ExistingCount = existingCount;
        CashCount = cashCount; CashTotal = cashTotal; Total = additions.Sum(e => e.Amount);
    }
    public string Month { get; }
    public string SourceRevision { get; }
    public IReadOnlyList<SalaryTrustEntry> Additions { get; }
    public int Count => Additions.Count;
    public long Total { get; }
    public int ExistingCount { get; }
    public int CashCount { get; }
    public long CashTotal { get; }
    public bool IsNoOp => Count == 0;
    public string Message => IsNoOp
        ? "جميع رواتب الكاش لهذا الشهر مسجلة بالمبلغ والحالة نفسيهما؛ لا توجد إضافات."
        : "الإضافات المعروضة أمانات غير مصروفة؛ لا تتغير القيود أو الدفعات أو روابط الكتب السابقة.";
    internal string SourceFingerprint { get; }
}

public static class LocalMonthlyCashTrust
{
    private static string Key(int year, int month, string name) => $"{year}-{month}|{PersonProfiles.Key(name)}";

    public static MonthlyCashTrustPreview Prepare(BackupSnapshot source, string month)
    {
        try { return PrepareCore(source, month); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException or KeyNotFoundException)
        { throw new InvalidDataException("تعذّر التحقق من بيانات كاش الشهر؛ لم تُغيّر الأمانات. راجع بيانات الرواتب ثم أعد المحاولة.", ex); }
    }

    private static MonthlyCashTrustPreview PrepareCore(BackupSnapshot source, string month)
    {
        if (source is null || string.IsNullOrWhiteSpace(month) || source.SourceDigest is not { Length: 64 } revision ||
            !revision.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw new InvalidDataException("لقطة الرواتب غير صالحة؛ أعد فتح مساحة العمل.");
        int period = MonthNames.SortKey(month);
        if (period == 0) throw new InvalidDataException("تعذّر تحديد شهر الرواتب؛ اختر شهراً محفوظاً.");
        if (period <= SalaryTrust.OpeningCutoff)
            throw new InvalidDataException("إضافة كاش الشهر متاحة من تشرين الأول 2026 فقط؛ الأشهر السابقة تخص الرصيد الافتتاحي.");
        var matching = source.Months.Where(m => m.Name == month).ToArray();
        if (matching.Length != 1 || source.Months.Count(m => MonthNames.SortKey(m.Name) == period) != 1)
            throw new InvalidDataException("تعذّر تحديد شهر الرواتب بصورة فريدة؛ راجع الأشهر المحفوظة.");

        // نملك نسخاً مستقلة من البايتات، ولا نعتمد نتيجة كشف بنك مخزنة للّقطة القابلة للتعديل.
        var employees = source.Employees.Where(e => e.Month == month).Select(e => e with
        { Items = e.Items?.ToArray(), Details = e.Details?.ToArray(), RawColumns = e.RawColumns?.ToArray() }).ToArray();
        if (employees.Length == 0 || matching[0].EmployeeCount != employees.Length)
            throw new InvalidDataException("رواتب الشهر ناقصة أو غير مستوردة؛ راجع الشهر قبل إضافة الأمانات.");
        var owned = source with
        {
            Months = matching,
            Employees = employees,
            Deductions = source.Deductions.Where(d => d.Month == month).ToArray(),
            Books = source.Books.ToArray(),
            SettingsPlists = source.SettingsPlists.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal)
        };
        var ledger = SalaryTrust.ReadFromBackup(owned);
        if (!ledger.OpeningImported)
            throw new InvalidDataException("استورد الرصيد الافتتاحي للأمانات وراجعه أولاً، ثم أضف كاش الشهر.");
        foreach (var employee in employees)
        {
            if (employee.Net is null or < 0 || employee.Net > SalaryTrust.Maximum || employee.Rank is null || employee.Workplace is null)
                throw new InvalidDataException("توجد رواتب ببيانات ناقصة أو صافي غير صالح؛ راجع الشهر قبل إضافة الأمانات.");
            // تصنيف البطاقة لا يجوز أن يتسلل إلى قائمة الكاش حتى لو جمع الحقل اللفظين.
            if (employee.Details is not null)
            {
                using var detailJson = JsonDocument.Parse(employee.Details);
                if (detailJson.RootElement.ValueKind != JsonValueKind.Object ||
                    detailJson.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != detailJson.RootElement.EnumerateObject().Count())
                    throw new InvalidDataException("تفاصيل أحد الرواتب غير صالحة أو تتضمن حقولاً مكررة؛ راجع طريقة الدفع.");
            }
            var details = employee.Details is null ? new Dictionary<string, string>() :
                JsonSerializer.Deserialize<Dictionary<string, string>>(employee.Details) ?? throw new JsonException();
            if (details.Any(p => p.Value is null)) throw new InvalidDataException("تحتوي تفاصيل الراتب على قيمة غير صالحة.");
            var payment = PayrollAnalysis.Normalize(details.GetValueOrDefault("طريقة الدفع", ""));
            bool cash = payment.Contains("كاش", StringComparison.Ordinal) || payment.Contains("نقد", StringComparison.Ordinal);
            bool card = payment.Contains("بطاق", StringComparison.Ordinal) || payment.Contains("كارت", StringComparison.Ordinal) || payment.Contains("ماستر", StringComparison.Ordinal);
            if (cash && card) throw new InvalidDataException("طريقة دفع أحد الرواتب تجمع الكاش والبطاقة؛ صحح التصنيف قبل إضافة الأمانات.");
        }
        var list = CashSalaryListPreparation.Prepare(owned, month);
        var existing = ledger.Entries.ToDictionary(e => Key(e.Year, e.Month, e.Name), StringComparer.Ordinal);
        var batch = new HashSet<string>(StringComparer.Ordinal);
        var additions = new List<SalaryTrustEntry>(); int existingCount = 0;
        foreach (var person in list.Rows)
        {
            if (person.CaseStatus is null)
                throw new InvalidDataException("عمود الملاحظات بعد صافي الراتب غير متاح؛ أعد استيراد رواتب الشهر وراجعها قبل إضافة الأمانات.");
            string key = Key(period / 100, period % 100, person.Name);
            if (!batch.Add(key)) throw new InvalidDataException("توجد أسماء كاش مكررة في الشهر؛ راجعها قبل إضافتها للأمانات.");
            if (existing.TryGetValue(key, out var entry))
            {
                if (entry.Amount != person.Net || entry.CaseStatus != person.CaseStatus)
                    throw new InvalidDataException("تغيّر مبلغ أمانة مسجلة أو حالتها؛ راجعها قبل التحديث.");
                existingCount++; continue;
            }
            additions.Add(new(Guid.NewGuid().ToString().ToUpperInvariant(), person.Name, person.Rank, person.Workplace,
                period / 100, period % 100, person.Net, person.CaseStatus, "", "", false, false,
                Array.AsReadOnly(Array.Empty<SalaryTrustPayment>())));
        }
        if (ledger.Entries.Any(e => e.Year * 100 + e.Month == period && !batch.Contains(Key(e.Year, e.Month, e.Name))))
            throw new InvalidDataException("تغيّرت قائمة كاش الشهر وفيها قيود محفوظة لم تعد موجودة؛ راجعها قبل التحديث.");
        SalaryTrust.Validate(ledger with { Entries = ledger.Entries.Concat(additions).ToArray() });
        return new(month, revision, Fingerprint(owned), additions, existingCount, list.Rows.Count, list.Total);
    }

    private static string Fingerprint(BackupSnapshot source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(byte[] bytes)
        {
            Span<byte> length = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(length, bytes.LongLength);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        Append(JsonSerializer.SerializeToUtf8Bytes(source.Version));
        Append(JsonSerializer.SerializeToUtf8Bytes(source.Months));
        foreach (var employee in source.Employees) Append(JsonSerializer.SerializeToUtf8Bytes(employee));
        Append(JsonSerializer.SerializeToUtf8Bytes(source.Books));
        Append(source.SettingsPlists.GetValueOrDefault(SalaryTrust.SettingsKey) ?? []);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static byte[] RevalidateAndPatch(BackupSnapshot current, MonthlyCashTrustPreview reviewed)
    {
        if (reviewed is null || current.SourceDigest != reviewed.SourceRevision)
            throw new InvalidDataException("تغيّرت البيانات منذ مراجعة كاش الشهر؛ أعد المراجعة قبل الحفظ.");
        var actual = Prepare(current, reviewed.Month);
        if (actual.SourceFingerprint != reviewed.SourceFingerprint || actual.Count != reviewed.Count ||
            actual.Total != reviewed.Total || actual.ExistingCount != reviewed.ExistingCount ||
            actual.CashCount != reviewed.CashCount || actual.CashTotal != reviewed.CashTotal)
            throw new InvalidDataException("تغيّرت رواتب الشهر أو دفتر الأمانات منذ المراجعة؛ لم تُحفظ الإضافات.");
        var before = SalaryTrust.ReadFromBackup(current);
        for (int i = 0; i < actual.Count; i++)
        {
            var modeled = actual.Additions[i] with { Id = reviewed.Additions[i].Id };
            if (!SalaryTrust.Encode(new(1, [modeled], true)).SequenceEqual(SalaryTrust.Encode(new(1, [reviewed.Additions[i]], true))))
                throw new InvalidDataException("الإضافات لا تطابق المراجعة؛ أعد مراجعة كاش الشهر.");
        }
        var after = before with { Entries = before.Entries.Concat(reviewed.Additions).ToArray() };
        SalaryTrust.Validate(after); SalaryTrust.ValidateBooks(after, current.Books);
        return LocalSalaryTrust.PatchLedger(current, before, after);
    }
}
