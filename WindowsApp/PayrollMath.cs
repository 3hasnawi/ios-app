using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SalaryWindows;
internal static class PayrollMath
{
    public static string Normalize(string value)
    {
        var chars = new StringBuilder();
        foreach (var c in value.ToLowerInvariant())
        {
            if ((c >= '\u064b' && c <= '\u065f') || c == '\u0670' || c == '\u0640') continue;
            if (c >= '٠' && c <= '٩') { chars.Append((char)('0' + c - '٠')); continue; }
            if (c >= '۰' && c <= '۹') { chars.Append((char)('0' + c - '۰')); continue; }
            chars.Append(c switch { 'آ' or 'أ' or 'إ' or 'ٱ' => 'ا', 'ة' => 'ه', 'ى' or 'ئ' => 'ي', 'ؤ' => 'و', _ => c });
        }
        return string.Join(" ", chars.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
    public static string Key(string value) => Normalize(value).Replace(" ", "");
    public static int Category(string rank)
    {
        var key = string.Join(" ", Normalize(rank).Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (key.Split(' ').Any(w => w == "عقد" || w == "عقود")) return 3;
        if (key.StartsWith("موظف")) return 1;
        string[] officers = ["لواء", "فريق", "فريق اول", "عميد", "عقيد", "مقدم", "رائد", "نقيب", "ملازم اول", "م اول", "ملازم"];
        return officers.Contains(key) ? 0 : 2;
    }
    public static Dictionary<string, T> Decode<T>(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, T>>(Convert.FromBase64String(base64)) ?? []; }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new InvalidDataException("تفاصيل أحد سجلات النسخة غير صالحة.", ex); }
    }
    public static double? ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = new StringBuilder();
        foreach (var c in value)
        {
            if (c >= '٠' && c <= '٩') s.Append((char)('0' + c - '٠'));
            else if (c >= '۰' && c <= '۹') s.Append((char)('0' + c - '۰'));
            else if (c == '٫') s.Append('.');
            else if (c is >= '0' and <= '9' or '.' or '-') s.Append(c);
        }
        return double.TryParse(s.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }
    public static double? Amount(Dictionary<string, string> details, Dictionary<string, double> items, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (details.TryGetValue(key, out var exact) && ParseNumber(exact) is double d) return d;
            if (items.TryGetValue(key, out var n)) return n;
            var norm = Normalize(key);
            foreach (var p in details) if (Normalize(p.Key) == norm && ParseNumber(p.Value) is double v) return v;
            foreach (var p in items) if (Normalize(p.Key) == norm) return p.Value;
        }
        return null;
    }
}
internal sealed record BankSummary(double Cash, double Card, double Unclassified, int MissingNet, double BankDeductions,
    double ExecutionDeductions, int ExecutionCount, int MissingExecution, double Contract, int MissingNominal,
    Dictionary<string, double> Breakdown)
{
    public bool Matched => ExecutionCount > 0 && MissingExecution == 0 && Math.Abs(BankDeductions - ExecutionDeductions) < 1;
    public double Subtotal => Cash + Card + Unclassified + BankDeductions + Contract + Breakdown.Values.Sum();
}
internal static class BankCalculator
{
    public static BankSummary Calculate(BackupData data, string month)
    {
        double cash = 0, card = 0, other = 0, bank = 0, nominal = 0, retirement = 0; int missingNet = 0, missingNominal = 0;
        var breakdown = new Dictionary<string, double> { ["صندوق شهداء الشرطة"] = 0, ["الرعاية الصحية"] = 0, ["النشاط الرياضي"] = 0, ["الضريبة"] = 0, ["الحماية الاجتماعية"] = 0, ["شركة عراق الجوال"] = 0 };
        foreach (var emp in data.Employees.Where(x => x.Month == month))
        {
            var details = PayrollMath.Decode<string>(emp.Details); var items = PayrollMath.Decode<double>(emp.Items);
            if (emp.Net is double net)
            {
                var pay = PayrollMath.Normalize(details.GetValueOrDefault("طريقة الدفع") ?? "");
                if (pay.Contains("بطاق") || pay.Contains("كارت") || pay.Contains("ماستر")) card += net;
                else if (pay.Contains("كاش") || pay.Contains("نقد")) cash += net; else other += net;
            }
            else missingNet++;
            bank += PayrollMath.Amount(details, items, "مجموع استقطاعات المصارف") ?? 0;
            if (PayrollMath.Normalize(emp.Rank ?? "") == "عقود")
            {
                var n = PayrollMath.Amount(details, items, "الاسمي");
                if (n is double v) nominal += v; else missingNominal++;
                retirement += PayrollMath.Amount(details, items, "توقيفات تقاعدية", "التوقيفات التقاعدية") ?? 0;
            }
            breakdown["صندوق شهداء الشرطة"] += PayrollMath.Amount(details, items, "صندوق الشهداء", "صندوق شهداء الشرطة") ?? 0;
            breakdown["الرعاية الصحية"] += PayrollMath.Amount(details, items, "الرعاية الصحية", "رعاية صحية") ?? 0;
            breakdown["النشاط الرياضي"] += PayrollMath.Amount(details, items, "نشاط رياضي", "النشاط الرياضي") ?? 0;
            breakdown["الضريبة"] += PayrollMath.Amount(details, items, "الضريبة", "ضريبة") ?? 0;
            breakdown["الحماية الاجتماعية"] += PayrollMath.Amount(details, items, "حماية الاجتماعية", "الحماية الاجتماعية") ?? 0;
            breakdown["شركة عراق الجوال"] += items.Where(x => PayrollMath.Normalize(x.Key).Contains("الجوال")).Sum(x => x.Value);
        }
        var ds = data.Deductions.Where(x => x.Month == month).ToList();
        var contract = (double)Math.Floor((decimal)nominal * 12m / 100m) + retirement;
        return new(cash, card, other, missingNet, bank, ds.Sum(x => x.Amount ?? 0), ds.Count, ds.Count(x => x.Amount == null), contract, missingNominal, breakdown);
    }
}

internal static class ManualAmounts
{
    // إعدادات الآيفون تُحفظ كـ binary plist، وقيمة المبالغ نفسها بيانات JSON.
    public static (double Leave, double Rewards, double Single) ForMonth(BackupData data, string month)
    {
        if (!data.Settings.TryGetValue("bankStatementManualAmounts", out var encoded)) return (0, 0, 0);
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            var raw = PlistData(bytes);
            using var json = JsonDocument.Parse(raw);
            if (!json.RootElement.TryGetProperty(month, out var value)) return (0, 0, 0);
            double Get(string key)
            {
                if (!value.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var n) || !double.IsFinite(n))
                    throw new FormatException();
                return n;
            }
            return (Get("accumulatedLeave"), Get("employeeRewards"), Get("singleList"));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or IndexOutOfRangeException or OverflowException or InvalidOperationException)
        { throw new InvalidDataException("تعذّرت قراءة المبالغ اليدوية من النسخة؛ لا يمكن عرض إجمالي كشف البنك.", ex); }
    }
    private static byte[] PlistData(byte[] b)
    {
        if (b.Length < 40 || Encoding.ASCII.GetString(b, 0, 8) != "bplist00") throw new FormatException();
        var trailer = b.Length - 32; int offsetSize = b[trailer + 6];
        if (offsetSize is < 1 or > 8) throw new FormatException();
        long ReadBig(int at, int length)
        {
            if (at < 0 || length < 1 || length > 8 || at > b.Length - length) throw new FormatException();
            ulong n = 0; for (var i = 0; i < length; i++) n = (n << 8) | b[at + i];
            return checked((long)n);
        }
        var count = ReadBig(trailer + 8, 8); var root = ReadBig(trailer + 16, 8); var table = ReadBig(trailer + 24, 8);
        if (root >= count || table < 8 || table > trailer || root > (trailer - table) / offsetSize) throw new FormatException();
        int pos = checked((int)ReadBig(checked((int)(table + root * offsetSize)), offsetSize));
        if (pos < 8 || pos >= table || (b[pos] >> 4) != 4) throw new FormatException();
        int marker = b[pos++]; long length = marker & 15;
        if (length == 15)
        {
            if (pos >= table || (b[pos] >> 4) != 1 || (b[pos] & 15) > 3) throw new FormatException();
            int size = 1 << (b[pos++] & 15); length = ReadBig(pos, size); pos += size;
        }
        if (length < 0 || length > table - pos) throw new FormatException();
        return b.AsSpan(pos, checked((int)length)).ToArray();
    }
}
