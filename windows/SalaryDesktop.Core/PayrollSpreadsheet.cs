using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace SalaryDesktop.Core;

public sealed record PayrollImportProblem(string Sheet, int Row, string Message);
public sealed record PayrollImportPreview(string Month, IReadOnlyList<Employee> Employees, IReadOnlyList<Deduction> Deductions, IReadOnlyList<PayrollImportProblem> Problems);
public static class PayrollSpreadsheet
{
    internal static readonly string[] Names = ["الاسم", "اسم الموظف", "الاسم الكامل", "الاسم الثلاثي", "الاسم الرباعي", "Name_m", "name"];
    internal static readonly string[] Ranks = ["الرتبة", "الرتبه", "rank"];
    private static readonly string[] Workplaces = ["محل العمل", "مكان العمل", "جهة العمل", "NO_markaz", "المركز", "القسم", "الوحدة", "الدائرة", "اسم المديرية", "workplace"];
    private static readonly string[] Nets = ["صافي الراتب", "الراتب الصافي", "الصافي", "صافي", "net salary", "net"];
    internal static readonly string[] Amounts = ["مبلغ الاستقطاع", "مبلغ القسط", "المبلغ", "مبلغ", "القسط", "amount"];
    internal static readonly string[] Titles = ["جهة التنفيذ", "نوع الاستقطاع", "جهة الاستقطاع", "اسم الفرع", "الجهة المستفيدة", "الجهة", "اسم المصرف", "المصرف", "اسم الاستقطاع", "الاستقطاع", "البيان", "السبب", "type"];
    private static readonly string[] Keywords = ["صندوق", "شهداء", "رعاية", "نشاط", "نادي", "تامين", "ضريبة", "توقيفات", "تقاعد", "حماية", "تضمين", "ايراد", "رسم", "جوال", "اتصالات", "استقطاعات المصارف", "مجموع الاستقطاعات"];
    internal static readonly string[] TotalNames = ["المجموع", "مجموع", "الاجمالي", "اجمالي", "المجموع الكلي", "المجموع العام", "الاجمالي العام"];
    private static readonly string[] Details = ["الدرجة الوظيفية", "اسم الوكالة", "اسم المديرية", "NO_markaz", "الاسمي", "عدد العلاوات", "الخطورة", "الارزاق", "الاستثنائية", "الاطفال", "الزوجية", "الشهادة", "منصب", "مخصصات صنف", "نسبة الصنف", "جامعية", "مخصصات نقل", "مؤقتة", "اضافية", "مخصصات اخرى", "لقب علمي", "نسبة من الاسمي", "الاستحقاق", "توقيفات تقاعدية", "الرعاية الصحية", "صندوق الشهداء", "نشاط صحي", "نشاط رياضي", "الضريبة", "تضمين", "ايراد نهائي", "تامين صحي", "مجموع استقطاعات المصارف", "حماية الاجتماعية", "رسم طابع", "اسم الاستقطاع", "مبلغ الاستقطاع", "2اسم الاستقطاع", "2مبلغ الاستقطاع", "% استقطاع 3", "مجموع الاستقطاعات", "استقطاع مؤقت", "اسم استقطاع المؤقته", "اسم الشهدة", "اسم الشهادة", "اسم المنصب", "اسم الصنف", "اسم المخصصات الاخرى", "اسم اللقب العلمي", "الرقم الوظيفي", "الرقم الموحد", "رقم الضمان الصحي", "طريقة الدفع", "توطين المصرف", "IBAN", "ملاحظات عامه", "ملاحظات"];
    internal static decimal? Number(string text)
    {
        text = text.Trim(); if (text.Length == 0) return null;
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (compact.IndexOfAny([',', '٬']) >= 0 && !Regex.IsMatch(compact, @"^[0-9٠-٩۰-۹]{1,3}(?:[,٬][0-9٠-٩۰-۹]{3})+(?:[.٫][0-9٠-٩۰-۹]+)?$", RegexOptions.CultureInvariant)) return null;
        var s = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c is ',' or '٬') continue;
            s.Append(c switch { >= '٠' and <= '٩' => (char)('0' + c - '٠'), >= '۰' and <= '۹' => (char)('0' + c - '۰'), '٫' => '.', _ => c });
        }
        return decimal.TryParse(s.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 9_007_199_254_740_991m ? value : null;
    }
    internal static int Find(string[] headers, IEnumerable<string> names, HashSet<int> used, bool exactOnly = false)
    {
        foreach (bool exact in exactOnly ? new[] { true } : new[] { true, false })
            foreach (var name in names.Select(PayrollAnalysis.Normalize))
                for (int i = 0; i < headers.Length; i++) if (!used.Contains(i) && headers[i].Length > 0 && (exact ? headers[i] == name : headers[i].Contains(name, StringComparison.Ordinal))) { used.Add(i); return i; }
        return -1;
    }
    public static PayrollImportPreview Read(string path, string month)
    {
        if (MonthNames.SortKey(month) == 0) throw new InvalidDataException("حدد شهراً وسنة صالحين قبل استيراد الملف.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 100 * 1024 * 1024) throw new InvalidDataException("حجم ملف Excel غير مدعوم.");
        try
        {
            var digest = SHA256.HashData(file); file.Position = 0;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, true))
            {
                long size = 0; var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                    if (!names.Add(entry.FullName) || entry.Length > 512 * 1024 * 1024 || (size += entry.Length) > 1_000_000_000) throw new InvalidDataException("ملف Excel مكرر المحتوى أو يتجاوز الحجم المدعوم.");
            }
            file.Position = 0; Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(file);
            var employees = new List<Employee>(); var deductions = new List<Deduction>(); var problems = new List<PayrollImportProblem>();
            bool recognized = false; int totalRows = 0;
            var excludedNames = Names.Concat(TotalNames).Select(PayrollAnalysis.Normalize).ToHashSet(StringComparer.Ordinal);
            do
            {
                string sheet = reader.Name ?? "ورقة"; int row = 0, name = -1, rank = -1, workplace = -1, amount = -1, title = -1, note = -1;
                int initialDeductions = deductions.Count, initialProblems = problems.Count;
                bool payroll = false, skip = false, nonEmpty = false; string[] headers = []; var itemColumns = new List<int>(); var pairs = new List<(int Name, int Amount, string Fallback)>();
                var sheetEmployees = new List<(Employee Person, Dictionary<string, decimal> Items)>(); var pairNames = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    if (++row > 200_000 || ++totalRows > 500_000 || reader.FieldCount > 500) throw new InvalidDataException("عدد صفوف الملف أو أعمدته يتجاوز الحد المدعوم.");
                    string Cell(int i) => i < 0 || i >= reader.FieldCount ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";
                    if (name < 0)
                    {
                        if (row > 20) break;
                        var raw = Enumerable.Range(0, reader.FieldCount).Select(Cell).ToArray(); var normalized = raw.Select(PayrollAnalysis.Normalize).ToArray();
                        nonEmpty |= raw.Any(v => v.Length > 0);
                        var used = new HashSet<int>(); var n = Find(normalized, Names, used, true); if (n < 0) continue;
                        var net = Find(normalized, Nets, used); var sum = net >= 0 ? net : Find(normalized, Amounts, used);
                        if (sum < 0) { problems.Add(new(sheet, row, "وجد عمود الاسم دون صافي راتب أو مبلغ استقطاع.")); skip = true; break; }
                        if (net < 0 && Find(normalized, ["شهر", "الشهر", "month"], new HashSet<int>(), true) >= 0)
                        { problems.Add(new(sheet, row, "ملف استقطاعات متعدد الأشهر يحتاج مستورداً سنوياً؛ لم تُقرأ صفوفه.")); skip = true; break; }
                        name = n; amount = sum; payroll = net >= 0; headers = raw; rank = Find(normalized, Ranks, used);
                        if (payroll)
                        {
                            workplace = Find(normalized, Workplaces, used);
                            foreach (var (nameHeader, amountHeader) in new[] { ("اسم الاستقطاع", "مبلغ الاستقطاع"), ("2اسم الاستقطاع", "2مبلغ الاستقطاع"), ("اسم استقطاع المؤقته", "استقطاع مؤقت"), ("اسم استقطاع المؤقتة", "استقطاع مؤقت") })
                            {
                                int nc = Array.IndexOf(normalized, PayrollAnalysis.Normalize(nameHeader)), ac = Array.IndexOf(normalized, PayrollAnalysis.Normalize(amountHeader));
                                if (nc < 0 || ac < 0 || used.Contains(nc) || pairs.Any(p => p.Name == nc)) continue;
                                pairs.Add((nc, ac, raw[ac])); used.Add(nc); used.Add(ac);
                            }
                            var keys = Keywords.Select(PayrollAnalysis.Normalize).ToArray();
                            for (int i = 0; i < normalized.Length; i++) if (!used.Contains(i) && normalized[i].Length > 0 && !normalized[i].Contains('%') && !normalized[i].StartsWith("اسم", StringComparison.Ordinal) && keys.Any(k => normalized[i].Contains(k, StringComparison.Ordinal))) itemColumns.Add(i);
                        }
                        else { title = Find(normalized, Titles, used); note = Find(normalized, ["رقم وتاريخ الاضبارة", "رقم الاضبارة", "الاضبارة", "الملاحظات", "ملاحظات", "ملاحظة", "note"], used); }
                        recognized = true; continue;
                    }
                    var person = Cell(name);
                    if (person.Length == 0)
                    {
                        var numeric = Cell(amount);
                        if ((numeric.Length > 0 && Number(numeric) != 0) || (!payroll && Cell(title).Length > 0)) problems.Add(new(sheet, row, "توجد بيانات راتب أو استقطاع دون اسم منتسب."));
                        continue;
                    }
                    if (excludedNames.Contains(PayrollAnalysis.Normalize(person))) continue;
                    var value = Number(Cell(amount));
                    if (payroll)
                    {
                        if (value is null) { problems.Add(new(sheet, row, "صافي الراتب فارغ أو غير صالح.")); continue; }
                        var items = new Dictionary<string, decimal>(StringComparer.Ordinal);
                        void AddItem(string key, decimal v) { if (v != 0) items[key] = items.GetValueOrDefault(key) + v; }
                        foreach (int i in itemColumns)
                        {
                            var cell = Cell(i); if (cell.Length == 0) continue; var v = Number(cell);
                            if (v is null) { problems.Add(new(sheet, row, $"قيمة غير صالحة في عمود {headers[i]}.")); continue; }
                            AddItem(headers[i], v.Value);
                        }
                        foreach (var pair in pairs)
                        {
                            var cell = Cell(pair.Amount); if (cell.Length == 0) continue; var v = Number(cell);
                            if (v is null) { problems.Add(new(sheet, row, $"قيمة غير صالحة في عمود {pair.Fallback}.")); continue; }
                            var label = Cell(pair.Name); if (label.Length > 0) { if (!pairNames.TryGetValue(pair.Fallback, out var counts)) pairNames[pair.Fallback] = counts = new(StringComparer.Ordinal); counts[label] = counts.GetValueOrDefault(label) + 1; }
                            AddItem(label.Length == 0 ? pair.Fallback : label, v.Value);
                        }
                        var detailValues = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var key in Details) { var i = Array.FindIndex(headers, h => PayrollAnalysis.Normalize(h) == PayrollAnalysis.Normalize(key)); if (i >= 0 && Cell(i) is { Length: > 0 } v && v != "0") detailValues.TryAdd(key, v); }
                        var values = Enumerable.Range(0, headers.Length).Select(Cell).ToArray();
                        var record = new Employee(month, person, Cell(rank), Cell(workplace), value, null, JsonSerializer.SerializeToUtf8Bytes(detailValues), JsonSerializer.SerializeToUtf8Bytes(new { headers, values }));
                        sheetEmployees.Add((record, items));
                    }
                    else
                    {
                        var status = PayrollAnalysis.Normalize(Cell(amount)); var statusValue = status switch { "موقوف" => "موقوف", "هارب" => "هارب", "محكوم" => "محكوم", _ => null };
                        if (value is null && statusValue is null) { problems.Add(new(sheet, row, "مبلغ الاستقطاع فارغ أو غير صالح.")); continue; }
                        var entity = Cell(title); if (entity.Length == 0) { problems.Add(new(sheet, row, "جهة الاستقطاع فارغة.")); continue; }
                        var text = Cell(note); if (statusValue is not null) text = $"⟦حالة الاستقطاع:{statusValue}⟧" + (text.Length > 0 ? "\n" + text : "");
                        deductions.Add(new(month, person, entity, value, text, Cell(rank)));
                    }
                }
                if (payroll && !skip)
                {
                    foreach (var (fallback, names) in pairNames)
                    {
                        var order = names.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
                        if (order.Length > 1 && order[0].Value == order[1].Value && sheetEmployees.Any(e => e.Items.ContainsKey(fallback))) { problems.Add(new(sheet, 0, $"تعذر تحديد الجهة الغالبة لعمود {fallback}.")); continue; }
                        var main = order[0].Key;
                        foreach (var employee in sheetEmployees) if (employee.Items.Remove(fallback, out var v)) employee.Items[main] = employee.Items.GetValueOrDefault(main) + v;
                    }
                    employees.AddRange(sheetEmployees.Select(e => e.Person with { Items = JsonSerializer.SerializeToUtf8Bytes(e.Items) }));
                }
                if (name < 0 && nonEmpty && !skip) problems.Add(new(sheet, 0, "ورقة غير فارغة لم تُتعرف أعمدتها؛ راجعها قبل الاستيراد."));
                if (name >= 0 && !skip && problems.Count == initialProblems && (payroll ? sheetEmployees.Count == 0 : deductions.Count == initialDeductions))
                    problems.Add(new(sheet, 0, payroll ? "عناوين الرواتب موجودة لكن لا توجد سجلات رواتب." : "عناوين الاستقطاعات موجودة لكن لا توجد سجلات استقطاع."));
            } while (reader.NextResult());
            if (!recognized && problems.Count == 0) throw new InvalidDataException("لم يُعثر على أعمدة رواتب أو استقطاعات في أول 20 صفاً.");
            if (employees.Count == 0 && deductions.Count == 0 && problems.Count == 0) throw new InvalidDataException("لم يُعثر على سجلات في الملف.");
            file.Position = 0; if (!digest.AsSpan().SequenceEqual(SHA256.HashData(file))) throw new InvalidDataException("تغيّر الملف أثناء قراءته؛ لم تُغيّر البيانات.");
            return new(month, employees, deductions, problems);
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        { throw new InvalidDataException("تعذّر قراءة ملف الرواتب أو الاستقطاعات؛ لم تُغيّر البيانات.", ex); }
    }
}
