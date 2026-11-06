using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

/// تخصيصات متوافقة مع الآيفون: نغيّر الحقول المختارة فقط ونبقي الحقول غير المعروفة.
public static class LocalTransferLetterSettings
{
    public const string SettingsKey = "transferLetterCustomizations";
    public const string ReferencesKey = "transferReferenceArchiveData";
    public const string FontKey = "transferHeaderBoldFont";
    public static IReadOnlyList<(string Id, string Title)> Choices { get; } = Array.AsReadOnly(new[] {
        ("rafidain_group", "كتاب مجموعة الرافدين"), ("other_banks_group", "كتاب الرشيد والمصارف الأخرى"),
        ("food_company", "شركة عراق الجوال"), ("food_tax", "ضريبة عقد الإطعام"), ("tax_social", "الضريبة والحماية الاجتماعية"),
        ("contracts", "ضمان العقود"), ("cash_salary", "أمانات رواتب الكاش"), ("ramadi", "تنفيذ الرمادي"),
        ("housing", "صندوق الإسكان"), ("water", "الموارد المائية"), ("rafidain_anbar", "مصرف الرافدين فرع الأنبار") });
    public static bool IsGroup(string id) => id is "rafidain_group" or "other_banks_group";
    public static bool IsEntity(string id) => id is "ramadi" or "housing" or "water" or "rafidain_anbar";
    public static string Title(string id) => Choices.FirstOrDefault(c => c.Id == id).Title ?? throw new InvalidDataException("نوع الكتاب غير معروف.");
    public static string DefaultBody(string id) { _ = Title(id); return IsGroup(id) ? GroupLetterPdf.DefaultBodyTemplate : IndividualLetterBody.Default(id); }
    internal static byte[] DataPlist(byte[] bytes)
    { using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(bytes), stream); return stream.ToArray(); }
    internal static JsonObject ReadObject(BackupSnapshot source, string key)
    {
        if (!source.SettingsPlists.TryGetValue(key, out var raw)) return new JsonObject();
        try
        {
            if (PropertyListParser.Parse(raw) is not NSData data || data.Bytes.Length > 30_000_000) throw new InvalidDataException("صيغة إعداد الكتب غير صالحة.");
            if (data.Bytes.Length == 0) return new JsonObject();
            using var document = JsonDocument.Parse(data.Bytes);
            void Unique(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object) { var keys = new HashSet<string>(StringComparer.Ordinal); foreach (var p in value.EnumerateObject()) { if (!keys.Add(p.Name)) throw new InvalidDataException("حقول مكررة في إعداد الكتب؛ لم يُستبدل الإعداد."); Unique(p.Value); } }
                else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
            }
            Unique(document.RootElement);
            return JsonNode.Parse(data.Bytes)?.AsObject() ?? throw new InvalidDataException("إعداد الكتب غير صالح.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة إعداد الكتب؛ لم تُفترض قيمة فارغة.", ex); }
    }
    private static JsonObject Object(JsonObject root, string key)
    {
        if (!root.ContainsKey(key)) { var result = new JsonObject(); root[key] = result; return result; }
        return root[key] as JsonObject ?? throw new InvalidDataException("بنية تخصيص الكتب غير مدعومة؛ لم تُستبدل.");
    }
    public static void Validate(string id, GroupLetterSettings value)
    {
        _ = Title(id);
        if (new[] { value.Header, value.Number, value.First, value.Second, value.Third }.Any(string.IsNullOrWhiteSpace) ||
            value.Header.Length > 2000 || value.Number.Length > 200 || value.Number.Any(char.IsControl) ||
            new[] { value.First, value.Second, value.Third }.Any(s => s.Length > 200 || s.Any(char.IsControl)) ||
            value.Header.Any(c => char.IsControl(c) && c != '\n') || value.Footer.Length > 4000 || value.Footer.Any(c => char.IsControl(c) && c != '\n') ||
            value.OutgoingMonth is < 1 or > 12 || value.SignatureOffset is < -60 or > 60 ||
            !string.IsNullOrWhiteSpace(value.BodyMonth) && MonthNames.SortKey(value.BodyMonth) == 0 ||
            (value.FoodContractReference?.Length ?? 0) > 200 || (value.FoodContractReference?.Any(char.IsControl) ?? false))
            throw new InvalidDataException("راجع الترويسة والعدد وأسماء اللجنة والشهر ومرجع عقد الإطعام.");
        var body = string.IsNullOrWhiteSpace(value.BodyTemplate) ? DefaultBody(id) : value.BodyTemplate;
        if (IsGroup(id)) GroupLetterPdf.ValidateBodyTemplate(body); else IndividualLetterBody.Validate(id, body);
    }
    public static byte[] Patch(BackupSnapshot source, string id, GroupLetterSettings value, bool commonFields)
    {
        Validate(id, value); var before = GroupLetterSettingsReader.Read(source, id); var root = ReadObject(source, SettingsKey);
        var common = Object(root, "common"); var individual = Object(root, "individual");
        var changes = new Dictionary<string, JsonNode?>();
        void Text(string key, string? old, string? current) { if ((old ?? "") != (current ?? "")) changes[key] = JsonValue.Create(current ?? ""); }
        Text("header", before.Header, value.Header); Text("committeeFirst", before.First, value.First); Text("committeeSecond", before.Second, value.Second);
        Text("committeeThird", before.Third, value.Third); Text("footer", before.Footer, value.Footer); Text("bodyMonth", before.BodyMonth, value.BodyMonth);
        if (before.OutgoingMonth != value.OutgoingMonth) changes["outgoingMonth"] = JsonValue.Create(value.OutgoingMonth?.ToString(CultureInfo.InvariantCulture) ?? "");
        if (before.SignatureOffset != value.SignatureOffset) changes["signatureOffset"] = JsonValue.Create(value.SignatureOffset);
        if (value.Number != before.Number) common["fixedNumber"] = value.Number;
        if ((value.FoodContractReference ?? "") != (before.FoodContractReference ?? "")) common["foodContractReference"] = value.FoodContractReference ?? "";
        var body = string.IsNullOrWhiteSpace(value.BodyTemplate) ? DefaultBody(id) : value.BodyTemplate;
        if (body != (string.IsNullOrWhiteSpace(before.BodyTemplate) ? DefaultBody(id) : before.BodyTemplate)) Object(individual, id)["bodyTemplate"] = body;
        if (commonFields)
        {
            // لا نغيّر متن بقية الكتب أو تخصيص حساباتها. رفض بنية غير معروفة يمنع فقدان البيانات.
            foreach (var item in individual) if (item.Value is not JsonObject) throw new InvalidDataException("بنية أحد تخصيصات الكتب غير مدعومة.");
            foreach (var pair in changes) { common[pair.Key] = pair.Value?.DeepClone(); foreach (var item in individual) ((JsonObject)item.Value!).Remove(pair.Key); }
        }
        else if (changes.Count > 0) { var item = Object(individual, id); foreach (var pair in changes) item[pair.Key] = pair.Value?.DeepClone(); }
        return DataPlist(JsonSerializer.SerializeToUtf8Bytes(root));
    }
    public static byte[] ReferencePlist(byte[] archive) { _ = LetterReferencePack.ReadArchive(archive); return DataPlist(archive); }
    public static byte[] FontPlist(byte[] font) { GroupLetterFont.Validate(font); return DataPlist(font); }
}
