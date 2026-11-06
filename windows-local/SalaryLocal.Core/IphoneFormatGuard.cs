using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Claunia.PropertyList;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

/// Validates the iPhone version/setting contract without changing any plist bytes or executing templates.
/// Archive members, books and cross-record links must additionally be checked by the archive bridge.
public static class IphoneFormatGuard
{
    public const string FundingKey = "fundingExpenseApprovalsV1";
    public const string TemplatesKey = "customTransferTemplatesV1";
    public const string UnifiedKey = "transferLetterCustomizations";
    public const string ContractsKey = "bankStatementContractOverridesV1";
    private const decimal MaxMoney = 9_007_199_254_740_991m;
    // An explicit resource limit, not an assertion that larger iPhone archives are invalid.
    private const int MaxPlistBytes = 256_000_000;
    private static readonly string[] RequiredTemplateStrings = ["title", "header", "number", "recipient", "subject", "body", "footer", "firstSigner", "secondSigner", "thirdSigner", "sourceName", "account"];
    private static readonly string[] OptionalTemplateStrings = ["attachments", "foodContractReference", "bodyMonth", "outgoingMonth"];
    private static readonly HashSet<string> TemplateKeys = new(RequiredTemplateStrings.Concat(OptionalTemplateStrings).Concat(["id", "source", "bodyAlignment", "bodySpacing", "bodyBold", "signatureVerticalOffset", "signatureHorizontalOffset"]), StringComparer.Ordinal);
    private static InvalidDataException Bad(string detail) => new("إعدادات نسخة الآيفون غير مدعومة أو غير سليمة: " + detail);
    private static bool Supported(int version) => version is >= 1 and <= 6 or 8 or 9 or 10;

    public static void Validate(int version, IReadOnlyDictionary<string, byte[]> settings)
    {
        if (!Supported(version)) throw Bad("الإصدار " + version + "؛ المدعوم 1–6 و8–10، ولا تُقبل الصيغة التجريبية 7.");
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            foreach (var (key, bytes) in settings)
            {
                if (bytes is null || bytes.Length == 0 || bytes.Length > MaxPlistBytes) throw Bad(key + "؛ حد قيمة plist الواحدة 256,000,000 بايت.");
                _ = PropertyListParser.Parse(bytes) ?? throw Bad(key + "؛ plist فارغ.");
            }
            if (settings.ContainsKey(ContractsKey)) { if (version < 6) throw Bad(ContractsKey + " يحتاج الإصدار 6 أو أحدث."); Contracts(Data(settings, ContractsKey, 80_000)); }
            if (settings.ContainsKey(FundingKey)) { if (version < 8) throw Bad(FundingKey + " يحتاج الإصدار 8 أو أحدث."); Funding(Data(settings, FundingKey, 16_000_000)); }
            else if (version == 8) throw Bad("الإصدار 8 يتطلب " + FundingKey);
            if (settings.ContainsKey(TemplatesKey)) { if (version < 9) throw Bad(TemplatesKey + " يحتاج الإصدار 9 أو أحدث."); Templates(Data(settings, TemplatesKey, 2_000_000)); }
            else if (version == 9) throw Bad("الإصدار 9 يتطلب " + TemplatesKey);
            bool unified = settings.ContainsKey(UnifiedKey) && Unified(Data(settings, UnifiedKey, 16_000_000));
            if (unified && version < 10 || !unified && version == 10) throw Bad("الإصدار 10 يتطلب قالب كتاب موحد، ولا يُحفظ هذا القالب بإصدار أقدم.");
            if (settings.ContainsKey(SalaryTrust.SettingsKey)) Trust(Data(settings, SalaryTrust.SettingsKey, 64_000_000));
            if (settings.ContainsKey(BankReconciliation.SettingsKey)) Reconciliation(Data(settings, BankReconciliation.SettingsKey, 64_000_000));
            if (settings.ContainsKey("transferReferenceArchiveData")) References(Data(settings, "transferReferenceArchiveData", 10_000_000));
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        { throw new InvalidDataException("تعذّر التحقق من إعدادات نسخة الآيفون؛ لم تُعدّل البيانات.", ex); }
    }

    public static int ExportVersion(IReadOnlyDictionary<string, byte[]> settings, int minimumVersion = 5)
    {
        if (!Supported(minimumVersion)) throw Bad("إصدار المصدر غير مدعوم؛ لا يمكن تخفيضه أو تحويل الإصدار 7 تلقائياً.");
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            // Validate the decoded settings before choosing a version, then require its exact feature gates.
            int feature = settings.ContainsKey(TemplatesKey) ? 9 : settings.ContainsKey(FundingKey) ? 8 : settings.ContainsKey(ContractsKey) ? 6 : 5;
            if (settings.ContainsKey(UnifiedKey) && Unified(Data(settings, UnifiedKey, 16_000_000))) feature = 10;
            int version = Math.Max(minimumVersion, feature);
            Validate(version, settings);
            return version;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        { throw new InvalidDataException("تعذّر تحديد إصدار تصدير آمن لنسخة الآيفون؛ لم تُعدّل البيانات.", ex); }
    }

    private static byte[] Data(IReadOnlyDictionary<string, byte[]> settings, string key, int maximum)
    {
        if (settings[key] is not { Length: > 0 } raw || raw.Length > MaxPlistBytes) throw Bad(key + "؛ حجم plist غير مدعوم.");
        if (PropertyListParser.Parse(raw) is not NSData data || data.Bytes.Length > maximum) throw Bad(key + "؛ يلزم NSData بحد " + maximum.ToString(CultureInfo.InvariantCulture) + " بايت.");
        return data.Bytes;
    }
    private static JsonDocument Document(byte[] data)
    {
        var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 64 });
        try { Unique(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!keys.Add(p.Name)) throw Bad("حقل JSON مكرر: " + p.Name); Unique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private static JsonElement Object(JsonElement value) { if (value.ValueKind != JsonValueKind.Object) throw Bad("يلزم كائن JSON."); return value; }
    private static JsonElement Field(JsonElement value, string key) => Object(value).TryGetProperty(key, out var field) ? field : throw Bad("حقل مطلوب: " + key);
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Bad("يلزم نص JSON.");
    private static string Text(JsonElement value, string key) => Text(Field(value, key));
    private static string? OptionalText(JsonElement value, string key) => !value.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null ? null : Text(field);
    private static int Length(string value) => new StringInfo(value).LengthInTextElements;
    private static long Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : throw Bad("يلزم عدد صحيح.");
    private static void Boolean(JsonElement value) { if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad("يلزم حقل منطقي."); }
    private static decimal Money(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var amount) || amount < 0 || amount > MaxMoney || decimal.Truncate(amount) != amount) throw Bad("المبلغ يجب أن يكون ديناراً صحيحاً ضمن الحد 9,007,199,254,740,991.");
        return amount;
    }
    private static double Date(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var date) || !double.IsFinite(date) || date < 0 || date > 7_000_000_000) throw Bad("تاريخ آيفون خارج المجال 0–7,000,000,000 ثانية منذ 2001.");
        return date;
    }
    private static JsonElement Array(JsonElement value, int maximum, bool nonempty = false)
    { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum || nonempty && value.GetArrayLength() == 0) throw Bad("عدد السجلات خارج الحد " + maximum); return value; }
    private static void Contracts(byte[] data)
    {
        using var doc = Document(data);
        foreach (var p in Object(doc.RootElement).EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(p.Name) || Length(p.Name) > 200) throw Bad(ContractsKey + "؛ اسم شهر غير صالح.");
            foreach (var key in new[] { "total", "nominal", "retirement" }) Money(Field(p.Value, key));
            if (Integer(Field(p.Value, "count")) <= 0 || Integer(Field(p.Value, "missing")) != 0) throw Bad(ContractsKey + "؛ عدد العقود أو النواقص غير صالح.");
        }
    }
    private static void Trust(byte[] data)
    {
        if (data.Length == 0) return;
        using var document = Document(data); var root = Object(document.RootElement);
        Boolean(Field(root, "openingImported"));
        foreach (var entry in Array(Field(root, "entries"), 100_000).EnumerateArray())
        {
            Boolean(Field(entry, "legacyPaid")); Boolean(Field(entry, "reviewPending"));
        }
        // Decode validates totals, civil dates, payment/refund amounts, duplicate identities and IDs.
        // Do not re-encode: the original NSData, including unknown keys, remains untouched.
        _ = SalaryTrust.Decode(data);
    }
    private static void Reconciliation(byte[] data)
    {
        if (data.Length == 0) return;
        using var document = Document(data); var root = Object(document.RootElement);
        Array(Field(root, "periods"), 5000);
        // The Swift custom decoder defaults these optional fields, unlike the legacy C# reader.
        // Supply defaults to a disposable validation-only copy, never to the imported setting.
        var validation = JsonNode.Parse(data)!.AsObject();
        foreach (var node in validation["periods"]!.AsArray())
        {
            var period = node?.AsObject() ?? throw Bad("فترة مطابقة ليست كائناً.");
            period["openingBalance"] ??= JsonValue.Create(0L);
            period["memberFirst"] ??= JsonValue.Create(""); period["memberSecond"] ??= JsonValue.Create("");
            period["movements"] ??= new JsonArray();
            if (period["movements"] is not JsonArray movements || movements.Count > 20_000) throw Bad("فترة المطابقة تتجاوز 20,000 حركة.");
        }
        using var plist = new MemoryStream();
        PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(validation)), plist);
        var settings = new Dictionary<string, byte[]> { [BankReconciliation.SettingsKey] = plist.ToArray() };
        var snapshot = new BackupSnapshot("", "", 6, [], [], [], 0, [], [], settings);
        _ = BankReconciliation.ReadFromBackup(snapshot);
    }
    private static void References(byte[] data)
    {
        if (data.Length == 0) throw Bad("حزمة مراجع الكتب فارغة.");
        using var memory = new MemoryStream(data, false); using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        if (zip.Entries.Count > 14) throw Bad("حزمة المراجع تحوي أكثر من 14 ملفاً/مجلداً.");
        var names = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!names.Add(entry.FullName)) throw Bad("اسم مكرر في حزمة المراجع.");
            if (entry.FullName == "templates/" && entry.Length == 0) continue;
            // Only fixed manifest-selected names are read by LetterReferencePack; never extract paths.
            if (((entry.ExternalAttributes >> 16) & 0xF000) is 0xA000 or 0x4000) throw Bad("رابط أو مجلد غير مسموح في حزمة المراجع.");
            if (entry.Length is < 0 or > 10_000_000 || (total += entry.Length) > 40_000_000) throw Bad("حزمة المراجع تتجاوز 40,000,000 بايت بعد الفك أو 10,000,000 للملف.");
            using var input = entry.Open(); using var content = new MemoryStream(); var buffer = new byte[65536]; long length = 0; int read;
            while ((read = input.Read(buffer)) > 0)
            {
                length += read; if (length > entry.Length) throw Bad("طول ملف مراجع يخالف فهرس الضغط.");
                if (entry.FullName == "manifest.json") { if (length > 100_000) throw Bad("فهرس المراجع يتجاوز 100,000 بايت."); content.Write(buffer, 0, read); }
            }
            if (length != entry.Length) throw Bad("ملف مراجع مبتور.");
            if (entry.FullName == "manifest.json")
            {
                using var manifest = Document(content.ToArray()); var root = Object(manifest.RootElement);
                if (Integer(Field(root, "version")) is not (1 or 2)) throw Bad("إصدار حزمة مراجع الكتب غير مدعوم.");
                foreach (var template in Array(Field(root, "templates"), 12).EnumerateArray())
                { Text(template, "id"); Text(template, "path"); Text(template, "sha256"); Integer(Field(template, "pages")); }
            }
        }
        // Reuses proven expected IDs, versions 1/2, SHA256, page counts, dimensions and digit text checks.
        var references = LetterReferencePack.ReadArchive(data);
        foreach (var (id, bytes) in references)
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
            if (pdf.IsEncrypted) throw Bad("مراجع PDF المشفرة غير مدعومة في الاستيراد المحلي.");
            var width = id == "month_digits" ? 612d : 595.3; var height = id == "month_digits" ? 792d : 841.9;
            foreach (var page in pdf.GetPages())
                if (Math.Abs(page.MediaBox.Bounds.Width - width) >= 2 || Math.Abs(page.MediaBox.Bounds.Height - height) >= 2) throw Bad("مقاس MediaBox لمرجع الكتاب لا يطابق الآيفون.");
        }
    }
    private static void Funding(byte[] data)
    {
        using var doc = Document(data); var root = Object(doc.RootElement);
        if (Integer(Field(root, "version")) != 1) throw Bad(FundingKey + "؛ يلزم إصدار دفتر 1.");
        var ids = new HashSet<Guid>();
        foreach (var record in Array(Field(root, "records"), 5000).EnumerateArray())
        {
            if (!Guid.TryParseExact(Text(record, "id"), "D", out var id) || !ids.Add(id)) throw Bad(FundingKey + "؛ معرف مكرر أو غير صالح.");
            var confirmed = Date(Field(record, "confirmedAt"));
            if (record.TryGetProperty("revokedAt", out var revoked) && revoked.ValueKind != JsonValueKind.Null && Date(revoked) < confirmed) throw Bad("إلغاء الاعتماد سابق للاعتماد.");
            var revision = Text(record, "sourceRevision");
            if (revision.Length == 0 || Length(revision) > 200 || Length(Text(record, "header")) > 400) throw Bad("مرجع الاعتماد أو ترويسته غير صالح.");
            var snapshot = Object(Field(record, "snapshot")); var month = Text(snapshot, "month");
            if (string.IsNullOrWhiteSpace(month) || Length(month) > 200) throw Bad("شهر الاعتماد غير صالح.");
            Money(Field(snapshot, "bankTotal"));
            foreach (var key in new[] { "unclassifiedCount", "missingNet", "missingContractNominal", "missingExecutionAmount" }) if (Integer(Field(snapshot, key)) < 0) throw Bad("عدد سالب في اعتماد المصروفات.");
            Boolean(Field(snapshot, "hasExecutionRows")); Boolean(Field(snapshot, "contractOverrideStale"));
            int index = 0; decimal total = 0;
            foreach (var row in Array(Field(snapshot, "rows"), 20_000, true).EnumerateArray())
            {
                var title = Text(row, "title"); if (Integer(Field(row, "id")) != index++ || title.Length == 0 || Length(title) > 1000) throw Bad("صف اعتماد المصروفات غير صالح.");
                total += Money(Field(row, "amount")); if (total > MaxMoney) throw Bad("مجموع اعتماد المصروفات يتجاوز الحد.");
            }
        }
    }
    private static void Templates(byte[] data)
    {
        if (data.Length == 0) return; // Explicit Swift compatibility: empty NSData means no custom templates.
        using var doc = Document(data); var root = Object(doc.RootElement);
        if (root.EnumerateObject().Any(p => p.Name is not ("version" or "templates")) || Integer(Field(root, "version")) != 1) throw Bad(TemplatesKey + "؛ دفتر أو حقول غير معروفة.");
        var ids = new HashSet<Guid>();
        foreach (var template in Array(Field(root, "templates"), 250).EnumerateArray())
        {
            if (Object(template).EnumerateObject().Any(p => !TemplateKeys.Contains(p.Name))) throw Bad(TemplatesKey + "؛ حقول قالب غير معروفة.");
            Template(template);
            if (!ids.Add(Guid.Parse(Text(template, "id")))) throw Bad("معرف قالب مكرر.");
        }
    }
    private static void Template(JsonElement template)
    {
        if (!Guid.TryParseExact(Text(template, "id"), "D", out _)) throw Bad("معرف قالب غير صالح.");
        foreach (var key in RequiredTemplateStrings.Concat(OptionalTemplateStrings))
        {
            var value = OptionalTemplateStrings.Contains(key) ? OptionalText(template, key) ?? "" : Text(template, key);
            if (Encoding.UTF8.GetByteCount(value) > 12_000 || value.Any(c => c < 32 && c is not ('\n' or '\r' or '\t'))) throw Bad("حقل القالب " + key + " يتجاوز 12,000 بايت أو يحوي محارف ممنوعة.");
        }
        if (string.IsNullOrWhiteSpace(Text(template, "title"))) throw Bad("عنوان القالب فارغ.");
        if (Text(template, "source") is not ("none" or "manual" or "payroll" or "execution")) throw Bad("مصدر القالب غير معروف.");
        if (OptionalText(template, "bodyAlignment") is { } align && align is not ("justified" or "right" or "center" or "left")) throw Bad("محاذاة القالب غير معروفة.");
        if (OptionalText(template, "bodySpacing") is { } spacing && spacing is not ("compact" or "medium" or "wide")) throw Bad("تباعد القالب غير معروف.");
        if (template.TryGetProperty("bodyBold", out var bold) && bold.ValueKind != JsonValueKind.Null) Boolean(bold);
        foreach (var (key, limit) in new[] { ("signatureVerticalOffset", 60), ("signatureHorizontalOffset", 30) })
            if (template.TryGetProperty(key, out var offset) && offset.ValueKind != JsonValueKind.Null && (Integer(offset) < -limit || Integer(offset) > limit)) throw Bad("إزاحة توقيع خارج الحد " + limit);
    }
    private static bool Unified(byte[] data)
    {
        if (data.Length == 0) return false;
        using var doc = Document(data); var root = Object(doc.RootElement); bool found = false;
        var common = Object(Field(root, "common")); var individual = Object(Field(root, "individual"));
        if (individual.EnumerateObject().Count() > 10_000) throw Bad(UnifiedKey + "؛ الحد المحلي 10,000 تخصيص.");
        if (root.TryGetProperty("wordReferenceHashes", out var hashes) && hashes.ValueKind != JsonValueKind.Null) foreach (var p in Object(hashes).EnumerateObject()) Text(p.Value);
        void Check(JsonElement customization, string? id)
        {
            Object(customization);
            foreach (var p in customization.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Null) continue;
                if (p.Name is "signatureOffset") { Integer(p.Value); continue; }
                if (p.Name is "header" or "committeeFirst" or "committeeSecond" or "committeeThird" or "footer" or "fixedNumber" or "foodContractReference" or "bodyTemplate" or "bodyMonth" or "outgoingMonth" or "committeeDateFirst" or "committeeDateSecond" or "committeeDateThird" or "beneficiaryAccount" or "beneficiarySource" or "beneficiaryOriginal" or "beneficiaryReference") Text(p.Value);
            }
            if (!customization.TryGetProperty("letterTemplate", out var template) || template.ValueKind == JsonValueKind.Null) return;
            found = true; Template(template);
            if (id is null) return;
            foreach (var key in new[] { "header", "number", "recipient", "subject", "footer" }) if (string.IsNullOrWhiteSpace(Text(template, key))) throw Bad("حقل القالب الموحد فارغ: " + key);
            Body(Text(template, "body"), id);
            var signers = new[] { "firstSigner", "secondSigner", "thirdSigner" }.Select(key => Text(template, key)).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (signers.Length == 0) throw Bad("القالب الموحد يحتاج توقيعاً واحداً على الأقل.");
            foreach (var signer in signers)
            {
                var parts = signer.Replace("\r\n", "\n").Replace('\r', '\n').Trim().Split('\n').Select(p => p.Trim()).ToArray();
                if (parts.Length is not (1 or 3) || parts.Any(string.IsNullOrEmpty)) throw Bad("التوقيع يجب أن يكون اسماً بسطر واحد أو ثلاثة أسطر غير فارغة.");
            }
            if (!id.StartsWith("treasury_", StringComparison.Ordinal))
            {
                var outgoing = OptionalText(template, "outgoingMonth")?.Trim() ?? "";
                var digits = string.Concat(outgoing.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c is >= '۰' and <= '۹' ? (char)('0' + c - '۰') : c));
                if (digits.Length > 0 && (digits.Length > 2 || digits.Any(c => !char.IsAsciiDigit(c)) || !int.TryParse(digits, out var number) || number is < 1 or > 12)) throw Bad("شهر الصادر يجب أن يكون 1–12.");
                if (OptionalText(template, "bodyMonth") is { } bodyMonth && !string.IsNullOrWhiteSpace(bodyMonth) && MonthNames.SortKey(bodyMonth) <= 0) throw Bad("شهر متن الكتاب غير صالح.");
            }
        }
        Check(common, null); foreach (var p in individual.EnumerateObject()) Check(p.Value, p.Name);
        return found;
    }
    private static void Body(string body, string id)
    {
        var fields = new List<string> { "المبلغ", "المبلغ كتابة", "حسابنا", "الشهر", "السنة" };
        switch (id)
        {
            case "treasury_guarantee": fields.AddRange(["حساب المستفيد", "سبب التضمين"]); break;
            case "treasury_schools": fields.Add("حساب المستفيد"); break;
            case "rafidain_group": case "other_banks_group": case "rafidain_anbar": break;
            case "food_company": case "food_tax": fields.AddRange(["المستفيد", "حساب المستفيد", "عقد الإطعام"]); break;
            case "tax_social": case "contracts": case "cash_salary": fields.AddRange(["المستفيد", "حساب المستفيد"]); break;
            case "ramadi": case "housing": fields.AddRange(["المستفيد", "حساب المستفيد", "المصرف"]); break;
            case "water": fields.AddRange(["المستفيد", "حساب المستفيد", "المصرف", "الرتبة", "الاسم"]); break;
            default: throw Bad("معرف قالب الكتاب غير مدعوم: " + id);
        }
        if (string.IsNullOrWhiteSpace(body) || Length(body) > 4000 || body.Any(c => c < 32 && c != '\n')) throw Bad("متن القالب غير صالح أو يتجاوز 4,000 محرف.");
        var matches = Regex.Matches(body, @"\{\{[^{}]+\}\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!matches.Select(m => m.Value).Order(StringComparer.Ordinal).SequenceEqual(fields.Select(f => "{{" + f + "}}").Order(StringComparer.Ordinal))) throw Bad("حقول متن الكتاب ناقصة أو مكررة أو غير مطابقة.");
        var stripped = Regex.Replace(body, @"\{\{[^{}]+\}\}", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (stripped.Contains("{{", StringComparison.Ordinal) || stripped.Contains("}}", StringComparison.Ordinal)) throw Bad("أقواس حقول متن الكتاب غير مكتملة.");
    }
}
