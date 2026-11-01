using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record BankManualAmounts(decimal AccumulatedLeave, decimal EmployeeRewards, decimal SingleList)
{
    public decimal Total => AccumulatedLeave + EmployeeRewards + SingleList;
}
public sealed record BankSummary(string Month, decimal Cash, decimal Card, decimal Unclassified, int UnclassifiedCount,
    int MissingNet, decimal BankDeductions, decimal ExecutionDeductions, int ExecutionCount, int MissingExecutionAmounts,
    decimal ContractNominal, decimal ContractRetirement, int ContractCount, int MissingContractNominal,
    IReadOnlyDictionary<string, decimal> Breakdown, BankManualAmounts Manual, IReadOnlyList<string> Warnings, IReadOnlyList<string> TransferWarnings)
{
    public decimal Net => Cash + Card + Unclassified;
    public decimal ContractNominalShare => decimal.Truncate(ContractNominal * 12m / 100m);
    public decimal ContractContribution => ContractNominalShare + ContractRetirement;
    public decimal BankTotal => Net + BankDeductions + Breakdown.Values.Sum() + ContractContribution + Manual.Total;
    public decimal ExecutionDifference => BankDeductions - ExecutionDeductions;
    public bool ExecutionMatched => ExecutionCount > 0 && MissingExecutionAmounts == 0 && Math.Abs(ExecutionDifference) < 1m;
}

public static class BankStatementCalculator
{
    private sealed record RawColumns([property: JsonRequired] string[] Headers, [property: JsonRequired] string[] Values);
    private sealed record ManualData([property: JsonRequired] decimal AccumulatedLeave, [property: JsonRequired] decimal EmployeeRewards, [property: JsonRequired] decimal SingleList);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Labels = ["صندوق شهداء الشرطة", "الرعاية الصحية", "النشاط الرياضي", "الضريبة", "الحماية الاجتماعية", "شركة عراق الجوال"];
    private static readonly (string[] Keys, string Label)[] Sources = [
        (["صندوق الشهداء", "صندوق شهداء الشرطة"], "صندوق شهداء الشرطة"),
        (["الرعاية الصحية", "رعاية صحية"], "الرعاية الصحية"),
        (["نشاط رياضي", "النشاط الرياضي"], "النشاط الرياضي"),
        (["الضريبة", "ضريبة"], "الضريبة"),
        (["حماية الاجتماعية", "الحماية الاجتماعية"], "الحماية الاجتماعية")];

    // الأداء: كانت أسماء الأعمدة الثابتة تُوحَّد ملايين المرات لعشرات آلاف الموظفين؛ تُوحَّد الآن مرة واحدة،
    // وأسماء الأعمدة والرتب وطرق الدفع المتكررة تُحفظ موحّدة في ذاكرة مؤقتة.
    private static readonly ConcurrentDictionary<string, string> KeyCache = new(StringComparer.Ordinal);
    private static string Key(string text)
    {
        if (KeyCache.TryGetValue(text, out var cached)) return cached;
        var value = PayrollAnalysis.Normalize(text);
        if (KeyCache.Count < 50_000) KeyCache[text] = value;
        return value;
    }
    private static readonly string BankKey = PayrollAnalysis.Normalize("مجموع استقطاعات المصارف"), NominalKey = PayrollAnalysis.Normalize("الاسمي"),
        RetirementKey = PayrollAnalysis.Normalize("توقيفات تقاعدية"), RetirementAltKey = PayrollAnalysis.Normalize("التوقيفات التقاعدية"),
        FoodKey = PayrollAnalysis.Normalize("الجوال"), ContractsKey = PayrollAnalysis.Normalize("عقود");
    private static readonly HashSet<string> SourceKeys = Sources.SelectMany(s => s.Keys).Select(PayrollAnalysis.Normalize).ToHashSet(StringComparer.Ordinal);
    private static bool Tracked(string key) => key.Contains(FoodKey, StringComparison.Ordinal) || SourceKeys.Contains(key) || key == NominalKey || key == RetirementKey || key == RetirementAltKey || key == BankKey;
    /// النتيجة محفوظة لكل نسخة بيانات وشهر: أي تعديل ينشئ نسخة بيانات جديدة فيُعاد الحساب، والعودة إلى القسم لا تعيده.
    private static readonly ConditionalWeakTable<BackupSnapshot, ConcurrentDictionary<string, BankSummary>> Results = new();

    private static Dictionary<string, T> ReadObject<T>(byte[]? bytes, string name)
    {
        if (bytes is null) return new(StringComparer.Ordinal);
        try { return JsonSerializer.Deserialize<Dictionary<string, T>>(bytes, JsonOptions) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException($"تعذّر قراءة {name} من النسخة؛ أوقفت العملية.", ex); }
    }
    private static decimal? ParseAmount(string value)
    {
        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (char.IsWhiteSpace(c) || c is ',' or '٬') continue;
            if (c is >= '٠' and <= '٩') buffer.Append((char)('0' + c - '٠'));
            else if (c is >= '۰' and <= '۹') buffer.Append((char)('0' + c - '۰'));
            else buffer.Append(c == '٫' ? '.' : c);
        }
        return decimal.TryParse(buffer.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }
    private static decimal? FindAmount(Dictionary<string, string> details, Dictionary<string, decimal> items, string[] keys)
    {
        foreach (var key in keys)
        {
            if (details.TryGetValue(key, out var text) && ParseAmount(text) is { } direct) return direct;
            if (items.TryGetValue(key, out var value)) return value;
            var normalized = Key(key);
            foreach (var pair in details) if (Key(pair.Key) == normalized && ParseAmount(pair.Value) is { } parsed) return parsed;
            foreach (var pair in items) if (Key(pair.Key) == normalized) return pair.Value;
        }
        return null;
    }
    public static BankManualAmounts Manual(BackupSnapshot snapshot, string month)
    {
        if (!snapshot.SettingsPlists.TryGetValue("bankStatementManualAmounts", out var plist)) return new(0, 0, 0);
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData bytes) throw new InvalidDataException("المبالغ اليدوية ليست بالصيغة المتوقعة.");
            var all = JsonSerializer.Deserialize<Dictionary<string, ManualData>>(bytes.Bytes, JsonOptions) ?? throw new InvalidDataException("المبالغ اليدوية غير صالحة.");
            if (!all.TryGetValue(month, out var entry)) return new(0, 0, 0);
            if (entry.AccumulatedLeave is < 0 or > 9_007_199_254_740_991m || entry.EmployeeRewards is < 0 or > 9_007_199_254_740_991m || entry.SingleList is < 0 or > 9_007_199_254_740_991m) throw new InvalidDataException("أحد المبالغ اليدوية خارج الحدود المقبولة.");
            return new(entry.AccumulatedLeave, entry.EmployeeRewards, entry.SingleList);
        }
        catch (Exception ex) when (ex is not InvalidDataException) { throw new InvalidDataException("تعذّر قراءة المبالغ اليدوية؛ لم تُفترض صفراً.", ex); }
    }
    public static BankSummary Calculate(BackupSnapshot snapshot, string month) => Results.GetOrCreateValue(snapshot).GetOrAdd(month, m => Compute(snapshot, m));
    private static BankSummary Compute(BackupSnapshot snapshot, string month)
    {
        if (!snapshot.Months.Any(m => m.Name == month)) throw new InvalidDataException("الشهر غير موجود في النسخة.");
        var breakdown = Labels.ToDictionary(x => x, _ => 0m, StringComparer.Ordinal);
        decimal cash = 0, card = 0, unclassified = 0, bank = 0, nominal = 0, retirement = 0, execution = 0;
        int unclassifiedCount = 0, missingNet = 0, contractCount = 0, missingNominal = 0, executionCount = 0, missingExecution = 0;
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        var foodObserved = false;
        var peopleCount = 0;
        foreach (var employee in snapshot.Employees.Where(x => x.Month == month))
        {
            peopleCount++;
            var details = ReadObject<string>(employee.Details, "تفاصيل الراتب");
            var items = ReadObject<decimal>(employee.Items, "بنود الراتب");
            if (details.Any(p => p.Value is null)) throw new InvalidDataException("تحتوي تفاصيل الراتب على قيمة غير صالحة.");
            foreach (var key in details.Keys) observed.Add(Key(key));
            foreach (var key in items.Keys) observed.Add(Key(key));
            if (employee.RawColumns is { } rawBytes)
            {
                RawColumns raw;
                try { raw = JsonSerializer.Deserialize<RawColumns>(rawBytes, JsonOptions) ?? throw new JsonException(); }
                catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة أعمدة الراتب الأصلية.", ex); }
                if (raw.Headers is null || raw.Values is null || raw.Headers.Length != raw.Values.Length ||
                    raw.Headers.Any(x => x is null) || raw.Values.Any(x => x is null)) throw new InvalidDataException("أعمدة الراتب الأصلية غير صالحة.");
                for (int i = 0; i < raw.Headers.Length; i++)
                {
                    var key = Key(raw.Headers[i]);
                    observed.Add(key);
                    foodObserved |= key.Contains(FoodKey, StringComparison.Ordinal);
                    if (Tracked(key) && !string.IsNullOrWhiteSpace(raw.Values[i]))
                    {
                        var number = ParseAmount(raw.Values[i]);
                        if (number is null || number < 0 || number != decimal.Truncate(number.Value)) invalid.Add(raw.Headers[i]);
                    }
                }
            }
            foreach (var pair in details.Where(p => !string.IsNullOrWhiteSpace(p.Value)))
            {
                var label = Key(pair.Key);
                if (SourceKeys.Contains(label) || label == BankKey || label == NominalKey || label == RetirementKey || label == RetirementAltKey)
                {
                    var number = ParseAmount(pair.Value);
                    if (number is null || number < 0 || number != decimal.Truncate(number.Value)) invalid.Add(pair.Key);
                }
            }
            if (employee.Net is { } net)
            {
                var payment = Key(details.GetValueOrDefault("طريقة الدفع", ""));
                if (payment.Contains("بطاق", StringComparison.Ordinal) || payment.Contains("كارت", StringComparison.Ordinal) || payment.Contains("ماستر", StringComparison.Ordinal)) card += net;
                else if (payment.Contains("كاش", StringComparison.Ordinal) || payment.Contains("نقد", StringComparison.Ordinal)) cash += net;
                else { unclassified += net; unclassifiedCount++; }
            }
            else missingNet++;
            bank += FindAmount(details, items, ["مجموع استقطاعات المصارف"]) ?? 0m;
            if (Key(employee.Rank) == ContractsKey)
            {
                contractCount++;
                var value = FindAmount(details, items, ["الاسمي"]);
                if (value is null) missingNominal++; else nominal += value.Value;
                retirement += FindAmount(details, items, ["توقيفات تقاعدية", "التوقيفات التقاعدية"]) ?? 0m;
            }
            foreach (var (keys, label) in Sources) breakdown[label] += FindAmount(details, items, keys) ?? 0m;
            foreach (var item in items)
            {
                var key = Key(item.Key);
                if (Tracked(key) && (item.Value < 0 || item.Value != decimal.Truncate(item.Value))) invalid.Add(item.Key);
                if (key.Contains(FoodKey, StringComparison.Ordinal)) { foodObserved = true; breakdown["شركة عراق الجوال"] += item.Value; }
            }
        }
        foreach (var entry in snapshot.Deductions.Where(d => d.Month == month && !PayrollAnalysis.IsInactive(d)))
        {
            executionCount++;
            if (entry.Amount is { } amount) execution += amount; else missingExecution++;
        }
        var warnings = new List<string>();
        if (missingNet > 0) warnings.Add($"{missingNet} منتسب بلا صافي راتب.");
        if (unclassifiedCount > 0) warnings.Add($"{unclassifiedCount} منتسب بلا طريقة دفع مصنفة.");
        if (missingNominal > 0) warnings.Add($"{missingNominal} عقد بلا اسمي؛ حصة 12% غير مكتملة.");
        if (missingExecution > 0) warnings.Add($"{missingExecution} قيد تنفيذ بلا مبلغ.");
        var transferWarnings = new List<string>();
        if (peopleCount == 0) transferWarnings.Add("لا توجد رواتب للشهر المختار.");
        if (invalid.Count > 0) transferWarnings.Add("قيم غير صالحة في: " + string.Join("، ", invalid.Order(StringComparer.Ordinal)));
        if (!observed.Contains(PayrollAnalysis.Normalize("مجموع استقطاعات المصارف"))) transferWarnings.Add("لم يُعثر على عمود مجموع استقطاعات المصارف ضمن البيانات المحفوظة.");
        foreach (var (keys, label) in Sources)
            if (!keys.Any(k => observed.Contains(PayrollAnalysis.Normalize(k)))) transferWarnings.Add($"لم يُعثر على مصدر {label} ضمن البيانات المحفوظة.");
        if (!foodObserved) transferWarnings.Add("لم يُعثر على بند شركة عراق الجوال ضمن بنود الرواتب المحفوظة.");
        if (contractCount > 0 && !observed.Contains(PayrollAnalysis.Normalize("الاسمي"))) transferWarnings.Add("لم يُعثر على عمود اسمي العقود.");
        if (contractCount > 0 && !observed.Contains(PayrollAnalysis.Normalize("توقيفات تقاعدية")) && !observed.Contains(PayrollAnalysis.Normalize("التوقيفات التقاعدية"))) transferWarnings.Add("لم يُعثر على عمود توقيفات العقود.");
        warnings.AddRange(transferWarnings);
        return new BankSummary(month, cash, card, unclassified, unclassifiedCount, missingNet, bank, execution, executionCount,
            missingExecution, nominal, retirement, contractCount, missingNominal, breakdown, Manual(snapshot, month), warnings, transferWarnings);
    }
}
