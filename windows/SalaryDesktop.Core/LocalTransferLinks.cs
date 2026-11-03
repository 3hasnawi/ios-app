using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record TransferLinksSnapshot(BackupSnapshot Snapshot, IReadOnlyDictionary<string, string> Links, string? Revision, IReadOnlyDictionary<string, string> Categories);
/// روابط أسماء الاستقطاعات إلى الحسابات المحفوظة؛ تحفظ مستقلة عن ملف الآيفون.
public sealed class LocalTransferLinks(string directory)
{
    private sealed record LinkData(int Version, string SourceDigest, Dictionary<string, string> Links, Dictionary<string, string>? Categories = null, List<ExecutionAccount>? Accounts = null, byte[]? Customizations = null, byte[]? BoldFont = null, byte[]? BookLinks = null, byte[]? BankName = null, byte[]? BankIban = null, byte[]? BankManual = null, byte[]? ReferenceArchive = null, byte[]? Issuance = null, Dictionary<string, byte[]>? Settings = null);
    private readonly string root = Path.GetFullPath(directory);
    /// إعدادات إضافية يعدّلها ويندوز وتُكتب كما هي في settings.json (قيم plist)، مثل فرع الحساب ورقمه المحلي.
    public static readonly IReadOnlySet<string> EditableSettings = new HashSet<string>(["bankStatementBranch", "bankStatementLocalAccount", ContractOverrides.SettingsKey], StringComparer.Ordinal);
    private static void ValidateSettings(IReadOnlyDictionary<string, byte[]>? settings)
    {
        if (settings is not null && settings.Any(p => !EditableSettings.Contains(p.Key) || p.Value is null || p.Value.Length > 100_000)) throw new InvalidDataException("إعدادات الحساب المحلية غير صالحة.");
    }
    private static string Digest(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') ? value : throw new InvalidDataException("بصمة النسخة غير صالحة.");
    private string FileFor(BackupSnapshot source) => Path.Combine(root, Digest(source.SourceDigest) + ".json");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Validate(Dictionary<string, string> links)
    {
        if (links.Count > 100_000 || links.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 1000 || string.IsNullOrWhiteSpace(p.Value) || p.Value.Length > 1000))
            throw new InvalidDataException("روابط الحسابات غير صالحة.");
    }
    private static Dictionary<string, string> Original(BackupSnapshot source, string key)
    {
        if (!source.SettingsPlists.TryGetValue(key, out var bytes)) return new(StringComparer.Ordinal);
        try
        {
            if (PropertyListParser.Parse(bytes) is not NSData data) throw new InvalidDataException("صيغة روابط الحسابات غير صالحة.");
            var links = JsonSerializer.Deserialize<Dictionary<string, string>>(data.Bytes) ?? throw new JsonException(); Validate(links); return links;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة روابط الحسابات من النسخة.", e); }
    }
    private static BackupSnapshot WithLinks(BackupSnapshot source, Dictionary<string, string> links, Dictionary<string, string> categories, IReadOnlyList<ExecutionAccount> accounts, byte[]? customizations, byte[]? boldFont, byte[]? bookLinks, byte[]? bankName, byte[]? bankIban, byte[]? bankManual, byte[]? referenceArchive, byte[]? issuance, IReadOnlyDictionary<string, byte[]>? extra = null)
    {
        using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(links)), plist);
        var settings = source.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        settings["transferPayrollAccountLinks"] = plist.ToArray();
        using var categoryPlist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(categories)), categoryPlist);
        settings["transferEntityCategories"] = categoryPlist.ToArray();
        if (customizations is not null) settings["transferLetterCustomizations"] = customizations;
        if (boldFont is not null) settings["transferHeaderBoldFont"] = boldFont;
        if (bookLinks is not null) settings["transferLetterEntityLinks"] = bookLinks;
        if (bankName is not null) settings["bankStatementAccountName"] = bankName;
        if (bankIban is not null) settings["bankStatementIBAN"] = bankIban;
        if (bankManual is not null) settings["bankStatementManualAmounts"] = bankManual;
        if (referenceArchive is not null) settings["transferReferenceArchiveData"] = referenceArchive;
        if (issuance is not null) settings[GroupIssuance.SettingsKey] = issuance;
        foreach (var (key, value) in extra ?? new Dictionary<string, byte[]>()) settings[key] = value;
        return source with { SettingsPlists = settings, Accounts = accounts };
    }
    public TransferLinksSnapshot Load(BackupSnapshot source)
    {
        var path = FileFor(source);
        if (!File.Exists(path)) return new(source, Original(source, "transferPayrollAccountLinks"), null, Original(source, "transferEntityCategories"));
        try
        {
            var bytes = File.ReadAllBytes(path);
            var data = JsonSerializer.Deserialize<LinkData>(bytes) ?? throw new JsonException();
            if (data.Version != 1 || data.SourceDigest != source.SourceDigest || data.Links is null) throw new InvalidDataException("ملف الروابط المحلي غير صالح.");
            var categories = data.Categories ?? Original(source, "transferEntityCategories");
            var accounts = data.Accounts ?? source.Accounts.ToList();
            Validate(data.Links); Validate(categories); ValidateAccounts(source, accounts);
            var custom = data.Customizations ?? source.SettingsPlists.GetValueOrDefault("transferLetterCustomizations");
            var bold = data.BoldFont ?? source.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont");
            var bookLinks = data.BookLinks ?? source.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks");
            var bankName = data.BankName ?? source.SettingsPlists.GetValueOrDefault("bankStatementAccountName");
            var bankIban = data.BankIban ?? source.SettingsPlists.GetValueOrDefault("bankStatementIBAN");
            var bankManual = data.BankManual ?? source.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts");
            var referenceArchive = data.ReferenceArchive ?? source.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData");
            var issuance = data.Issuance ?? source.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey);
            ValidateSettings(data.Settings);
            var snapshot = WithLinks(source, data.Links, categories, accounts, custom, bold, bookLinks, bankName, bankIban, bankManual, referenceArchive, issuance, data.Settings);
            _ = IndividualLetterLinks.Read(snapshot);
            return new(snapshot, data.Links, Hash(bytes), categories);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة روابط الحسابات المحلية؛ لم تُغيّر البيانات.", e); }
    }
    public TransferLinksSnapshot SaveLink(BackupSnapshot source, TransferLinksSnapshot expected, TransferEntry entry, ExecutionAccount account)
    {
        if (!expected.Snapshot.Accounts.Any(a => a == account) || expected.Snapshot.Accounts.Count(a => PayrollAnalysis.Normalize(a.Entity) == PayrollAnalysis.Normalize(account.Entity)) != 1)
            throw new InvalidDataException("الحساب المختار غير موجود أو اسمه مكرر في النسخة.");
        if (!entry.Id.StartsWith("payroll|", StringComparison.Ordinal) && !entry.Id.StartsWith("deduction|", StringComparison.Ordinal)) throw new InvalidDataException("مصدر الجهة غير صالح.");
        var key = entry.Id.StartsWith("payroll|", StringComparison.Ordinal) ? entry.Id[8..] : "deduction|" + PayrollAnalysis.Normalize(entry.Title);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("جهة الاستقطاع غير صالحة.");
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات في نافذة أخرى؛ أعد فتح البيانات.");
        var actual = TransferPreparation.Prepare(current.Snapshot, entry.Month).Entries.SingleOrDefault(e => e.Id == entry.Id && e.Title == entry.Title);
        if (actual is null || actual.Amount != entry.Amount || actual.MissingAmounts != entry.MissingAmounts) throw new InvalidDataException("تغيّرت بيانات الجهة؛ أعد تحميلها قبل الربط.");
        var links = current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        links[key] = PayrollAnalysis.Normalize(account.Entity); Validate(links);
        return Write(source, links, current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Snapshot.Accounts, current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey));
    }
    public TransferLinksSnapshot SaveCategory(BackupSnapshot source, TransferLinksSnapshot expected, TransferEntry entry, TransferGroup category)
    {
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت بيانات الجهات في نافذة أخرى؛ أعد فتح البيانات.");
        var actual = TransferPreparation.Prepare(current.Snapshot, entry.Month).Entries.SingleOrDefault(e => e.Id == entry.Id && e.Title == entry.Title);
        if (actual is null || actual.Amount != entry.Amount || actual.MissingAmounts != entry.MissingAmounts || actual.Account != entry.Account) throw new InvalidDataException("تغيّرت بيانات الجهة؛ أعد تحميلها قبل التصنيف.");
        var value = category switch { TransferGroup.Rafidain => "rafidain", TransferGroup.OtherBanks => "otherBanks", TransferGroup.Anbar => "anbar", TransferGroup.Unresolved => "unresolved", _ => throw new InvalidDataException("التصنيف غير صالح.") };
        var key = $"{PayrollAnalysis.Normalize(actual.Title)}|{actual.Account?.Iban ?? ""}|{PayrollAnalysis.Normalize(actual.Account?.Branch ?? "")}";
        var categories = current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        categories[key] = value; Validate(categories);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), categories, current.Snapshot.Accounts, current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey));
    }
    private static void ValidateAccounts(BackupSnapshot source, IReadOnlyList<ExecutionAccount> accounts)
    {
        if (accounts.Any(a => a is null || a.Entity is null || a.Iban is null || a.Branch is null || a.Swift is null) || accounts.Count < source.Accounts.Count ||
            accounts.Count > 100_000 || accounts.Select(a => PayrollAnalysis.Normalize(a.Entity)).Distinct(StringComparer.Ordinal).Count() != accounts.Count ||
            !accounts.Take(source.Accounts.Count).Select(a => a.Entity).SequenceEqual(source.Accounts.Select(a => a.Entity)))
            throw new InvalidDataException("قائمة الحسابات المحلية غير صالحة؛ لم تُستبدل البيانات.");
    }
    private static ExecutionAccount NormalizeAndValidate(ExecutionAccount updated)
    {
        if (string.IsNullOrWhiteSpace(updated.Entity) || updated.Entity.Length > 1000 || updated.Branch.Length > 1000 || updated.Entity.Any(char.IsControl) || updated.Branch.Any(char.IsControl))
            throw new InvalidDataException("اسم الحساب أو المصرف غير صالح.");
        var iban = TransferPreparation.NormalizeAccount(updated.Iban);
        var local = updated.LocalAccount is null || updated.LocalAccount.Length == 0 ? null : TransferPreparation.NormalizeAccount(updated.LocalAccount);
        var normalized = updated with { Iban = iban, LocalAccount = local, Branch = updated.Branch.Trim(), Swift = updated.Swift.Trim().ToUpperInvariant() };
        var directLocal = local is not null && (PayrollAnalysis.Normalize(updated.Entity).Contains(PayrollAnalysis.Normalize("صندوق الإسكان"), StringComparison.Ordinal) || PayrollAnalysis.Normalize(updated.Entity).Contains(PayrollAnalysis.Normalize("الموارد المائية"), StringComparison.Ordinal)) && TransferPreparation.Classify(normalized) == TransferGroup.Anbar;
        if (!TransferPreparation.ValidIban(iban) && !(iban.Length == 0 && directLocal && TransferPreparation.LocalAccount(normalized) is not null)) throw new InvalidDataException("IBAN غير صالح، أو الحساب المحلي غير متاح لهذه الجهة.");
        if (normalized.Swift.Length > 0 && !TransferPreparation.ValidSwift(normalized.Swift)) throw new InvalidDataException("رمز SWIFT غير صالح.");
        if (local is not null && TransferPreparation.LocalAccount(normalized) is null) throw new InvalidDataException("الحساب المحلي لا يطابق IBAN أو ليس 15 رقماً.");
        return normalized;
    }
    public TransferLinksSnapshot SaveAccount(BackupSnapshot source, TransferLinksSnapshot expected, ExecutionAccount old, ExecutionAccount updated)
    {
        if (source.Version < 2) throw new InvalidDataException("إصدار النسخة لا يدعم حفظ الحسابات؛ استورد نسخة أحدث أولاً.");
        if (old.Entity != updated.Entity) throw new InvalidDataException("لا يمكن تغيير اسم الحساب هنا؛ أعد استيراد قائمة الحسابات لتغيير الأسماء.");
        var normalized = NormalizeAndValidate(updated);
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision || !current.Snapshot.Accounts.Contains(old)) throw new InvalidDataException("تغيّرت بيانات الحساب؛ أعد تحميل القائمة قبل الحفظ.");
        var accounts = current.Snapshot.Accounts.Select(a => a == old ? normalized : a).ToArray(); ValidateAccounts(source, accounts);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), accounts, current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey));
    }
    public TransferLinksSnapshot ImportAccounts(BackupSnapshot source, TransferLinksSnapshot expected, AccountImportPreview preview)
    {
        if (source.Version < 2 || preview.Problems.Count > 0 || preview.Accounts.Count == 0) throw new InvalidDataException("راجع أخطاء ملف الحسابات قبل الحفظ، وتأكد من استخدام نسخة تدعم الحسابات.");
        var imported = preview.Accounts.Select(NormalizeAndValidate).ToArray();
        if (imported.Select(a => PayrollAnalysis.Normalize(a.Entity)).Distinct(StringComparer.Ordinal).Count() != imported.Length) throw new InvalidDataException("توجد حسابات مكررة في الملف.");
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت الحسابات في نافذة أخرى؛ أعد فتح الملف قبل الاستيراد.");
        var accounts = current.Snapshot.Accounts.ToList();
        var positions = accounts.Select((a, i) => (key: PayrollAnalysis.Normalize(a.Entity), index: i)).ToDictionary(x => x.key, x => x.index, StringComparer.Ordinal);
        foreach (var account in imported)
        {
            var key = PayrollAnalysis.Normalize(account.Entity);
            if (!positions.TryGetValue(key, out var index)) { positions[key] = accounts.Count; accounts.Add(account); continue; }
            var old = accounts[index];
            // يحتفظ الاسم القديم بروابط الجهات، والحقول الفارغة لا تمحو ما سبق حفظه.
            accounts[index] = NormalizeAndValidate(old with { Iban = account.Iban.Length == 0 ? old.Iban : account.Iban,
                Branch = account.Branch.Length == 0 ? old.Branch : account.Branch, Swift = account.Swift.Length == 0 ? old.Swift : account.Swift,
                LocalAccount = account.LocalAccount ?? old.LocalAccount });
        }
        ValidateAccounts(source, accounts);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), accounts, current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey));
    }
    public TransferLinksSnapshot SaveGroupSettings(BackupSnapshot source, TransferLinksSnapshot expected, TransferGroup group, GroupLetterSettings value, bool applyToAll, IReadOnlySet<string>? selectedFields = null)
    {
        var id = group switch { TransferGroup.Rafidain => "rafidain_group", TransferGroup.OtherBanks => "other_banks_group", _ => throw new InvalidDataException("نوع الكتاب غير صالح.") };
        return SaveLetterSettings(source, expected, id, value, applyToAll, selectedFields, null, null, null);
    }
    public TransferLinksSnapshot SaveIndividualSettings(BackupSnapshot source, TransferLinksSnapshot expected, string id, GroupLetterSettings value, bool applyToAll, IReadOnlySet<string>? selectedFields = null, string? accountMonth = null, string? accountEntryId = null, string? accountValue = null)
    {
        if (id is not ("food_company" or "food_tax" or "tax_social" or "contracts" or "cash_salary" or "ramadi" or "housing" or "water" or "rafidain_anbar")) throw new InvalidDataException("نوع الكتاب غير صالح.");
        return SaveLetterSettings(source, expected, id, value, applyToAll, selectedFields, accountMonth, accountEntryId, accountValue);
    }
    private TransferLinksSnapshot SaveLetterSettings(BackupSnapshot source, TransferLinksSnapshot expected, string id, GroupLetterSettings value, bool applyToAll, IReadOnlySet<string>? selectedFields, string? accountMonth, string? accountEntryId, string? accountValue)
    {
        if (new[] { value.Header, value.Number, value.First, value.Second, value.Third }.Any(string.IsNullOrWhiteSpace) || value.Header.Length > 2000 || value.Number.Length > 200 ||
            new[] { value.First, value.Second, value.Third }.Any(s => s.Length > 200 || s.Any(char.IsControl)) || value.Footer.Length > 4000 ||
            value.Header.Any(c => char.IsControl(c) && c != '\n') || value.Footer.Any(c => char.IsControl(c) && c != '\n') || value.Number.Any(char.IsControl) ||
            value.OutgoingMonth is < 1 or > 12 || value.SignatureOffset is < -60 or > 60) throw new InvalidDataException("راجع الترويسة والعدد والأسماء وشهر الصادر وموضع اللجنة.");
        var supported = new HashSet<string>(["header", "fixedNumber", "committeeFirst", "committeeSecond", "committeeThird", "footer", "outgoingMonth", "signatureOffset", "bodyMonth", "bodyTemplate", "foodContractReference", "beneficiaryAccount"], StringComparer.Ordinal);
        selectedFields ??= supported.Where(f => f != "foodContractReference" && f != "beneficiaryAccount" && (!applyToAll || f != "bodyTemplate")).ToHashSet(StringComparer.Ordinal);
        if (selectedFields.Count == 0 || selectedFields.Any(f => !supported.Contains(f))) throw new InvalidDataException("اختر حقلاً صالحاً للتخصيص.");
        if (selectedFields.Contains("beneficiaryAccount") && (applyToAll || id is "rafidain_group" or "other_banks_group" or "rafidain_anbar")) throw new InvalidDataException("حساب المستفيد يخصص لكتاب منفرد واحد فقط.");
        if (selectedFields.Contains("bodyMonth") && !string.IsNullOrWhiteSpace(value.BodyMonth) && MonthNames.SortKey(value.BodyMonth) == 0) throw new InvalidDataException("شهر متن الكتاب غير صالح.");
        var defaultBody = id is "rafidain_group" or "other_banks_group" ? GroupLetterPdf.DefaultBodyTemplate : IndividualLetterBody.Default(id);
        if (selectedFields.Contains("bodyTemplate"))
        {
            if (applyToAll) throw new InvalidDataException("متن الكتاب يختلف عن الكتب الأخرى؛ احفظ تعديله لهذا الكتاب فقط.");
            if (id is "rafidain_group" or "other_banks_group") GroupLetterPdf.ValidateBodyTemplate(value.BodyTemplate ?? defaultBody);
            else IndividualLetterBody.Validate(id, value.BodyTemplate ?? defaultBody);
        }
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت تخصيصات الكتب؛ أعد فتح الكتاب قبل الحفظ.");
        JsonObject data;
        if (current.Snapshot.SettingsPlists.TryGetValue("transferLetterCustomizations", out var original))
        {
            if (PropertyListParser.Parse(original) is not NSData bytes) throw new InvalidDataException("صيغة تخصيص الكتب غير صالحة.");
            data = bytes.Bytes.Length == 0 ? new JsonObject() : JsonNode.Parse(bytes.Bytes)?.AsObject() ?? throw new InvalidDataException("تخصيص الكتب غير صالح.");
        }
        else data = new JsonObject();
        var common = data["common"] as JsonObject ?? new JsonObject(); data["common"] = common.DeepClone(); common = (JsonObject)data["common"]!;
        var individual = data["individual"] as JsonObject ?? new JsonObject(); data["individual"] = individual.DeepClone(); individual = (JsonObject)data["individual"]!;
        var fields = new Dictionary<string, JsonNode?> {
            ["header"] = JsonValue.Create(value.Header), ["committeeFirst"] = JsonValue.Create(value.First), ["committeeSecond"] = JsonValue.Create(value.Second),
            ["committeeThird"] = JsonValue.Create(value.Third), ["footer"] = JsonValue.Create(value.Footer), ["outgoingMonth"] = JsonValue.Create(value.OutgoingMonth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""),
            ["signatureOffset"] = JsonValue.Create(value.SignatureOffset), ["bodyMonth"] = JsonValue.Create(value.BodyMonth ?? ""), ["bodyTemplate"] = JsonValue.Create(value.BodyTemplate ?? defaultBody)
        };
        if (selectedFields.Contains("fixedNumber")) common["fixedNumber"] = value.Number;
        if (selectedFields.Contains("foodContractReference"))
        {
            if ((value.FoodContractReference?.Length ?? 0) > 200 || (value.FoodContractReference?.Any(char.IsControl) ?? false)) throw new InvalidDataException("رقم عقد الإطعام غير صالح.");
            common["foodContractReference"] = value.FoodContractReference ?? "";
        }
        if (applyToAll)
        {
            foreach (var (key, item) in fields.Where(p => selectedFields.Contains(p.Key))) common[key] = item?.DeepClone();
            foreach (var key in individual.Select(p => p.Key).ToArray())
                if (individual[key] is JsonObject item) foreach (var field in fields.Keys.Where(selectedFields.Contains)) item.Remove(field);
        }
        else
        {
            var item = individual[id] as JsonObject ?? new JsonObject(); individual[id] = item.DeepClone(); item = (JsonObject)individual[id]!;
            foreach (var (key, field) in fields.Where(p => selectedFields.Contains(p.Key))) item[key] = field?.DeepClone();
        }
        if (selectedFields.Contains("beneficiaryAccount"))
        {
            var item = individual[id] as JsonObject ?? new JsonObject(); individual[id] = item.DeepClone(); item = (JsonObject)individual[id]!;
            var chosen = (accountValue ?? "").Trim();
            if (chosen.Length > 128 || chosen.Any(char.IsControl)) throw new InvalidDataException("حساب المستفيد غير صالح.");
            if (chosen.Length == 0)
            {
                item["beneficiaryAccount"] = ""; item.Remove("beneficiarySource"); item.Remove("beneficiaryOriginal"); item.Remove("beneficiaryReference");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(accountMonth)) throw new InvalidDataException("اختر شهر الكتاب قبل تعديل الحساب.");
                var transfer = TransferPreparation.Prepare(current.Snapshot, accountMonth);
                var reference = LetterReferencePack.IndividualAccount(LetterReferencePack.Read(current.Snapshot), id, transfer.PayerAccount) ?? throw new InvalidDataException("مرجع حساب المستفيد غير موجود.");
                string origin, originalAccount;
                if (id is "ramadi" or "housing" or "water")
                {
                    var entry = transfer.Entries.SingleOrDefault(e => e.Id == accountEntryId && e.Group == TransferGroup.Anbar) ?? throw new InvalidDataException("اختر جهة الكتاب أولاً.");
                    if (entry.Account is null || TransferPreparation.Issues(entry).Count > 0) throw new InvalidDataException("راجع حساب جهة الكتاب أولاً.");
                    origin = entry.Title; originalAccount = id == "ramadi" ? TransferPreparation.NormalizeAccount(entry.Account.Iban) : TransferPreparation.LocalAccount(entry.Account) ?? throw new InvalidDataException("الحساب المحلي للجهة غير صالح.");
                }
                else { origin = id; originalAccount = reference; }
                item["beneficiaryAccount"] = TransferPreparation.NormalizeAccount(chosen); item["beneficiarySource"] = origin; item["beneficiaryOriginal"] = originalAccount; item["beneficiaryReference"] = reference;
                using var checkPlist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(data)), checkPlist);
                var checkSettings = current.Snapshot.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal); checkSettings["transferLetterCustomizations"] = checkPlist.ToArray();
                var checkedAccount = IndividualAccountCustomization.Resolve(current.Snapshot with { SettingsPlists = checkSettings }, id, origin, originalAccount, reference, requireAccountListMatch: false);
                if (checkedAccount == TransferPreparation.NormalizeAccount(transfer.PayerAccount)) throw new InvalidDataException("حساب المستفيد يطابق حساب الدفع.");
            }
        }
        using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(data)), plist);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Snapshot.Accounts, plist.ToArray(), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey));
    }
    public TransferLinksSnapshot SaveBookLink(BackupSnapshot source, TransferLinksSnapshot expected, TransferEntry entry, string id)
    {
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت بيانات الجهات؛ أعد فتح الكتاب.");
        var transfer = TransferPreparation.Prepare(current.Snapshot, entry.Month);
        var actual = transfer.Entries.SingleOrDefault(e => e.Id == entry.Id && e.Title == entry.Title);
        if (actual != entry) throw new InvalidDataException("تغيّرت جهة الكتاب؛ أعد فتحها.");
        _ = IndividualEntityPreparation.Prepare(current.Snapshot, transfer, id, entry.Id, LetterReferencePack.Read(current.Snapshot));
        var links = IndividualLetterLinks.Read(current.Snapshot); links[id] = IndividualLetterLinks.FromEntry(actual);
        var saved = IndividualLetterLinks.Encode(links);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Snapshot.Accounts, current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), saved, current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey));
    }
    public TransferLinksSnapshot ConfirmGroup(BackupSnapshot source, TransferLinksSnapshot expected, GroupIssuanceReview review, string note)
    {
        Directory.CreateDirectory(root); using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source); if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت بيانات التحويل؛ أعد تدقيق المجموعة.");
        var transfer = TransferPreparation.Prepare(current.Snapshot, review.Month);
        var fresh = GroupIssuance.Review(current.Snapshot, transfer, review.Group);
        if (fresh.Month != review.Month || fresh.Payer != review.Payer || fresh.PayerIban != review.PayerIban || fresh.BaselineMonth != review.BaselineMonth || !fresh.Lines.SequenceEqual(review.Lines) || !fresh.Changes.SequenceEqual(review.Changes)) throw new InvalidDataException("تغيّرت المجموعة أو تفاصيلها؛ أعد تدقيقها.");
        var issuance = GroupIssuance.Append(current.Snapshot, fresh, note);
        var settings = current.Snapshot.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal); settings[GroupIssuance.SettingsKey] = issuance;
        GroupIssuance.Validate(current.Snapshot with { SettingsPlists = settings });
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Snapshot.Accounts,
            current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"),
            current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), issuance);
    }
    public TransferLinksSnapshot SaveReferences(BackupSnapshot source, TransferLinksSnapshot expected, byte[] archive)
    {
        _ = LetterReferencePack.ReadArchive(archive);
        Directory.CreateDirectory(root); using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source); if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت بيانات الكتب؛ أعد اختيار المراجع.");
        using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(archive), plist);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Snapshot.Accounts,
            current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"),
            current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), plist.ToArray(), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey));
    }
    public TransferLinksSnapshot SaveContractOverride(BackupSnapshot source, TransferLinksSnapshot expected, string month, BankSummary basis, decimal? total)
    {
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت بيانات الحساب؛ أعد فتح النافذة.");
        var actual = BankStatementCalculator.Calculate(current.Snapshot, month);
        if (actual.ContractNominal != basis.ContractNominal || actual.ContractRetirement != basis.ContractRetirement || actual.ContractCount != basis.ContractCount || actual.MissingContractNominal != basis.MissingContractNominal)
            throw new InvalidDataException("تغيّر أساس العقود؛ أعد مراجعة المبلغ.");
        if (total is not null && (actual.ContractCount == 0 || actual.MissingContractNominal > 0 || actual.TransferWarnings.Count > 0))
            throw new InvalidDataException("أكمل مصادر العقود ومراجعة الاسمي قبل حفظ المبلغ اليدوي.");
        var entry = total is null ? null : new ContractOverride(total.Value, actual.ContractNominal, actual.ContractRetirement, actual.ContractCount, actual.MissingContractNominal);
        var value = ContractOverrides.Set(current.Snapshot.SettingsPlists.GetValueOrDefault(ContractOverrides.SettingsKey), month, entry);
        var updated = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [ContractOverrides.SettingsKey] = value };
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Snapshot.Accounts,
            current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"),
            current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey), updated);
    }
    public TransferLinksSnapshot SaveBankDetails(BackupSnapshot source, TransferLinksSnapshot expected, string month, string name, string iban, BankManualAmounts amounts, string? branch = null, string? localAccount = null)
    {
        name = name.Trim(); iban = TransferPreparation.NormalizeAccount(iban);
        var account = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (branch is not null)
        {
            branch = branch.Trim();
            if (branch.Length is 0 or > 100 || branch.Any(char.IsControl)) throw new InvalidDataException("اكتب فرع المصرف (حتى ١٠٠ حرف).");
            account["bankStatementBranch"] = PlistString(branch);
        }
        if (localAccount is not null)
        {
            localAccount = TransferPreparation.NormalizeAccount(localAccount);
            if (localAccount.Length > 30 || localAccount.Any(c => !char.IsAsciiDigit(c))) throw new InvalidDataException("رقم الحساب المحلي أرقام فقط (حتى ٣٠ رقماً).");
            account["bankStatementLocalAccount"] = PlistString(localAccount);
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(c => char.IsControl(c) || c == '|') || !TransferPreparation.ValidIban(iban) || MonthNames.SortKey(month) == 0 ||
            new[] { amounts.AccumulatedLeave, amounts.EmployeeRewards, amounts.SingleList }.Any(v => v < 0 || v > 9_007_199_254_740_991m || v != decimal.Truncate(v)))
            throw new InvalidDataException("راجع اسم الحساب وIBAN ومبالغ الشهر اليدوية.");
        Directory.CreateDirectory(root); using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت بيانات الحساب؛ أعد فتح النافذة.");
        if (!current.Snapshot.Months.Any(m => m.Name == month)) throw new InvalidDataException("الشهر غير موجود في النسخة المفتوحة.");
        _ = BankStatementCalculator.Manual(current.Snapshot, month);
        JsonObject all;
        if (current.Snapshot.SettingsPlists.TryGetValue("bankStatementManualAmounts", out var raw))
        {
            if (PropertyListParser.Parse(raw) is not NSData existing) throw new InvalidDataException("صيغة المبالغ اليدوية غير صالحة.");
            all = existing.Bytes.Length == 0 ? new JsonObject() : JsonNode.Parse(existing.Bytes)?.AsObject() ?? throw new InvalidDataException("المبالغ اليدوية غير صالحة.");
        }
        else all = new JsonObject();
        all[month] = new JsonObject { ["accumulatedLeave"] = amounts.AccumulatedLeave, ["employeeRewards"] = amounts.EmployeeRewards, ["singleList"] = amounts.SingleList };
        static byte[] Plist(NSObject obj) { using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(obj, stream); return stream.ToArray(); }
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Snapshot.Accounts,
            current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferHeaderBoldFont"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"),
            Plist(new NSString(name)), Plist(new NSString(iban)), Plist(new NSData(JsonSerializer.SerializeToUtf8Bytes(all))), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey), account);
    }
    public TransferLinksSnapshot SaveGroupFont(BackupSnapshot source, TransferLinksSnapshot expected, byte[] font)
    {
        GroupLetterFont.Validate(font); Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت بيانات الكتب؛ أعد فتح الكتاب قبل حفظ الخط.");
        using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(font), plist);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Snapshot.Accounts, current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterCustomizations"), plist.ToArray(), current.Snapshot.SettingsPlists.GetValueOrDefault("transferLetterEntityLinks"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementAccountName"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementIBAN"), current.Snapshot.SettingsPlists.GetValueOrDefault("bankStatementManualAmounts"), current.Snapshot.SettingsPlists.GetValueOrDefault("transferReferenceArchiveData"), current.Snapshot.SettingsPlists.GetValueOrDefault(GroupIssuance.SettingsKey));
    }
    private static byte[] PlistString(string value) { using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSString(value), stream); return stream.ToArray(); }
    private TransferLinksSnapshot Write(BackupSnapshot source, Dictionary<string, string> links, Dictionary<string, string> categories, IReadOnlyList<ExecutionAccount> accounts, byte[]? customizations, byte[]? boldFont, byte[]? bookLinks, byte[]? bankName, byte[]? bankIban, byte[]? bankManual, byte[]? referenceArchive, byte[]? issuance, IReadOnlyDictionary<string, byte[]>? settingsUpdate = null)
    {
        // الإعدادات الإضافية المحفوظة سابقاً تبقى مع كل حفظ، وتُستبدل القيم الجديدة فقط.
        var settings = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var path = FileFor(source);
        if (File.Exists(path))
        {
            try { foreach (var (key, value) in JsonSerializer.Deserialize<LinkData>(File.ReadAllBytes(path))?.Settings ?? new Dictionary<string, byte[]>()) settings[key] = value; }
            catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة الإعدادات المحلية؛ لم تُحفظ التغييرات.", ex); }
        }
        foreach (var (key, value) in settingsUpdate ?? new Dictionary<string, byte[]>()) settings[key] = value;
        ValidateSettings(settings);
        var extra = settings.Count == 0 ? null : settings;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LinkData(1, source.SourceDigest, links, categories, accounts.ToList(), customizations, boldFont, bookLinks, bankName, bankIban, bankManual, referenceArchive, issuance, extra));
        var temp = Path.Combine(root, ".links-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes); output.Flush(true); }
            using (var input = File.OpenRead(temp)) if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من روابط الحسابات.");
            File.Move(temp, FileFor(source), true);
            return new(WithLinks(source, links, categories, accounts, customizations, boldFont, bookLinks, bankName, bankIban, bankManual, referenceArchive, issuance, extra), links, Hash(bytes), categories);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
