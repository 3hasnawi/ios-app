using System.Text.Json;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace SalaryDesktop.Core;

public sealed record SalaryReviewRow(string Kind, string Name, string Detail, decimal? Before, decimal? After);
public sealed record SalaryReviewResult(string Month, string? PreviousMonth, int EmployeeCount, int ExcludedAmbiguous, IReadOnlyList<SalaryReviewRow> Rows, IReadOnlyList<string> Notes);

/// مقارنة مبدئية لا تفترض أن الاسمين المتطابقين يعودان للشخص نفسه عند وجود تكرار.
public static class SalaryReview
{
    private static string Key(string text) => PayrollAnalysis.Normalize(text).Replace(" ", "", StringComparison.Ordinal);
    private static Dictionary<string, string> Details(Employee e)
    {
        try { return e.Details is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(e.Details) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة بيانات أحد الرواتب للتدقيق.", ex); }
    }
    private static string Identifier(string value)
    {
        var digits = string.Concat(value.Select(c => CharUnicodeInfo.GetDecimalDigitValue(c)).Where(n => n >= 0).Select(n => (char)('0' + n))).TrimStart('0');
        return digits;
    }
    private static Dictionary<string, decimal> Items(Employee e)
    {
        try { return e.Items is null ? new() : JsonSerializer.Deserialize<Dictionary<string, decimal>>(e.Items) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة بنود أحد الرواتب للتدقيق.", ex); }
    }
    private static decimal? Value(Dictionary<string, string> details, Dictionary<string, decimal> items, string title)
    {
        var key = PayrollAnalysis.Normalize(title);
        foreach (var (name, text) in details)
        {
            if (PayrollAnalysis.Normalize(name) != key || string.IsNullOrWhiteSpace(text)) continue;
            var normalized = TransferPreparation.NormalizeAccount(text).Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal).Replace('٫', '.');
            return decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        foreach (var (name, value) in items) if (PayrollAnalysis.Normalize(name) == key) return value;
        return null;
    }
    private static string Rank(string value)
    {
        var s = PayrollAnalysis.Normalize(TransferPreparation.NormalizeAccount(value));
        foreach (var (word, digit) in new[] { ("الاول", "1"), ("اول", "1"), ("الثاني", "2"), ("ثاني", "2"), ("الثالث", "3"), ("ثالث", "3"), ("الرابع", "4"), ("رابع", "4"), ("الخامس", "5"), ("خامس", "5") }) s = s.Replace(word, digit, StringComparison.Ordinal);
        return new string(s.Where(c => !char.IsWhiteSpace(c) && c is not ('.' or '/' or '-')).ToArray());
    }
    /// النتيجة محفوظة لكل نسخة بيانات وشهر وقائمة ملاك (المرجع نفسه)، فالرئيسية وفتح التدقيق لا يعيدان الحساب.
    /// أي تعديل ينشئ BackupSnapshot جديدة فتُحسب من جديد.
    private static readonly ConditionalWeakTable<BackupSnapshot, Dictionary<string, (IReadOnlyList<StaffMember>? Staff, SalaryReviewResult Result)>> Cache = new();
    public static SalaryReviewResult Prepare(BackupSnapshot source, string month, IReadOnlyList<StaffMember>? staff = null)
    {
        var cache = Cache.GetOrCreateValue(source);
        lock (cache) if (cache.TryGetValue(month, out var hit) && ReferenceEquals(hit.Staff, staff)) return hit.Result;
        var result = Compute(source, month, staff);
        lock (cache) cache[month] = (staff, result);
        return result;
    }
    private static SalaryReviewResult Compute(BackupSnapshot source, string month, IReadOnlyList<StaffMember>? staff)
    {
        var key = MonthNames.SortKey(month);
        if (key == 0 || !source.Months.Any(m => m.Name == month)) throw new InvalidDataException("اختر شهراً محفوظاً صالحاً للتدقيق.");
        var current = source.Employees.Where(e => e.Month == month).ToArray();
        if (current.Length == 0) throw new InvalidDataException("لا توجد رواتب للشهر المختار.");
        if (current.Any(e => string.IsNullOrWhiteSpace(Key(e.Name)))) throw new InvalidDataException("يوجد راتب بلا اسم؛ راجع البيانات قبل التدقيق.");
        var rows = new List<SalaryReviewRow>(); var notes = new List<string>();
        var currentGroups = current.GroupBy(e => Key(e.Name)).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var group in currentGroups.Values.Where(g => g.Length > 1)) rows.Add(new("أسماء مكررة", group[0].Name, $"الاسم موجود {group.Length} مرات؛ تحقق من الهوية.", null, null));
        foreach (var person in current.Where(e => e.Net is null)) rows.Add(new("صافي ناقص", person.Name, "صافي الراتب غير موجود.", null, null));
        var details = current.Select(e => (Employee: e, Data: Details(e))).ToArray();
        string Iban(Dictionary<string, string> d) => TransferPreparation.NormalizeAccount(d.GetValueOrDefault("IBAN") ?? "");
        var withIban = details.Where(x => Iban(x.Data).Length > 0).ToArray();
        foreach (var row in withIban.Where(x => !TransferPreparation.ValidIban(Iban(x.Data)))) rows.Add(new("IBAN يحتاج مراجعة", row.Employee.Name, "رقم الحساب غير صالح وفق فحص IBAN العراقي.", null, row.Employee.Net));
        foreach (var group in withIban.GroupBy(x => Iban(x.Data)).Where(g => g.Select(x => Key(x.Employee.Name)).Distinct(StringComparer.Ordinal).Count() > 1))
            foreach (var row in group) rows.Add(new("IBAN مكرر", row.Employee.Name, "رقم الحساب مستخدم لأكثر من اسم؛ راجع الهوية.", null, row.Employee.Net));
        if (withIban.Length > 0 && withIban.Length * 2 >= current.Length)
            foreach (var row in details.Where(x => Iban(x.Data).Length == 0)) rows.Add(new("IBAN فارغ", row.Employee.Name, "رقم الحساب غير موجود في الراتب.", null, row.Employee.Net));
        if (withIban.Length == 0) notes.Add("لا توجد أرقام IBAN ضمن تفاصيل الرواتب؛ لم يُجرَ فحص الحسابات.");
        foreach (var label in new[] { "الرقم الوظيفي", "الرقم الموحد" })
        {
            var shared = details.Select(x => (x.Employee, Number: Identifier(x.Data.GetValueOrDefault(label) ?? ""))).Where(x => x.Number.Length > 0)
                .GroupBy(x => x.Number).Where(g => g.Select(x => Key(x.Employee.Name)).Distinct(StringComparer.Ordinal).Count() > 1);
            foreach (var group in shared) foreach (var person in group) rows.Add(new("رقم مكرر", person.Employee.Name, label + " مستخدم لأكثر من اسم؛ راجع الهوية.", null, person.Employee.Net));
        }
        var calculations = new List<SalaryReviewRow>(); int calculated = 0, healthFound = 0;
        foreach (var (employee, data) in details)
        {
            var items = Items(employee); var entitlement = Value(data, items, "الاستحقاق"); var deductions = Value(data, items, "مجموع الاستقطاعات");
            if (entitlement is not null && deductions is not null && employee.Net is not null)
            {
                calculated++; var expected = entitlement.Value - deductions.Value;
                if (Math.Abs(expected - employee.Net.Value) >= 1) calculations.Add(new("حساب الصافي", employee.Name, "الاستحقاق ناقص الاستقطاعات لا يساوي الصافي.", expected, employee.Net));
            }
            var health = Value(data, items, "الرعاية الصحية");
            if (health is not null) { healthFound++; if (health != 0 && health % 3500 != 0) rows.Add(new("الرعاية الصحية", employee.Name, "المبلغ لا يقبل القسمة على ٣٬٥٠٠؛ راجع الاستقطاع.", null, health)); }
        }
        if (calculated == 0) notes.Add("لم تتوفر أعمدة كافية لفحص حساب الصافي.");
        else if ((decimal)calculations.Count / calculated >= .3m) notes.Add("لم يُعتمد فحص حساب الصافي لأن عدداً كبيراً من الصفوف لا يطابق الأعمدة؛ راجع تنسيق الملف.");
        else rows.AddRange(calculations);
        if (healthFound == 0) notes.Add("لم يُعثر على بند الرعاية الصحية لفحصه.");
        var activeDeductions = source.Deductions.Where(d => d.Month == month && !PayrollAnalysis.IsInactive(d)).ToArray();
        foreach (var deduction in activeDeductions.Where(d => !currentGroups.ContainsKey(Key(d.Name)))) rows.Add(new("استقطاع بلا راتب", deduction.Name, deduction.Title, null, deduction.Amount));
        if (activeDeductions.Length == 0) notes.Add("لا توجد استقطاعات فعالة لفحص مطابقتها مع الرواتب.");
        else
        {
            var bank = BankStatementCalculator.Calculate(source, month);
            if (bank.MissingExecutionAmounts > 0) notes.Add("توجد مبالغ استقطاع ناقصة؛ لم تُعتمد مطابقة المجموع.");
            else if (bank.BankDeductions > 0 && Math.Abs(bank.ExecutionDifference) >= 1) rows.Add(new("مجموع الاستقطاعات", "مجموع الشهر", "مجموع الجهات لا يطابق استقطاعات المصارف في الرواتب.", bank.BankDeductions, bank.ExecutionDeductions));
            else if (bank.BankDeductions == 0) notes.Add("لم يتوفر مجموع استقطاعات المصارف للمقارنة مع الجهات.");
        }
        var previousKey = MonthNames.Previous(month); var previousMatches = source.Months.Where(m => MonthNames.SortKey(m.Name) == previousKey).Select(m => m.Name).ToArray();
        string? previous = previousMatches.Length == 1 ? previousMatches[0] : null; int ambiguous = 0;
        if (previous is null) notes.Add(previousMatches.Length > 1 ? "توجد أكثر من تسمية للشهر السابق؛ لم تُجرَ المقارنة." : "الشهر السابق غير محفوظ؛ لم تُجرَ المقارنة بين الشهرين.");
        else
        {
            var old = source.Employees.Where(e => e.Month == previous).ToArray();
            if (old.Length == 0) notes.Add("لا توجد رواتب في الشهر السابق؛ لم تُجرَ المقارنة بين الشهرين.");
            else
            {
                var oldGroups = old.GroupBy(e => Key(e.Name)).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
                var ambiguousKeys = oldGroups.Where(x => x.Value.Length != 1).Select(x => x.Key).Concat(currentGroups.Where(x => x.Value.Length != 1).Select(x => x.Key)).ToHashSet(StringComparer.Ordinal);
                ambiguous = ambiguousKeys.Count;
                foreach (var (personKey, list) in currentGroups.Where(p => !ambiguousKeys.Contains(p.Key)))
                {
                    var after = list[0];
                    if (!oldGroups.TryGetValue(personKey, out var oldList)) { rows.Add(new("جديد في الرواتب", after.Name, after.Workplace, null, after.Net)); continue; }
                    var before = oldList[0];
                    if (before.Net is not null && after.Net is not null && before.Net != after.Net) rows.Add(new("تغيّر الصافي", after.Name, "راجع سبب تغير صافي الراتب.", before.Net, after.Net));
                    if (!string.IsNullOrWhiteSpace(before.Rank) && !string.IsNullOrWhiteSpace(after.Rank) && Rank(before.Rank) != Rank(after.Rank)) rows.Add(new("تغيّرت الرتبة", after.Name, before.Rank + " ← " + after.Rank, before.Net, after.Net));
                    if (!string.IsNullOrWhiteSpace(before.Workplace) && !string.IsNullOrWhiteSpace(after.Workplace) && PayrollAnalysis.Normalize(before.Workplace) != PayrollAnalysis.Normalize(after.Workplace)) rows.Add(new("تغيّر محل العمل", after.Name, before.Workplace + " ← " + after.Workplace, before.Net, after.Net));
                    var oldIban = Iban(Details(before)); var newIban = Iban(Details(after));
                    if (oldIban.Length > 0 && newIban.Length > 0 && oldIban != newIban) rows.Add(new("تغيّر الحساب", after.Name, "تغيّر IBAN بين الشهرين؛ راجع بيانات الموظف.", before.Net, after.Net));
                }
                foreach (var (personKey, list) in oldGroups.Where(p => !ambiguousKeys.Contains(p.Key) && !currentGroups.ContainsKey(p.Key))) rows.Add(new("خارج من الرواتب", list[0].Name, list[0].Workplace, list[0].Net, null));
                if (ambiguous > 0) notes.Add($"استُبعد {ambiguous} اسم مكرر من مقارنة الشهرين حتى لا تُربط هويات مختلفة خطأً.");
            }
        }
        if (staff is not null && staff.Count != source.StaffCount) throw new InvalidDataException("عدد سجلات الملاك لا يطابق النسخة؛ لم تُجرَ المطابقة.");
        if (staff is null || staff.Count == 0) notes.Add("لا توجد سجلات ملاك متاحة؛ لم تُجرَ مطابقة الملاك.");
        else
        {
            var indexed = staff.Select((member, index) => (member, index)).ToArray();
            var staffNames = indexed.GroupBy(x => Key(x.member.Name)).ToDictionary(g => g.Key, g => g.Select(x => x.index).ToArray(), StringComparer.Ordinal);
            var staffIds = indexed.Where(x => Identifier(x.member.Unified).Length > 0).GroupBy(x => Identifier(x.member.Unified)).ToDictionary(g => g.Key, g => g.Select(x => x.index).ToArray(), StringComparer.Ordinal);
            var payrollIds = details.Select(x => (x.Employee, Id: Identifier(x.Data.GetValueOrDefault("الرقم الموحد") ?? ""))).Where(x => x.Id.Length > 0).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var uncertain = staffNames.Where(p => p.Key.Length == 0 || p.Value.Length > 1).Select(p => p.Value).Concat(staffIds.Values.Where(v => v.Length > 1)).SelectMany(v => v).ToHashSet();
            var candidates = new List<(Employee Employee, int StaffIndex)>(); int skipped = 0;
            foreach (var (employee, info) in details)
            {
                var personKey = Key(employee.Name); var unified = Identifier(info.GetValueOrDefault("الرقم الموحد") ?? "");
                var byName = staffNames.GetValueOrDefault(personKey) ?? []; var byId = unified.Length == 0 ? [] : staffIds.GetValueOrDefault(unified) ?? [];
                if (currentGroups[personKey].Length > 1 || (unified.Length > 0 && payrollIds.GetValueOrDefault(unified) > 1) || byId.Length > 1 || (byId.Length == 0 && byName.Length > 1))
                { foreach (var index in byName.Concat(byId)) uncertain.Add(index); skipped++; continue; }
                var match = byId.Length == 1 ? byId[0] : byName.Length == 1 ? byName[0] : -1;
                if (match < 0) { rows.Add(new("راتب بلا ملاك", employee.Name, employee.Workplace, null, employee.Net)); continue; }
                var staffId = Identifier(staff[match].Unified);
                if (byId.Length == 0 && unified.Length > 0 && staffId.Length > 0 && unified != staffId)
                { uncertain.Add(match); rows.Add(new("اختلاف الرقم الموحد", employee.Name, "الرقم الموحد في الرواتب لا يطابق سجل الملاك؛ راجع الهوية.", null, employee.Net)); continue; }
                if (uncertain.Contains(match)) { skipped++; continue; }
                candidates.Add((employee, match));
            }
            var matched = new HashSet<int>();
            foreach (var group in candidates.GroupBy(c => c.StaffIndex))
            {
                if (group.Count() > 1) { uncertain.Add(group.Key); skipped += group.Count(); continue; }
                var employee = group.Single().Employee; var member = staff[group.Key]; matched.Add(group.Key);
                if (!string.IsNullOrWhiteSpace(employee.Rank) && !string.IsNullOrWhiteSpace(member.Rank) && Rank(employee.Rank) != Rank(member.Rank))
                    rows.Add(new("رتبة مختلفة عن الملاك", employee.Name, "الرواتب: " + employee.Rank + " · الملاك: " + member.Rank, null, employee.Net));
            }
            string[] inactive = ["تقاعد", "احاله", "وفاه", "متوفي", "شهيد", "استشهد", "فصل", "مفصول", "استقاله", "مستقيل", "ترقين", "طي قيد", "منقول", "نقل خارج", "انهاء خدمه", "منتهي"];
            foreach (var (member, index) in indexed)
            {
                var status = PayrollAnalysis.Normalize(member.Status);
                if (!matched.Contains(index) && !uncertain.Contains(index) && !inactive.Any(status.Contains)) rows.Add(new("ملاك بلا راتب", member.Name, string.Join(" · ", new[] { member.Rank, member.Status, member.Workplace }.Where(x => !string.IsNullOrWhiteSpace(x))), null, null));
            }
            if (skipped > 0 || uncertain.Count > 0) notes.Add($"توجد {skipped:N0} مطابقة راتب و{uncertain.Count:N0} سجل ملاك غير محسوم بسبب تكرار الاسم أو الرقم؛ راجع الهوية يدوياً.");
        }
        notes.Add("هذه فحوص أولية؛ بعض تفاصيل التدقيق لم تكتمل بعد.");
        return new(month, previous, current.Length, ambiguous, rows.OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray(), notes);
    }
}
