using System.Globalization;
using System.Text.Json;

namespace SalaryDesktop.Core;

/// خلية في جدول الحالة: Bad للقيمة المتعارضة أو الخاطئة، Good للقيمة السليمة المرجعية.
public sealed record ReviewCell(string Text, bool Bad = false, bool Good = false);
public sealed record ReviewTableRow(string Label, IReadOnlyList<ReviewCell> Cells);
/// وسم قصير على الحالة؛ Tone: bad أو warn أو info أو muted.
public sealed record ReviewTag(string Text, string Tone);
/// حالة تدقيق واحدة: التكرار حالة لكل رقم مكرر (لا لكل اسم)، ولكل حالة تشخيص «مكمن الخلل» والخطوة المقترحة وجدول مقارنة.
public sealed record ReviewIssue(string Kind, string Title, string Subtitle, string Diagnosis, string Action, IReadOnlyList<ReviewTag> Tags,
    IReadOnlyList<string> Columns, IReadOnlyList<ReviewTableRow> Table, IReadOnlyList<string> People, bool MonoTitle = false)
{
    public string Search => PayrollAnalysis.Normalize(string.Join(" ", new[] { Title, Subtitle, Diagnosis }.Concat(People)));
}

/// تشخيص ملاحظات التدقيق (طلب المالك): بدل قائمة أسماء، تُجمع الحالة ويُستنتج سببها من بيانات الشهر والشهر السابق.
/// الاستنتاج ترجيح مبني على البيانات ولا يغيّر شيئاً؛ نصوصه تقول «الأرجح» حيث لا يقين.
public static class SalaryReviewDiagnosis
{
    public static readonly string[] Severe = ["أسماء مكررة", "صافي ناقص", "IBAN يحتاج مراجعة", "IBAN مكرر", "IBAN فارغ", "رقم مكرر", "حساب الصافي", "مجموع الاستقطاعات"];
    public static readonly string[] Review = ["استقطاع بلا راتب", "الرعاية الصحية"];
    public static readonly string[] Changes = ["جديد في الرواتب", "خارج من الرواتب", "تغيّر الصافي", "تغيّرت الرتبة", "تغيّر محل العمل", "تغيّر الحساب"];
    public static readonly string[] Staff = ["راتب بلا ملاك", "ملاك بلا راتب", "رتبة مختلفة عن الملاك", "اختلاف الرقم الموحد"];
    public static string GroupOf(string kind) => Severe.Contains(kind) ? "مشكلات مهمة" : Changes.Contains(kind) ? "تغيّرات الشهر" : Staff.Contains(kind) ? "الملاك" : "تحتاج مراجعة";
    public static string Label(string kind) => kind switch
    {
        "IBAN يحتاج مراجعة" => "IBAN غير صالح", "رقم مكرر" => "الرقم الموحد أو الوظيفي مكرر", "حساب الصافي" => "حساب الصافي لا يطابق",
        "مجموع الاستقطاعات" => "مجموع الاستقطاعات لا يطابق", _ => kind,
    };

    private sealed record Person(Employee E, Dictionary<string, string> D, Dictionary<string, decimal> I);
    private static readonly CultureInfo Numbers = CultureInfo.GetCultureInfo("en-US");
    private static string Key(string text) => PayrollAnalysis.Normalize(text).Replace(" ", "", StringComparison.Ordinal);
    private static string Money(decimal? value) => value?.ToString("#,0.##", Numbers) ?? "—";
    private static string Iban(Person p) => TransferPreparation.NormalizeAccount(p.D.GetValueOrDefault("IBAN") ?? "");
    private static string Id(string? value) => string.Concat((value ?? "").Select(c => CharUnicodeInfo.GetDecimalDigitValue(c)).Where(n => n >= 0).Select(n => (char)('0' + n))).TrimStart('0');
    private static string Text(Person p, string key) => (p.D.GetValueOrDefault(key) ?? "").Trim();
    private static string Show(string value) => value.Length == 0 ? "—" : value;
    private static string Tail(string value, int count) => value.Length == 0 ? "—" : "…" + (value.Length <= count ? value : value[^count..]);
    /// IBAN بمجموعات من أربع خانات لسهولة المقارنة بالنظر.
    public static string GroupedIban(string iban) => iban.Length == 0 ? "فارغ" : string.Join(" ", Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));

    private static Person Read(Employee e)
    {
        try
        {
            var details = e.Details is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(e.Details) ?? new Dictionary<string, string>();
            var items = e.Items is null ? new Dictionary<string, decimal>() : JsonSerializer.Deserialize<Dictionary<string, decimal>>(e.Items) ?? new Dictionary<string, decimal>();
            return new Person(e, details, items);
        }
        catch (JsonException) { return new Person(e, new Dictionary<string, string>(), new Dictionary<string, decimal>()); }
    }
    private static decimal? Value(Person p, string title)
    {
        var key = PayrollAnalysis.Normalize(title);
        foreach (var (name, text) in p.D)
        {
            if (PayrollAnalysis.Normalize(name) != key || string.IsNullOrWhiteSpace(text)) continue;
            var normalized = TransferPreparation.NormalizeAccount(text).Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal).Replace('٫', '.');
            return decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        foreach (var (name, value) in p.I) if (PayrollAnalysis.Normalize(name) == key) return value;
        return null;
    }

    /// سبب عدم صلاحية IBAN عراقي بالتحديد: الطول، البداية، الرموز، أو رقم التحقق (مع الرقم الصحيح المتوقع).
    public static string IbanProblem(string iban)
    {
        if (iban.Length != 23) return $"طول الرقم {iban.Length} خانة بدل 23؛ خانة ناقصة أو زائدة.";
        if (!iban.StartsWith("IQ", StringComparison.Ordinal)) return "الرقم لا يبدأ بـ IQ.";
        if (iban.Any(c => !char.IsAsciiLetterOrDigit(c))) return "الرقم فيه رموز غير الحروف والأرقام الإنجليزية.";
        var remainder = 0;
        foreach (var c in iban[4..] + iban[..2] + "00")
        {
            int n = c >= 'A' ? c - 'A' + 10 : c - '0';
            if (n >= 10) remainder = (remainder * 10 + n / 10) % 97;
            remainder = (remainder * 10 + n % 10) % 97;
        }
        var expected = (98 - remainder).ToString("00", CultureInfo.InvariantCulture);
        return expected == iban[2..4]
            ? "الرقم لا يجتاز فحص IBAN."
            : $"رقم التحقق (الخانتان بعد IQ) {iban[2..4]} والصحيح لبقية الرقم {expected}؛ غالباً خطأ في خانة واحدة أو تبديل خانتين.";
    }

    public static IReadOnlyList<ReviewIssue> Build(BackupSnapshot source, SalaryReviewResult result)
    {
        var month = result.Month; var previous = result.PreviousMonth;
        var current = source.Employees.Where(e => e.Month == month).Select(Read).ToArray();
        var byKey = current.GroupBy(p => Key(p.E.Name)).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var before = previous is null ? new Dictionary<string, Person[]>(StringComparer.Ordinal)
            : source.Employees.Where(e => e.Month == previous).Select(Read).GroupBy(p => Key(p.E.Name)).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        Person? Cur(string name) => byKey.TryGetValue(Key(name), out var list) && list.Length == 1 ? list[0] : null;
        Person? Prev(string name) => before.TryGetValue(Key(name), out var list) && list.Length == 1 ? list[0] : null;
        var prevLabel = previous ?? "الشهر السابق";
        var issues = new List<ReviewIssue>();
        ReviewIssue Generic(SalaryReviewRow row) => new(row.Kind, row.Name, row.Detail, row.Detail, "", [],
            ["القيمة"], new ReviewTableRow[] { new("قبل", [new(Money(row.Before))]), new("بعد", [new(Money(row.After))]) }.Where(r => r.Cells[0].Text != "—").ToArray(), [row.Name]);

        foreach (var kind in result.Rows.Select(r => r.Kind).Distinct(StringComparer.Ordinal))
        {
            var rows = result.Rows.Where(r => r.Kind == kind).ToArray();
            switch (kind)
            {
                case "أسماء مكررة":
                    foreach (var row in rows)
                    {
                        if (!byKey.TryGetValue(Key(row.Name), out var group) || group.Length < 2) { issues.Add(Generic(row)); continue; }
                        string[] Field(Func<Person, string> pick) => group.Select(pick).ToArray();
                        var fields = new (string Label, string[] Values)[] {
                            ("الرتبة", Field(p => p.E.Rank)), ("محل العمل", Field(p => p.E.Workplace)), ("الرقم الموحد", Field(p => Text(p, "الرقم الموحد"))),
                            ("IBAN", Field(p => GroupedIban(Iban(p)))), ("الصافي", Field(p => Money(p.E.Net))), ("طريقة الدفع", Field(p => Text(p, "طريقة الدفع"))) };
                        var identical = fields.All(f => f.Values.Distinct(StringComparer.Ordinal).Count() == 1);
                        var ids = group.Select(p => Id(Text(p, "الرقم الموحد"))).Where(x => x.Length > 0).Distinct().ToArray();
                        var twoPeople = ids.Length > 1;
                        var table = fields.Select(f => new ReviewTableRow(f.Label, f.Values.Select(v => new ReviewCell(Show(v), Bad: !identical && !twoPeople && f.Values.Distinct(StringComparer.Ordinal).Count() > 1)).ToArray())).ToArray();
                        var diagnosis = identical
                            ? $"{group.Length} صفوف متطابقة تماماً في ملف {month}؛ الأرجح تكرار صف، وسيُحسب الراتب أكثر من مرة."
                            : twoPeople ? "الرقم الموحد مختلف؛ الأرجح أنهم أشخاص مختلفون بالاسم نفسه. التدقيق لا يقارنهم بالشهر السابق حتى لا تختلط بياناتهم."
                            : "الصفوف بالاسم نفسه وبياناتها مختلفة جزئياً (الخانات الملوّنة)؛ تحقق هل هو شخص واحد مكرر أم شخصان.";
                        var action = identical ? "احذف الصف المكرر من ملف الرواتب ثم أعد استيراد الشهر." : twoPeople ? "لا خطأ إن كانوا أشخاصاً مختلفين؛ للتمييز أضف اسم الأب أو اللقب في الملف." : "راجع الصفوف في ملف الرواتب وصحّح المكرر.";
                        issues.Add(new(kind, row.Name, $"الاسم موجود {group.Length} مرات", diagnosis, action,
                            [identical ? new("صف مكرر", "bad") : twoPeople ? new("أشخاص مختلفون", "info") : new("بيانات مختلفة", "warn")],
                            group.Select((_, i) => $"الصف {i + 1}").ToArray(), table, [row.Name]));
                    }
                    break;

                case "صافي ناقص":
                    foreach (var row in rows)
                    {
                        if (Cur(row.Name) is not { } p) { issues.Add(Generic(row)); continue; }
                        var entitlement = Value(p, "الاستحقاق"); var deductions = Value(p, "مجموع الاستقطاعات");
                        decimal? expected = entitlement is not null && deductions is not null ? entitlement - deductions : null;
                        var old = Prev(row.Name)?.E.Net;
                        var diagnosis = $"خانة صافي الراتب فارغة في ملف {month}؛ لا يدخل الراتب في المجاميع ولا كشف البنك ولا ملفات التحويل."
                            + (expected is { } e ? $" المتوقع حسب الأعمدة {Money(e)}." : "") + (old is { } o ? $" كان صافيه في {prevLabel} {Money(o)}." : "");
                        issues.Add(new(kind, row.Name, p.E.Workplace, diagnosis, "أكمل خانة الصافي في ملف الرواتب ثم أعد استيراد الشهر.", [new("صافي فارغ", "bad")],
                            [month], [new("الاستحقاق", [new(Money(entitlement))]), new("مجموع الاستقطاعات", [new(Money(deductions))]), new("الصافي المتوقع", [new(Money(expected))]),
                                new("الصافي في الملف", [new("فارغ", Bad: true)]), new($"الصافي في {prevLabel}", [new(Money(old))]), new("طريقة الدفع", [new(Show(Text(p, "طريقة الدفع")))])], [row.Name]));
                    }
                    break;

                case "IBAN يحتاج مراجعة":
                    foreach (var row in rows)
                    {
                        if (Cur(row.Name) is not { } p) { issues.Add(Generic(row)); continue; }
                        var iban = Iban(p); var oldIban = Prev(row.Name) is { } q ? Iban(q) : "";
                        var oldValid = oldIban.Length > 0 && TransferPreparation.ValidIban(oldIban);
                        var diagnosis = IbanProblem(iban) + (oldValid && oldIban != iban ? $" كان رقمه في {prevLabel} صالحاً؛ الأرجح خطأ إدخال في ملف {month}." : "");
                        issues.Add(new(kind, row.Name, GroupedIban(iban), diagnosis, oldValid && oldIban != iban ? $"قارن الرقم بحساب {prevLabel} وبكتاب الحساب، صحّحه ثم أعد استيراد الشهر." : "صحّح IBAN من كتاب الحساب أو المصرف ثم أعد استيراد الشهر.",
                            [new("IBAN غير صالح", "bad"), .. oldValid && oldIban != iban ? new[] { new ReviewTag($"كان صالحاً في {prevLabel}", "info") } : Array.Empty<ReviewTag>()],
                            [row.Name], [new($"IBAN {month}", [new(GroupedIban(iban), Bad: true)]), new($"IBAN {prevLabel}", [new(oldIban.Length == 0 ? "—" : GroupedIban(oldIban), Good: oldValid)]),
                                new("عدد الخانات", [new(iban.Length.ToString(Numbers), Bad: iban.Length != 23)]), new("محل العمل", [new(Show(p.E.Workplace))])], [row.Name]));
                    }
                    break;

                case "IBAN مكرر":
                    foreach (var group in current.Where(p => Iban(p).Length > 0).GroupBy(Iban).Where(g => g.Select(p => Key(p.E.Name)).Distinct(StringComparer.Ordinal).Count() > 1))
                    {
                        var people = group.ToArray(); var iban = group.Key;
                        var olds = people.Select(p => Prev(p.E.Name) is { } q ? Iban(q) : "").ToArray();
                        var changed = people.Where((_, i) => olds[i].Length > 0 && olds[i] != iban).Select(p => p.E.Name).ToArray();
                        var stable = people.Where((_, i) => olds[i] == iban).Select(p => p.E.Name).ToArray();
                        var ids = people.Select(p => Id(Text(p, "الرقم الموحد"))).ToArray();
                        var sameId = ids.All(x => x.Length > 0) && ids.Distinct().Count() == 1;
                        var differentIds = ids.Where(x => x.Length > 0).Distinct().Count() > 1;
                        var diagnosis = changed.Length > 0 && stable.Length > 0
                            ? $"حساب «{string.Join("» و«", changed)}» تغيّر في {month} وصار مطابقاً لحساب «{stable[0]}» الذي لم يتغير منذ {prevLabel}. الأرجح أن IBAN نُسخ من صف آخر في ملف {month}."
                            : stable.Length == people.Length ? $"الرقم مشترك بينهم منذ {prevLabel} أيضاً؛ راجع هل هو حساب مشترك فعلاً أم خطأ قديم."
                            : changed.Length == people.Length ? $"تغيّر الحساب عند الجميع هذا الشهر إلى الرقم نفسه؛ الأرجح خطأ نسخ في ملف {month}."
                            : $"رقم حساب واحد مسجّل لأكثر من اسم في {month}.";
                        if (sameId) diagnosis += " الرقم الموحد نفسه؛ قد يكون شخصاً واحداً كُتب اسمه بطريقتين.";
                        else if (differentIds) diagnosis += " الرقم الموحد مختلف، فهم أشخاص مختلفون.";
                        var tags = new List<ReviewTag>();
                        if (changed.Length > 0 && stable.Length > 0) tags.Add(new($"تغيّر هذا الشهر عند {changed[0]}", "bad"));
                        if (people.Length > 2) tags.Add(new($"{people.Length} أسماء", "bad"));
                        if (stable.Length == people.Length) tags.Add(new($"مكرر منذ {prevLabel}", "muted"));
                        if (sameId) tags.Add(new("الرقم الموحد نفسه", "warn"));
                        issues.Add(new(kind, GroupedIban(iban), string.Join(" · ", people.Select(p => p.E.Name)), diagnosis,
                            $"راجع IBAN «{string.Join("» و«", changed.Length > 0 ? changed : people.Select(p => p.E.Name).ToArray())}» في ملف الرواتب أو كتاب تغيير الحساب، صحّحه ثم أعد استيراد الشهر.",
                            tags, people.Select(p => p.E.Name).ToArray(),
                            [new($"IBAN {month}", people.Select(_ => new ReviewCell(Tail(iban, 7), Bad: true)).ToArray()),
                             new($"IBAN {prevLabel}", olds.Select(o => new ReviewCell(Tail(o, 7), Good: o == iban)).ToArray()),
                             new("الرقم الموحد", people.Select(p => new ReviewCell(Show(Text(p, "الرقم الموحد")), Bad: sameId)).ToArray()),
                             new("الرتبة", people.Select(p => new ReviewCell(Show(p.E.Rank))).ToArray()),
                             new("محل العمل", people.Select(p => new ReviewCell(Show(p.E.Workplace))).ToArray()),
                             new("الصافي", people.Select(p => new ReviewCell(Money(p.E.Net))).ToArray())],
                            people.Select(p => p.E.Name).ToArray(), MonoTitle: true));
                    }
                    break;

                case "IBAN فارغ":
                    foreach (var row in rows)
                    {
                        if (Cur(row.Name) is not { } p) { issues.Add(Generic(row)); continue; }
                        var oldIban = Prev(row.Name) is { } q ? Iban(q) : ""; var method = Text(p, "طريقة الدفع");
                        var cash = PayrollAnalysis.Normalize(method).Contains("كاش", StringComparison.Ordinal);
                        var diagnosis = oldIban.Length > 0 ? $"كان له IBAN في {prevLabel} ({GroupedIban(oldIban)}) وسقط من ملف {month}."
                            : cash ? "طريقة الدفع كاش؛ قد لا يحتاج حساباً، ولن يدخل ملفات التحويل المصرفية."
                            : $"لا يوجد IBAN في {month} ولا في {prevLabel}؛ إن كان راتبه بطاقة فلن يدخل ملفات التحويل.";
                        issues.Add(new(kind, row.Name, p.E.Workplace, diagnosis, oldIban.Length > 0 ? $"أعد إدخال IBAN من ملف {prevLabel} أو كتاب الحساب ثم أعد استيراد الشهر." : "أكمل IBAN من كتاب الحساب إن كان الراتب بطاقة.",
                            [oldIban.Length > 0 ? new($"كان موجوداً في {prevLabel}", "bad") : cash ? new("كاش", "muted") : new("بلا حساب", "warn")],
                            [row.Name], [new($"IBAN {month}", [new("فارغ", Bad: true)]), new($"IBAN {prevLabel}", [new(oldIban.Length == 0 ? "—" : GroupedIban(oldIban), Good: oldIban.Length > 0)]),
                                new("طريقة الدفع", [new(Show(method))]), new("الصافي", [new(Money(p.E.Net))])], [row.Name]));
                    }
                    break;

                case "رقم مكرر":
                    foreach (var label in new[] { "الرقم الوظيفي", "الرقم الموحد" })
                        foreach (var group in current.Select(p => (P: p, N: Id(Text(p, label)))).Where(x => x.N.Length > 0).GroupBy(x => x.N)
                                     .Where(g => g.Select(x => Key(x.P.E.Name)).Distinct(StringComparer.Ordinal).Count() > 1))
                        {
                            var people = group.Select(x => x.P).ToArray();
                            var ibans = people.Select(Iban).ToArray();
                            var sameIban = ibans.All(x => x.Length > 0) && ibans.Distinct().Count() == 1;
                            var changed = people.Where(p => Prev(p.E.Name) is { } q && Id(Text(q, label)) is { Length: > 0 } old && old != group.Key).Select(p => p.E.Name).ToArray();
                            var diagnosis = (sameIban ? "IBAN نفسه أيضاً؛ الأرجح شخص واحد كُتب اسمه بطريقتين أو صف مكرر." : "IBAN مختلف؛ الأرجح أشخاص مختلفون وأحد الرقمين خطأ إدخال.")
                                + (changed.Length > 0 ? $" {label} لـ«{string.Join("» و«", changed)}» تغيّر هذا الشهر وصار مطابقاً." : "");
                            issues.Add(new(kind, $"{label} {group.Key}", string.Join(" · ", people.Select(p => p.E.Name)), diagnosis,
                                changed.Length > 0 ? $"راجع {label} لـ«{string.Join("» و«", changed)}» في ملف الرواتب وصحّحه." : $"راجع {label} في ملف الرواتب وسجل الملاك وصحّح الخاطئ.",
                                [sameIban ? new("نفس الحساب", "warn") : new("حسابات مختلفة", "bad"), .. changed.Length > 0 ? new[] { new ReviewTag("تغيّر هذا الشهر", "bad") } : Array.Empty<ReviewTag>()],
                                people.Select(p => p.E.Name).ToArray(),
                                [new(label, people.Select(_ => new ReviewCell(group.Key, Bad: true)).ToArray()),
                                 new("IBAN", people.Select(p => new ReviewCell(GroupedIban(Iban(p)), Bad: sameIban)).ToArray()),
                                 new("الرتبة", people.Select(p => new ReviewCell(Show(p.E.Rank))).ToArray()),
                                 new("محل العمل", people.Select(p => new ReviewCell(Show(p.E.Workplace))).ToArray()),
                                 new("الصافي", people.Select(p => new ReviewCell(Money(p.E.Net))).ToArray())],
                                people.Select(p => p.E.Name).ToArray()));
                        }
                    break;

                case "حساب الصافي":
                    foreach (var row in rows)
                    {
                        if (Cur(row.Name) is not { } p || row.Before is not { } expected || row.After is not { } net) { issues.Add(Generic(row)); continue; }
                        var entitlement = Value(p, "الاستحقاق"); var deductions = Value(p, "مجموع الاستقطاعات");
                        var difference = expected - net;
                        var match = p.I.Where(x => x.Value != 0 && Math.Abs(x.Value) == Math.Abs(difference)).Select(x => x.Key).FirstOrDefault();
                        var diagnosis = Math.Abs(difference) <= 1 ? $"فرق تقريب صغير ({Money(difference)})."
                            : match is not null ? $"الفرق {Money(Math.Abs(difference))} يساوي بالضبط بند «{match}»؛ الأرجح أن عمود «مجموع الاستقطاعات» {(difference > 0 ? "لم يشمل هذا البند" : "شمله مرتين أو شمل بنداً غير مستقطع")}."
                            : $"الفرق {Money(Math.Abs(difference))} لا يساوي بنداً واحداً؛ راجع أعمدة الاستحقاق والاستقطاعات في ملف {month}.";
                        issues.Add(new(kind, row.Name, $"فرق {Money(Math.Abs(difference))}" + (match is not null ? $" · يساوي بند «{match}»" : ""), diagnosis,
                            match is not null ? $"تأكد من عمود «مجموع الاستقطاعات» في ملف {month}؛ إن كان البند مستقطعاً فعلاً فالصافي صحيح والمجموع ناقص." : "راجع صف المنتسب في ملف الرواتب.",
                            [Math.Abs(difference) <= 1 ? new("تقريب", "muted") : match is not null ? new("بند خارج المجموع", "bad") : new("راجع الملف", "warn")],
                            [row.Name], [new("الاستحقاق", [new(Money(entitlement))]), new("مجموع الاستقطاعات في الملف", [new(Money(deductions))]), new("الصافي المتوقع", [new(Money(expected))]),
                                new("الصافي في الملف", [new(Money(net))]), new("الفرق", [new(Money(difference), Bad: true)]), new("بند مطابق للفرق", [new(match is null ? "—" : $"{match} · {Money(Math.Abs(difference))}")])], [row.Name]));
                    }
                    break;

                case "الرعاية الصحية":
                    foreach (var row in rows)
                    {
                        if (row.After is not { } amount) { issues.Add(Generic(row)); continue; }
                        var low = Math.Floor(amount / 3500) * 3500; var high = low + 3500;
                        var old = Prev(row.Name) is { } q ? Value(q, "الرعاية الصحية") : null;
                        issues.Add(new(kind, row.Name, $"{Money(amount)} لا يقبل القسمة على 3,500",
                            $"المبلغ {Money(amount)} ÷ 3,500 = {(amount / 3500).ToString("0.##", Numbers)}؛ الأقرب {Money(low)} أو {Money(high)}." + (old is { } o ? $" كان في {prevLabel} {Money(o)}." : ""),
                            "راجع استقطاع الرعاية الصحية لهذا المنتسب (عدد أفراد الأسرة × 3,500).", [new("لا يقبل القسمة", "warn")], [row.Name],
                            [new("المبلغ", [new(Money(amount), Bad: true)]), new("÷ 3,500", [new((amount / 3500).ToString("0.##", Numbers))]), new($"في {prevLabel}", [new(Money(old))])], [row.Name]));
                    }
                    break;

                case "استقطاع بلا راتب":
                    foreach (var row in rows)
                    {
                        var words = PayrollAnalysis.Normalize(row.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var similar = words.Length < 2 ? null : current.Select(p => p.E.Name).FirstOrDefault(n =>
                        {
                            var other = PayrollAnalysis.Normalize(n).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            return other.Length >= 2 && other[0] == words[0] && (other[1] == words[1] || other[^1] == words[^1]);
                        });
                        var left = Prev(row.Name) is not null;
                        var diagnosis = similar is not null ? $"لا يوجد راتب بهذا الاسم في {month}، ويوجد اسم مشابه «{similar}»؛ الأرجح اختلاف كتابة الاسم بين ملفي الرواتب والاستقطاعات."
                            : left ? $"كان له راتب في {prevLabel} ولا يوجد في {month}؛ الاستقطاع ما زال قائماً."
                            : $"الاسم غير موجود في رواتب {month}؛ راجع الجهة أو أوقف الاستقطاع.";
                        issues.Add(new(kind, row.Name, $"{row.Detail} · {Money(row.After)}", diagnosis,
                            similar is not null ? "وحّد كتابة الاسم في الملفين ثم أعد الاستيراد." : left ? "أبلغ الجهة بإيقاف الاستقطاع أو تحقق من سبب خروجه." : "راجع قيد الاستقطاع مع الجهة.",
                            [similar is not null ? new("اسم مشابه", "warn") : left ? new($"خرج بعد {prevLabel}", "info") : new("غير موجود", "bad")],
                            [row.Name], [new("الجهة", [new(Show(row.Detail))]), new("المبلغ", [new(Money(row.After))]), new("اسم مشابه في الرواتب", [new(similar ?? "—", Good: similar is not null)])],
                            similar is null ? Array.Empty<string>() : new[] { similar }));
                    }
                    break;

                case "مجموع الاستقطاعات":
                    foreach (var row in rows)
                    {
                        var difference = (row.Before ?? 0) - (row.After ?? 0);
                        issues.Add(new(kind, "مجموع الشهر", $"فرق {Money(Math.Abs(difference))}",
                            $"مجموع «استقطاعات المصارف» في الرواتب {Money(row.Before)} ومجموع ملف الجهات {Money(row.After)}؛ الفرق {Money(Math.Abs(difference))}. افتح كشف البنك ← مطابقة الاستقطاعات التنفيذية لمعرفة الجهات.",
                            "راجع الجهات التي يختلف مجموعها بين الملفين.", [new("مجموع غير متطابق", "bad")], ["المبلغ"],
                            [new("استقطاعات المصارف في الرواتب", [new(Money(row.Before))]), new("مجموع ملف الجهات", [new(Money(row.After))]), new("الفرق", [new(Money(difference), Bad: true)])], []));
                    }
                    break;

                case "تغيّر الصافي":
                    foreach (var row in rows)
                    {
                        if (Cur(row.Name) is not { } p || Prev(row.Name) is not { } q) { issues.Add(Generic(row)); continue; }
                        var changes = q.I.Keys.Union(p.I.Keys).Select(k => (Item: k, Old: q.I.GetValueOrDefault(k), New: p.I.GetValueOrDefault(k)))
                            .Where(x => x.Old != x.New).OrderByDescending(x => Math.Abs(x.New - x.Old)).Take(6).ToArray();
                        var delta = (row.After ?? 0) - (row.Before ?? 0);
                        var diagnosis = changes.Length == 0 ? "لم يتغير أي بند مسجّل في الراتب؛ التغيّر من أعمدة غير مقروءة، راجع الملف."
                            : $"تغيّر الصافي بمقدار {(delta > 0 ? "+" : "−")}{Money(Math.Abs(delta))}. أكبر سبب: بند «{changes[0].Item}» من {Money(changes[0].Old)} إلى {Money(changes[0].New)}.";
                        issues.Add(new(kind, row.Name, $"{Money(row.Before)} ← {Money(row.After)}", diagnosis, "تأكد أن التغيّر في البنود المذكورة له سبب (ترفيع، علاوة، استقطاع جديد).",
                            [new(delta > 0 ? "زيادة" : "نقص", "info")], [prevLabel, month],
                            [new("الصافي", [new(Money(row.Before)), new(Money(row.After), Bad: true)]), .. changes.Select(c => new ReviewTableRow(c.Item, [new(Money(c.Old)), new(Money(c.New))]))],
                            [row.Name]));
                    }
                    break;

                case "تغيّر الحساب":
                    foreach (var row in rows)
                    {
                        if (Cur(row.Name) is not { } p || Prev(row.Name) is not { } q) { issues.Add(Generic(row)); continue; }
                        var oldIban = Iban(q); var newIban = Iban(p); var valid = TransferPreparation.ValidIban(newIban);
                        issues.Add(new(kind, row.Name, $"{Tail(oldIban, 6)} ← {Tail(newIban, 6)}",
                            "تغيّر IBAN بين الشهرين" + (valid ? "؛ الرقم الجديد صالح." : "؛ والرقم الجديد لا يجتاز فحص IBAN."), "تأكد من وجود كتاب تغيير حساب للمنتسب.",
                            [valid ? new("حساب جديد", "info") : new("الجديد غير صالح", "bad")], [prevLabel, month],
                            [new("IBAN", [new(GroupedIban(oldIban)), new(GroupedIban(newIban), Bad: !valid)]), new("الصافي", [new(Money(row.Before)), new(Money(row.After))])], [row.Name]));
                    }
                    break;

                case "جديد في الرواتب" or "خارج من الرواتب" or "تغيّرت الرتبة" or "تغيّر محل العمل":
                    foreach (var row in rows)
                    {
                        var person = kind == "خارج من الرواتب" ? Prev(row.Name) : Cur(row.Name);
                        var hasDeductions = kind == "خارج من الرواتب" && source.Deductions.Any(d => d.Month == month && !PayrollAnalysis.IsInactive(d) && Key(d.Name) == Key(row.Name));
                        var diagnosis = kind switch
                        {
                            "جديد في الرواتب" => $"اسم غير موجود في رواتب {prevLabel}.",
                            "خارج من الرواتب" => $"كان في رواتب {prevLabel} ولا يوجد في {month}." + (hasDeductions ? " وله استقطاعات قائمة هذا الشهر." : ""),
                            _ => row.Detail,
                        };
                        issues.Add(new(kind, row.Name, row.Detail, diagnosis, kind == "خارج من الرواتب" && hasDeductions ? "أبلغ الجهات بإيقاف استقطاعاته." : "",
                            hasDeductions ? new[] { new ReviewTag("له استقطاعات", "warn") } : Array.Empty<ReviewTag>(), ["القيمة"],
                            new ReviewTableRow[] { new("الرتبة", [new(Show(person?.E.Rank ?? ""))]), new("محل العمل", [new(Show(person?.E.Workplace ?? ""))]),
                                new("الرقم الموحد", [new(person is null ? "—" : Show(Text(person, "الرقم الموحد")))]), new("الصافي", [new(Money(row.After ?? row.Before))]) }, [row.Name]));
                    }
                    break;

                default:
                    foreach (var row in rows) issues.Add(Generic(row));
                    break;
            }
        }
        return issues;
    }
}
