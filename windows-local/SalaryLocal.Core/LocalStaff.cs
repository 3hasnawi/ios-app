using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

public sealed record LocalStaffRow(int Position, string Fingerprint, StaffMember Member);
public sealed record LocalStaffSnapshot(string RevisionId, IReadOnlyList<LocalStaffRow> Rows);
public sealed record LocalStaffColumn(string Name, byte Type);
public sealed record LocalStaffTable(string Name, IReadOnlyList<LocalStaffColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, int DeclaredRowCount, bool IsComplete);
public sealed record LocalStaffMapping(string Source, string Destination);
public sealed record LocalStaffProblem(int Row, string Message);

public sealed class LocalStaffImportPreview
{
    public string SourcePath { get; }
    public string SourceDigest { get; }
    public string TableName { get; }
    public string ExpectedRevision { get; }
    public IReadOnlyList<StaffMember> Members { get; }
    public IReadOnlyList<LocalStaffMapping> Mappings { get; }
    public IReadOnlyList<LocalStaffProblem> Problems { get; }
    public IReadOnlyList<string> Warnings { get; }
    internal string ContentDigest { get; }
    internal LocalStaffImportPreview(string path, string sourceDigest, string table, string revision, IReadOnlyList<StaffMember> members, IReadOnlyList<LocalStaffMapping> mappings, IReadOnlyList<LocalStaffProblem> problems, IReadOnlyList<string> warnings)
    {
        SourcePath = Path.GetFullPath(path); SourceDigest = sourceDigest; TableName = table; ExpectedRevision = revision;
        Members = members; Mappings = mappings; Problems = problems; Warnings = warnings;
        ContentDigest = LocalStaff.Fingerprint(Members);
    }
}

public static class LocalStaff
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 64 };
    public static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    public static string FileDigest(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 1_500_000_000) throw new InvalidDataException("حجم قاعدة الملاك غير مدعوم.");
        return Convert.ToHexString(SHA256.HashData(file));
    }
    public static void VerifySource(LocalStaffImportPreview preview)
    {
        if (FileDigest(preview.SourcePath) != preview.SourceDigest) throw new InvalidDataException("تغيّر ملف Access منذ المراجعة؛ أعد اختياره ومراجعته.");
        if (Fingerprint(preview.Members) != preview.ContentDigest) throw new InvalidDataException("تغيّرت بيانات معاينة الملاك؛ أعد المراجعة.");
    }
    private static string Key(string value) => PayrollAnalysis.Normalize(value);
    public static string IdentityKey(string value) => string.Concat(Key(value).Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.DecimalDigitNumber ? (char)('0' + (int)char.GetNumericValue(c)) : c));
    public static readonly IReadOnlyDictionary<string, string> LookupAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["محافظة السكن"] = "المحافظة", ["الوكالة او الهيئة"] = "الوكالة", ["المديرية العامة"] = "المديرية او الهيئة", ["المنصب الاداري"] = "المناصب", ["الجهة المنقول اليها"] = "الدائرة المنقول اليها", ["هل يعمل ضمن مقر المديرية"] = "هل يعمل ضمن المقر", ["الشهر"] = "شهر الترقية"
    };
    private static readonly HashSet<string> Hidden = ["رقم_معرف", "Imagepath", "Bookname"];
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["اسم الام الكامل"] = "اسم الأم", ["الشهر"] = "شهر الترقية", ["المحافظة"] = "محافظة العمل", ["رقم الامر"] = "رقم امر التعيين", ["تاريخ الامر"] = "تاريخ التعيين", ["اسباب ايقاف صرق الراتب"] = "أسباب إيقاف صرف الراتب", ["رتبة"] = "رتبة السائق", ["الاسم"] = "اسم السائق", ["اسم الكلية او المعهد"] = "الكلية او المعهد", ["نوع الشهادة عسكري او مدني"] = "نوع الشهادة", ["هل يعمل ضمن مقر المديرية"] = "يعمل ضمن المقر"
    };
    private static readonly (string Title, string[] Columns)[] Layout = [
        ("الوظيفية", ["صنف الوظيفة", "الصنف", "الرتبة", "العنوان الوظيفي", "جهة التعيين", "اشارة الركن", "الحالة الوظيفية", "حالة العمل", "حالة التعيين", "نوع التعيين", "المنصب الاداري", "المنصب", "رقم الامر الوزاري لاسناد المنصب", "تاريخ امر المنصب", "تاريخ المباشرة بالمنصب", "الصنف الاداري", "الصنف الرئيسي", "الصنف الفرعي", "طبيعة العمل", "ممارسة الاختصاص"]),
        ("الترقية", ["الشهر", "سنة الترقية", "رقم امر الترقية", "تاريخ الترقية"]),
        ("مكان العمل", ["المحافظة", "الوكالة او الهيئة", "المديرية العامة", "المديرية", "المديرية الفرعية", "القسم", "الشعبة", "هل يعمل ضمن مقر المديرية", "تاريخ المباشرة في الدائرة"]),
        ("التعيين والمباشرة", ["رقم الامر", "تاريخ الامر", "تاريخ مباشرة بالخدمة لاول مرة", "تاريخ مباشرة بالاعادة", "رقم امر الاعادة للخدمة", "تاريخ امر الاعادة للخدمة", "لديه تمديد", "مدة التمديد", "رقم امر التمديد", "تاريخ امر التمديد"]),
        ("المعلومات الشخصية", ["اسم الام الكامل", "التولد", "الجنس", "الحالة الزوجية", "اسم الزوجة/ الزوج", "عدد الاطفال", "القومية", "الديانة", "الحالة الصحية", "نسبة العجز", "من ذوي"]),
        ("التحصيل الدراسي", ["التحصيل الدراسي", "نوع الشهادة عسكري او مدني", "الجامعة", "اسم الكلية او المعهد", "تخصص الشهادة", "التخصص", "سنة التخرج", "الدورات", "دورات الضباط", "رقم الدورة"]),
        ("الأرقام", ["الرقم الاحصائي", "الرقم الموحد", "الرقم الوظيفي", "رقم الضمان", "رقم البطاقة الموحدة", "رقم البطاقة التموينية", "رقم الهوية العسكرية", "تاريخ اصدار الهوية العسكرية", "رقم الهاتف"]),
        ("السكن", ["محافظة السكن", "القضاء او الناحية", "محلة", "زقاق", "دار", "اقرب نقطة دالة"]),
        ("الراتب", ["صرف الراتب", "جهة تقاضي الراتب", "اسباب عدم صرف الراتب", "اسباب ايقاف صرق الراتب", "الوكالة حسب الملاك الحسابي", "المديرية حسب الملاك الحسابي", "التبويب المالي حسب الملاك الحسابي"]),
        ("معلومات سائق الضابط", ["صفة العمل", "رتبة", "الاسم"]),
    ];
    public static LocalStaffImportPreview Review(string sourcePath, string sourceDigest, string expectedRevision, LocalStaffTable table, IReadOnlyList<LocalStaffTable>? lookupTables = null, IReadOnlyList<LocalStaffProblem>? parserProblems = null)
    {
        var problems = (parserProblems ?? []).ToList(); var warnings = new List<string>(); var members = new List<StaffMember>();
        if (!table.IsComplete || table.Rows.Count != table.DeclaredRowCount) problems.Add(new(0, "لم تكتمل قراءة الجدول؛ يمنع حفظ استيراد جزئي."));
        if (table.Rows.Count is 0 or > 500_000) problems.Add(new(0, "جدول الملاك فارغ أو يتجاوز الحد المدعوم."));
        var columns = new Dictionary<string, LocalStaffColumn>(StringComparer.Ordinal);
        foreach (var column in table.Columns)
            if (string.IsNullOrWhiteSpace(column.Name) || !columns.TryAdd(Key(column.Name), column)) problems.Add(new(0, "عناوين أعمدة متعارضة بعد توحيد الكتابة؛ لا يمكن تحديد الحقول بأمان."));
        foreach (var column in table.Columns.Where(c => c.Type is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 10 or 12)))
            problems.Add(new(0, $"نوع العمود «{column.Name}» غير مدعوم ({column.Type})؛ لا تُسقط حقوله أثناء الاستيراد."));
        LocalStaffColumn? Find(string name) => columns.GetValueOrDefault(Key(name));
        if (Find("الاسم الكامل") is null) problems.Add(new(0, "عمود «الاسم الكامل» مطلوب؛ لا يُستخدم عمود «الاسم» الخاص بالسائق بدلاً منه."));
        var recognized = new (string Field, string[] Names)[] {
            ("الاسم", ["الاسم الكامل"]), ("الأم", ["اسم الام الكامل", "اسم الام", "اسم الوالدة"]),
            ("الإحصائي", ["الرقم الاحصائي"]), ("الموحد", ["الرقم الموحد"]), ("الرتبة", ["الرتبة"]),
            ("الحالة", ["الحالة الوظيفية"]), ("محل العمل", ["القسم", "المديرية"])
        };
        var mappings = recognized.SelectMany(r => r.Names.Select(Find).Where(c => c is not null).Select(c => new LocalStaffMapping(c!.Name, r.Field))).ToArray();
        var lookups = BuildLookups(table, lookupTables ?? [], problems, warnings);
        var placed = Layout.SelectMany(s => s.Columns).Concat(["الملاحظات", "تاريخ الادخال", "تاريخ التحديث"]).Select(Key).ToHashSet();
        var others = table.Columns.Where(c => !placed.Contains(Key(c.Name)) && !Hidden.Contains(c.Name) && Key(c.Name) != Key("الاسم الكامل")).Select(c => c.Name).ToArray();
        var originalIds = new HashSet<string>(StringComparer.Ordinal);
        if (problems.Count == 0)
        {
            for (int i = 0; i < table.Rows.Count; i++)
            {
                var row = table.Rows[i];
                if (row.Count != table.Columns.Count || table.Columns.Any(c => !row.ContainsKey(c.Name))) { problems.Add(new(i + 1, "عدد حقول الصف لا يطابق الأعمدة؛ لم يُحفظ أي سجل.")); continue; }
                string? Display(string column)
                {
                    var actual = Find(column); if (actual is null) return null;
                    var raw = row[actual.Name]; if (raw is null) return null;
                    if (lookups.TryGetValue(actual.Name, out var lookup) && lookup.TryGetValue(raw, out var mapped)) return mapped;
                    var text = raw.Trim();
                    if (actual.Type is 2 or 3 or 4 or 5 or 6 or 7 or 16 && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == 0) return null;
                    if (actual.Type == 1) return text is "true" or "True" or "1" ? "نعم" : null;
                    if (actual.Type == 8 && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)) return date.Year > 1900 ? date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) : null;
                    return text.Length == 0 ? null : text;
                }
                var name = Display("الاسم الكامل");
                if (string.IsNullOrWhiteSpace(name)) { problems.Add(new(i + 1, "صف بلا اسم كامل؛ لا يُتجاوز بصمت.")); continue; }
                var idColumn = Find("رقم_معرف");
                if (idColumn is not null && row[idColumn.Name] is { Length: > 0 } original && !originalIds.Add(original)) problems.Add(new(i + 1, "تكرار معرّف المصدر الداخلي؛ راجع الملف قبل الاستبدال."));
                var sections = new JsonArray();
                void Section(string title, IEnumerable<string> names)
                {
                    var fields = new JsonArray();
                    foreach (var column in names) if (Display(column) is { } value) fields.Add(new JsonObject { ["label"] = Labels.GetValueOrDefault(column, column), ["value"] = value });
                    if (fields.Count > 0) sections.Add(new JsonObject { ["title"] = title, ["fields"] = fields });
                }
                foreach (var section in Layout) Section(section.Title, section.Columns);
                Section("الأوامر ومعلومات أخرى", others); Section("الملاحظات", ["الملاحظات"]); Section("السجل", ["تاريخ الادخال", "تاريخ التحديث"]);
                // الترتيب محفوظ في المراجعة، لا داخل الصف، كي لا تتكرر بيانات الجميع عند إدراج موظف.
                // لقطة المصدر لا تستعمل كهوية ولا تُعدّل عند تعديل حقول العرض؛ تحفظ الفراغ والصفر والحقول المخفية.
                sections.Add(new JsonObject { ["title"] = "مصدر الاستيراد", ["fields"] = new JsonArray(new JsonObject { ["label"] = "الجدول", ["value"] = table.Name }),
                    ["localAccessSource"] = new JsonObject { ["version"] = 1, ["table"] = table.Name,
                        ["columns"] = new JsonArray(table.Columns.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["type"] = c.Type, ["value"] = row[c.Name] }).ToArray()) } });
                var details = JsonSerializer.SerializeToUtf8Bytes(sections, Json);
                if (details.Length > 2_000_000) { problems.Add(new(i + 1, "تفاصيل الصف تتجاوز الحد الآمن؛ لم تُحذف حقول لتصغيرها.")); continue; }
                string mother = Display("اسم الام الكامل") ?? Display("اسم الام") ?? table.Columns.Where(c => Key(c.Name).Contains(Key("اسم الام"), StringComparison.Ordinal) || Key(c.Name).Contains(Key("اسم الوالدة"), StringComparison.Ordinal)).Select(c => Display(c.Name)).FirstOrDefault(v => v is not null) ?? "";
                members.Add(new(name, Key(mother), Display("الرقم الاحصائي") ?? "", Display("الرقم الموحد") ?? "", Display("الرتبة") ?? "", Display("الحالة الوظيفية") ?? "", Display("القسم") ?? Display("المديرية") ?? "", details));
            }
        }
        foreach (var group in new[] { ("الإحصائي", members.Select(m => m.Stat)), ("الموحد", members.Select(m => m.Unified)) })
            if (group.Item2.Where(v => !string.IsNullOrWhiteSpace(v)).GroupBy(IdentityKey).Any(g => g.Count() > 1)) problems.Add(new(0, $"تكرار الرقم {group.Item1}؛ راجع تعارض الهوية قبل الحفظ."));
        if (members.GroupBy(m => Key(m.Name)).Any(g => g.Count() > 1)) warnings.Add("توجد أسماء متكررة. ستبقى صفوفاً مستقلة ولا يجري دمجها بالاسم.");
        warnings.Add("الاستيراد يستبدل الملاك كاملاً كما في الآيفون. لا يوجد دمج بالأسماء أو الأرقام؛ تبقى السجلات السابقة وحقولها في التاريخ المحلي.");
        if (members.Count != table.Rows.Count && problems.Count == 0) problems.Add(new(0, "عدد الموظفين لا يطابق عدد صفوف المصدر."));
        return new(sourcePath, sourceDigest, table.Name, expectedRevision, members.AsReadOnly(), mappings, problems.AsReadOnly(), warnings.AsReadOnly());
    }
    private static Dictionary<string, Dictionary<string, string>> BuildLookups(LocalStaffTable main, IReadOnlyList<LocalStaffTable> tables, List<LocalStaffProblem> problems, List<string> warnings)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var column in main.Columns.Where(c => c.Type is 2 or 3 or 4))
        {
            var tableName = LookupAliases.GetValueOrDefault(column.Name, column.Name);
            var matches = tables.Where(t => t.Name == tableName && t.Name != main.Name).ToArray(); if (matches.Length == 0) continue;
            if (matches.Length != 1 || !matches[0].IsComplete || matches[0].Rows.Count != matches[0].DeclaredRowCount) { problems.Add(new(0, "تعذّرت قراءة جدول القيم المرمّزة كاملاً: " + tableName)); continue; }
            var table = matches[0];
            if (table.Columns.Select(c => Key(c.Name)).Distinct(StringComparer.Ordinal).Count() != table.Columns.Count || table.Rows.Any(r => r.Count != table.Columns.Count || table.Columns.Any(c => !r.ContainsKey(c.Name))))
            { problems.Add(new(0, "حقول جدول القيم ناقصة أو متعارضة: " + tableName)); continue; }
            var texts = table.Columns.Where(c => c.Type is 10 or 12).ToArray();
            var textColumn = texts.FirstOrDefault(c => c.Name == tableName || c.Name == column.Name) ?? texts.FirstOrDefault();
            if (textColumn is null) { problems.Add(new(0, "جدول القيم بلا عمود نص: " + tableName)); continue; }
            var allUsed = main.Rows.Select(r => r.GetValueOrDefault(column.Name)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            // الصفر غير المعرّف يبقى فارغاً، لكنه يدخل مقارنة المعاني إذا عرّفه أحد المفاتيح المحتملة.
            var used = allUsed.Where(v => v != "0").ToArray();
            var candidates = new List<(Dictionary<string, string> Map, int Hits)>();
            foreach (var key in table.Columns.Where(c => c.Type is 2 or 3 or 4))
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal); bool conflict = false;
                foreach (var row in table.Rows)
                    if (row.GetValueOrDefault(key.Name) is { } id && row.GetValueOrDefault(textColumn.Name)?.Trim() is { Length: > 0 } text)
                    { if (map.TryGetValue(id, out var old) && old != text) conflict = true; else map[id] = text; }
                if (!conflict) candidates.Add((map, used.Count(v => map.ContainsKey(v!))));
            }
            var best = candidates.OrderByDescending(c => c.Hits).FirstOrDefault();
            if (best.Map is null || used.Length > 0 && best.Hits == 0) { problems.Add(new(0, "تعذّر ربط رموز العمود: " + column.Name)); continue; }
            if (candidates.Any(c => c.Hits == best.Hits && allUsed.Any(v => c.Map.GetValueOrDefault(v) != best.Map.GetValueOrDefault(v)))) { problems.Add(new(0, "أكثر من مفتاح محتمل في جدول القيم: " + tableName)); continue; }
            result[column.Name] = best.Map;
            if (used.Any(v => !best.Map.ContainsKey(v!))) problems.Add(new(0, "توجد رموز بلا قيمة في العمود: " + column.Name));
        }
        return result;
    }
    private static bool MotherLabel(string label)
    {
        var key = Key(label);
        return key.Contains(Key("اسم الام"), StringComparison.Ordinal) || key.Contains(Key("اسم الوالدة"), StringComparison.Ordinal);
    }
    public static string MotherName(StaffMember member)
    {
        try
        {
            var matches = StaffRecords.Sections(member).SelectMany(s => s.Fields).Where(f => MotherLabel(f.Label) && Key(f.Value) == Key(member.Mother)).ToArray();
            return matches.Length == 1 ? matches[0].Value : member.Mother;
        }
        catch (InvalidDataException) { return member.Mother; }
    }
    public static StaffMember Edit(StaffMember prior, string name, string mother, string stat, string unified, string rank, string status, string workplace, bool exactMotherSpelling = false)
    {
        string Text(string value, bool required = false)
        { var t = value.Trim(); if (required && t.Length == 0 || t.Length > 1000 || t.Any(char.IsControl)) throw new InvalidDataException("حقل الملاك فارغ أو طويل أو يحتوي محارف غير صالحة."); return t; }
        var motherName = Text(mother);
        // حقل النسخة هو مفتاح البحث نفسه في الآيفون؛ الكتابة الأصلية تبقى في التفاصيل.
        var next = prior with { Name = Text(name, true), Mother = Key(motherName), Stat = Text(stat), Unified = Text(unified), Rank = Text(rank), Status = Text(status), Workplace = Text(workplace) };
        bool motherRequested = exactMotherSpelling || motherName != prior.Mother;
        bool motherNeedsPreservation = prior.Mother != Key(prior.Mother);
        if (prior == next && !motherRequested) return prior;
        try
        {
            var sections = prior.Details is { Length: > 0 } ? JsonNode.Parse(prior.Details)?.AsArray() ?? throw new InvalidDataException("تفاصيل سجل الملاك غير صالحة؛ لم تُعدّل.") : new JsonArray();
            (string[] Labels, string Old, string New)[] edits = [ (new[] { "الاسم الكامل" }, prior.Name, next.Name), (new[] { "الرقم الاحصائي" }, prior.Stat, next.Stat), (new[] { "الرقم الموحد" }, prior.Unified, next.Unified), (new[] { "الرتبة" }, prior.Rank, next.Rank), (new[] { "الحالة الوظيفية" }, prior.Status, next.Status), (new[] { "القسم", "المديرية" }, prior.Workplace, next.Workplace) ];
            bool changed = false; var allFields = new List<JsonObject>();
            foreach (var section in sections)
            {
                if (section is not JsonObject obj || obj["fields"] is not JsonArray fields) throw new InvalidDataException("تفاصيل الملاك لا تسمح بالتعديل الآمن.");
                foreach (var field in fields)
                {
                    if (field is not JsonObject item || item["label"] is not JsonValue labelNode || !labelNode.TryGetValue<string>(out _) || item["value"] is not JsonValue valueNode || !valueNode.TryGetValue<string>(out _)) throw new InvalidDataException("تفاصيل الملاك غير صالحة.");
                    allFields.Add(item);
                }
            }
            if (motherRequested || next.Mother != Key(prior.Mother) || motherNeedsPreservation)
            {
                var namedFields = allFields.Where(f => MotherLabel(f["label"]!.GetValue<string>())).ToArray();
                var matches = namedFields.Where(f => Key(f["value"]!.GetValue<string>()) == Key(prior.Mother)).ToArray();
                if (matches.Length > 1 || matches.Length == 0 && namedFields.Length > 0)
                    throw new InvalidDataException("اسم الأم في التفاصيل متعارض أو ملتبس؛ راجع المصدر قبل تعديله.");
                if (matches.Length == 1)
                {
                    // التطبيع خاص بالأم فقط؛ لا يبدّل بقية الحقول أو لقطة Access الأصلية.
                    if (matches[0]["value"]!.GetValue<string>() != motherName && (motherRequested || next.Mother != Key(prior.Mother)))
                    { matches[0]["value"] = motherName; changed = true; }
                }
                else if (motherName.Length > 0)
                {
                    sections.Add(new JsonObject { ["title"] = "المعلومات الشخصية", ["fields"] = new JsonArray(new JsonObject { ["label"] = "اسم الأم", ["value"] = motherName }) });
                    changed = true;
                }
            }
            foreach (var edit in edits.Where(e => e.Old != e.New))
            {
                // محل العمل مشتق من القسم أولاً؛ تساوي نص المديرية لا يسمح بتعديل الاثنين.
                foreach (var label in edit.Labels)
                {
                    var matched = allFields.Where(f => Key(f["label"]!.GetValue<string>()) == Key(label) && f["value"]!.GetValue<string>() == edit.Old).ToArray();
                    if (matched.Length == 0) continue;
                    if (matched.Length != 1) throw new InvalidDataException("الحقل المكرر في تفاصيل الملاك يحتاج مراجعة المصدر قبل التعديل.");
                    matched[0]["value"] = edit.New; changed = true; break;
                }
            }
            return changed ? next with { Details = JsonSerializer.SerializeToUtf8Bytes(sections, Json) } : next;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { throw new InvalidDataException("تعذّر الحفاظ على تفاصيل الملاك؛ لم تُعدّل.", ex); }
    }
}
