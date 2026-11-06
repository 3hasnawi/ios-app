using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Globalization;
using System.Xml.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

public sealed partial class LocalPayrollStore
{
    public LocalStaffSnapshot GetLocalStaff()
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: true);
        string revision = (string)Scalar(c, t, "SELECT current FROM metadata");
        var staff = ReadList<StaffMember>(c, t, CurrentAuxiliaryState(c, t), "staff");
        var rows = staff.Select((s, i) => new LocalStaffRow(i, LocalStaff.Fingerprint(s), s)).ToArray();
        t.Commit(); return new(revision, rows);
    }
    public MutationResult ImportReviewedStaff(LocalStaffImportPreview preview, bool replaceExisting)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Problems.Count != 0 || preview.Members.Count is 0 or > 500_000) throw new InvalidDataException("راجع أخطاء الملاك؛ لا يمكن حفظ استيراد ناقص أو فارغ.");
        LocalStaff.VerifySource(preview);
        // إبقاء الملف مفتوحاً يمنع تغييره على ويندوز بين الفحص وحفظ المعاملة.
        using var source = new FileStream(preview.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(source)) != preview.SourceDigest) throw new InvalidDataException("تغيّر ملف الملاك بعد المراجعة.");
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); CheckCurrent(c, t, preview.ExpectedRevision);
        var state = CurrentAuxiliaryState(c, t);
        if (state.ContainsKey("staff") && !replaceExisting) throw new InvalidDataException("يلزم تأكيد استبدال الملاك الحالي كاملاً؛ الدمج غير متاح.");
        var staff = preview.Members.Select(s => s with { Details = s.Details?.ToArray() }).ToArray(); foreach (var member in staff) CheckStaff(member);
        if (LocalStaff.Fingerprint(staff) != preview.ContentDigest) throw new InvalidDataException("تغيّرت المعاينة؛ أعد استيراد الملف.");
        // بيانات مجموعة قديمة لا تُربط بأسماء مجموعة جديدة؛ تبقى محفوظة في المراجعة السابقة.
        var attachments = ReadSet(c, t, state.GetValueOrDefault("attachments"));
        RewriteStaffExtras(c, t, attachments, [], []);
        string? previousStaff = state.GetValueOrDefault("staff");
        SaveList(c, t, state, "staff", staff); SetArea(c, t, state, "attachments", attachments);
        if (previousStaff != state.GetValueOrDefault("staff")) UpdateLocalStaffInfo(c, t, state, staff, Path.GetFileNameWithoutExtension(preview.SourcePath));
        source.Position = 0;
        if (Convert.ToHexString(SHA256.HashData(source)) != preview.SourceDigest) throw new InvalidDataException("تغيّر ملف الملاك أثناء الحفظ؛ لم تُحفظ بيانات جزئية.");
        LocalStaff.VerifySource(preview);
        var result = SaveAuxiliaryRevision(c, t, state, "استيراد الملاك: " + Path.GetFileName(preview.SourcePath) + " • " + preview.TableName, preview.SourceDigest, preview.ExpectedRevision);
        if (result.Changed) t.Commit(); return result;
    }
    public MutationResult AddStaffMember(StaffMember member, string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(member);
        var clean = LocalStaff.Edit(new("", "", "", "", "", "", "", null), member.Name, member.Mother, member.Stat, member.Unified, member.Rank, member.Status, member.Workplace);
        if (member.Details is { Length: > 0 }) throw new InvalidDataException("إضافة تفاصيل خام غير مدعومة؛ استخدم استيراد Access للاحتفاظ بالمصدر.");
        return MutateStaff(null, clean with { Details = clean.Details ?? "[]"u8.ToArray() }, expectedRevision);
    }
    public MutationResult EditStaffMember(LocalStaffRow target, StaffMember values, string expectedRevision, string? motherDisplayName = null) => MutateStaff(target, values, expectedRevision, motherDisplayName);
    public MutationResult DeleteStaffMember(LocalStaffRow target, string expectedRevision) => MutateStaff(target, null, expectedRevision);
    private MutationResult MutateStaff(LocalStaffRow? target, StaffMember? values, string expected, string? motherDisplayName = null)
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); CheckCurrent(c, t, expected);
        var state = CurrentAuxiliaryState(c, t); var before = ReadList<StaffMember>(c, t, state, "staff"); var rows = before.ToList();
        var links = Enumerable.Range(0, rows.Count).Select(i => (int?)i).ToList();
        StaffMember? old = null; StaffMember? next = values;
        if (target is not null)
        {
            if (target.Position < 0 || target.Position >= rows.Count || LocalStaff.Fingerprint(rows[target.Position]) != target.Fingerprint) throw new InvalidDataException("تغيّر سجل الملاك المحدد؛ أعد فتح الصفحة.");
            old = rows[target.Position];
            if (values is null) { rows.RemoveAt(target.Position); links.RemoveAt(target.Position); }
            else
            {
                next = LocalStaff.Edit(old, values.Name, motherDisplayName ?? values.Mother, values.Stat, values.Unified, values.Rank, values.Status, values.Workplace, motherDisplayName is not null);
                rows[target.Position] = next;
            }
        }
        else
        {
            if (next is null || rows.Count >= 500_000) throw new InvalidDataException("عدد سجلات الملاك غير مدعوم.");
            rows.Add(next); links.Add(null);
        }
        if (next is not null)
        {
            CheckStaff(next);
            var others = rows.Where((_, i) => i != (target?.Position ?? rows.Count - 1));
            if ((!string.IsNullOrWhiteSpace(next.Stat) && next.Stat != old?.Stat && others.Any(s => LocalStaff.IdentityKey(s.Stat) == LocalStaff.IdentityKey(next.Stat))) ||
                (!string.IsNullOrWhiteSpace(next.Unified) && next.Unified != old?.Unified && others.Any(s => LocalStaff.IdentityKey(s.Unified) == LocalStaff.IdentityKey(next.Unified))))
                throw new InvalidDataException("الرقم الإحصائي أو الموحد مستخدم في سجل آخر؛ راجع تعارض الهوية.");
        }
        var attachments = ReadSet(c, t, state.GetValueOrDefault("attachments"));
        RewriteStaffExtras(c, t, attachments, before, rows.Select((s, i) => (s, links[i])).ToArray());
        string? previousStaff = state.GetValueOrDefault("staff");
        SaveList(c, t, state, "staff", rows); SetArea(c, t, state, "attachments", attachments);
        if (previousStaff != state.GetValueOrDefault("staff")) UpdateLocalStaffInfo(c, t, state, rows, null);
        var result = SaveAuxiliaryRevision(c, t, state, target is null ? "إضافة سجل ملاك محلي" : next is null ? "حذف سجل ملاك من البيانات الحالية" : "تعديل سجل ملاك محلي", Hash(Encode(state)), expected);
        if (result.Changed) t.Commit(); return result;
    }
    private static void UpdateLocalStaffInfo(SqliteConnection c, SqliteTransaction t, SortedDictionary<string, string> state, IReadOnlyList<StaffMember> staff, string? importedFile)
    {
        var settings = ReadSet(c, t, state.GetValueOrDefault("settings"));
        void Put(string key, XElement value) => settings[key] = SaveValue(c, t, Encoding.UTF8.GetBytes(new XElement("plist", new XAttribute("version", "1.0"), value).ToString(SaveOptions.DisableFormatting)));
        if (staff.Count == 0)
        {
            foreach (var key in new[] { "staffCount", "staffImportedAt", "staffFileName", "staffStatuses" }) settings.Remove(key);
        }
        else
        {
            Put("staffCount", new XElement("integer", staff.Count.ToString(CultureInfo.InvariantCulture)));
            Put("staffStatuses", new XElement("array", staff.Where(s => !string.IsNullOrWhiteSpace(s.Status)).GroupBy(s => s.Status, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => new XElement("string", g.Key))));
            if (importedFile is not null)
            {
                Put("staffFileName", new XElement("string", importedFile));
                Put("staffImportedAt", new XElement("date", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
            }
        }
        SetArea(c, t, state, "settings", settings);
    }
    private static void RewriteStaffExtras(SqliteConnection c, SqliteTransaction t, SortedDictionary<string, string> attachments, IReadOnlyList<StaffMember> before, IReadOnlyList<(StaffMember Member, int? Original)> after)
    {
        const string key = "interop/metadata.json"; if (!attachments.TryGetValue(key, out var id)) return;
        try
        {
            var metadata = JsonNode.Parse(ReadValue(c, t, id))?.AsObject() ?? throw new InvalidDataException("بيانات التبادل غير صالحة.");
            if (metadata["extraFields"] is not JsonObject all) throw new InvalidDataException("بيانات الحقول الإضافية غير صالحة.");
            if (all["staff.jsonl"] is null) return;
            if (all["staff.jsonl"] is not JsonObject extras) throw new InvalidDataException("بيانات حقول الملاك الإضافية غير صالحة.");
            if (extras.Count == 0) return;
            var oldExtras = new JsonObject[before.Count]; var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var counts = before.GroupBy(LocalStaff.Fingerprint).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            if (before.Count > 0 && extras.Any(e => e.Value is not JsonObject obj || obj["count"]?.GetValue<int>() != counts.GetValueOrDefault(e.Key))) throw new InvalidDataException("حقول الملاك الإضافية لا تطابق عدد السجلات؛ لا يمكن تعديلها بأمان.");
            for (int i = 0; i < before.Count; i++)
            {
                string hash = LocalStaff.Fingerprint(before[i]); int ordinal = positions.GetValueOrDefault(hash); positions[hash] = ordinal + 1;
                if (extras[hash] is not JsonObject item) { oldExtras[i] = new(); continue; }
                if (item["values"] is not JsonArray values || values.Count != counts[hash] || values[ordinal] is not JsonObject value) throw new InvalidDataException("تعارض في ربط حقول الملاك الإضافية.");
                oldExtras[i] = (JsonObject)value.DeepClone();
            }
            var groups = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
            foreach (var row in after)
            {
                string hash = LocalStaff.Fingerprint(row.Member); if (!groups.TryGetValue(hash, out var list)) groups[hash] = list = [];
                list.Add(row.Original is { } position ? oldExtras[position] : new JsonObject());
            }
            var updated = new JsonObject();
            foreach (var group in groups.Where(g => g.Value.Any(v => v.Count > 0))) updated.Add(group.Key, new JsonObject { ["count"] = group.Value.Count, ["values"] = new JsonArray(group.Value.Select(v => (JsonNode?)v).ToArray()) });
            all["staff.jsonl"] = updated;
            attachments[key] = SaveValue(c, t, JsonSerializer.SerializeToUtf8Bytes(metadata, LocalStaff.Json));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        { throw new InvalidDataException("تعذّر الحفاظ على الحقول الإضافية للملاك؛ لم تتغير البيانات.", ex); }
    }
}
