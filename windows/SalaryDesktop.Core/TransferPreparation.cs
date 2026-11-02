using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record BankAccountDetails(string Name, string Branch, string LocalAccount, string Iban)
{
    /// ما يظهر في رأس كشف المطابقة: الاسم والفرع وIBAN (الحساب المحلي حُذف من الكشف بطلب المالك).
    public bool IsIncomplete => new[] { Name, Branch, Iban }.Any(string.IsNullOrWhiteSpace);
}

public enum TransferGroup { Rafidain, OtherBanks, Anbar, Unresolved }
public sealed record TransferEntry(string Id, string Month, string Title, decimal Amount, int People,
    int MissingAmounts, ExecutionAccount? Account, TransferGroup Group, string? Beneficiary, bool DuplicateSource);
public sealed record TransferSnapshot(string Month, string Payer, string PayerAccount, IReadOnlyList<TransferEntry> Entries,
    IReadOnlyList<string> SourceWarnings);

public static class TransferPreparation
{
    public static string NormalizeAccount(string value)
    {
        var buffer = new StringBuilder();
        foreach (var character in value.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(character) || character == '-') continue;
            buffer.Append(character switch { >= '٠' and <= '٩' => (char)('0' + character - '٠'), >= '۰' and <= '۹' => (char)('0' + character - '۰'), _ => character });
        }
        return buffer.ToString();
    }
    public static bool ValidIban(string value)
    {
        var iban = NormalizeAccount(value);
        if (iban.Length != 23 || !iban.StartsWith("IQ", StringComparison.Ordinal) || iban.Any(c => !char.IsAsciiLetterOrDigit(c))) return false;
        var remainder = 0;
        foreach (var c in iban[4..] + iban[..4])
        {
            int n = c >= 'A' ? c - 'A' + 10 : c - '0';
            if (n >= 10) remainder = (remainder * 10 + n / 10) % 97;
            remainder = (remainder * 10 + n % 10) % 97;
        }
        return remainder == 1;
    }
    public static bool ValidSwift(string value)
    {
        var bic = value.Trim().ToUpperInvariant();
        return bic.Length is 8 or 11 && bic.Take(6).All(c => c is >= 'A' and <= 'Z') && bic.Skip(6).All(char.IsAsciiLetterOrDigit);
    }
    public static TransferGroup Classify(ExecutionAccount? account)
    {
        if (account is null) return TransferGroup.Unresolved;
        var branch = PayrollAnalysis.Normalize(account.Branch);
        if (branch.Contains(PayrollAnalysis.Normalize("الرافدين"), StringComparison.Ordinal))
        {
            var clean = string.Join(' ', branch.Replace('/', ' ').Replace('-', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (clean.EndsWith("فرع الانبار", StringComparison.Ordinal)) return TransferGroup.Anbar;
            if (clean.Contains("الانبار", StringComparison.Ordinal)) return TransferGroup.Unresolved;
            return clean.Contains("فرع", StringComparison.Ordinal) ? TransferGroup.Rafidain : TransferGroup.Unresolved;
        }
        string[] known = ["الرشيد", "العقاري", "الزراعي", "الاسلامي", "التجاري", "الاهلي"];
        if (known.Any(name => branch.Contains(PayrollAnalysis.Normalize(name), StringComparison.Ordinal))) return TransferGroup.OtherBanks;
        var explicitBank = branch.Contains(PayrollAnalysis.Normalize("مصرف"), StringComparison.Ordinal) || branch.Contains(PayrollAnalysis.Normalize("بنك"), StringComparison.Ordinal);
        return explicitBank && ValidSwift(account.Swift) ? TransferGroup.OtherBanks : TransferGroup.Unresolved;
    }
    public static string? LocalAccount(ExecutionAccount account)
    {
        var local = account.LocalAccount is null ? null : NormalizeAccount(account.LocalAccount);
        if (local is not null && (local.Length != 15 || local.Any(c => !char.IsAsciiDigit(c)))) return null;
        if (account.Iban.Length == 0) return local;
        if (!ValidIban(account.Iban)) return null;
        var fromIban = NormalizeAccount(account.Iban)[^15..];
        if (fromIban.Any(c => !char.IsAsciiDigit(c)) || (local is not null && local != fromIban)) return null;
        return fromIban;
    }
    private static bool SafeField(string value) => !string.IsNullOrEmpty(value) && value.IndexOfAny(['|', '\n', '\r', '"']) < 0;
    public static IReadOnlyList<string> Issues(TransferEntry entity)
    {
        var issues = new List<string>();
        bool directAnbar = entity.Account is null && entity.Group == TransferGroup.Anbar && IsDirectAnbar(entity.Title);
        if (entity.Account is null && !directAnbar) return ["حساب الجهة غير محفوظ"];
        if (entity.DuplicateSource) issues.Add("البند موجود أيضاً في ملف الجهات؛ راجع التكرار.");
        if (entity.Account is { } account)
        {
            var detected = Classify(account);
            if (detected != TransferGroup.Unresolved && entity.Group != TransferGroup.Unresolved && detected != entity.Group) issues.Add("المجموعة لا تطابق مصرف الحساب.");
            if (entity.Group == TransferGroup.Anbar) { if (LocalAccount(account) is null) issues.Add("الحساب المحلي غير صالح أو غير مطابق."); }
            else
            {
                if (!ValidIban(account.Iban)) issues.Add("IBAN غير صالح.");
                if (!ValidSwift(account.Swift)) issues.Add("SWIFT غير صالح أو غير موجود.");
                if (account.LocalAccount is not null && LocalAccount(account) is null) issues.Add("الحساب المحلي لا يطابق IBAN.");
            }
        }
        if (entity.MissingAmounts > 0) issues.Add("توجد مبالغ ناقصة.");
        if (entity.Amount <= 0 || entity.Amount != decimal.Truncate(entity.Amount) || entity.Amount > 9_007_199_254_740_991m) issues.Add("المبلغ غير صالح للتحويل.");
        if (!SafeField(entity.Beneficiary ?? entity.Title)) issues.Add("اسم المستفيد غير صالح في ملف التحويل.");
        return issues;
    }
    private static bool IsDirectAnbar(string title)
    {
        var clean = string.Join(' ', PayrollAnalysis.Normalize(title).Replace('/', ' ').Replace('-', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean == PayrollAnalysis.Normalize("مصرف الرافدين فرع الانبار");
    }
    private static object? ReadSetting(BackupSnapshot snapshot, string name)
    {
        if (!snapshot.SettingsPlists.TryGetValue(name, out var data)) return null;
        try { return PropertyListParser.Parse(data); }
        catch (Exception ex) { throw new InvalidDataException($"تعذرت قراءة إعداد {name} من النسخة.", ex); }
    }
    private static string SettingString(BackupSnapshot snapshot, string key) => ReadSetting(snapshot, key) switch
    {
        null => "", NSString value => value.Content, _ => throw new InvalidDataException($"صيغة إعداد {key} غير صالحة.")
    };
    /// بيانات الحساب الظاهرة في كشف المطابقة (مثل الآيفون): الاسم والفرع (الافتراضي الأنبار) والحساب المحلي وIBAN.
    public static BankAccountDetails Account(BackupSnapshot snapshot)
    {
        var branch = SettingString(snapshot, "bankStatementBranch");
        return new(SettingString(snapshot, "bankStatementAccountName"), ReadSetting(snapshot, "bankStatementBranch") is null ? "الأنبار" : branch,
            SettingString(snapshot, "bankStatementLocalAccount"), SettingString(snapshot, "bankStatementIBAN"));
    }
    public static (string Name, string Iban) Payer(BackupSnapshot snapshot) => (SettingString(snapshot, "bankStatementAccountName"), SettingString(snapshot, "bankStatementIBAN"));
    private static Dictionary<string, string> SettingDictionary(BackupSnapshot snapshot, string key)
    {
        var value = ReadSetting(snapshot, key);
        if (value is null) return new(StringComparer.Ordinal);
        if (value is not NSData data) throw new InvalidDataException($"صيغة إعداد {key} غير صالحة.");
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(data.Bytes) ?? throw new JsonException();
            if (values.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)))
                throw new InvalidDataException($"يحتوي إعداد {key} قيمة ناقصة.");
            return values;
        }
        catch (JsonException ex) { throw new InvalidDataException($"تعذرت قراءة إعداد {key}.", ex); }
    }
    /// التجهيز محفوظ لكل نسخة بيانات وشهر (مثل كشف البنك): الرجوع إلى قسم التحويل لا يعيد الحساب، وأي تعديل ينشئ نسخة جديدة.
    private static readonly ConditionalWeakTable<BackupSnapshot, ConcurrentDictionary<string, TransferSnapshot>> Prepared = new();
    public static TransferSnapshot Prepare(BackupSnapshot snapshot, string month) => Prepared.GetOrCreateValue(snapshot).GetOrAdd(month, m => Build(snapshot, m));
    private static TransferSnapshot Build(BackupSnapshot snapshot, string month)
    {
        var bank = BankStatementCalculator.Calculate(snapshot, month);
        var accounts = new Dictionary<string, ExecutionAccount>(StringComparer.Ordinal);
        foreach (var account in snapshot.Accounts)
            if (!accounts.TryAdd(PayrollAnalysis.Normalize(account.Entity), account)) throw new InvalidDataException("توجد حسابات متعددة بالاسم نفسه؛ يجب مراجعتها.");
        var links = SettingDictionary(snapshot, "transferPayrollAccountLinks");
        var overrides = SettingDictionary(snapshot, "transferEntityCategories");
        ExecutionAccount? AccountFor(string title, string? payrollId = null)
        {
            var key = payrollId is null ? "deduction|" + PayrollAnalysis.Normalize(title) : payrollId;
            var selected = links.TryGetValue(key, out var linked) ? linked : payrollId is null ? PayrollAnalysis.Normalize(title) : "";
            return accounts.GetValueOrDefault(selected);
        }
        TransferGroup GroupFor(string title, ExecutionAccount? account, bool direct)
        {
            var key = $"{PayrollAnalysis.Normalize(title)}|{account?.Iban ?? ""}|{PayrollAnalysis.Normalize(account?.Branch ?? "")}";
            if (overrides.TryGetValue(key, out var value)) return value switch
            {
                "rafidain" => TransferGroup.Rafidain, "otherBanks" => TransferGroup.OtherBanks,
                "anbar" => TransferGroup.Anbar, "unresolved" => TransferGroup.Unresolved,
                _ => throw new InvalidDataException("تصنيف جهة محفوظ بصيغة غير معروفة.")
            };
            return direct ? TransferGroup.Anbar : Classify(account);
        }
        var groups = PayrollAnalysis.Groups(snapshot, month);
        var entries = new List<TransferEntry>();
        foreach (var group in groups)
        {
            var account = AccountFor(group.Title);
            var explicitLink = links.ContainsKey("deduction|" + PayrollAnalysis.Normalize(group.Title));
            var direct = !explicitLink && account is null && IsDirectAnbar(group.Title);
            entries.Add(new TransferEntry("deduction|" + group.Title, month, group.Title, group.Total, group.People, group.MissingAmounts,
                account, GroupFor(group.Title, account, direct), explicitLink ? account?.Entity : null, false));
        }
        var foodFund = decimal.Round(bank.Breakdown["شركة عراق الجوال"] * 0.03m, 0, MidpointRounding.AwayFromZero);
        var payroll = new (string Id, string Title, decimal Amount)[] {
            ("health", "مديرية شؤون الرعاية الصحية", bank.Breakdown["الرعاية الصحية"]),
            ("fundFood", "صندوق شهداء الشرطة (عقد إطعام)", foodFund),
            ("fundDonations", "صندوق شهداء الشرطة (تبرعات)", bank.Breakdown["صندوق شهداء الشرطة"]),
            ("sports", "مديرية التدريب البدني وألعاب الشرطة", bank.Breakdown["النشاط الرياضي"])
        };
        foreach (var item in payroll.Where(item => item.Amount > 0))
        {
            var account = AccountFor(item.Title, item.Id);
            var duplicate = groups.Any(group => PayrollAnalysis.Normalize(group.Title) == PayrollAnalysis.Normalize(item.Title));
            entries.Add(new TransferEntry("payroll|" + item.Id, month, item.Title, item.Amount, 1, 0, account,
                GroupFor(item.Title, account, false), account?.Entity, duplicate));
        }
        var warnings = bank.TransferWarnings.ToList();
        if (!bank.ExecutionMatched) warnings.Add("استقطاعات المصارف لا تطابق ملف جهات التنفيذ أو المطابقة غير مكتملة.");
        return new TransferSnapshot(month, SettingString(snapshot, "bankStatementAccountName"), SettingString(snapshot, "bankStatementIBAN"), entries, warnings);
    }
    public static byte[] MakeCsv(TransferSnapshot snapshot, TransferGroup group, DateOnly date)
    {
        if (group is not (TransferGroup.Rafidain or TransferGroup.OtherBanks) || snapshot.SourceWarnings.Count != 0 ||
            snapshot.Entries.Any(x => x.Group == TransferGroup.Unresolved) || !ValidIban(snapshot.PayerAccount) || !SafeField(snapshot.Payer) || !SafeField(snapshot.Month))
            throw new InvalidDataException("راجع مصادر الشهر والتصنيف وبيانات الحساب الدافع قبل التصدير.");
        var entries = snapshot.Entries.Where(x => x.Group == group).ToArray();
        if (entries.Length == 0 || entries.Any(x => Issues(x).Count != 0) || entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException("راجع حسابات ومبالغ جهات المجموعة قبل التصدير.");
        const string header = "Reference|Value Date|Payer Name|Payer Account|Amount|Currency|Receiver BIC|Beneficiary Name|Beneficiary Account|Remittance Information|Details of Charges";
        var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var rows = new List<string> { header };
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i]; var account = entry.Account ?? throw new InvalidDataException("حساب المستفيد غير متوفر.");
            string[] fields = [day + (group == TransferGroup.Rafidain ? "30000" : "20000") + (i + 1).ToString(CultureInfo.InvariantCulture), day,
                snapshot.Payer, NormalizeAccount(snapshot.PayerAccount), entry.Amount.ToString("0", CultureInfo.InvariantCulture), "IQD", account.Swift.Trim().ToUpperInvariant(),
                entry.Beneficiary ?? entry.Title, NormalizeAccount(account.Iban), "استقطاعات " + snapshot.Month, "SLEV"];
            if (fields.Any(x => !SafeField(x))) throw new InvalidDataException("تحتوي بيانات التحويل على رمز غير مسموح.");
            rows.Add(string.Join('|', fields));
        }
        var output = new UTF8Encoding(false, true).GetBytes(string.Join("\r\n", rows));
        VerifyCsv(output, snapshot, group, date);
        return output;
    }
    public static void VerifyCsv(byte[] data, TransferSnapshot snapshot, TransferGroup group, DateOnly date)
    {
        if (group is not (TransferGroup.Rafidain or TransferGroup.OtherBanks) || snapshot.SourceWarnings.Count != 0 ||
            snapshot.Entries.Any(x => x.Group == TransferGroup.Unresolved) || !ValidIban(snapshot.PayerAccount) || !SafeField(snapshot.Payer) || !SafeField(snapshot.Month) ||
            data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw new InvalidDataException("ملف التحويل أو مصادره غير صالحة.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("ترميز ملف التحويل غير صالح.", ex); }
        if (text.EndsWith('\n') || text.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n') ||
            text.Replace("\r\n", "", StringComparison.Ordinal).Contains('\r')) throw new InvalidDataException("فواصل سطور ملف التحويل غير صالحة.");
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var entities = snapshot.Entries.Where(e => e.Group == group).ToArray();
        const string header = "Reference|Value Date|Payer Name|Payer Account|Amount|Currency|Receiver BIC|Beneficiary Name|Beneficiary Account|Remittance Information|Details of Charges";
        if (entities.Length == 0 || lines.Length != entities.Length + 1 || lines[0] != header ||
            entities.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != entities.Length || entities.Any(e => Issues(e).Count != 0))
            throw new InvalidDataException("جدول التحويل لا يطابق الجهات المحددة.");
        var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = entities[i]; var account = entity.Account ?? throw new InvalidDataException("حساب المستفيد غير متوفر.");
            var fields = lines[i + 1].Split('|');
            if (fields.Length != 11 || fields[0] != day + (group == TransferGroup.Rafidain ? "30000" : "20000") + (i + 1).ToString(CultureInfo.InvariantCulture) ||
                fields[1] != day || fields[2] != snapshot.Payer || fields[3] != NormalizeAccount(snapshot.PayerAccount) ||
                fields[4] != entity.Amount.ToString("0", CultureInfo.InvariantCulture) ||
                fields[5] != "IQD" || fields[6] != account.Swift.Trim().ToUpperInvariant() || fields[7] != (entity.Beneficiary ?? entity.Title) ||
                fields[8] != NormalizeAccount(account.Iban) || fields[9] != "استقطاعات " + snapshot.Month || fields[10] != "SLEV")
                throw new InvalidDataException("أحد صفوف ملف التحويل لا يطابق مصدره.");
        }
    }
}
